using System.Diagnostics;
using Microsoft.Playwright;
using RubyDownloader.Config;
using RubyDownloader.Models;

namespace RubyDownloader.Services;

internal sealed class DownloadProcessor
{
    private readonly AppSettings _settings;
    private readonly ILogger<DownloadProcessor> _logger;

    public DownloadProcessor(
        AppSettings settings,
        ILogger<DownloadProcessor> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<ProcessResponse> ProcessAsync(
        string? mediaUrl,
        string outputDirectory,
        Action<int, string, string>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        string? platform = GetPlatform(mediaUrl);
        long resolveMs = 0;
        long downloadMs = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                return Failure("INVALID_INPUT", "URL TikTok/Instagram không được để trống.");
            }

            if (platform is null)
            {
                return Failure("UNSUPPORTED_URL", "URL không phải link TikTok hoặc Instagram hợp lệ.");
            }

            string normalizedOutputDirectory = Path.GetFullPath(outputDirectory);
            ProcessResponse response;

            Report(10, "browser_starting", "Đang khởi tạo Chromium.");
            _logger.LogInformation("Khởi tạo Chromium cho platform {Platform}", platform);

            await using (var browserService = new BrowserService(_settings))
            await using (BrowserSession session = await browserService.CreateSessionAsync())
            {
                Report(20, "resolving", $"Đang phân tích nội dung {platform}.");
                _logger.LogInformation("Chromium đã sẵn sàng; bắt đầu phân tích URL");
                cancellationToken.ThrowIfCancellationRequested();
                var downloadService = new DownloadService(_settings);
                Stopwatch resolveStopwatch = Stopwatch.StartNew();

                VideoInfo mediaInfo = platform == "tiktok"
                    ? await new TikTokService(_settings).ResolveAsync(session.Page, mediaUrl)
                    : await new InstagramService(_settings).ResolveAsync(session.Page, mediaUrl);

                resolveStopwatch.Stop();
                resolveMs = resolveStopwatch.ElapsedMilliseconds;
                _logger.LogInformation(
                    "Đã tìm thấy media. Username={Username}, MediaId={MediaId}, MediaType={MediaType}, ResolveMs={ResolveMs}",
                    mediaInfo.Username, mediaInfo.VideoId,
                    mediaInfo.IsVideo ? "video" : "image", resolveMs);
                cancellationToken.ThrowIfCancellationRequested();

                if (platform == "instagram" && !mediaInfo.IsVideo)
                {
                    const string message = "URL Instagram chỉ chứa hình ảnh; service chỉ hỗ trợ tải video.";
                    Report(0, "failed", message);
                    _logger.LogWarning(
                        "Từ chối Instagram image-only. Username={Username}, MediaId={MediaId}",
                        mediaInfo.Username, mediaInfo.VideoId);
                    totalStopwatch.Stop();

                    return new ProcessResponse(
                        1, false, platform, "image", mediaUrl,
                        mediaInfo.Username, mediaInfo.VideoId, [],
                        new TimingInfo(resolveMs, 0, totalStopwatch.ElapsedMilliseconds),
                        new ProcessError("URL_ONLY_IMAGE", message));
                }

                string baseFileName = SanitizeFileName($"{mediaInfo.Username}-{mediaInfo.VideoId}");
                var files = new List<OutputFileInfo>();
                Stopwatch downloadStopwatch = Stopwatch.StartNew();

                if (mediaInfo.IsVideo)
                {
                    Report(55, "downloading_video", "Đang tải video.");
                    await DownloadVideoAsync(
                        platform, normalizedOutputDirectory, baseFileName,
                        session.Context, downloadService, mediaInfo, files);
                }
                else
                {
                    Report(55, "downloading_images", $"Đang tải {mediaInfo.ImageUrls?.Count ?? 0} ảnh.");
                    await DownloadImagesAsync(
                        normalizedOutputDirectory, baseFileName,
                        session.Context, downloadService, mediaInfo, files);
                }

                downloadStopwatch.Stop();
                downloadMs = downloadStopwatch.ElapsedMilliseconds;
                Report(90, "browser_cleanup", "Đang đóng Chromium và hoàn thiện kết quả.");
                response = new ProcessResponse(
                    1, true, platform, mediaInfo.IsVideo ? "video" : "image",
                    mediaUrl, mediaInfo.Username, mediaInfo.VideoId, files,
                    new TimingInfo(resolveMs, downloadMs, 0), null);
            }

            totalStopwatch.Stop();
            _logger.LogInformation(
                "Xử lý media thành công. ResolveMs={ResolveMs}, DownloadMs={DownloadMs}, TotalMs={TotalMs}",
                resolveMs, downloadMs, totalStopwatch.ElapsedMilliseconds);
            return response with
            {
                Timings = new TimingInfo(resolveMs, downloadMs, totalStopwatch.ElapsedMilliseconds)
            };
        }
        catch (System.TimeoutException ex) { return Failure("TIMEOUT", ex.Message, ex); }
        catch (PlaywrightException ex) { return Failure("PLAYWRIGHT_ERROR", ex.Message, ex); }
        catch (HttpRequestException ex) { return Failure("HTTP_ERROR", ex.Message, ex); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("CANCELLED", "Quá trình đã bị hủy hoặc hết thời gian.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure("PROCESSING_ERROR", ex.Message, ex); }

        ProcessResponse Failure(string code, string message, Exception? exception = null)
        {
            totalStopwatch.Stop();
            Report(0, "failed", message);
            _logger.LogError(
                exception,
                "Xử lý media thất bại. ErrorCode={ErrorCode}, Message={ErrorMessage}, TotalMs={TotalMs}",
                code, message, totalStopwatch.ElapsedMilliseconds);
            return new ProcessResponse(
                1, false, platform, null, mediaUrl, null, null, [],
                new TimingInfo(resolveMs, downloadMs, totalStopwatch.ElapsedMilliseconds),
                new ProcessError(code, message));
        }

        void Report(int percent, string stage, string message) =>
            reportProgress?.Invoke(percent, stage, message);
    }

    private async Task DownloadVideoAsync(
        string platform, string outputDirectory, string baseFileName,
        IBrowserContext browserContext, DownloadService downloadService,
        VideoInfo mediaInfo, List<OutputFileInfo> files)
    {
        string videoPath = Path.Combine(outputDirectory, baseFileName + ".mp4");
        string thumbnailPath = Path.Combine(outputDirectory, baseFileName + ".jpg");
        DownloadResult video = await downloadService.DownloadAsync(browserContext, mediaInfo, videoPath);
        files.Add(CreateFileInfo("video", video, "video/mp4"));
        _logger.LogInformation(
            "Đã tải video {FileName}, SizeBytes={SizeBytes}",
            Path.GetFileName(video.OutputPath), video.FileSize);

        DownloadResult? thumbnail = platform == "instagram"
            ? await downloadService.GenerateVideoThumbnailAsync(browserContext, video.OutputPath, thumbnailPath)
            : await downloadService.DownloadThumbnailAsync(browserContext, mediaInfo, thumbnailPath);

        if (thumbnail is not null)
        {
            files.Add(CreateFileInfo("thumbnail", thumbnail, "image/jpeg"));
            _logger.LogInformation(
                "Đã tạo thumbnail {FileName}, SizeBytes={SizeBytes}",
                Path.GetFileName(thumbnail.OutputPath), thumbnail.FileSize);
        }
    }

    private async Task DownloadImagesAsync(
        string outputDirectory, string baseFileName,
        IBrowserContext browserContext, DownloadService downloadService,
        VideoInfo mediaInfo, List<OutputFileInfo> files)
    {
        IReadOnlyList<string> imageUrls = mediaInfo.ImageUrls ?? [];
        if (imageUrls.Count == 0)
        {
            throw new InvalidOperationException("Không tìm thấy URL ảnh Instagram hợp lệ.");
        }

        for (int index = 0; index < imageUrls.Count; index++)
        {
            string fileName = imageUrls.Count == 1
                ? baseFileName + ".jpg"
                : $"{baseFileName}-{index + 1}.jpg";
            DownloadResult image = await downloadService.DownloadThumbnailAsync(
                browserContext,
                mediaInfo with { ThumbnailUrl = imageUrls[index] },
                Path.Combine(outputDirectory, fileName))
                ?? throw new InvalidOperationException("Không tìm thấy URL ảnh Instagram hợp lệ.");
            files.Add(CreateFileInfo("image", image, "image/jpeg"));
            _logger.LogInformation(
                "Đã tải ảnh {CurrentImage}/{TotalImages}: {FileName}, SizeBytes={SizeBytes}",
                index + 1, imageUrls.Count, Path.GetFileName(image.OutputPath), image.FileSize);
        }
    }

    private static OutputFileInfo CreateFileInfo(string type, DownloadResult result, string contentType) =>
        new(type, result.OutputPath, Path.GetFileName(result.OutputPath), contentType, result.FileSize);

    private static string? GetPlatform(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        string host = uri.Host.ToLowerInvariant();
        if (host == "tiktok.com" || host.EndsWith(".tiktok.com") ||
            host == "tiktokv.com" || host.EndsWith(".tiktokv.com")) return "tiktok";
        if (host == "instagram.com" || host.EndsWith(".instagram.com")) return "instagram";
        return null;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        sanitized = sanitized.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "social-media" : sanitized;
    }
}
