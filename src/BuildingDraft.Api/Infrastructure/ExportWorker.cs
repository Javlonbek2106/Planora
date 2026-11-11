using BuildingDraft.Application;
using BuildingDraft.Domain.Abstractions;

namespace BuildingDraft.Api.Infrastructure;

public sealed class ExportWorker(IExportQueue queue, IServiceScopeFactory scopes, ILogger<ExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ExportRunner>().RunAsync(jobId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Export worker could not process job {JobId}", jobId);
            }
    }

    // Jobs that were pending or running when the server stopped are picked up again.
    public async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            foreach (var jobId in await scope.ServiceProvider.GetRequiredService<ExportRunner>().UnfinishedJobIdsAsync(ct))
                await queue.EnqueueAsync(jobId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover unfinished export jobs");
        }
    }
}
