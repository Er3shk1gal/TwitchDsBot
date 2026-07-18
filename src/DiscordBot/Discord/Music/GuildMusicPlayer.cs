using System.Diagnostics;
using DiscordBot.Configuration;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Discord.Music;

/// <summary>
/// A single guild's music player: an ordered queue plus a background loop that streams each track
/// through ffmpeg into the VoiceNext transmit sink. Shared mutable state (queue, current track, the
/// per-track cancellation source, the transmit sink, seek/pause flags) is guarded by <c>_lock</c>;
/// no <c>await</c> is ever performed while holding it. Command threads (skip/stop/seek/pause/volume)
/// and the loop thread coordinate exclusively through that lock.
/// </summary>
public sealed class GuildMusicPlayer : IAsyncDisposable
{
    // 20ms of 48kHz 16-bit stereo PCM — the frame size VoiceNext expects.
    private const int FrameSize = 3840;
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    private readonly ulong _guildId;
    private readonly VoiceNextConnection _connection;
    private readonly TrackResolver _resolver;
    private readonly MusicOptions _options;
    private readonly ILogger _logger;
    private readonly Func<ResolvedTrack, GuildMusicPlayer, Task> _announce;
    private readonly Action<GuildMusicPlayer> _onIdle;

    private readonly object _lock = new();
    private readonly List<ResolvedTrack> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _loopCts = new();

    // All of the following are guarded by _lock.
    private ResolvedTrack? _current;
    private CancellationTokenSource? _trackCts;
    private VoiceTransmitSink? _currentSink;
    private TimeSpan? _nextStartOffset;
    private bool _seekPending;
    private bool _suppressLoopOnce;
    private bool _pauseRequested;
    private bool _isPaused;
    private bool _loopEnabled;
    private double _volume;
    private bool _terminated;
    private bool _started;

    private Task? _loopTask;

    public GuildMusicPlayer(
        ulong guildId,
        VoiceNextConnection connection,
        TrackResolver resolver,
        MusicOptions options,
        ILogger logger,
        Func<ResolvedTrack, GuildMusicPlayer, Task> announce,
        Action<GuildMusicPlayer> onIdle)
    {
        _guildId = guildId;
        _connection = connection;
        _resolver = resolver;
        _options = options;
        _logger = logger;
        _announce = announce;
        _onIdle = onIdle;
        _volume = Math.Clamp(options.DefaultVolume, 0.0, 2.0);
    }

    public ulong GuildId => _guildId;
    public DiscordChannel VoiceChannel => _connection.TargetChannel;

    /// <summary>Text channel where "now playing" messages are posted. Updated by the latest /play.</summary>
    public DiscordChannel? AnnouncementChannel { get; set; }

    public ResolvedTrack? Current { get { lock (_lock) { return _current; } } }
    public double Volume { get { lock (_lock) { return _volume; } } }
    public bool IsPaused { get { lock (_lock) { return _isPaused; } } }
    public bool IsTerminated { get { lock (_lock) { return _terminated; } } }

    public bool Loop
    {
        get { lock (_lock) { return _loopEnabled; } }
        set { lock (_lock) { _loopEnabled = value; } }
    }

