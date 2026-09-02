using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>Kształt znacznika stanu. Kształt niesie znaczenie, kolor je wzmacnia.</summary>
    public enum GlyphShape
    {
        /// <summary>Brak znacznika — stan normalny nie potrzebuje sygnału.</summary>
        None,
        /// <summary>Wypełniony dysk — osiągalny.</summary>
        Disc,
        /// <summary>Pierścień — stan pośredni (wolna odpowiedź).</summary>
        Ring,
        /// <summary>Kreska — nieosiągalny / rozłączona.</summary>
        Bar,
        /// <summary>Romb — błąd wymagający decyzji użytkownika.</summary>
        Diamond,
        /// <summary>Obracający się łuk — trwa łączenie.</summary>
        Arc
    }

    /// <summary>
    /// Znaczniki stanu dla listy serwerów i paska kart — jedna definicja dla obu.
    ///
    /// Powód istnienia: znacznik był kropką w trzech odcieniach, czyli dla osoby nierozróżniającej
    /// barw JEDNYM odcieniem. WCAG 1.4.1 („Użycie koloru") wymaga drugiego nośnika informacji —
    /// tutaj jest nim kształt. Kolor zostaje, ale przestaje być jedynym sygnałem.
    ///
    /// Kształty żyją w polu o stałym rozmiarze (<see cref="Field"/>), żeby kolumna statusu nie
    /// skakała między wierszami — to jej jedyne zadanie.
    /// </summary>
    public static class StatusGlyph
    {
        /// <summary>Bok kwadratowego pola znacznika. Każdy kształt mieści się w nim W CAŁOŚCI.</summary>
        public const double Field = 10;

        /// <summary>Osiągalność SERWERA (lista serwerów).</summary>
        public static (GlyphShape Shape, string ColorKey) For(ServerStatus status)
        {
            switch (status)
            {
                case ServerStatus.Online: return (GlyphShape.Disc, "Online");
                case ServerStatus.Idle:   return (GlyphShape.Ring, "Idle");
                default:                  return (GlyphShape.Bar,  "Offline");
            }
        }

        /// <summary>
        /// Stan SESJI (pasek kart). Sesja połączona nie dostaje znacznika: przy sześciu zdrowych
        /// sesjach znika sześć sygnałów, a zostają tylko te, które czegoś wymagają. Milczenie znaczy
        /// „działa" — to najtańsze uspokojenie paska, jakie da się zrobić.
        /// </summary>
        public static (GlyphShape Shape, string ColorKey) For(SessionState state)
        {
            switch (state)
            {
                case SessionState.Connecting:   return (GlyphShape.Arc,     "Accent");
                case SessionState.Connected:    return (GlyphShape.None,    null);
                case SessionState.Disconnected: return (GlyphShape.Bar,     "Offline");
                default:                        return (GlyphShape.Diamond, "Danger");
            }
        }

        /// <summary>Pole o stałym rozmiarze, do którego wstawia się kształt (patrz <see cref="Set"/>).</summary>
        public static Grid Host() => new Grid
        {
            Width = Field,
            Height = Field,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };

        /// <summary>Podmienia kształt w polu. <see cref="GlyphShape.None"/> zostawia pole puste.</summary>
        public static void Set(Grid host, GlyphShape shape, Brush brush)
        {
            if (host == null) return;
            host.Children.Clear();
            var glyph = Build(shape, brush);
            if (glyph != null) host.Children.Add(glyph);
        }

        /// <summary>Buduje kształt wyśrodkowany w polu <see cref="Field"/>; null dla braku znacznika.</summary>
        public static FrameworkElement Build(GlyphShape shape, Brush brush)
        {
            switch (shape)
            {
                case GlyphShape.Disc:
                    return Center(new Ellipse { Width = 8, Height = 8, Fill = brush });

                case GlyphShape.Ring:
                    // Grubość 2.5, nie 3: przy polu 10 px zostaje wtedy 5 px światła w środku, czyli
                    // pierścień czyta się jako pierścień, a nie jako grubszy dysk.
                    return Center(new Ellipse
                    {
                        Width = Field, Height = Field, Stroke = brush, StrokeThickness = 2.5, Fill = null
                    });

                case GlyphShape.Bar:
                    return Center(new Rectangle { Width = Field, Height = 2, RadiusX = 1, RadiusY = 1, Fill = brush });

                case GlyphShape.Diamond:
                    // 7×7, nie 8×8: kwadrat obrócony o 45° ma przekątną a·√2, więc 8 px wystawałoby
                    // poza pole (11,3 px) i romb obcinałby się o sąsiednią kolumnę. 7 px daje 9,9 px.
                    return Center(new Rectangle
                    {
                        Width = 7, Height = 7, Fill = brush,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(45)
                    });

                case GlyphShape.Arc:
                    return BuildArc(brush);

                default:
                    return null;
            }
        }

        private static FrameworkElement Center(FrameworkElement e)
        {
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.VerticalAlignment = VerticalAlignment.Center;
            return e;
        }

        // Łuk = okrąg z przerywaną obwódką, obracany w kółko. Prostsze i lżejsze niż Path z geometrią
        // łuku, a wygląda tak samo. Obwód okręgu o r=4 to ~25 px, więc kreska 9 z przerwą 16 daje
        // mniej więcej jedną trzecią obwodu.
        private static FrameworkElement BuildArc(Brush brush)
        {
            var rotate = new RotateTransform();
            var arc = new Ellipse
            {
                Width = Field, Height = Field,
                Stroke = brush, StrokeThickness = 2, Fill = null,
                StrokeDashArray = new DoubleCollection { 4.5, 8 },   // w jednostkach grubości (2 px)
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = rotate
            };
            var spin = new DoubleAnimation(0, 360, new Duration(System.TimeSpan.FromMilliseconds(1200)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
            return Center(arc);
        }
    }
}
