using System.Windows;
using System.Windows.Input;

namespace RdpManager
{
    /// <summary>
    /// Modal wyboru źródła importu (Compass §4.7). Zwraca wybrane źródło przez <see cref="Selected"/>;
    /// właściwy import (z oknem wyboru pliku) uruchamia wołający (MainWindow), żeby dialog należał do niego.
    /// </summary>
    public partial class ImportWindow
    {
        public string Selected { get; private set; }

        public ImportWindow()
        {
            InitializeComponent();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private void Card_Click(object sender, RoutedEventArgs e)
        {
            Selected = (sender as FrameworkElement)?.Tag as string;
            DialogResult = true;
            Close();
        }
    }
}
