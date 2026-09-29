using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace RdpManager.Core
{
    /// <summary>
    /// Motyw Monaco wyprowadzony z palety — z tych samych kluczy co terminal (<see cref="TerminalTheme"/>),
    /// żeby edytor otwarty obok terminala wyglądał jak jego część, a preset/akcent docierał i tutaj.
    /// Kolory tokenów (słowa kluczowe, napisy) zostają z bazowego motywu vs/vs-dark — są dopracowane
    /// pod czytelność, a paleta Waypointa nie ma własnych barw składni.
    /// Monaco przyjmuje wyłącznie #RRGGBB albo #RRGGBBAA (nie rgba()), stąd własne formatowanie.
    /// </summary>
    public sealed class EditorTheme
    {
        /// <summary>„vs" albo „vs-dark" — motyw, po którym dziedziczone są kolory składni.</summary>
        public string Base { get; private set; }
        public Dictionary<string, string> Colors { get; private set; }

        public static EditorTheme From(Func<string, Color?> resolve, bool light)
        {
            Color Get(string key, string fallback)
                => resolve?.Invoke(key) ?? (Color)ColorConverter.ConvertFromString(fallback);

            var canvas = Get("Canvas", light ? "#EEF0F3" : "#0F1014");
            var panel = Get("Panel", light ? "#FAFBFC" : "#282A36");
            var text = Get("TextPrim", light ? "#1B1D22" : "#E7E8EE");
            var ter = Get("TextTer", light ? "#6B6F78" : "#9396A6");
            var accent = Get("Accent", light ? "#5B4BD6" : "#6C6DFF");
            var border = resolve?.Invoke("Border") ?? (light ? Color.FromArgb(0x21, 0, 0, 0) : Color.FromArgb(0x21, 255, 255, 255));
            var tint = light ? Color.FromRgb(0, 0, 0) : Color.FromRgb(255, 255, 255);

            return new EditorTheme
            {
                Base = light ? "vs" : "vs-dark",
                Colors = new Dictionary<string, string>
                {
                    ["editor.background"] = Hex(canvas),
                    ["editorGutter.background"] = Hex(canvas),
                    ["editor.foreground"] = Hex(text),
                    ["editorLineNumber.foreground"] = Hex(ter),
                    ["editorLineNumber.activeForeground"] = Hex(text),
                    ["editorCursor.foreground"] = Hex(accent),
                    ["editor.selectionBackground"] = Hex(accent, light ? 0.22 : 0.34),
                    ["editor.inactiveSelectionBackground"] = Hex(accent, light ? 0.12 : 0.18),
                    ["editor.findMatchHighlightBackground"] = Hex(accent, 0.25),
                    ["editor.lineHighlightBackground"] = Hex(tint, 0.04),
                    ["editor.lineHighlightBorder"] = "#00000000",
                    ["editorWidget.background"] = Hex(panel),
                    ["editorWidget.border"] = Hex(border, border.A / 255.0),
                    ["editorSuggestWidget.background"] = Hex(panel),
                    ["input.background"] = Hex(canvas),
                    ["focusBorder"] = Hex(accent),
                    ["editorIndentGuide.background1"] = Hex(tint, 0.08),
                    ["editorWhitespace.foreground"] = Hex(ter, 0.5)
                }
            };
        }

        public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static string Hex(Color c, double alpha)
        {
            int a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}{a:X2}";
        }
    }
}
