using System.Runtime.InteropServices;

namespace DiskCleaner
{
    public class CleanReport
    {
        public string Name { get; set; }
        public long Freed { get; set; }
        public int Removed { get; set; }
        public int Failed { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Deletes what the user confirmed. Recycle mode moves entries to the Recycle Bin through the shell;
    /// permanent mode deletes with Win32 calls. Junctions and symlinks are unlinked, never followed.
    /// </summary>
    public static class Cleaner
    {
        private const int ERROR_ACCESS_DENIED = 5;

        /// <summary>Runs work on a dedicated STA thread, which shell file operations expect.</summary>
        public static Task<T> RunSta<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }

        public static CleanReport Clean(CleanItem item, bool permanent, CancellationToken ct)
        {
            var r = new CleanReport { Name = item.Name };
            try
            {
                switch (item.Mode)
                {
                    case CleanMode.RecycleBin:
                        EmptyRecycleBin(item, r);
                        break;
                    case CleanMode.Contents:
                        foreach (string root in item.Paths) CleanContents(root, permanent, r, ct);
                        break;
                    case CleanMode.Files:
                        foreach (string f in item.Paths) DeleteOneFile(f, permanent, r);
                        break;
                    case CleanMode.InfoOnly:
                        r.Error = "Information only";
                        break;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        /// <summary>Deletes files picked in the large-file finder. Protected files are refused.</summary>
        public static CleanReport DeleteLargeFile(LargeFile file, bool permanent)
        {
            var r = new CleanReport { Name = file.Name };
            uint attrs = Native.GetFileAttributesW(Native.Long(file.Path));
            if (attrs == Native.INVALID_FILE_ATTRIBUTES)
            {
                r.Error = "File no longer exists";
                return r;
            }
            string reason = Safety.ProtectedReason(file.Path, attrs);
            if (reason != null)
            {
                r.Error = "Protected: " + reason;
                r.Failed++;
                return r;
            }
            DeleteOneFile(file.Path, permanent, r);
            return r;
        }

        private static void EmptyRecycleBin(CleanItem item, CleanReport r)
        {
            long before = item.Size;
            int hr = Native.SHEmptyRecycleBinW(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            if (hr == 0)
            {
                r.Freed = Math.Max(0, before);
                r.Removed = 1;
            }
            else if (before > 0)
            {
                r.Failed = 1;
                r.Error = "Windows could not empty the Recycle Bin (0x" + hr.ToString("X8") + ")";
            }
        }

        private static void CleanContents(string root, bool permanent, CleanReport r, CancellationToken ct)
        {
            if (!FileWalker.DirectoryExists(root)) return;
            if (Safety.IsCriticalFolder(root))
            {
                r.Error = "Refused to clean a protected folder: " + root;
                return;
            }
            foreach (var child in FileWalker.ListChildren(root))
            {
                if (ct.IsCancellationRequested) return;
                if (child.IsReparsePoint)
                {
                    // Remove the link itself; the target is left untouched.
                    bool ok = child.IsDirectory ? Native.RemoveDirectoryW(Native.Long(child.Path)) : Native.DeleteFileW(Native.Long(child.Path));
                    if (ok) r.Removed++; else r.Failed++;
                    continue;
                }
                if (permanent)
                {
                    if (child.IsDirectory) DeleteTree(child.Path, r, ct);
                    else DeleteFilePermanent(child.Path, child.Size, r);
                }
                else
                {
                    long size = child.IsDirectory ? FileWalker.DirSize(child.Path, ct).Bytes : child.Size;
                    if (Recycle(child.Path) && !Exists(child.Path))
                    {
                        r.Removed++;
                        r.Freed += size;
                    }
                    else
                    {
                        // Partly moved folders still count what actually left.
                        long left = child.IsDirectory ? FileWalker.DirSize(child.Path, ct).Bytes : (Exists(child.Path) ? size : 0);
                        r.Freed += Math.Max(0, size - left);
                        r.Failed++;
                    }
                }
            }
        }

        private static void DeleteOneFile(string path, bool permanent, CleanReport r)
        {
            string dir = Path.GetDirectoryName(path);
            if (dir == null || Safety.IsCriticalFolder(path))
            {
                r.Error = "Refused to delete a protected path";
                r.Failed++;
                return;
            }
            long size = FileWalker.FileSize(path);
            if (size < 0) return;
            if (permanent)
            {
                DeleteFilePermanent(path, size, r);
            }
            else if (Recycle(path) && !Exists(path))
            {
                r.Removed++;
                r.Freed += size;
            }
            else
            {
                r.Failed++;
            }
        }

        /// <summary>Deletes a folder tree bottom-up without recursion.</summary>
        private static void DeleteTree(string path, CleanReport r, CancellationToken ct)
        {
            var order = new List<string>();
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) return;
                string dir = stack.Pop();
                order.Add(dir);
                foreach (var child in FileWalker.ListChildren(dir))
                {
                    if (child.IsReparsePoint)
                    {
                        bool ok = child.IsDirectory ? Native.RemoveDirectoryW(Native.Long(child.Path)) : Native.DeleteFileW(Native.Long(child.Path));
                        if (!ok) r.Failed++;
                    }
                    else if (child.IsDirectory)
                    {
                        stack.Push(child.Path);
                    }
                    else
                    {
                        DeleteFilePermanent(child.Path, child.Size, r);
                    }
                }
            }
            // Parents were added before their children, so walking backwards removes children first.
            bool rootRemoved = false;
            for (int i = order.Count - 1; i >= 0; i--)
            {
                bool ok = Native.RemoveDirectoryW(Native.Long(order[i]));
                if (i == 0) rootRemoved = ok;
            }
            if (rootRemoved) r.Removed++;
        }

        private static void DeleteFilePermanent(string path, long size, CleanReport r)
        {
            string lp = Native.Long(path);
            bool ok = Native.DeleteFileW(lp);
            if (!ok && Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
            {
                uint a = Native.GetFileAttributesW(lp);
                if (a != Native.INVALID_FILE_ATTRIBUTES && (a & Native.FILE_ATTRIBUTE_READONLY) != 0)
                {
                    Native.SetFileAttributesW(lp, Native.FILE_ATTRIBUTE_NORMAL);
                    ok = Native.DeleteFileW(lp);
                }
            }
            if (ok)
            {
                r.Freed += size;
                r.Removed++;
            }
            else
            {
                r.Failed++;
            }
        }

        private static bool Recycle(string path)
        {
            // The shell API does not take \\?\ paths; very long paths fail and are reported as skipped.
            var op = new Native.SHFILEOPSTRUCT
            {
                wFunc = Native.FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_NOERRORUI | Native.FOF_SILENT | Native.FOF_WANTNUKEWARNING)
            };
            int rc = Native.SHFileOperationW(ref op);
            return rc == 0 && !op.fAnyOperationsAborted;
        }

        private static bool Exists(string path)
        {
            return Native.GetFileAttributesW(Native.Long(path)) != Native.INVALID_FILE_ATTRIBUTES;
        }
    }
}
