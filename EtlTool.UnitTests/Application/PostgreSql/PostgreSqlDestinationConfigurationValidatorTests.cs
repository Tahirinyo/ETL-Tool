using EtlTool.Application.PostgreSql;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.PostgreSql;

public sealed class PostgreSqlDestinationConfigurationValidatorTests
{
    [Fact]
    public void Validate_AcceptsMappedPrimaryKeyAndSingleColumnUniqueIndex()
    {
        var destination = Destination("id");
        var columns = Columns();
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> constraints =
        [
            new PostgreSqlKeyConstraintMetadata(
                "customers_pkey", PostgreSqlKeyConstraintKind.PrimaryKey,
                [new PostgreSqlKeyColumnMetadata("id", 1, false)]),
            new PostgreSqlKeyConstraintMetadata(
                "customers_email_key", PostgreSqlKeyConstraintKind.Unique,
                [new PostgreSqlKeyColumnMetadata("email", 1, false)])
        ];

        PostgreSqlDestinationConfigurationValidator.Validate(
            destination, new HashSet<string>(["id", "email"], StringComparer.Ordinal), columns, constraints);

        Assert.Equal(["email", "id"],
            PostgreSqlDestinationConfigurationValidator.GetEligibleSingleColumnKeyColumns(constraints));
    }

    [Fact]
    public void Validate_RejectsStaleColumnsDuplicateMappingsAndIneligibleKeys()
    {
        var active = new HashSet<string>(["id", "email"], StringComparer.Ordinal);
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> nonUnique =
        [
            new PostgreSqlKeyConstraintMetadata(
                "customers_email_id_key", PostgreSqlKeyConstraintKind.Unique,
                [
                    new PostgreSqlKeyColumnMetadata("email", 1, false),
                    new PostgreSqlKeyColumnMetadata("id", 2, false)
                ])
        ];

        var stale = Destination("missing");
        Assert.Throws<InvalidOperationException>(() =>
        {
            PostgreSqlDestinationConfigurationValidator.Validate(stale, active, Columns(), nonUnique);
        });

        var duplicate = Destination("id");
        duplicate.ColumnMappings.Add(new PostgreSqlDestinationColumnMapping
        {
            OutputField = "email", DestinationColumn = "id"
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            PostgreSqlDestinationConfigurationValidator.Validate(duplicate, active, Columns(), nonUnique);
        });

        var missingOutput = Destination("id");
        missingOutput.ColumnMappings[0].OutputField = "missing";
        Assert.Throws<InvalidOperationException>(() =>
        {
            PostgreSqlDestinationConfigurationValidator.Validate(missingOutput, active, Columns(), nonUnique);
        });
    }

    [Fact]
    public void Validate_RejectsDeferrableUniqueConstraintEvenWhenPostedDirectly()
    {
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> constraints =
        [
            new PostgreSqlKeyConstraintMetadata(
                "customers_id_key",
                PostgreSqlKeyConstraintKind.Unique,
                [new PostgreSqlKeyColumnMetadata("id", 1, false)],
                IsDeferrable: true)
        ];

        Assert.Empty(PostgreSqlDestinationConfigurationValidator
            .GetEligibleSingleColumnKeyColumns(constraints));
        Assert.Throws<InvalidOperationException>(() =>
        {
            PostgreSqlDestinationConfigurationValidator.Validate(
                Destination("id"),
                new HashSet<string>(["id"], StringComparer.Ordinal),
                Columns(),
                constraints);
        });
    }

    private static PostgreSqlDestinationOptions Destination(string upsertColumn) => new()
    {
        ConnectionProfile = "WarehouseDb",
        Database = "warehouse",
        Schema = "import",
        Table = "customers",
        ColumnMappings =
        [
            new PostgreSqlDestinationColumnMapping { OutputField = "id", DestinationColumn = "id" }
        ],
        UpsertKeyColumn = upsertColumn
    };

    private static IReadOnlyList<PostgreSqlColumnMetadata> Columns() =>
    [
        new PostgreSqlColumnMetadata("id", "integer", false, 1),
        new PostgreSqlColumnMetadata("email", "text", false, 2)
    ];
}
