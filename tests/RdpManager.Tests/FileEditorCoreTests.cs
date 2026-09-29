using System;
using System.Text;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Logika edytora plików bez WPF i bez serwera: uprawnienia, wykrywanie zmiany pliku, format pliku
    // (kodowanie + końce linii) i wybór podświetlania. Zapis przez SFTP sprawdzany był na prawdziwym
    // OpenSSH (opis w PR) — tu jest to, co da się przypiąć testem.
    public class FileEditorCoreTests
    {
        // Tryby zawsze z ósemkowego NAPISU — literał 0755 w C# jest dziesiętny (patrz UnixMode).
        private static int Oct(string s) => Convert.ToInt32(s, 8);

        // ---------- UnixMode ----------

        [Theory]
        [InlineData("755", "rwxr-xr-x")]
        [InlineData("644", "rw-r--r--")]
        [InlineData("600", "rw-------")]
        [InlineData("4755", "rwsr-xr-x")]
        [InlineData("2775", "rwxrwsr-x")]
        [InlineData("1777", "rwxrwxrwt")]
        [InlineData("1666", "rw-rw-rwT")]
        public void Symbolic_JakLs(string octal, string expected)
            => Assert.Equal(expected, UnixMode.Symbolic(Oct(octal)));

        [Fact]
        public void Compose_DajeOsemkowe755()
        {
            int m = UnixMode.Compose(false, false, false, true, true, true, true, false, true, true, false, true);
            Assert.Equal(Oct("755"), m);
            Assert.Equal("0755", UnixMode.Octal(m));
        }

        [Theory]
        [InlineData("755", 755)]
        [InlineData("600", 600)]
        [InlineData("2775", 2775)]
        public void ForSshNet_CyfryOsemkoweJakoLiczbaDziesietna(string octal, short expected)
            => Assert.Equal(expected, UnixMode.ForSshNet(Oct(octal)));

        // ---------- RemoteFileInfo ----------

        private static RemoteFileInfo File(string mode, int uid, long len = 10, int sec = 0)
            => new RemoteFileInfo { Path = "/x", Mode = Oct(mode), UserId = uid, GroupId = uid, Length = len,
                                    ModifiedUtc = new DateTime(2026, 1, 1, 12, 0, sec, DateTimeKind.Utc) };

        [Fact]
        public void ChangedSince_RozmiarAlboPelnaSekunda()
        {
            var a = File("644", 1000);
            Assert.False(RemoteFileInfo.ChangedSince(a, File("644", 1000)));
            Assert.True(RemoteFileInfo.ChangedSince(a, File("644", 1000, len: 11)));
            Assert.True(RemoteFileInfo.ChangedSince(a, File("644", 1000, sec: 1)));
            var frac = File("644", 1000);
            frac.ModifiedUtc = frac.ModifiedUtc.AddMilliseconds(400);   // SFTP v3: ułamki to nie zmiana
            Assert.False(RemoteFileInfo.ChangedSince(a, frac));
        }

        [Fact]
        public void LikelyWritable_PlikRootaToPewnaOdmowa()
            => Assert.False(File("644", 0).LikelyWritableBy(1000));

        [Fact]
        public void LikelyWritable_WlasnyZBitemZapisu()
        {
            Assert.True(File("600", 1000).LikelyWritableBy(1000));
            Assert.False(File("444", 1000).LikelyWritableBy(1000));
        }

        [Fact]
        public void LikelyWritable_NiewiadomeGdyRozstrzygaGrupa()
        {
            Assert.Null(File("664", 33).LikelyWritableBy(1000));   // www-data:www-data 0664 — może jesteśmy w grupie
            Assert.True(File("666", 0).LikelyWritableBy(1000));
            Assert.True(File("600", 33).LikelyWritableBy(0));      // root
            Assert.Null(new RemoteFileInfo { Path = "/x" }.LikelyWritableBy(1000));   // FTP: brak danych
        }

        // ---------- TextFileFormat ----------

        private static byte[] B(params int[] b) { var r = new byte[b.Length]; for (int i = 0; i < b.Length; i++) r[i] = (byte)b[i]; return r; }

        [Fact]
        public void Crlf_EdytorDostajeLf_ZapisOdtwarzaCrlf()
        {
            var data = Encoding.ASCII.GetBytes("a\r\nb\r\n");
            var f = TextFileFormat.Detect(data, out var text);
            Assert.Equal("a\nb\n", text);
            Assert.Equal(LineEnding.CRLF, f.Eol);
            Assert.True(f.TryEncode(text + "c\n", out var bytes, out _));
            Assert.Equal("a\r\nb\r\nc\r\n", Encoding.ASCII.GetString(bytes));
        }

        [Fact]
        public void Lf_WklejonyCrlfNieWchodziDoPlikuLinuksowego()
        {
            var f = TextFileFormat.Detect(Encoding.ASCII.GetBytes("#!/bin/sh\necho\n"), out var text);
            Assert.True(f.TryEncode(text + "ls\r\n", out var bytes, out _));
            Assert.DoesNotContain((byte)'\r', bytes);
        }

        [Fact]
        public void BezZmian_TeSameBajty()
        {
            foreach (var data in new[]
            {
                Encoding.ASCII.GetBytes("x\r\ny\r\n"),
                B(0xEF, 0xBB, 0xBF, 0x61, 0x0A),                 // UTF-8 z BOM
                B(0xFF, 0xFE, 0x61, 0x00, 0x0A, 0x00),           // UTF-16 LE
                B(0x63, 0x61, 0x66, 0xE9, 0x0A),                 // Latin-1 „café"
                new UTF8Encoding(false).GetBytes("zażółć\n"),
                Array.Empty<byte>()
            })
            {
                var f = TextFileFormat.Detect(data, out var text);
                Assert.True(f.TryEncode(text, out var bytes, out _));
                Assert.True(TextFileFormat.SameBytes(data, bytes), f.EncodingName);
            }
        }

        [Fact]
        public void Latin1_ZnakSpozaKodowaniaJestZglaszany()
        {
            var f = TextFileFormat.Detect(B(0x63, 0x61, 0x66, 0xE9, 0x0A), out var text);
            Assert.Equal(TextFileFormat.Latin1, f.EncodingName);
            Assert.False(f.TryEncode(text + "ł\n", out _, out var bad));
            Assert.Equal("ł", bad);
            Assert.True(f.AsUtf8().TryEncode(text + "ł\n", out var utf8, out _));
            Assert.Equal("café\nł\n", new UTF8Encoding(false).GetString(utf8));
        }

        [Fact]
        public void MieszaneKonceLinii_PrzewazajacyWygrywa()
        {
            var f = TextFileFormat.Detect(Encoding.ASCII.GetBytes("a\r\nb\r\nc\n"), out _);
            Assert.Equal(LineEnding.CRLF, f.Eol);
            Assert.True(f.MixedEol);
            Assert.False(TextFileFormat.Detect(Encoding.ASCII.GetBytes("jedna linia"), out _).MixedEol);
        }

        // ---------- EditorLanguage ----------

        [Theory]
        [InlineData("/etc/nginx/nginx.conf", null, "ini")]
        [InlineData("docker-compose.yml", null, "yaml")]
        [InlineData("/srv/app/Dockerfile", null, "dockerfile")]
        [InlineData("Dockerfile.prod", null, "dockerfile")]
        [InlineData(".bashrc", null, "shell")]
        [InlineData(".env", null, "ini")]
        [InlineData("deploy", "#!/bin/bash -e", "shell")]
        [InlineData("tool", "#!/usr/bin/env python3", "python")]
        [InlineData("tool", "#!/usr/bin/env -S python3.12 -u", "python")]
        [InlineData("notes", "zwykły tekst", "plaintext")]
        [InlineData("appsettings.JSON", null, "json")]
        public void For_RozszerzenieNazwaShebang(string name, string first, string expected)
            => Assert.Equal(expected, EditorLanguage.For(name, first));
    }
}
