using System.Security.Cryptography;
using DirMover.Core.Engine;
using DirMover.Core.Interop;

namespace DirMover.Core.IO;

/// <summary>一个已落到目标侧的文件（或复用项）。</summary>
/// <remarks>
/// <see cref="Fingerprint"/> 是<b>源</b>的指纹（<see cref="FileFingerprint.Digest"/> 在复制时
/// 边读源边算）。<see cref="TargetRel"/> 只在"同名冲突另存"时与 <see cref="Rel"/> 不同。
/// </remarks>
public sealed class CopyItem
{
    /// <summary>源相对路径。</summary>
    public required string Rel { get; init; }

    /// <summary>目标相对路径（同名冲突时形如 <c>a (1).txt</c>）。</summary>
    public required string TargetRel { get; init; }

    /// <summary>源指纹（含摘要）。</summary>
    public required FileFingerprint Fingerprint { get; init; }

    /// <summary>目标已存在且内容与源一致 → 复用，未重新写入。</summary>
    public bool Reused { get; init; }
}

/// <summary>
/// 复制合并结果。<b>本类型不含任何删除语义</b>——删源是调用方的决定，且必须在
/// <see cref="FsOps.VerifyCopy"/> 通过之后。
/// </summary>
public sealed class CopyResult
{
    /// <summary>已落盘的文件项（顺序即处理顺序）。</summary>
    public List<CopyItem> Items { get; } = new();

    /// <summary>已创建的子目录相对路径（含空目录）。</summary>
    public List<string> Dirs { get; } = new();

    /// <summary>已重建的链接相对路径。</summary>
    public List<string> Links { get; } = new();

    /// <summary>同名不同内容的冲突记录，形如 <c>a.txt → a (1).txt</c>。</summary>
    public List<string> Conflicts { get; } = new();

    /// <summary>按名跳过的条目（非空 → 校验必须失败，fail-closed）。</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>复制失败项。</summary>
    public List<string> Errors { get; } = new();

    /// <summary>是否被取消（取消后目标侧不留 <c>.part</c> 残留）。</summary>
    public bool Cancelled { get; set; }

    /// <summary>按洞复制（稀疏）的文件数。</summary>
    public int SparseFiles { get; set; }

    /// <summary>以硬链接方式重建的文件数。</summary>
    public int HardlinkedFiles { get; set; }

    /// <summary>实际写入的文件数（不含复用）。</summary>
    public int CopiedFiles => Items.Count(static i => !i.Reused);

    /// <summary>实际写入的字节数（不含复用）。</summary>
    public long CopiedBytes
    {
        get
        {
            long sum = 0;
            foreach (CopyItem item in Items)
            {
                if (!item.Reused)
                {
                    sum += item.Fingerprint.Size;
                }
            }

            return sum;
        }
    }

    /// <summary>无错误且未被取消。</summary>
    public bool Ok => Errors.Count == 0 && !Cancelled;
}

public static partial class FsOps
{
    /// <summary>
    /// 把 <paramref name="src"/> 的内容合并复制到 <paramref name="dst"/>（<b>绝不删除任何东西</b>）。
    /// </summary>
    /// <param name="src">源目录。</param>
    /// <param name="dst">目标目录（不存在则创建）。</param>
    /// <param name="progress">进度出口（可空）。</param>
    /// <param name="ct">取消令牌：项边界、每 1 MiB 分块、以及每个文件前都会检查。</param>
    /// <param name="skipNames">按条目名跳过的名字。</param>
    /// <remarks>
    /// 安全口径（逐条对应红线）：
    /// ① 源与目标同物理位置 → 直接记错误并返回，<b>不做任何写操作</b>；
    /// ② 目标已存在同名文件时<b>先比内容</b>（大小 + SHA256），一致才复用，不一致则另存
    ///    <c>name (1).ext</c> 并记入 <see cref="CopyResult.Conflicts"/>——绝不静默覆盖；
    /// ③ 每个文件先写 <c>.part</c> 临时名，成功后用 <c>File.Move(..., overwrite: false)</c>
    ///    原子落盘；任何异常/取消都删除临时文件，<b>不残留</b>；
    /// ④ 稀疏文件按洞复制（全零块用 Seek 跳过的同时先 SetLength），硬链接按 (卷序列号, 文件索引)
    ///    重连，避免把一份 4 GB 的虚拟机磁盘复制成两份。
    /// </remarks>
    public static CopyResult CopyMerge(
        string src,
        string dst,
        ITransferProgress? progress = null,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? skipNames = null)
    {
        var result = new CopyResult();

        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            result.Errors.Add("源或目标路径为空");
            return result;
        }

        if (PathTools.SamePath(src, dst) || PathTools.ResolvesToSame(src, dst))
        {
            // 必须"零写操作"：同物理位置下的复制会把源自身写乱（DST == SRC 时就地复制）。
            result.Errors.Add(UiText.GuardSamePhysical(dst, src));
            return result;
        }

