namespace RubyDownloader.Models;

internal sealed record VideoInfo(
    string PageUrl,
    string ResourceUrl,
    string ContentType,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string Username,
    string VideoId,
    string? ThumbnailUrl,
    bool IsVideo = true,
    IReadOnlyList<string>? ImageUrls = null);
