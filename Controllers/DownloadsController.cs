using Microsoft.AspNetCore.Mvc;
using RubyDownloader.Models;
using RubyDownloader.Services;

namespace RubyDownloader.Controllers;

[ApiController]
[Route("api/downloads")]
public sealed class DownloadsController : ControllerBase
{
    private readonly DownloadJobQueue _queue;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        DownloadJobQueue queue,
        ILogger<DownloadsController> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType<ProcessResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProcessResponse>> CreateAsync(
        CreateDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            ModelState.AddModelError(nameof(request.Url), "URL không được để trống.");
            return ValidationProblem(ModelState);
        }

        DownloadJob job;

        try
        {
            job = _queue.Enqueue(request);
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(nameof(request.OutputDirectory), ex.Message);
            return ValidationProblem(ModelState);
        }
        _logger.LogInformation("API đã tiếp nhận download task {JobId}", job.Id);

        ProcessResponse result = await job.Completion.Task.WaitAsync(HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpGet("{id:guid}", Name = nameof(Get))]
    [ProducesResponseType<DownloadJob>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadJob> Get(Guid id)
    {
        return _queue.TryGet(id, out DownloadJob? job) && job is not null
            ? Ok(job)
            : NotFound(new { message = "Không tìm thấy task." });
    }
}
