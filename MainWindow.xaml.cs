using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DiskCleaner
{
    public partial class MainWindow : Window
    {
        private readonly bool isAdmin;
        private readonly ProcessDirectory processes = new();
        private readonly ObservableCollection<Category> categories = new();
        private readonly ObservableCollection<LargeFile> largeFiles = new();
        private CancellationTokenSource scanCts;
        private CancellationTokenSource largeCts;
        private bool dockerLoaded;

        private class DriveChoice
        {
            public string Root { get; set; }
            public string Display { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();
            using (var id = WindowsIdentity.GetCurrent())
                isAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);

            CategoryList.ItemsSource = categories;
            LargeList.ItemsSource = largeFiles;
            AdminBanner.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
            AdminText.Text = isAdmin ? "Running as administrator" : "Running as standard user";
            UpdateThemeLabel();

            SizeChanged += (_, _) => ApplyCompactLayout(ActualWidth < 900);
            Loaded += async (_, _) =>
            {
                LoadDrives();
                await ScanAsync();
            };
            Closing += (_, _) =>
            {
                scanCts?.Cancel();
                largeCts?.Cancel();
                folderCts?.Cancel();
                appsCts?.Cancel();
                SetActivityRunning(false);
                SetNetworkRunning(false);
            };
            NetAdminBanner.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- Layout and navigation ----------

        private void ApplyCompactLayout(bool compact)
        {
            NavColumn.Width = new GridLength(compact ? 64 : 232);
            var v = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (ListBoxItem item in NavList.Items)
                if (item.Content is StackPanel sp && sp.Children.Count > 1) sp.Children[1].Visibility = v;
            AppNameText.Visibility = ThemeLabel.Visibility = AdminText.Visibility = v;
        }

        private void OnNavChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageOverview == null) return;
            string tag = (NavList.SelectedItem as ListBoxItem)?.Tag as string ?? "Overview";
            var pages = new Dictionary<string, FrameworkElement>
            {
                ["Overview"] = PageOverview, ["Cleanup"] = PageCleanup, ["Large"] = PageLarge, ["Folders"] = PageFolders,
                ["Apps"] = PageApps, ["Startup"] = PageStartup, ["Activity"] = PageActivity, ["Network"] = PageNetwork, ["Docker"] = PageDocker
            };
            foreach (var kv in pages) kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;

            // Live pages only sample while they are visible.
            SetActivityRunning(tag == "Activity");
            SetNetworkRunning(tag == "Network");
            if (tag == "Docker" && !dockerLoaded) _ = LoadDockerAsync();
            if (tag == "Folders" && !foldersLoaded) InitFolders();
            if (tag == "Apps" && !appsLoaded) _ = LoadAppsAsync();
            if (tag == "Startup" && !startupLoaded) _ = LoadStartupAsync();
        }

        private void OnGoCleanup(object sender, RoutedEventArgs e) => NavList.SelectedIndex = 1;

        private void OnThemeClick(object sender, RoutedEventArgs e)
        {
            var app = Application.Current;
            var current = app.ThemeMode;
            app.ThemeMode = current == ThemeMode.System ? ThemeMode.Light : current == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.System;
            AppSettings.SaveTheme(app.ThemeMode);
            UpdateThemeLabel();
        }

        private void UpdateThemeLabel()
        {
            var mode = Application.Current.ThemeMode;
            string name = mode == ThemeMode.Light ? "Light" : mode == ThemeMode.Dark ? "Dark" : "System";
            ThemeLabel.Text = "Theme: " + name;
            ThemeGlyph.Text = mode == ThemeMode.Light ? "" : mode == ThemeMode.Dark ? "" : "";
            ThemeButton.ToolTip = "Switch theme (now " + name + ")";
            System.Windows.Automation.AutomationProperties.SetName(ThemeButton, "Theme: " + name + ". Switch theme");
        }

        private void OnRestartAdmin(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
                Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user declined the UAC prompt.
            }
        }

        private void SetBusy(bool busy, string title = null, string detail = null)
        {
            BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (title != null) BusyTitle.Text = title;
            BusyDetail.Text = detail ?? "";
        }

        // ---------- Drives ----------

        private void LoadDrives()
        {
            var drives = new List<DriveModel>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    drives.Add(new DriveModel
                    {
                        Name = d.Name.TrimEnd('\\'),
                        Label = string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel,
                        Total = d.TotalSize,
                        Free = d.AvailableFreeSpace
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            DriveList.ItemsSource = drives;

            var previous = (DriveCombo.SelectedItem as DriveChoice)?.Root;
            var choices = drives.Select(d => new DriveChoice { Root = d.Name + "\\", Display = d.Name + "  " + d.Label + " (" + d.FreeText + ")" }).ToList();
            DriveCombo.ItemsSource = choices;
            DriveCombo.SelectedItem = choices.FirstOrDefault(c => c.Root == previous) ?? choices.FirstOrDefault();
        }

        private static Dictionary<string, long> FreeSpaceSnapshot()
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady) map[d.Name.TrimEnd('\\')] = d.AvailableFreeSpace;
                }
                catch (IOException) { }
            }
            return map;
        }

        private static string FreeSpaceChange(Dictionary<string, long> before, Dictionary<string, long> after)
        {
            var parts = new List<string>();
            foreach (var kv in after)
            {
                if (!before.TryGetValue(kv.Key, out long b)) continue;
                long delta = kv.Value - b;
                if (Math.Abs(delta) < 1024 * 1024) continue;
                parts.Add(kv.Key + " " + Fmt.Bytes(b) + " free → " + Fmt.Bytes(kv.Value) + " free");
            }
            return parts.Count == 0 ? "" : string.Join(";  ", parts) + ".";
        }

        // ---------- Category scan ----------

        private void OnRescan(object sender, RoutedEventArgs e)
        {
            LoadDrives();
            _ = ScanAsync();
        }

        private async Task ScanAsync()
        {
            scanCts?.Cancel();
            scanCts = new CancellationTokenSource();
            var ct = scanCts.Token;

            OverviewRescan.IsEnabled = CleanupRescan.IsEnabled = false;
            ReclaimTotal.Text = "Scanning...";
            ReclaimSub.Text = "";
            var defs = Catalog.Build(isAdmin);
            var expanded = new HashSet<string>(categories.Where(c => c.IsExpanded).Select(c => c.Key));
            categories.Clear();
            foreach (var def in defs)
            {
                def.Category.IsScanning = true;
                def.Category.IsExpanded = expanded.Contains(def.Category.Key);
                def.Category.SelectionChanged += (_, _) => UpdateSelection();
                categories.Add(def.Category);
            }
            UpdateSelection();
            UpdateOverview();

            await Task.WhenAll(defs.Select(async def =>
            {
                List<CleanItem> items;
                try
                {
                    items = await Task.Run(() => def.Load(ct), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    items = new List<CleanItem>();
                }
                if (ct.IsCancellationRequested) return;
                def.Category.SetItems(items);
                def.Category.IsScanning = false;
                UpdateOverview();
                UpdateSelection();
            }));

            if (!ct.IsCancellationRequested) OverviewRescan.IsEnabled = CleanupRescan.IsEnabled = true;
        }

        private void UpdateOverview()
        {
            var cleanable = categories.Where(c => !c.IsInfo).ToList();
            long total = cleanable.Sum(c => c.TotalSize);
            bool scanning = categories.Any(c => c.IsScanning);
            ReclaimTotal.Text = Fmt.Bytes(total) + (scanning ? "..." : "");
            int found = cleanable.Count(c => c.TotalSize > 0);
            ReclaimSub.Text = scanning
                ? "Still scanning. Sizes are approximate."
                : "Found in " + found + (found == 1 ? " category" : " categories") + ". Sizes are approximate.";

            var rows = cleanable.Where(c => c.TotalSize > 0).OrderByDescending(c => c.TotalSize).ToList();
            long max = rows.Count > 0 ? rows[0].TotalSize : 1;
            foreach (var c in rows) c.Share = Math.Max(1, c.TotalSize * 100.0 / max);
            OverviewCats.ItemsSource = rows;
        }

        private void UpdateSelection()
        {
            var selected = categories.SelectMany(c => c.Items).Where(i => i.IsChecked && i.CanClean).ToList();
            long size = selected.Where(i => i.Size > 0).Sum(i => i.Size);
            SelectedTotal.Text = Fmt.Bytes(size) + " selected";
            SelectedSub.Text = selected.Count == 0 ? "Tick the items you want to remove." : selected.Count + (selected.Count == 1 ? " item" : " items");
            CleanButton.IsEnabled = selected.Count > 0 && !categories.Any(c => c.IsScanning);
        }

        // ---------- Cleaning ----------

        private async void OnCleanSelected(object sender, RoutedEventArgs e)
        {
            var selected = categories.SelectMany(c => c.Items).Where(i => i.IsChecked && i.CanClean).ToList();
            if (selected.Count == 0) return;

            bool hasBin = selected.Any(i => i.Mode == CleanMode.RecycleBin);
            var rows = selected.Select(i => new DialogRow
            {
                Name = i.Owner.Title + "  ›  " + i.Name,
                Detail = i.Detail,
                Value = i.SizeText,
                IsWarning = i.Mode == CleanMode.RecycleBin
            });
            long total = selected.Where(i => i.Size > 0).Sum(i => i.Size);
            string note = "Space moved to the Recycle Bin is only freed when the bin is emptied. Files that are in use are skipped.";
            if (hasBin) note += " Emptying the Recycle Bin is always permanent.";

            var dlg = new ConfirmDialog("Clean " + selected.Count + (selected.Count == 1 ? " item?" : " items?"),
                "These will be removed:", rows, Fmt.Bytes(total), "Clean", showModes: true, modeNote: note) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            bool permanent = dlg.Permanent;

            var before = FreeSpaceSnapshot();
            var reports = new List<CleanReport>();
            SetBusy(true, "Cleaning...");
            try
            {
                // Empty the bin first so it never wipes what this run just moved into it.
                foreach (var item in selected.OrderBy(i => i.Mode == CleanMode.RecycleBin ? 0 : 1))
                {
                    BusyDetail.Text = item.Owner.Title + ": " + item.Name;
                    var it = item;
                    reports.Add(await Cleaner.RunSta(() => Cleaner.Clean(it, permanent, CancellationToken.None)));
                }
            }
            finally
            {
                SetBusy(false);
            }
            var after = FreeSpaceSnapshot();
            ShowCleanResults(reports, permanent, before, after);
            OnRescan(this, new RoutedEventArgs());
        }

        private void ShowCleanResults(List<CleanReport> reports, bool permanent, Dictionary<string, long> before, Dictionary<string, long> after)
        {
            long freed = reports.Sum(r => r.Freed);
            int failed = reports.Sum(r => r.Failed);
            var rows = reports.Select(r => new DialogRow
            {
                Name = r.Name,
                Detail = r.Error ?? (r.Failed > 0 ? r.Removed + " removed, " + r.Failed + " skipped (in use or no access)" : r.Removed + " removed"),
                Value = Fmt.Bytes(r.Freed),
                IsWarning = r.Error != null
            });
            string message = permanent
                ? "Deleted " + Fmt.Bytes(freed) + "."
                : "Moved " + Fmt.Bytes(freed) + " to the Recycle Bin. Empty the bin to get that space back.";
            if (failed > 0) message += " " + failed + " entries were skipped because they are in use or need more rights.";
            string drives = FreeSpaceChange(before, after);
            if (drives.Length > 0) message += "\n" + drives;

            new ConfirmDialog("Cleanup finished", message, rows, Fmt.Bytes(freed), "Done", showCancel: false) { Owner = this }.ShowDialog();
        }

        // ---------- Large files ----------

        private async void OnLargeScan(object sender, RoutedEventArgs e)
        {
            if (DriveCombo.SelectedItem is not DriveChoice drive) return;
            long min = LargeFileFinder.ParseSize(SizeCombo.Text);
            if (min <= 0)
            {
                LargeStatus.Text = "Enter a minimum size such as 500 MB or 2 GB.";
                SizeCombo.Focus();
                return;
            }

            largeCts?.Cancel();
            largeCts = new CancellationTokenSource();
            var ct = largeCts.Token;
            largeFiles.Clear();
            UpdateLargeSelection();
            LargeEmpty.Visibility = Visibility.Collapsed;
            LargeScanButton.IsEnabled = false;
            LargeStopButton.IsEnabled = true;
            LargeProgress.Visibility = Visibility.Visible;

            var queue = new ConcurrentQueue<LargeFile>();
            var progress = new LargeScanProgress();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                FlushLarge(queue);
                LargeStatus.Text = "Scanning " + progress.CurrentFolder + "   (" + progress.Folders.ToString("N0") + " folders, " + progress.Files.ToString("N0") + " files)";
            };
            var watch = Stopwatch.StartNew();
            timer.Start();
            WalkStats stats;
            try
            {
                stats = await Task.Run(() => LargeFileFinder.Find(drive.Root, min, ct, progress, f =>
                {
                    queue.Enqueue(f);
                }));
            }
            finally
            {
                timer.Stop();
            }
            FlushLarge(queue);

            LargeScanButton.IsEnabled = true;
            LargeStopButton.IsEnabled = false;
            LargeProgress.Visibility = Visibility.Collapsed;
            string what = largeFiles.Count + (largeFiles.Count == 1 ? " file" : " files") + " over " + Fmt.Bytes(min) + " on " + drive.Root.TrimEnd('\\');
            string tail = " (" + progress.Folders.ToString("N0") + " folders in " + watch.Elapsed.TotalSeconds.ToString("0") + " s"
                + (stats.Errors > 0 ? ", " + stats.Errors.ToString("N0") + " folders could not be read" : "") + ").";
            LargeStatus.Text = (ct.IsCancellationRequested ? "Stopped. Found " : "Found ") + what + tail;
            LargeEmpty.Text = "No files that large.";
            LargeEmpty.Visibility = largeFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FlushLarge(ConcurrentQueue<LargeFile> queue)
        {
            while (queue.TryDequeue(out var f))
            {
                f.CheckedChanged += (_, _) => UpdateLargeSelection();
                // Keep the list sorted by size, biggest first.
                int i = 0;
                while (i < largeFiles.Count && largeFiles[i].Size >= f.Size) i++;
                largeFiles.Insert(i, f);
            }
        }

        private void OnLargeStop(object sender, RoutedEventArgs e) => largeCts?.Cancel();

        private void UpdateLargeSelection()
        {
            var selected = largeFiles.Where(f => f.IsChecked).ToList();
            LargeSelected.Text = Fmt.Bytes(selected.Sum(f => f.Size)) + " selected";
            LargeSelectedSub.Text = selected.Count == 0 ? "Tick files to delete them." : selected.Count + (selected.Count == 1 ? " file" : " files");
            LargeDeleteButton.IsEnabled = selected.Count > 0;
        }

        private async void OnLargeDelete(object sender, RoutedEventArgs e)
        {
            var selected = largeFiles.Where(f => f.IsChecked && f.CanSelect).ToList();
            if (selected.Count == 0) return;
            var rows = selected.Select(f => new DialogRow { Name = f.Name, Detail = f.Folder, Value = f.SizeText });
            var dlg = new ConfirmDialog("Delete " + selected.Count + (selected.Count == 1 ? " file?" : " files?"),
                "Check this list carefully. These are your own files.", rows, Fmt.Bytes(selected.Sum(f => f.Size)), "Delete",
                showModes: true, modeNote: "Files moved to the Recycle Bin still use space until the bin is emptied.") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            bool permanent = dlg.Permanent;

            var before = FreeSpaceSnapshot();
            var reports = new List<CleanReport>();
            SetBusy(true, "Deleting...");
            try
            {
                foreach (var f in selected)
                {
                    BusyDetail.Text = f.Name;
                    var file = f;
                    var r = await Cleaner.RunSta(() => Cleaner.DeleteLargeFile(file, permanent));
                    reports.Add(r);
                    if (r.Removed > 0) largeFiles.Remove(f);
                }
            }
            finally
            {
                SetBusy(false);
            }
            UpdateLargeSelection();
            ShowCleanResults(reports, permanent, before, FreeSpaceSnapshot());
            LoadDrives();
        }

        // ---------- Docker ----------

        private async Task LoadDockerAsync()
        {
            dockerLoaded = true;
            DockerRefresh.IsEnabled = false;
            DockerDf.Text = "Asking Docker...";
            var disks = await Task.Run(DockerTools.FindVirtualDisks);
            VhdxList.ItemsSource = disks;
            VhdxEmpty.Visibility = disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            string main = disks.FirstOrDefault(d => d.Name.Equals("docker_data.vhdx", StringComparison.OrdinalIgnoreCase))?.Path
                ?? disks.FirstOrDefault()?.Path
                ?? @"%LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx";
            CompactSteps.Text = string.Format(DockerTools.CompactSteps, main);

            var df = await DockerTools.SystemDf();
            DockerDf.Text = df.ExitCode == 0 ? df.Output : DockerError(df);
            DockerPruneButton.IsEnabled = df.ExitCode == 0;
            DockerRefresh.IsEnabled = true;
        }

        private static string DockerError(ProcessResult r)
        {
            if (r.NotFound) return r.Output;
            if (r.Output.Contains("error during connect", StringComparison.OrdinalIgnoreCase)
                || r.Output.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)
                || r.Output.Contains("pipe", StringComparison.OrdinalIgnoreCase))
                return "Docker Desktop is not running. Start it, then press Refresh.\n\n" + r.Output;
            return r.Output;
        }

        private void OnDockerRefresh(object sender, RoutedEventArgs e) => _ = LoadDockerAsync();

        private async void OnDockerPrune(object sender, RoutedEventArgs e)
        {
            var rows = new[]
            {
                new DialogRow { Name = "Stopped containers", Value = "removed" },
                new DialogRow { Name = "Unused networks", Value = "removed" },
                new DialogRow { Name = "Images not used by a container", Value = "removed" },
                new DialogRow { Name = "Build cache", Value = "removed" },
                new DialogRow { Name = "Volumes (databases and other data)", Value = "kept" }
            };
            var dlg = new ConfirmDialog("Prune Docker data?",
                "Runs docker system prune -a -f. This can't be undone, but removed images download again when needed.",
                rows, null, "Prune") { Owner = this };
            if (dlg.ShowDialog() != true) return;

            SetBusy(true, "Pruning Docker...", "This can take a few minutes.");
            ProcessResult result;
            try
            {
                result = await DockerTools.PruneAll();
            }
            finally
            {
                SetBusy(false);
            }
            string summary = result.Output.Split('\n').LastOrDefault(l => l.Contains("reclaimed", StringComparison.OrdinalIgnoreCase))?.Trim();
            string message = result.ExitCode == 0
                ? (summary ?? "Done.") + "\nThe .vhdx file keeps its size until it is compacted (see \"Shrink the virtual disk\")."
                : "Docker reported an error:\n" + DockerError(result);
            new ConfirmDialog("Docker prune finished", message, null, null, "Done", showCancel: false) { Owner = this }.ShowDialog();
            await LoadDockerAsync();
        }

        private void OnCopySteps(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(CompactSteps.Text); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }
}
