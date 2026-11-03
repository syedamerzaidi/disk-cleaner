using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Media;

namespace DiskCleaner
{
    /// <summary>One app (all processes sharing an executable) in the Activity list.</summary>
    public class AppUsage : Notify
    {
        private long memory;
        private double cpu;
        private long io;
        private int connections;
        private int processCount;
        private double memoryShare;

        public string Key { get; set; }
        public string Name { get; set; }
        public string ExePath { get; set; }
        public ImageSource Icon { get; set; }
        public bool HasIcon => Icon != null;
        public bool CanEnd { get; set; }
        public List<int> Pids { get; set; } = new();

        public long Memory { get => memory; set { memory = value; Raise(nameof(Memory), nameof(MemoryText)); } }
        public double Cpu { get => cpu; set { cpu = value; Raise(nameof(Cpu), nameof(CpuText)); } }
        public long Io { get => io; set { io = value; Raise(nameof(Io), nameof(IoText)); } }
        public int Connections { get => connections; set { connections = value; Raise(nameof(Connections), nameof(ConnectionsText)); } }
        public int ProcessCount { get => processCount; set { processCount = value; Raise(nameof(ProcessCount), nameof(DetailText)); } }
        public double MemoryShare { get => memoryShare; set { memoryShare = value; Raise(nameof(MemoryShare)); } }

        public string MemoryText => Fmt.Bytes(memory);
        public string CpuText => cpu < 0.05 ? "0%" : cpu.ToString(cpu < 10 ? "0.0" : "0") + "%";
        public string IoText => io <= 0 ? "-" : Fmt.Bytes(io) + "/s";
        public string ConnectionsText => connections == 0 ? "-" : connections.ToString();
        public string DetailText => (processCount == 1 ? "1 process" : processCount + " processes") + (string.IsNullOrEmpty(ExePath) ? "" : "  ·  " + ExePath);

        public System.Windows.Input.ICommand RevealCommand => new RelayCommand(() => { if (!string.IsNullOrEmpty(ExePath)) Fmt.Reveal(ExePath, true); });
    }

    public class AppSample
    {
        public string Key, Name, ExePath;
        public long Memory, Io;
        public double Cpu;
        public int Connections, ProcessCount;
        public bool CanEnd;
        public List<int> Pids = new();
    }

    public class SystemSample
    {
        public ulong MemTotal, MemUsed;
        public double Cpu;
        public long NetDown, NetUp; // bytes per second
        public List<AppSample> Apps;
    }

    /// <summary>Samples per-process memory, CPU, I/O and TCP connections. Call Sample() on a background thread.</summary>
    public class ActivityMonitor
    {
        // Processes that keep Windows running. "End task" is refused for them.
        private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
            "dwm", "fontdrvhost", "explorer", "sihost", "ctfmon", "MemCompression", "Secure System", "audiodg", "spoolsv", "conhost"
        };

        private readonly Dictionary<int, (long cpu, ulong io)> previous = new();
        private readonly Dictionary<string, string> descriptions = new(StringComparer.OrdinalIgnoreCase);
        private readonly int ownPid = Environment.ProcessId;
        private readonly int ownSession;
        private DateTime lastTime = DateTime.MinValue;
        private long lastIdle, lastKernel, lastUser;
        private long lastRecv, lastSent;

        public ActivityMonitor()
        {
            NativeSys.ProcessIdToSessionId(ownPid, out ownSession);
        }

        public SystemSample Sample()
        {
            var now = DateTime.UtcNow;
            double seconds = lastTime == DateTime.MinValue ? 0 : (now - lastTime).TotalSeconds;
            lastTime = now;
            var s = new SystemSample();

            var mem = new NativeSys.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSys.MEMORYSTATUSEX>() };
            if (NativeSys.GlobalMemoryStatusEx(ref mem))
            {
                s.MemTotal = mem.ullTotalPhys;
                s.MemUsed = mem.ullTotalPhys - mem.ullAvailPhys;
            }

            if (NativeSys.GetSystemTimes(out long idle, out long kernel, out long user))
            {
                long total = (kernel - lastKernel) + (user - lastUser); // kernel time includes idle
                if (lastKernel != 0 && total > 0) s.Cpu = Math.Clamp(100.0 * (total - (idle - lastIdle)) / total, 0, 100);
                lastIdle = idle; lastKernel = kernel; lastUser = user;
            }

