using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RdpManager
{
    /// <summary>
    /// Terminal Telnet (RFC 854) na bazie xterm. Minimalna negocjacja: odmawiamy wszystkich
    /// opcji (WILL→DONT, DO→WONT), subnegocjacje pomijamy — wystarcza dla sprzętu sieciowego.
    /// UWAGA: transmisja jawnym tekstem — tylko do zaufanych sieci.
    /// </summary>
    public class TelnetTerminalControl : XtermControl
    {
        private TcpClient _tcp;
        private NetworkStream _stream;
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private volatile bool _userClosed;
        private int _state;    // 0 dane, 1 po IAC, 2 po IAC+verb, 3 subnegocjacja, 4 subnegocjacja po IAC
        private byte _verb;

        public Task ConnectAsync(string host, int port)
        {
            return Task.Run(() =>
            {
                DisposeConnection();
                _userClosed = false;
                _state = 0;

                var tcp = new TcpClient { NoDelay = true };
                tcp.Connect(host, port > 0 ? port : 23);
                _tcp = tcp;
                _stream = tcp.GetStream();
                RaiseConnected();
                Task.Run(ReadLoop);
            });
        }

        private void ReadLoop()
        {
            var buf = new byte[4096];
            var payload = new List<byte>(4096);
            var stream = _stream;
            try
            {
                int n;
                while (stream != null && (n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    payload.Clear();
                    for (int i = 0; i < n; i++) Filter(buf[i], payload);
                    if (payload.Count == 0) continue;

                    string text;
                    lock (_utf8)
                    {
                        var bytes = payload.ToArray();
                        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                        int c = _utf8.GetChars(bytes, 0, bytes.Length, chars, 0);
                        if (c == 0) continue;
                        text = new string(chars, 0, c);
                    }
                    PostToTerminal(text);
                }
                RaiseDisconnected(null);   // serwer zamknął połączenie
            }
            catch (Exception ex)
            {
                RaiseDisconnected(_userClosed || IsTerminalDisposed ? null : ex.Message);
            }
        }

        // Maszyna stanów IAC — działa też przez granice buforów.
        private void Filter(byte b, List<byte> payload)
        {
            const byte IAC = 255, SB = 250, SE = 240;
            switch (_state)
            {
                case 0:
                    if (b == IAC) _state = 1; else payload.Add(b);
                    break;
                case 1:
                    if (b == IAC) { payload.Add(IAC); _state = 0; }            // IAC IAC = literalne 255
                    else if (b >= 251 && b <= 254) { _verb = b; _state = 2; }  // WILL/WONT/DO/DONT
                    else if (b == SB) _state = 3;
                    else _state = 0;                                           // NOP/GA/AYT/… — ignoruj
                    break;
                case 2:
                    Refuse(_verb, b);
                    _state = 0;
                    break;
                case 3:
                    if (b == IAC) _state = 4;
                    break;
                case 4:
                    _state = b == SE ? 0 : 3;
                    break;
            }
        }

        // Odmawiamy każdej opcji: WILL→DONT, DO→WONT (na WONT/DONT nie odpowiada się).
        private void Refuse(byte verb, byte option)
        {
            byte reply = verb == 251 ? (byte)254 : verb == 253 ? (byte)252 : (byte)0;
            if (reply == 0) return;
            try { _stream?.Write(new byte[] { 255, reply, option }, 0, 3); } catch { }
        }

        protected override void OnTerminalInput(string data)
        {
            var s = _stream;
            if (s == null) return;

            var bytes = Encoding.UTF8.GetBytes(data);
            // literalne 255 w danych musi być zdublowane (IAC IAC)
            int extra = 0;
            foreach (var b in bytes) if (b == 255) extra++;
            if (extra > 0)
            {
                var esc = new byte[bytes.Length + extra];
                int j = 0;
                foreach (var b in bytes) { esc[j++] = b; if (b == 255) esc[j++] = 255; }
                bytes = esc;
            }
            try { s.Write(bytes, 0, bytes.Length); } catch { /* połączenie w trakcie zamykania */ }
        }

        public override void Disconnect()
        {
            _userClosed = true;
            var t = _tcp;
            Task.Run(() => { try { t?.Close(); } catch { } });   // ReadLoop zakończy się i zgłosi Disconnected
        }

        private void DisposeConnection()
        {
            var s = _stream; var t = _tcp;
            _stream = null; _tcp = null;
            try { s?.Dispose(); } catch { }
            try { t?.Close(); } catch { }
        }

        public override void DisposeTerminal()
        {
            MarkDisposed();
            _userClosed = true;
            DisposeConnection();
            base.DisposeTerminal();
        }
    }
}
