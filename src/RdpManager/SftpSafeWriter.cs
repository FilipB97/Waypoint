using System;
using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>Jak zapis faktycznie przebiegł — pokazywane użytkownikowi, bo to nie jest obojętne.</summary>
    public enum SafeWriteMode
    {
        /// <summary>Plik tymczasowy obok prawdziwego + atomowa podmiana. Urwane połączenie nie zostawi uciętego pliku.</summary>
        AtomicReplace,
        /// <summary>Nadpisanie w miejscu. Zachowuje właściciela i uprawnienia, ale nie jest atomowe.</summary>
        InPlace
    }

    /// <summary>
    /// Zapis się nie udał. Najważniejsza informacja dla użytkownika to NIE przyczyna, tylko to, czy plik
    /// na serwerze jest cały: padnięcie przed atomową podmianą zostawia oryginał nietknięty, padnięcie
    /// w trakcie zapisu w miejscu — mogło go uciąć. Od tego zależy komunikat, a edytor w obu przypadkach
    /// zostaje otwarty z tekstem użytkownika, żeby dało się ponowić zapis.
    /// </summary>
    public sealed class SafeWriteException : Exception
    {
        public bool OriginalMayBeDamaged { get; }
        public SafeWriteException(bool originalMayBeDamaged, Exception inner)
            : base(inner?.Message, inner) { OriginalMayBeDamaged = originalMayBeDamaged; }
    }

    public sealed class SafeWriteResult
    {
        public SafeWriteMode Mode { get; set; }
        /// <summary>Dlaczego nie atomowo (null przy AtomicReplace) — klucz tłumaczenia.</summary>
        public string FallbackReasonKey { get; set; }
    }

    /// <summary>
    /// Bezpieczny zapis pliku przez SFTP. Istnieje, bo zwykły UploadFile pisze W MIEJSCU: najpierw obcina
    /// plik, potem strumieniuje treść — urwane połączenie w trakcie zostawia na serwerze ucięty config.
    ///
    /// Strategia domyślna: plik tymczasowy w katalogu PRAWDZIWEGO pliku, uprawnienia ustawione ZANIM
    /// trafi do niego treść, a na końcu atomowa podmiana (posix-rename@openssh.com). Zachowanie
    /// sprawdzone na OpenSSH 9.6 przypadek po przypadku — patrz komentarze przy każdym warunku.
    ///
    /// Zapis w miejscu zostaje jako świadomy wybór tam, gdzie podmiana zrobiłaby więcej szkody niż
    /// pożytku: gdy zmieniłaby właściciela lub grupę pliku albo gdy nie da się utworzyć pliku obok.
    /// </summary>
    public static class SftpSafeWriter
    {
        /// <summary>
        /// Metadane pliku. Ścieżka bierze się z SftpClient.Get(...).FullName, czyli PO realpath — SSH.NET
        /// rozwiązuje dowiązania, więc GetAttributes nie widzi, że ścieżka była dowiązaniem (sprawdzone:
        /// IsSymbolicLink == false dla dowiązania). Zamiast wykrywać dowiązania, bierzemy więc od razu
        /// ścieżkę prawdziwego pliku i dalej pracujemy wyłącznie na niej.
        /// </summary>
        public static RemoteFileInfo Stat(SftpClient c, string path)
        {
            var f = c.Get(path);
            var a = f.Attributes;
            return new RemoteFileInfo
            {
                Path = f.FullName,
                Length = a.Size,
                ModifiedUtc = a.LastWriteTimeUtc,
                Mode = UnixMode.Compose(a.IsUIDBitSet, a.IsGroupIDBitSet, a.IsStickyBitSet,
                                        a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute,
                                        a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute,
                                        a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute),
                UserId = a.UserId,
                GroupId = a.GroupId
            };
        }

        /// <summary>Pliki tymczasowe starsze niż to są resztkami po zerwanym zapisie, a nie cudzym zapisem w toku.</summary>
        private static readonly TimeSpan StaleTemp = TimeSpan.FromMinutes(10);

        public static SafeWriteResult Write(SftpClient c, byte[] content, RemoteFileInfo original)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            string real = original.Path;
            string dir = ParentOf(real);
            string prefix = "." + NameOf(real) + ".waypoint-";
            string tmp = dir + "/" + prefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            RemoveStaleTemps(c, dir, prefix);

            // Do chwili podmiany oryginał jest nietknięty — każdy błąd wcześniej jest „bezpieczny".
            // Tylko zapis w miejscu dotyka prawdziwego pliku i tylko jego błąd może go uszkodzić.
            try { return WriteCore(c, content, original, real, tmp); }
            catch (InPlaceFailedException e) { throw new SafeWriteException(originalMayBeDamaged: e.Started, e.InnerException); }
            catch (Exception e) when (!(e is SafeWriteException)) { throw new SafeWriteException(originalMayBeDamaged: false, e); }
        }

        /// <summary>
        /// Sprząta resztki po zerwanym zapisie TEGO pliku. Połączenie, które padło w trakcie wysyłki,
        /// nie ma już jak usunąć swojego pliku tymczasowego (sprawdzone: zerwanie po 982 KB z 20 MB
        /// zostawia plik .tmp o tym rozmiarze obok nietkniętego oryginału). Tylko starsze niż 10 minut —
        /// młodszy może należeć do zapisu, który właśnie trwa w innym oknie.
        /// </summary>
        private static void RemoveStaleTemps(SftpClient c, string dir, string prefix)
        {
            try
            {
                foreach (var f in c.ListDirectory(dir))
                {
                    if (!f.IsRegularFile || !f.Name.StartsWith(prefix, StringComparison.Ordinal) || !f.Name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                    if (DateTime.UtcNow - f.LastWriteTimeUtc < StaleTemp) continue;
                    try { c.DeleteFile(f.FullName); } catch { /* cudzy plik albo brak prawa — nie nasza sprawa */ }
                }
            }
            catch { /* brak prawa do listowania katalogu nie może blokować zapisu */ }
        }

        private sealed class InPlaceFailedException : Exception
        {
            public bool Started { get; }
            public InPlaceFailedException(bool started, Exception inner) : base(inner.Message, inner) { Started = started; }
        }

        private static SafeWriteResult WriteCore(SftpClient c, byte[] content, RemoteFileInfo original, string real, string tmp)
        {

            // 1) Pusty plik tymczasowy, CreateNew — nigdy nie nadpisze czegoś, co akurat tak się nazywa.
            //    Brak prawa zapisu do KATALOGU (sam plik może być zapisywalny, np. 0666 w katalogu roota)
            //    to jedyny powód, dla którego to się nie uda — wtedy zapis w miejscu.
            try { using (c.Open(tmp, FileMode.CreateNew, FileAccess.Write)) { } }
            catch (SftpPermissionDeniedException) { return InPlace(c, content, real, "S.edit.fb.dir"); }

            bool renamed = false;
            try
            {
                var t = c.GetAttributes(tmp);

                // 2) Właściciel. Podmiana daje plik o NASZYM właścicielu. Plik roota w katalogu, do którego
                //    mamy zapis, zostałby po cichu „przejęty" — podmiana by się udała, a właściciel
                //    zmieniłby się z root na nas. Zapis w miejscu w takim pliku albo się uda z zachowaniem
                //    właściciela, albo uczciwie odmówi (brak uprawnień) — i o to chodzi.
                if (original.UserId.HasValue && t.UserId != original.UserId.Value)
                    return InPlace(c, content, real, "S.edit.fb.owner");

                // 3) Grupa. Plik wpt:web 0664 po podmianie miałby grupę wpt i serwer WWW straciłby zapis.
                //    Najpierw próba ustawienia grupy (wolno, jeśli należymy do niej); nie wyszło → w miejscu.
                if (original.GroupId.HasValue && t.GroupId != original.GroupId.Value)
                {
                    try { t.GroupId = original.GroupId.Value; c.SetAttributes(tmp, t); } catch (SshException) { }
                    if (c.GetAttributes(tmp).GroupId != original.GroupId.Value)
                        return InPlace(c, content, real, "S.edit.fb.group");
                }

                // 4) Uprawnienia ZANIM trafi do pliku treść. Plik tymczasowy powstaje z domyślną maską
                //    (zwykle 0644), więc klucz 0600 byłby przez chwilę czytelny dla wszystkich; skrypt 0755
                //    straciłby +x. Pusty plik z 0644 niczego nie ujawnia.
                if (original.Mode.HasValue) c.ChangePermissions(tmp, UnixMode.ForSshNet(original.Mode.Value));

                using (var ms = new MemoryStream(content, writable: false)) c.UploadFile(ms, tmp, canOverride: true);

                // 5) Kontrola: rozmiar musi się zgadzać, zanim zastąpimy nim prawdziwy plik.
                if (c.GetAttributes(tmp).Size != content.Length)
                    throw new IOException("Plik tymczasowy ma inny rozmiar niż zapisana treść.");

                // 6) Atomowa podmiana. Bez rozszerzenia posix-rename serwer odmówiłby nadpisania
                //    istniejącego pliku (SFTP v3) — wtedy w miejscu.
                try { c.RenameFile(tmp, real, isPosix: true); renamed = true; }
                catch (NotSupportedException) { return InPlace(c, content, real, "S.edit.fb.rename"); }
                catch (SshException) { return InPlace(c, content, real, "S.edit.fb.rename"); }

                return new SafeWriteResult { Mode = SafeWriteMode.AtomicReplace };
            }
            finally
            {
                if (!renamed) { try { c.DeleteFile(tmp); } catch { /* sprzątanie — najlepszy wysiłek */ } }
            }
        }

        // Nadpisanie w miejscu: to samo i-node, więc właściciel, grupa i uprawnienia zostają. Brak prawa
        // zapisu do samego pliku kończy się tu SftpPermissionDeniedException — wołający mówi o tym wprost.
        private static SafeWriteResult InPlace(SftpClient c, byte[] content, string real, string reasonKey)
        {
            // Odmowa otwarcia (brak prawa zapisu) pada ZANIM plik zostanie obcięty — oryginał cały.
            // Każdy błąd po otwarciu mógł go już uciąć.
            SftpFileStream fs;
            try { fs = c.Open(real, FileMode.Open, FileAccess.Write); }
            catch (Exception e) { throw new InPlaceFailedException(started: false, e); }
            try
            {
                using (fs)
                {
                    fs.SetLength(0);
                    fs.Write(content, 0, content.Length);
                    fs.Flush();
                }
            }
            catch (Exception e) { throw new InPlaceFailedException(started: true, e); }
            return new SafeWriteResult { Mode = SafeWriteMode.InPlace, FallbackReasonKey = reasonKey };
        }

        private static string ParentOf(string p)
        {
            int i = p.LastIndexOf('/');
            return i <= 0 ? "/" : p.Substring(0, i);
        }

        private static string NameOf(string p)
        {
            int i = p.LastIndexOf('/');
            return i < 0 ? p : p.Substring(i + 1);
        }
    }
}
