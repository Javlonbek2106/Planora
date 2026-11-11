using System.Threading.Channels;
using BuildingDraft.Domain.Abstractions;

namespace BuildingDraft.Infrastructure;

// In-process queue. Jobs are durable in the database, so a restart loses nothing: the worker re-enqueues
// unfinished jobs on startup.
public sealed class ChannelExportQueue : IExportQueue
{
    readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new() { SingleReader = true });

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct) => channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}
