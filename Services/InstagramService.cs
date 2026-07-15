using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using RubyDownloader.Config;
using RubyDownloader.Models;

namespace RubyDownloader.Services;

internal sealed class InstagramService
{
    private readonly AppSettings _settings;

    public InstagramService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<VideoInfo> ResolveAsync(
        IPage page,
        string instagramUrl)
    {
        var candidates = new ConcurrentDictionary<string, VideoCandidate>(
            StringComparer.OrdinalIgnoreCase);

        page.Response += (_, response) =>
        {
            VideoCandidate? candidate = CreateCandidate(response);

            if (candidate is not null)
            {
                candidates.AddOrUpdate(
                    candidate.ResourceUrl,
                    candidate,
                    (_, existing) => candidate.Score > existing.Score
                        ? candidate
                        : existing with
                        {
                            ContentLength = candidate.ContentLength ??
                                            existing.ContentLength,
                            ContentType = string.IsNullOrWhiteSpace(
                                candidate.ContentType)
                                ? existing.ContentType
                                : candidate.ContentType,
                            RequestHeaders = candidate.RequestHeaders.Count > 0
                                ? candidate.RequestHeaders
                                : existing.RequestHeaders
                        });
            }
        };

        await page.RouteAsync(
            "**/*",
            async route =>
            {
                if (route.Request.ResourceType is "image" or "font")
                {
                    await route.AbortAsync();
                    return;
                }

                await route.ContinueAsync();
            });

        await page.GotoAsync(
            instagramUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Commit,
                Timeout = Math.Min(_settings.NavigationTimeoutMs, 15_000)
            });

        try
        {
            await page.WaitForLoadStateAsync(
                LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            // Metadata and network responses may already be available.
        }

        InstagramMetadata metadata = await ResolveMetadataAsync(page);

        foreach (string metadataVideoUrl in metadata.VideoUrls)
        {
            string normalizedMetadataVideoUrl =
                NormalizeInstagramVideoUrl(metadataVideoUrl);

            int metadataScore = CalculateQualityScore(
                normalizedMetadataVideoUrl);

            candidates.TryAdd(
                normalizedMetadataVideoUrl,
                new VideoCandidate(
                    normalizedMetadataVideoUrl,
                    "video/mp4",
                    null,
                    metadataScore,
                    new Dictionary<string, string>()));
        }

        if (metadata.IsImageOnly && candidates.IsEmpty)
        {
            string? firstImageUrl = metadata.ImageUrls.FirstOrDefault();

            if (firstImageUrl is null)
            {
                throw new InvalidOperationException(
                    "The Instagram post is image-only, but no downloadable image URL was found.");
            }

            return new VideoInfo(
                page.Url,
                firstImageUrl,
                "image/jpeg",
                new Dictionary<string, string>(),
                metadata.Username,
                metadata.MediaId,
                firstImageUrl,
                IsVideo: false,
                ImageUrls: metadata.ImageUrls);
        }

        await StartVideoPlaybackAsync(page);

        DateTime timeoutAt = DateTime.UtcNow.AddMilliseconds(
            Math.Min(_settings.ResourceTimeoutMs, 20_000));
        DateTime collectUntil = DateTime.UtcNow.AddMilliseconds(2_000);
        VideoCandidate? bestCandidate = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            bestCandidate = candidates.Values
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.ContentLength ?? 0)
                .FirstOrDefault();

            if (bestCandidate is { Score: >= 100 } &&
                DateTime.UtcNow >= collectUntil)
            {
                break;
            }

