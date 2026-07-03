using System;
using System.Diagnostics;
using System.Windows;
using RdpManager.Core;

namespace RdpManager
{
    public partial class AboutWindow
    {
        public AboutWindow()
        {
            InitializeComponent();
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = "v" + v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
            DataPathText.Text = LocalizationManager.S("S.msg.about.datafolder") + " " + SettingsStore.Dir;
        }

        private void Github_Click(object sender, RoutedEventArgs e) => Open("https://github.com/FilipB97");
        private void Repo_Click(object sender, RoutedEventArgs e) => Open("https://github.com/FilipB97/Waypoint");
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* brak przeglądarki — ignoruj */ }
        }
    }
}
