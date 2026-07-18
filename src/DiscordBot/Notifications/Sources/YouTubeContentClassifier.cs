using System.Xml;

namespace DiscordBot.Notifications.Sources;

/// <summary>
/// Helpers for classifying YouTube items. YouTube exposes no official "is this a Short?" flag, so
/// we use the community-accepted heuristic: a Short is a video whose duration is at most 60 seconds
/// (3 minutes since late 2024, but 60s is the safe, widely-used cutoff for older content and avoids
/// misclassifying normal short videos). Callers can also confirm via the /shorts/ URL, but the
/// duration check is quota-free once the video details are already loaded.
/// </summary>
public static class YouTubeContentClassifier
{
    /// <summary>Upper bound (inclusive) for treating a video as a Short.</summary>
    public static readonly TimeSpan ShortMaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Parse an ISO-8601 duration (e.g. "PT59S", "PT1M2S") as returned by videos.list
    /// contentDetails.duration. Returns null if it can't be parsed.
    /// </summary>
    public static TimeSpan? ParseDuration(string? iso8601Duration)
    {
        if (string.IsNullOrWhiteSpace(iso8601Duration))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(iso8601Duration);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>True when the duration marks the video as a Short (0 &lt; d ≤ 60s).</summary>
    public static bool IsShort(TimeSpan? duration) =>
        duration is { } d && d > TimeSpan.Zero && d <= ShortMaxDuration;

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    public static string ShortsUrl(string videoId) => $"https://www.youtube.com/shorts/{videoId}";
}
