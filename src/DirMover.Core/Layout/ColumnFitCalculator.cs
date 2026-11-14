namespace DirMover.Core.Layout;

/// <summary>
/// 一列的定义：标识、表头文案、余量配额、是否为"保护列"。
/// </summary>
/// <param name="Key">列标识（name / current / target / status）。</param>
/// <param name="Header">表头文案（取自 <see cref="UiText"/>，本类不写字面量）。</param>
/// <param name="Quota">窗口变宽时该列最多额外拿多少 DIP。</param>
/// <param name="Protected">
/// 保护列：永不因视口不足而低于"内容需要宽度"。名称列是行标识，状态列"一个字都不裁"
/// 是既有产品契约（历史缺陷：150 px 的列宽把状态文案裁掉了）。
/// </param>
public sealed record ColumnSpec(string Key, string Header, int Quota, bool Protected);

/// <summary>
/// 表格列宽分配（纯函数）。移植自原 <c>ui/columns.py</c> 的 <c>fit_columns</c>，签名一致。
/// </summary>
/// <remarks>
/// 为什么把它做成 Core 里的纯函数，而不是写在窗口的 <c>SizeChanged</c> 里：
/// 这条算法有四条可断言的性质（各列之和恒等于可用宽、保护列不被压缩、同输入同输出、
/// 极端窄宽仍有解），而 UI 层在本机根本无法编译验证。做成纯函数后四条性质都能被
/// <c>ColumnFitTests</c> 钉住；UI 只负责"把 needs 量出来、把结果写进共享 GridLength"。
///
/// 为什么用最大余数法：它同时满足"和恒等"与"可复现"。四舍五入会让各列之和比可用宽
/// 多/少 1~2 px，既产生水平滚动条又让同一个窗口尺寸在两次计算中给出不同结果。
/// </remarks>
public static class ColumnFitCalculator
{
    /// <summary>值列文字左右留白（含单元格原生边距与呼吸空间）。</summary>
    public const int CellPad = 12;

    /// <summary>表头文字左右留白。</summary>
    public const int HeadPad = 14;

    /// <summary>非保护列在前者让无可让时不得低于的硬下限。</summary>
    public const int MinColumn = 90;

    /// <summary>极端窄窗下的绝对下限（仅当连所有硬下限都放不下时才会用到）。</summary>
    public const int FloorColumn = 56;

    /// <summary>未映射窗口（构建期 / 无图形会话）的保守可用宽。</summary>
    public const int FallbackViewport = 720;

    private static readonly ColumnSpec[] ColumnList =
    {
        new("name", UiText.ColumnName, 24, true),
        new("current", UiText.ColumnCurrent, 140, false),
        new("target", UiText.ColumnTarget, 140, false),
        new("status", UiText.ColumnStatus, 72, true),
    };

    /// <summary>4 列定义，顺序即界面顺序。</summary>
    public static IReadOnlyList<ColumnSpec> Columns => ColumnList;

