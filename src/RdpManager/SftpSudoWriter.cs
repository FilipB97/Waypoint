using System;
using System.IO;
using System.Text;
using Renci.SshNet;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>
    /// Zapis przez sudo (SSH): treść przez SFTP do pliku 0600 w /tmp, polecenie z
    /// <see cref="SudoScript"/> przez SshClient (hasło na stdin), sprzątanie. Uprawnienia pliku w /tmp
    /// są ustawiane ZANIM trafi do niego treść — nikt inny jej nie przeczyta.
    /// </summary>
    public static class SftpSudoWriter
    {
        public static SudoOutcome Write(SftpClient sftp, Func<SshClient> makeSsh, byte[] content, string destination, string password)
        {
            string staging = SudoScript.StagingPath();
            using (sftp.Open(staging, FileMode.CreateNew, FileAccess.Write)) { }
            try
            {
                sftp.ChangePermissions(staging, 600);   // SSH.NET: cyfry ósemkowe jako liczba dziesiętna (patrz UnixMode)
                using (var ms = new MemoryStream(content, writable: false)) sftp.UploadFile(ms, staging, canOverride: true);

                using (var ssh = makeSsh())
                {
                    ssh.Connect();
                    using (var cmd = ssh.CreateCommand(SudoScript.RemoteCommand(staging, destination)))
                    {
                        cmd.CommandTimeout = TimeSpan.FromSeconds(60);
                        var ar = cmd.BeginExecute();
                        var input = cmd.CreateInputStream();   // SSH.NET: dopiero w trakcie wykonania
                        if (password != null)
                        {
                            var pw = Encoding.UTF8.GetBytes(password + "\n");
                            input.Write(pw, 0, pw.Length);
                        }
                        input.Dispose();   // koniec stdin — sudo nie czeka na kolejne próby hasła
                        cmd.EndExecute(ar);
                        return SudoScript.Interpret(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
                    }
                }
            }
            finally
            {
                try { sftp.DeleteFile(staging); } catch { /* sprzątanie — najlepszy wysiłek */ }
            }
        }
    }
}
