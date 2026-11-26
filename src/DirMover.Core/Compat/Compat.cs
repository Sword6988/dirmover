using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DirMover.Core.Interop;

namespace DirMover.Core;

/// <summary>
/// 跨目标（net8 / net48）的 BCL 差异出口：同一个方法名，两个目标各自取平台自带实现。
/// </summary>
/// <remarks>
/// 与 <c>Net48Shims</c> 的分工：那里遮蔽的是"调用点不改一个字"的静态成员（ArgumentNullException /
/// HashCode）；这里遮蔽的是<b>参数不同、语义可能不同</b>的 API——这种必须让调用点显式写成
/// <c>Compat.Xxx(...)</c>，读代码的人才会意识到"这里跨了两个运行时"。
/// </remarks>
internal static class Compat
{
    /// <summary>字节数组 → 大写十六进制（等价 <c>Convert.ToHexString</c>）。</summary>
    public static string HexUpper(byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

#if NET48
        var chars = new char[bytes.Length * 2];
        const string Digits = "0123456789ABCDEF";
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = Digits[bytes[i] >> 4];
            chars[(i * 2) + 1] = Digits[bytes[i] & 0xF];
        }

        return new string(chars);
#else
        return Convert.ToHexString(bytes);
#endif
    }

    /// <summary>是否运行在 Windows 上（net48 恒为 true）。</summary>
    public static bool IsWindows()
    {
#if NET48
        return true;
#else
        return OperatingSystem.IsWindows();
#endif
    }

    /// <summary>当前可执行文件路径（等价 <c>Environment.ProcessPath</c>）。</summary>
    public static string? ProcessPath()
    {
#if NET48
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            // 权限不足时 MainModule 会抛；退回入口程序集路径。
            return System.Reflection.Assembly.GetEntryAssembly()?.Location;
        }
#else
        return Environment.ProcessPath;
#endif
    }

    /// <summary>当前进程 ID（等价 <c>Environment.ProcessId</c>）。</summary>
    public static int ProcessId()
    {
#if NET48
        using var process = Process.GetCurrentProcess();
        return process.Id;
#else
        return Environment.ProcessId;
#endif
    }

    /// <summary>
    /// 单测 / 特殊宿主注入的会话号；<c>null</c> = 取真实 <c>Process.SessionId</c>。
    /// 与 <c>ProcessId</c> 不同，这里必须可注入：CI 服务常跑在会话 0，
    /// 不注入就无法确定性地验证"按会话收窄"这条契约。
    /// </summary>
    internal static int? SessionIdOverride { get; set; }

    /// <summary>
    /// 当前进程所在的终端服务会话 ID（net8 / net48 的 <c>Process.SessionId</c> 语义一致）。
    /// 取不到时返回 0，调用方据此 fail-closed，不会发出无法按会话收窄的结束命令。
    /// </summary>
    public static int SessionId()
    {
        if (SessionIdOverride.HasValue)
        {
            return SessionIdOverride.Value;
        }

        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>移动文件且不允许覆盖目标（等价 <c>File.Move(src, dst, overwrite: false)</c>）。</summary>
    /// <remarks>两个运行时上"目标存在即失败"的语义本来一致，这里只是把调用点收敛到一处。</remarks>
    public static void MoveNoOverwrite(string source, string destination)
    {
#if NET48
        File.Move(source, destination);
#else
        File.Move(source, destination, overwrite: false);
#endif
    }

    /// <summary>移动并覆盖（等价 <c>File.Move(src, dst, overwrite: true)</c>）。</summary>
    /// <remarks>
    /// net48 只能先删后移，两步之间留了窗口期；调用点只用于"临时文件 → 正式名"，
    /// 中途崩溃时上层会重新生成临时文件，不会读到半个内容。
    /// </remarks>
    public static void MoveOrReplace(string source, string destination)
    {
#if NET48
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(source, destination);
#else
        File.Move(source, destination, overwrite: true);
#endif
    }

    /// <summary>结束进程（net8 走整树，net48 只结束主进程）。</summary>
    public static void KillTree(Process process)
    {
#if NET48
        process.Kill();
#else
        process.Kill(entireProcessTree: true);
#endif
    }

    /// <summary>路径是否完全限定（不依赖当前目录；等价 <c>Path.IsPathFullyQualified</c>）。</summary>
    public static bool IsPathFullyQualified(string path)
    {
#if NET48
        // 完全限定 = 有盘符且盘符后紧跟分隔符，或 UNC 路径；"C:foo" 与 "\foo" 都依赖当前目录。
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        if (path.Length >= 2 && path[1] == ':')
        {
            return path.Length > 2 && (path[2] == '\\' || path[2] == '/');
        }

        return path[0] == '\\' && path.Length > 1 && (path[1] == '\\' || path[1] == '/')
            ? true
            : path[0] == '/';
#else
        return Path.IsPathFullyQualified(path);
#endif
    }

    /// <summary>按"是否包含子串"比较（等价 <c>string.Contains(value, comparison)</c>）。</summary>
    public static bool Contains(string text, string value, StringComparison comparison)
    {
#if NET48
        return text.IndexOf(value, comparison) >= 0;
#else
        return text.Contains(value, comparison);
#endif
    }

    /// <summary>向 <see cref="ProcessStartInfo"/> 追加一个参数（自动处理引号转义）。</summary>
    public static void AddArgument(ProcessStartInfo startInfo, string argument)
    {
#if NET48
        startInfo.Arguments = startInfo.Arguments.Length == 0
            ? QuoteArgument(argument)
            : startInfo.Arguments + " " + QuoteArgument(argument);
#else
        startInfo.ArgumentList.Add(argument);
#endif
    }

    /// <summary>取链接条目的原始目标路径；不是链接时返回 null（等价 <c>FileSystemInfo.LinkTarget</c>）。</summary>
    public static string? GetLinkTarget(FileSystemInfo info)
    {
#if NET48
        return ReparsePoint.ReadTarget(info.FullName);
#else
        return info.LinkTarget;
#endif
    }

    /// <summary>解析到最终目标（等价 <c>FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName</c>）。</summary>
    public static string? ResolveFinalLinkTarget(FileSystemInfo info)
    {
#if NET48
        return ReparsePoint.ResolveFinalTarget(info.FullName);
#else
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
#endif
    }

    /// <summary>创建符号链接（等价 <c>File.CreateSymbolicLink</c> / <c>Directory.CreateSymbolicLink</c>）。</summary>
    public static void CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
#if NET48
        uint flags = NativeMethods.SymbolicLinkFlagAllowUnprivilegedCreate;
        if (isDirectory)
        {
            flags |= NativeMethods.SymbolicLinkFlagDirectory;
        }

        if (!NativeMethods.CreateSymbolicLinkW(linkPath, targetPath, flags))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException($"创建符号链接失败（WinError {error}）：{linkPath}");
        }
