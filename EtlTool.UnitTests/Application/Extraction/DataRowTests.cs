using EtlTool.Application.Extraction;

namespace EtlTool.UnitTests.Application.Extraction;

public sealed class DataRowTests
{
    [Fact]
    public void Rows_HaveIndependentValueStorage()
    {
        var first = new DataRow();
        var second = new DataRow();

        first.Values["Name"] = "Ada";

        Assert.NotSame(first.Values, second.Values);
        Assert.Empty(second.Values);
    }

    [Fact]
    public void Row_PreservesSourcePositionAndOrdinalMissingAndEmptyValueSemantics()
    {
        var row = new DataRow { SourceRowNumber = 2 };

        row.Values["Name"] = "Ada";
        row.Values["name"] = "Alias";
        row.Values["Missing"] = null;
        row.Values["Empty"] = string.Empty;

        Assert.Equal(2, row.SourceRowNumber);
        Assert.Equal(StringComparer.Ordinal, row.Values.Comparer);
        Assert.Equal(4, row.Values.Count);
        Assert.Equal("Ada", Assert.IsType<string>(row.Values["Name"]));
        Assert.Equal("Alias", Assert.IsType<string>(row.Values["name"]));
        Assert.True(row.Values.ContainsKey("Missing"));
        Assert.Null(row.Values["Missing"]);
        Assert.Equal(string.Empty, Assert.IsType<string>(row.Values["Empty"]));
    }
}
