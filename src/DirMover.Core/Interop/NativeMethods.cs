using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

// net48 的 System.Runtime.InteropServices 里也有一个 FILETIME（与 ComTypes 的同名结构布局一致），
// 两个 using 同时在场就会 CS0104 歧义。显式取 ComTypes 那个，net8 下同样存在，语义不变。
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace DirMover.Core.Interop;

/// <summary>
/// 全部 Win32 P/Invoke 声明（<c>internal</c>，Core 之外不可见）。
/// </summary>
/// <remarks>
/// 为什么集中在一个文件里并保持 <c>internal</c>：
/// ① P/Invoke 的失败模式（返回码/SetLastError/结构体布局）无法被单测覆盖，只能靠集中
///    声明 + 只暴露很薄的托管包装（<see cref="IO.PathTools"/>/<see cref="IO.DriveInventory"/>）来限制影响面；
/// ② 句柄一律用 <see cref="IntPtr"/> 而不是 <c>int</c>——在 64 位进程里 <c>int</c> 会截断句柄，
///    表现为"偶发无效句柄"，属于最难排查的一类缺陷；
/// ③ 全部声明带 <c>SetLastError = true</c>，才能在失败时通过 <c>Marshal.GetLastWin32Error</c> 分类
///    （例如删源时要区分"被占用"与"其他错误"，后者不允许重试）。
/// </remarks>
internal static class NativeMethods
{
    // ------------------------------------------------------------------ 常量

    /// <summary><c>DRIVE_FIXED</c>：本地固定磁盘。</summary>
    internal const uint DriveFixed = 3;

    /// <summary><c>FILE_ATTRIBUTE_REPARSE_POINT</c>。</summary>
    internal const uint FileAttributeReparsePoint = 0x0400;

    /// <summary><c>FILE_ATTRIBUTE_SPARSE_FILE</c>。</summary>
    internal const uint FileAttributeSparseFile = 0x0200;

    /// <summary><c>FSCTL_SET_SPARSE</c>：把一个文件标记为稀疏（按洞复制的前提）。</summary>
    internal const uint FsctlSetSparse = 0x000900C4;

    /// <summary><c>GENERIC_READ</c>。</summary>
    internal const uint GenericRead = 0x80000000;

    /// <summary><c>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE</c>。</summary>
    internal const uint FileShareAll = 0x00000007;

    /// <summary><c>OPEN_EXISTING</c>。</summary>
    internal const uint OpenExisting = 3;

    /// <summary><c>SEE_MASK_NOCLOSEPROCESS</c>：让 <c>ShellExecuteExW</c> 回填 hProcess。</summary>
    internal const uint SeeMaskNoCloseProcess = 0x00000040;

    /// <summary><c>SEE_MASK_NOASYNC</c>：要求同步启动（避免进程还没起来调用方就退出）。</summary>
    internal const uint SeeMaskNoAsync = 0x00000400;

    /// <summary><c>SW_HIDE</c>。</summary>
    internal const int SwHide = 0;

    /// <summary><c>SW_SHOWNORMAL</c>。</summary>
    internal const int SwShowNormal = 1;

    /// <summary><c>FSCTL_GET_REPARSE_POINT</c>：按句柄读出重解析点数据（net48 用它替代 <c>FileSystemInfo.LinkTarget</c>）。</summary>
    internal const uint FsctlGetReparsePoint = 0x000900A8;

    /// <summary><c>FILE_FLAG_BACKUP_SEMANTICS</c>：能打开目录句柄。</summary>
    internal const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary><c>FILE_FLAG_OPEN_REPARSE_POINT</c>：打开链接本身而不是它的目标。</summary>
    internal const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary><c>SYMBOLIC_LINK_FLAG_DIRECTORY</c>：建目录链接。</summary>
    internal const uint SymbolicLinkFlagDirectory = 0x1;

    /// <summary><c>SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE</c>：未提权也能建链接（Win10 1703+）。</summary>
    internal const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;

    /// <summary>重解析点标签：符号链接。</summary>
    internal const uint IOReparseTagSymlink = 0xA000000C;

    /// <summary>重解析点标签：目录联接（junction）。</summary>
    internal const uint IOReparseTagMountPoint = 0xA0000003;

    /// <summary>创建符号链接（net48 没有 <c>File.CreateSymbolicLink</c>）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateSymbolicLinkW(
        string symlinkFileName,
        string targetFileName,
        uint flags);

    // ------------------------------------------------------------------ 磁盘与路径

    /// <summary>
    /// 取盘符的驱动类型。用它而不是 <c>DriveInfo.DriveType</c>：原实现就是这个 API，
    /// 换实现会让"某些盘在原版可选、重写后消失"（ReFS / 动态盘 / 存储池 / VHD 挂载盘的判定差异）。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetDriveTypeW(string rootPathName);

    /// <summary>取目录所在卷的可用/总空间（字节）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    /// <summary>把 8.3 短名还原为长名。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);

    /// <summary>取 8.3 短名（测试用来构造"C:\PROGRA~1"这类形态）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    /// <summary>取挂载点对应的卷 GUID 路径（形如 <c>\\?\Volume{...}\</c>）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeNameForVolumeMountPointW(
        string volumeMountPoint,
        StringBuilder volumeName,
        uint bufferLength);

    // ------------------------------------------------------------------ 文件句柄与稀疏

    /// <summary>创建/打开文件句柄（用于取指纹与设置稀疏属性）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    /// <summary>关闭句柄。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>设备控制（本工具只用于 <c>FSCTL_SET_SPARSE</c> / <c>FSCTL_GET_REPARSE_POINT</c>）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>按句柄取文件信息（体积、硬链接数、文件索引，即 inode 的等价物）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(IntPtr hFile, out ByHandleFileInformation fileInformation);

    /// <summary>
    /// 建立硬链接。用于"源里同一份 inode 有多个名字"的场景：不重连的话，一份 4 GB 的
    /// 虚拟机磁盘会被复制成两份（既浪费空间，也让两个名字从此不再是同一份数据）。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // ------------------------------------------------------------------ 权限与外壳

    /// <summary>当前进程是否为管理员（提权兜底提示用）。</summary>
    [DllImport("shell32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsUserAnAdmin();

    /// <summary>启动外部程序（提权重启 / 拉起 explorer / 打开目录）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteExW(ref ShellExecuteInfo executeInfo);

    /// <summary>广播窗口消息（环境变量变更通知，预留）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    /// <summary><c>SHELLEXECUTEINFOW</c>。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }
}