#else
        if (isDirectory)
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        else
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
#endif
    }

#if NET48
    /// <summary>
    /// 按 MSVC 运行库规则给命令行参数加引号（net48 只有 <c>Arguments</c> 字符串，没有 <c>ArgumentList</c>）。
    /// </summary>
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        for (int i = 0; i < argument.Length; i++)
        {
            int backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// net48 下的重解析点读取：<c>DeviceIoControl(FSCTL_GET_REPARSE_POINT)</c> + 手工解析
    /// <c>REPARSE_DATA_BUFFER</c>。
    /// </summary>
    /// <remarks>
    /// 为什么不用 <c>File.GetAttributes &amp; ReparsePoint</c> 了事：属性位只回答"是不是链接"，
    /// 回答不了"指向哪里"。复制阶段要靠真实目标重建链接（含悬空链接），所以必须读到目标字符串。
    /// </remarks>
    private static class ReparsePoint
    {
        private const int MaxReparseBuffer = 16 * 1024;
        private const int MaxResolveDepth = 32;

        /// <summary>读单个条目的重解析目标；不是链接/联接时返回 null。</summary>
        public static string? ReadTarget(string path)
        {
            IntPtr handle = NativeMethods.CreateFileW(
                path,
                0,
                NativeMethods.FileShareAll,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle == new IntPtr(-1))
            {
                return null;
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(MaxReparseBuffer);
                bool ok = NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlGetReparsePoint,
                    IntPtr.Zero,
                    0,
                    buffer,
                    MaxReparseBuffer,
                    out uint returned,
                    IntPtr.Zero);

                if (!ok || returned < 16)
                {
                    return null;
                }

                uint tag = (uint)Marshal.ReadInt32(buffer, 0);
                int unionOffset = 8;
                int pathBufferOffset;
                if (tag == NativeMethods.IOReparseTagSymlink)
                {
                    // SymbolicLinkReparseBuffer 比 MountPoint 多一个 Flags(ULONG)，路径缓冲区因此后移 4 字节。
                    pathBufferOffset = 20;
                }
                else if (tag == NativeMethods.IOReparseTagMountPoint)
                {
                    pathBufferOffset = 16;
                }
                else
                {
                    return null;
                }

                ushort substituteOffset = (ushort)Marshal.ReadInt16(buffer, unionOffset);
                ushort substituteLength = (ushort)Marshal.ReadInt16(buffer, unionOffset + 2);
                ushort printOffset = (ushort)Marshal.ReadInt16(buffer, unionOffset + 4);
                ushort printLength = (ushort)Marshal.ReadInt16(buffer, unionOffset + 6);

                int offset = pathBufferOffset + (printLength > 0 ? printOffset : substituteOffset);
                int length = printLength > 0 ? printLength : substituteLength;
                if (length <= 0 || offset + length > returned)
                {
                    return null;
                }

                var chars = new char[length / 2];
                Marshal.Copy(new IntPtr(buffer.ToInt64() + offset), chars, 0, chars.Length);
                string target = new string(chars);

                // 符号链接的目标通常带 \??\ 前缀（NT 对象管理器命名空间），去掉才是可用路径。
                const string NtPrefix = @"\??\";
                if (target.StartsWith(NtPrefix, StringComparison.Ordinal))
                {
                    target = target.Substring(NtPrefix.Length);
                }

                return target;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                NativeMethods.CloseHandle(handle);
            }
        }

        /// <summary>顺着链接一路解析到最终目标；不是链接时返回 null。</summary>
        public static string? ResolveFinalTarget(string path)
        {
            string? target = ReadTarget(path);
            if (target is null)
            {
                return null;
            }

            string current = path;
            for (int depth = 0; depth < MaxResolveDepth; depth++)
            {
                string resolved = Path.IsPathRooted(target)
                    ? target
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, target));

                bool isDirectory = Directory.Exists(resolved);
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(resolved) : new FileInfo(resolved);
                string? next = ReadTarget(info.FullName);
                current = resolved;
                if (next is null)
                {
                    return current;
                }

                target = next;
            }

            // 环状链接：返回最后落点，由调用方的"是否还在链接上"判断决定后续处理。
            return current;
        }
    }
#endif
}
