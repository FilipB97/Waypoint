using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Popup startowy: lista ostatnio otwartych połączeń (wstępnie zaznaczone) + „Otwórz zaznaczone".
    /// Pozwala szybko wrócić do pracy. „Nie teraz" nic nie otwiera; oba przyciski zwracają true,
    /// wołający czyta <see cref="SelectedServers"/> i <see cref="DontAskAgain"/>.
    /// </summary>
    public partial class SessionRestoreWindow
    {
        private readonly List<(CheckBox Cb, ServerInfo Server)> _items = new List<(CheckBox, ServerInfo)>();

        public List<ServerInfo> SelectedServers { get; private set; } = new List<ServerInfo>();
        public bool DontAskAgain => DontAsk.IsChecked == true;

        public SessionRestoreWindow(IEnumerable<ServerInfo> servers)
        {
            InitializeComponent();
            foreach (var s in servers)
            {
                var text = new StackPanel { Orientation = Orientation.Horizontal };
                text.Children.Add(new TextBlock
                {
                    Text = s.Name ?? s.Host ?? "",
                    Foreground = (Brush)TryFindResource("TextPrim"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                text.Children.Add(new TextBlock
                {
                    Text = s.Host ?? "",
                    Foreground = (Brush)TryFindResource("TextTer"),
                    FontFamily = (FontFamily)TryFindResource("Mono"), FontSize = 11,
                    Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center
                });
                var cb = new CheckBox { IsChecked = true, Content = text, Margin = new Thickness(0, 3, 0, 3) };
                _items.Add((cb, s));
                List.Children.Add(cb);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            foreach (var it in _items)
                if (it.Cb.IsChecked == true) SelectedServers.Add(it.Server);
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = true;   // nic nie otwiera; honoruje „nie pytaj"
    }
}
