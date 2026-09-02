using System;
using System.Linq;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Znaczniki stanu w liście serwerów i na pasku kart.
    //
    // Powód istnienia: znacznik był kropką w trzech odcieniach — czyli dla osoby nierozróżniającej
    // barw JEDNYM odcieniem. WCAG 1.4.1 („Użycie koloru") wymaga drugiego nośnika; tutaj jest nim
    // kształt. Testy pilnują właśnie KSZTAŁTU, bo kolor sam z siebie niczego nie gwarantuje.
    public class StatusGlyphTests
    {
        private static readonly ServerStatus[] AllStatuses =
            (ServerStatus[])Enum.GetValues(typeof(ServerStatus));

        private static readonly SessionState[] AllStates =
            (SessionState[])Enum.GetValues(typeof(SessionState));

        [Fact]
        public void KazdyStatusSerweraMaWLASNYKsztalt()
        {
            var shapes = AllStatuses.Select(s => StatusGlyph.For(s).Shape).ToList();
            Assert.Equal(shapes.Count, shapes.Distinct().Count());
        }

        [Fact]
        public void KazdyStanSesjiMaWLASNYKsztalt()
        {
            var shapes = AllStates.Select(s => StatusGlyph.For(s).Shape).ToList();
            Assert.Equal(shapes.Count, shapes.Distinct().Count());
        }

        [Fact]
        public void StatusSerweraZawszeCosRysuje()
        {
            // Lista serwerów pokazuje osiągalność KAŻDEGO wpisu — pusta komórka znaczyłaby „nie wiem",
            // a takiego stanu nie ma. Milczenie jest zarezerwowane dla sesji (patrz niżej).
            foreach (var s in AllStatuses)
                Assert.NotEqual(GlyphShape.None, StatusGlyph.For(s).Shape);
        }

        [Fact]
        public void SesjaPolaczonaNieMaZnacznika()
        {
            // Sedno uspokojenia paska kart: przy sześciu zdrowych sesjach znika sześć sygnałów.
            Assert.Equal(GlyphShape.None, StatusGlyph.For(SessionState.Connected).Shape);
            Assert.Null(StatusGlyph.For(SessionState.Connected).ColorKey);
        }

        [Fact]
        public void KazdyStanWymagajacyUwagiNiesieKsztaltIKolor()
        {
            foreach (var st in AllStates.Where(x => x != SessionState.Connected))
            {
                var (shape, key) = StatusGlyph.For(st);
                Assert.NotEqual(GlyphShape.None, shape);
                Assert.False(string.IsNullOrEmpty(key), st + " nie ma klucza koloru");
            }
        }

        [Theory]
        [InlineData(ServerStatus.Online, "Online")]
        [InlineData(ServerStatus.Idle, "Idle")]
        [InlineData(ServerStatus.Offline, "Offline")]
        public void KolorStatusuIdzieZPalety(ServerStatus status, string key)
            => Assert.Equal(key, StatusGlyph.For(status).ColorKey);

        [Theory]
        [InlineData(SessionState.Connecting, "Accent")]
        [InlineData(SessionState.Disconnected, "Offline")]
        [InlineData(SessionState.Failed, "Danger")]
        public void KolorStanuSesjiIdzieZPalety(SessionState state, string key)
            => Assert.Equal(key, StatusGlyph.For(state).ColorKey);

        [Fact]
        public void RozlaczonaSesjaToNIETOSAMOCoSerwerNieosiagalny()
        {
            // Sedno rozdzielenia obu zestawów: serwer może odpowiadać na porcie, gdy sesja poległa na
            // uwierzytelnieniu. Wcześniej oba stany dzieliły jeden znacznik i jeden kolor.
            Assert.NotEqual(StatusGlyph.For(SessionState.Failed).Shape,
                            StatusGlyph.For(ServerStatus.Offline).Shape);
        }

        [Fact]
        public void SesjaNieMaStanuPonownegoLaczenia()
        {
            // Świadome odstępstwo od propozycji z przeglądu: AutoReconnect jest flagą oddawaną
            // kontrolce RDP, która ponawia we własnym zakresie i nie zgłasza nam zdarzenia. Stan bez
            // źródła byłby martwym kodem z własnym kształtem, kolorem i tłumaczeniem. Gdyby kiedyś
            // pojawiło się realne źródło, ten test upadnie i przypomni o dodaniu kształtu.
            Assert.Equal(4, AllStates.Length);
            Assert.DoesNotContain(AllStates, s => s.ToString().Contains("Reconnect"));
        }
    }
}
