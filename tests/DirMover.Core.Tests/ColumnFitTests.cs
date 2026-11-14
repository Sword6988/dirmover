using DirMover.Core.Layout;

namespace DirMover.Core.Tests;

/// <summary>
/// 列宽算法的四条红线（简报 §10-12）：各列之和恒等于可用宽、保护列（名称/状态）不被压缩、
/// 同输入同输出、极端窄宽仍有解。
/// </summary>
/// <remarks>
/// 这四条都是"窗口变窄时数据是否还看得见"的直接后果：之和不等会出现横向滚动条或右侧留白；
/// 保护列被压缩会让状态文案被裁（原项目的历史缺陷）。
/// </remarks>
public sealed class ColumnFitTests
{
    private static readonly int[] Needs = { 120, 300, 300, 200 };
    private static readonly int[] Minimums = { 120, 90, 90, 200 };
    private static readonly int[] Quotas = { 24, 140, 140, 72 };

    [Fact]
    public void Columns_AreFourAndNameStatusAreProtected()
    {
        Assert.Equal(4, ColumnFitCalculator.Columns.Count);
        Assert.Equal(new[] { "name", "current", "target", "status" },
            ColumnFitCalculator.Columns.Select(c => c.Key).ToArray());
        Assert.True(ColumnFitCalculator.Columns[0].Protected);
        Assert.False(ColumnFitCalculator.Columns[1].Protected);
        Assert.False(ColumnFitCalculator.Columns[2].Protected);
        Assert.True(ColumnFitCalculator.Columns[3].Protected);
        Assert.Equal(24, ColumnFitCalculator.Columns[0].Quota);
        Assert.Equal(72, ColumnFitCalculator.Columns[3].Quota);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(420)]
    [InlineData(720)]
    [InlineData(919)]
    [InlineData(920)]
    [InlineData(921)]
    [InlineData(1220)]
    [InlineData(2400)]
    public void Fit_SumAlwaysEqualsAvailable(int available)
    {
        int[] widths = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, available);
        Assert.Equal(available, widths.Sum());
    }

    [Fact]
    public void Fit_SumIdentityHoldsAcrossWholeViewportRange()
    {
        for (int available = 0; available <= 2400; available += 7)
        {
            int[] widths = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, available);
            Assert.Equal(4, widths.Length);
            Assert.Equal(available, widths.Sum());
            Assert.All(widths, w => Assert.True(w >= 0, $"宽度不得为负（available={available}）"));
        }
    }

    [Fact]
    public void Fit_DoesNotShrinkProtectedColumnsWhenViewportIsNarrow()
    {
        // 可用宽比"内容需要总和"少 220：只应有两条路径列让步。
        int[] widths = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, 700);

        Assert.Equal(700, widths.Sum());
        Assert.True(widths[0] >= Needs[0], "名称列（保护列）不得低于内容需要宽度");
        Assert.True(widths[3] >= Needs[3], "状态列（保护列）一个字都不许裁");
        Assert.True(widths[1] < Needs[1], "路径列应承担压缩");
        Assert.True(widths[2] < Needs[2], "路径列应承担压缩");
    }

    [Fact]
    public void Fit_ProtectsColumnsEvenIfCallerPassesZeroMinimums()
    {
        // 保护是算法自身的性质：调用方传错下限也压不坏名称列与状态列。
        int[] widths = ColumnFitCalculator.Fit(Needs, new[] { 0, 0, 0, 0 }, Quotas, 700);
        Assert.Equal(700, widths.Sum());
        Assert.True(widths[0] >= Needs[0]);
        Assert.True(widths[3] >= Needs[3]);
    }

    [Fact]
    public void Fit_GivesSurplusToPathColumnsFirst()
    {
        int[] widths = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, 1220);

        Assert.Equal(1220, widths.Sum());
        Assert.True(widths[0] <= Needs[0] + ColumnFitCalculator.Columns[0].Quota,
            "名称列承载 2~5 个固定短文本，多出来的宽度只应变成极少留白");
        Assert.True(widths[1] > Needs[1], "路径列应优先吃到余量");
        Assert.True(widths[2] > Needs[2], "路径列应优先吃到余量");
    }

    [Fact]
    public void Fit_IsDeterministicForSameInput()
    {
        int[] first = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, 913);
        int[] second = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, 913);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Fit_StillHasSolutionAtPathologicalWidth()
    {
        // 连所有下限之和都放不下（正常会被窗口最小尺寸挡住）：唯一还能保证的是总和恒等。
        int[] widths = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, 200);
        Assert.Equal(200, widths.Sum());
        Assert.All(widths, w => Assert.True(w >= 1));
    }

    [Fact]
    public void Fit_EmptyInputReturnsEmpty()
    {
        Assert.Empty(ColumnFitCalculator.Fit(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 500));
    }

    [Fact]
    public void Fit_NegativeAvailableIsTreatedAsZero()
    {
        int[] widths = ColumnFitCalculator.Fit(Needs, Minimums, Quotas, -50);
        Assert.Equal(0, widths.Sum());
    }
}
