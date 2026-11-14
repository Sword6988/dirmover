namespace DirMover.Core.IO;

/// <summary>源位置的风险分类。</summary>
public enum SourceRisk
{
    /// <summary>没有风险。</summary>
    None,

    /// <summary>不是合法路径。</summary>
    Invalid,

    /// <summary>是磁盘根目录。</summary>
    DriveRoot,

    /// <summary>是系统目录（或其内部）。</summary>
    SystemDirectory,
}

/// <summary>源位置的风险判定结果。</summary>
/// <param name="Risk">风险分类。</param>
/// <param name="Reason">面向用户的原因（空串表示无风险）。</param>
public readonly record struct SourceRiskResult(SourceRisk Risk, string Reason);

/// <summary>目标校验结果。</summary>
/// <param name="Ok">是否通过。</param>
/// <param name="Reason">不通过的原因（通过时为空串）。</param>
public readonly record struct GuardResult(bool Ok, string Reason)
{
    /// <summary>通过。</summary>
    public static GuardResult Pass => new(true, string.Empty);

    /// <summary>不通过。</summary>
    /// <param name="reason">原因。</param>
    public static GuardResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// 目标/源禁区校验（PRD P0-7 的 9 条）与重解析点祖先链判定。
/// </summary>
/// <remarks>
/// 为什么这些判定必须在 Core 且必须能被单测：
/// 它们是"能不能删掉用户数据"的最后一道闸门。原实现有四处历史漏洞——目标侧禁区名单写死
/// <c>C:\</c>（系统盘非 C: 时整道闸门失效）、只比字符串不看物理位置（junction 绕过）、
/// 不还原 8.3 短名（<c>PROGRA~1</c> 绕过）、只在规划期查重解析点（规划到执行之间有分钟级
/// 时间窗，可被 <c>mklink /J</c> 抢占）。这些漏洞的共同点是"代码看起来对，但输入换一种
/// 写法就放行"，只能靠用例逐条钉住。
/// </remarks>
public static class DestinationGuard
{
    /// <summary>Windows 文件名里不允许出现的字符（<c>:</c> 仅在盘符位合法）。</summary>
    private static readonly char[] InvalidNameChars = { '<', '>', '"', '|', '?', '*' };

    /// <summary>
    /// 上标数字。<c>COM¹</c>/<c>COM²</c>/<c>COM³</c> 与 <c>COM1</c>/<c>COM2</c>/<c>COM3</c>
    /// 等价，同属设备保留名；只列 ASCII 会把这批变体放行，交给后续创建步骤去撞墙。
    /// </summary>
    private const string SuperscriptDigits = "\u00b9\u00b2\u00b3\u2074\u2075\u2076\u2077\u2078\u2079";

    /// <summary>Windows 设备保留名（大小写不敏感，含 <c>CON.txt</c> 形式）。</summary>
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    /// <summary>
    /// 校验目标路径是否可用于承接转移（9 条，先命中先返回）。
    /// </summary>
    /// <param name="target">目标绝对路径（尚未创建）。</param>
    /// <param name="sources">本次将要搬走的源目录集合。</param>
    public static GuardResult ValidateDestination(string target, IReadOnlyList<string> sources)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return GuardResult.Fail(UiText.GuardEmptyTarget());
        }

        string candidate = target.Trim().Trim('"');

        // 先剥 \\?\ / \\?\UNC\ / \\.\ 前缀，再判非法字符：否则 \\?\C:\Windows 会先撞上前缀里的
        // '?' 被当成「非法字符」拒掉，走不到后面的盘根 / 系统目录判定（结论虽同为拒绝，但 Reason
        // 会误导排查）。剥前缀后 \\?\C:\Windows 与裸写 C:\Windows 完全等价。裸写路径无前缀，此处是 no-op。
        candidate = PathTools.StripExtendedPrefix(candidate);

        char illegal = FirstIllegalCharacter(candidate);
        if (illegal != '\0')
        {
            return GuardResult.Fail(UiText.GuardIllegalCharacter(illegal));
        }

        string expanded = PathTools.Expand(candidate);
        if (!IsFullyQualified(expanded))
        {
            return GuardResult.Fail(UiText.GuardNotAbsolute());
        }

        string badSegment = InvalidSegment(expanded);
        if (badSegment.Length > 0)
        {
            return GuardResult.Fail(UiText.GuardInvalidSegment(badSegment));
        }

        string drive = Path.GetPathRoot(expanded) ?? string.Empty;
        if (drive.Length < 2)
        {
            return GuardResult.Fail(UiText.GuardMissingDrive());
        }

        if (!Directory.Exists(drive))
        {
            return GuardResult.Fail(UiText.GuardDriveMissing(drive));
        }

        // 盘根（C:\）必须拒绝：盘根下没有可承载目标的片段，任何「搬过来的用户目录」落在这里
        // 都等于摊到卷顶层，删源/回滚时极易误伤整个卷。判定与 CheckSourceTree 同款——
        // Path.GetDirectoryName("C:\") 返回 null，故「没有上级」或「上级就是自己」都算盘根。
        // 对 \\?\C:\、C:\、C:/ 等不同写法必须是同一个结论，故逐形态判定（任一命中即拒绝）。
        var targetForms = new List<string> { PathTools.Normalize(expanded) };
        AddIfNew(targetForms, PathTools.Normalize(PathTools.LongPath(expanded)));

        foreach (string form in targetForms)
        {
            string? parent = Path.GetDirectoryName(form);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, form, StringComparison.OrdinalIgnoreCase))
            {
                return GuardResult.Fail(UiText.GuardDriveRootAsTarget(drive));
            }
        }

        // 系统目录：目标与禁区根都做一次长名还原后再比（否则 C:\PROGRA~1 会绕过）。
        foreach (string forbidden in Catalog.ForbiddenTargetRoots())
        {
            var forbiddenForms = new List<string> { PathTools.Normalize(forbidden) };
            AddIfNew(forbiddenForms, PathTools.Normalize(PathTools.LongPath(forbidden)));

            foreach (string need in targetForms)
            {
                foreach (string blocked in forbiddenForms)
                {
                    if (need.Length == 0 || blocked.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(need, blocked, StringComparison.Ordinal) ||
                        need.StartsWith(blocked.TrimEnd('\\') + "\\", StringComparison.Ordinal))
                    {
                        return GuardResult.Fail(UiText.GuardForbiddenSystemDirectory(forbidden));
                    }
                }
            }
        }

        foreach (string source in sources ?? Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            if (PathTools.SamePath(expanded, source))
            {
                // 同路径不属于"非法目标"：调用方按「路径相同，无需转移」处理。
                continue;
            }

            if (PathTools.ResolvesToSame(expanded, source))
            {
                return GuardResult.Fail(UiText.GuardSamePhysical(expanded, source));
            }

            if (PathTools.IsWithin(expanded, source))
            {
                return GuardResult.Fail(UiText.GuardTargetInsideSource(expanded, source));
            }

            if (PathTools.IsWithin(source, expanded))
            {
                return GuardResult.Fail(UiText.GuardSourceInsideTarget(source, expanded));
            }
        }

        // 目标自身与其每一级已存在的上级都不得是重解析点。放进 ValidateDestination 只是
        // 规划期那一次；执行期在建目录**之前**必须再调 ReparseAncestor（时间窗可达分钟级）。
        string blockedByLink = ReparseAncestor(expanded);
        if (blockedByLink.Length > 0)
        {
            return GuardResult.Fail(UiText.GuardTargetIsLink(blockedByLink));
        }

        return GuardResult.Pass;
    }

    /// <summary>
    /// 重解析点判定的单测替换点（<c>null</c> = 走真实实现，<b>默认行为零变化</b>）。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：审计 A-11 的加固分支（"校验之后、删源之前发现目标变成链接"）要在
    /// <b>复制完成 → 写注册表 → 删源</b> 这段窗口内让目标"变成"链接。真做只能靠后台线程抢时序
    /// （大文件 + 竞态），必然是 flaky 的红线。这个钩子让用例可以确定性地进入该分支。
    /// <para>
    /// 约束（与 <c>ShellIntegration.ShellLaunchHook</c> 同一套纪律）：
    /// ① 只有 <c>internal</c>（靠 <c>InternalsVisibleTo</c> 给测试工程），生产路径读不到、也写不到；
    /// ② 默认 <c>null</c>，即生产永远走真实实现（<c>File.GetAttributes</c> + <c>LinkTarget</c>）；
    /// ③ 用例必须在 <c>try/finally</c>（或 <c>using</c> 的作用域对象）里复位，
    /// 否则会污染同一进程内其它用例。
    /// </para>
    /// </remarks>
    internal static Func<string, string>? Hook { get; set; }

    /// <summary>
    /// 返回 <paramref name="path"/> 自身、或其任一级<b>已存在</b>上级目录中的第一个重解析点；
    /// 没有命中返回空串。
    /// </summary>
    /// <param name="path">待检查路径（本身可以不存在）。</param>
    /// <remarks>
    /// 对不存在的层级返回 <c>false</c>（不是链接），因此"目标那一级还不存在"这一正常场景
    /// 不会被误伤。执行期必须再查一次的原因：规划到执行之间用户/攻击者可以用
    /// <c>mklink /J</c>（不需要管理员）把目标的某一级上级换成联接点，让以管理员身份运行的
    /// 复制把数据写进链接指向的位置，随后连同源目录一起删掉。
    /// </remarks>
    public static string ReparseAncestor(string path)
    {
        if (Hook is not null)
        {
            // 仅供单测注入；空返回值与"没有命中"同义。
            return Hook(path) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string probe = PathTools.StripExtendedPrefix(path.Trim());
        while (probe.Length > 0)
        {
            if (PathTools.IsReparsePoint(probe))
            {
                return probe;
            }

            string? parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
            {
                break;
            }

            probe = parent;
        }

        return string.Empty;
    }

    /// <summary>
    /// 源位置自身的禁区判定（转移与恢复<b>共用</b>同一套判定）。
    /// </summary>
    /// <param name="path">当前位置（来自注册表，属外部可写数据）。</param>
    /// <remarks>
    /// 威胁模型：注册表被改成 <c>C:\Windows</c> 就会"整目录搬走再删掉"。因此不仅要比字符串，
    /// 还要 ①剥掉 <c>\\?\</c> 前缀（否则 <c>\\?\C:\Windows</c> 既不等也不包含）、
    /// ②解析链接后的真实路径（否则"指向 C:\Windows 的 junction"会被判为不是系统目录）、
    /// ③还原 8.3 短名（否则 <c>C:\PROGRA~1</c> 绕过）。
    /// </remarks>
    public static SourceRiskResult CheckSourceTree(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new SourceRiskResult(SourceRisk.None, string.Empty);
        }

        string plain = PathTools.StripExtendedPrefix(path);
        string absolute;
        try
        {
            absolute = Path.GetFullPath(plain);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SourceRiskResult(SourceRisk.Invalid, UiText.SourceInvalidPath(path));
        }

        var checks = new List<(string Candidate, string Absolute)> { (plain, absolute) };

        // 链接展开：源也可能是一个"指向禁区"的重解析点，纯字符串比较看不出来。
        try
        {
            string resolved = PathTools.StripExtendedPrefix(PathTools.ResolveLinks(plain));
            if (resolved.Length > 0 &&
                !string.Equals(PathTools.Normalize(resolved), PathTools.Normalize(plain), StringComparison.Ordinal))
            {
                checks.Add((resolved, Path.GetFullPath(resolved)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 展开失败则只用原路径（fail-safe：既不因展开失败放行，也不抛异常中断规划）。
        }

        // 8.3 短名还原（在剥掉前缀之后做，否则混合形态会绕过）。
        foreach ((string candidate, _) in checks.ToArray())
        {
            string longForm = PathTools.StripExtendedPrefix(PathTools.LongPath(candidate));
            if (!string.Equals(PathTools.Normalize(longForm), PathTools.Normalize(candidate), StringComparison.Ordinal))
            {
                try
                {
                    checks.Add((longForm, Path.GetFullPath(longForm)));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // 还原后的形态无法绝对化：忽略这一候选即可。
                }
            }
        }

        foreach ((string candidate, string candidateAbsolute) in checks)
        {
            // 盘根判定：.NET 的 Path.GetDirectoryName("C:\") 返回 null（而不是自身），
            // 故必须把"没有上级"与"上级就是自己"都当作盘根，否则 C:\ 会被漏判为无风险。
            string? parent = Path.GetDirectoryName(candidateAbsolute);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidateAbsolute, StringComparison.OrdinalIgnoreCase))
            {
                return new SourceRiskResult(SourceRisk.DriveRoot, UiText.SourceDriveRoot(path));
            }

            foreach (string root in Catalog.ForbiddenSourceTrees())
            {
                if (PathTools.IsWithin(candidate, root))
                {
                    return new SourceRiskResult(
                        SourceRisk.SystemDirectory,
                        UiText.SourceInsideSystemDirectory(root));
                }
            }

            foreach (string root in Catalog.ForbiddenSourceExact())
            {
                // \Users 只做精确匹配：把资料目录放在 C:\Users\me\MyDocs 是合法状态。
                if (PathTools.SamePath(candidate, root))
                {
                    return new SourceRiskResult(
                        SourceRisk.SystemDirectory,
                        UiText.SourceIsSystemDirectory(root));
                }
            }
        }

        return new SourceRiskResult(SourceRisk.None, string.Empty);
    }

    /// <summary>
    /// 恢复路径专用：<see cref="CheckSourceTree"/> + 拒绝"源与默认目标互为祖先/后代"。
    /// </summary>
    /// <param name="sourcePath">当前位置。</param>
    /// <param name="defaultTarget">系统默认位置（已展开）。</param>
    /// <remarks>
    /// 恢复路径不能整体套用 <see cref="ValidateDestination"/>：系统默认目录位于
    /// <c>C:\Users\&lt;用户&gt;</c> 之下，对转移路径而言那是禁区。但"互为祖先/后代"这条
    /// 互斥检查绝不能因此丢掉——目标在源内会自我嵌套复制，源在目标内则删源会连带删掉默认目录。
    /// </remarks>
    public static GuardResult CheckRestoreSource(string sourcePath, string defaultTarget)
    {
        SourceRiskResult risk = CheckSourceTree(sourcePath);
        switch (risk.Risk)
        {
            case SourceRisk.Invalid:
                return GuardResult.Fail(UiText.RestoreRejectedInvalid(risk.Reason));
            case SourceRisk.DriveRoot:
                return GuardResult.Fail(UiText.RestoreRejectedDriveRoot(risk.Reason));
            case SourceRisk.SystemDirectory:
                return GuardResult.Fail(UiText.RestoreRejectedSystemDirectory(risk.Reason));
        }

        if (!string.IsNullOrEmpty(defaultTarget) && !PathTools.SamePath(sourcePath, defaultTarget))
        {
            if (PathTools.IsWithin(defaultTarget, sourcePath))
            {
                return GuardResult.Fail(UiText.RestoreTargetInsideSource(defaultTarget, sourcePath));
            }

            if (PathTools.IsWithin(sourcePath, defaultTarget))
            {
                return GuardResult.Fail(UiText.RestoreSourceInsideTarget(sourcePath, defaultTarget));
            }
        }

        return GuardResult.Pass;
    }

    // ================================================================ 内部判定

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
        };

        for (int i = 1; i <= 9; i++)
        {
            names.Add("COM" + i);
            names.Add("LPT" + i);
        }

        foreach (char digit in SuperscriptDigits)
        {
            names.Add("COM" + digit);
            names.Add("LPT" + digit);
        }

        return names;
    }

    private static void AddIfNew(List<string> values, string value)
    {
        if (value.Length > 0 && !values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    private static bool IsFullyQualified(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return Compat.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 返回首个非法字符；<c>:</c> 只在盘符位置合法。返回 <c>'\0'</c> 表示没有。
    /// </summary>
    private static char FirstIllegalCharacter(string candidate)
    {
        int start = candidate.Length >= 2 && candidate[1] == ':' ? 2 : 0;
        for (int i = start; i < candidate.Length; i++)
        {
            char ch = candidate[i];
            if (ch == ':' || Array.IndexOf(InvalidNameChars, ch) >= 0)
            {
                return ch;
            }
        }

        return '\0';
    }

    /// <summary>
    /// 返回路径中首个使用保留名或以点/空格结尾的片段（空串表示没有）。
    /// </summary>
    /// <remarks>
    /// 这些名字在校验时看似合法，真正创建时会被系统拒绝，导致"目标目录建不出来但已通过前置校验"。
    /// 正常的 <c>.</c>/<c>..</c> 语义必须放行，不得误伤。
    /// </remarks>
    private static string InvalidSegment(string path)
    {
        string cleaned = PathTools.StripExtendedPrefix(path);
        string root = Path.GetPathRoot(cleaned) ?? string.Empty;
        string tail = cleaned.Substring(Math.Min(root.Length, cleaned.Length));

        foreach (string segment in tail.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                continue;
            }

            int dot = segment.IndexOf('.');
            string stem = dot >= 0 ? segment.Substring(0, dot) : segment;
            if (ReservedNames.Contains(stem))
            {
                return segment;
            }

            char last = segment[segment.Length - 1];
            if (last == ' ' || last == '.')
            {
                return segment;
            }
        }

        return string.Empty;
    }
}