            await page.WaitForTimeoutAsync(250);
        }

        if (bestCandidate is null)
        {
            throw new TimeoutException(
                "No public Instagram video resource was found. " +
                "The post may require login or may contain images only.");
        }

        return new VideoInfo(
            page.Url,
            bestCandidate.ResourceUrl,
            bestCandidate.ContentType,
            bestCandidate.RequestHeaders,
            metadata.Username,
            metadata.MediaId,
            metadata.ThumbnailUrl);
    }

    private static VideoCandidate? CreateCandidate(IResponse response)
    {
        string contentType = GetHeader(response.Headers, "content-type");
        string url = response.Url;

        bool isVideo = contentType.StartsWith(
                           "video/",
                           StringComparison.OrdinalIgnoreCase) ||
                       url.Contains(".mp4", StringComparison.OrdinalIgnoreCase);

        if (!isVideo || response.Status is not (200 or 206))
        {
            return null;
        }

        bool isPartialUrl = HasPartialRangeQuery(url);
        long? contentLength = isPartialUrl
            ? null
            : ParseContentLength(response.Headers);
        int score = CalculateQualityScore(url);

        if (url.Contains("cdninstagram.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (contentLength >= 250_000)
        {
            score += 5;
        }

        var requestHeaders = new Dictionary<string, string>(
            response.Request.Headers,
            StringComparer.OrdinalIgnoreCase);

        // Instagram's playback request often carries a byte range for a
        // middle segment. DownloadService must keep its own bytes=0- header.
        requestHeaders.Remove("range");

        return new VideoCandidate(
            NormalizeInstagramVideoUrl(url),
            contentType,
            contentLength,
            score,
            requestHeaders);
    }

    private static async Task<InstagramMetadata> ResolveMetadataAsync(
        IPage page)
    {
        string canonicalUrl = await GetAttributeAsync(
            page,
            "link[rel='canonical']",
            "href") ?? page.Url;

        Match idMatch = Regex.Match(
            canonicalUrl,
            @"/(?:reel|reels|p|tv)/(?<id>[A-Za-z0-9_-]+)",
            RegexOptions.IgnoreCase);

        if (!idMatch.Success)
        {
            idMatch = Regex.Match(
                page.Url,
                @"/(?:reel|reels|p|tv)/(?<id>[A-Za-z0-9_-]+)",
                RegexOptions.IgnoreCase);
        }

        if (!idMatch.Success)
        {
            throw new InvalidOperationException(
                "Cannot determine the Instagram media shortcode.");
        }

        string mediaId = idMatch.Groups["id"].Value;
        string? videoUrl = await GetMetaContentAsync(
            page,
            "og:video:secure_url",
            "og:video");
        string? thumbnailUrl = await GetMetaContentAsync(
            page,
            "og:image");
        string? title = await GetMetaContentAsync(
            page,
            "og:title",
            "twitter:title");
        string? description = await GetMetaContentAsync(
            page,
            "og:description",
            "twitter:description");

        string? fallbackUsername = ExtractUsername(
            title,
            description,
            canonicalUrl);
        string[]? embeddedData = await FindEmbeddedMediaAsync(page, mediaId);
        string? ownerUsername = null;
        bool isImageOnly = false;
        var videoUrls = new List<string>();
        var imageUrls = new List<string>();

        if (IsHttpUrl(videoUrl))
        {
            videoUrls.Add(videoUrl!);
        }

        if (embeddedData is { Length: >= 5 })
        {
            // The owner attached to the exact shortcode is authoritative.
            ownerUsername = NullIfEmpty(embeddedData[0]);
            thumbnailUrl ??= NullIfEmpty(embeddedData[2]);

            isImageOnly = embeddedData[4].Equals(
                "image",
                StringComparison.OrdinalIgnoreCase);

            foreach (string embeddedValue in embeddedData.Skip(5))
            {
                if (embeddedValue.StartsWith(
                        "video:",
                        StringComparison.Ordinal))
                {
                    string embeddedUrl = embeddedValue[6..];

                    if (IsHttpUrl(embeddedUrl))
                    {
                        videoUrls.Add(embeddedUrl);
                    }
                }
                else if (embeddedValue.StartsWith(
                             "image:",
                             StringComparison.Ordinal))
                {
                    string embeddedUrl = embeddedValue[6..];

                    if (IsHttpUrl(embeddedUrl))
                    {
                        imageUrls.Add(embeddedUrl);
                    }
                }
            }

            if (IsHttpUrl(embeddedData[1]))
            {
                videoUrls.Add(embeddedData[1]);
            }
        }

        videoUrls = videoUrls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        imageUrls = imageUrls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool isConfirmedVideo = videoUrls.Count > 0;

        if (ownerUsername is null || videoUrls.Count == 0)
        {
            string[] apiMediaData =
                await FindApiMediaDataAsync(page, mediaId);

            if (apiMediaData.Length >= 2)
            {
                ownerUsername ??= NullIfEmpty(apiMediaData[0]);

                if (apiMediaData[1] == "image")
                {
                    isImageOnly = true;
                    imageUrls.InsertRange(
                        0,
                        apiMediaData.Skip(2).Where(IsHttpUrl)!);
                }
                else if (apiMediaData[1] == "video")
                {
                    isConfirmedVideo = true;
                }
            }
        }

        IReadOnlyList<string> domImageUrls =
            await FindDomImageUrlsAsync(page, thumbnailUrl);

        var domImageFileNames = domImageUrls
            .Select(GetUrlFileName)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        imageUrls = domImageUrls
            .Concat(imageUrls.Where(url =>
                !domImageFileNames.Contains(GetUrlFileName(url))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? domUsername = await FindDomOwnerUsernameAsync(page);
        string username = NormalizeUsername(
            ownerUsername ??
            fallbackUsername ??
            domUsername ??
            "instagram");


        if (isConfirmedVideo)
        {
            isImageOnly = false;
        }
        else if (!isImageOnly)
        {
            isImageOnly = await IsJsonLdImageOnlyAsync(page);
        }

        if (!isConfirmedVideo &&
            videoUrls.Count == 0 &&
            IsHttpUrl(thumbnailUrl))
        {
            isImageOnly = true;

            if (imageUrls.Count == 0)
            {
                imageUrls.Add(thumbnailUrl!);
            }
        }

        return new InstagramMetadata(
            username,
            mediaId,
            videoUrls,
            thumbnailUrl,
            isImageOnly,
            imageUrls);
    }

    private static async Task<string[]> FindApiMediaDataAsync(
        IPage page,
        string shortcode)
    {
        try
        {
            return await page.EvaluateAsync<string[]>(
                """
                async shortcode => {
                    const alphabet =
                        'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
                    let mediaId = 0n;

                    for (const character of shortcode) {
                        mediaId = mediaId * 64n + BigInt(alphabet.indexOf(character));
                    }

                    const controller = new AbortController();
                    const timeout = setTimeout(() => controller.abort(), 3000);

                    try {
                        const response = await fetch(
                            `/api/v1/media/${mediaId}/info/`,
                            {
                                credentials: 'include',
                                signal: controller.signal,
                                headers: {
                                    'X-IG-App-ID': '936619743392459'
                                }
                            }
                        );

                        if (!response.ok) return [];

                        const data = await response.json();
                        const item = data.items?.[0];
                        if (!item) return [];

                        const mediaItems = item.carousel_media ?? [item];
                        const imageUrls = [];

                        for (const media of mediaItems) {
                            if (media.media_type !== 1) continue;

                            const candidates = [
                                ...(media.image_versions2?.candidates ?? [])
                            ].sort(
                                (a, b) =>
                                    (b.width * b.height) - (a.width * a.height)
                            );

                            if (candidates[0]?.url) {
                                imageUrls.push(candidates[0].url);
                            }
                        }

                        const kind = mediaItems.some(media => media.media_type === 2)
                            ? 'video'
                            : imageUrls.length > 0 ? 'image' : 'unknown';

                        return [item.user?.username ?? '', kind, ...imageUrls];
                    } catch {
                        return [];
                    } finally {
                        clearTimeout(timeout);
                    }
                }
                """,
                shortcode);
        }
        catch
        {
            return [];
        }
    }

    private static string? GetUrlFileName(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? Path.GetFileName(uri.AbsolutePath)
            : null;
    }

    private static async Task<IReadOnlyList<string>> FindDomImageUrlsAsync(
        IPage page,
        string? referenceUrl)
    {
        if (!Uri.TryCreate(referenceUrl, UriKind.Absolute, out Uri? uri))
        {
            return [];
        }

        string fileName = Path.GetFileName(uri.AbsolutePath);

        try
        {
            return await page.EvaluateAsync<string[]>(
                """
                fileName => {
                    const candidates = [];

                    for (const image of document.querySelectorAll('img')) {
                        const values = [];

                        if (image.currentSrc) {
                            values.push({ url: image.currentSrc, width: image.naturalWidth });
                        }
                        if (image.src) {
                            values.push({ url: image.src, width: image.naturalWidth });
                        }

                        for (const item of (image.srcset ?? '').split(',')) {
                            const match = item.trim().match(/^(\S+)\s+(\d+)w$/);
                            if (match) {
                                values.push({ url: match[1], width: Number(match[2]) });
                            }
                        }

                        for (const value of values) {
                            if (value.url.includes(fileName)) {
                                candidates.push(value);
                            }
                        }
                    }

                    candidates.sort((a, b) => b.width - a.width);
                    return candidates.length > 0
                        ? [candidates[0].url]
                        : [];
                }
                """,
                fileName);
        }
        catch
        {
            return [];
        }
    }

    private static async Task<bool> IsJsonLdImageOnlyAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                () => {
                    const types = [];
                    const collect = value => {
                        if (!value || typeof value !== 'object') return;
                        if (typeof value['@type'] === 'string') {
                            types.push(value['@type'].toLowerCase());
                        }
                        for (const child of Object.values(value)) collect(child);
                    };

                    for (const script of document.querySelectorAll(
                        "script[type='application/ld+json']")) {
                        try { collect(JSON.parse(script.textContent ?? '')); }
                        catch {}
                    }

                    return types.some(type => type.includes('imageobject')) &&
                           !types.some(type => type.includes('videoobject'));
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> FindDomOwnerUsernameAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(
                """
                () => {
                    const blocked = new Set([
                        'accounts', 'direct', 'explore', 'reels',
                        'stories', 'about', 'developer', 'legal'
                    ]);

                    const candidates = Array.from(
                        document.querySelectorAll("a[href]")
                    ).map(anchor => {
                        try {
                            const url = new URL(anchor.href, location.href);
                            const parts = url.pathname.split('/').filter(Boolean);

                            if (url.hostname !== location.hostname ||
                                parts.length !== 1 ||
                                blocked.has(parts[0].toLowerCase())) {
                                return null;
                            }

                            let score = 0;
                            if (anchor.closest('article, header')) score += 100;
                            if (anchor.querySelector("img[alt*='profile picture']")) score += 200;
                            if (anchor.getAttribute('role') === 'link') score += 10;

                            return { username: parts[0], score };
                        } catch {
                            return null;
                        }
                    }).filter(Boolean);

                    candidates.sort((a, b) => b.score - a.score);
                    return candidates[0]?.username ?? null;
                }
                """);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeUsername(string username)
    {
        return username.Trim().TrimStart('@').ToLowerInvariant();
    }

    private static async Task<string[]?> FindEmbeddedMediaAsync(
        IPage page,
        string mediaId)
    {
        try
        {
            return await page.EvaluateAsync<string[]?>(
                """
                mediaId => {
                    const findString = (value, keys, seen = new Set()) => {
                        if (!value || typeof value !== 'object' || seen.has(value)) {
                            return null;
                        }

                        seen.add(value);

                        for (const key of keys) {
                            if (typeof value[key] === 'string' && value[key]) {
                                return value[key];
                            }
                        }

                        for (const child of Object.values(value)) {
                            const result = findString(child, keys, seen);
                            if (result) return result;
                        }

                        return null;
                    };

                    const collectVideoUrls = (value, result = [], seen = new Set()) => {
                        if (!value || typeof value !== 'object' || seen.has(value)) {
                            return result;
                        }

                        seen.add(value);

                        for (const [key, child] of Object.entries(value)) {
                            if ((key === 'video_url' || key === 'videoUrl') &&
                                typeof child === 'string' &&
                                /^https?:\/\//i.test(child)) {
                                result.push(child);
                            }

                            if ((key === 'video_versions' || key === 'videoVersions') &&
                                Array.isArray(child)) {
                                for (const version of child) {
                                    if (typeof version?.url === 'string' &&
                                        /^https?:\/\//i.test(version.url)) {
                                        result.push(version.url);
                                    }
                                }
                            }

                            collectVideoUrls(child, result, seen);
                        }

                        return [...new Set(result)];
                    };

                    const collectImageUrls = (value, result = [], seen = new Set()) => {
                        if (!value || typeof value !== 'object' || seen.has(value)) {
                            return result;
                        }

                        seen.add(value);

                        const imageUrl = value.display_url ??
                            value.image_versions2?.candidates?.[0]?.url ??
                            value.thumbnail_src;
                        if (typeof imageUrl === 'string') {
                            result.push(imageUrl);
                        }

                        for (const child of Object.values(value)) {
                            collectImageUrls(child, result, seen);
                        }

                        return [...new Set(result)];
                    };

                    const visit = (value, seen = new Set()) => {
                        if (!value || typeof value !== 'object' || seen.has(value)) {
                            return null;
                        }

                        seen.add(value);
                        const id = value.shortcode ?? value.code;
                        if (id === mediaId) {
                            const videos = collectVideoUrls(value);
                            const images = collectImageUrls(value);
                            const video = videos[0] ?? '';
                            const mediaType = value.media_type ??
                                value.mediaType ?? value.__typename;
                            const explicitVideo = value.is_video === true ||
                                mediaType === 2 ||
                                /video/i.test(String(mediaType));
                            const explicitImage = value.is_video === false ||
                                mediaType === 1 ||
                                /image|graphimage/i.test(String(mediaType));
                            const kind = videos.length > 0 || explicitVideo
                                ? 'video'
                                : explicitImage ? 'image' : 'unknown';
                            const owner = value.owner?.username ??
                                value.user?.username ??
                                findString(value, ['username']);
                            const thumbnail = value.display_url ??
                                value.thumbnail_src ??
                                value.image_versions2?.candidates?.[0]?.url ??
                                findString(value, ['display_url', 'thumbnail_src']);

                            return [
                                owner ?? '',
                                video,
                                thumbnail ?? '',
                                id ?? '',
                                kind,
                                ...videos.map(url => `video:${url}`),
                                ...images.map(url => `image:${url}`)
                            ];
                        }

                        for (const child of Object.values(value)) {
                            const result = visit(child, seen);
                            if (result) return result;
                        }

                        return null;
                    };

                    for (const script of document.querySelectorAll(
                        "script[type='application/json'], script[id]")) {
                        try {
                            const result = visit(JSON.parse(script.textContent ?? ''));
                            if (result) return result;
                        } catch {}
                    }

                    return null;
                }
                """,
                mediaId);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractUsername(
        string? title,
        string? description,
        string url)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            Match descriptionAuthor = Regex.Match(
                description,
                @"(?:^|-\s*)(?<username>[A-Za-z0-9._]+)\s+on\s+",
                RegexOptions.IgnoreCase);

            if (descriptionAuthor.Success)
            {
                return descriptionAuthor.Groups["username"].Value;
            }
        }

        foreach (string? metadataText in new[] { title, description })
        {
            if (string.IsNullOrWhiteSpace(metadataText))
            {
                continue;
            }

            Match handleMatch = Regex.Match(
                metadataText,
                @"@(?<username>[A-Za-z0-9._]+)",
                RegexOptions.IgnoreCase);

            if (handleMatch.Success)
            {
                return handleMatch.Groups["username"].Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            Match displayNameFallback = Regex.Match(
                title,
                @"(?<username>[A-Za-z0-9._]+)\s+on Instagram",
                RegexOptions.IgnoreCase);

            if (displayNameFallback.Success)
            {
                return displayNameFallback.Groups["username"].Value;
            }
        }

        Match urlMatch = Regex.Match(
            url,
            @"instagram\.com/(?<username>[A-Za-z0-9._]+)/(?:reel|p|tv)/",
            RegexOptions.IgnoreCase);

        return urlMatch.Success
            ? urlMatch.Groups["username"].Value
            : null;
    }

    private static async Task<string?> GetMetaContentAsync(
        IPage page,
        params string[] properties)
    {
        foreach (string property in properties)
        {
            string? value = await GetAttributeAsync(
                page,
                $"meta[property='{property}'], meta[name='{property}']",
                "content");

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static async Task<string?> GetAttributeAsync(
        IPage page,
        string selector,
        string attribute)
    {
        ILocator locator = page.Locator(selector);

        return await locator.CountAsync() > 0
            ? await locator.First.GetAttributeAsync(attribute)
            : null;
    }

    private static async Task StartVideoPlaybackAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(
                """
                async () => {
                    for (const video of document.querySelectorAll('video')) {
                        video.muted = true;
                        await video.play().catch(() => {});
                    }
                }
                """);
        }
        catch
        {
            // Instagram may replace the DOM while the page is loading.
        }
    }

    private static long? ParseContentLength(
        IReadOnlyDictionary<string, string> headers)
    {
        string contentRange = GetHeader(headers, "content-range");
        int slashIndex = contentRange.LastIndexOf('/');

        if (slashIndex >= 0 &&
            long.TryParse(
                contentRange[(slashIndex + 1)..],
                out long rangeTotal))
        {
            return rangeTotal;
        }

        string value = GetHeader(headers, "content-length");

        return long.TryParse(value, out long contentLength)
            ? contentLength
            : null;
    }

    private static string GetHeader(
        IReadOnlyDictionary<string, string> headers,
        string name)
    {
        return headers.TryGetValue(name, out string? value)
            ? value
            : string.Empty;
    }

    private static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme is "http" or "https";
    }

    private static bool HasPartialRangeQuery(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Query.Contains(
                   "bytestart=",
                   StringComparison.OrdinalIgnoreCase) ||
               uri.Query.Contains(
                   "byteend=",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static int CalculateQualityScore(string url)
    {
        long? bitrate = GetBitrate(url);

        return 100 + (int)Math.Clamp(
            (bitrate ?? 0) / 100_000,
            0,
            200);
    }

    private static long? GetBitrate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string? encodedValue = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .Where(parts => parts[0].Equals(
                "efg",
                StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts.Length > 1 ? parts[1] : string.Empty)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(encodedValue))
        {
            return null;
        }

        try
        {
            string base64 = Uri.UnescapeDataString(encodedValue)
                .Replace('-', '+')
                .Replace('_', '/');

            base64 = base64.PadRight(
                base64.Length + (4 - base64.Length % 4) % 4,
                '=');

            string json = Encoding.UTF8.GetString(
                Convert.FromBase64String(base64));

            using JsonDocument document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty(
                       "bitrate",
                       out JsonElement bitrateElement) &&
                   bitrateElement.TryGetInt64(out long bitrate)
                ? bitrate
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeInstagramVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return url;
        }

        string[] retainedParameters = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter =>
            {
                string name = parameter.Split('=', 2)[0];

                return !name.Equals(
                           "bytestart",
                           StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals(
                           "byteend",
                           StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        var builder = new UriBuilder(uri)
        {
            Query = string.Join('&', retainedParameters)
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record VideoCandidate(
        string ResourceUrl,
        string ContentType,
        long? ContentLength,
        int Score,
        IReadOnlyDictionary<string, string> RequestHeaders);

    private sealed record InstagramMetadata(
        string Username,
        string MediaId,
        IReadOnlyList<string> VideoUrls,
        string? ThumbnailUrl,
        bool IsImageOnly,
        IReadOnlyList<string> ImageUrls);
}
