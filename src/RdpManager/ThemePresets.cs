using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace RdpManager
{
    /// <summary>
    /// Nazwany wariant palety (Compass §4.9). Nakłada tylko klucze „tonu" (tło/powierzchnie/tekst/akcent)
    /// na bazową paletę Waypoint (Palette.{Dark,Light}.xaml) — statusy, kolory grup, protokołów i gradienty
    /// zostają wspólne (czytelne na wszystkich tłach). Definiowane w kodzie (nie w XAML), więc dodanie
    /// kolejnego presetu = jeden wpis w <see cref="ThemePresets.All"/>, bez duplikowania ~40 kluczy.
    /// „Waypoint" to preset domyślny — bez nakładki, obowiązuje sama baza (Find zwraca null).
    /// </summary>
    public sealed class ThemePreset
    {
        public string Id;
        public string Name;
        public bool Light;
        public Color Canvas, Panel, Border, RailBg, TextPrim, TextSec, TextTer, Accent;
    }

    public static class ThemePresets
    {
        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        public const string DefaultId = "Waypoint";

        public static readonly IReadOnlyList<ThemePreset> All = new List<ThemePreset>
        {
            // — Ciemne — (Waypoint = podgląd bazy; reszta wg kanonicznych kolorów motywów)
            new ThemePreset { Id = "Waypoint", Name = "Waypoint", Light = false,
                Canvas = C("#0C0D11"), Panel = C("#17181D"), Border = C("#2A2B31"), RailBg = C("#121319"),
                TextPrim = C("#E7E8EE"), TextSec = C("#8A8C97"), TextTer = C("#5A5C66"), Accent = C("#4C86FF") },
            new ThemePreset { Id = "AtomOne", Name = "Atom One Dark", Light = false,
                Canvas = C("#282C34"), Panel = C("#2F343D"), Border = C("#3B414D"), RailBg = C("#21252B"),
                TextPrim = C("#ABB2BF"), TextSec = C("#828997"), TextTer = C("#5C6370"), Accent = C("#61AFEF") },
            new ThemePreset { Id = "GitHubDark", Name = "GitHub Dark", Light = false,
                Canvas = C("#0D1117"), Panel = C("#161B22"), Border = C("#30363D"), RailBg = C("#010409"),
                TextPrim = C("#C9D1D9"), TextSec = C("#8B949E"), TextTer = C("#6E7681"), Accent = C("#58A6FF") },
            new ThemePreset { Id = "ClaudeDark", Name = "Claude Dark", Light = false,
                Canvas = C("#262624"), Panel = C("#30302E"), Border = C("#45443F"), RailBg = C("#1F1E1D"),
                TextPrim = C("#ECEBE6"), TextSec = C("#A3A196"), TextTer = C("#6F6E66"), Accent = C("#D97757") },
            new ThemePreset { Id = "TokyoNight", Name = "Tokyo Night", Light = false,
                Canvas = C("#1A1B26"), Panel = C("#24283B"), Border = C("#3B4261"), RailBg = C("#16161E"),
                TextPrim = C("#C0CAF5"), TextSec = C("#9AA5CE"), TextTer = C("#565F89"), Accent = C("#7AA2F7") },
            new ThemePreset { Id = "Nord", Name = "Nord", Light = false,
                Canvas = C("#2E3440"), Panel = C("#3B4252"), Border = C("#4C566A"), RailBg = C("#292E39"),
                TextPrim = C("#ECEFF4"), TextSec = C("#D8DEE9"), TextTer = C("#7B88A1"), Accent = C("#88C0D0") },

            // — Jasne —
            new ThemePreset { Id = "Waypoint", Name = "Waypoint", Light = true,
                Canvas = C("#EEF0F3"), Panel = C("#FFFFFF"), Border = C("#DDE1E7"), RailBg = C("#F2F3F5"),
                TextPrim = C("#1B1D22"), TextSec = C("#565A62"), TextTer = C("#888B93"), Accent = C("#2657D6") },
            new ThemePreset { Id = "GitHubLight", Name = "GitHub Light", Light = true,
                Canvas = C("#FFFFFF"), Panel = C("#F6F8FA"), Border = C("#D0D7DE"), RailBg = C("#F6F8FA"),
                TextPrim = C("#1F2328"), TextSec = C("#656D76"), TextTer = C("#8C959F"), Accent = C("#0969DA") },
            new ThemePreset { Id = "Solarized", Name = "Solarized Light", Light = true,
                Canvas = C("#FDF6E3"), Panel = C("#EEE8D5"), Border = C("#D9D2B8"), RailBg = C("#EEE8D5"),
                TextPrim = C("#073642"), TextSec = C("#586E75"), TextTer = C("#93A1A1"), Accent = C("#268BD2") },
            new ThemePreset { Id = "ClaudeLight", Name = "Claude Light", Light = true,
                Canvas = C("#F5F4EE"), Panel = C("#FFFFFF"), Border = C("#DDDBCF"), RailBg = C("#EDEBE1"),
                TextPrim = C("#2A2925"), TextSec = C("#6B6A60"), TextTer = C("#97968B"), Accent = C("#C15F3C") },
            new ThemePreset { Id = "Catppuccin", Name = "Catppuccin Latte", Light = true,
                Canvas = C("#EFF1F5"), Panel = C("#FFFFFF"), Border = C("#CCD0DA"), RailBg = C("#E6E9EF"),
                TextPrim = C("#4C4F69"), TextSec = C("#6C6F85"), TextTer = C("#9CA0B0"), Accent = C("#1E66F5") },
            new ThemePreset { Id = "OneLight", Name = "One Light", Light = true,
                Canvas = C("#FAFAFA"), Panel = C("#FFFFFF"), Border = C("#D3D3D4"), RailBg = C("#EAEAEB"),
                TextPrim = C("#383A42"), TextSec = C("#696C77"), TextTer = C("#A0A1A7"), Accent = C("#4078F2") },
        };

        /// <summary>Presety dla danego trybu (do siatki wyboru w Ustawieniach).</summary>
        public static IEnumerable<ThemePreset> For(bool light) => All.Where(p => p.Light == light);

        /// <summary>Preset do nałożenia; null dla domyślnego „Waypoint" (obowiązuje sama baza palety).</summary>
        public static ThemePreset Find(string id, bool light)
            => (string.IsNullOrEmpty(id) || id == DefaultId) ? null
             : All.FirstOrDefault(p => p.Light == light && p.Id == id);
    }
}
