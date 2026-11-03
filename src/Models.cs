using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;

namespace DiskCleaner
{
    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise(params string[] names)
        {
            var h = PropertyChanged;
            if (h == null) return;
            foreach (string n in names) h(this, new PropertyChangedEventArgs(n));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action action;
        public RelayCommand(Action action) { this.action = action; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) { return true; }
        public void Execute(object parameter) { action(); }
    }

    public static class Fmt
    {
        public static string Bytes(long b)
        {
            if (b < 0) return "?";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = b;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            if (i == 0) return b + " B";
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + units[i];
        }

        public static void Reveal(string path, bool isFile)
        {
            try
            {
                if (isFile) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception) { }
        }
    }

    public enum CleanMode { Contents, Files, RecycleBin, InfoOnly }

    public class CleanItem : Notify
    {
        private bool isChecked;
        private long size = -1;
        private string note;
        private bool noteIsWarning;
        private bool canClean = true;

        public string Name { get; set; }
        public string Detail { get; set; }
        public CleanMode Mode { get; set; }
        public List<string> Paths { get; set; }
        public bool NeedsAdmin { get; set; }
        public Category Owner { get; set; }

        public CleanItem() { Paths = new List<string>(); }

        public bool IsChecked
        {
            get { return isChecked; }
            set
            {
                bool v = value && canClean;
                if (isChecked == v) { Raise("IsChecked"); return; }
                isChecked = v;
                Raise("IsChecked");
                if (Owner != null) Owner.OnItemChanged();
            }
        }

        public bool CanClean
        {
            get { return canClean; }
            set { canClean = value; if (!value) isChecked = false; Raise("CanClean", "IsChecked", "ShowCheckBox"); }
        }

        public bool ShowCheckBox { get { return Mode != CleanMode.InfoOnly; } }

        public long Size
        {
            get { return size; }
            set { size = value; Raise("Size", "SizeText"); }
        }

        public string SizeText { get { return size < 0 ? "-" : Fmt.Bytes(size); } }

        public string Note
        {
            get { return note; }
            set { note = value; Raise("Note", "HasNote"); }
        }

        public bool NoteIsWarning
        {
            get { return noteIsWarning; }
            set { noteIsWarning = value; Raise("NoteIsWarning"); }
        }

        public bool HasNote { get { return !string.IsNullOrEmpty(note); } }

        public bool CanOpen { get { return Mode != CleanMode.RecycleBin && Paths.Count > 0; } }

        public ICommand OpenCommand
        {
            get
            {
                return new RelayCommand(delegate
                {
                    if (Paths.Count == 0) return;
                    string p = Paths[0];
                    bool isFile = Mode == CleanMode.Files || Mode == CleanMode.InfoOnly;
                    Fmt.Reveal(p, isFile);
                });
            }
        }
    }

    public class Category : Notify
    {
        private bool isExpanded;
        private bool isScanning;
        private double share;

