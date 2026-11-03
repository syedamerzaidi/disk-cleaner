using Microsoft.Win32;

namespace DiskCleaner
{
    public class StartupItem : Notify
    {
        private bool isEnabled;

        public string Name { get; set; }
        public string Command { get; set; }
        public string ExePath { get; set; }
        public string Publisher { get; set; }
        public string Source { get; set; }
        public System.Windows.Media.ImageSource Icon { get; set; }
        public bool HasIcon => Icon != null;
        public bool CanChange { get; set; }
        public string Note { get; set; }
        public bool HasNote => !string.IsNullOrEmpty(Note);

        // Where Windows records the enabled/disabled choice (same place Task Manager uses).
        internal RegistryHive ApprovedHive;
        internal string ApprovedKey;
        internal string ApprovedValue;

        public event EventHandler<bool> Toggled;

        public bool IsEnabled
        {
            get => isEnabled;
            set
            {
                if (isEnabled == value) return;
                isEnabled = value;
                Raise(nameof(IsEnabled), nameof(StatusText));
                Toggled?.Invoke(this, value);
            }
        }

        internal void SetEnabledSilently(bool value)
        {
            isEnabled = value;
            Raise(nameof(IsEnabled), nameof(StatusText));
        }

        public string StatusText => isEnabled ? "Starts with Windows" : "Disabled";
        public string Detail => string.Join("  ·  ", new[] { Publisher, Source }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>
    /// Startup entries from the Run keys and Startup folders. Turning one off writes the same
    /// StartupApproved flag Task Manager uses, so it is reversible and the entry itself is kept.
    /// </summary>
    public static class StartupManager
    {
        private const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupItem> Load(bool isAdmin)
        {
            var items = new List<StartupItem>();
            ReadRun(items, RegistryHive.CurrentUser, Run, "Run", "Registry (your account)", true);
            ReadRun(items, RegistryHive.LocalMachine, Run, "Run", "Registry (all users)", isAdmin);
            ReadRun(items, RegistryHive.LocalMachine, Run32, "Run32", "Registry (all users, 32-bit)", isAdmin);
            ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Startup folder (your account)", true);
            ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Startup folder (all users)", isAdmin);
            foreach (var i in items)
            {
                if (i.ExePath != null)
                {
                    try { i.Publisher = System.Diagnostics.FileVersionInfo.GetVersionInfo(i.ExePath).CompanyName?.Trim(); }
                    catch (FileNotFoundException) { }
                }
                if (!i.CanChange) i.Note = "Run as administrator to change this.";
            }
            return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void ReadRun(List<StartupItem> items, RegistryHive hive, string path, string approvedName, string source, bool canChange)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                if (key == null) return;
                foreach (string name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    string cmd = key.GetValue(name)?.ToString();
                    if (string.IsNullOrWhiteSpace(cmd)) continue;
                    var item = new StartupItem
                    {
                        Name = name,
                        Command = cmd,
                        ExePath = ExeFromCommand(cmd),
                        Source = source,
                        CanChange = canChange,
                        ApprovedHive = hive,
                        ApprovedKey = Approved + approvedName,
                        ApprovedValue = name
                    };
                    item.SetEnabledSilently(ReadApproved(item));
                    items.Add(item);
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void ReadFolder(List<StartupItem> items, string folder, RegistryHive hive, string source, bool canChange)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var c in FileWalker.ListChildren(folder))
            {
                if (c.IsDirectory) continue;
                string file = Path.GetFileName(c.Path);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var item = new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = c.Path,
                    ExePath = c.Path,
                    Source = source,
                    CanChange = canChange,
                    ApprovedHive = hive,
                    ApprovedKey = Approved + "StartupFolder",
                    ApprovedValue = file
                };
                item.SetEnabledSilently(ReadApproved(item));
                items.Add(item);
            }
        }

        private static bool ReadApproved(StartupItem item)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(item.ApprovedHive, RegistryView.Registry64);
                using var key = root.OpenSubKey(item.ApprovedKey);
                // An odd first byte (03, 07...) means the user turned it off. No value means enabled.
                return key?.GetValue(item.ApprovedValue) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
            }
            catch (System.Security.SecurityException) { return true; }
        }

        /// <summary>Writes the enabled/disabled flag. Returns false if Windows refused.</summary>
        public static bool SetEnabled(StartupItem item, bool enabled)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(item.ApprovedHive, RegistryView.Registry64);
                using var key = root.CreateSubKey(item.ApprovedKey, true);
                var data = new byte[12];
                data[0] = (byte)(enabled ? 2 : 3);
                if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
                key.SetValue(item.ApprovedValue, data, RegistryValueKind.Binary);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static string ExeFromCommand(string cmd)
        {
            string expanded = Environment.ExpandEnvironmentVariables(cmd);
            if (InstalledApps.SplitCommand(expanded, out string exe, out _) && File.Exists(exe)) return exe;
            return null;
        }
    }
}
