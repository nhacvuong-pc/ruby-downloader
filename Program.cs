using System.Diagnostics;
using Microsoft.Playwright;
using RubyDownloader.Config;
using RubyDownloader.Models;
using RubyDownloader.Services;

namespace RubyDownloader;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            EnvLoader.Load();

            AppSettings settings =
                AppSettings.Load();

            string? mediaUrl = args.Length > 0
                ? args[0]
                : ReadMediaUrl();

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                Console.Error.WriteLine(
                    "URL TikTok/Instagram không được để trống.");

                return 1;
            }

            bool isTikTok = IsTikTokUrl(mediaUrl);
            bool isInstagram = IsInstagramUrl(mediaUrl);

            if (!isTikTok && !isInstagram)
            {
                Console.Error.WriteLine(
                    "URL không phải link TikTok hoặc Instagram hợp lệ.");

                return 1;
            }

            string outputDirectory = args.Length > 1
                ? args[1]
                : settings.DownloadPath;

            outputDirectory =
                Path.GetFullPath(outputDirectory);

            Console.WriteLine(
                $"Output folder : {outputDirectory}");

            await using var browserService =
                new BrowserService(settings);

            await using BrowserSession session =
                await browserService.CreateSessionAsync();

            var downloadService =
                new DownloadService(settings);

            string platformName = isTikTok
                ? "TikTok"
                : "Instagram";

            Console.WriteLine();
            Console.WriteLine(
                $"Đang mở {platformName} bằng Chromium của Playwright...");

            VideoInfo videoInfo;

            if (isTikTok)
            {
                videoInfo = await new TikTokService(settings).ResolveAsync(
                    session.Page,
                    mediaUrl);
            }
            else
            {
                videoInfo = await new InstagramService(settings).ResolveAsync(
                    session.Page,
                    mediaUrl);
            }

            string baseFileName = SanitizeFileName(
                $"{videoInfo.Username}-{videoInfo.VideoId}");

            string videoOutputPath = Path.Combine(
                outputDirectory,
                baseFileName + ".mp4");

            string thumbnailOutputPath = Path.Combine(
                outputDirectory,
                baseFileName + ".jpg");

            if (!videoInfo.IsVideo)
            {
                IReadOnlyList<string> imageUrls =
                    videoInfo.ImageUrls ?? [];

                for (int index = 0; index < imageUrls.Count; index++)
                {
                    string imageOutputPath = imageUrls.Count == 1
                        ? thumbnailOutputPath
                        : Path.Combine(
                            outputDirectory,
                            $"{baseFileName}-{index + 1}.jpg");

                    DownloadResult imageResult =
                        (await downloadService.DownloadThumbnailAsync(
                            session.Context,
                            videoInfo with
                            {
                                ThumbnailUrl = imageUrls[index]
                            },
                            imageOutputPath))!;

                    Console.WriteLine(
                        $"Image        : {imageResult.OutputPath}");
                }

                return 0;
            }

            Console.WriteLine();
            Console.WriteLine(
                "Đang tải video bằng BrowserContext...");

            Stopwatch downloadStopwatch = Stopwatch.StartNew();

            DownloadResult result =
                await downloadService.DownloadAsync(
                    session.Context,
                    videoInfo,
                    videoOutputPath);

            downloadStopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine(
                "Tải video thành công.");

            Console.WriteLine(
                $"File         : {result.OutputPath}");

            Console.WriteLine(
                $"Kích thước   : {FormatSize(result.FileSize)}");

            Console.WriteLine(
                $"Download time: {downloadStopwatch.Elapsed.TotalSeconds:F2}s");

            DownloadResult? thumbnailResult;

            if (isInstagram)
            {
                thumbnailResult =
                    await downloadService.GenerateVideoThumbnailAsync(
                        session.Context,
                        result.OutputPath,
                        thumbnailOutputPath);
            }
            else
            {
                thumbnailResult =
                    await downloadService.DownloadThumbnailAsync(
                        session.Context,
                        videoInfo,
                        thumbnailOutputPath);
            }

            Console.WriteLine(
                thumbnailResult is null
                    ? "Thumbnail    : unavailable"
                    : $"Thumbnail    : {thumbnailResult.OutputPath}");

            return 0;
        }
        catch (PlaywrightException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"Lỗi Playwright: {ex.Message}");

            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Hãy build project rồi cài Chromium:");

            Console.Error.WriteLine(
                @"powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium");

            return 2;
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"Hết thời gian: {ex.Message}");

            return 3;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"Lỗi HTTP: {ex.Message}");

            return 4;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Quá trình đã bị hủy hoặc hết thời gian.");

            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"Lỗi: {ex.Message}");

            return 6;
        }
    }

    private static string? ReadMediaUrl()
    {
        Console.Write(
            "Nhập URL TikTok hoặc Instagram: ");

        return Console.ReadLine();
    }

    private static bool IsTikTokUrl(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host =
            uri.Host.ToLowerInvariant();

        return host == "tiktok.com" ||
               host.EndsWith(
                   ".tiktok.com",
                   StringComparison.OrdinalIgnoreCase) ||
               host == "tiktokv.com" ||
               host.EndsWith(
                   ".tiktokv.com",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstagramUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();

        return host == "instagram.com" ||
               host.EndsWith(
                   ".instagram.com",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(
        long bytes)
    {
        string[] units =
        [
            "B",
            "KB",
            "MB",
            "GB",
            "TB"
        ];

        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 &&
               unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();

        string sanitized = new(
            value.Select(character =>
                    invalidCharacters.Contains(character)
                        ? '_'
                        : character)
                .ToArray());

        sanitized = sanitized.Trim().TrimEnd('.');

        return string.IsNullOrWhiteSpace(sanitized)
            ? "social-video"
            : sanitized;
    }
}
