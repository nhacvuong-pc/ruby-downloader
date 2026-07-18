namespace RubyDownloader.Models;

public sealed record ProcessResponse(
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

public sealed record OutputFileInfo(
    string Type,
    string Path,
    string FileName,
    string ContentType,
    long SizeBytes);

public sealed record TimingInfo(
    long ResolveMs,
    long DownloadMs,
    long TotalMs);

public sealed record ProcessError(
    string Code,
    string Message);