        if (!PathTools.Exists(src))
        {
            result.Errors.Add(UiText.CurrentMissing(src));
            return result;
        }

        // 第三道重解析点闸：复制内部再查一次（规划期与执行期之间可达分钟级时间窗）。
        string linkAncestor = DestinationGuard.ReparseAncestor(dst);
        if (linkAncestor.Length > 0)
        {
            result.Errors.Add(UiText.TargetIsLink(linkAncestor));
            return result;
        }

        SourceTree tree = EnumerateSource(src, skipNames);
        result.Skipped.AddRange(tree.Skipped);
        if (tree.Errors.Count > 0)
        {
            // 清单不完整 → 整体失败。带着不完整的清单去复制，等于让漏掉的部分随后被删掉。
            result.Errors.AddRange(tree.Errors);
            return result;
        }

        OpResult created = EnsureDirectory(dst);
        if (!created.Ok)
        {
            result.Errors.Add(created.Message);
            return result;
        }

        // ---- 目录结构（含空目录）
        var dirs = new List<string>(tree.Dirs);
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string rel in dirs)
        {
            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }

            OpResult ensured = EnsureDirectory(Path.Combine(dst, rel));
            if (!ensured.Ok)
            {
                result.Errors.Add(ensured.Message);
                continue;
            }

            result.Dirs.Add(rel);
        }

        // ---- 链接（用"条目存在"重建：悬空链接也要重建，否则校验维度 ⑥ 会恒失败）
        var links = new List<string>(tree.Links);
        links.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string rel in links)
        {
            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }

            try
            {
                RecreateLink(Path.Combine(src, rel), Path.Combine(dst, rel));
                result.Links.Add(rel);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                result.Errors.Add($"重建链接失败 {rel}：{ex.Message}");
            }
        }

        // ---- 文件
        var rels = new List<string>(tree.Files.Keys);
        rels.Sort(StringComparer.OrdinalIgnoreCase);
        int total = rels.Count;
        int done = 0;
        long bytesDone = 0;
        var hardlinkAnchors = new Dictionary<(uint Device, ulong Inode), CopyItem>();

        foreach (string rel in rels)
        {
            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }

            FileFingerprint fingerprint = tree.Files[rel];
            string sourcePath = Path.Combine(src, rel);
            string targetPath = Path.Combine(dst, rel);
            string targetRel = rel;

            try
            {
                // 同 (卷, 索引) 的第二次出现：直接建硬链接，不重复写数据。
                if (fingerprint.LinkCount > 1 &&
                    fingerprint.Inode != 0 &&
                    hardlinkAnchors.TryGetValue((fingerprint.Device, fingerprint.Inode), out CopyItem? anchor))
                {
                    string? hardlinkParent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(hardlinkParent))
                    {
                        Directory.CreateDirectory(PathTools.ToExtended(hardlinkParent));
                    }

                    string anchorPath = Path.Combine(dst, anchor.TargetRel);
                    if (!NativeMethods.CreateHardLinkW(
                            PathTools.ToExtended(targetPath),
                            PathTools.ToExtended(anchorPath),
                            IntPtr.Zero))
                    {
                        throw new IOException("创建硬链接失败（目标卷可能不支持硬链接）");
                    }

                    // 与 CopySingleFile 一致：长路径必须带 \\?\ 前缀（见 PathTools.ToExtended 的约定）。
                    File.SetLastWriteTimeUtc(
                        PathTools.ToExtended(targetPath),
                        new DateTime(fingerprint.ModifiedTicksUtc, DateTimeKind.Utc));
                    result.HardlinkedFiles++;
                    result.Items.Add(new CopyItem
                    {
                        Rel = rel,
                        TargetRel = targetRel,
                        Fingerprint = new FileFingerprint
                        {
                            Size = fingerprint.Size,
                            ModifiedTicksUtc = fingerprint.ModifiedTicksUtc,
                            Digest = anchor.Fingerprint.Digest,
                            Device = fingerprint.Device,
                            Inode = fingerprint.Inode,
                            LinkCount = fingerprint.LinkCount,
                            Attributes = fingerprint.Attributes,
                        },
                    });

                    done++;
                    bytesDone += fingerprint.Size;
                    ReportProgress(progress, done, total, bytesDone, rel);
                    continue;
                }

                if (PathTools.Exists(targetPath))
                {
                    CopyItem? reused = TryReuse(rel, sourcePath, targetPath, fingerprint);
                    if (reused is not null)
                    {
                        result.Items.Add(reused);
                        done++;
                        bytesDone += fingerprint.Size;
                        ReportProgress(progress, done, total, bytesDone, rel);
                        continue;
                    }

                    targetRel = NextConflictRel(dst, rel, out targetPath);
                    result.Conflicts.Add($"{rel} → {targetRel}");
                }

                EnsureParentDirectory(targetPath);

                string digest = CopySingleFile(
                    sourcePath,
                    targetPath,
                    fingerprint,
                    result,
                    onProgress: delta =>
                    {
                        bytesDone += delta;
                        ReportProgress(progress, done, total, bytesDone, rel);
                    },
                    ct);

                var item = new CopyItem
                {
                    Rel = rel,
                    TargetRel = targetRel,
                    Fingerprint = new FileFingerprint
                    {
                        Size = fingerprint.Size,
                        ModifiedTicksUtc = fingerprint.ModifiedTicksUtc,
                        Digest = digest,
                        Device = fingerprint.Device,
                        Inode = fingerprint.Inode,
                        LinkCount = fingerprint.LinkCount,
                        Attributes = fingerprint.Attributes,
                    },
                };

                result.Items.Add(item);
                if (fingerprint.LinkCount > 1 && fingerprint.Inode != 0)
                {
                    hardlinkAnchors[(fingerprint.Device, fingerprint.Inode)] = item;
                }

                done++;
                ReportProgress(progress, done, total, bytesDone, rel);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                return result;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                // 单项失败不中断整批：引擎会因 Errors 非空而拒绝删源，用户可以逐条看明细。
                result.Errors.Add($"{rel}：{ex.Message}");
            }
        }

        // 最后一个文件之后一定再报一次，避免"卡在 n-1/N"的界面。
        ReportProgress(progress, done, total, bytesDone, rels.Count > 0 ? rels[rels.Count - 1] : string.Empty);
        return result;
    }

    // ================================================================ 内部

    private static void ReportProgress(
        ITransferProgress? progress,
        int done,
        int total,
        long bytes,
        string currentRelativePath)
    {
        progress?.Report(new TransferProgressInfo(
            string.Empty,
            string.Empty,
            done,
            total,
            bytes,
            currentRelativePath));
    }

    private static void EnsureParentDirectory(string targetPath)
    {
        string? parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            OpResult ensured = EnsureDirectory(parent);
            if (!ensured.Ok)
            {
                throw new IOException(ensured.Message);
            }
        }
    }

    /// <summary>
    /// 目标已存在同名文件时"先比内容"：大小相同且 SHA256 相同才复用。
    /// </summary>
    /// <remarks>
    /// 只比 size + mtime 是不够的：迁移场景里很常见"上一次复制中途中断，目标留下一个
    /// 大小与时间都对得上、内容却残缺"的文件，按元数据复用等于把残缺副本当成已转移。
    /// </remarks>
    private static CopyItem? TryReuse(
        string rel,
        string sourcePath,
        string targetPath,
        FileFingerprint fingerprint)
    {
        if (Directory.Exists(PathTools.ToExtended(targetPath)))
        {
            // 源是文件、目标同名项却是目录：类型冲突，必须另存。
            return null;
        }

        FileFingerprint existing;
        try
        {
            existing = FingerprintOf(targetPath, withDigest: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (existing.Size != fingerprint.Size)
        {
            return null;
        }

        string sourceDigest = ComputeDigest(sourcePath);
        if (!string.Equals(sourceDigest, existing.Digest, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new CopyItem
        {
            Rel = rel,
            TargetRel = rel,
            Fingerprint = new FileFingerprint
            {
                Size = fingerprint.Size,
                ModifiedTicksUtc = fingerprint.ModifiedTicksUtc,
                Digest = sourceDigest,
                Device = fingerprint.Device,
                Inode = fingerprint.Inode,
                LinkCount = fingerprint.LinkCount,
                Attributes = fingerprint.Attributes,
            },
            Reused = true,
        };
    }

    /// <summary>找下一个可用的冲突名：<c>a.txt</c> → <c>a (1).txt</c>。</summary>
    private static string NextConflictRel(string dst, string rel, out string absolutePath)
    {
        string? directory = Path.GetDirectoryName(rel);
        string name = Path.GetFileName(rel);
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);

        for (int index = 1; index < 10000; index++)
        {
            string candidateName = $"{stem} ({index}){extension}";
            string candidateRel = string.IsNullOrEmpty(directory) ? candidateName : directory + "\\" + candidateName;
            string candidateAbsolute = Path.Combine(dst, candidateRel);
            if (!PathTools.Exists(candidateAbsolute))
            {
                absolutePath = candidateAbsolute;
                return candidateRel;
            }
        }

        throw new IOException($"同名冲突过多，无法为 {rel} 找到可用名字");
    }

    /// <summary>
    /// 复制单个文件：<c>.part</c> + 边读边算摘要 + 稀疏按洞 + 原子改名。
    /// </summary>
    /// <returns>源文件的 SHA256（十六进制小写）。</returns>
    private static string CopySingleFile(
        string sourcePath,
        string targetPath,
        FileFingerprint fingerprint,
        CopyResult result,
        Action<long> onProgress,
        CancellationToken ct)
    {
        string tempPath = targetPath + ".part";
        bool sparse = (fingerprint.Attributes & FileAttributes.SparseFile) != 0;

        // 复用池化缓冲区：每文件 new 一个 1 MiB 数组会全部落到大对象堆（LOH），
        // 几万个小文件的树上 GC / LOH 碎片压力显著；池化后同一次复制全程复用同一块内存。
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyChunkSize);
        try
        {
            if (File.Exists(PathTools.ToExtended(tempPath)))
            {
                File.Delete(PathTools.ToExtended(tempPath));
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            using (var input = new FileStream(
                       PathTools.ToExtended(sourcePath),
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       CopyChunkSize,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       PathTools.ToExtended(tempPath),
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       CopyChunkSize))
            {
                if (sparse)
                {
                    MarkSparse(output);
                }

                long length = input.Length;
                int read;
                while ((read = input.Read(buffer, 0, CopyChunkSize)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);

                    if (sparse && IsAllZero(buffer, read))
                    {
                        // 洞：跳过不写（随后 SetLength 把文件尾部补齐）。
                        output.Seek(read, SeekOrigin.Current);
                    }
                    else
                    {
                        output.Write(buffer, 0, read);
                    }

                    onProgress(read);
                }

                if (sparse)
                {
                    output.SetLength(length);
                }
            }

            // 保留源时间戳：校验维度 ④（源自清点以来未改动）与"复用比内容"都依赖 mtime。
            File.SetLastWriteTimeUtc(PathTools.ToExtended(tempPath), new DateTime(fingerprint.ModifiedTicksUtc, DateTimeKind.Utc));

            if (sparse)
            {
                result.SparseFiles++;
            }

            // 原子落盘：overwrite:false —— 目标若在竞态中被他人创建，这里失败而不是覆盖。
            Compat.MoveNoOverwrite(PathTools.ToExtended(tempPath), PathTools.ToExtended(targetPath));
            return Compat.HexUpper(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            // 任何异常 / 取消都必须删掉临时文件，绝不留下 .part 残留。
            TryDeleteFile(tempPath);
            throw;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void MarkSparse(FileStream stream)
    {
        if (!Compat.IsWindows())
        {
            return;
        }

        NativeMethods.DeviceIoControl(
            stream.SafeFileHandle.DangerousGetHandle(),
            NativeMethods.FsctlSetSparse,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }

    private static bool IsAllZero(byte[] buffer, int length)
    {
        for (int index = 0; index < length; index++)
        {
            if (buffer[index] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(PathTools.ToExtended(path)))
            {
                File.SetAttributes(PathTools.ToExtended(path), FileAttributes.Normal);
                File.Delete(PathTools.ToExtended(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 删临时文件失败不改变结论（取消/失败已经上报），但不能因此掩盖原始异常。
        }
    }

    /// <summary>按原样在目标侧重建立链接（<b>悬空链接也重建</b>）。</summary>
    private static void RecreateLink(string sourceLinkPath, string targetLinkPath)
    {
        string source = PathTools.StripExtendedPrefix(sourceLinkPath);
        bool isDirectory = Directory.Exists(PathTools.ToExtended(source));
        FileSystemInfo info = isDirectory ? new DirectoryInfo(source) : new FileInfo(source);

        string? rawTarget = Compat.GetLinkTarget(info);
        if (rawTarget is null || rawTarget.Length == 0)
        {
            throw new IOException("源条目已不是链接");
        }

        // 不用 string.IsNullOrEmpty 收窄：net48 引用程序集没有 NotNullWhen 注解，
        // 同一段代码在 net8 编译干净、在 net48 会报 CS8600/8604（显式判空对两个目标都成立）。
        string resolved = rawTarget;
        if (!Path.IsPathRooted(resolved))
        {
            string? parent = Path.GetDirectoryName(source);
            resolved = Path.GetFullPath(Path.Combine(parent ?? string.Empty, resolved));
        }

        string? parentDir = Path.GetDirectoryName(targetLinkPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(PathTools.ToExtended(parentDir));
        }

        if (PathTools.Exists(targetLinkPath))
        {
            // 目标侧已有同名条目：不覆盖（可能是上一次留下的链接）。
            return;
        }

        if (isDirectory)
        {
            Compat.CreateSymbolicLink(PathTools.ToExtended(targetLinkPath), resolved, isDirectory: true);
        }
        else
        {
            Compat.CreateSymbolicLink(PathTools.ToExtended(targetLinkPath), resolved, isDirectory: false);
        }
    }
}
