# RubyDownloader (.NET 8)

Console App dùng Playwright Chromium để mở TikTok hoặc Instagram, theo dõi
network và tìm resource video, sau đó tải video và thumbnail bằng browser context.

> Chỉ tải nội dung công khai mà bạn sở hữu hoặc được phép sử dụng. Không dùng công cụ để vượt quyền riêng tư, DRM hoặc kiểm soát truy cập.

## Yêu cầu

- .NET 8 SDK.
- Không cần cài Google Chrome.
- Cần cài Chromium do Playwright quản lý sau khi restore/build.

## Windows PowerShell

```powershell
Copy-Item .env.example .env
dotnet restore
dotnet build
pwsh .\bin\Debug\net8.0\playwright.ps1 install chromium

dotnet run -- "https://vt.tiktok.com/xxxxx" ".\downloads"
dotnet run -- "https://www.instagram.com/reel/SHORTCODE/" ".\downloads"
```

Nếu máy không có `pwsh`, có thể chạy script PowerShell bằng:

```powershell
powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium
```

## Linux

```bash
cp .env.example .env
dotnet restore
dotnet build
./bin/Debug/net8.0/playwright.sh install chromium

dotnet run -- "https://vt.tiktok.com/xxxxx" "./downloads"
dotnet run -- "https://www.instagram.com/reel/SHORTCODE/" "./downloads"
```

Tham so thu hai la thu muc dau ra. Video va thumbnail (neu nen tang cung cap)
duoc luu theo ten `username-videoUid.mp4` va `username-videoUid.jpg`.

## JSON response

Process chỉ ghi một JSON object trên `stdout` sau khi hoàn tất. Service gọi bên
ngoài nên chờ process kết thúc, đọc toàn bộ `stdout`, parse JSON và kiểm tra cả
`success` lẫn exit code.

```json
{"schema_version":1,"success":true,"platform":"instagram","media_type":"video","source_url":"https://www.instagram.com/reel/SHORTCODE/","username":"example","media_id":"SHORTCODE","files":[{"type":"video","path":"D:\\downloads\\example-SHORTCODE.mp4","file_name":"example-SHORTCODE.mp4","content_type":"video/mp4","size_bytes":123456},{"type":"thumbnail","path":"D:\\downloads\\example-SHORTCODE.jpg","file_name":"example-SHORTCODE.jpg","content_type":"image/jpeg","size_bytes":12345}],"timings":{"resolve_ms":2500,"download_ms":900,"total_ms":3700},"error":null}
```

Exit code `0` là thành công. Khi thất bại, `success` là `false`, `files` rỗng và
`error` chứa `code` cùng `message`. Các code hiện có gồm `INVALID_INPUT`,
`UNSUPPORTED_URL`, `TIMEOUT`, `PLAYWRIGHT_ERROR`, `HTTP_ERROR`, `CANCELLED` và
`PROCESSING_ERROR`.

JSON chỉ được ghi sau khi Chromium, page và browser context đã đóng. Service cha
vẫn nên chờ process exit rồi mới parse `stdout`. Khi HTTP request bị hủy hoặc quá
timeout, dừng cả process tree để không để Chromium chạy nền:

```csharp
using var process = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }
};

process.StartInfo.ArgumentList.Add("RubyDownloader.dll");
process.StartInfo.ArgumentList.Add(mediaUrl);
process.StartInfo.ArgumentList.Add(outputDirectory);

process.Start();
Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

try
{
    await process.WaitForExitAsync(cancellationToken);
}
catch (OperationCanceledException)
{
    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
    }

    throw;
}

string json = await stdoutTask;
string stderr = await stderrTask;
```

Để giảm tải server, giới hạn số Chromium chạy đồng thời bằng `SemaphoreSlim`
hoặc hàng đợi background; không khởi chạy một process không giới hạn cho mỗi HTTP
request.

Instagram hỗ trợ nội dung video công khai dạng Reel, Post và IGTV. Shortcode
trong URL được dùng làm `videoUid`. Nội dung riêng tư hoặc bắt buộc đăng nhập
không được hỗ trợ.

Trên một số Linux server thiếu thư viện hệ thống, cài cả dependencies:

```bash
./bin/Debug/net8.0/playwright.sh install --with-deps chromium
```

Lệnh `--with-deps` cần quyền phù hợp để cài package hệ điều hành. Google Chrome không cần được cài.

## Debug hiển thị trình duyệt

Sửa `.env`:

```properties
PLAYWRIGHT_HEADLESS=false
PLAYWRIGHT_SLOW_MO_MS=100
```

## Publish Linux x64

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/linux-x64
./bin/Release/net8.0/linux-x64/playwright.sh install chromium
```

Chromium của Playwright được cài riêng, không nằm bên trong single executable.

## Lưu ý

TikTok và Instagram có thể hiển thị CAPTCHA, yêu cầu đăng nhập hoặc thay đổi cơ
chế phát video. Project không tự động giải CAPTCHA và không đảm bảo tải được mọi
video. Khi không bắt được resource TikTok, ảnh chụp, HTML và thông tin chẩn đoán
được lưu trong `debug/`.
