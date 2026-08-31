using System.Globalization;
using EtlTool.Application.Mapping;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.PostgreSql;

public sealed class PostgreSqlSourceSchemaConverterTests
{
    private readonly PostgreSqlSourceSchemaConverter _converter = new();

    [Theory]
    [InlineData("smallint", SourceFieldType.Integer)]
    [InlineData("integer", SourceFieldType.Integer)]
    [InlineData("bigint", SourceFieldType.Integer)]
    [InlineData("numeric", SourceFieldType.Decimal)]
    [InlineData("numeric(12, 3)", SourceFieldType.Decimal)]
    [InlineData("decimal(12,-3)", SourceFieldType.Decimal)]
    [InlineData("real", SourceFieldType.Decimal)]
    [InlineData("double precision", SourceFieldType.Decimal)]
    [InlineData("boolean", SourceFieldType.Boolean)]
    [InlineData("date", SourceFieldType.Date)]
    [InlineData("timestamp without time zone", SourceFieldType.Date)]
    [InlineData("timestamp(3) with time zone", SourceFieldType.Date)]
    [InlineData("varchar", SourceFieldType.String)]
    [InlineData("varchar(64)", SourceFieldType.String)]
    [InlineData("character varying", SourceFieldType.String)]
    [InlineData("character varying (64)", SourceFieldType.String)]
    [InlineData("char", SourceFieldType.String)]
    [InlineData("char(3)", SourceFieldType.String)]
    [InlineData("character", SourceFieldType.String)]
    [InlineData("character (3)", SourceFieldType.String)]
    [InlineData("text", SourceFieldType.String)]
    public void Convert_MapsApprovedNativeTypes(string nativeType, SourceFieldType expectedType)
    {
        var schema = _converter.Convert([Column("Value", nativeType)]);

        var field = Assert.Single(schema);
        Assert.Equal("Value", field.Name);
        Assert.Equal(expectedType, field.DataType);
    }

    [Theory]
    [InlineData("numeric(1)")]
    [InlineData("numeric(1000)")]
    [InlineData("numeric(1,-1000)")]
    [InlineData("decimal(1,1000)")]
    public void Convert_MapsNumericModifiersAtPostgreSqlBounds(string nativeType)
    {
        var schema = _converter.Convert([Column("Value", nativeType)]);

        Assert.Equal(SourceFieldType.Decimal, Assert.Single(schema).DataType);
    }

    [Theory]
    [InlineData("varchar(1)")]
    [InlineData("character varying(10485760)")]
    [InlineData("char(1)")]
    [InlineData("character(10485760)")]
    public void Convert_MapsCharacterLengthModifiersAtPostgreSqlBounds(string nativeType)
    {
        var schema = _converter.Convert([Column("Value", nativeType)]);

        Assert.Equal(SourceFieldType.String, Assert.Single(schema).DataType);
    }

    [Theory]
    [InlineData("timestamp(0) without time zone")]
    [InlineData("timestamp(6) with time zone")]
    public void Convert_MapsTimestampPrecisionAtPostgreSqlBounds(string nativeType)
    {
        var schema = _converter.Convert([Column("Value", nativeType)]);

        Assert.Equal(SourceFieldType.Date, Assert.Single(schema).DataType);
    }

    [Fact]
    public void Convert_UsesOrdinalCaseInsensitiveTypeMatchingWithoutUsingCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;

            var schema = _converter.Convert(
            [
                Column("Whole", "  INTEGER  ", ordinal: 2),
                Column("Logical", "BOOLEAN", ordinal: 1)
            ]);

            Assert.Equal(["Logical", "Whole"], schema.Select(field => field.Name));
            Assert.Equal([SourceFieldType.Boolean, SourceFieldType.Integer], schema.Select(field => field.DataType));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Convert_IgnoresNullabilityAndMapsEquivalentNativeCategoriesToEquivalentSchemas()
    {
        var first = _converter.Convert(
        [
            Column("Id", "smallint", isNullable: false),
            Column("Amount", "numeric(10, 2)", isNullable: true)
        ]);
        var second = _converter.Convert(
        [
            Column("Id", "bigint", isNullable: true),
            Column("Amount", "decimal", isNullable: false)
        ]);

        Assert.Equal(
            first.Select(field => (field.Name, field.DataType)),
            second.Select(field => (field.Name, field.DataType)));
        Assert.Equal(
            first.Select(field => (field.Name, field.DataType)),
            _converter.Convert(
            [
                Column("Id", "smallint", isNullable: false),
                Column("Amount", "numeric(10, 2)", isNullable: true)
            ]).Select(field => (field.Name, field.DataType)));
        Assert.Equal(
            ["Name", "DataType"],
            typeof(SourceFieldDefinition).GetProperties().Select(property => property.Name));

        var difference = new SourceSchemaComparisonService().Compare(
            first,
            second,
            [new FieldMapping { SourceField = "Id", TargetField = "id" }]);
        Assert.False(difference.HasDifferences);
    }

