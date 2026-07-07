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

            // Tryb podmiany po auto-aktualizacji: uruchomieni z pobranego exe (w %TEMP%). Poczekaj aż stary
            // proces zniknie (zwolni plik + mutex), podmień plik docelowy sobą i uruchom go. NIE bierzemy
            // mutexa i nie pokazujemy UI — to tylko krótki „installer".
            if (e.Args.Length >= 3 && e.Args[0] == "--apply-update")
            {
                RunUpdateBootstrap(e.Args[1], e.Args[2]);
                Shutdown();
                return;
            }

            // Jedna instancja: dwa procesy nadpisywałyby sobie servers.json/settings.json (ostatni wygrywa).
            _singleInstance = new Mutex(true, "Waypoint.SingleInstance", out bool firstInstance);
            if (!firstInstance)
            {
                var hwnd = FindWindow(null, "Waypoint");
                if (hwnd != IntPtr.Zero) { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); }
                Shutdown();
                return;
            }

            Core.PersistLog.Write(SettingsStore.Dir,
                "=== app start v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version + " ===");

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

            // Zdejmij kolorową (akcentową) obwódkę z KAŻDEGO okna FluentWindow — jednym class-handlerem,
            // zanim StartupUri utworzy MainWindow. Keep (a nie jednorazowe Neutralize) dobija ją też po
            // wyrenderowaniu i przy aktywacji — WPF-UI przemalowuje krawędź na akcent PO Loaded (kobalt z #49).
            EventManager.RegisterClassHandler(
                typeof(Wpf.Ui.Controls.FluentWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => WindowBorder.Keep(s as Window)));

            CleanupUpdateLeftovers();
        }

        // Po auto-aktualizacji zostaje pobrany exe w %TEMP% i ewentualny plik .new obok celu — posprzątaj.
        private static void CleanupUpdateLeftovers()
        {
            try
            {
                string dotNew = Environment.ProcessPath + ".new";
                if (File.Exists(dotNew)) File.Delete(dotNew);
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "Waypoint-update-*.exe"))
                {
                    try { File.Delete(f); } catch { /* może jeszcze działać (installer) — następnym razem */ }
                }
            }
            catch { /* sprzątanie jest opcjonalne */ }
        }

        // Uruchamiane z pobranego exe (%TEMP%). Czeka na wyjście starego procesu, podmienia cel i go startuje.
        private static void RunUpdateBootstrap(string targetPath, string oldPidStr)
        {
            try
            {
                if (int.TryParse(oldPidStr, out int oldPid))
                {
                    try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(20000); }
                    catch { /* już nie żyje */ }
                }

                Core.PersistLog.Write(SettingsStore.Dir, "update-bootstrap: stary proces zniknął, podmieniam " + targetPath);

                // Kopiuj obok celu, potem atomowo podmień (File.Move overwrite). Kilka prób — plik bywa
                // jeszcze chwilę zablokowany po wyjściu procesu. Gdy się nie uda, cel zostaje stary (bez szkody).
                string self = Environment.ProcessPath;
                string tmp = targetPath + ".new";
                File.Copy(self, tmp, true);
                for (int i = 0; i < 30; i++)
                {
                    try { File.Move(tmp, targetPath, true); break; }
                    catch { Thread.Sleep(300); }
                }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

                Core.PersistLog.Write(SettingsStore.Dir, "update-bootstrap: uruchamiam zaktualizowany exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(targetPath) { UseShellExecute = true });
            }
            catch (Exception ex) { LogCrash("update", ex); }
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
