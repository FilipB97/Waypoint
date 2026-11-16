using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    /// <summary>Poprawki ze zgłoszenia #241: DPI przy monitorach mieszanych, pasek pełnego ekranu,
    /// anulowanie sond i żądań REST.</summary>
    public class Issue241Tests
    {
        [Fact]
        public void ZmianaSamejSkaliWysylaDisplayUpdate()
        {
            // Te same piksele, inna skala (okno przeniesione z 200% na 100%) — trzeba wysłać.
            Assert.True(RdpDisplay.ShouldApplyResize(false, 1920, 1080, 100u, 1920, 1080, 200u));
            Assert.False(RdpDisplay.ShouldApplyResize(false, 1920, 1080, 200u, 1920, 1080, 200u));
            Assert.False(RdpDisplay.ShouldApplyResize(true, 1920, 1080, 100u, 1920, 1080, 200u));   // zminimalizowane
            // Stara sygnatura bez skali — zachowanie jak dotąd.
            Assert.False(RdpDisplay.ShouldApplyResize(false, 1920, 1080, 1920, 1080));
        }

        [Fact]
        public void GornaKrawedzTylkoTegoMonitora()
        {
            // Monitor 1920×1080 pod monitorem 2560×1440 (y od 0 do 1080; ten nad nim ma y < 0).
            Assert.True(FullscreenEdge.IsAtTop(500, 0, 0, 0, 1920, 1080));
            Assert.True(FullscreenEdge.IsAtTop(500, 2, 0, 0, 1920, 1080));
            Assert.False(FullscreenEdge.IsAtTop(500, -1, 0, 0, 1920, 1080));     // dolny brzeg monitora wyżej
            Assert.False(FullscreenEdge.IsAtTop(2000, 0, 0, 0, 1920, 1080));     // obok, na innym monitorze
            // Monitor po lewej z ujemnymi współrzędnymi.
            Assert.True(FullscreenEdge.IsAtTop(-500, -200, -1920, -200, 0, 880));
            Assert.False(FullscreenEdge.IsAtTop(-500, 100, -1920, -200, 0, 880));
        }

        [Fact]
        public void NieaktualnaPartiaSondNieZmieniaListy()
        {
            Assert.True(ReachabilityBatch.IsCurrent(5, 5, cancelled: false, stopped: false));
            Assert.False(ReachabilityBatch.IsCurrent(4, 5, cancelled: false, stopped: false));   // zastąpiona
            Assert.False(ReachabilityBatch.IsCurrent(5, 5, cancelled: true, stopped: false));    // sonda wyłączona
            Assert.False(ReachabilityBatch.IsCurrent(5, 5, cancelled: false, stopped: true));    // zamykanie
        }

        [Fact]
        public void StareZadanieRestNieDotykaUi()
        {
            Assert.True(RestRequestOwnership.IsCurrent(stillOwner: true, cancelled: false));
            Assert.False(RestRequestOwnership.IsCurrent(stillOwner: false, cancelled: false));   // nowsze przejęło konsolę
            Assert.False(RestRequestOwnership.IsCurrent(stillOwner: true, cancelled: true));     // konsola zamknięta
        }

        [Fact]
        public void AnulowaneZrodloNieRzucaPrzyOdczycieZapamietanegoTokenu()
        {
            // Wzorzec z RestConsole: token zapamiętany przed await zostaje czytelny po Dispose źródła
            // (czytanie cts.Token po Dispose rzucało ObjectDisposedException w catch/finally).
            var cts = new System.Threading.CancellationTokenSource();
            var token = cts.Token;
            cts.Cancel();
            cts.Dispose();
            Assert.True(token.IsCancellationRequested);
            Assert.Throws<System.ObjectDisposedException>(() => cts.Token);
        }
    }
}
