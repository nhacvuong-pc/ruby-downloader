namespace RubyDownloader.Models;

using System.Text.Json.Serialization;

public enum DownloadJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

public sealed class DownloadJob
{
    public required Guid Id { get; init; }
    public required string Url { get; init; }
    public required string OutputDirectory { get; init; }
    public DownloadJobStatus Status { get; internal set; } = DownloadJobStatus.Queued;
    public int ProgressPercent { get; internal set; }
    public string Stage { get; internal set; } = "queued";
    public string Message { get; internal set; } = "Task đang chờ xử lý.";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    public ProcessResponse? Result { get; internal set; }

    [JsonIgnore]
    internal TaskCompletionSource<ProcessResponse> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record CreateDownloadRequest(
    string Url,
    string? OutputDirectory = null);
