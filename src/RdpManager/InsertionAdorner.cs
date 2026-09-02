using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RdpManager
{
    /// <summary>
    /// Cienka linia (akcent) na krawędzi elementu — pokazuje podczas przeciągania, gdzie wyląduje
    /// element. Rysowana w warstwie adornerów, więc NIE ZMIENIA UKŁADU: to jej sens, bo wcześniejszy
    /// wskaźnik na pasku kart ustawiał obramowanie na samej karcie i przesuwał jej treść w trakcie
    /// przeciągania.
    ///
    /// Pozioma dla listy serwerów (elementy jeden pod drugim), pionowa dla paska kart (obok siebie) —
    /// jedna definicja obsługuje oba, bo różni je wyłącznie oś.
    /// </summary>
    internal sealed class InsertionAdorner : Adorner
    {
        private readonly Pen _pen;
        private readonly Brush _brush;

        /// <summary>Krawędź: dla poziomej dolna (true) albo górna, dla pionowej prawa albo lewa.</summary>
        public bool AtEnd { get; set; }

        /// <summary>Linia pionowa (pasek kart) zamiast poziomej (lista serwerów).</summary>
        public bool Vertical { get; set; }

        public InsertionAdorner(UIElement adorned, Brush brush) : base(adorned)
        {
            _brush = brush;
            _pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _pen.Freeze();
            IsHitTestVisible = false;   // nie przechwytuj zdarzeń drag
        }

        protected override void OnRender(DrawingContext dc)
        {
            Size size = AdornedElement.RenderSize;
            Point a, b;
            if (Vertical)
            {
                double x = AtEnd ? size.Width : 0;
                a = new Point(x, 3); b = new Point(x, size.Height - 3);
            }
            else
            {
                double y = AtEnd ? size.Height : 0;
                a = new Point(3, y); b = new Point(size.Width - 3, y);
            }
            dc.DrawLine(_pen, a, b);
            dc.DrawEllipse(_brush, null, a, 2.5, 2.5);
            dc.DrawEllipse(_brush, null, b, 2.5, 2.5);
        }
    }
}
