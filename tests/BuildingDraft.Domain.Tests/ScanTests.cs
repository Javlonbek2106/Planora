using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Pipeline;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Scans;

namespace BuildingDraft.Domain.Tests;

public class ScanTests
{
    static readonly Guid Floor = Guid.NewGuid();

    // A 4 x 3 m room whose corners are off by a few millimetres, as a real scan would be.
    static ScanCapture NoisyRoom() => new(
        [
            new(0, 0, 4003, 4),
            new(3998, -3, 4001, 3002),
            new(4000, 2999, 5, 3004),
            new(-2, 3001, 2, -1)
        ],
        Confidence: 0.82, DeviceModel: "iPhone 15 Pro");

    [Fact]
    public void Nearby_endpoints_snap_into_shared_nodes()
    {
        var proposal = ScanNormalizer.Normalize(NoisyRoom(), Floor, 2700);

        Assert.Equal(4, proposal.Command.Walls.Count);
        Assert.Equal(4, proposal.Command.Nodes.Count);
        Assert.Empty(proposal.Issues);
        Assert.All(proposal.Command.Walls, w => Assert.Equal(200, w.ThicknessMm));
        Assert.All(proposal.Command.Walls, w => Assert.Equal(2700, w.HeightMm));
    }

    [Fact]
    public void A_snapped_scan_becomes_a_valid_closed_room_geometry()
    {
        var proposal = ScanNormalizer.Normalize(NoisyRoom(), Floor, 2700);

        var outcome = GeometryProcessor.Default.Execute(new GeometryModel(), proposal.Command);

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(4, outcome.Model!.Walls.Count);
    }

    [Fact]
    public void Short_and_duplicate_walls_are_dropped_with_an_explanation()
    {
        var capture = new ScanCapture(
            [
                new(0, 0, 4000, 0),
                new(4000, 0, 4000, 30),
                new(0, 0, 4000, 10),
                new(0, 0, 20, 0)
            ],
            Confidence: 0.9);

        var proposal = ScanNormalizer.Normalize(capture, Floor, 2700);

        Assert.Single(proposal.Command.Walls);
        Assert.Equal(["WALL_TOO_SHORT", "WALL_DUPLICATE", "WALL_TOO_SHORT"], proposal.Issues.Select(i => i.Code));
    }

    [Fact]
    public void Openings_follow_their_walls_and_orphans_are_reported()
    {
        var capture = NoisyRoom() with
        {
            Openings = [new(0, OpeningKind.Door, 500, 900, 2100), new(9, OpeningKind.Window, 100, 600, 1000, 900)]
        };

        var proposal = ScanNormalizer.Normalize(capture, Floor, 2700);

        Assert.Single(proposal.Command.Openings);
        Assert.Equal(proposal.Command.Walls[0].Id, proposal.Command.Openings[0].WallId);
        Assert.Contains(proposal.Issues, i => i.Code == "OPENING_WITHOUT_WALL");
    }

    [Fact]
    public void Ids_are_generated_once_so_the_reviewed_proposal_is_what_gets_applied()
    {
        var a = ScanNormalizer.Normalize(NoisyRoom(), Floor, 2700);
        var b = ScanNormalizer.Normalize(NoisyRoom(), Floor, 2700);

        Assert.NotEqual(a.Command.Walls[0].Id, b.Command.Walls[0].Id);
    }

    [Fact]
    public void Scan_sessions_only_accept_estimate_sources()
    {
        Assert.Equal("INVALID_SCAN_SOURCE", Assert.Throws<DomainException>(
            () => ScanSession.Create(Guid.NewGuid(), Guid.NewGuid(), Floor, MeasurementSource.LaserManual, DateTimeOffset.UtcNow)).Code);
    }

    [Fact]
    public void A_session_must_be_processed_before_apply_and_cannot_be_applied_twice()
    {
        var session = ScanSession.Create(Guid.NewGuid(), Guid.NewGuid(), Floor, MeasurementSource.ArLidar, DateTimeOffset.UtcNow);

        Assert.Equal("SCAN_NOT_PROCESSED", Assert.Throws<DomainException>(session.EnsureApplicable).Code);

        session.Process("{}", 0.8, "Pixel 8");
        session.EnsureApplicable();
        session.MarkApplied(5);

        Assert.Equal("SCAN_ALREADY_APPLIED", Assert.Throws<DomainException>(session.EnsureApplicable).Code);
        Assert.Equal("SCAN_ALREADY_APPLIED", Assert.Throws<DomainException>(() => session.Process("{}", 0.8, null)).Code);
    }
}