    /// <summary>
    /// 把 <paramref name="available"/> 分给各列，返回每列宽度。
    /// </summary>
    /// <param name="needs">各列"完整显示内容"所需宽度（已含留白）。</param>
    /// <param name="minimums">各列下限；保护列由本方法强制抬到 <paramref name="needs"/>。</param>
    /// <param name="quotas">各列"变宽时最多多拿多少"，仅用于分配余量。</param>
    /// <param name="available">文本区可用宽度（不含勾选列）。</param>
    /// <returns>各列宽度，长度 == <paramref name="needs"/> 长度，且总和<b>恰等于</b>可用宽。</returns>
    public static int[] Fit(int[] needs, int[] minimums, int[] quotas, int available)
    {
        if (needs is null || needs.Length == 0)
        {
            return Array.Empty<int>();
        }

        int count = needs.Length;
        int avail = Math.Max(available, 0);
        var soft = new int[count];
        var floor = new int[count];
        for (int i = 0; i < count; i++)
        {
            int need = Math.Max(needs[i], 0);
            int minimum = minimums is not null && i < minimums.Length ? Math.Max(minimums[i], 0) : 0;

            // 保护列：下限不得低于内容需要宽度。这道抬升写在 Fit 内部而不是交给调用方，
            // 是为了让"保护列不被压缩"成为算法自身的性质，调用方传错下限也压不坏它。
            if (i < ColumnList.Length && ColumnList[i].Protected)
            {
                minimum = Math.Max(minimum, need);
            }

            floor[i] = minimum;
            soft[i] = Math.Max(need, minimum);
        }

        long total = 0;
        foreach (int width in soft)
        {
            total += width;
        }

        if (total == avail)
        {
            return soft;
        }

        if (total < avail)
        {
            // 余量：先按角色配额分（路径列拿大头），配额用尽后再按内容比例摊。
            var widths = new int[count];
            Array.Copy(soft, widths, count);
            int surplus = avail - (int)total;
            var quota = new int[count];
            for (int i = 0; i < count; i++)
            {
                quota[i] = quotas is not null && i < quotas.Length ? Math.Max(quotas[i], 0) : 0;
            }

            int room = Math.Min(surplus, Sum(quota));
            if (room > 0)
            {
                int[] add = LargestRemainder(room, quota);
                for (int i = 0; i < count; i++)
                {
                    widths[i] += add[i];
                }

                surplus -= room;
            }

            if (surplus > 0)
            {
                int[] add = LargestRemainder(surplus, soft);
                for (int i = 0; i < count; i++)
                {
                    widths[i] += add[i];
                }
            }

            return widths;
        }

        // 视口不足：先让"有余量可让"的列按余量等比让步（保护列余量为 0，不参与）。
        int deficit = (int)total - avail;
        var slack = new int[count];
        for (int i = 0; i < count; i++)
        {
            slack[i] = soft[i] - floor[i];
        }

        if (Sum(slack) >= deficit)
        {
            int[] cut = LargestRemainder(deficit, slack);
            var widths = new int[count];
            for (int i = 0; i < count; i++)
            {
                widths[i] = soft[i] - cut[i];
            }

            return widths;
        }

        // 连所有下限都放不下（被窗口最小尺寸挡住的病态输入）。此时"保护列不压缩"已无解，
        // 唯一还能保证的不变式是"总和恰等于可用宽"——按下限比例整体压缩，用最大余数法
        // 保证精确配平（而不是像逐格 ±1 的写法那样可能凑不满）。
        return LargestRemainder(avail, floor);
    }

    private static int Sum(IReadOnlyList<int> values)
    {
        long sum = 0;
        for (int i = 0; i < values.Count; i++)
        {
            sum += Math.Max(values[i], 0);
        }

        return (int)Math.Min(sum, int.MaxValue);
    }

    /// <summary>
    /// 把 <paramref name="amount"/> 按权重拆成整数份，总和<b>恰为</b> <paramref name="amount"/>。
    /// </summary>
    /// <param name="amount">待分配的整数总量。</param>
    /// <param name="weights">权重（非负）。</param>
    private static int[] LargestRemainder(int amount, IReadOnlyList<int> weights)
    {
        int count = weights?.Count ?? 0;
        if (count == 0)
        {
            return Array.Empty<int>();
        }

        var result = new int[count];
        if (amount <= 0)
        {
            return result;
        }

        long weightTotal = 0;
        for (int i = 0; i < count; i++)
        {
            weightTotal += Math.Max(weights![i], 0);
        }

        if (weightTotal <= 0)
        {
            // 无权重可依据：平均分，余数给靠前的列（可复现）。
            int each = amount / count;
            int rest = amount % count;
            for (int i = 0; i < count; i++)
            {
                result[i] = each + (i < rest ? 1 : 0);
            }

            return result;
        }

        var remainder = new double[count];
        int assigned = 0;
        for (int i = 0; i < count; i++)
        {
            double raw = amount * (double)Math.Max(weights![i], 0) / weightTotal;
            int whole = (int)Math.Floor(raw);
            result[i] = whole;
            remainder[i] = raw - whole;
            assigned += whole;
        }

        int left = amount - assigned;
        if (left <= 0)
        {
            return result;
        }

        // 排序键：余数降序 → 权重降序 → 索引升序（同输入同输出）。
        var order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int byRemainder = remainder[b].CompareTo(remainder[a]);
            if (byRemainder != 0)
            {
                return byRemainder;
            }

            int byWeight = Math.Max(weights![b], 0).CompareTo(Math.Max(weights[a], 0));
            return byWeight != 0 ? byWeight : a.CompareTo(b);
        });

        int cursor = 0;
        while (left > 0)
        {
            result[order[cursor % count]] += 1;
            left -= 1;
            cursor += 1;
        }

        return result;
    }
}
