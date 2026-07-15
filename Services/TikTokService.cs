using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using RubyDownloader.Config;
using RubyDownloader.Models;

namespace RubyDownloader.Services;

internal sealed class TikTokService
{
    private const int TotalResolveTimeoutMs = 20_000;
    private const int MaximumNavigationTimeoutMs = 12_000;

    private readonly AppSettings _settings;

    public TikTokService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<VideoInfo> ResolveAsync(
        IPage page,
        string tikTokUrl)
    {
        Stopwatch resolveStopwatch = Stopwatch.StartNew();

        var candidates =
            new ConcurrentDictionary<string, VideoCandidate>(
                StringComparer.OrdinalIgnoreCase);

        await page.RouteAsync(
            "**/*",
            async route =>
            {
                string resourceType = route.Request.ResourceType;

                if (resourceType is "image" or "font")
                {
                    await route.AbortAsync();
                    return;
                }

                await route.ContinueAsync();
            });

        page.Response += (_, response) =>
        {
            try
            {
                VideoCandidate? candidate =
                    CreateCandidate(response);

                if (candidate is null)
                {
                    return;
                }

                candidates.AddOrUpdate(
                    candidate.ResourceUrl,
                    candidate,
                    (_, existing) =>
                        candidate.Score > existing.Score
                            ? candidate
                            : existing);

            }
            catch
            {
                // Ignore malformed responses and continue collecting candidates.
            }
        };

        await page.GotoAsync(
            tikTokUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Commit,
                Timeout = Math.Min(
                    _settings.NavigationTimeoutMs,
                    MaximumNavigationTimeoutMs)
            });

        try
        {
            await page.WaitForLoadStateAsync(
                LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions
                {
                    Timeout = 2_000
                });
        }
        catch (TimeoutException)
        {
            // The response listener can find the video before the full DOM is ready.
        }

        await StartVideoPlaybackAsync(page);

        DateTime timeoutAt = DateTime.UtcNow.AddMilliseconds(
            Math.Max(
                0,
                TotalResolveTimeoutMs -
                (int)resolveStopwatch.ElapsedMilliseconds));

        VideoCandidate? bestCandidate = null;
        string? stableCandidateUrl = null;
        DateTime stableSince = DateTime.MinValue;
        DateTime nextPlaybackAt = DateTime.UtcNow.AddSeconds(3);

        while (DateTime.UtcNow < timeoutAt)
        {
            bestCandidate = GetBestCandidate(candidates.Values);

            // Candidate đủ tin cậy thì không cần chờ hết timeout.
            if (bestCandidate is { Score: >= 100 })
            {
                break;
            }

            if (bestCandidate is not null &&
                IsAcceptableStableCandidate(bestCandidate))
            {
                if (!string.Equals(
                        stableCandidateUrl,
                        bestCandidate.ResourceUrl,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stableCandidateUrl = bestCandidate.ResourceUrl;
                    stableSince = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - stableSince >=
                         TimeSpan.FromMilliseconds(800))
                {
                    break;
                }
            }
            else
            {
                stableCandidateUrl = null;
                stableSince = DateTime.MinValue;
            }

            if (DateTime.UtcNow >= nextPlaybackAt)
            {
                await StartVideoPlaybackAsync(page);
                nextPlaybackAt = DateTime.UtcNow.AddSeconds(3);
            }

            await page.WaitForTimeoutAsync(200);
        }

        bestCandidate ??=
            GetBestCandidate(candidates.Values);

        if (bestCandidate is null)
        {
            await SaveDebugFilesAsync(page);

            throw new TimeoutException(
                "Không tìm thấy resource video TikTok hợp lệ. " +
                "Các file video giao diện/login đã được loại bỏ.");
        }

        VideoMetadata metadata =
            await ResolveMetadataAsync(page);

        resolveStopwatch.Stop();

        return new VideoInfo(
            page.Url,
            bestCandidate.ResourceUrl,
            bestCandidate.ContentType,
            bestCandidate.RequestHeaders,
            metadata.Username,
            metadata.VideoId,
            metadata.ThumbnailUrl);
    }

