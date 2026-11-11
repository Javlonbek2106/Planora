using BuildingDraft.Application;

namespace BuildingDraft.Api.Infrastructure;

// MVP is single-user and has no login yet: every request acts as one configured user.
// Replace this adapter with a token-based ICurrentUser when authentication is added.
public sealed class DevCurrentUser(IConfiguration configuration) : ICurrentUser
{
    public Guid UserId { get; } = Guid.Parse(configuration["Auth:DevUserId"] ?? "00000000-0000-0000-0000-000000000001");
}
