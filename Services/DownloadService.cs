using Microsoft.Playwright;
using RubyDownloader.Config;
using RubyDownloader.Models;

namespace RubyDownloader.Services;

internal sealed class DownloadService
{
    private readonly int _downloadTimeoutMs;

    public DownloadService(AppSettings settings)
    {
        _downloadTimeoutMs = settings.DownloadTimeoutMs;
    }

    public async Task<DownloadResult> GenerateVideoThumbnailAsync(
        IBrowserContext browserContext,
        string videoPath,
        string outputPath)
    {
        string normalizedVideoPath = Path.GetFullPath(videoPath);
        string normalizedOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(normalizedOutputPath);

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryPath = normalizedOutputPath + ".part";
        string videoUrl = new Uri(normalizedVideoPath).AbsoluteUri;
        IPage page = await browserContext.NewPageAsync();

        try
        {
            await page.GotoAsync(
                videoUrl,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Commit,
                    Timeout = _downloadTimeoutMs
                });

            await page.EvaluateAsync(
                """
                () => new Promise((resolve, reject) => {
                    const video = document.querySelector('video');
                    const timeout = setTimeout(
                        () => reject(new Error('Video metadata timeout')),
                        15000
                    );

                    const ready = () => {
                        clearTimeout(timeout);
                        video.controls = false;
                        video.muted = true;
                        document.documentElement.style.background = '#000';
                        document.body.style.margin = '0';
                        video.style.width = `${video.videoWidth}px`;
                        video.style.height = `${video.videoHeight}px`;
                        resolve();
                    };

                    if (video.readyState >= 1) ready();
                    else video.addEventListener('loadedmetadata', ready, { once: true });
                })
                """);

            await page.EvaluateAsync(
                """
                () => new Promise((resolve, reject) => {
                    const video = document.querySelector('video');
                    const target = Math.min(
                        1,
                        Math.max(0.1, video.duration * 0.05)
                    );
                    const timeout = setTimeout(
                        () => reject(new Error('Video seek timeout')),
                        15000
                    );

                    const done = () => {
                        clearTimeout(timeout);
                        resolve();
                    };

                    video.addEventListener('seeked', done, { once: true });
                    video.currentTime = target;
                })
                """);

            byte[] frameBytes = await page.Locator("video").ScreenshotAsync(
                new LocatorScreenshotOptions
                {
                    Type = ScreenshotType.Png
                });

            string frameDataUrl =
                $"data:image/png;base64,{Convert.ToBase64String(frameBytes)}";

            await page.GotoAsync(
                "about:blank",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Commit,
                    Timeout = 5_000
                });

