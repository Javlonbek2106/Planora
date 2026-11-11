namespace BuildingDraft.Domain.Projects;

public enum VariantType { Original, Alternative }

// Created only through Project, which enforces the Original Layout rules.
public sealed class DesignVariant
{
    DesignVariant()
    {
    }

    internal DesignVariant(Guid id, Guid projectId, string name, VariantType type, Guid? sourceVariantId, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Name = name;
        Type = type;
        SourceVariantId = sourceVariantId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = "";
    public VariantType Type { get; private set; }
    public Guid? SourceVariantId { get; private set; }
    public bool IsLocked { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // Every variant starts with version 1 (the empty or copied initial geometry).
    public int CurrentVersionNumber { get; private set; } = 1;

    public void Rename(string name) => Name = ProjectRules.RequireName(name);

    // Approving the plan freezes the variant; the Original Layout is never unlocked.
    public void Approve() => IsLocked = true;

    public void EnsureEditable()
    {
        if (!IsLocked)
            return;
        throw new DomainException("ORIGINAL_LAYOUT_PROTECTED",
            Type == VariantType.Original
                ? "The Original Layout is approved and cannot be changed. Create a design variant instead."
                : "This variant is locked.");
    }

    public int AdvanceVersion(int expectedVersion)
    {
        EnsureVersion(expectedVersion);
        return ++CurrentVersionNumber;
    }

    public void EnsureVersion(int expectedVersion)
    {
        if (expectedVersion != CurrentVersionNumber)
            throw new DomainException("VERSION_CONFLICT",
                $"Expected version {expectedVersion}, but the current version is {CurrentVersionNumber}.");
    }

    internal void Delete()
    {
        if (Type == VariantType.Original)
            throw new DomainException("ORIGINAL_LAYOUT_PROTECTED", "The Original Layout cannot be deleted.");
        IsDeleted = true;
    }
}
