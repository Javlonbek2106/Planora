using BuildingDraft.Application;
using BuildingDraft.Domain.Abstractions;

namespace BuildingDraft.Api.Infrastructure;

public sealed class ExportWorkerOptions
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
}

public sealed class ExportWorker(IExportQueue queue, IServiceScopeFactory scopes, ExportWorkerOptions options, ILogger<ExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
            await RunWithRetryAsync(jobId, stoppingToken);
    }

    // The runner already turns export errors into a failed job. What is left here are infrastructure failures
    // (for example the database being briefly unavailable), which are retried with growing delays; a job that still
    // cannot be processed stays unfinished and is picked up again when the service restarts.
    public async Task RunWithRetryAsync(Guid jobId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IExportJobRunner>().RunAsync(jobId, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= options.MaxAttempts)
                {
                    logger.LogError(ex, "Export worker gave up on job {JobId} after {Attempts} attempts; it stays unfinished until the next start", jobId, attempt);
                    return;
                }

                logger.LogWarning(ex, "Export job {JobId} hit an infrastructure error on attempt {Attempt}; retrying", jobId, attempt);
                await Task.Delay(options.RetryBaseDelay * Math.Pow(5, attempt - 1), ct);
            }
    }

    // Jobs that were pending or running when the server stopped are picked up again.
    public async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            foreach (var jobId in await scope.ServiceProvider.GetRequiredService<IExportJobRunner>().UnfinishedJobIdsAsync(ct))
                await queue.EnqueueAsync(jobId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover unfinished export jobs");
        }
    }
}
