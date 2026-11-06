using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Eksport haseł do pliku dla Maca (docs/specs/password-export.md). Te same przypadki co
    // PasswordExportTests w wersji macOS, w tym wspólny wektor z Node `crypto`.
    public class PasswordExportTests
    {
        private const int Fast = 1000;
        private const string FilePassword = "dobre-haslo";
        private const string VectorPassword = "zażółć gęślą jaźń";

        private static string VectorPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Nie znaleziono katalogu repozytorium (RdpManager.sln) powyżej " + AppContext.BaseDirectory);
            return Path.Combine(dir.FullName, "tests", "fixtures", "password-export", "vector-v1.json");
        }

        private static string Sample()
            => PasswordExport.Encrypt(new Dictionary<string, string> { ["s1"] = "tajne" }, FilePassword, Fast);

        private static string Edit(string json, Action<JsonObject> change)
        {
            var obj = JsonNode.Parse(json).AsObject();
            change(obj);
            return obj.ToJsonString();
        }

        private static PasswordExportFailure FailureOf(string json, string password)
        {
            var ex = Assert.Throws<PasswordExportException>(() => PasswordExport.Decrypt(json, password));
            return ex.Reason;
        }

        // Kryterium 2: wektor z niezależnej implementacji

        [Fact]
        public void K2_VectorFromNodeDecrypts()
        {
            var json = File.ReadAllText(VectorPath());
            var result = PasswordExport.Decrypt(json, VectorPassword);

            Assert.Equal(2, result.Count);
            Assert.Equal("Hasło-Żółw#1", result["srv-0001"]);
            Assert.Equal("zażółć", result["profile:prof-0001"]);
        }

        [Fact]
        public void K2_VectorFromNodeWithWrongPassword()
        {
            var json = File.ReadAllText(VectorPath());
            Assert.Equal(PasswordExportFailure.WrongPassword, FailureOf(json, "zazolc gesla jazn"));
        }

        // Kryterium 1: zaszyfrowane odszyfrowuje się tym samym hasłem

        [Fact]
        public void K1_RoundTripWithPolishCharacters()
        {
            var passwords = new Dictionary<string, string>
            {
                ["abc123"] = "Hasło-Żółw#1",
                ["profile:p1"] = "zażółć gęślą jaźń",
                ["x"] = "\"\\/\n",
            };
            var json = PasswordExport.Encrypt(passwords, "ąęśćżźńół!", Fast);
            var result = PasswordExport.Decrypt(json, "ąęśćżźńół!");

            Assert.Equal(passwords.Count, result.Count);
            foreach (var kv in passwords)
                Assert.Equal(kv.Value, result[kv.Key]);
        }

        [Fact]
        public void K1_RoundTripManyPasswords()
        {
            var passwords = new Dictionary<string, string>();
            for (int i = 0; i < 2000; i++)
                passwords["srv-" + i] = new string('ż', i % 50 + 1);
            var result = PasswordExport.Decrypt(PasswordExport.Encrypt(passwords, FilePassword, Fast), FilePassword);

            Assert.Equal(passwords.Count, result.Count);
            foreach (var kv in passwords)
                Assert.Equal(kv.Value, result[kv.Key]);
        }

        [Fact]
        public void K1_FileFieldsMatchSpec()
        {
            var obj = JsonNode.Parse(Sample()).AsObject();

            Assert.Equal("waypoint-passwords", (string)obj["Format"]);
            Assert.Equal(1, (int)obj["Version"]);
            Assert.Equal("PBKDF2-SHA256", (string)obj["Kdf"]);
            Assert.Equal(Fast, (int)obj["Iterations"]);
            Assert.Equal(16, Convert.FromBase64String((string)obj["Salt"]).Length);
            Assert.Equal(12, Convert.FromBase64String((string)obj["Nonce"]).Length);
            Assert.True(Convert.FromBase64String((string)obj["Data"]).Length > 16, "szyfrogram + tag 16 bajtów");
        }

        [Fact]
        public void K1_DefaultIterationsIs600000()
        {
            var json = PasswordExport.Encrypt(new Dictionary<string, string> { ["s1"] = "x" }, FilePassword);
            Assert.Equal(600000, (int)JsonNode.Parse(json)["Iterations"]);
        }

        // Ten sam nonce z tym samym kluczem łamie AES-GCM: sól i nonce muszą być losowe.
        [Fact]
        public void K1_EachExportHasFreshSaltAndNonce()
        {
            var a = JsonNode.Parse(Sample());
            var b = JsonNode.Parse(Sample());
            Assert.NotEqual((string)a["Salt"], (string)b["Salt"]);
            Assert.NotEqual((string)a["Nonce"], (string)b["Nonce"]);
        }

        // Kryterium 3: czytelne błędy bez awarii

        [Fact]
        public void K3_WrongPassword()
        {
            Assert.Equal(PasswordExportFailure.WrongPassword, FailureOf(Sample(), "zle-haslo"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void K3_TamperedDataByte(int position)
        {
            var original = Sample();
            var payload = Convert.FromBase64String((string)JsonNode.Parse(original)["Data"]);
            int index = position >= 0 ? position : payload.Length + position;
            payload[index] ^= 0x01;
            var tampered = Edit(original, o => o["Data"] = Convert.ToBase64String(payload));

            Assert.Equal(PasswordExportFailure.WrongPassword, FailureOf(tampered, FilePassword));
        }

        [Fact]
        public void K3_TruncatedData()
        {
            var original = Sample();
            var payload = Convert.FromBase64String((string)JsonNode.Parse(original)["Data"]);
            Array.Resize(ref payload, payload.Length - 1);
            var truncated = Edit(original, o => o["Data"] = Convert.ToBase64String(payload));

            Assert.Equal(PasswordExportFailure.WrongPassword, FailureOf(truncated, FilePassword));
        }

        [Fact]
        public void K3_ForeignFormat()
        {
            var json = Edit(Sample(), o => o["Format"] = "keepass");
            Assert.Equal(PasswordExportFailure.Unrecognized, FailureOf(json, FilePassword));
        }

        [Fact]
        public void K3_OtherVersion()
        {
            var json = Edit(Sample(), o => o["Version"] = 2);
            Assert.Equal(PasswordExportFailure.Unrecognized, FailureOf(json, FilePassword));
        }

        [Fact]
        public void K3_OtherKdf()
        {
            var json = Edit(Sample(), o => o["Kdf"] = "scrypt");
            Assert.Equal(PasswordExportFailure.Unrecognized, FailureOf(json, FilePassword));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(10000001)]
        [InlineData(20000000)]
        public void K3_IterationsOutOfRange(int iterations)
        {
            var json = Edit(Sample(), o => o["Iterations"] = iterations);
            Assert.Equal(PasswordExportFailure.Unrecognized, FailureOf(json, FilePassword));
        }

        [Fact]
        public void K3_MissingField()
        {
            var json = Edit(Sample(), o => o.Remove("Salt"));
            Assert.Equal(PasswordExportFailure.Unrecognized, FailureOf(json, FilePassword));
        }

        [Theory]
        [InlineData("")]
        [InlineData("nie json")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"Passwords\":{}}")]
        public void K3_NotAPasswordFile(string text)
        {
            Assert.Equal(PasswordExportFailure.Unrecognized, FailureOf(text, FilePassword));
        }

        // Kryterium 5: eksport pomija serwery bez SavePassword i puste hasła

        [Fact]
        public void K5_CollectSkipsServersWithoutSavePasswordAndEmptyPasswords()
        {
            var saved = new ServerInfo { Name = "web", Host = "10.0.0.1", SavePassword = true };
            var notSaved = new ServerInfo { Name = "db", Host = "10.0.0.2", SavePassword = false };
            var savedEmpty = new ServerInfo { Name = "pusty", Host = "10.0.0.3", SavePassword = true };
            var savedMissing = new ServerInfo { Name = "brak", Host = "10.0.0.4", SavePassword = true };
            var profile = new CredentialProfile { Name = "Domena", Username = "admin" };
            var profileEmpty = new CredentialProfile { Name = "Pusty", Username = "x" };
            var profileMissing = new CredentialProfile { Name = "Brak", Username = "y" };

            var store = new Dictionary<string, string>
            {
                [saved.CredTarget] = "Hasło-Żółw#1",
                [notSaved.CredTarget] = "stare-haslo",
                [savedEmpty.CredTarget] = "",
                [profile.CredTarget] = "zażółć",
                [profileEmpty.CredTarget] = "",
            };
            Func<string, string> read = target => store.TryGetValue(target, out var v) ? v : null;

            var result = PasswordExport.Collect(
                new[] { saved, notSaved, savedEmpty, savedMissing },
                new[] { profile, profileEmpty, profileMissing },
                read);

            Assert.Equal(2, result.Count);
            Assert.Equal("Hasło-Żółw#1", result[saved.Id]);
            Assert.Equal("zażółć", result["profile:" + profile.Id]);
        }

        [Fact]
        public void K5_CollectWithNothingReturnsEmpty()
        {
            var server = new ServerInfo { Name = "web", Host = "h", SavePassword = false };
            var result = PasswordExport.Collect(new[] { server }, new CredentialProfile[0], target => "x");
            Assert.Empty(result);
        }

        // Klucze z Collect to konta Pęku kluczy na Macu: cała ścieżka Windows -> plik -> odczyt.
        [Fact]
        public void K1_CollectThenEncryptThenDecrypt()
        {
            var server = new ServerInfo { Id = "srv-0001", Name = "web", Host = "h", SavePassword = true };
            var profile = new CredentialProfile { Id = "prof-0001", Name = "p" };
            var store = new Dictionary<string, string>
            {
                ["RdpManager:srv-0001"] = "Hasło-Żółw#1",
                ["RdpManager:profile:prof-0001"] = "zażółć",
            };
            var collected = PasswordExport.Collect(new[] { server }, new[] { profile },
                                                   target => store.TryGetValue(target, out var v) ? v : null);

            var result = PasswordExport.Decrypt(PasswordExport.Encrypt(collected, VectorPassword, Fast), VectorPassword);

            Assert.Equal(2, result.Count);
            Assert.Equal("Hasło-Żółw#1", result["srv-0001"]);
            Assert.Equal("zażółć", result["profile:prof-0001"]);
        }
    }
}
