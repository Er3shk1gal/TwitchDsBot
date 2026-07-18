using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DiscordBot.Configuration;
using DiscordBot.Data.Entities;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Notifications.Sources;

/// <summary>
/// Polls YouTube (Data API v3, API-key auth) for a channel's new uploads, Shorts and live streams.
/// Uses the cheap flow: playlistItems.list on the uploads playlist (~1 unit) + videos.list to
/// classify new ids (~1 unit). See the README for quota math.
/// </summary>
public sealed class YouTubeNotificationSource : INotificationSource
{
    private readonly YouTubeService _yt;
    private readonly ILogger<YouTubeNotificationSource> _logger;
    private readonly ConcurrentDictionary<string, string> _uploadsPlaylistCache = new();

    public YouTubeNotificationSource(IOptions<YouTubeOptions> options, ILogger<YouTubeNotificationSource> logger)
    {
        _logger = logger;
        _yt = new YouTubeService(new BaseClientService.Initializer
        {
            ApiKey = options.Value.ApiKey,
            ApplicationName = "DiscordBot.YouTubeNotifier",
        });
    }

    public ContentSourceType SourceType => ContentSourceType.YouTube;

    public async Task<(string SourceChannelId, string DisplayName)?> ResolveAsync(string userInput, CancellationToken ct)
    {
        var (mode, value) = Interpret(userInput);

        try
        {
            var req = _yt.Channels.List("snippet,contentDetails");
            switch (mode)
            {
                case InputMode.Id: req.Id = value; break;
                case InputMode.Handle: req.ForHandle = value; break;
                case InputMode.Username: req.ForUsername = value; break;
            }
            req.MaxResults = 1;

            var resp = await req.ExecuteAsync(ct);
            var channel = resp.Items?.FirstOrDefault();
            if (channel is null)
            {
                return null;
            }

            var uploads = channel.ContentDetails?.RelatedPlaylists?.Uploads;
            if (!string.IsNullOrEmpty(uploads))
            {
                _uploadsPlaylistCache[channel.Id] = uploads;
            }

            return (channel.Id, channel.Snippet?.Title ?? channel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube channel resolve failed for '{Input}'.", userInput);
            return null;
        }
    }

    public async IAsyncEnumerable<ContentEvent> PollAsync(
        NotificationSubscription subscription, [EnumeratorCancellation] CancellationToken ct)
    {
        var events = await FetchAsync(subscription, ct);
        foreach (var evt in events)
        {
            yield return evt;
        }
    }

    private async Task<IReadOnlyList<ContentEvent>> FetchAsync(NotificationSubscription subscription, CancellationToken ct)
    {
        try
        {
            var uploadsPlaylist = await GetUploadsPlaylistAsync(subscription.SourceChannelId, ct);
            if (uploadsPlaylist is null)
            {
                return Array.Empty<ContentEvent>();
            }

            var playlistReq = _yt.PlaylistItems.List("snippet,contentDetails");
            playlistReq.PlaylistId = uploadsPlaylist;
            playlistReq.MaxResults = 10;
            var playlistResp = await playlistReq.ExecuteAsync(ct);

            var videoIds = playlistResp.Items
                .Select(i => i.ContentDetails?.VideoId ?? i.Snippet?.ResourceId?.VideoId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct()
                .Take(50)
                .ToList();

            if (videoIds.Count == 0)
            {
                return Array.Empty<ContentEvent>();
            }

            var videosReq = _yt.Videos.List("snippet,contentDetails,liveStreamingDetails");
            videosReq.Id = string.Join(",", videoIds);
            videosReq.MaxResults = 50;
            var videosResp = await videosReq.ExecuteAsync(ct);

            var events = new List<ContentEvent>();
            foreach (var video in videosResp.Items)
            {
                if (TryClassify(video, out var evt))
                {
                    events.Add(evt);
                }
            }

            return events;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube poll failed for channel {Channel}.", subscription.SourceChannelId);
            return Array.Empty<ContentEvent>();
        }
    }

    private static bool TryClassify(Video video, out ContentEvent contentEvent)
    {
        contentEvent = null!;
        if (video.Id is null || video.Snippet is null)
        {
            return false;
        }

        var state = video.Snippet.LiveBroadcastContent; // "live" | "upcoming" | "none"
        var author = video.Snippet.ChannelTitle;
        var thumb = PickThumbnail(video.Snippet.Thumbnails);
        var live = video.LiveStreamingDetails;

        ContentEventKind kind;
        string url = YouTubeContentClassifier.WatchUrl(video.Id);
        DateTimeOffset? scheduled = live?.ScheduledStartTimeDateTimeOffset;

        switch (state)
        {
            case "live":
                kind = ContentEventKind.LiveStarted;
                break;

            case "upcoming":
                kind = ContentEventKind.LiveScheduled;
                break;

            default: // "none"
                // A finished live stream keeps liveStreamingDetails — don't re-announce it as an
                // upload (it was already covered by the live notification). Caveat: a finished
                // Premiere is indistinguishable from a finished stream via the API and is likewise
                // suppressed, so a Premiere is only announced through the live/upcoming path.
                if (live?.ActualStartTimeDateTimeOffset is not null)
                {
                    return false;
                }

                var duration = YouTubeContentClassifier.ParseDuration(video.ContentDetails?.Duration);
                if (YouTubeContentClassifier.IsShort(duration))
                {
                    kind = ContentEventKind.ShortUploaded;
                    url = YouTubeContentClassifier.ShortsUrl(video.Id);
                }
                else
                {
                    kind = ContentEventKind.VideoUploaded;
                }
                break;
        }

        contentEvent = new ContentEvent
        {
            SourceType = ContentSourceType.YouTube,
            Kind = kind,
            ExternalId = video.Id,
            Title = video.Snippet.Title ?? "(untitled)",
            Url = url,
            AuthorName = author,
            ThumbnailUrl = thumb,
            PublishedAt = video.Snippet.PublishedAtDateTimeOffset,
            ScheduledStartAt = scheduled,
        };
        return true;
    }

    private async Task<string?> GetUploadsPlaylistAsync(string channelId, CancellationToken ct)
    {
        if (_uploadsPlaylistCache.TryGetValue(channelId, out var cached))
        {
            return cached;
        }

        // The uploads playlist id is the channel id with "UC" -> "UU". Use it directly to save a
        // quota unit, but fall back to channels.list if the id shape is unexpected.
        if (channelId.StartsWith("UC", StringComparison.Ordinal) && channelId.Length > 2)
        {
            var derived = "UU" + channelId[2..];
            _uploadsPlaylistCache[channelId] = derived;
            return derived;
        }

        try
        {
            var req = _yt.Channels.List("contentDetails");
            req.Id = channelId;
            var resp = await req.ExecuteAsync(ct);
            var uploads = resp.Items?.FirstOrDefault()?.ContentDetails?.RelatedPlaylists?.Uploads;
            if (!string.IsNullOrEmpty(uploads))
            {
                _uploadsPlaylistCache[channelId] = uploads;
            }
            return uploads;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve uploads playlist for {Channel}.", channelId);
            return null;
        }
    }

    private static string? PickThumbnail(ThumbnailDetails? thumbnails) =>
        thumbnails?.Maxres?.Url
        ?? thumbnails?.Standard?.Url
        ?? thumbnails?.High?.Url
        ?? thumbnails?.Medium?.Url
        ?? thumbnails?.Default__?.Url;

    private enum InputMode { Id, Handle, Username }

    private static (InputMode Mode, string Value) Interpret(string input)
    {
        input = input.Trim();

        // Full URL forms.
        if (input.Contains("/channel/", StringComparison.OrdinalIgnoreCase))
        {
            var id = LastSegment(input, "/channel/");
            return (InputMode.Id, id);
        }
        if (input.Contains("/user/", StringComparison.OrdinalIgnoreCase))
        {
            return (InputMode.Username, LastSegment(input, "/user/"));
        }
        if (input.Contains("/@", StringComparison.Ordinal))
        {
            return (InputMode.Handle, "@" + LastSegment(input, "/@"));
        }

        // Bare forms.
        if (input.StartsWith('@'))
        {
            return (InputMode.Handle, input);
        }
        if (input.StartsWith("UC", StringComparison.Ordinal) && input.Length is 24 && !input.Contains(' '))
        {
            return (InputMode.Id, input);
        }

        // Anything else: try it as a handle (the API accepts handles with or without '@').
        return (InputMode.Handle, input);
    }

    private static string LastSegment(string input, string marker)
    {
        var idx = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var rest = input[(idx + marker.Length)..];
        var end = rest.IndexOfAny(['/', '?', '&']);
        return end >= 0 ? rest[..end] : rest;
    }
}
