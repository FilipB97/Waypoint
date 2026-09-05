using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Wymiary paska kart. Kontroler buduje kontrolki WPF i nie uruchomi się poza aplikacją, więc same
    // liczby żyją w TabMetrics — i tylko dzięki temu da się sprawdzić to, co w tej funkcji faktycznie
    // może się zepsuć po cichu: że styl blokowy przestanie się zwężać tam, gdzie ma, albo że karta
    // zrobi się niższa od przycisku okna, który stoi na tym samym pasku.
    public class TabMetricsTests
    {
        private static TabMetrics M(TabStyle s, bool min = false, bool focus = false) => TabMetrics.For(s, min, focus);

        [Theory]
        [InlineData("Block", TabStyle.Block)]
        [InlineData("block", TabStyle.Block)]
        [InlineData("Marker", TabStyle.Marker)]
        [InlineData("Default", TabStyle.Default)]
        [InlineData("", TabStyle.Default)]
        [InlineData(null, TabStyle.Default)]
        [InlineData("cokolwiek", TabStyle.Default)]
        public void NieznanaWartoscZUstawienDajeStylDomyslny(string value, TabStyle expected)
            => Assert.Equal(expected, TabMetrics.Parse(value));

        [Fact]
        public void StylBlokowyMaInneWypelnienieAktywnejKartyNizPozostale()
        {
            // Regresja z użycia. Blok i znacznik malowały aktywną kartę tym samym „Panel", więc
            // różniły się WYŁĄCZNIE promieniem, odstępem i położeniem paska 2 px — czyli szczegółami,
            // które przy rzucie oka na sześć kart znikają. Zgłoszone wprost: „wygląda praktycznie tak
            // samo jak znacznik". Tło aktywnej karty to sygnał dominujący i style o różnej BUDOWIE
            // muszą się różnić właśnie tutaj, a nie tylko w detalach.
            var block = M(TabStyle.Block);
            Assert.NotEqual(M(TabStyle.Default).ActiveFill, block.ActiveFill);
            Assert.NotEqual(M(TabStyle.Marker).ActiveFill, block.ActiveFill);
        }

        [Fact]
        public void ZnacznikDzieliWypelnienieZDomyslnym_ITakMaByc()
        {
            // Świadomy wyjątek od reguły powyżej: znacznik NIE jest inną budową karty, tylko tym samym
            // kształtem z akcentem przeniesionym na lewą krawędź. Wspólne wypełnienie jest tu treścią
            // wariantu, a nie przeoczeniem — rozróżnia je kształt i miejsce akcentu.
            Assert.Equal(M(TabStyle.Default).ActiveFill, M(TabStyle.Marker).ActiveFill);
            Assert.NotEqual(M(TabStyle.Default).Mark, M(TabStyle.Marker).Mark);
        }

        [Fact]
        public void KartaBlokowaDobijaDoObuKrawedziPaska()
        {
            // Bez tego „blok" jest tylko kanciastą kartą pływającą w pasku — a to jest ten sam obrazek
            // co pozostałe style. Karta ma dotykać paska z góry, z dołu i z boków.
            var b = M(TabStyle.Block);
            Assert.Equal(0, b.StripPadV);
            Assert.Equal(0, b.StripPadH);
            Assert.True(M(TabStyle.Default).StripPadH > 0);
        }

        [Fact]
        public void PasekAkcentuBlokuNieJestCienszyNizZnacznika()
        {
            // Pasek bloku leży na samej krawędzi paska kart, gdzie 2 px ginie. Znacznik ma 3 px
            // i to jest dolna granica dla obu.
            Assert.True(M(TabStyle.Block).MarkSize >= M(TabStyle.Marker).MarkSize);
        }

        [Fact]
        public void KazdyStylNosiAkcentGdzieIndziej()
        {
            Assert.Equal(TabMark.Bottom, M(TabStyle.Default).Mark);
            Assert.Equal(TabMark.Top, M(TabStyle.Block).Mark);
            Assert.Equal(TabMark.Left, M(TabStyle.Marker).Mark);
        }

        [Fact]
        public void ZnacznikNieZmieniaWysokosciWzgledemDomyslnego()
        {
            // Sedno: przełączenie „zaokrąglony ⇄ znacznik" ma zmienić WYŁĄCZNIE miejsce akcentu.
            // Gdyby znacznik przestał rezerwować dolną kreskę, pasek zrobiłby się niższy i obszar
            // sesji podskoczyłby przy samej zmianie ustawienia.
            foreach (var min in new[] { false, true })
            {
                var d = M(TabStyle.Default, min);
                var k = M(TabStyle.Marker, min);

                Assert.True(k.ReserveBottom);
                Assert.Equal(d.Padding, k.Padding);
                Assert.Equal(d.Margin, k.Margin);
                Assert.Equal(d.StripPadV, k.StripPadV);
                Assert.Equal(d.TabHeight, k.TabHeight);
            }
        }

        [Fact]
        public void PasekZnacznikaMiesciSieWLewymWypelnieniu()
        {
            // Pasek stoi 4 px od krawędzi karty. Gdyby był szerszy niż lewe wypełnienie minus ten
            // odstęp, wchodziłby pod awatar — a wypełnienia nie zmieniamy, żeby trzymać wysokość.
            foreach (var min in new[] { false, true })
            {
                var k = M(TabStyle.Marker, min);
                Assert.True(4 + k.MarkSize <= k.Padding.Left,
                    $"Pasek {k.MarkSize} px + odstęp 4 px nie mieści się w wypełnieniu {k.Padding.Left} px");
            }
        }

        [Fact]
        public void StylBlokowyNieMaZaokraglenAniOdstepow()
        {
            var b = M(TabStyle.Block);

            Assert.Equal(0, b.Radius);
            Assert.Equal(0, b.Margin.Left + b.Margin.Top + b.Margin.Right + b.Margin.Bottom);
            Assert.Equal(0, b.StripPadV);                 // karta dotyka krawędzi paska — stąd „blok"
            Assert.Equal(1, b.Border.Right);              // separator między kartami
            Assert.Equal(0, b.Border.Top + b.Border.Left + b.Border.Bottom);
            Assert.False(b.ReserveBottom);                // wysokość daje TabHeight, nie rozpórka
            Assert.True(b.TabHeight > 0);
        }

        [Fact]
        public void StylBlokowyJestNizszyWWidokuMinimalnym()
        {
            Assert.True(M(TabStyle.Block, min: true).TabHeight < M(TabStyle.Block).TabHeight);
            Assert.True(M(TabStyle.Block, min: true, focus: true).TabHeight
                        < M(TabStyle.Block, focus: true).TabHeight);
        }

        [Fact]
        public void StylBlokowyJestNizszyWTrybieSkupienia()
        {
            // W skupieniu pasek kart zastępuje pasek tytułu, więc jego wysokość jest zabierana sesji.
            Assert.True(M(TabStyle.Block, focus: true).TabHeight < M(TabStyle.Block).TabHeight);
            Assert.True(M(TabStyle.Block, min: true, focus: true).TabHeight
                        < M(TabStyle.Block, min: true).TabHeight);
        }

        [Fact]
        public void KartaBlokowaMiesciPrzyciskOknaZTegoSamegoPaska()
        {
            // W skupieniu przyciski minimalizuj/przywróć/zamknij stoją NA pasku kart. Karta niższa od
            // nich nie zmniejszyłaby paska ani o piksel — zmniejszyłaby tylko samą siebie.
            Assert.True(M(TabStyle.Block, focus: true).TabHeight >= TabMetrics.FocusButton);
            Assert.True(M(TabStyle.Block, min: true, focus: true).TabHeight >= TabMetrics.FocusButtonMinimal);
        }

        [Fact]
        public void KartaBlokowaMiesciAwatar()
        {
            // Awatar ma 17 px i w widoku domyślnym jest najwyższym elementem treści; karta niższa
            // przycięłaby go. (W widoku minimalnym awatara nie ma — stąd wolno tam zejść niżej.)
            foreach (var focus in new[] { false, true })
                Assert.True(M(TabStyle.Block, focus: focus).TabHeight >= 17 + 2 * 4,
                    "Karta blokowa musi pomieścić awatar 17 px z oddechem");
        }

        [Theory]
        [InlineData(false, false, 40)]
        [InlineData(false, true, 36)]
        [InlineData(true, false, 28)]
        [InlineData(true, true, 26)]
        public void WysokosciKartyBlokowejSaPrzypiete(bool minimal, bool focus, double expected)
        {
            // Przypięte wprost, a nie wyliczane z wysokości stylu domyślnego. Odtwarzanie tamtej
            // w teście znaczyłoby powtórzenie układu WPF (wypełnienia + kreska + obrys + margines
            // paska) w drugim miejscu — czyli sprawdzanie własnej kopii zamiast wartości. Zmiana
            // którejkolwiek z tych liczb ma być widoczna w diffie i świadoma.
            Assert.Equal(expected, M(TabStyle.Block, minimal, focus).TabHeight);
        }

        [Fact]
        public void KartaBlokowaMiesciWierszTekstu()
        {
            // Widok minimalny nie ma awatara, więc granicą jest wiersz nazwy (12 px ≈ 16 px wysokości).
            foreach (var focus in new[] { false, true })
                Assert.True(M(TabStyle.Block, min: true, focus: focus).TabHeight >= 16 + 2 * 4,
                    "Karta blokowa w widoku minimalnym musi pomieścić wiersz nazwy z oddechem");
        }
    }
}
