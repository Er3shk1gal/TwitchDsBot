using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DiscordBot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Discord.Music;

/// <summary>
/// Resolves a user query (a URL, or a search term) into playable <see cref="ResolvedTrack"/>s using
/// yt-dlp. yt-dlp handles SoundCloud, Bandcamp, direct audio URLs and many other open sources, and
/// its format selection yields a direct stream URL that ffmpeg can read.
/// </summary>
public sealed class TrackResolver
{
    private readonly MusicOptions _options;
    private readonly ILogger<TrackResolver> _logger;

    public TrackResolver(IOptions<MusicOptions> options, ILogger<TrackResolver> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Resolve a query into one track. Non-URL queries are searched with the configured provider
    /// (SoundCloud by default). Returns null if nothing playable was found.
    /// </summary>
    public async Task<ResolvedTrack?> ResolveAsync(string query, ulong requestedBy, CancellationToken ct)
    {
        var results = await ResolveManyAsync(query, requestedBy, playlist: false, ct);
        if (results.Count > 0)
        {
            return results[0];
        }

        // For plain search queries (not URLs), retry once with the alternate provider: if the default
        // is YouTube and it found nothing, try SoundCloud, and vice versa. This also rescues searches
        // that only hit a DRM-protected SoundCloud track by falling through to YouTube.
        if (!LooksLikeUrl(query))
        {
            var alternate = _options.DefaultSearchPrefix.StartsWith("yt", StringComparison.OrdinalIgnoreCase)
                ? "scsearch"
                : "ytsearch";
            var fallback = await ResolveManyAsync(query, requestedBy, playlist: false, ct, searchPrefix: alternate);
            if (fallback.Count > 0)
            {
                return fallback[0];
            }
        }

        return null;
    }

    private static bool LooksLikeUrl(string query) =>
        query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        query.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a query into one or more tracks. When <paramref name="playlist"/> is true and the
    /// query is a playlist URL, every entry is returned; otherwise a single track.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedTrack>> ResolveManyAsync(
        string query, ulong requestedBy, bool playlist, CancellationToken ct, string? searchPrefix = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Best audio-only format; fall back to best combined if a source has no audio-only stream.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("bestaudio/best");
        if (!playlist)
        {
            psi.ArgumentList.Add("--no-playlist");
        }
        psi.ArgumentList.Add("--default-search");
        psi.ArgumentList.Add(searchPrefix ?? _options.DefaultSearchPrefix); // e.g. "ytsearch"
        psi.ArgumentList.Add("--no-warnings");

        // One --print per field => newline-delimited, unambiguous even for titles with odd chars.
        // %(url)s on a format-selected item is the direct media stream URL.
        foreach (var field in PrintFields)
        {
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add(field);
        }
        psi.ArgumentList.Add(query);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start yt-dlp ('{Path}'). Is it installed and on PATH?", _options.YtDlpPath);
            return Array.Empty<ResolvedTrack>();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask;
                _logger.LogWarning("yt-dlp exited {Code} for query '{Query}': {Error}",
                    process.ExitCode, query, stderr.Trim());
                return Array.Empty<ResolvedTrack>();
            }

            return ParseTracks(stdout, requestedBy);
        }
        finally
        {
            // On cancellation/timeout WaitForExitAsync throws before the process exits — kill it so we
            // don't orphan a yt-dlp process, then observe the read tasks so they don't fault unwatched.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }
            try { await Task.WhenAll(stdoutTask, stderrTask); }
            catch { /* streams closed by the kill */ }
        }
    }

    // The `j` (JSON) conversion escapes newlines inside string values, so every field stays on
    // exactly one line — keeping the fixed 6-lines-per-track block parsing correct even for titles
    // that contain newlines. Missing fields print as JSON `null`.
    private static readonly string[] PrintFields =
    [
        "%(title)j",
        "%(uploader)j",
        "%(webpage_url)j",
        "%(duration)j",
        "%(thumbnail)j",
        "%(url)j",
    ];

    private static IReadOnlyList<ResolvedTrack> ParseTracks(string stdout, ulong requestedBy)
    {
        var lines = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None);
        var tracks = new List<ResolvedTrack>();

        // Each track is a block of PrintFields.Length lines.
        for (var i = 0; i + PrintFields.Length <= lines.Length; i += PrintFields.Length)
        {
            var title = DecodeField(lines[i]);
            var uploader = DecodeField(lines[i + 1]);
            var webpage = DecodeField(lines[i + 2]);
            var durationRaw = DecodeField(lines[i + 3]);
            var thumb = DecodeField(lines[i + 4]);
            var streamUrl = DecodeField(lines[i + 5]);

            if (string.IsNullOrEmpty(streamUrl) || string.IsNullOrEmpty(title))
            {
                continue;
            }

            tracks.Add(new ResolvedTrack
            {
                Title = title,
                Uploader = uploader,
                WebpageUrl = string.IsNullOrEmpty(webpage) ? streamUrl : webpage,
                ThumbnailUrl = thumb,
                Duration = ParseDuration(durationRaw),
                StreamUrl = streamUrl,
                RequestedBy = requestedBy,
            });
        }

        return tracks;
    }

    // Each --print line is JSON: a quoted string, a bare number (duration), or `null`.
    private static string? DecodeField(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed is "null" or "NA")
        {
            return null;
        }

        if (trimmed.StartsWith('"'))
        {
            try
            {
                var value = JsonSerializer.Deserialize<string>(trimmed);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (JsonException)
            {
                return trimmed;
            }
        }

        return trimmed; // numbers (e.g. duration) come through unquoted
    }

    private static TimeSpan? ParseDuration(string? seconds)
    {
        if (seconds is null)
        {
            return null;
        }

        // duration is a float number of seconds (e.g. "213.0").
        return double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s)
            : null;
    }
}
