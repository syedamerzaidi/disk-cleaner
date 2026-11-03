using System.Diagnostics;
using Microsoft.Win32;

namespace DiskCleaner
{
    public class InstalledApp : Notify
    {
        private long size = -1;
        private double share;

        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string InstallLocation { get; set; }
        public string UninstallCommand { get; set; }
        public DateTime? InstallDate { get; set; }
        public bool SizeFromFolder { get; set; }
        public System.Windows.Media.ImageSource Icon { get; set; }
        public string IconPath { get; set; }

        public long Size { get => size; set { size = value; Raise(nameof(Size), nameof(SizeText)); } }
        public double Share { get => share; set { share = value; Raise(nameof(Share)); } }
        public string SizeText => size < 0 ? "Unknown" : Fmt.Bytes(size);
        public bool HasIcon => Icon != null;
        public bool CanUninstall => !string.IsNullOrEmpty(UninstallCommand);
        public bool CanOpen => !string.IsNullOrEmpty(InstallLocation) && FileWalker.DirectoryExists(InstallLocation);

        public string Detail
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Publisher)) parts.Add(Publisher);
                if (!string.IsNullOrEmpty(Version)) parts.Add(Version);
                if (InstallDate.HasValue) parts.Add("installed " + InstallDate.Value.ToString("d MMM yyyy"));
                return string.Join("  ·  ", parts);
            }
        }

        public System.Windows.Input.ICommand OpenCommand => new RelayCommand(() => { if (CanOpen) Fmt.Reveal(InstallLocation, false); });
    }

    /// <summary>Reads desktop apps from the Uninstall registry keys (machine 64/32-bit and current user).</summary>
    public static class InstalledApps
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<InstalledApp> Load()
        {
            var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
            Read(RegistryHive.LocalMachine, RegistryView.Registry64, apps);
            Read(RegistryHive.LocalMachine, RegistryView.Registry32, apps);
            Read(RegistryHive.CurrentUser, RegistryView.Registry64, apps);
            return apps.Values.OrderByDescending(a => a.Size).ThenBy(a => a.Name).ToList();
        }

        private static void Read(RegistryHive hive, RegistryView view, Dictionary<string, InstalledApp> apps)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (key == null) return;
                foreach (string sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    if (k == null) continue;
                    string name = (k.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    // Hidden components and update entries are not separate apps.
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") != null || k.GetValue("ReleaseType") is string rt && rt.Contains("Update")) continue;

                    var app = new InstalledApp
                    {
                        Name = name,
                        Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                        Version = (k.GetValue("DisplayVersion") as string)?.Trim(),
                        InstallLocation = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"').TrimEnd('\\'),
                        UninstallCommand = (k.GetValue("UninstallString") as string)?.Trim(),
                        IconPath = IconFile(k.GetValue("DisplayIcon") as string)
                    };
                    if (k.GetValue("EstimatedSize") is int kb && kb > 0) app.Size = kb * 1024L;
                    if (k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
                        app.InstallDate = date;

                    string id = name + "|" + app.Version;
                    if (!apps.ContainsKey(id)) apps[id] = app;
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string IconFile(string displayIcon)
        {
            if (string.IsNullOrWhiteSpace(displayIcon)) return null;
            string s = displayIcon.Trim();
            int comma = s.LastIndexOf(',');
            if (comma > 2) s = s.Substring(0, comma);
            s = s.Trim().Trim('"');
            return File.Exists(s) ? s : null;
        }

        /// <summary>Fills in sizes the installer didn't record by measuring the install folder.</summary>
        public static void MeasureMissing(IEnumerable<InstalledApp> apps, CancellationToken ct)
        {
            foreach (var app in apps)
            {
                if (ct.IsCancellationRequested) return;
                if (app.Size >= 0 || string.IsNullOrEmpty(app.InstallLocation)) continue;
                if (Safety.IsCriticalFolder(app.InstallLocation) || !FileWalker.DirectoryExists(app.InstallLocation)) continue;
                var s = FileWalker.DirSize(app.InstallLocation, ct);
                if (s.Bytes > 0)
                {
                    app.Size = s.Bytes;
                    app.SizeFromFolder = true;
                }
            }
        }

        /// <summary>Starts the app's own uninstaller through the shell, so it can ask for elevation.</summary>
        public static bool StartUninstall(InstalledApp app)
        {
            if (!SplitCommand(app.UninstallCommand, out string exe, out string args)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public static bool SplitCommand(string command, out string exe, out string args)
        {
            exe = args = null;
            if (string.IsNullOrWhiteSpace(command)) return false;
            string c = command.Trim();
            if (c.StartsWith('"'))
            {
                int end = c.IndexOf('"', 1);
                if (end < 0) return false;
                exe = c.Substring(1, end - 1);
                args = c.Substring(end + 1).Trim();
                return true;
            }
            int i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            exe = c.Substring(0, i + 4);
            args = c.Substring(i + 4).Trim();
            return true;
        }
    }
}
