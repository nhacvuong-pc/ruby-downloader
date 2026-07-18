# RubyDownloader Service (.NET 8)

HTTP service dùng Playwright Chromium để xử lý link TikTok hoặc Instagram. Service
chạy liên tục, nhận task qua REST API và xử lý bằng hàng đợi nền. Mỗi thời điểm chỉ
có một Chromium task hoạt động để tránh làm quá tải máy chủ.

> Chỉ tải nội dung công khai mà bạn sở hữu hoặc được phép sử dụng. Công cụ không
> hỗ trợ vượt quyền riêng tư, DRM, CAPTCHA hoặc kiểm soát truy cập.

## Chạy bằng Docker

```bash
docker compose up --build -d
docker compose ps
curl http://localhost:8080/health
```

Service lắng nghe tại `http://localhost:8080`. Container chạy bằng user `pwuser`,
không chạy bằng root. Chromium và các thư viện Linux cần thiết đã nằm trong image
Playwright chính thức.

Thư mục `./downloads` trên host được mount vào `/app/downloads` trong container.

## Tạo download task

```bash
curl -X POST http://localhost:8080/api/downloads \
  -H "Content-Type: application/json" \
  -d '{"url":"https://vt.tiktok.com/xxxxx"}'
```

Request được đưa vào hàng đợi nội bộ. Kết nối HTTP chờ worker xử lý xong và trả
trực tiếp `ProcessResponse` với status `200 OK`:

```json
{
  "schema_version": 1,
  "success": true,
  "platform": "tiktok",
  "media_type": "video",
  "source_url": "https://vt.tiktok.com/xxxxx",
  "username": "example",
  "media_id": "123456789",
  "files": [
    {
      "type": "video",
      "path": "/app/downloads/example-123456789.mp4",
      "file_name": "example-123456789.mp4",
      "content_type": "video/mp4",
      "size_bytes": 123456
    }
  ],
  "timings": {
    "resolve_ms": 2500,
    "download_ms": 900,
    "total_ms": 3700
  },
  "error": null
}
```

Không cần nhập hoặc xác nhận tương tác trong container. Client chỉ cần giữ kết nối
đến khi response hoàn tất. Với Instagram chỉ chứa hình, service không tải file và
trả:

```json
{
  "success": false,
  "platform": "instagram",
  "media_type": "image",
  "files": [],
  "error": {
    "code": "URL_ONLY_IMAGE",
    "message": "URL Instagram chỉ chứa hình ảnh; service chỉ hỗ trợ tải video."
  }
}
```

## Theo dõi log

Xem log realtime của service và từng download task:

```bash
docker compose logs -f --tail=100 ruby-downloader
```

Mỗi task có `JobId` trong logging scope. Log bao gồm lúc nhận task, khởi tạo
Chromium, phân tích media, tên và kích thước từng file, thời gian xử lý, mã lỗi và
exception liên quan. Điều chỉnh mức log bằng biến môi trường:

```yaml
Logging__LogLevel__Default: Information
```

Có thể chỉ định thư mục con nằm trong `DOWNLOAD_PATH`:

```json
{
  "url": "https://www.instagram.com/reel/SHORTCODE/",
  "output_directory": "/app/downloads/instagram"
}
```

Đường dẫn nằm ngoài `DOWNLOAD_PATH` bị từ chối để service không thể ghi tùy ý vào
filesystem.

## Chạy trực tiếp để phát triển

Yêu cầu .NET 8 SDK và Chromium của Playwright:

```bash
dotnet restore
dotnet build
./bin/Debug/net8.0/playwright.sh install --with-deps chromium
dotnet run
```

Trên Windows PowerShell:

```powershell
dotnet restore
dotnet build
pwsh .\bin\Debug\net8.0\playwright.ps1 install chromium
dotnet run
```

Mặc định ASP.NET dùng port trong launch environment. Có thể đặt rõ:

```bash
ASPNETCORE_URLS=http://localhost:8080 dotnet run
```

## Cấu hình

Các biến môi trường hỗ trợ:

- `DOWNLOAD_PATH`: thư mục gốc chứa file tải về.
- `PLAYWRIGHT_HEADLESS`: chạy Chromium headless, mặc định `true`.
- `PLAYWRIGHT_SLOW_MO_MS`: độ trễ thao tác để debug.
- `NAVIGATION_TIMEOUT_MS`: timeout điều hướng.
- `RESOURCE_TIMEOUT_MS`: timeout tìm resource.
- `DOWNLOAD_TIMEOUT_MS`: timeout tải file.
- `USER_AGENT`: user agent của browser context.

Docker Compose tự đặt `DOWNLOAD_PATH=/app/downloads` và mở port `8080`.

## Dừng service

```bash
docker compose down
```