    private static bool IsAcceptableStableCandidate(
        VideoCandidate candidate)
    {
        if (candidate.Score < 50)
        {
            return false;
        }

        if (candidate.ContentLength is > 0 and < 250_000)
        {
            return false;
        }

        return candidate.ContentType.StartsWith(
                   "video/",
                   StringComparison.OrdinalIgnoreCase) ||
               candidate.ResourceUrl.Contains(
                   "mime_type=video",
                   StringComparison.OrdinalIgnoreCase) ||
               candidate.ResourceUrl.Contains(
                   ".mp4",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<VideoMetadata> ResolveMetadataAsync(
        IPage page)
    {
        string canonicalUrl = await GetAttributeAsync(
            page,
            "link[rel='canonical']",
            "href") ?? page.Url;

        string sourceUrl = await GetAttributeAsync(
            page,
            "meta[property='og:url']",
            "content") ?? canonicalUrl;

        Match match = Regex.Match(
            sourceUrl,
            @"/@(?<username>[^/?#]+)/video/(?<id>\d+)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            match = Regex.Match(
                page.Url,
                @"/@(?<username>[^/?#]+)/video/(?<id>\d+)",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            throw new InvalidOperationException(
                "Cannot determine the TikTok username and video UID.");
        }

        string? thumbnailUrl = await ResolveThumbnailUrlAsync(page);

        return new VideoMetadata(
            Uri.UnescapeDataString(match.Groups["username"].Value),
            match.Groups["id"].Value,
            thumbnailUrl);
    }

    private static async Task<string?> ResolveThumbnailUrlAsync(
        IPage page)
    {
        string? thumbnailUrl = await GetAttributeAsync(
            page,
            "meta[property='og:image']",
            "content");

        if (IsHttpUrl(thumbnailUrl))
        {
            return thumbnailUrl;
        }

        thumbnailUrl = await GetAttributeAsync(
            page,
            "video[poster]",
            "poster");

        if (IsHttpUrl(thumbnailUrl))
        {
            return thumbnailUrl;
        }

        try
        {
            thumbnailUrl = await page.EvaluateAsync<string?>(
                """
                () => {
                    const preferredKeys = [
                        'originCover',
                        'cover',
                        'dynamicCover'
                    ];

                    const findCover = (value, visited = new Set()) => {
                        if (!value || typeof value !== 'object') {
                            return null;
                        }

                        if (visited.has(value)) {
                            return null;
                        }

                        visited.add(value);

                        for (const key of preferredKeys) {
                            const candidate = value[key];

                            if (typeof candidate === 'string' &&
                                /^https?:\/\//i.test(candidate)) {
                                return candidate;
                            }

                            if (candidate && typeof candidate === 'object') {
                                const url = candidate.urlList?.[0] ??
                                    candidate.url_list?.[0] ??
                                    candidate.url;

                                if (typeof url === 'string' &&
                                    /^https?:\/\//i.test(url)) {
                                    return url;
                                }
                            }
                        }

                        for (const child of Object.values(value)) {
                            const result = findCover(child, visited);

                            if (result) {
                                return result;
                            }
                        }

                        return null;
                    };

                    const scriptIds = [
                        '__UNIVERSAL_DATA_FOR_REHYDRATION__',
                        'SIGI_STATE',
                        '__NEXT_DATA__'
                    ];

                    for (const id of scriptIds) {
                        const text = document.getElementById(id)?.textContent;

                        if (!text) {
                            continue;
                        }

                        try {
                            const result = findCover(JSON.parse(text));

                            if (result) {
                                return result;
                            }
                        } catch {
                            // Ignore malformed or non-JSON script content.
                        }
                    }

                    return null;
                }
                """);
        }
        catch
        {
            thumbnailUrl = null;
        }

        return IsHttpUrl(thumbnailUrl)
            ? thumbnailUrl
            : null;
    }

    private static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps);
    }

    private static async Task<string?> GetAttributeAsync(
        IPage page,
        string selector,
        string attribute)
    {
        try
        {
            ILocator locator = page.Locator(selector);

            if (await locator.CountAsync() == 0)
            {
                return null;
            }

            return await locator.First.GetAttributeAsync(attribute);
        }
        catch
        {
            return null;
        }
    }

    private static VideoCandidate? CreateCandidate(
        IResponse response)
    {
        string url = response.Url;

        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return null;
        }

        string contentType =
            GetHeader(
                response.Headers,
                "content-type");

        long? contentLength =
            ParseContentLength(response.Headers);

        bool looksLikeVideo =
            contentType.StartsWith(
                "video/",
                StringComparison.OrdinalIgnoreCase) ||
            url.Contains(
                "mime_type=video",
                StringComparison.OrdinalIgnoreCase) ||
            url.Contains(
                "/video/tos/",
                StringComparison.OrdinalIgnoreCase) ||
            url.Contains(
                ".mp4",
                StringComparison.OrdinalIgnoreCase);

        if (!looksLikeVideo)
        {
            return null;
        }

        if (IsIgnoredStaticVideo(uri, url))
        {
            return null;
        }

        int score = CalculateCandidateScore(
            uri,
            url,
            contentType,
            contentLength,
            response.Status);

        if (score <= 0)
        {
            return null;
        }

