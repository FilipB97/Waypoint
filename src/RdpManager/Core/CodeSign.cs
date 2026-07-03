using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RdpManager.Core
{
    /// <summary>
    /// Weryfikacja podpisu Authenticode przy auto-aktualizacji. Model: „publisher pinning"
    /// (TOFU) — pobrana aktualizacja musi być podpisana tym samym certyfikatem, co aktualnie
    /// działająca aplikacja. To właściwy model dla certyfikatu self-signed: nie jest on w
    /// zaufanym magazynie systemowym, więc pełna walidacja łańcucha (WinVerifyTrust) i tak by
    /// go odrzuciła — liczy się natomiast, że kolejne wydanie niesie ten sam odcisk wydawcy.
    /// </summary>
    public static class CodeSign
    {
        /// <summary>Wynik porównania wydawców — do decyzji w UI i logu.</summary>
        public enum Verdict
        {
            /// <summary>Ten sam wydawca — aktualizacja zaufana.</summary>
            Match,
            /// <summary>Bieżąca aplikacja jest niepodpisana — nie ma do czego przypiąć (przepuszczamy, ale bez gwarancji).</summary>
            CurrentUnsigned,
            /// <summary>Pobrany plik jest niepodpisany, a bieżąca aplikacja podpisana — odrzuć.</summary>
            DownloadUnsigned,
            /// <summary>Oba podpisane, ale innym certyfikatem — odrzuć.</summary>
            Mismatch,
        }

        /// <summary>Odcisk (SHA-256) certyfikatu, którym podpisano plik; null gdy plik nie jest podpisany/uszkodzony.</summary>
        public static string SignerThumbprint(string path)
        {
            try
            {
                // X509Certificate.CreateFromSignedFile zwraca certyfikat podpisującego (bez walidacji łańcucha).
                using (var basic = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    return basic.GetCertHashString(HashAlgorithmName.SHA256);
            }
            catch { return null; }   // niepodpisany / uszkodzony / plik nie istnieje
        }

        /// <summary>Czy pobrana aktualizacja jest podpisana tym samym wydawcą, co bieżąca aplikacja.</summary>
        /// <param name="downloadedPath">pobrany installer (%TEMP%)</param>
        /// <param name="runningPath">bieżący plik exe (Environment.ProcessPath)</param>
        public static Verdict VerifyPublisher(string downloadedPath, string runningPath)
            => Compare(SignerThumbprint(runningPath), SignerThumbprint(downloadedPath));

        /// <summary>Czysta logika porównania odcisków — testowalna bez plików i Win32.</summary>
        public static Verdict Compare(string runningThumbprint, string downloadedThumbprint)
        {
            // Bieżąca aplikacja niepodpisana (build lokalny albo stare, jeszcze niepodpisane wydanie):
            // nie mamy się do czego przypiąć. Nie blokujemy — to ścieżka przejścia na pierwsze
            // podpisane wydanie — ale sygnalizujemy brak weryfikacji.
            if (string.IsNullOrEmpty(runningThumbprint)) return Verdict.CurrentUnsigned;

            // Jesteśmy podpisani, a pobrany plik nie — odrzuć (ktoś podsunął niepodpisany plik).
            if (string.IsNullOrEmpty(downloadedThumbprint)) return Verdict.DownloadUnsigned;

            return string.Equals(runningThumbprint, downloadedThumbprint, StringComparison.OrdinalIgnoreCase)
                ? Verdict.Match
                : Verdict.Mismatch;
        }

        /// <summary>Czy werdykt pozwala kontynuować instalację aktualizacji.</summary>
        public static bool IsAcceptable(Verdict v) => v == Verdict.Match || v == Verdict.CurrentUnsigned;
    }
}
