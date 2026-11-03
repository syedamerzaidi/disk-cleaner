using System.Windows;

namespace DiskCleaner
{
    /// <summary>Modal dialog used to confirm a delete (with Recycle Bin / permanent choice) and to show results.</summary>
    public partial class ConfirmDialog : Window
    {
        public bool Permanent => PermanentOption.IsChecked == true;

        public ConfirmDialog(string title, string message, IEnumerable<DialogRow> rows, string total, string okText, bool showCancel = true, bool showModes = false, string modeNote = null)
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;

            var list = rows?.ToList() ?? new List<DialogRow>();
            RowList.ItemsSource = list;
            ListBorder.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            TotalText.Text = total;
            TotalRow.Visibility = string.IsNullOrEmpty(total) ? Visibility.Collapsed : Visibility.Visible;

            ModePanel.Visibility = showModes ? Visibility.Visible : Visibility.Collapsed;
            ModeNote.Text = modeNote;
            ModeNote.Visibility = string.IsNullOrEmpty(modeNote) ? Visibility.Collapsed : Visibility.Visible;

            OkButton.Content = okText;
            CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
            if (!showCancel) OkButton.IsCancel = true;

            // Destructive dialogs start on Cancel so Enter never deletes by accident.
            Loaded += (_, _) => (showCancel ? CancelButton : OkButton).Focus();
        }

        private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