        return new VideoCandidate(
            ResourceUrl: url,
            ContentType: contentType,
            ContentLength: contentLength,
            Score: score,
            RequestHeaders: new Dictionary<string, string>(
                response.Request.Headers,
                StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsIgnoredStaticVideo(
        Uri uri,
        string url)
    {
        string host = uri.Host;
        string path = uri.AbsolutePath;

        string[] ignoredParts =
        [
            "playback1.mp4",
            "playback2.mp4",
            "webapp-desktop/playback",
            "tiktok_web_login_static",
            "website-login",
            "login_static",
            "/obj/tiktok_web_login",
            "background-video",
            "loading-video"
        ];

        if (ignoredParts.Any(part =>
                url.Contains(
                    part,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Host này thường chứa asset của giao diện đăng nhập,
        // không phải CDN video của bài TikTok.
        if (host.Contains(
                "website-login",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.EndsWith(
                "/playback1.mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static int CalculateCandidateScore(
        Uri uri,
        string url,
        string contentType,
        long? contentLength,
        int status)
    {
        int score = 0;

        string host = uri.Host;

        if (url.Contains(
                "/video/tos/",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (url.Contains(
                "mime_type=video_mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }
        else if (url.Contains(
                     "mime_type=video",
                     StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (host.Contains(
                "tiktok.com",
                StringComparison.OrdinalIgnoreCase) ||
            host.Contains(
                "tiktokcdn.com",
                StringComparison.OrdinalIgnoreCase) ||
            host.Contains(
                "byteoversea.com",
                StringComparison.OrdinalIgnoreCase) ||
            host.Contains(
                "ibytedtos.com",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (host.StartsWith(
                "v",
                StringComparison.OrdinalIgnoreCase) &&
            host.Contains(
                "tiktok",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (contentType.StartsWith(
                "video/mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (status is 200 or 206)
        {
            score += 10;
        }

        if (contentLength.HasValue)
        {
            if (contentLength.Value >= 1_000_000)
            {
                score += 30;
            }
            else if (contentLength.Value >= 500_000)
            {
                score += 15;
            }
            else if (contentLength.Value < 250_000)
            {
                // Các video giao diện thường rất nhỏ.
                score -= 30;
            }
        }

        if (url.Contains(
                "expire=",
                StringComparison.OrdinalIgnoreCase) &&
            url.Contains(
                "signature=",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (url.Contains(
                "policy=",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }

    private static VideoCandidate? GetBestCandidate(
        IEnumerable<VideoCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate =>
                candidate.ContentLength ?? 0)
            .FirstOrDefault();
    }

    private static async Task StartVideoPlaybackAsync(
        IPage page)
    {
        try
        {
            await page.EvaluateAsync(
                """
                async () => {
                    const videos = Array.from(
                        document.querySelectorAll('video')
                    );

                    for (const video of videos) {
                        try {
                            video.muted = true;
                            video.volume = 0;

                            if (video.preload === 'none') {
                                video.preload = 'auto';
                            }

                            video.load();

                            const result = video.play();

                            if (result && typeof result.catch === 'function') {
                                await result.catch(() => {});
                            }
                        } catch {
                            // Bỏ qua video không phát được.
                        }
                    }

                    window.scrollTo({
                        top: Math.floor(document.body.scrollHeight * 0.25),
                        behavior: 'instant'
                    });
                }
                """);
        }
        catch
        {
            // TikTok có thể thay DOM hoặc không cho gọi play().
        }
    }

    private static long? ParseContentLength(
        IReadOnlyDictionary<string, string> headers)
    {
        string contentLength =
            GetHeader(headers, "content-length");

        if (long.TryParse(
                contentLength,
                out long parsedLength))
        {
            return parsedLength;
        }

        // Response 206 thường có:
        // content-range: bytes 0-123/4567890
        string contentRange =
            GetHeader(headers, "content-range");

        int slashIndex =
            contentRange.LastIndexOf('/');

        if (slashIndex >= 0 &&
            slashIndex < contentRange.Length - 1)
        {
            string totalPart =
                contentRange[(slashIndex + 1)..];

            if (long.TryParse(
                    totalPart,
                    out long totalLength))
            {
                return totalLength;
            }
        }

        return null;
    }

    private static string GetHeader(
        IReadOnlyDictionary<string, string> headers,
        string headerName)
    {
        return headers.TryGetValue(
            headerName,
            out string? value)
            ? value
            : string.Empty;
    }

    private static async Task SaveDebugFilesAsync(
        IPage page)
    {
        try
        {
            string debugDirectory =
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "debug");

            Directory.CreateDirectory(debugDirectory);

            string timestamp =
                DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss");

            await page.ScreenshotAsync(
                new PageScreenshotOptions
                {
                    Path = Path.Combine(
                        debugDirectory,
                        $"{timestamp}-page.png"),
                    FullPage = true
                });

            string html =
                await page.ContentAsync();

            await File.WriteAllTextAsync(
                Path.Combine(
                    debugDirectory,
                    $"{timestamp}-page.html"),
                html);
        }
        catch
        {
            // Debug artifacts are best-effort only.
        }
    }

    private sealed record VideoCandidate(
        string ResourceUrl,
        string ContentType,
        long? ContentLength,
        int Score,
        IReadOnlyDictionary<string, string> RequestHeaders);

    private sealed record VideoMetadata(
        string Username,
        string VideoId,
        string? ThumbnailUrl);
}
