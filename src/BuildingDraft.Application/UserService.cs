using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Users;

namespace BuildingDraft.Application;

public sealed class UserService(IUserRepository users, IUnitOfWork unitOfWork, TimeProvider clock)
{
    // Maps an authenticated token subject to a local user id, creating the user on first sight.
    public async Task<Guid> ResolveAsync(string subject, string? email, string? displayName, CancellationToken ct)
    {
        var user = await users.FindBySubjectAsync(subject, ct);
        if (user is null)
        {
            user = User.Create(subject, email, displayName, clock.GetUtcNow());
            users.Add(user);
            try
            {
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (DomainException)
            {
                // Two first requests raced; the other one created the user.
                user = await users.FindBySubjectAsync(subject, ct);
                if (user is null)
                    throw;
            }
        }

        return user.IsActive ? user.Id : throw new DomainException("USER_INACTIVE", "This account is deactivated.");
    }
}
