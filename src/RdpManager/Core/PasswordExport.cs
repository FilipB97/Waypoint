using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RdpManager.Models;

namespace RdpManager.Core
{
    public enum PasswordExportFailure { Unrecognized, WrongPassword }

    public class PasswordExportException : Exception
    {
        public PasswordExportFailure Reason { get; }
        public PasswordExportException(PasswordExportFailure reason) : base(reason.ToString()) { Reason = reason; }
    }

    /// <summary>
    /// Plik z hasłami do przeniesienia na Maca (docs/specs/password-export.md): PBKDF2-SHA256 + AES-256-GCM,
    /// klucz z hasła podanego przez użytkownika. Klucze słownika = cel w Credential Manager bez „RdpManager:",
    /// czyli to samo konto, którego używa Pęk kluczy w wersji na Maca.
    /// </summary>
    public static class PasswordExport
    {
        private const string FormatName = "waypoint-passwords";
        private const string KdfName = "PBKDF2-SHA256";
        private const int MaxIterations = 10_000_000;
        private const string TargetPrefix = "RdpManager:";

        private class FileData
        {
            public string Format { get; set; }
            public int Version { get; set; }
            public string Kdf { get; set; }
            public int Iterations { get; set; }
            public string Salt { get; set; }
            public string Nonce { get; set; }
            public string Data { get; set; }
        }

        private class Payload
        {
            public Dictionary<string, string> Passwords { get; set; }
        }

        public static Dictionary<string, string> Collect(IEnumerable<ServerInfo> servers, IEnumerable<CredentialProfile> profiles,
            Func<string, string> readSecret)
        {
            var targets = servers.Where(s => s.SavePassword).Select(s => s.CredTarget)
                .Concat(profiles.Select(p => p.CredTarget));
            var result = new Dictionary<string, string>();
            foreach (var target in targets)
            {
                var pw = readSecret(target);
                if (!string.IsNullOrEmpty(pw)) result[target.Substring(TargetPrefix.Length)] = pw;
            }
            return result;
        }

        public static string Encrypt(IReadOnlyDictionary<string, string> passwords, string filePassword, int iterations = 600_000)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new Payload { Passwords = passwords.ToDictionary(kv => kv.Key, kv => kv.Value) });
            byte[] data = new byte[plain.Length + 16];
            using (var aes = new AesGcm(DeriveKey(filePassword, salt, iterations), 16))
                aes.Encrypt(nonce, plain, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length));

            var file = new FileData
            {
                Format = FormatName, Version = 1, Kdf = KdfName, Iterations = iterations,
                Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce), Data = Convert.ToBase64String(data)
            };
            return JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        }

        public static Dictionary<string, string> Decrypt(string json, string filePassword)
        {
            FileData file;
            byte[] salt, nonce, data;
            try
            {
                file = JsonSerializer.Deserialize<FileData>(json);
                salt = Convert.FromBase64String(file.Salt);
                nonce = Convert.FromBase64String(file.Nonce);
                data = Convert.FromBase64String(file.Data);
            }
            catch (Exception) { throw new PasswordExportException(PasswordExportFailure.Unrecognized); }
            if (file.Format != FormatName || file.Version != 1 || file.Kdf != KdfName
                || file.Iterations < 1 || file.Iterations > MaxIterations || nonce.Length != 12 || data.Length < 16)
                throw new PasswordExportException(PasswordExportFailure.Unrecognized);

            int len = data.Length - 16;
            byte[] plain = new byte[len];
            try
            {
                using var aes = new AesGcm(DeriveKey(filePassword, salt, file.Iterations), 16);
                aes.Decrypt(nonce, data.AsSpan(0, len), data.AsSpan(len), plain);
            }
            catch (CryptographicException) { throw new PasswordExportException(PasswordExportFailure.WrongPassword); }

            try { return JsonSerializer.Deserialize<Payload>(plain)?.Passwords ?? new Dictionary<string, string>(); }
            catch (JsonException) { throw new PasswordExportException(PasswordExportFailure.Unrecognized); }
        }

        private static byte[] DeriveKey(string password, byte[] salt, int iterations)
            => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}
