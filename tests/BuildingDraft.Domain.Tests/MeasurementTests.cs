using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Measurements;

namespace BuildingDraft.Domain.Tests;

public class MeasurementTests
{
    static Measurement Record(MeasurementSource source, double? confidence = null) =>
        Measurement.Record(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4250, MeasurementReference.Inner,
            source, confidence, "Bosch GLM", null, DateTimeOffset.UtcNow);

    [Fact]
    public void New_measurements_start_unverified_even_for_laser()
    {
        Assert.Equal(MeasurementStatus.Unverified, Record(MeasurementSource.LaserManual).Status);
    }

    [Theory]
    [InlineData(MeasurementSource.Manual)]
    [InlineData(MeasurementSource.LaserManual)]
    [InlineData(MeasurementSource.LaserBluetooth)]
    public void Exact_sources_can_be_verified(MeasurementSource source)
    {
        var m = Record(source);

        m.ChangeStatus(MeasurementStatus.Verified);

        Assert.Equal(MeasurementStatus.Verified, m.Status);
    }

    [Theory]
    [InlineData(MeasurementSource.ArLidar)]
    [InlineData(MeasurementSource.AiRecognition)]
    public void Estimates_can_never_be_verified_or_applied(MeasurementSource source)
    {
        var m = Record(source, confidence: 0.8);

        Assert.Equal("MEASUREMENT_NOT_VERIFIABLE", Assert.Throws<DomainException>(() => m.ChangeStatus(MeasurementStatus.Verified)).Code);
        Assert.Equal("MEASUREMENT_NOT_EXACT", Assert.Throws<DomainException>(m.EnsureApplicable).Code);
    }

    [Fact]
    public void Estimates_require_a_confidence_between_zero_and_one()
    {
        Assert.Equal("INVALID_CONFIDENCE", Assert.Throws<DomainException>(() => Record(MeasurementSource.AiRecognition)).Code);
        Assert.Equal("INVALID_CONFIDENCE", Assert.Throws<DomainException>(() => Record(MeasurementSource.LaserManual, 1.5)).Code);
    }

    [Fact]
    public void Rejected_and_superseded_measurements_are_final()
    {
        var m = Record(MeasurementSource.Manual);
        m.ChangeStatus(MeasurementStatus.Rejected);

        Assert.Equal("MEASUREMENT_STATUS_FINAL", Assert.Throws<DomainException>(() => m.ChangeStatus(MeasurementStatus.Verified)).Code);
        Assert.Equal("MEASUREMENT_NOT_APPLICABLE", Assert.Throws<DomainException>(m.EnsureApplicable).Code);
    }

    [Fact]
    public void A_measurement_cannot_return_to_unverified()
    {
        var m = Record(MeasurementSource.Manual);
        m.ChangeStatus(MeasurementStatus.Verified);

        Assert.Equal("INVALID_STATUS_TRANSITION", Assert.Throws<DomainException>(() => m.ChangeStatus(MeasurementStatus.Unverified)).Code);
    }

    [Fact]
    public void Non_positive_values_are_rejected()
    {
        Assert.Throws<DomainException>(() => Measurement.Record(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0,
            MeasurementReference.Inner, MeasurementSource.Manual, null, null, null, DateTimeOffset.UtcNow));
    }
}
