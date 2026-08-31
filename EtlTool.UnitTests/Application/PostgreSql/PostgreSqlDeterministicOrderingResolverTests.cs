using EtlTool.Application.PostgreSql;

namespace EtlTool.UnitTests.Application.PostgreSql;

public sealed class PostgreSqlDeterministicOrderingResolverTests
{
    private readonly PostgreSqlDeterministicOrderingResolver _resolver = new();

    [Fact]
    public void Resolve_UsesSingleColumnPrimaryKey()
    {
        var ordering = _resolver.Resolve([Constraint("customers_pkey", PostgreSqlKeyConstraintKind.PrimaryKey, ("Id", 1, false))]);

        Assert.Equal("customers_pkey", ordering.ConstraintName);
        Assert.Equal(["Id"], ordering.Columns);
    }

    [Fact]
    public void Resolve_PreservesCompositePrimaryKeyOrderAndPrefersItOverUnique()
    {
        var ordering = _resolver.Resolve(
        [
            Constraint("customers_email_key", PostgreSqlKeyConstraintKind.Unique, ("Email", 1, false)),
            Constraint("customers_pkey", PostgreSqlKeyConstraintKind.PrimaryKey,
                ("TenantId", 1, false), ("CustomerId", 2, false))
        ]);

        Assert.Equal("customers_pkey", ordering.ConstraintName);
        Assert.Equal(["TenantId", "CustomerId"], ordering.Columns);
    }

    [Fact]
    public void Resolve_UsesCompositeNonNullableUniqueConstraintWhenThereIsNoPrimaryKey()
    {
        var ordering = _resolver.Resolve(
        [
            Constraint("orders_customer_external_key", PostgreSqlKeyConstraintKind.Unique,
                ("CustomerId", 1, false), ("ExternalId", 2, false))
        ]);

        Assert.Equal("orders_customer_external_key", ordering.ConstraintName);
        Assert.Equal(["CustomerId", "ExternalId"], ordering.Columns);
    }

    [Fact]
    public void Resolve_SelectsMultipleUniqueConstraintsByOrdinalConstraintName()
    {
        var ordering = _resolver.Resolve(
        [
            Constraint("alpha_key", PostgreSqlKeyConstraintKind.Unique, ("Alpha", 1, false)),
            Constraint("Zeta_key", PostgreSqlKeyConstraintKind.Unique, ("Zeta", 1, false))
        ]);

        Assert.Equal("Zeta_key", ordering.ConstraintName);
        Assert.Equal(["Zeta"], ordering.Columns);
    }

    [Fact]
    public void Resolve_UsesOrderedColumnSequenceAsTheFinalTieBreaker()
    {
        var ordering = _resolver.Resolve(
        [
            Constraint("duplicate_name", PostgreSqlKeyConstraintKind.Unique, ("Zeta", 1, false)),
            Constraint("duplicate_name", PostgreSqlKeyConstraintKind.Unique, ("Alpha", 1, false))
        ]);

        Assert.Equal("duplicate_name", ordering.ConstraintName);
        Assert.Equal(["Alpha"], ordering.Columns);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_RejectsNullableOrMalformedUniqueConstraint(bool nullable)
    {
        var columns = nullable
            ? new[] { new PostgreSqlKeyColumnMetadata("Email", 1, true) }
            : new[] { new PostgreSqlKeyColumnMetadata("Email", 2, false) };

        Assert.Throws<PostgreSqlDeterministicOrderingUnavailableException>(
            () => _resolver.Resolve(
            [
                new PostgreSqlKeyConstraintMetadata(
                    "users_email_key",
                    PostgreSqlKeyConstraintKind.Unique,
                    columns)
            ]));
    }

    [Fact]
    public void Resolve_RejectsCompositeUniqueConstraintWhenAnyComponentIsNullable()
    {
        Assert.Throws<PostgreSqlDeterministicOrderingUnavailableException>(
            () => _resolver.Resolve(
            [
                Constraint(
                    "orders_tenant_external_key",
                    PostgreSqlKeyConstraintKind.Unique,
                    ("TenantId", 1, false),
                    ("ExternalId", 2, true))
            ]));
    }

    [Fact]
    public void Resolve_RejectsNonNullableUniqueConstraintWithNullsNotDistinct()
    {
        Assert.Throws<PostgreSqlDeterministicOrderingUnavailableException>(
            () => _resolver.Resolve(
            [
                new PostgreSqlKeyConstraintMetadata(
                    "users_email_key",
                    PostgreSqlKeyConstraintKind.Unique,
                    [new PostgreSqlKeyColumnMetadata("Email", 1, false)],
                    IsNullsNotDistinct: true)
            ]));
    }

    [Fact]
    public void Resolve_RejectsUnsupportedConstraintKindsAndNoCandidate()
    {
        Assert.Throws<PostgreSqlDeterministicOrderingUnavailableException>(
            () => _resolver.Resolve(
            [
                Constraint("plain_unique_index", (PostgreSqlKeyConstraintKind)99, ("Email", 1, false))
            ]));

        Assert.Throws<PostgreSqlDeterministicOrderingUnavailableException>(
            () => _resolver.Resolve([]));
    }

    private static PostgreSqlKeyConstraintMetadata Constraint(
        string name,
        PostgreSqlKeyConstraintKind kind,
        params (string Name, int Ordinal, bool IsNullable)[] columns) =>
        new(
            name,
            kind,
            columns.Select(column => new PostgreSqlKeyColumnMetadata(
                column.Name,
                column.Ordinal,
                column.IsNullable)).ToArray());
}
