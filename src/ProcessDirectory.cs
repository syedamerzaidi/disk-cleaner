using System.Diagnostics;
using System.Windows.Media;

namespace DiskCleaner
{
    /// <summary>Caches display name, executable path and icon per process id and per executable.</summary>
    public class ProcessDirectory
    {
        public class Entry
        {
            public string Name;
            public string ExePath;
            public ImageSource Icon;
        }

        private readonly Dictionary<int, Entry> byPid = new();
        private readonly Dictionary<string, Entry> byExe = new(StringComparer.OrdinalIgnoreCase);

        public Entry Get(int pid)
        {
            lock (byPid)
            {
                if (byPid.TryGetValue(pid, out var e)) return e;
            }
            var entry = Lookup(pid);
            lock (byPid) byPid[pid] = entry;
            return entry;
        }

        /// <summary>Drops pids that no longer exist so recycled ids get looked up again.</summary>
        public void Prune(ICollection<int> alive)
        {
            lock (byPid)
                foreach (int pid in byPid.Keys.Where(p => !alive.Contains(p)).ToList()) byPid.Remove(pid);
        }

        private Entry Lookup(int pid)
        {
            if (pid == 4) return new Entry { Name = "Windows (System)" };
            string exe = null;
            IntPtr h = NativeSys.OpenProcess(NativeSys.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try { exe = NativeSys.ImagePath(h); }
                finally { NativeSys.CloseHandle(h); }
            }
            if (exe != null)
            {
                lock (byExe)
                    if (byExe.TryGetValue(exe, out var cached)) return cached;
                var e = new Entry { ExePath = exe, Name = Describe(exe), Icon = NativeSys.SmallIcon(exe) };
                lock (byExe) byExe[exe] = e;
                return e;
            }
            string name;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
            }
            catch (ArgumentException) { name = "Exited process"; }
            catch (InvalidOperationException) { name = "Exited process"; }
            return new Entry { Name = name + " (" + pid + ")" };
        }

        public static string Describe(string exe)
        {
            string fallback = Path.GetFileNameWithoutExtension(exe);
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                return string.IsNullOrWhiteSpace(info.FileDescription) ? fallback : info.FileDescription.Trim();
            }
            catch (FileNotFoundException) { return fallback; }
            catch (UnauthorizedAccessException) { return fallback; }
        }
    }
}
