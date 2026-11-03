using System;
using System.Collections.Generic;
using System.IO;

namespace DiskCleaner
{
    /// <summary>Guards that keep the app away from system-critical files and folders.</summary>
    public static class Safety
    {
        private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
        private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
        // Per-user installs (Docker Desktop, VS Code, ...) live here.
        private static readonly string UserPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        private static readonly string ProgramData =Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\');

        private static readonly HashSet<string> SystemRootFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "BOOTSECT.BAK"
        };

        private static readonly HashSet<string> VirtualDiskExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".vhdx", ".vhd", ".avhdx", ".vmdk", ".vdi", ".qcow2"
        };

        private static List<string> criticalFolders;

        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            return path.Equals(root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Why a file found by the large-file finder must not be deleted, or null when it may be.</summary>
        public static string ProtectedReason(string path, uint attributes)
        {
            string name = Path.GetFileName(path);
            string parent = Path.GetDirectoryName(path);
            bool atDriveRoot = parent != null && parent.Length <= 3;

            if (atDriveRoot && (SystemRootFiles.Contains(name) || name.StartsWith("DumpStack", StringComparison.OrdinalIgnoreCase)))
                return "Windows system file";
            if (IsUnder(path, WindowsDir)) return "Windows system file";
            if (path.IndexOf(@"\System Volume Information", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Windows system file";
            if (VirtualDiskExt.Contains(Path.GetExtension(path)))
                return "Virtual disk in use by Docker, WSL or a VM. Shrink it from that app (see Docker page)";
            if (IsUnder(path, ProgramFiles) || IsUnder(path, ProgramFilesX86) || IsUnder(path, UserPrograms))
                return "Installed app. Uninstall it from Settings > Apps instead";
            if (IsUnder(path, ProgramData))
                return "Shared app data. Remove it through the owning app";
            if ((attributes & Native.FILE_ATTRIBUTE_SYSTEM) != 0)
                return "Marked as a system file";
            return null;
        }

        /// <summary>
        /// Folders that must never be deleted, nor have their whole contents wiped.
        /// Used as the last check before any delete.
        /// </summary>
        public static bool IsCriticalFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string p = path.TrimEnd('\\');
            if (p.Length <= 2) return true; // "C:" drive root
            if (criticalFolders == null)
            {
                var list = new List<string>();
                Environment.SpecialFolder[] specials =
                {
                    Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                    Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                    Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                    Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos
                };
                foreach (var s in specials)
                {
                    string f = Environment.GetFolderPath(s);
                    if (!string.IsNullOrEmpty(f)) list.Add(f.TrimEnd('\\'));
                }
                string downloads = Native.KnownFolder(Native.FOLDERID_Downloads);
                if (downloads != null) list.Add(downloads.TrimEnd('\\'));
                string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (users != null) list.Add(users.TrimEnd('\\'));
                criticalFolders = list;
            }
            foreach (string c in criticalFolders)
                if (p.Equals(c, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