    public void Start()
    {
        lock (_lock) { _started = true; }
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token));
    }

    /// <summary>Add a track. Returns its 1-based queue position, or -1 if the player has terminated.</summary>
    public int Enqueue(ResolvedTrack track)
    {
        lock (_lock)
        {
            if (_terminated)
            {
                return -1;
            }
            _queue.Add(track);
            var position = _queue.Count;
            _signal.Release();
            return position;
        }
    }

    public void EnqueueRange(IEnumerable<ResolvedTrack> tracks)
    {
        foreach (var track in tracks)
        {
            if (Enqueue(track) < 0)
            {
                break;
            }
        }
    }

    public IReadOnlyList<ResolvedTrack> QueueSnapshot()
    {
        lock (_lock) { return _queue.ToList(); }
    }

    public bool Skip()
    {
        lock (_lock)
        {
            if (_current is null)
            {
                return false;
            }
            _suppressLoopOnce = true;
        }
        CancelCurrentTrack();
        return true;
    }

    public void Stop()
    {
        lock (_lock)
        {
            _queue.Clear();
            _suppressLoopOnce = true;
            _loopEnabled = false;
        }
        CancelCurrentTrack();
    }

    public int ClearQueue()
    {
        lock (_lock)
        {
            var count = _queue.Count;
            _queue.Clear();
            return count;
        }
    }

    public bool Shuffle()
    {
        lock (_lock)
        {
            if (_queue.Count < 2)
            {
                return false;
            }
            var rng = Random.Shared;
            for (var i = _queue.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
            }
            return true;
        }
    }

    public bool Pause()
    {
        VoiceTransmitSink? sink;
        lock (_lock)
        {
            if (_current is null || _isPaused)
            {
                return false;
            }
            _pauseRequested = true;
            _isPaused = true;
            sink = _currentSink;
        }
        // If the sink isn't created yet (still resolving/starting ffmpeg), the loop applies the
        // pending pause once it exists.
        sink?.Pause();
        return true;
    }

    public async Task<bool> ResumeAsync()
    {
        VoiceTransmitSink? sink;
        lock (_lock)
        {
            if (!_isPaused)
            {
                return false;
            }
            _pauseRequested = false;
            _isPaused = false;
            sink = _currentSink;
        }
        if (sink is not null)
        {
            await sink.ResumeAsync();
        }
        return true;
    }

    public void SetVolume(double volume)
    {
        VoiceTransmitSink? sink;
        lock (_lock)
        {
            _volume = Math.Clamp(volume, 0.0, 2.0);
            sink = _currentSink;
        }
        if (sink is not null)
        {
            sink.VolumeModifier = _volume; // snapshot avoids a check-then-use NRE at track boundaries
        }
    }

    /// <summary>Restart the current track from the given position.</summary>
    public bool Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_current is null || _seekPending)
            {
                return false; // ignore a second seek before the first is consumed (avoids replay)
            }
            _queue.Insert(0, _current);
            _nextStartOffset = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            _seekPending = true;
            _suppressLoopOnce = true;
        }
        _signal.Release();
        CancelCurrentTrack();
        return true;
    }

    private void CancelCurrentTrack()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _trackCts; }
        if (cts is null)
        {
            return;
        }
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* track already finished tearing down */ }
    }

    private async Task RunAsync(CancellationToken loopToken)
    {
        var idleTimeout = TimeSpan.FromSeconds(Math.Max(15, _options.IdleTimeoutSeconds));

        try
        {
            while (!loopToken.IsCancellationRequested)
            {
                bool gotTrack;
                try
                {
                    gotTrack = await _signal.WaitAsync(idleTimeout, loopToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!gotTrack)
                {
                    break; // idle for too long -> leave the channel
                }

                // Consume any pending seek offset on EVERY wake (before the empty-queue check) so a
                // seek whose track got cleared (e.g. by /stop) can't leak its offset to a later track.
                ResolvedTrack? track;
                TimeSpan? offset;
                lock (_lock)
                {
                    offset = _nextStartOffset;
                    _nextStartOffset = null;
                    _seekPending = false;
                    _suppressLoopOnce = false;
                    track = _queue.Count > 0 ? _queue[0] : null;
                    if (track is not null)
                    {
                        _queue.RemoveAt(0);
                    }
                }

                if (track is null)
                {
                    continue; // spurious wake / queue cleared
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
                CancellationTokenSource? previous;
                lock (_lock)
                {
                    previous = _trackCts;
                    _trackCts = cts;
                    _current = track;
                }
                previous?.Dispose();

                try
                {
                    if (offset is null)
                    {
                        await _announce(track, this);
                    }
                    await PlayTrackAsync(track, offset ?? TimeSpan.Zero, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Skipped, stopped or seeked — expected.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Playback failed for '{Title}' in guild {Guild}.", track.Title, _guildId);
                }
                finally
                {
                    lock (_lock)
                    {
                        _currentSink = null;
                        _pauseRequested = false;
                        _isPaused = false;
                    }
                }

                bool reEnqueue;
                lock (_lock)
                {
                    reEnqueue = _loopEnabled && !_suppressLoopOnce && !loopToken.IsCancellationRequested;
                    _current = null;
                }
                if (reEnqueue)
                {
                    Enqueue(track);
                }
            }
        }
        finally
        {
            CancellationTokenSource? last;
            lock (_lock)
            {
                _terminated = true;
                _current = null;
                last = _trackCts;
                _trackCts = null;
            }
            last?.Dispose();
            SafeDisconnect();
            _onIdle(this);
        }
    }

    private async Task PlayTrackAsync(ResolvedTrack track, TimeSpan startOffset, CancellationToken ct)
    {
        var streamUrl = await ResolveFreshStreamUrlAsync(track, ct);

        using var ffmpeg = StartFfmpeg(streamUrl, startOffset);
        var stdout = ffmpeg.StandardOutput.BaseStream;

        var sink = _connection.GetTransmitSink();
        bool applyPause;
        lock (_lock)
        {
            sink.VolumeModifier = _volume;
            _currentSink = sink;
            applyPause = _pauseRequested; // a /pause issued while we were resolving/starting
        }
        if (applyPause)
        {
            sink.Pause();
        }

        await _connection.SendSpeakingAsync(true);
        try
        {
            var buffer = new byte[FrameSize];
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await sink.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, read), ct);
            }
            await sink.FlushAsync(ct);
        }
        finally
        {
            await _connection.SendSpeakingAsync(false);
            TryKill(ffmpeg);
        }
    }

    // Source URLs (e.g. SoundCloud) expire, so re-resolve right before playing. Bounded by a timeout
    // so a stalled yt-dlp can't hang playback forever; on timeout/failure fall back to the cached URL.
    private async Task<string> ResolveFreshStreamUrlAsync(ResolvedTrack track, CancellationToken ct)
    {
        using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resolveCts.CancelAfter(ResolveTimeout);
        try
        {
            var fresh = await _resolver.ResolveAsync(track.WebpageUrl, track.RequestedBy, resolveCts.Token);
            return fresh?.StreamUrl ?? track.StreamUrl;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real skip/stop/seek — propagate
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Re-resolve timed out for '{Title}', using cached URL.", track.Title);
            return track.StreamUrl;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Re-resolve failed for '{Title}', using cached URL.", track.Title);
            return track.StreamUrl;
        }
    }

    private Process StartFfmpeg(string input, TimeSpan startOffset)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("warning");
        // Reconnect on transient network hiccups for http(s)/hls inputs.
        psi.ArgumentList.Add("-reconnect");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max");
        psi.ArgumentList.Add("5");
        if (startOffset > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startOffset.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("pipe:1");

        var process = new Process { StartInfo = psi };
        process.Start();
        // Drain stderr so the pipe buffer never fills and blocks ffmpeg.
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(); }
            catch { /* ignore */ }
        });
        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }
    }

    private void SafeDisconnect()
    {
        try { _connection.Disconnect(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Voice disconnect failed for guild {Guild}.", _guildId); }
    }

    public async ValueTask DisposeAsync()
    {
        await _loopCts.CancelAsync();
        CancelCurrentTrack();

        bool started;
        lock (_lock) { started = _started; }

        if (started && _loopTask is not null)
        {
            // The loop's finally disconnects the voice connection and marks the player terminated,
            // so we don't disconnect again here (avoids a redundant second Disconnect()).
            try { await _loopTask; }
            catch { /* ignore */ }
        }
        else
        {
            SafeDisconnect(); // loop never ran; disconnect ourselves
        }

        _loopCts.Dispose();
        _signal.Dispose();
    }
}
