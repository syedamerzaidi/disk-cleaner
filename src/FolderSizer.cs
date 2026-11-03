namespace DiskCleaner
{
    public class FolderEntry : Notify
    {
        private long size = -1;
        private long files;
        private double share;
        private bool measuring = true;

        public string Path { get; set; }
        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public bool IsFilesGroup { get; set; }
        public string Glyph => IsFilesGroup ? "" : IsFolder ? "" : "";
        public bool CanOpen => IsFolder && !IsFilesGroup;

        public long Size { get => size; set { size = value; Raise(nameof(Size), nameof(SizeText)); } }
        public long Files { get => files; set { files = value; Raise(nameof(Files), nameof(DetailText)); } }
        public double Share { get => share; set { share = value; Raise(nameof(Share)); } }
        public bool Measuring { get => measuring; set { measuring = value; Raise(nameof(Measuring), nameof(SizeText), nameof(DetailText)); } }

        public string SizeText => measuring ? "..." : Fmt.Bytes(size);
        public string DetailText => measuring ? "Measuring" : IsFolder ? files.ToString("N0") + (files == 1 ? " file" : " files") : "";

        public System.Windows.Input.ICommand RevealCommand => new RelayCommand(() => Fmt.Reveal(Path, !IsFolder));
    }

    /// <summary>Measures every child of a folder in parallel, for the storage drill-down.</summary>
    public static class FolderSizer
    {
        public static List<FolderEntry> Children(string folder)
        {
            var list = new List<FolderEntry>();
            long looseBytes = 0, looseCount = 0;
            foreach (var c in FileWalker.ListChildren(folder))
            {
                if (c.IsReparsePoint) continue;
                if (c.IsDirectory)
                    list.Add(new FolderEntry { Path = c.Path, Name = System.IO.Path.GetFileName(c.Path), IsFolder = true });
                else
                {
                    looseBytes += c.Size;
                    looseCount++;
                }
            }
            if (looseCount > 0)
                list.Add(new FolderEntry
                {
                    Path = folder.TrimEnd('\\'),
                    Name = "Files in this folder",
                    IsFolder = true,
                    IsFilesGroup = true,
                    Size = looseBytes,
                    Files = looseCount,
                    Measuring = false
                });
            return list;
        }

        public static void Measure(IList<FolderEntry> entries, CancellationToken ct, Action<FolderEntry, WalkStats> done)
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6), CancellationToken = ct };
            try
            {
                Parallel.ForEach(entries.Where(e => e.Measuring), po, e =>
                {
                    var s = FileWalker.DirSize(e.Path, ct);
                    if (!ct.IsCancellationRequested) done(e, s);
                });
            }
            catch (OperationCanceledException) { }
        }
    }
}
