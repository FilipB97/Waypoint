using System;
using System.Windows;
using System.Windows.Controls;

namespace RdpManager
{
    /// <summary>
    /// Widok dwupanelowy plików: lokalny (<see cref="LocalFs"/>) po lewej, zdalny (SFTP/FTP) po prawej,
    /// z transferem w obie strony (strzałki na środku oraz przeciąganie wiersza między panelami).
    /// Cały transfer przez zdalny panel (jego RunAsync serializuje dostęp do zdalnego FS); lokalna strona
    /// to zwykłe operacje na plikach. Zgłasza <see cref="Connected"/>/<see cref="Failed"/> ze zdalnego panelu.
    /// </summary>
    public partial class DualFilePanel : UserControl
    {
        private readonly FileTransferPanel _local;
        private readonly FileTransferPanel _remote;

        public event Action Connected;
        public event Action<string> Failed;

        public DualFilePanel(Func<IRemoteFs> remoteFactory)
        {
            InitializeComponent();
            _local = new FileTransferPanel(() => new LocalFs(), localMode: true);
            _remote = new FileTransferPanel(remoteFactory);
            LocalHost.Content = _local;
            RemoteHost.Content = _remote;

            _remote.Connected += () => Connected?.Invoke();
            _remote.Failed += m => Failed?.Invoke(m);
            _remote.CrossPaneDrop += OnDropOnRemote;   // lokalny plik upuszczony na zdalny → wyślij
            _local.CrossPaneDrop += OnDropOnLocal;     // zdalny plik upuszczony na lokalny → pobierz

            _local.RefreshAsync();   // lokalny nie wymaga połączenia — pokaż od razu
        }

        /// <summary>Odświeża oba panele (zdalny łączy się przy pierwszym listowaniu).</summary>
        public void RefreshAsync()
        {
            _local.RefreshAsync();
            _remote.RefreshAsync();
        }

        public void DisposePanel()
        {
            _local.DisposePanel();
            _remote.DisposePanel();
        }

        private async void ToRemote_Click(object sender, RoutedEventArgs e)
        {
            foreach (var it in _local.GetSelection()) await _remote.TransferInLocalFileAsync(it.Full);
        }

        private async void ToLocal_Click(object sender, RoutedEventArgs e)
        {
            bool any = false;
            foreach (var it in _remote.GetSelection())
                any |= await _remote.TransferOutToLocalAsync(it.Full, it.Name, it.IsDir, _local.CurrentDir);
            if (any) _local.RefreshAsync();
        }

        private async void OnDropOnRemote(FileDragData d)
        {
            foreach (var it in d.Items) await _remote.TransferInLocalFileAsync(it.Full);   // plik/katalog (Full = lokalna ścieżka)
        }

        private async void OnDropOnLocal(FileDragData d)
        {
            bool any = false;
            foreach (var it in d.Items)
                any |= await d.Source.TransferOutToLocalAsync(it.Full, it.Name, it.IsDir, _local.CurrentDir);
            if (any) _local.RefreshAsync();
        }
    }
}
