using SBproxy.Presentation.Collections;
using Xunit;

namespace SBproxy.Monitoring.Tests;

public sealed class LazyRowListTests
{
    [Fact(DisplayName = "Lazy row list creates only indexed rows")]
    public void CreatesOnlyIndexedRows()
    {
        var created = 0;
        var rows = new LazyRowList<int, TestRow>(
            (value, _) =>
            {
                created++;
                return new TestRow(value);
            });
        rows.Replace([1, 2, 3]);

        Assert.Equal(0, rows.RealizedCount);

        Assert.Equal(2, rows[1].Value);
        Assert.Equal(1, created);
        Assert.Equal(1, rows.RealizedCount);
    }

    [Fact(DisplayName = "Lazy row list recreates a recycled viewport row")]
    public void RecreatesRecycledViewportRow()
    {
        var created = 0;
        var rows = new LazyRowList<int, TestRow>(
            (value, _) => new TestRow(value, ++created));
        rows.Replace([1, 2, 3]);

        var first = rows[1];
        rows.Recycle(1);
        var second = rows[1];

        Assert.NotSame(first, second);
        Assert.Equal(2, second.Generation);
        Assert.Equal(1, rows.RealizedCount);
    }

    [Fact(DisplayName = "Lazy row list releases realized rows without losing its source")]
    public void ReleasesRowsWithoutLosingSource()
    {
        var rows = new LazyRowList<int, TestRow>((value, _) => new TestRow(value));
        rows.Replace([1, 2, 3]);
        _ = rows[0];
        _ = rows[2];

        rows.RecycleAll();

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows.RealizedCount);
        Assert.Equal(3, rows[2].Value);
    }

    private sealed record TestRow(int Value, int Generation = 0);
}
