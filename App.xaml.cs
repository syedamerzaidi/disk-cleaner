using System.Windows;
using System.Windows.Threading;

namespace DiskCleaner
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner", "error.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandled;
            ThemeMode = AppSettings.LoadTheme();
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + e.Exception + Environment.NewLine);
            }
            catch (IOException) { }
            MessageBox.Show(e.Exception.Message, "Disk Cleaner: something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
