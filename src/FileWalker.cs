using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskCleaner
{
    public class WalkStats
    {
        public long Bytes;
        public long Files;
        public int Errors;
        public bool RootDenied;
    }

    public class DirEntry
    {
        public string Path;
        public bool IsDirectory;
        public bool IsReparsePoint;
        public long Size;
    }

    public delegate void FileVisitor(string path, ref Native.WIN32_FIND_DATA data);

    /// <summary>
    /// Iterative directory walker on FindFirstFileEx. Uses an explicit stack (no recursion),
    /// never follows junctions/symlinks, and treats access errors as counts instead of exceptions.
    /// </summary>
    public static class FileWalker
    {
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_NO_MORE_FILES = 18;

        public static void Walk(string root, CancellationToken ct, WalkStats stats, FileVisitor onFile, Func<string, bool> shouldEnter, Action<string> onDirectory)
        {
            var stack = new Stack<string>();
            stack.Push(root.TrimEnd('\\'));
            bool isRoot = true;
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) return;
                string dir = stack.Pop();
                if (onDirectory != null) onDirectory(dir);
                VisitDirectory(dir, isRoot, stats, onFile, shouldEnter, stack.Push);
                isRoot = false;
            }
        }

        /// <summary>
        /// Same walk spread over several threads sharing one work stack. onFile and onDirectory
        /// must be thread-safe. Used for whole-drive scans.
        /// </summary>
        public static WalkStats WalkParallel(string root, int threads, CancellationToken ct, FileVisitor onFile, Func<string, bool> shouldEnter, Action<string> onDirectory)
        {
            var stats = new WalkStats();
            var work = new System.Collections.Concurrent.BlockingCollection<string>(new System.Collections.Concurrent.ConcurrentStack<string>());
            int pending = 1;
            work.Add(root.TrimEnd('\\'));

            Action<string> push = sub =>
            {
                Interlocked.Increment(ref pending);
                work.Add(sub);
            };
            var workers = new Thread[Math.Max(1, threads)];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(() =>
                {
                    var local = new WalkStats();
                    try
                    {
                        foreach (string dir in work.GetConsumingEnumerable(ct))
                        {
                            try
                            {
                                if (onDirectory != null) onDirectory(dir);
                                VisitDirectory(dir, false, local, onFile, shouldEnter, push);
                            }
                            finally
                            {
                                if (Interlocked.Decrement(ref pending) == 0) work.CompleteAdding();
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    Interlocked.Add(ref stats.Bytes, local.Bytes);
                    Interlocked.Add(ref stats.Files, local.Files);
                    Interlocked.Add(ref stats.Errors, local.Errors);
                });
                workers[i].IsBackground = true;
                workers[i].Start();
            }
            foreach (var t in workers) t.Join();
            return stats;
        }

        private static void VisitDirectory(string dir, bool isRoot, WalkStats stats, FileVisitor onFile, Func<string, bool> shouldEnter, Action<string> pushDir)
        {
            Native.WIN32_FIND_DATA d;
            IntPtr h = Native.FindFirst(Native.Long(dir + @"\*"), out d);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_FILE_NOT_FOUND && err != ERROR_PATH_NOT_FOUND && err != ERROR_NO_MORE_FILES)
                {
                    stats.Errors++;
                    if (isRoot && err == ERROR_ACCESS_DENIED) stats.RootDenied = true;
                }
                return;
            }
            try
            {
                do
                {
                    string name = d.cFileName;
                    if (name == "." || name == "..") continue;
                    if (d.IsReparsePoint) continue; // never follow links; their targets are measured where they live
                    string full = dir + @"\" + name;
                    if (d.IsDirectory)
                    {
                        if (shouldEnter == null || shouldEnter(full)) pushDir(full);
                    }
                    else
                    {
                        stats.Files++;
                        stats.Bytes += d.Size;
                        if (onFile != null) onFile(full, ref d);
                    }
                } while (Native.FindNextFileW(h, out d));
            }
            finally
            {
                Native.FindClose(h);
            }
        }

        public static WalkStats DirSize(string root, CancellationToken ct)
        {
            var s = new WalkStats();
            Walk(root, ct, s, null, null, null);
            return s;
        }

        /// <summary>Immediate children of a folder (files and folders), without descending.</summary>
        public static List<DirEntry> ListChildren(string dir)
        {
            var list = new List<DirEntry>();
            Native.WIN32_FIND_DATA d;
            IntPtr h = Native.FindFirst(Native.Long(dir.TrimEnd('\\') + @"\*"), out d);
            if (h == Native.INVALID_HANDLE_VALUE) return list;
            try
            {
                do
                {
                    string name = d.cFileName;
                    if (name == "." || name == "..") continue;
                    list.Add(new DirEntry
                    {
                        Path = dir.TrimEnd('\\') + @"\" + name,
                        IsDirectory = d.IsDirectory,
                        IsReparsePoint = d.IsReparsePoint,
                        Size = d.IsDirectory ? 0 : d.Size
                    });
                } while (Native.FindNextFileW(h, out d));
            }
            finally
            {
                Native.FindClose(h);
            }
            return list;
        }

        /// <summary>Size of a single file, or -1 if missing. Works for locked files such as pagefile.sys.</summary>
        public static long FileSize(string path)
        {
            Native.WIN32_FIND_DATA d;
            IntPtr h = Native.FindFirst(Native.Long(path), out d);
            if (h == Native.INVALID_HANDLE_VALUE) return -1;
            Native.FindClose(h);
            return d.IsDirectory ? -1 : d.Size;
        }

        public static bool DirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            uint a = Native.GetFileAttributesW(Native.Long(path));
            return a != Native.INVALID_FILE_ATTRIBUTES && (a & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
        }
    }
}
