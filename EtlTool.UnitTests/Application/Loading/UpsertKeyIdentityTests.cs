using EtlTool.Application.Loading;
using MongoDB.Bson;

namespace EtlTool.UnitTests.Application.Loading;

public sealed class UpsertKeyIdentityTests
{
    [Fact]
    public void Create_UsesMongoCompatibleEffectiveIdentityForSupportedScalars()
    {
        Assert.Equal(Identity(1), Identity(1L));
        Assert.Equal(Identity(1L), Identity(1m));
        Assert.Equal(Identity(1m), Identity(1d));
        Assert.IsType<decimal>(Identity(1L).EffectiveValue);
        Assert.NotEqual(Identity("1"), Identity(1));
        Assert.NotEqual(Identity("ABC"), Identity("abc"));

        var instant = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(3));
        var dateIdentity = Identity(instant);
        Assert.Equal(Identity(instant.UtcDateTime), dateIdentity);
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(dateIdentity.EffectiveValue).Kind);
    }

    [Fact]
    public void Create_CanonicalizesDatesToTheEffectiveBsonMillisecond()
    {
        var instant = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc)
            .AddTicks(1_234);
        var sameBsonMillisecond = instant.AddTicks(1);
        var nextBsonMillisecond = instant.AddTicks(TimeSpan.TicksPerMillisecond);

        var first = Identity(instant);
        var same = Identity(sameBsonMillisecond);
        var next = Identity(nextBsonMillisecond);
        var firstEffective = Assert.IsType<DateTime>(first.EffectiveValue);
        var sameEffective = Assert.IsType<DateTime>(same.EffectiveValue);

        Assert.Equal(first, same);
        Assert.NotEqual(first, next);
        Assert.Equal(0, firstEffective.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(
            new BsonDateTime(firstEffective).MillisecondsSinceEpoch,
            new BsonDateTime(sameEffective).MillisecondsSinceEpoch);
        Assert.Equal(
            new BsonDateTime(instant).MillisecondsSinceEpoch,
            new BsonDateTime(firstEffective).MillisecondsSinceEpoch);
    }

    [Fact]
    public void Create_PreservesSupportedDateTimeKindNormalizationAtBsonPrecision()
    {
        var utc = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc)
            .AddTicks(1_234);
        var local = utc.ToLocalTime();
        var unspecifiedAsUtc = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.Equal(Identity(utc), Identity(local));
        Assert.Equal(Identity(utc), Identity(unspecifiedAsUtc));
        Assert.Equal(
            DateTimeKind.Utc,
            Assert.IsType<DateTime>(Identity(local).EffectiveValue).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_RejectsMissingValues(object? value)
    {
        Assert.Throws<InvalidOperationException>(() => Identity(value));
    }

    [Fact]
    public void Create_RejectsUnsupportedOrNonfiniteValues()
    {
        Assert.Throws<InvalidOperationException>(() => Identity(new object()));
        Assert.Throws<InvalidOperationException>(() => Identity(double.NaN));
        Assert.Throws<InvalidOperationException>(() => Identity(double.PositiveInfinity));
    }

    private static UpsertKeyIdentity Identity(object? value) =>
        UpsertKeyIdentity.Create(value, "id", 2);
}
