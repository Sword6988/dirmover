using DirMover.Core.Engine;

namespace DirMover.Core.IO;

/// <summary>校验结论。</summary>
/// <param name="Verified">是否通过全部 7 个维度。</param>
/// <param name="Detail">未通过时的维度名与样例（可读中文）。</param>
/// <param name="Cancelled">校验过程被取消（不是"失败"，调用方不得据此删源）。</param>
public readonly record struct VerifyResult(bool Verified, string Detail, bool Cancelled);

/// <summary>原子移动结论。</summary>
/// <param name="Moved">是否已完成同卷原子改名。</param>
/// <param name="Message">未移动时的原因（必须说明"改用复制合并"）。</param>
public readonly record struct MoveResult(bool Moved, string Message);

/// <summary>通用操作结论。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Message">失败原因（成功时为空串）。</param>
public readonly record struct OpResult(bool Ok, string Message);

public static partial class FsOps
{
    private const int VerifySampleLimit = 8;

    /// <summary>
    /// 以<b>源</b>为基准做全量校验（7 个维度，缺一即不通过）。
    /// </summary>
    /// <param name="src">源目录（应仍在）。</param>
    /// <param name="dst">目标目录。</param>
    /// <param name="result">复制结果（携带源指纹，摘要只跟目标比）。</param>
    /// <param name="progress">进度出口（可空）。</param>
    /// <param name="ct">取消令牌（逐文件检查）。</param>
    /// <remarks>
    /// 7 个维度：①目标存在；②目标大小 == 源记录大小；③目标摘要 == 源摘要（复用项也带摘要）；
    /// ④源自清点以来未改动（大小 + mtime）；⑤重新枚举源后差集为空（文件/目录/链接分别比）；
    /// ⑥清单内目录与链接在目标侧齐备（链接按"条目存在"判，悬空链接不算失败）；
    /// ⑦没有"按名跳过却未复制"的项。
    /// <para>
    /// 方向性是红线：摘要<b>只</b>从 <paramref name="result"/> 里取（那是复制时读到的源摘要），
    /// 这里<b>不</b>重算源摘要。重算源摘要会让"源在复制期间被改写"这件事被判定为通过。
    /// </para>
    /// </remarks>
    public static VerifyResult VerifyCopy(
        string src,
        string dst,
        CopyResult result,
        ITransferProgress? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            return new VerifyResult(false, "源或目标路径为空", false);
        }

        if (!PathTools.Exists(dst))
        {
            return new VerifyResult(false, $"目标不存在：{dst}", false);
        }

        var problems = new List<string>();

        // ⑦ 按名跳过却未复制 → fail-closed
        if (result.Skipped.Count > 0)
        {
            problems.Add($"按名跳过 {result.Skipped.Count} 项未复制（样例：{Sample(result.Skipped)}）");
        }

        int total = result.Items.Count;
        int done = 0;
        long bytes = 0;

        foreach (CopyItem item in result.Items)
        {
            if (ct.IsCancellationRequested)
            {
                return new VerifyResult(false, "校验被取消（源未删除）", true);
            }

            string sourcePath = Path.Combine(src, item.Rel);
            string targetPath = Path.Combine(dst, item.TargetRel);

            try
            {
                // ① 目标存在
                if (!File.Exists(PathTools.ToExtended(targetPath)))
                {
                    problems.Add($"目标缺少文件 {item.TargetRel}");
                    continue;
                }

                // ② 大小
                long targetSize = new FileInfo(PathTools.ToExtended(targetPath)).Length;
                if (targetSize != item.Fingerprint.Size)
                {
                    problems.Add($"大小不一致 {item.TargetRel}（源 {item.Fingerprint.Size} / 目标 {targetSize}）");
                    continue;
                }

                // ③ 摘要（源摘要 ← 复制时读取；只跟目标比）
                string targetDigest = ComputeDigest(targetPath);
                if (!string.Equals(targetDigest, item.Fingerprint.Digest, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"摘要不一致 {item.TargetRel}");
                }

                // ④ 源自清点以来未改动
                var now = FingerprintOf(sourcePath, withDigest: false);
                if (now.Size != item.Fingerprint.Size || now.ModifiedTicksUtc != item.Fingerprint.ModifiedTicksUtc)
                {
                    problems.Add($"源已改动 {item.Rel}（大小或修改时间变了）");
                }
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                problems.Add($"校验读写失败 {item.Rel}：{ex.Message}");
            }

            done++;
            bytes += item.Fingerprint.Size;
            progress?.Report(new TransferProgressInfo(
                string.Empty,
                string.Empty,
                done,
                total,
                bytes,
                item.Rel));

            if (problems.Count >= VerifySampleLimit)
            {
                break;
            }
        }

