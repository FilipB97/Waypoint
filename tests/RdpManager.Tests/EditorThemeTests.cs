using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Media;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Edytor plików (Monaco w WebView2) jak terminal nie widzi DynamicResource — motyw liczymy z palety.
    // Monaco przyjmuje wyłącznie #RRGGBB / #RRGGBBAA: zły format nie rzuca błędem, tylko po cichu
    // zostawia domyślny kolor, więc format jest tu sprawdzany wprost.
    public class EditorThemeTests
    {
        private static readonly Dictionary<string, Color> Preset = new Dictionary<string, Color>
        {
            ["Canvas"] = Color.FromRgb(0x1A, 0x1B, 0x26),
            ["Panel"] = Color.FromRgb(0x24, 0x28, 0x3B),
            ["Border"] = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
            ["TextPrim"] = Color.FromRgb(0xC0, 0xCA, 0xF5),
            ["TextTer"] = Color.FromRgb(0x56, 0x5F, 0x89),
            ["Accent"] = Color.FromRgb(0x7A, 0xA2, 0xF7)
        };

        private static Func<string, Color?> Map(Dictionary<string, Color> m)
            => k => m.TryGetValue(k, out var c) ? c : (Color?)null;

        [Fact]
        public void KoloryZPalety()
        {
            var t = EditorTheme.From(Map(Preset), light: false);
            Assert.Equal("vs-dark", t.Base);
            Assert.Equal("#1A1B26", t.Colors["editor.background"]);
            Assert.Equal("#C0CAF5", t.Colors["editor.foreground"]);
            Assert.Equal("#7AA2F7", t.Colors["editorCursor.foreground"]);
            Assert.StartsWith("#7AA2F7", t.Colors["editor.selectionBackground"]);
            Assert.Equal("#FFFFFF22", t.Colors["editorWidget.border"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WszystkieWFormacieMonaco_TakzeBezPalety(bool light)
        {
            var t = EditorTheme.From(_ => null, light);
            Assert.Equal(light ? "vs" : "vs-dark", t.Base);
            foreach (var kv in t.Colors)
                Assert.Matches(new Regex("^#[0-9A-F]{6}([0-9A-F]{2})?$"), kv.Value);
        }
    }
}
