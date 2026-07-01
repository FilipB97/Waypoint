using System;
using System.IO;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Dziennik audytu połączeń (kto/kiedy/dokąd) w %APPDATA%\RdpManager\connections.log.
    /// Zapisuje tylko metadane — nigdy haseł. Wyłączalny w ustawieniach.
    /// </summary>
    public static class ConnectionLog
    {
        private static readonly string DefaultDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager");

        /// <summary>Sterowane z AppSettings.ConnectionLogEnabled (ustawiane przy starcie).</summary>
        public static bool Enabled { get; set; } = true;

        public static void Append(string ev, ServerInfo server) => Append(ev, server, DateTime.Now, DefaultDir);

        /// <summary>Wariant testowalny — jawny znacznik czasu i katalog.</summary>
        public static void Append(string ev, ServerInfo server, DateTime timestamp, string dir)
        {
            if (!Enabled) return;
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "connections.log"),
                    RdpUtils.FormatConnectionLog(timestamp, ev, server) + Environment.NewLine);
            }
            catch { /* dziennik jest best-effort — nie przerywamy pracy aplikacji */ }
        }
    }
}
