using System.Runtime.InteropServices;
using System.Text;

namespace DiskCleaner
{
    /// <summary>Win32 calls for process, memory, CPU and TCP statistics.</summary>
    public static class NativeSys
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS counters);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool K32GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);

        public static string ImagePath(IntPtr h)
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }

        [DllImport("kernel32.dll")]
        public static extern bool ProcessIdToSessionId(int pid, out int session);

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5, MIB_TCP_STATE_ESTAB = 5;

        /// <summary>Established TCP connections (IPv4 and IPv6) per process id.</summary>
        public static Dictionary<int, int> TcpConnectionsByPid()
        {
            var map = new Dictionary<int, int>();
            ReadTcp(AF_INET, 24, 0, 20, map);   // MIB_TCPROW_OWNER_PID: state at 0, pid at 20
            ReadTcp(AF_INET6, 56, 48, 52, map); // MIB_TCP6ROW_OWNER_PID: state at 48, pid at 52
            return map;
        }

        private static void ReadTcp(int af, int rowSize, int stateOffset, int pidOffset, Dictionary<int, int> map)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    uint rc = GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew, try again
                    if (rc != 0) return;
                    int count = Marshal.ReadInt32(buf);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = buf + 4 + i * rowSize;
                        if (Marshal.ReadInt32(row, stateOffset) != MIB_TCP_STATE_ESTAB) continue;
                        int pid = Marshal.ReadInt32(row, pidOffset);
                        map[pid] = map.TryGetValue(pid, out int c) ? c + 1 : 1;
                    }
                    return;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        // ---- Icons ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1;

        /// <summary>Small shell icon for a file as a frozen WPF image, or null.</summary>
        public static System.Windows.Media.ImageSource SmallIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var info = new SHFILEINFO();
            if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
    }
}
