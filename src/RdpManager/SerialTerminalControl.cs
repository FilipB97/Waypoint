using System;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace RdpManager
{
    /// <summary>
    /// Terminal portu szeregowego (konsole urządzeń przez kabel) na bazie xterm.
    /// Ustawienia 8N1 bez kontroli przepływu; DTR/RTS włączone (konsole Cisco / przejściówki USB).
    /// </summary>
    public class SerialTerminalControl : XtermControl
    {
        private SerialPort _port;
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

        public Task ConnectAsync(string portName, int baud)
        {
            return Task.Run(() =>
            {
                DisposePort();
                var p = new SerialPort(portName, baud > 0 ? baud : 115200, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    DtrEnable = true,
                    RtsEnable = true
                };
                p.DataReceived += OnData;
                p.Open();
                if (IsTerminalDisposed) { try { p.Dispose(); } catch { } return; }   // karta zamknięta w trakcie otwierania portu
                _port = p;
                RaiseConnected();
            });
        }

        private void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            var p = _port;
            if (p == null || IsTerminalDisposed) return;
            try
            {
                int n = p.BytesToRead;
                if (n <= 0) return;
                var buf = new byte[n];
                n = p.Read(buf, 0, n);
                if (n <= 0) return;

                string text;
                lock (_utf8)
                {
                    var chars = new char[Encoding.UTF8.GetMaxCharCount(n)];
                    int c = _utf8.GetChars(buf, 0, n, chars, 0);
                    if (c == 0) return;
                    text = new string(chars, 0, c);
                }
                PostToTerminal(text);
            }
            catch { /* port w trakcie zamykania */ }
        }

        protected override void OnTerminalInput(string data)
        {
            var p = _port;
            if (p == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                p.Write(bytes, 0, bytes.Length);
            }
            catch { /* port w trakcie zamykania */ }
        }

        public override void Disconnect()
        {
            Task.Run(() =>
            {
                DisposePort();
                RaiseDisconnected(null);   // SerialPort nie ma zdarzenia zamknięcia — zgłaszamy sami
            });
        }

        private void DisposePort()
        {
            var p = _port;
            _port = null;
            if (p == null) return;
            try { p.DataReceived -= OnData; } catch { }
            try { p.Close(); } catch { }
            try { p.Dispose(); } catch { }
        }

        public override void DisposeTerminal()
        {
            MarkDisposed();
            DisposePort();
            base.DisposeTerminal();
        }
    }
}
