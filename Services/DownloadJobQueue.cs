using System.Collections.Concurrent;
using System.Threading.Channels;
using RubyDownloader.Config;
using RubyDownloader.Models;

namespace RubyDownloader.Services;

public sealed class DownloadJobQueue : BackgroundService
{
    private readonly Channel<DownloadJob> _queue = Channel.CreateBounded<DownloadJob>(
        new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly DownloadProcessor _processor;
    private readonly AppSettings _settings;
    private readonly ILogger<DownloadJobQueue> _logger;

    internal DownloadJobQueue(
        DownloadProcessor processor,
        AppSettings settings,
        ILogger<DownloadJobQueue> logger)
    {
        _processor = processor;
        _settings = settings;
        _logger = logger;
    }

    public DownloadJob Enqueue(CreateDownloadRequest request)
    {
        RemoveExpiredJobs();
        string downloadRoot = Path.GetFullPath(_settings.DownloadPath);
        string outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? downloadRoot
            : Path.GetFullPath(request.OutputDirectory);
        string relativePath = Path.GetRelativePath(downloadRoot, outputDirectory);

        if (relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Output directory phải nằm trong DOWNLOAD_PATH.");
        }

        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            Url = request.Url.Trim(),
            OutputDirectory = outputDirectory
        };

        _jobs[job.Id] = job;

        if (!_queue.Writer.TryWrite(job))
        {
            _jobs.TryRemove(job.Id, out _);
            throw new InvalidOperationException("Không thể thêm task vào hàng đợi.");
        }

        _logger.LogInformation(
            "Đã thêm task {JobId} vào hàng đợi. Url={Url}, OutputDirectory={OutputDirectory}",
            job.Id, job.Url, job.OutputDirectory);

        return job;
    }

    public bool TryGet(Guid id, out DownloadJob? job) => _jobs.TryGetValue(id, out job);

    private void RemoveExpiredJobs()
    {
        DateTimeOffset expiresBefore = DateTimeOffset.UtcNow.AddHours(-24);

        foreach ((Guid id, DownloadJob job) in _jobs)
        {
            if (job.FinishedAt < expiresBefore)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (DownloadJob job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using IDisposable? scope = _logger.BeginScope("JobId={JobId}", job.Id);
            job.Status = DownloadJobStatus.Processing;
            job.StartedAt = DateTimeOffset.UtcNow;
            UpdateProgress(job, 5, "starting", "Bắt đầu xử lý task.");
            _logger.LogInformation("Bắt đầu xử lý URL {Url}", job.Url);

            try
            {
                job.Result = await _processor.ProcessAsync(
                    job.Url,
                    job.OutputDirectory,
                    (percent, stage, message) => UpdateProgress(job, percent, stage, message),
                    stoppingToken);
                job.Status = job.Result.Success
                    ? DownloadJobStatus.Completed
                    : DownloadJobStatus.Failed;

                if (job.Result.Success)
                {
                    UpdateProgress(job, 100, "completed", "Tải file hoàn tất.");
                    _logger.LogInformation(
                        "Task hoàn tất. Files={FileCount}, TotalMs={TotalMs}",
                        job.Result.Files.Count, job.Result.Timings.TotalMs);
                }
                else
                {
                    job.Stage = "failed";
                    job.Message = job.Result.Error?.Message ?? "Task thất bại.";
                    _logger.LogWarning(
                        "Task thất bại. ErrorCode={ErrorCode}, Message={ErrorMessage}",
                        job.Result.Error?.Code, job.Result.Error?.Message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Status = DownloadJobStatus.Failed;
                job.Stage = "cancelled";
                job.Message = "Service đang dừng; task đã bị hủy.";
                _logger.LogWarning("Task bị hủy do service đang dừng");
                job.Completion.TrySetCanceled(stoppingToken);
                throw;
            }
            finally
            {
                job.FinishedAt = DateTimeOffset.UtcNow;

                if (job.Result is not null)
                {
                    job.Completion.TrySetResult(job.Result);
                }
            }
        }
    }

    private void UpdateProgress(DownloadJob job, int percent, string stage, string message)
    {
        job.ProgressPercent = Math.Clamp(percent, 0, 100);
        job.Stage = stage;
        job.Message = message;
        _logger.LogInformation(
            "Tiến trình {ProgressPercent}% [{Stage}] {ProgressMessage}",
            job.ProgressPercent, stage, message);
    }
}
