using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RdpManager
{
    /// <summary>
    /// Cienka pozioma linia (akcent) na górnej albo dolnej krawędzi wiersza — pokazuje podczas
    /// przeciągania, gdzie wyląduje serwer. Rysowana w warstwie adornerów, więc nie zmienia układu.
    /// </summary>
    internal sealed class InsertionAdorner : Adorner
    {
        private readonly Pen _pen;
        private readonly Brush _brush;

        /// <summary>Czy linia ma być na dolnej (true) czy górnej (false) krawędzi wiersza.</summary>
        public bool AtBottom { get; set; }

        public InsertionAdorner(UIElement adorned, Brush brush) : base(adorned)
        {
            _brush = brush;
            _pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _pen.Freeze();
            IsHitTestVisible = false;   // nie przechwytuj zdarzeń drag
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = AdornedElement.RenderSize.Width;
            double y = AtBottom ? AdornedElement.RenderSize.Height : 0;
            dc.DrawLine(_pen, new Point(3, y), new Point(w - 3, y));
            dc.DrawEllipse(_brush, null, new Point(3, y), 2.5, 2.5);
            dc.DrawEllipse(_brush, null, new Point(w - 3, y), 2.5, 2.5);
        }
    }
}