        public string Key { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Glyph { get; set; }
        public string HowTo { get; set; }
        public bool IsInfo { get; set; }
        public ObservableCollection<CleanItem> Items { get; private set; }

        public event EventHandler SelectionChanged;

        public Category() { Items = new ObservableCollection<CleanItem>(); }

        public bool HasHowTo { get { return !string.IsNullOrEmpty(HowTo); } }
        public bool ShowCheckBox { get { return !IsInfo; } }

        public bool IsExpanded
        {
            get { return isExpanded; }
            set { isExpanded = value; Raise("IsExpanded"); }
        }

        public bool IsScanning
        {
            get { return isScanning; }
            set { isScanning = value; Raise("IsScanning", "StatusText", "IsEmpty", "TotalText"); }
        }

        public void SetItems(IEnumerable<CleanItem> items)
        {
            Items.Clear();
            foreach (var i in items)
            {
                i.Owner = this;
                Items.Add(i);
            }
            OnItemChanged();
            Raise("IsEmpty", "TotalSize", "TotalText", "StatusText", "ItemCountText");
        }

        public long TotalSize
        {
            get { return Items.Where(i => i.Size > 0).Sum(i => i.Size); }
        }

        public long SelectedSize
        {
            get { return Items.Where(i => i.IsChecked && i.Size > 0).Sum(i => i.Size); }
        }

        public int SelectedCount { get { return Items.Count(i => i.IsChecked); } }

        public string TotalText { get { return isScanning ? "" : Fmt.Bytes(TotalSize); } }

        public bool IsEmpty { get { return !isScanning && Items.Count == 0; } }

        public string StatusText
        {
            get
            {
                if (isScanning) return "Scanning...";
                if (Items.Count == 0) return "Nothing found";
                if (IsInfo) return Items.Count + (Items.Count == 1 ? " file" : " files");
                int sel = SelectedCount;
                return sel + " of " + Items.Count + " selected";
            }
        }

        public string ItemCountText { get { return StatusText; } }

        /// <summary>Header checkbox: true when all cleanable items are checked, false when none, null otherwise.</summary>
        public bool? AllChecked
        {
            get
            {
                var cleanable = Items.Where(i => i.CanClean).ToList();
                if (cleanable.Count == 0) return false;
                int c = cleanable.Count(i => i.IsChecked);
                if (c == 0) return false;
                if (c == cleanable.Count) return true;
                return null;
            }
            set
            {
                bool v = value == true;
                foreach (var i in Items.Where(i => i.CanClean).ToList()) i.IsChecked = v;
                OnItemChanged();
            }
        }

        public bool HasCleanable { get { return Items.Any(i => i.CanClean); } }

        /// <summary>0..100, size relative to the largest category, for the overview bars.</summary>
        public double Share
        {
            get { return share; }
            set { share = value; Raise("Share"); }
        }

        public void OnItemChanged()
        {
            Raise("AllChecked", "SelectedSize", "StatusText", "ItemCountText", "HasCleanable", "TotalSize", "TotalText");
            var h = SelectionChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    public class DriveModel
    {
        public string Name { get; set; }
        public string Label { get; set; }
        public long Total { get; set; }
        public long Free { get; set; }
        public long Used { get { return Total - Free; } }
        public double UsedPercent { get { return Total > 0 ? Used * 100.0 / Total : 0; } }
        public string FreeText { get { return Fmt.Bytes(Free) + " free"; } }
        public string UsageText { get { return Fmt.Bytes(Used) + " used of " + Fmt.Bytes(Total) + " (" + UsedPercent.ToString("0") + "%)"; } }
        public string Level { get { return UsedPercent >= 90 ? "High" : UsedPercent >= 75 ? "Warn" : "Normal"; } }
        public string AccessibleText { get { return "Drive " + Name + " " + Label + ", " + FreeText + ", " + UsageText; } }
    }

    public class LargeFile : Notify
    {
        private bool isChecked;

        public string Path { get; set; }
        public string Name { get; set; }
        public string Folder { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string ProtectedReason { get; set; }
        public event EventHandler CheckedChanged;

        public string SizeText { get { return Fmt.Bytes(Size); } }
        public string ModifiedText { get { return Modified == DateTime.MinValue ? "" : Modified.ToString("d MMM yyyy"); } }
        public bool IsProtected { get { return ProtectedReason != null; } }
        public bool CanSelect { get { return ProtectedReason == null; } }

        public bool IsChecked
        {
            get { return isChecked; }
            set
            {
                isChecked = value && CanSelect;
                Raise("IsChecked");
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public ICommand RevealCommand
        {
            get { return new RelayCommand(delegate { Fmt.Reveal(Path, true); }); }
        }
    }

    /// <summary>A row in a confirm or result dialog.</summary>
    public class DialogRow
    {
        public string Name { get; set; }
        public string Detail { get; set; }
        public string Value { get; set; }
        public bool IsWarning { get; set; }
        public bool HasDetail { get { return !string.IsNullOrEmpty(Detail); } }
    }
}
