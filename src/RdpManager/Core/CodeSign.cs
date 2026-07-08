using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RdpManager.Core
{
    /// <summary>
    /// Weryfikacja podpisu Authenticode przy auto-aktualizacji. Model: „publisher pinning"
    /// (TOFU) — pobrana aktualizacja musi być podpisana tym samym certyfikatem, co aktualnie
    /// działająca aplikacja. Sam odcisk certyfikatu (<see cref="SignerThumbprint"/>) tylko
    /// stwierdza, że plik GO ZAWIERA — nie że sygnatura faktycznie pokrywa treść pliku. Dlatego
    /// dodatkowo <see cref="HasValidSignature"/> woła WinVerifyTrust; certyfikat jest self-signed
    /// (nie w zaufanym magazynie systemowym), więc akceptujemy CERT_E_UNTRUSTEDROOT — to dotyczy
    /// tylko zaufania do KORZENIA, nie integralności podpisu — realne zaufanie do WYDAWCY i tak
    /// zapewnia publisher pinning w <see cref="Compare"/>.
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
            /// <summary>Pobrany plik ma certyfikat, ale sygnatura nie pokrywa jego treści (WinVerifyTrust) — odrzuć.</summary>
            DownloadTampered,
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
        {
            string downloadedThumbprint = SignerThumbprint(downloadedPath);
            // Certyfikat obecny (CreateFromSignedFile go wyciągnął), ale WinVerifyTrust mówi, że sygnatura
            // NIE pokrywa treści pliku — ktoś mógł dokleić prawidłowy certyfikat do zmodyfikowanej zawartości.
            // Sam odcisk certyfikatu by tego nie złapał (CreateFromSignedFile nie waliduje podpisu).
            if (!string.IsNullOrEmpty(downloadedThumbprint) && !HasValidSignature(downloadedPath))
                return Verdict.DownloadTampered;
            return Compare(SignerThumbprint(runningPath), downloadedThumbprint);
        }

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

        // ---------- WinVerifyTrust (Authenticode) ----------

        private const uint ERROR_SUCCESS = 0;
        private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;   // łańcuch OK, korzeń poza zaufanym magazynem (self-signed)

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_SAFER_FLAG = 0x100;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        /// <summary>Sprawdza (WinVerifyTrust), czy podpis Authenticode pliku faktycznie pokrywa jego treść —
        /// w przeciwieństwie do <see cref="SignerThumbprint"/>, który tylko WYCIĄGA certyfikat bez weryfikacji.
        /// Akceptuje CERT_E_UNTRUSTEDROOT (self-signed — oczekiwane), odrzuca każdy inny błąd (brak podpisu,
        /// zmanipulowana treść, jawna nieufność).</summary>
        public static bool HasValidSignature(string path)
        {
            IntPtr fileInfoPtr = IntPtr.Zero;
            try
            {
                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = path,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };
                fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = fileInfoPtr,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_SAFER_FLAG
                };

                uint result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);

                data.dwStateAction = WTD_STATEACTION_CLOSE;   // zwolnij uchwyt stanu (WTD_STATEACTION_VERIFY go alokuje)
                WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);

                return result == ERROR_SUCCESS || result == CERT_E_UNTRUSTEDROOT;
            }
            catch { return false; }   // wątpliwość = odmowa (bezpieczny domyślny)
            finally { if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr); }
        }
    }
}
