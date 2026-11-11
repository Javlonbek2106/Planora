using BuildingDraft.Api.Infrastructure;
using BuildingDraft.Application;
using BuildingDraft.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingDraft.Api.Tests;

public class ExportWorkerTests
{
    sealed class FlakyRunner(int failures) : IExportJobRunner
    {
        public int Calls;
        public Task<IReadOnlyList<Guid>> UnfinishedJobIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task RunAsync(Guid jobId, CancellationToken ct) =>
            Interlocked.Increment(ref Calls) <= failures ? throw new InvalidOperationException("database is locked") : Task.CompletedTask;
    }

    sealed class NoQueue : IExportQueue
    {
        public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct) => ValueTask.CompletedTask;
        public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    static ExportWorker Worker(FlakyRunner runner, int maxAttempts = 3)
    {
        var services = new ServiceCollection().AddScoped<IExportJobRunner>(_ => runner).BuildServiceProvider();
        return new(new NoQueue(), services.GetRequiredService<IServiceScopeFactory>(),
            new ExportWorkerOptions { MaxAttempts = maxAttempts, RetryBaseDelay = TimeSpan.FromMilliseconds(1) }, NullLogger<ExportWorker>.Instance);
    }

    [Fact]
    public async Task A_transient_infrastructure_error_is_retried_until_it_works()
    {
        var runner = new FlakyRunner(failures: 2);

        await Worker(runner).RunWithRetryAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(3, runner.Calls);
    }

    [Fact]
    public async Task A_job_that_keeps_failing_is_given_up_on_without_crashing_the_worker()
    {
        var runner = new FlakyRunner(failures: int.MaxValue);

        await Worker(runner, maxAttempts: 3).RunWithRetryAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(3, runner.Calls);
    }

    [Fact]
    public async Task Cancellation_is_not_retried()
    {
        var runner = new FlakyRunner(failures: 0);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var services = new ServiceCollection().AddScoped<IExportJobRunner, CancelledRunner>().BuildServiceProvider();
        var worker = new ExportWorker(new NoQueue(), services.GetRequiredService<IServiceScopeFactory>(),
            new ExportWorkerOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) }, NullLogger<ExportWorker>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunWithRetryAsync(Guid.NewGuid(), cts.Token));
        Assert.Equal(0, runner.Calls);
    }

    sealed class CancelledRunner : IExportJobRunner
    {
        public Task<IReadOnlyList<Guid>> UnfinishedJobIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task RunAsync(Guid jobId, CancellationToken ct) => throw new OperationCanceledException(ct);
    }
}
