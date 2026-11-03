namespace DiskCleaner
{
    public class LargeScanProgress
    {
        public long Folders;
        public long Files;
        public string CurrentFolder;
    }

    public static class LargeFileFinder
    {
        // Folders that are never readable or never hold user files worth listing.
        private static readonly string[] SkipNames = { "System Volume Information", "$Recycle.Bin", "$WinREAgent", "Config.Msi" };

        public static WalkStats Find(string root, long minBytes, CancellationToken ct, LargeScanProgress progress, Action<LargeFile> found)
        {
            int threads = Math.Clamp(Environment.ProcessorCount, 2, 8);
            return FileWalker.WalkParallel(root, threads, ct,
                (string path, ref Native.WIN32_FIND_DATA d) =>
                {
                    Interlocked.Increment(ref progress.Files);
                    if (d.Size < minBytes) return;
                    found(new LargeFile
                    {
                        Path = path,
                        Name = Path.GetFileName(path),
                        Folder = Path.GetDirectoryName(path),
                        Size = d.Size,
                        Modified = d.LastWrite,
                        ProtectedReason = Safety.ProtectedReason(path, d.dwFileAttributes)
                    });
                },
                dir =>
                {
                    string name = Path.GetFileName(dir);
                    foreach (string s in SkipNames)
                        if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return false;
                    return true;
                },
                dir =>
                {
                    Interlocked.Increment(ref progress.Folders);
                    progress.CurrentFolder = dir;
                });
        }

        /// <summary>Parses "500", "500 MB", "1.5 GB", "2g". A bare number means megabytes.</summary>
        public static long ParseSize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            string t = text.Trim().ToUpperInvariant().Replace(" ", "");
            long mult = 1024L * 1024;
            if (t.EndsWith("TB") || t.EndsWith("T")) mult = 1024L * 1024 * 1024 * 1024;
            else if (t.EndsWith("GB") || t.EndsWith("G")) mult = 1024L * 1024 * 1024;
            else if (t.EndsWith("KB") || t.EndsWith("K")) mult = 1024;
            t = t.TrimEnd('B', 'K', 'M', 'G', 'T');
            if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v <= 0)
                return -1;
            return (long)(v * mult);
        }
    }
}
