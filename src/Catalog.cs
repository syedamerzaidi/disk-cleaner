using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DiskCleaner
{
    /// <summary>Defines the cleanup categories and measures what each one holds.</summary>
    public static class Catalog
    {
        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static readonly string ProgData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        private static readonly string[] InstallerExt = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso", ".img", ".apk", ".aab" };

        public class Definition
        {
            public Category Category;
            public Func<CancellationToken, List<CleanItem>> Load;
        }

        public static List<Definition> Build(bool isAdmin)
        {
            var defs = new List<Definition>();

            defs.Add(Def("installers", "Installers & disk images", "\uE896",
                "Setup files and disk images in Downloads and on the Desktop. Usually safe to remove once the app is installed.",
                ct => LoadInstallers(ct)));

            defs.Add(Def("temp", "Temporary files", "\uE81C",
                "Leftovers from installers and apps. Files that are in use are skipped.",
                ct => Measure(ct, isAdmin,
                    Folder("Your temp folder", Path.GetTempPath(), true, false, null),
                    Folder("Windows temp folder", Path.Combine(Win, "Temp"), true, true, null))));

            defs.Add(Def("recycle", "Recycle Bin", "\uE74D",
                "Files you already deleted. Emptying the bin removes them for good.",
                ct => LoadRecycleBin()));

            defs.Add(Def("winupdate", "Windows Update cache", "\uE895",
                "Downloaded update packages that are already installed. Windows downloads again anything it still needs.",
                ct => Measure(ct, isAdmin,
                    Folder("Update downloads", Path.Combine(Win, @"SoftwareDistribution\Download"), true, true, null))));

            defs.Add(Def("crash", "Crash dumps & error reports", "\uE7BA",
                "Memory dumps and Windows Error Reporting files kept after apps or Windows crashed.",
                ct => Measure(ct, isAdmin,
                    Folder("App crash dumps", Path.Combine(Local, "CrashDumps"), true, false, null),
                    Folder("System minidumps", Path.Combine(Win, "Minidump"), true, true, null),
                    FileItem("Full memory dump", Path.Combine(Win, "MEMORY.DMP"), true, true, null),
                    Folders("Error reports (your account)", true, false, null,
                        Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue")),
                    Folders("Error reports (system)", true, true, null,
                        Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue")))));

            defs.Add(Def("browser", "Browser caches", "\uE774",
                "Cached pages, images and shaders for Chrome and Edge. Only cache folders are cleaned: passwords, cookies, history and profiles are never touched.",
                ct => LoadBrowsers(ct, isAdmin)));

            defs.Add(Def("dev", "Developer caches", "\uE943",
                "Package manager download caches. They refill on the next install or build, which will be slower the first time.",
                ct => LoadDevCaches(ct, isAdmin)));

            defs.Add(Def("unity", "Unity Hub downloads", "\uE7FC",
                "Editor and module installers Unity Hub downloaded. Installed editors are not affected.",
                ct => Measure(ct, isAdmin, Folder("Unity Hub downloads", Path.Combine(Roaming, @"UnityHub\downloads"), true, false, null))));

            defs.Add(Def("android", "Android SDK downloads", "\uE8EA",
                "Partial and temporary downloads the Android SDK manager left behind. Installed SDK packages are not affected.",
                ct => LoadAndroid(ct, isAdmin)));

            var sys = Def("system", "Hibernation & page files", "\uE946",
                "Large Windows system files. They can't be deleted here, but you can shrink or turn them off in Windows.",
                ct => LoadSystemFiles());
            sys.Category.IsInfo = true;
            sys.Category.HowTo =
                "Hibernation file (hiberfil.sys)\n" +
                "Open Terminal as administrator and run  powercfg /hibernate off  to remove it (this also turns off Hibernate and Fast Startup). " +
                "To keep Fast Startup with a smaller file, run  powercfg /hibernate /type reduced  instead.\n\n" +
                "Page file (pagefile.sys)\n" +
                "Settings > System > About > Advanced system settings > Performance: Settings > Advanced > Virtual memory: Change. " +
                "Untick \"Automatically manage\", then set a smaller custom size or move it to another drive. Don't turn it off completely: apps can crash when memory runs out.\n\n" +
                "Swap file (swapfile.sys)\n" +
                "Used by Store apps and managed by Windows together with the page file. Leave it alone.";
            defs.Add(sys);

            return defs;
        }

        private static Definition Def(string key, string title, string glyph, string description, Func<CancellationToken, List<CleanItem>> load)
        {
            return new Definition
            {
                Category = new Category { Key = key, Title = title, Glyph = glyph, Description = description },
                Load = load
            };
        }

        private static CleanItem Folder(string name, string path, bool check, bool needsAdmin, string note)
        {
            return Folders(name, check, needsAdmin, note, path);
        }

        private static CleanItem Folders(string name, bool check, bool needsAdmin, string note, params string[] paths)
        {
            var item = new CleanItem { Name = name, Mode = CleanMode.Contents, NeedsAdmin = needsAdmin, Note = note };
            foreach (string p in paths)
                if (!string.IsNullOrEmpty(p)) item.Paths.Add(p.TrimEnd('\\'));
            item.Detail = string.Join("  ·  ", item.Paths);
            item.IsChecked = check;
            return item;
        }

        private static CleanItem FileItem(string name, string path, bool check, bool needsAdmin, string note)
        {
            var item = new CleanItem { Name = name, Mode = CleanMode.Files, NeedsAdmin = needsAdmin, Note = note, Detail = path };
            item.Paths.Add(path);
            item.IsChecked = check;
            return item;
        }

        /// <summary>Measures items and keeps the ones that exist on this machine.</summary>
        private static List<CleanItem> Measure(CancellationToken ct, bool isAdmin, params CleanItem[] items)
        {
            var result = new List<CleanItem>();
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (MeasureItem(item, ct, isAdmin)) result.Add(item);
            }
            return result;
        }

        /// <summary>Sets the item's size, note and cleanability. Returns false when nothing exists.</summary>
        public static bool MeasureItem(CleanItem item, CancellationToken ct, bool isAdmin)
        {
            bool wanted = item.IsChecked;
            long total = 0;
            bool exists = false, denied = false;
            foreach (string p in item.Paths)
            {
                if (item.Mode == CleanMode.Contents)
                {
                    if (!FileWalker.DirectoryExists(p)) continue;
                    exists = true;
                    var s = FileWalker.DirSize(p, ct);
                    total += s.Bytes;
                    if (s.RootDenied || s.Errors > 0) denied |= s.RootDenied;
                }
                else
                {
                    long size = FileWalker.FileSize(p);
                    if (size < 0) continue;
                    exists = true;
                    total += size;
                }
            }
            // Empty folders are left out; unreadable ones stay so the user learns they need admin rights.
            if (!exists || (total == 0 && !denied)) return false;

            item.Size = total;
            if (item.Mode == CleanMode.InfoOnly)
            {
                item.CanClean = false;
                return true;
            }
            if (item.NeedsAdmin && !isAdmin)
            {
                item.CanClean = false;
                item.NoteIsWarning = true;
                item.Note = denied ? "Run as administrator to measure and clean this." : "Run as administrator to clean this.";
                if (denied) item.Size = -1;
            }
            else
            {
                item.CanClean = true;
                item.IsChecked = wanted;
            }
            return true;
        }

        private static List<CleanItem> LoadInstallers(CancellationToken ct)
        {
            var roots = new List<string>();
            string downloads = Native.KnownFolder(Native.FOLDERID_Downloads);
            if (downloads != null) roots.Add(downloads.TrimEnd('\\'));
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop)) roots.Add(desktop.TrimEnd('\\'));

            var ext = new HashSet<string>(InstallerExt, StringComparer.OrdinalIgnoreCase);
            var items = new List<CleanItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!FileWalker.DirectoryExists(root)) continue;
                int rootDepth = root.Count(c => c == '\\');
                FileWalker.Walk(root, ct, new WalkStats(),
                    delegate (string path, ref Native.WIN32_FIND_DATA d)
                    {
                        if (!ext.Contains(Path.GetExtension(path)) || !seen.Add(path)) return;
                        var item = FileItem(Path.GetFileName(path), path, false, false, null);
                        item.Size = d.Size;
                        item.Detail = Path.GetDirectoryName(path) + "  ·  " + d.LastWrite.ToString("d MMM yyyy");
                        items.Add(item);
                    },
                    // Look up to 3 folders deep; skip node_modules and similar project folders.
                    dir => dir.Count(c => c == '\\') - rootDepth <= 3
                        && !dir.EndsWith(@"\node_modules", StringComparison.OrdinalIgnoreCase)
                        && !dir.EndsWith(@"\.git", StringComparison.OrdinalIgnoreCase),
                    null);
            }
            return items.OrderByDescending(i => i.Size).ToList();
        }

        private static List<CleanItem> LoadRecycleBin()
        {
            var info = new Native.SHQUERYRBINFO();
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
            if (Native.SHQueryRecycleBinW(null, ref info) != 0) return new List<CleanItem>();
            var item = new CleanItem
            {
                Name = "Recycle Bin (all drives)",
                Mode = CleanMode.RecycleBin,
                Detail = info.i64NumItems + (info.i64NumItems == 1 ? " item" : " items"),
                Note = "Emptying the Recycle Bin is permanent.",
                NoteIsWarning = true
            };
            item.Size = info.i64Size;
            item.CanClean = info.i64NumItems > 0;
            return new List<CleanItem> { item };
        }

        private static List<CleanItem> LoadBrowsers(CancellationToken ct, bool isAdmin)
        {
            var items = new List<CleanItem>();
            AddBrowser(items, ct, isAdmin, "Google Chrome", Path.Combine(Local, @"Google\Chrome\User Data"), "chrome");
            AddBrowser(items, ct, isAdmin, "Microsoft Edge", Path.Combine(Local, @"Microsoft\Edge\User Data"), "msedge");
            return items;
        }

        // Cache-only folders. Nothing that holds logins, cookies, history, extensions or settings.
        private static readonly string[] ProfileCacheDirs = { "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache" };
        private static readonly string[] UserDataCacheDirs = { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" };

        private static void AddBrowser(List<CleanItem> items, CancellationToken ct, bool isAdmin, string name, string userData, string process)
        {
            if (!FileWalker.DirectoryExists(userData)) return;
            var paths = new List<string>();
            int profiles = 0;
            foreach (var child in FileWalker.ListChildren(userData))
            {
                if (!child.IsDirectory || child.IsReparsePoint) continue;
                // A profile folder is one that has a Preferences file.
                if (FileWalker.FileSize(Path.Combine(child.Path, "Preferences")) < 0) continue;
                profiles++;
                foreach (string c in ProfileCacheDirs) paths.Add(Path.Combine(child.Path, c));
            }
            foreach (string c in UserDataCacheDirs) paths.Add(Path.Combine(userData, c));

            var item = Folders(name, true, false, null, paths.Where(FileWalker.DirectoryExists).ToArray());
            if (item.Paths.Count == 0) return;
            item.Detail = userData + "  ·  " + profiles + (profiles == 1 ? " profile" : " profiles");
            if (!MeasureItem(item, ct, isAdmin)) return;
            if (Process.GetProcessesByName(process).Length > 0)
            {
                item.Note = name + " is running. Close it first: files in use are skipped.";
                item.NoteIsWarning = true;
            }
            items.Add(item);
        }

        private static List<CleanItem> LoadDevCaches(CancellationToken ct, bool isAdmin)
        {
            string gradleHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME");
            if (string.IsNullOrEmpty(gradleHome)) gradleHome = Path.Combine(Home, ".gradle");

            var pnpmStores = new List<string> { Path.Combine(Local, @"pnpm\store"), Path.Combine(Local, "pnpm-store") };
            foreach (var drive in DriveInfoSafe())
                pnpmStores.Add(Path.Combine(drive, ".pnpm-store"));

            return Measure(ct, isAdmin,
                Folders("npm cache", true, false, "Re-downloaded on the next npm install.",
                    Path.Combine(Local, "npm-cache"), Path.Combine(Roaming, "npm-cache")),
                Folders("pnpm store", false, false,
                    "Projects hard-link into this store, so deleting it only frees space for packages no project still uses. Gentler option: run  pnpm store prune.",
                    pnpmStores.ToArray()),
                Folder("Gradle caches", Path.Combine(gradleHome, "caches"), true, false, "Re-downloaded on the next Gradle build."),
                Folders("NuGet packages & HTTP cache", true, false, "Restored on the next dotnet restore or build.",
                    Path.Combine(Home, @".nuget\packages"), Path.Combine(Local, @"NuGet\v3-cache"), Path.Combine(Local, @"NuGet\Cache"),
                    Path.Combine(Local, @"NuGet\plugins-cache")),
                Folder("pip cache", Path.Combine(Local, @"pip\Cache"), true, false, null),
                Folders("Yarn cache", true, false, null, Path.Combine(Local, @"Yarn\Cache"), Path.Combine(Local, @"Yarn\Berry\cache")));
        }

        private static List<CleanItem> LoadAndroid(CancellationToken ct, bool isAdmin)
        {
            var sdks = new List<string> { Path.Combine(Local, @"Android\Sdk") };
            foreach (string v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            {
                string e = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(e)) sdks.Add(e.TrimEnd('\\'));
            }
            var paths = new List<string>();
            foreach (string sdk in sdks.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(Path.Combine(sdk, ".downloadIntermediates"));
                paths.Add(Path.Combine(sdk, ".temp"));
            }
            return Measure(ct, isAdmin, Folders("SDK manager downloads", true, false, null, paths.ToArray()));
        }

        private static List<CleanItem> LoadSystemFiles()
        {
            var items = new List<CleanItem>();
            foreach (string drive in DriveInfoSafe())
            {
                foreach (string f in new[] { "hiberfil.sys", "pagefile.sys", "swapfile.sys" })
                {
                    string path = drive + f;
                    long size = FileWalker.FileSize(path);
                    if (size < 0) continue;
                    var item = new CleanItem { Name = f, Mode = CleanMode.InfoOnly, Detail = path };
                    item.Paths.Add(path);
                    item.Size = size;
                    item.CanClean = false;
                    items.Add(item);
                }
            }
            return items.OrderByDescending(i => i.Size).ToList();
        }

        /// <summary>Root paths ("C:\") of ready fixed drives.</summary>
        public static List<string> DriveInfoSafe()
        {
            var list = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(d.RootDirectory.FullName);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return list;
        }
    }
}
