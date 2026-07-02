using System;
using System.IO;
using System.Windows;

namespace RdpManager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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
