namespace BuildingDraft.Domain.Users;

// The local record of a person authenticated by an external identity provider (ExternalSubject = the token's "sub").
public sealed class User
{
    User()
    {
    }

    public Guid Id { get; private set; }
    public string ExternalSubject { get; private set; } = "";
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }

    public static User Create(string externalSubject, string? email, string? displayName, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(externalSubject)
            ? throw new DomainException("INVALID_SUBJECT", "The token has no subject.")
            : new()
            {
                Id = Guid.CreateVersion7(),
                ExternalSubject = externalSubject,
                Email = email?.Trim(),
                DisplayName = displayName?.Trim(),
                CreatedAt = now
            };

    public void Deactivate() => IsActive = false;
}
