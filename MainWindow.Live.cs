using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiskCleaner
{
    // Activity (per-app memory, CPU, I/O) and Network (per-app connections) pages.
    public partial class MainWindow
    {
        private ActivityMonitor activity;
        private DispatcherTimer activityTimer;
        private bool activityBusy;
        private SystemSample lastActivity;
        private readonly ObservableCollection<AppUsage> activityRows = new();
        private readonly Dictionary<string, AppUsage> activityByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ImageSource> iconCache = new(StringComparer.OrdinalIgnoreCase);

        private NetworkMonitor network;
        private DispatcherTimer networkTimer;
        private bool networkBusy;
        private DateTime lastNetworkSample;
        private readonly ObservableCollection<ConnectionInfo> netRows = new();
        private readonly Dictionary<string, ConnectionInfo> netByKey = new();

        /// <summary>Reorders a bound collection to match a list without resetting scroll position.</summary>
        private static void Sync<T>(ObservableCollection<T> target, IList<T> desired)
        {
            var keep = new HashSet<T>(desired);
            for (int i = target.Count - 1; i >= 0; i--)
                if (!keep.Contains(target[i])) target.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                int at = target.IndexOf(desired[i]);
                if (at < 0) target.Insert(i, desired[i]);
                else if (at != i) target.Move(at, i);
            }
        }

        private ImageSource IconFor(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            if (!iconCache.TryGetValue(exe, out var icon))
            {
                icon = NativeSys.SmallIcon(exe);
                iconCache[exe] = icon;
            }
            return icon;
        }

        // ---------- Activity ----------

        private void SetActivityRunning(bool on)
        {
            if (on)
            {
                activity ??= new ActivityMonitor();
                ActivityList.ItemsSource ??= activityRows;
                if (activityTimer == null)
                {
                    activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    activityTimer.Tick += async (_, _) => await ActivityTickAsync();
                }
                activityTimer.Start();
                _ = ActivityTickAsync();
            }
            else
            {
                activityTimer?.Stop();
            }
        }

        private async Task ActivityTickAsync()
        {
            if (activityBusy) return;
            activityBusy = true;
            try
            {
                lastActivity = await Task.Run(activity.Sample);
                ApplyActivity();
            }
            finally
            {
                activityBusy = false;
            }
        }

        private void ApplyActivity()
        {
            var s = lastActivity;
            if (s == null) return;
            double memPct = s.MemTotal > 0 ? s.MemUsed * 100.0 / s.MemTotal : 0;
            MemText.Text = Fmt.Bytes((long)s.MemUsed) + " of " + Fmt.Bytes((long)s.MemTotal);
            MemBar.Value = memPct;
            CpuText.Text = s.Cpu.ToString("0") + "%";
            CpuBar.Value = s.Cpu;
            NetText.Text = "↓ " + Fmt.Bytes(s.NetDown) + "/s   ↑ " + Fmt.Bytes(s.NetUp) + "/s";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in s.Apps)
            {
                seen.Add(a.Key);
                if (!activityByKey.TryGetValue(a.Key, out var row))
                {
                    row = new AppUsage { Key = a.Key, Name = a.Name, ExePath = a.ExePath, Icon = IconFor(a.ExePath) };
                    activityByKey[a.Key] = row;
                }
                row.CanEnd = a.CanEnd;
                row.Pids = a.Pids;
                row.Memory = a.Memory;
                row.Cpu = a.Cpu;
                row.Io = a.Io;
                row.Connections = a.Connections;
                row.ProcessCount = a.ProcessCount;
            }
            foreach (string gone in activityByKey.Keys.Where(k => !seen.Contains(k)).ToList()) activityByKey.Remove(gone);

            long maxMem = Math.Max(1, activityByKey.Values.Max(r => (long?)r.Memory) ?? 1);
            foreach (var r in activityByKey.Values) r.MemoryShare = r.Memory * 100.0 / maxMem;

            string filter = ActivitySearch.Text.Trim();
            IEnumerable<AppUsage> rows = activityByKey.Values;
            if (filter.Length > 0)
                rows = rows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (r.ExePath ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
            rows = SortCpu.IsChecked == true ? rows.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.Memory)
                : SortIo.IsChecked == true ? rows.OrderByDescending(r => r.Io).ThenByDescending(r => r.Memory)
                : SortConn.IsChecked == true ? rows.OrderByDescending(r => r.Connections).ThenByDescending(r => r.Memory)
                : rows.OrderByDescending(r => r.Memory);
            Sync(activityRows, rows.ToList());
        }

        private void OnActivitySort(object sender, RoutedEventArgs e)
        {
            if (activityRows.Count > 0) ApplyActivity();
        }

        private void OnActivityFilter(object sender, TextChangedEventArgs e)
        {
            if (lastActivity != null) ApplyActivity();
        }

        private void OnEndTask(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not AppUsage app || !app.CanEnd) return;
            var rows = new[] { new DialogRow { Name = app.Name, Detail = app.DetailText, Value = app.MemoryText } };
            var dlg = new ConfirmDialog("End " + app.Name + "?",
                "All of its processes are closed right away. Unsaved work in this app will be lost.", rows, null, "End task") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            int failed = ActivityMonitor.EndApp(app);
            if (failed > 0)
                new ConfirmDialog("Could not end every process", failed + " process(es) need administrator rights to end.", null, null, "OK", showCancel: false) { Owner = this }.ShowDialog();
            _ = ActivityTickAsync();
        }

        // ---------- Network ----------

        private void SetNetworkRunning(bool on)
        {
            if (on)
            {
                network ??= new NetworkMonitor(isAdmin);
                NetList.ItemsSource ??= netRows;
                if (networkTimer == null)
                {
                    networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    networkTimer.Tick += async (_, _) => await NetworkTickAsync();
                }
                networkTimer.Start();
                _ = NetworkTickAsync();
            }
            else
            {
                networkTimer?.Stop();
            }
        }

        private async Task NetworkTickAsync()
        {
            if (networkBusy) return;
            networkBusy = true;
            try
            {
                bool listening = ShowListening.IsChecked == true;
                var samples = await Task.Run(() => network.Read(listening));
                ApplyNetwork(samples);
            }
            finally
            {
                networkBusy = false;
            }
        }

        private void ApplyNetwork(List<NetworkMonitor.Sample> samples)
        {
            var now = DateTime.UtcNow;
            double seconds = lastNetworkSample == default ? 0 : (now - lastNetworkSample).TotalSeconds;
            lastNetworkSample = now;

            var seen = new HashSet<string>();
            foreach (var s in samples)
            {
                if (!seen.Add(s.Key)) continue;
                if (!netByKey.TryGetValue(s.Key, out var row))
                {
                    var p = processes.Get(s.Pid);
                    row = new ConnectionInfo
                    {
                        Key = s.Key, Protocol = s.Protocol, Pid = s.Pid, AppName = p.Name, ExePath = p.ExePath, Icon = p.Icon,
                        Local = s.Local, RemoteIp = s.RemoteIp, RemotePort = s.RemotePort, IsListening = s.IsListening
                    };
                    if (!s.IsListening)
                    {
                        row.Host = network.CachedHost(s.RemoteIp);
                        if (row.Host == null) _ = ResolveHostAsync(row);
                    }
                    netByKey[s.Key] = row;
                }
                else if (seconds > 0 && s.BytesIn >= 0 && row.BytesIn >= 0)
                {
                    row.RateIn = (long)(Math.Max(0, s.BytesIn - row.BytesIn) / seconds);
                    row.RateOut = (long)(Math.Max(0, s.BytesOut - row.BytesOut) / seconds);
                }
                row.State = s.State;
                if (s.BytesIn >= 0)
                {
                    row.BytesIn = s.BytesIn;
                    row.BytesOut = s.BytesOut;
                }
            }
            foreach (string gone in netByKey.Keys.Where(k => !seen.Contains(k)).ToList()) netByKey.Remove(gone);
            processes.Prune(samples.Select(s => s.Pid).ToHashSet());

            string filter = NetSearch.Text.Trim();
            IEnumerable<ConnectionInfo> rows = netByKey.Values;
            if (filter.Length > 0)
                rows = rows.Where(r => r.AppName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || r.RemoteText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || r.PortText.Contains(filter, StringComparison.OrdinalIgnoreCase));
            var ordered = rows.OrderBy(r => r.IsListening)
                .ThenByDescending(r => r.RateIn + r.RateOut)
                .ThenBy(r => r.AppName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.RemoteText).ToList();
            Sync(netRows, ordered);

            var connected = netByKey.Values.Where(r => !r.IsListening).ToList();
            var apps = connected.GroupBy(r => r.AppName).Select(g => new AppNetworkSummary
            {
                Name = g.Key,
                Icon = g.First().Icon,
                Connections = g.Count(),
                RateIn = g.Sum(r => r.RateIn),
                RateOut = g.Sum(r => r.RateOut),
                TotalIn = g.Sum(r => Math.Max(0, r.BytesIn)),
                TotalOut = g.Sum(r => Math.Max(0, r.BytesOut)),
                HasTraffic = network.CollectsBytes
            }).OrderByDescending(a => a.TotalIn + a.TotalOut).ThenByDescending(a => a.Connections).Take(8).ToList();
            NetApps.ItemsSource = apps;
            int appCount = connected.Select(r => r.AppName).Distinct().Count();
            NetCount.Text = connected.Count + (connected.Count == 1 ? " connection" : " connections") + " from " + appCount + (appCount == 1 ? " app" : " apps")
                + (network.CollectsBytes ? ". Traffic counted since this page opened." : "");
        }

        private async Task ResolveHostAsync(ConnectionInfo row)
        {
            string host = await network.ResolveAsync(row.RemoteIp);
            if (!string.IsNullOrEmpty(host)) row.Host = host;
        }

        private void OnNetOption(object sender, RoutedEventArgs e)
        {
            if (network != null) _ = NetworkTickAsync();
        }

        private void OnNetFilter(object sender, TextChangedEventArgs e)
        {
            if (network != null) _ = NetworkTickAsync();
        }
    }
}
