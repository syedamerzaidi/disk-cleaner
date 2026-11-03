using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DiskCleaner
{
    // Folders (storage drill-down), Apps (installed apps by size) and Startup apps pages.
    public partial class MainWindow
    {
        private bool foldersLoaded, appsLoaded, startupLoaded;
        private CancellationTokenSource folderCts, appsCts;
        private string currentFolder;
        private readonly ObservableCollection<FolderEntry> folderRows = new();
        private List<InstalledApp> allApps = new();

        // ---------- Folders ----------

        private void InitFolders()
        {
            foldersLoaded = true;
            FolderList.ItemsSource = folderRows;
            FolderRoots.Children.Clear();
            var roots = new List<(string label, string path)>();
            foreach (string d in Catalog.DriveInfoSafe()) roots.Add((d.TrimEnd('\\') + " drive", d));
            roots.Add(("Your user folder", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            roots.Add(("App data (Local)", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            string downloads = Native.KnownFolder(Native.FOLDERID_Downloads);
            if (downloads != null) roots.Add(("Downloads", downloads));
            foreach (var (label, path) in roots)
            {
                var b = new Button { Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6), Tag = path, ToolTip = path };
                b.Click += (s, _) => NavigateFolder((string)((Button)s).Tag);
                FolderRoots.Children.Add(b);
            }
            NavigateFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        private async void NavigateFolder(string path)
        {
            folderCts?.Cancel();
            folderCts = new CancellationTokenSource();
            var ct = folderCts.Token;
            currentFolder = path;
            FolderPath.Text = path;
            FolderUp.IsEnabled = Path.GetDirectoryName(path.TrimEnd('\\') + "\\") != null && path.TrimEnd('\\').Length > 2;

            var entries = await Task.Run(() => FolderSizer.Children(path));
            if (ct.IsCancellationRequested) return;
            folderRows.Clear();
            foreach (var e in entries) folderRows.Add(e);
            FolderStatus.Text = entries.Count == 0 ? "Empty, or no access." : "Measuring " + entries.Count(x => x.Measuring) + " folders...";

            int unreadable = 0;
            var refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            refresh.Tick += (_, _) => ResortFolders();
            refresh.Start();
            await Task.Run(() => FolderSizer.Measure(entries, ct, (e, s) =>
            {
                Interlocked.Add(ref unreadable, s.Errors);
                Dispatcher.BeginInvoke(() =>
                {
                    e.Size = s.Bytes;
                    e.Files = s.Files;
                    e.Measuring = false;
                });
            }));
            refresh.Stop();
            if (ct.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(ResortFolders, DispatcherPriority.Background);
            long total = entries.Sum(e => Math.Max(0, e.Size));
            FolderStatus.Text = Fmt.Bytes(total) + " in " + entries.Count + (entries.Count == 1 ? " item" : " items")
                + (unreadable > 0 ? ". " + unreadable.ToString("N0") + " folders could not be read (run as administrator to include them)." : ".");
        }

        private void ResortFolders()
        {
            var ordered = folderRows.OrderBy(e => e.Measuring).ThenByDescending(e => e.Size).ToList();
            long max = Math.Max(1, ordered.Max(e => (long?)e.Size) ?? 1);
            foreach (var e in ordered) e.Share = e.Size > 0 ? e.Size * 100.0 / max : 0;
            Sync(folderRows, ordered);
        }

        private void OnFolderOpen(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FolderEntry f && f.CanOpen) NavigateFolder(f.Path);
        }

        private void OnFolderUp(object sender, RoutedEventArgs e)
        {
            if (currentFolder == null) return;
            string parent = Path.GetDirectoryName(currentFolder.TrimEnd('\\'));
            if (parent != null) NavigateFolder(parent);
        }

        private void OnFolderRefresh(object sender, RoutedEventArgs e)
        {
            if (currentFolder != null) NavigateFolder(currentFolder);
        }

        // ---------- Apps ----------

        private async Task LoadAppsAsync()
        {
            appsLoaded = true;
            appsCts?.Cancel();
            appsCts = new CancellationTokenSource();
            var ct = appsCts.Token;
            AppsStatus.Text = "Loading...";
            allApps = await Task.Run(InstalledApps.Load);
            foreach (var a in allApps) a.Icon = IconFor(a.IconPath);
            ApplyAppFilter();
            AppsStatus.Text += "  Measuring apps with no recorded size...";
            await Task.Run(() => InstalledApps.MeasureMissing(allApps, ct));
            if (!ct.IsCancellationRequested) ApplyAppFilter();
        }

        private void ApplyAppFilter()
        {
            string q = AppSearch.Text.Trim();
            var list = allApps.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (a.Publisher ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Size).ThenBy(a => a.Name).ToList();
            long max = Math.Max(1, list.Max(a => (long?)a.Size) ?? 1);
            foreach (var a in list) a.Share = a.Size > 0 ? a.Size * 100.0 / max : 0;
            AppList.ItemsSource = list;
            AppsStatus.Text = list.Count + (list.Count == 1 ? " app" : " apps") + ", " + Fmt.Bytes(list.Sum(a => Math.Max(0, a.Size))) + " known size";
        }

        private void OnAppSearch(object sender, TextChangedEventArgs e)
        {
            if (appsLoaded) ApplyAppFilter();
        }

        private void OnUninstall(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not InstalledApp app || !app.CanUninstall) return;
            var rows = new[] { new DialogRow { Name = app.Name, Detail = app.Detail, Value = app.SizeText } };
            var dlg = new ConfirmDialog("Uninstall " + app.Name + "?",
                "This opens the app's own uninstaller. Follow its steps to finish; it may ask for administrator rights.", rows, null, "Uninstall") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (!InstalledApps.StartUninstall(app))
                new ConfirmDialog("Couldn't start the uninstaller", "Use Settings > Apps to remove " + app.Name + " instead.", null, null, "OK", showCancel: false) { Owner = this }.ShowDialog();
            appsLoaded = false; // reload next time the page opens
        }

        private void OnOpenAppSettings(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
        }

        // ---------- Startup apps ----------

        private async Task LoadStartupAsync()
        {
            startupLoaded = true;
            var items = await Task.Run(() => StartupManager.Load(isAdmin));
            foreach (var i in items)
            {
                i.Icon = IconFor(i.ExePath);
                i.Toggled += OnStartupToggled;
            }
            StartupList.ItemsSource = items;
            StartupEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnStartupToggled(object sender, bool enabled)
        {
            var item = (StartupItem)sender;
            if (StartupManager.SetEnabled(item, enabled)) return;
            item.SetEnabledSilently(!enabled);
            new ConfirmDialog("Couldn't change " + item.Name, "Windows refused the change. Run as administrator for apps that start for all users.", null, null, "OK", showCancel: false) { Owner = this }.ShowDialog();
        }
    }
}
