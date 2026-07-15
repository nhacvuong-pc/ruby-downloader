namespace RubyDownloader.Models;

internal sealed record ProcessResponse(
    int SchemaVersion,
    bool Success,
    string? Platform,
    string? MediaType,
    string? SourceUrl,
    string? Username,
    string? MediaId,
    IReadOnlyList<OutputFileInfo> Files,
    TimingInfo Timings,
    ProcessError? Error);

internal sealed record OutputFileInfo(
    string Type,
    string Path,
    string FileName,
    string ContentType,
    long SizeBytes);

internal sealed record TimingInfo(
    long ResolveMs,
    long DownloadMs,
    long TotalMs);

internal sealed record ProcessError(
    string Code,
    string Message);