            await page.SetContentAsync(
                $"<img id='frame' src='{frameDataUrl}' alt='video frame'>",
                new PageSetContentOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 15_000
                });

            await page.Locator("#frame").WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 15_000
                });

            string jpegBase64 = await page.EvaluateAsync<string>(
                """
                () => {
                    const frame = document.querySelector('#frame');
                    const source = document.createElement('canvas');
                    source.width = frame.naturalWidth;
                    source.height = frame.naturalHeight;

                    const context = source.getContext('2d', {
                        willReadFrequently: true
                    });
                    context.drawImage(frame, 0, 0);

                    const pixels = context.getImageData(
                        0,
                        0,
                        source.width,
                        source.height
                    ).data;

                    const isDarkPixel = offset =>
                        pixels[offset] <= 20 &&
                        pixels[offset + 1] <= 20 &&
                        pixels[offset + 2] <= 20;

                    const isBlackColumn = x => {
                        const step = Math.max(1, Math.floor(source.height / 400));
                        let dark = 0;
                        let count = 0;

                        for (let y = 0; y < source.height; y += step) {
                            if (isDarkPixel((y * source.width + x) * 4)) dark++;
                            count++;
                        }

                        return dark / count >= 0.98;
                    };

                    const isBlackRow = y => {
                        const step = Math.max(1, Math.floor(source.width / 400));
                        let dark = 0;
                        let count = 0;

                        for (let x = 0; x < source.width; x += step) {
                            if (isDarkPixel((y * source.width + x) * 4)) dark++;
                            count++;
                        }

                        return dark / count >= 0.98;
                    };

                    let left = 0;
                    let right = source.width - 1;
                    let top = 0;
                    let bottom = source.height - 1;
                    const maxHorizontalCrop = Math.floor(source.width * 0.4);
                    const maxVerticalCrop = Math.floor(source.height * 0.4);

                    while (left < maxHorizontalCrop && isBlackColumn(left)) left++;
                    while (source.width - 1 - right < maxHorizontalCrop &&
                           isBlackColumn(right)) right--;
                    while (top < maxVerticalCrop && isBlackRow(top)) top++;
                    while (source.height - 1 - bottom < maxVerticalCrop &&
                           isBlackRow(bottom)) bottom--;

                    let width = right - left + 1;
                    let height = bottom - top + 1;

                    if (width < source.width * 0.2 ||
                        height < source.height * 0.2) {
                        left = 0;
                        top = 0;
                        width = source.width;
                        height = source.height;
                    }

                    const output = document.createElement('canvas');
                    output.width = width;
                    output.height = height;
                    output.getContext('2d').drawImage(
                        frame,
                        left,
                        top,
                        width,
                        height,
                        0,
                        0,
                        width,
                        height
                    );

                    return output.toDataURL('image/jpeg', 0.9).split(',')[1];
                }
                """);

            await File.WriteAllBytesAsync(
                temporaryPath,
                Convert.FromBase64String(jpegBase64));

            File.Move(temporaryPath, normalizedOutputPath, overwrite: true);

            return new DownloadResult(
                normalizedOutputPath,
                new FileInfo(normalizedOutputPath).Length);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<DownloadResult?> DownloadThumbnailAsync(
        IBrowserContext browserContext,
        VideoInfo videoInfo,
        string outputPath)
    {
        if (string.IsNullOrWhiteSpace(videoInfo.ThumbnailUrl))
        {
            return null;
        }

        string normalizedOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(normalizedOutputPath);

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryPath = normalizedOutputPath + ".part";

        try
        {
            IAPIResponse response = await browserContext.APIRequest.GetAsync(
                videoInfo.ThumbnailUrl,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Accept"] = "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
                        ["Referer"] = videoInfo.PageUrl
                    },
                    Timeout = _downloadTimeoutMs,
                    FailOnStatusCode = false
                });

            if (!response.Ok)
            {
                throw new InvalidOperationException(
                    $"Cannot download thumbnail. HTTP {response.Status} {response.StatusText}.");
            }

            byte[] body = await response.BodyAsync();
            string contentType = response.Headers.TryGetValue(
                "content-type",
                out string? value)
                ? value
                : string.Empty;

            if (body.Length == 0 ||
                !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The thumbnail response is empty or is not an image.");
            }

            await SaveAsJpegAsync(
                browserContext,
                body,
                contentType,
                temporaryPath);

            File.Move(temporaryPath, normalizedOutputPath, overwrite: true);

            long jpegLength = new FileInfo(normalizedOutputPath).Length;

            return new DownloadResult(
                normalizedOutputPath,
                jpegLength);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static async Task SaveAsJpegAsync(
        IBrowserContext browserContext,
        byte[] imageBytes,
        string contentType,
        string outputPath)
    {
        string dataUrl =
            $"data:{contentType};base64,{Convert.ToBase64String(imageBytes)}";

        IPage page = await browserContext.NewPageAsync();

        try
        {
            await page.SetContentAsync(
                $"<style>html,body{{margin:0;padding:0}}</style>" +
                $"<img id='source' src='{dataUrl}' alt='thumbnail'>");

            ILocator image = page.Locator("#source");
            await image.WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 15_000
                });

            int naturalWidth = await image.EvaluateAsync<int>(
                "element => element.naturalWidth");
            int naturalHeight = await image.EvaluateAsync<int>(
                "element => element.naturalHeight");

            if (naturalWidth <= 0 || naturalHeight <= 0)
            {
                throw new InvalidOperationException(
                    "Chromium could not decode the thumbnail image.");
            }

            await image.ScreenshotAsync(
                new LocatorScreenshotOptions
                {
                    Path = outputPath,
                    Type = ScreenshotType.Jpeg,
                    Quality = 90
                });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        IBrowserContext browserContext,
        VideoInfo videoInfo,
        string outputPath)
    {
        string normalizedOutputPath = Path.GetFullPath(outputPath);

        string? outputDirectory =
            Path.GetDirectoryName(normalizedOutputPath);

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryPath = normalizedOutputPath + ".part";

        try
        {
            Dictionary<string, string> headers =
                BuildDownloadHeaders(videoInfo);

            IAPIResponse response =
                await browserContext.APIRequest.GetAsync(
                    videoInfo.ResourceUrl,
                    new APIRequestContextOptions
                    {
                        Headers = headers,
                        Timeout = _downloadTimeoutMs,
                        FailOnStatusCode = false
                    });

            if (!response.Ok)
            {
                string responseText;

                try
                {
                    responseText = await response.TextAsync();
                }
                catch
                {
                    responseText = string.Empty;
                }

                throw new InvalidOperationException(
                    $"Không thể tải resource bằng Playwright context. " +
                    $"HTTP {response.Status} {response.StatusText}. " +
                    $"Response: {Truncate(responseText, 500)}");
            }

            byte[] body = await response.BodyAsync();

            if (body.Length == 0)
            {
                throw new InvalidOperationException(
                    "Resource video trả về dữ liệu rỗng.");
            }

            if (!LooksLikeMp4(body))
            {
                throw new InvalidOperationException(
                    "Downloaded resource does not have a valid MP4 signature.");
            }

            await File.WriteAllBytesAsync(
                temporaryPath,
                body);

            string contentType =
                response.Headers.TryGetValue(
                    "content-type",
                    out string? headerValue)
                    ? headerValue
                    : videoInfo.ContentType;

            if (contentType.Contains(
                    "text/html",
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);

                throw new InvalidOperationException(
                    "Resource trả về HTML thay vì video.");
            }

            File.Move(
                temporaryPath,
                normalizedOutputPath,
                overwrite: true);

            return new DownloadResult(
                normalizedOutputPath,
                body.LongLength);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static Dictionary<string, string> BuildDownloadHeaders(
        VideoInfo videoInfo)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "video/avif,video/webm,video/apng,video/*,*/*;q=0.8",
            ["Referer"] = videoInfo.PageUrl,
            ["Range"] = "bytes=0-",
            ["Sec-Fetch-Dest"] = "video",
            ["Sec-Fetch-Mode"] = "no-cors",
            ["Sec-Fetch-Site"] = "cross-site"
        };

        foreach ((string key, string value)
                 in videoInfo.RequestHeaders)
        {
            if (ShouldForwardHeader(key))
            {
                headers[key] = value;
            }
        }

        return headers;
    }

    private static bool ShouldForwardHeader(string headerName)
    {
        string[] allowedHeaders =
        [
            "accept",
            "accept-language",
            "referer",
            "origin",
            "range",
            "sec-ch-ua",
            "sec-ch-ua-mobile",
            "sec-ch-ua-platform",
            "sec-fetch-dest",
            "sec-fetch-mode",
            "sec-fetch-site",
            "user-agent"
        ];

        return allowedHeaders.Contains(
            headerName,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMp4(byte[] body)
    {
        int maximumOffset = Math.Min(body.Length - 4, 32);

        for (int index = 0; index <= maximumOffset; index++)
        {
            bool isFtyp =
                body[index] == (byte)'f' &&
                body[index + 1] == (byte)'t' &&
                body[index + 2] == (byte)'y' &&
                body[index + 3] == (byte)'p';

            bool isStyp =
                body[index] == (byte)'s' &&
                body[index + 1] == (byte)'t' &&
                body[index + 2] == (byte)'y' &&
                body[index + 3] == (byte)'p';

            if (isFtyp || isStyp)
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(
        string value,
        int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength] + "...";
    }
}
