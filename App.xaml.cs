using System;
using System.IO;
using System.Windows;

namespace ChatClientWpf {
    public partial class App : Application {
        public const string ThemeFile = "theme.cfg";

        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);

            string theme = "Light";
            try {
                if (File.Exists(ThemeFile)) theme = File.ReadAllText(ThemeFile).Trim();
            } catch { }

            ApplyTheme(theme);
        }

        public static void ApplyTheme(string themeName) {
            // Remove previous theme dictionaries
            for (int i = Current.Resources.MergedDictionaries.Count - 1; i >= 0; i--) {
                var md = Current.Resources.MergedDictionaries[i];
                if (md.Source != null && (md.Source.OriginalString.Contains("Themes/LightTheme.xaml") || md.Source.OriginalString.Contains("Themes/DarkTheme.xaml"))) {
                    Current.Resources.MergedDictionaries.RemoveAt(i);
                }
            }

            string uri = themeName == "Dark" ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
            var dict = new ResourceDictionary() { Source = new Uri(uri, UriKind.Relative) };
            Current.Resources.MergedDictionaries.Add(dict);
        }

        public static void SaveTheme(string themeName) {
            try {
                File.WriteAllText(ThemeFile, themeName);
            } catch { }
        }
    }
}
