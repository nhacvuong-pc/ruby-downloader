namespace RubyDownloader.Models;

internal sealed record DownloadResult(
    string OutputPath,
    long FileSize);
