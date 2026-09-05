using System.Collections.Generic;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Wymiary listy serwerów. ServerTreeController buduje kontrolki WPF i nie uruchomi się poza
    // aplikacją, więc same liczby żyją w ListMetrics — i to tutaj da się sprawdzić rzecz, na której
    // najłatwiej się przejechać: że dołożenie NOWEJ opcji nie przesunęło układu tym, którzy jej nie
    // włączyli.
    public class ListMetricsTests
    {
        private static ListMetrics M(ListDensity d, GroupLayout l = GroupLayout.Indent) => ListMetrics.For(d, l);

        private static IEnumerable<object[]> Wszystkie()
        {
            foreach (ListDensity d in System.Enum.GetValues(typeof(ListDensity)))
                foreach (GroupLayout l in System.Enum.GetValues(typeof(GroupLayout)))
                    yield return new object[] { d, l };
        }
        public static IEnumerable<object[]> Kombinacje => Wszystkie();

        [Theory]
        [InlineData("Minimal", ListDensity.Minimal)]
        [InlineData("minimal", ListDensity.Minimal)]
        [InlineData("Dense", ListDensity.Dense)]
        [InlineData("Default", ListDensity.Default)]
        [InlineData("", ListDensity.Default)]
        [InlineData(null, ListDensity.Default)]
        [InlineData("cokolwiek", ListDensity.Default)]
        public void NieznanaGestoscDajeDomyslna(string value, ListDensity expected)
            => Assert.Equal(expected, ListMetrics.ParseDensity(value));

        [Theory]
        [InlineData("Rail", GroupLayout.Rail)]
        [InlineData("flat", GroupLayout.Flat)]
        [InlineData("Indent", GroupLayout.Indent)]
        [InlineData(null, GroupLayout.Indent)]
        [InlineData("cokolwiek", GroupLayout.Indent)]
        public void NieznanyUkladDajeWciecie(string value, GroupLayout expected)
            => Assert.Equal(expected, ListMetrics.ParseLayout(value));

        [Fact]
        public void UkladDomyslnyNieRuszylUkladuDotychczasowego()
        {
            // Sedno: wartości przeniesione 1:1 z BuildServerRowDefault / BuildServerRowMinimal.
            // Gdyby którakolwiek się przesunęła, użytkownik zobaczyłby zmianę bez dotykania ustawień.
            var d = M(ListDensity.Default);
            Assert.Equal(18, d.RowIndent);
            Assert.Equal(new System.Windows.Thickness(4, 5, 6, 5), d.RowPadding);
            Assert.Equal(12, d.HeaderPadding.Left);
            Assert.Equal(7, d.HeaderPadding.Top);
            Assert.True(d.HeaderRule);

            var m = M(ListDensity.Minimal);
            Assert.Equal(12, m.RowIndent);
            Assert.Equal(new System.Windows.Thickness(4, 2, 6, 2), m.RowPadding);
            Assert.Equal(27, m.RowMinHeight);
            Assert.Equal(5, m.HeaderPadding.Top);
        }

        [Fact]
        public void KazdaGestoscNiesieProtokolDokladnieJednymSposobem()
        {
            // Kafelek, pasek i etykieta mówią to samo. Dwa naraz to dwa sygnały o tym samym
            // w odległości kilku pikseli — dokładnie to, co usunięto z wariantu minimalnego.
            foreach (ListDensity d in System.Enum.GetValues(typeof(ListDensity)))
            {
                var x = M(d);
                int nosniki = (x.Avatar && x.ProtocolTag ? 1 : 0) + (x.ProtocolTile ? 1 : 0) + (x.ProtocolBar ? 1 : 0);
                Assert.True(nosniki == 1, $"Gęstość {d} niesie protokół {nosniki} sposobami");
            }
        }

        [Fact]
        public void GestyJestNizszyOdMinimalnego_AMinimalnyOdDomyslnego()
        {
            // Trzy stopnie mają być realnie trzema stopniami, a nie dwoma i kopią.
            double H(ListDensity d)
            {
                var x = M(d);
                return System.Math.Max(x.RowMinHeight, x.RowPadding.Top + x.RowPadding.Bottom + 22) + 2 * x.RowGap;
            }
            Assert.True(H(ListDensity.Dense) < H(ListDensity.Minimal));
            Assert.True(H(ListDensity.Minimal) <= H(ListDensity.Default));
        }

        [Fact]
        public void GestyNieMaAniAwataraAniKafelka()
        {
            var x = M(ListDensity.Dense);
            Assert.False(x.Avatar);
            Assert.False(x.ProtocolTile);
            Assert.True(x.ProtocolBar);
            Assert.False(x.ProtocolTag);
        }

        [Theory]
        [MemberData(nameof(Kombinacje))]
        public void KazdaKombinacjaGestosciIUkladuJestPoprawna(ListDensity d, GroupLayout l)
        {
            // Dziewięć kombinacji, bo ustawienia są NIEZALEŻNE — żadna nie może dać wiersza
            // o ujemnym wcięciu ani sekcji bez sposobu na odróżnienie od sąsiedniej.
            var x = ListMetrics.For(d, l);

            Assert.True(x.RowIndent >= 0);
            Assert.True(x.RowPadding.Left >= 0 && x.RowPadding.Right >= 0);
            Assert.True(x.HeaderPadding.Left >= 0);
            Assert.True(x.HeaderRule || x.Rail || x.StickyHeader,
                "Sekcje muszą się od siebie odróżniać: kreską, pasem koloru albo przyklejonym nagłówkiem");
        }

        [Fact]
        public void PasKoloruIPlaskieSekcjeSaRozlaczne()
        {
            // To dwa różne pomysły na tę samą rzecz — gdyby dały się włączyć naraz, sekcja miałaby
            // i pas, i tło nagłówka, czyli dwa sygnały przynależności zamiast jednego.
            foreach (ListDensity d in System.Enum.GetValues(typeof(ListDensity)))
            {
                Assert.True(M(d, GroupLayout.Rail).Rail);
                Assert.False(M(d, GroupLayout.Rail).StickyHeader);
                Assert.True(M(d, GroupLayout.Flat).StickyHeader);
                Assert.False(M(d, GroupLayout.Flat).Rail);
            }
        }

        [Fact]
        public void UkladyBezWciecieNieWcinajaWiersza()
        {
            // W obu wcięcie daje co innego niż margines wiersza: kontener sekcji (pas) albo nic (płaskie).
            foreach (ListDensity d in System.Enum.GetValues(typeof(ListDensity)))
            {
                Assert.Equal(0, M(d, GroupLayout.Rail).RowIndent);
                Assert.Equal(0, M(d, GroupLayout.Flat).RowIndent);
                Assert.True(M(d, GroupLayout.Flat).FullBleedRow);
                Assert.False(M(d, GroupLayout.Indent).FullBleedRow);
            }
        }

        [Fact]
        public void PasKoloruMaNiezerowaSzerokosc()
        {
            var x = M(ListDensity.Default, GroupLayout.Rail);
            Assert.True(x.RailWidth > 0);
        }
    }
}
