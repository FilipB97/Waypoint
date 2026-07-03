using System.Collections.Generic;
using System.Windows.Media;

namespace RdpManager
{
    /// <summary>
    /// Grupa kart (stos jak w Vivaldi) — nazwa + kolor + stan zwinięcia. Przynależność jest po Id
    /// serwera (ServerIds), nie po sesji: dzięki temu grupa przeżywa zamknięcie/otwarcie karty i daje
    /// się zapisać (TabGroupDef) oraz odtworzyć przy starcie. Sesja należy do grupy, gdy jej serwer
    /// jest na liście ServerIds.
    /// </summary>
    public sealed class TabGroup
    {
        public string Name { get; set; }
        public Color Color { get; set; }
        public bool Collapsed { get; set; }
        public List<string> ServerIds { get; set; } = new List<string>();
    }
}