        if (problems.Count < VerifySampleLimit)
        {
            CompareSets(src, dst, result, problems);
        }

        if (problems.Count == 0)
        {
            return new VerifyResult(true, string.Empty, false);
        }

        return new VerifyResult(false, string.Join("；", problems.Take(VerifySampleLimit)), false);
    }

    /// <summary>
    /// 同卷且目标不存在时做原子改名（路径 A）。
    /// </summary>
    /// <param name="src">源目录。</param>
    /// <param name="dst">目标目录（必须不存在）。</param>
    /// <remarks>
    /// 必须<b>先判卷再动手</b>：<c>Directory.Move</c> 跨卷会失败，而失败时目录状态依实现而异，
    /// "先尝试再回退"会让源目录处于不可预期状态。这里先判卷，跨卷直接返回
    /// <c>Moved=false</c> 并说明"改用复制合并"，调用方自然会走安全通道。
    /// </remarks>
    public static MoveResult TryAtomicMove(string src, string dst)
    {
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            return new MoveResult(false, "源或目标路径为空，改用复制合并");
        }

        if (PathTools.ResolvesToSame(src, dst))
        {
            return new MoveResult(false, UiText.GuardSamePhysical(dst, src));
        }

        if (PathTools.Exists(dst))
        {
            return new MoveResult(false, "目标已存在，改用复制合并");
        }

        string sourceVolume = PathTools.VolumeKey(src);
        string targetVolume = PathTools.VolumeKey(dst);
        if (sourceVolume.Length > 0 &&
            targetVolume.Length > 0 &&
            !string.Equals(sourceVolume, targetVolume, StringComparison.Ordinal))
        {
            return new MoveResult(false, "源与目标不在同一卷，无法原子改名，改用复制合并");
        }

        try
        {
            Directory.Move(PathTools.ToExtended(PathTools.StripExtendedPrefix(src)), PathTools.ToExtended(dst));
            return new MoveResult(true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new MoveResult(false, $"原子改名失败（{ex.Message}），改用复制合并");
        }
    }

    /// <summary>创建目录（已存在视为成功）。</summary>
    /// <param name="path">目录路径。</param>
    public static OpResult EnsureDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new OpResult(false, "目录路径为空");
        }

        try
        {
            Directory.CreateDirectory(PathTools.ToExtended(path));
            return new OpResult(true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return new OpResult(false, $"创建目录失败 {path}：{ex.Message}");
        }
    }

    /// <summary>
    /// 安全删除整个目录树（只读属性先清、占用型错误退避重试 3 次、累计 &lt; 1 s）。
    /// </summary>
    /// <param name="path">目录路径（不存在视为成功）。</param>
    /// <remarks>
    /// 这个方法是整个程序里<b>唯一</b>会整树删除的入口，只允许在
    /// <see cref="VerifyCopy"/> 通过且删源前两道闸门都放行之后调用。
    /// 失败文案必须包含"重启本机后即可删除"——占用型失败的唯一可靠出路就是重启。
    /// </remarks>
    public static OpResult SafeRemoveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new OpResult(false, UiText.RemoveDirectoryFailed("路径为空", string.Empty));
        }

        if (!Directory.Exists(PathTools.ToExtended(path)))
        {
            return new OpResult(true, string.Empty);
        }

        string lastError = string.Empty;
        for (int attempt = 1; attempt <= Catalog.RemoveAttempts; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(PathTools.ToExtended(PathTools.StripExtendedPrefix(path)), recursive: true);
                return new OpResult(true, string.Empty);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                lastError = ex.Message;
                if (attempt >= Catalog.RemoveAttempts)
                {
                    break;
                }

                Thread.Sleep(attempt == 1 ? Catalog.RemoveBackoff1 : Catalog.RemoveBackoff2);
            }
        }

        return new OpResult(false, UiText.RemoveDirectoryFailed(lastError, path));
    }

    // ================================================================ 内部

    private static void CompareSets(string src, string dst, CopyResult result, List<string> problems)
    {
        SourceTree fresh;
        try
        {
            fresh = EnumerateSource(src);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add($"重新清点源失败：{ex.Message}");
            return;
        }

        if (fresh.Errors.Count > 0)
        {
            problems.Add($"重新清点源时出现读取失败（{fresh.Errors.Count} 项，样例：{Sample(fresh.Errors)}）");
        }

        // ⑤ 源集合 − 清单集合 必须为空（新增项说明复制期间源被写入，目标缺内容）
        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CopyItem item in result.Items)
        {
            manifestFiles.Add(item.Rel);
        }

        var manifestDirs = new HashSet<string>(result.Dirs, StringComparer.OrdinalIgnoreCase);
        var manifestLinks = new HashSet<string>(result.Links, StringComparer.OrdinalIgnoreCase);

        var addedFiles = fresh.Files.Keys
            .Where(freshFile => !manifestFiles.Contains(freshFile))
            .ToList();
        if (addedFiles.Count > 0)
        {
            problems.Add($"源侧多出 {addedFiles.Count} 个文件未复制（样例：{Sample(addedFiles)}）");
        }

        var addedDirs = fresh.Dirs.Where(freshDir => !manifestDirs.Contains(freshDir)).ToList();
        if (addedDirs.Count > 0)
        {
            problems.Add($"源侧多出 {addedDirs.Count} 个目录未创建（样例：{Sample(addedDirs)}）");
        }

        var addedLinks = fresh.Links.Where(freshLink => !manifestLinks.Contains(freshLink)).ToList();
        if (addedLinks.Count > 0)
        {
            problems.Add($"源侧多出 {addedLinks.Count} 个链接未重建（样例：{Sample(addedLinks)}）");
        }

        // 清单里记录过、但源侧已经消失的项：也属于"源已变化"，同样不通过。
        var removed = manifestFiles.Where(rel => !fresh.Files.ContainsKey(rel)).ToList();
        if (removed.Count > 0)
        {
            problems.Add($"源侧少了 {removed.Count} 个清单内文件（样例：{Sample(removed)}）");
        }

        // ⑥ 清单内目录与链接在目标侧齐备（链接按"条目存在"判：悬空链接也算已重建）
        var missingDirs = new List<string>();
        foreach (string rel in result.Dirs)
        {
            if (!Directory.Exists(PathTools.ToExtended(Path.Combine(dst, rel))))
            {
                missingDirs.Add(rel);
            }
        }

        if (missingDirs.Count > 0)
        {
            problems.Add($"目标缺少 {missingDirs.Count} 个目录（样例：{Sample(missingDirs)}）");
        }

        var missingLinks = result.Links
            .Where(rel => !PathTools.Exists(Path.Combine(dst, rel)))
            .ToList();
        if (missingLinks.Count > 0)
        {
            problems.Add($"目标缺少 {missingLinks.Count} 个链接（样例：{Sample(missingLinks)}）");
        }
    }

    private static string Sample(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        int take = Math.Min(3, items.Count);
        var parts = new List<string>(take);
        for (int index = 0; index < take; index++)
        {
            parts.Add(items[index]);
        }

        return string.Join("、", parts);
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        try
        {
            var directory = new DirectoryInfo(PathTools.ToExtended(PathTools.StripExtendedPrefix(root)));
            if (!directory.Exists)
            {
                return;
            }

            // 目录自身的只读位（Windows 上对目录无意义）不清；文件必须清，否则 Delete 抛 UnauthorizedAccessException。
            foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件清不掉：交给随后的 Delete 去暴露（错误会带路径上报）。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 枚举都失败时直接尝试 Delete：让它给出更准确的原因。
        }
    }
}
