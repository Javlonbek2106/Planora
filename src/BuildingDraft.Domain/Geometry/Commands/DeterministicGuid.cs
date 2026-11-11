using System.Security.Cryptography;
using System.Text;

namespace BuildingDraft.Domain.Geometry.Commands;

static class DeterministicGuid
{
    // Derived ids keep command replay (server vs. offline client) reproducible.
    public static Guid From(Guid source, string purpose) =>
        new(SHA256.HashData([..source.ToByteArray(), ..Encoding.UTF8.GetBytes(purpose)])[..16]);
}
