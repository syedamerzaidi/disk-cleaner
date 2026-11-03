using System.Windows;

namespace DiskCleaner
{
    /// <summary>Remembers the theme choice in %LOCALAPPDATA%\DiskCleaner\settings.txt.</summary>
    public static class AppSettings
    {
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner");
        private static readonly string FilePath = Path.Combine(Dir, "settings.txt");

        public static ThemeMode LoadTheme()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        if (!line.StartsWith("theme=", StringComparison.Ordinal)) continue;
                        switch (line.Substring(6).Trim())
                        {
                            case "Light": return ThemeMode.Light;
                            case "Dark": return ThemeMode.Dark;
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ThemeMode.System;
        }

        public static void SaveTheme(ThemeMode mode)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, "theme=" + mode.Value + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
