namespace BuildingDraft.Domain.Projects;

public enum ProjectStatus { Active, Archived }

public enum LengthUnit { Mm, Cm, M }

public static class ProjectRules
{
    public static string RequireName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? throw new DomainException("INVALID_NAME", "Name is required.") : name.Trim();
}

public sealed class Floor
{
    Floor()
    {
    }

    internal Floor(Guid id, Guid projectId, int number, double elevationMm, double heightMm, string? name)
    {
        Id = id;
        ProjectId = projectId;
        Update(number, elevationMm, heightMm, name);
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public int FloorNumber { get; private set; }
    public double ElevationMm { get; private set; }
    public double HeightMm { get; private set; }
    public string? Name { get; private set; }

    internal void Update(int number, double elevationMm, double heightMm, string? name)
    {
        if (heightMm <= 0)
            throw new DomainException("INVALID_FLOOR_HEIGHT", "Floor height must be positive.");
        (FloorNumber, ElevationMm, HeightMm, Name) = (number, elevationMm, heightMm, name?.Trim());
    }
}

// Aggregate root: one project is one apartment with its floors and design variants.
public sealed class Project
{
    readonly List<Floor> floors = [];
    readonly List<DesignVariant> variants = [];

    Project()
    {
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = "";
    public string? Address { get; private set; }
    public string? ConstructionState { get; private set; }
    public ProjectStatus Status { get; private set; }
    public LengthUnit Units { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<Floor> Floors => floors;
    public IReadOnlyList<DesignVariant> Variants => variants;
    public DesignVariant Original => variants.Single(v => v.Type == VariantType.Original);
    public IEnumerable<DesignVariant> ActiveVariants => variants.Where(v => !v.IsDeleted);

    public static Project Create(Guid ownerId, string name, string? address, string? constructionState, LengthUnit units, DateTimeOffset now)
    {
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
            Name = ProjectRules.RequireName(name),
            Address = address?.Trim(),
            ConstructionState = constructionState?.Trim(),
            Units = units,
            Status = ProjectStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        project.variants.Add(new(Guid.CreateVersion7(), project.Id, "Original Layout", VariantType.Original, null, now));
        return project;
    }

    public void UpdateDetails(string name, string? address, string? constructionState, LengthUnit units, DateTimeOffset now)
    {
        EnsureActive();
        (Name, Address, ConstructionState, Units, UpdatedAt) =
            (ProjectRules.RequireName(name), address?.Trim(), constructionState?.Trim(), units, now);
    }

    public void Archive(DateTimeOffset now) => (Status, UpdatedAt) = (ProjectStatus.Archived, now);

    public Floor AddFloor(int number, double elevationMm, double heightMm, string? name, DateTimeOffset now)
    {
        EnsureActive();
        EnsureFloorNumberFree(number, except: null);
        var floor = new Floor(Guid.CreateVersion7(), Id, number, elevationMm, heightMm, name);
        floors.Add(floor);
        UpdatedAt = now;
        return floor;
    }

    public Floor UpdateFloor(Guid floorId, int number, double elevationMm, double heightMm, string? name, DateTimeOffset now)
    {
        EnsureActive();
        var floor = floors.FirstOrDefault(f => f.Id == floorId) ?? throw new DomainException("NOT_FOUND", "Floor not found.");
        EnsureFloorNumberFree(number, except: floor);
        floor.Update(number, elevationMm, heightMm, name);
        UpdatedAt = now;
        return floor;
    }

    public DesignVariant GetVariant(Guid variantId) =>
        variants.FirstOrDefault(v => v.Id == variantId && !v.IsDeleted) ?? throw new DomainException("NOT_FOUND", "Variant not found.");

    // A variant may only be copied from an approved plan, so the copy starts from a stable geometry.
    public DesignVariant CreateVariant(Guid sourceVariantId, string name, DateTimeOffset now)
    {
        EnsureActive();
        var source = GetVariant(sourceVariantId);
        if (!source.IsLocked && source.Type == VariantType.Original)
            throw new DomainException("ORIGINAL_NOT_APPROVED", "Approve the Original Layout before creating design variants.");

        var variant = new DesignVariant(Guid.CreateVersion7(), Id, ProjectRules.RequireName(name), VariantType.Alternative, source.Id, now);
        variants.Add(variant);
        UpdatedAt = now;
        return variant;
    }

    public void DeleteVariant(Guid variantId, DateTimeOffset now)
    {
        GetVariant(variantId).Delete();
        UpdatedAt = now;
    }

    public void EnsureFloorsExist(IEnumerable<Guid> floorIds)
    {
        var missing = floorIds.Distinct().Where(id => floors.All(f => f.Id != id)).ToList();
        if (missing.Count > 0)
            throw new DomainException("FLOOR_NOT_FOUND", $"Floor {missing[0]} does not belong to this project.");
    }

    void EnsureActive()
    {
        if (Status == ProjectStatus.Archived)
            throw new DomainException("PROJECT_ARCHIVED", "The project is archived.");
    }

    void EnsureFloorNumberFree(int number, Floor? except)
    {
        if (floors.Any(f => f.FloorNumber == number && f != except))
            throw new DomainException("FLOOR_NUMBER_EXISTS", $"Floor number {number} already exists in this project.");
    }
}
