using System;

namespace RdpManager.Core
{
    /// <summary>
    /// Metadane pojedynczego pliku zdalnego, potrzebne do BEZPIECZNEJ edycji: żeby zapis nie zgubił
    /// uprawnień ani właściciela i żeby wykrył, że ktoś zmienił plik w międzyczasie.
    /// Pola nullowalne — FTP ich nie zna, a zapis ma wtedy działać, tylko ostrożniej.
    /// </summary>
    public sealed class RemoteFileInfo
    {
        /// <summary>
        /// Ścieżka PO rozwiązaniu dowiązań symbolicznych. To w katalogu PRAWDZIWEGO pliku powstaje plik
        /// tymczasowy — inaczej atomowa podmiana na ścieżce dowiązania zastąpiłaby samo dowiązanie
        /// zwykłym plikiem (np. sites-enabled/default w nginx przestałby wskazywać na sites-available).
        /// </summary>
        public string Path { get; set; }
        public long Length { get; set; }
        public DateTime ModifiedUtc { get; set; }
        /// <summary>st_mode &amp; 07777; null = backend nie zna uprawnień (FTP).</summary>
        public int? Mode { get; set; }
        public int? UserId { get; set; }
        public int? GroupId { get; set; }

        /// <summary>
        /// Czy plik zmienił się na serwerze od chwili otwarcia. Czas porównywany z dokładnością do
        /// SEKUNDY — SFTP v3 przesyła mtime w pełnych sekundach, więc różnica ułamków nie jest zmianą.
        /// </summary>
        /// <summary>
        /// Czy zalogowany użytkownik (<paramref name="myUid"/>) może pisać do pliku — na ile da się to
        /// ustalić z samych bitów uprawnień. SFTP nie podaje „kim jestem", więc uid bierze się z właściciela
        /// katalogu domowego. false tylko wtedy, gdy odmowa jest PEWNA (edytor otwiera się tylko do
        /// odczytu); null, gdy rozstrzyga coś, czego nie widać (członkostwo w grupie, ACL).
        /// </summary>
        public bool? LikelyWritableBy(int? myUid)
        {
            if (Mode == null || UserId == null || myUid == null) return null;
            if (myUid.Value == 0) return true;   // root pisze wszędzie
            int m = Mode.Value;
            if (UserId.Value == myUid.Value) return (m & UnixMode.UW) != 0;
            if ((m & UnixMode.OW) != 0) return true;
            if ((m & UnixMode.GW) != 0) return null;   // może należymy do grupy
            return false;
        }

        public static bool ChangedSince(RemoteFileInfo opened, RemoteFileInfo now)
        {
            if (opened == null || now == null) return false;
            if (opened.Length != now.Length) return true;
            return Math.Abs((now.ModifiedUtc - opened.ModifiedUtc).TotalSeconds) >= 1;
        }
    }
}
