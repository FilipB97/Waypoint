using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace RdpManager
{
    public partial class App : Application
    {
        // Trzymane przez cały czas życia procesu — zwolnienie mutexa = wpuszczenie drugiej instancji.
        private static Mutex _singleInstance;

        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Jedna instancja: dwa procesy nadpisywałyby sobie servers.json/settings.json (ostatni wygrywa).
            _singleInstance = new Mutex(true, "Waypoint.SingleInstance", out bool firstInstance);
            if (!firstInstance)
            {
                var hwnd = FindWindow(null, "Waypoint");
                if (hwnd != IntPtr.Zero) { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); }
                Shutdown();
                return;
            }

            // Kontrolka ActiveX (mstscax) potrafi rzucić natywnym wyjątkiem ASYNCHRONICZNIE,
            // w pętli komunikatów (SEHException w DispatchMessage) — np. przy zmianach trybu
            // pełnoekranowego. try/catch wokół wywołania nie pomaga, bo wyjątek leci później.
            // Nie pozwalamy, by jeden kaprys kontrolki ubił wszystkie otwarte sesje:
            // logujemy do %APPDATA%\RdpManager\crash.log i jedziemy dalej.
            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash("Dispatcher", args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                LogCrash("AppDomain", args.ExceptionObject as Exception);

            // Zastosuj zapisany motyw i język ZANIM powstanie okno (bez mignięcia).
            try { var s = SettingsStore.Load(); ThemeManager.Apply(s.Theme); LocalizationManager.Apply(s.Language); } catch { }
        }

        private static void LogCrash(string source, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Dir);
                File.AppendAllText(Path.Combine(SettingsStore.Dir, "crash.log"),
                    string.Format("{0:yyyy-MM-dd HH:mm:ss}  [{1}] {2}\r\n\r\n", DateTime.Now, source, ex));
            }
            catch { /* logowanie nie może wywołać kolejnego błędu */ }
        }
    }
}
