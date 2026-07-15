using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using RubyDownloader.Config;
using RubyDownloader.Models;
using RubyDownloader.Services;

namespace RubyDownloader;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private static async Task<int> Main(string[] args)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        string? mediaUrl = args.Length > 0 ? args[0] : null;
        string? platform = GetPlatform(mediaUrl);
        long resolveMs = 0;
        long downloadMs = 0;

        try
        {
            EnvLoader.Load();
            AppSettings settings = AppSettings.Load();

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                return WriteFailure(
                    1, "INVALID_INPUT",
                    "URL TikTok/Instagram không được để trống.",
                    mediaUrl, platform, resolveMs, downloadMs, totalStopwatch);
            }

            if (platform is null)
            {
                return WriteFailure(
                    1, "UNSUPPORTED_URL",
                    "URL không phải link TikTok hoặc Instagram hợp lệ.",
                    mediaUrl, platform, resolveMs, downloadMs, totalStopwatch);
            }

            string outputDirectory = Path.GetFullPath(
                args.Length > 1 ? args[1] : settings.DownloadPath);
            ProcessResponse successResponse;

            await using (var browserService = new BrowserService(settings))
            {
                await using BrowserSession session =
                    await browserService.CreateSessionAsync();

                var downloadService = new DownloadService(settings);
                Stopwatch resolveStopwatch = Stopwatch.StartNew();

                VideoInfo mediaInfo = platform == "tiktok"
                    ? await new TikTokService(settings).ResolveAsync(
                        session.Page,
                        mediaUrl)
                    : await new InstagramService(settings).ResolveAsync(
                        session.Page,
                        mediaUrl);

                resolveStopwatch.Stop();
                resolveMs = resolveStopwatch.ElapsedMilliseconds;

                string baseFileName = SanitizeFileName(
                    $"{mediaInfo.Username}-{mediaInfo.VideoId}");
                var files = new List<OutputFileInfo>();
                Stopwatch downloadStopwatch = Stopwatch.StartNew();

                if (mediaInfo.IsVideo)
                {
                    await DownloadVideoAsync(
                        platform,
                        outputDirectory,
                        baseFileName,
                        session.Context,
                        downloadService,
                        mediaInfo,
                        files);
                }
                else
                {
                    await DownloadImagesAsync(
                        outputDirectory,
                        baseFileName,
                        session.Context,
                        downloadService,
                        mediaInfo,
                        files);
                }

                downloadStopwatch.Stop();
                downloadMs = downloadStopwatch.ElapsedMilliseconds;

                successResponse = new ProcessResponse(
                    SchemaVersion: 1,
                    Success: true,
                    Platform: platform,
                    MediaType: mediaInfo.IsVideo ? "video" : "image",
                    SourceUrl: mediaUrl,
                    Username: mediaInfo.Username,
                    MediaId: mediaInfo.VideoId,
                    Files: files,
                    Timings: new TimingInfo(resolveMs, downloadMs, 0),
                    Error: null);
            }

            // Browser/context cleanup is complete before stdout receives JSON.
            totalStopwatch.Stop();
            successResponse = successResponse with
            {
                Timings = new TimingInfo(
                    resolveMs,
                    downloadMs,
                    totalStopwatch.ElapsedMilliseconds)
            };

            return WriteResponse(successResponse, 0);
        }
        catch (System.TimeoutException ex)
        {
            return WriteFailure(
                3, "TIMEOUT", ex.Message, mediaUrl, platform,
                resolveMs, downloadMs, totalStopwatch);
        }
        catch (PlaywrightException ex)
        {
            return WriteFailure(
                2, "PLAYWRIGHT_ERROR", ex.Message, mediaUrl, platform,
                resolveMs, downloadMs, totalStopwatch);
        }
        catch (HttpRequestException ex)
        {
            return WriteFailure(
                4, "HTTP_ERROR", ex.Message, mediaUrl, platform,
                resolveMs, downloadMs, totalStopwatch);
        }
        catch (OperationCanceledException)
        {
            return WriteFailure(
                5, "CANCELLED",
                "Quá trình đã bị hủy hoặc hết thời gian.",
                mediaUrl, platform, resolveMs, downloadMs, totalStopwatch);
        }
        catch (Exception ex)
        {
            return WriteFailure(
                6, "PROCESSING_ERROR", ex.Message, mediaUrl, platform,
                resolveMs, downloadMs, totalStopwatch);
        }
    }

    private static async Task DownloadVideoAsync(
        string platform,
        string outputDirectory,
        string baseFileName,
        IBrowserContext browserContext,
        DownloadService downloadService,
        VideoInfo mediaInfo,
        List<OutputFileInfo> files)
    {
        string videoPath = Path.Combine(
            outputDirectory,
            baseFileName + ".mp4");
        string thumbnailPath = Path.Combine(
            outputDirectory,
            baseFileName + ".jpg");

        DownloadResult videoResult = await downloadService.DownloadAsync(
            browserContext,
            mediaInfo,
            videoPath);

        files.Add(CreateFileInfo("video", videoResult, "video/mp4"));

        DownloadResult? thumbnailResult = platform == "instagram"
            ? await downloadService.GenerateVideoThumbnailAsync(
                browserContext,
                videoResult.OutputPath,
                thumbnailPath)
            : await downloadService.DownloadThumbnailAsync(
                browserContext,
                mediaInfo,
                thumbnailPath);

        if (thumbnailResult is not null)
        {
            files.Add(CreateFileInfo(
                "thumbnail",
                thumbnailResult,
                "image/jpeg"));
        }
    }

    private static async Task DownloadImagesAsync(
        string outputDirectory,
        string baseFileName,
        IBrowserContext browserContext,
        DownloadService downloadService,
        VideoInfo mediaInfo,
        List<OutputFileInfo> files)
    {
        IReadOnlyList<string> imageUrls = mediaInfo.ImageUrls ?? [];

        if (imageUrls.Count == 0)
        {
            throw new InvalidOperationException(
                "Không tìm thấy URL ảnh Instagram hợp lệ.");
        }

        for (int index = 0; index < imageUrls.Count; index++)
        {
            string imageFileName = imageUrls.Count == 1
                ? baseFileName + ".jpg"
                : $"{baseFileName}-{index + 1}.jpg";
            string imagePath = Path.Combine(outputDirectory, imageFileName);

            DownloadResult imageResult =
                await downloadService.DownloadThumbnailAsync(
                    browserContext,
                    mediaInfo with { ThumbnailUrl = imageUrls[index] },
                    imagePath)
                ?? throw new InvalidOperationException(
                    "Không tìm thấy URL ảnh Instagram hợp lệ.");

            files.Add(CreateFileInfo("image", imageResult, "image/jpeg"));
        }
    }

    private static OutputFileInfo CreateFileInfo(
        string type,
        DownloadResult result,
        string contentType)
    {
        return new OutputFileInfo(
            type,
            result.OutputPath,
            Path.GetFileName(result.OutputPath),
            contentType,
            result.FileSize);
    }

    private static int WriteFailure(
        int exitCode,
        string errorCode,
        string message,
        string? sourceUrl,
        string? platform,
        long resolveMs,
        long downloadMs,
        Stopwatch totalStopwatch)
    {
        totalStopwatch.Stop();

        return WriteResponse(
            new ProcessResponse(
                SchemaVersion: 1,
                Success: false,
                Platform: platform,
                MediaType: null,
                SourceUrl: sourceUrl,
                Username: null,
                MediaId: null,
                Files: [],
                Timings: new TimingInfo(
                    resolveMs,
                    downloadMs,
                    totalStopwatch.ElapsedMilliseconds),
                Error: new ProcessError(errorCode, message)),
            exitCode);
    }

    private static int WriteResponse(ProcessResponse response, int exitCode)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return exitCode;
    }

    private static string? GetPlatform(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string host = uri.Host.ToLowerInvariant();

        if (host == "tiktok.com" ||
            host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase) ||
            host == "tiktokv.com" ||
            host.EndsWith(".tiktokv.com", StringComparison.OrdinalIgnoreCase))
        {
            return "tiktok";
        }

        if (host == "instagram.com" ||
            host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            return "instagram";
        }

        return null;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string sanitized = new(
            value.Select(character =>
                    invalidCharacters.Contains(character) ? '_' : character)
                .ToArray());

        sanitized = sanitized.Trim().TrimEnd('.');

        return string.IsNullOrWhiteSpace(sanitized)
            ? "social-media"
            : sanitized;
    }
}