            SampleNetwork(s, seconds);
            s.Apps = SampleProcesses(seconds);
            return s;
        }

        private void SampleNetwork(SystemSample s, double seconds)
        {
            long recv = 0, sent = 0;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var st = nic.GetIPStatistics();
                    recv += st.BytesReceived;
                    sent += st.BytesSent;
                }
            }
            catch (NetworkInformationException) { return; }
            if (seconds > 0 && lastRecv > 0)
            {
                s.NetDown = (long)(Math.Max(0, recv - lastRecv) / seconds);
                s.NetUp = (long)(Math.Max(0, sent - lastSent) / seconds);
            }
            lastRecv = recv;
            lastSent = sent;
        }

        private List<AppSample> SampleProcesses(double seconds)
        {
            var tcp = NativeSys.TcpConnectionsByPid();
            var groups = new Dictionary<string, AppSample>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<int>();
            int cores = Environment.ProcessorCount;

            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    int pid = p.Id;
                    string name = p.ProcessName;
                    if (pid == 0) continue;
                    seen.Add(pid);
                    long memory = 0, cpuTime = 0;
                    ulong ioTotal = 0;
                    string exe = null;

                    IntPtr h = NativeSys.OpenProcess(NativeSys.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            if (NativeSys.K32GetProcessMemoryInfo(h, out var mc, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSys.PROCESS_MEMORY_COUNTERS_EX>()))
                                memory = (long)mc.WorkingSetSize;
                            if (NativeSys.GetProcessTimes(h, out _, out _, out long k, out long u)) cpuTime = k + u;
                            if (NativeSys.GetProcessIoCounters(h, out var io))
                                ioTotal = io.ReadTransferCount + io.WriteTransferCount + io.OtherTransferCount;
                            exe = NativeSys.ImagePath(h);
                        }
                        finally
                        {
                            NativeSys.CloseHandle(h);
                        }
                    }
                    else
                    {
                        try { memory = p.WorkingSet64; } catch (InvalidOperationException) { }
                    }

                    double cpu = 0;
                    long ioRate = 0;
                    if (seconds > 0 && previous.TryGetValue(pid, out var prev))
                    {
                        cpu = Math.Clamp((cpuTime - prev.cpu) / (seconds * 10_000_000.0 * cores) * 100.0, 0, 100);
                        if (ioTotal >= prev.io) ioRate = (long)((ioTotal - prev.io) / seconds);
                    }
                    previous[pid] = (cpuTime, ioTotal);

                    string key = exe ?? name;
                    if (!groups.TryGetValue(key, out var g))
                    {
                        NativeSys.ProcessIdToSessionId(pid, out int session);
                        g = new AppSample
                        {
                            Key = key,
                            ExePath = exe,
                            Name = Describe(exe, name),
                            CanEnd = session == ownSession && session != 0 && !Critical.Contains(name) && exe != null
                        };
                        groups[key] = g;
                    }
                    g.Pids.Add(pid);
                    g.ProcessCount++;
                    g.Memory += memory;
                    g.Cpu += cpu;
                    g.Io += ioRate;
                    if (tcp.TryGetValue(pid, out int c)) g.Connections += c;
                    if (pid == ownPid) g.CanEnd = false;
                }
            }
            foreach (int gone in previous.Keys.Where(k => !seen.Contains(k)).ToList()) previous.Remove(gone);
            return groups.Values.ToList();
        }

        private string Describe(string exe, string fallback)
        {
            if (exe == null) return fallback;
            if (descriptions.TryGetValue(exe, out string d)) return d;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                d = string.IsNullOrWhiteSpace(info.FileDescription) ? fallback : info.FileDescription.Trim();
            }
            catch (FileNotFoundException) { d = fallback; }
            catch (UnauthorizedAccessException) { d = fallback; }
            descriptions[exe] = d;
            return d;
        }

        /// <summary>Ends every process of an app. Returns how many could not be ended.</summary>
        public static int EndApp(AppUsage app)
        {
            int failed = 0;
            foreach (int pid in app.Pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill();
                }
                catch (ArgumentException) { } // already gone
                catch (System.ComponentModel.Win32Exception) { failed++; }
                catch (InvalidOperationException) { }
            }
            return failed;
        }
    }
}