    [Fact]
    public void Convert_RejectsTheFirstUnsupportedColumnWithoutReturningAPartialSchema()
    {
        var exception = Assert.Throws<PostgreSqlUnsupportedColumnTypeException>(
            () => _converter.Convert(
            [
                Column("Later", "jsonb", ordinal: 3),
                Column("UnsupportedFirst", "uuid", ordinal: 1),
                Column("Supported", "integer", ordinal: 2)
            ]));

        Assert.Equal("UnsupportedFirst", exception.ColumnName);
        Assert.Equal("uuid", exception.NativeType);
        Assert.Equal(
            "The PostgreSQL column 'UnsupportedFirst' uses unsupported type 'uuid'.",
            exception.Message);
    }

    [Theory]
    [InlineData("integer[]")]
    [InlineData("json")]
    [InlineData("jsonb")]
    [InlineData("int4range")]
    [InlineData("customer_status")]
    [InlineData("double precision extra")]
    [InlineData("character varying(16)[]")]
    [InlineData("numeric(12, 3, 1)")]
    [InlineData("numeric(0)")]
    [InlineData("numeric(1001)")]
    [InlineData("numeric(-1)")]
    [InlineData("numeric(1001,0)")]
    [InlineData("numeric(1,-1001)")]
    [InlineData("decimal(1,1001)")]
    [InlineData("numeric(one,1)")]
    [InlineData("numeric(2147483648)")]
    [InlineData("numeric(1,2147483648)")]
    [InlineData("varchar(0)")]
    [InlineData("character varying(-1)")]
    [InlineData("char(10485761)")]
    [InlineData("character(ten)")]
    [InlineData("timestamp(-1) without time zone")]
    [InlineData("timestamp(7) with time zone")]
    [InlineData("timestamp(six) with time zone")]
    [InlineData("timestamp(2147483648) with time zone")]
    public void Convert_RejectsUnapprovedOrMalformedNativeTypes(string nativeType)
    {
        var exception = Assert.Throws<PostgreSqlUnsupportedColumnTypeException>(
            () => _converter.Convert([Column("Unsupported", nativeType)]));

        Assert.Equal("Unsupported", exception.ColumnName);
        Assert.Equal(nativeType, exception.NativeType);
        Assert.DoesNotContain("Password", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_UsesExistingSchemaDifferenceSemantics()
    {
        var saved = _converter.Convert(
        [
            Column("Id", "integer", ordinal: 2),
            Column("IsActive", "boolean", ordinal: 1)
        ]);
        var mappings =
            new[]
            {
                new FieldMapping { SourceField = "Id", TargetField = "id" },
                new FieldMapping { SourceField = "IsActive", TargetField = "isActive" }
            };
        var comparer = new SourceSchemaComparisonService();

        var unchanged = comparer.Compare(
            saved,
            _converter.Convert(
            [
                Column("Id", "bigint", ordinal: 1),
                Column("IsActive", "boolean", ordinal: 2)
            ]),
            mappings);
        var added = comparer.Compare(
            saved,
            _converter.Convert(
            [
                Column("Id", "bigint", ordinal: 1),
                Column("IsActive", "boolean", ordinal: 2),
                Column("Notes", "text", ordinal: 3)
            ]),
            mappings);
        var removedAndRenamed = comparer.Compare(
            saved,
            _converter.Convert([Column("Enabled", "boolean")]),
            mappings);
        var typeChanged = comparer.Compare(
            saved,
            _converter.Convert(
            [
                Column("Id", "integer", ordinal: 1),
                Column("IsActive", "text", ordinal: 2)
            ]),
            mappings);

        Assert.False(unchanged.HasDifferences);
        Assert.Equal("Notes", Assert.Single(added.NewFields).Name);
        Assert.Equal(["IsActive", "Id"], removedAndRenamed.MissingFields.Select(field => field.Name));
        Assert.Equal("Enabled", Assert.Single(removedAndRenamed.NewFields).Name);
        Assert.Equal("IsActive", Assert.Single(typeChanged.TypeChanges).FieldName);
    }

    [Fact]
    public void Convert_ProducesSchemaThatUsesTheExistingGenericMappingService()
    {
        var pipeline = new PipelineDefinition
        {
            ExpectedSchema = _converter.Convert(
            [
                Column("Id", "integer", ordinal: 1),
                Column("IsActive", "boolean", ordinal: 2)
            ]).ToList(),
            FieldMappings =
            [
                new FieldMapping { SourceField = "Id", TargetField = "id" },
                new FieldMapping { SourceField = "IsActive", TargetField = "isActive" }
            ]
        };

        var plan = new FieldMappingService().Prepare(pipeline);

        Assert.NotNull(plan);
    }

    private static PostgreSqlColumnMetadata Column(
        string name,
        string nativeType,
        bool isNullable = true,
        int ordinal = 1) => new(name, nativeType, isNullable, ordinal);
}
