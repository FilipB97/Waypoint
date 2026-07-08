using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RdpManager
{
    /// <summary>
    /// Cienki wrapper na Windows Credential Manager (advapi32). Hasła trzymamy w systemowym
    /// sejfie bieżącego użytkownika Windows (DPAPI pod spodem), nigdy w plikach aplikacji — to samo
    /// miejsce, z którego korzysta mstsc. Persist=LOCAL_MACHINE (nie SESSION) — wpis przeżywa wylogowanie
    /// i restart, ale w odróżnieniu od ENTERPRISE nie roami się z profilem na inny komputer; nadal jest
    /// widoczny wyłącznie dla konta, które go zapisało, nie dla innych użytkowników tej maszyny (A11 z przeglądu).
    /// </summary>
    public static class CredentialStore
    {
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        public static void Save(string target, string username, string password)
        {
            if (!TrySave(target, username, password))
                throw new InvalidOperationException("CredWrite nie powiódł się.");
        }

        /// <summary>Zapis do sejfu BEZ wyjątku: true = zapisano, false = CredWrite odmówił. Wołający musi
        /// obsłużyć false i ostrzec użytkownika — dotąd Save rzucał wyjątek, który globalny handler po cichu
        /// połykał (hasło nie trafiało do sejfu, a metoda bywała przerwana przed zapisem metadanych).</summary>
        public static bool TrySave(string target, string username, string password)
        {
            byte[] blob = Encoding.Unicode.GetBytes(password ?? "");
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blob.Length > 0 ? Marshal.AllocCoTaskMem(blob.Length) : IntPtr.Zero,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = string.IsNullOrEmpty(username) ? target : username
            };
            try
            {
                if (blob.Length > 0) Marshal.Copy(blob, 0, cred.CredentialBlob, blob.Length);
                return CredWrite(ref cred, 0);
            }
            finally
            {
                if (cred.CredentialBlob != IntPtr.Zero) Marshal.FreeCoTaskMem(cred.CredentialBlob);
            }
        }

        public static bool TryRead(string target, out string password)
        {
            password = null;
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr handle)) return false;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
                if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                {
                    byte[] blob = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
                    password = Encoding.Unicode.GetString(blob);
                }
                else
                {
                    password = "";
                }
                return true;
            }
            finally
            {
                CredFree(handle);
            }
        }

        public static void Delete(string target)
        {
            CredDelete(target, CRED_TYPE_GENERIC, 0);   // ignorujemy wynik: brak wpisu to nie błąd
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr cred);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }
    }
}
