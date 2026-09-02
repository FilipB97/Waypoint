using System.Collections.Generic;
using System.Linq;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Model pulpitu. Pulpit rysuje się teraz w WebView2, więc granica „co pokazać" / „jak narysować"
    // jest jawna — i to ta pierwsza połowa jest tutaj sprawdzana. Strona dostaje gotowe liczby.
    public class DashboardModelTests
    {
        private static ServerInfo S(RemoteProtocol p, ServerStatus st = ServerStatus.Online,
                                   int lat = -1, string group = null, string name = "s")
            => new ServerInfo { Name = name, Protocol = p, Status = st, LatencyMs = lat, Group = group };

        private static DashboardModel Build(params ServerInfo[] servers)
            => DashboardModel.Build(servers, null, null, 0, "Serwery");

        [Fact]
        public void WpisyRestNieLiczaSieDoStatystykSerwerowych()
        {
            // Kolekcje REST nie są sondowane, więc zawsze wyglądałyby na niedostępne i zawyżały „offline".
            var m = Build(
                S(RemoteProtocol.Rdp, ServerStatus.Online),
                S(RemoteProtocol.Rest, ServerStatus.Offline),
                S(RemoteProtocol.Rest, ServerStatus.Offline));

            Assert.Equal(1, m.Servers);
            Assert.Equal(0, m.Offline);
            Assert.DoesNotContain(m.Protocols, p => p.Name == "REST");
        }

        [Fact]
        public void LiczbyStatusowSumujaSieDoLiczbySerwerow()
        {
            var m = Build(
                S(RemoteProtocol.Rdp, ServerStatus.Online),
                S(RemoteProtocol.Ssh, ServerStatus.Idle),
                S(RemoteProtocol.Ftp, ServerStatus.Offline),
                S(RemoteProtocol.Ftp, ServerStatus.Offline));

            Assert.Equal(4, m.Servers);
            Assert.Equal(m.Servers, m.Online + m.Idle + m.Offline);
        }

        [Fact]
        public void SrednieOpoznienieLiczyTylkoOsiagalne()
        {
            // LatencyMs = -1 znaczy „nie zmierzono" (sonda zwraca to przy porażce) — wliczenie tego
            // zaniżałoby średnią do bezsensu.
            var m = Build(
                S(RemoteProtocol.Rdp, ServerStatus.Online, lat: 10),
                S(RemoteProtocol.Rdp, ServerStatus.Online, lat: 30),
                S(RemoteProtocol.Rdp, ServerStatus.Offline, lat: -1));

            Assert.Equal(20, m.AvgLatency);
        }

        [Fact]
        public void BrakPomiarowDajeMinusJedenANieZero()
        {
            // Zero milisekund to poprawny wynik („<1 ms"), więc brak danych musi mieć własną wartość.
            var m = Build(S(RemoteProtocol.Rdp, ServerStatus.Offline, lat: -1));
            Assert.Equal(-1, m.AvgLatency);
        }

        [Fact]
        public void ProtokolySaPosortowaneMalejaco()
        {
            var m = Build(
                S(RemoteProtocol.Ssh), S(RemoteProtocol.Ftp), S(RemoteProtocol.Ftp),
                S(RemoteProtocol.Ftp), S(RemoteProtocol.Rdp), S(RemoteProtocol.Rdp));

            Assert.Equal(new[] { "FTP", "RDP", "SSH" }, m.Protocols.Select(p => p.Name));
            Assert.Equal(new[] { 3, 2, 1 }, m.Protocols.Select(p => p.Count));
        }

        [Fact]
        public void SumaProtokolowRownaSieLiczbieSerwerow()
        {
            var m = Build(S(RemoteProtocol.Rdp), S(RemoteProtocol.Vnc), S(RemoteProtocol.Serial));
            Assert.Equal(m.Servers, m.Protocols.Sum(p => p.Count));
        }

        [Theory]
        [InlineData(RemoteProtocol.Rdp, "ProtoRdp")]
        [InlineData(RemoteProtocol.Vnc, "ProtoRdp")]     // jak MainWindow.ProtocolBrush
        [InlineData(RemoteProtocol.Ssh, "ProtoSsh")]
        [InlineData(RemoteProtocol.Sftp, "ProtoSftp")]
        [InlineData(RemoteProtocol.Ftp, "ProtoSftp")]
        [InlineData(RemoteProtocol.Http, "ProtoWeb")]
        [InlineData(RemoteProtocol.Telnet, "ProtoTelnet")]
        [InlineData(RemoteProtocol.Serial, "ProtoTelnet")]
        public void BarwaProtokoluJestTaSamaCoWResztyAplikacji(RemoteProtocol p, string key)
        {
            // Ten sam protokół nie może mieć na pulpicie innej barwy niż w liście serwerów i na karcie.
            var m = Build(S(p));
            Assert.Equal(key, m.Protocols.Single().ColorKey);
        }

        [Fact]
        public void GrupyLiczoneBezWzgleduNaWielkoscLiter()
        {
            var m = Build(
                S(RemoteProtocol.Rdp, group: "Produkcja"),
                S(RemoteProtocol.Rdp, group: "PRODUKCJA"),
                S(RemoteProtocol.Rdp, group: "Test"));

            Assert.Equal(2, m.Groups);
        }

        [Fact]
        public void SerweryBezGrupyTrafiajaDoGrupyDomyslnej()
        {
            var m = DashboardModel.Build(
                new[] { S(RemoteProtocol.Rdp, group: null), S(RemoteProtocol.Rdp, group: "  ") },
                null, null, 0, "Serwery");

            Assert.Equal(1, m.Groups);
        }

        [Fact]
        public void PustaListaNieWysypujeModelu()
        {
            var m = DashboardModel.Build(null, null, null, 0, "Serwery");

            Assert.Equal(0, m.Servers);
            Assert.Equal(-1, m.AvgLatency);
            Assert.Empty(m.Protocols);
            Assert.Empty(m.Weekday);
            Assert.Empty(m.LatencySeries);
        }

        [Fact]
        public void SerieWejscioweSaKopiowane()
        {
            // Model idzie do serializacji na innym wątku niż źródło; współdzielenie listy z serwisem
            // sondującym oznaczałoby wyjątek „kolekcja zmodyfikowana" w losowym momencie.
            var samples = new List<int> { 1, 2, 3 };
            var m = DashboardModel.Build(new[] { S(RemoteProtocol.Rdp) }, samples, new[] { 1, 2 }, 0, "g");

            samples.Add(99);

            Assert.Equal(new[] { 1, 2, 3 }, m.LatencySeries);
        }
    }
}
