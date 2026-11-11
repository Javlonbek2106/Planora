using BuildingDraft.Domain.Abstractions;

namespace BuildingDraft.Infrastructure.Persistence;

// Files live outside the database, under a root folder that is never served directly (private storage).
public sealed class LocalFileStorage(string root) : IFileStorage
{
    readonly string root = Path.GetFullPath(root);

    public async Task SaveAsync(string key, byte[] content, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(root, key));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException("Storage key escapes the storage root.", nameof(key));
    }
}
