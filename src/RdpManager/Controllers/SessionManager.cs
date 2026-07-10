using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AxMSTSCLib;
using MSTSCLib;
using RdpManager.Core;
using RdpManager.Models;
using RdpManager.ViewModels;

namespace RdpManager.Controllers
{
    /// <summary>
    /// Cykl życia sesji 8 protokołów: fabryka (OpenServer/LaunchServer/OpenUrl), łączenie i rozłączanie
    /// (BeginConnect/ConnectSession/ConnectVnc/ConnectTerm/ConnectFiles + Wire*Events), podgląd/kanwa/toolbar,
    /// podział ekranu, wyciąganie do okna i status. Wyniesione 1:1 z MainWindow (PR 6 planu
    /// docs/REFACTOR-MAINWINDOW.md, wzorzec „back-reference move-method"). Współdzielony rdzeń
    /// (_owner._sessions/_owner._active/_owner._paneLeft/_owner._paneRight/_owner._splitDropSession/_owner._sessionWindows/_owner._settings/_vm) ZOSTAJE
    /// w MainWindow (dostęp przez _owner.), więc odwołania w pozostałych kontrolerach się nie zmieniają.
    /// Jedyny zamierzony nie-mechaniczny fragment (B3/B4): RefreshIfActive + Session.MarkConnected/
    /// MarkDisconnected (współdzielone z SessionWindow.WireEvents).
    /// </summary>
    internal sealed class SessionManager
    {
        private readonly MainWindow _owner;

        private static string L(string key) => LocalizationManager.S(key);

        public SessionManager(MainWindow owner) => _owner = owner;

        // Zwija powtórzony epilog handlerów (~5×): po zdarzeniu sesji odśwież toolbar+kanwę, jeśli to aktywna.
        private void RefreshIfActive(Session s)
        {
            if (s == _owner._active) { UpdateToolbarMode(); UpdateCanvas(); }
        }

        internal void LaunchServer(ServerInfo server, bool autoConnect, bool forceNew = false)
        {
            // Terminale zawsze jako karta — osobne okno sesji (SessionWindow) jest RDP-owe.
            if (_owner._settings.OpenInNewWindowByDefault && server.Protocol == RemoteProtocol.Rdp) OpenInNewWindow(server);
            else OpenServer(server, autoConnect, forceNew);
        }

        // Wpis WWW: nie ma sesji — otwieramy panel webowy w domyślnej przeglądarce. Tylko http/https,
        // zob. Core.UrlValidation.
        private void OpenUrl(ServerInfo server)
        {
            string raw = (server.Host ?? "").Trim();
            if (raw.Length == 0) return;
            if (!Core.UrlValidation.TryNormalizeWebUrl(raw, out var uri))
            {
                SetStatus(string.Format(L("S.st.badurl"), raw), StatusKind.Error);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                _owner.RecordRecent(server);
            }
            catch (Exception ex) { SetStatus(string.Format(L("S.st.exception"), ex.Message), StatusKind.Error); }
        }

        internal void OpenServer(ServerInfo server, bool autoConnect = false, bool forceNew = false)
        {
            if (server.Protocol == RemoteProtocol.Http) { OpenUrl(server); return; }

            // Kontrolka (RDP/konsola) musi powstać przy widocznym kontenerze sesji. Widoki „Sessions" i „Rest"
            // dzielą ten sam _owner.SessionContainer, więc gdy już jesteśmy w którymś z nich — nie przełączaj (inaczej
            // otwarcie żądania REST wyrzucałoby z modułu REST na listę serwerów). Z innych widoków → sesje.
            if (_owner._currentView != "Sessions" && _owner._currentView != "Rest") _owner.ShowView("Sessions");
            if (!forceNew)
            {
                var existing = _owner._sessions.Find(x => x.Server == server);
                if (existing != null)
                {
                    Activate(existing);
                    if (autoConnect && !existing.Connected) BeginConnect(existing);
                    return;
                }
            }

            Session session;
            if (server.Protocol == RemoteProtocol.Telnet)
            {
                var term = new TelnetTerminalControl();
                _owner.SessionContainer.Children.Add(term);
                session = new Session(server, term);
                WireTermEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Serial)
            {
                var term = new SerialTerminalControl();
                _owner.SessionContainer.Children.Add(term);
                session = new Session(server, term);
                WireTermEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Ssh)
            {
                // SSH: terminal (WebView2 + xterm.js) zamiast kontrolki RDP; reszta cyklu życia wspólna.
                var term = new SshTerminalControl();
                term.TrustHostKey = AskTrustHostKey;              // TOFU klucza hosta (dialog na wątku UI)
                term.RequestKeyPassphrase = AskKeyPassphrase;     // zaszyfrowany klucz → prompt o passphrase
                _owner.SessionContainer.Children.Add(term);
                session = new Session(server, term);
                WireTermEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Sftp)
            {
                // SFTP jako osobny protokół: panel plików = widok sesji; łączy się leniwie (jak terminal).
                var conn = new SshConnectionFactory
                {
                    TrustHostKey = AskTrustHostKey,
                    RequestKeyPassphrase = AskKeyPassphrase
                };
                var panel = new DualFilePanel(() => conn.NewFs());
                _owner.SessionContainer.Children.Add(panel);
                session = new Session(server, panel, conn);
                WireFilesEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Ftp)
            {
                // FTP/FTPS jako osobny protokół: ten sam panel plików (IRemoteFs), konektor FluentFTP.
                var conn = new FtpConnector { TrustCertificate = AskTrustFtpsCert };   // TOFU certyfikatu (dialog na wątku UI)
                var panel = new DualFilePanel(() => conn.NewFs());
                _owner.SessionContainer.Children.Add(panel);
                session = new Session(server, panel, conn);
                WireFilesEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Rest)
            {
                // REST: konsola HTTP jako widok sesji. Narzędzie bez cyklu łączenia — gotowe od razu.
                var console = new RestConsole(server);
                // Konsola utrwaliła zmianę kolekcji (nazwa/metoda/struktura) → moduł w railu przebudowuje drzewo.
                console.CollectionChanged += () => { if (_owner._restMode) _owner.BuildRestModule(); };
                _owner.SessionContainer.Children.Add(console);
                session = new Session(server, console);
                session.Connected = true;
                _owner.RecordRecent(server);
            }
            else if (server.Protocol == RemoteProtocol.Vnc)
            {
                // VNC (RemoteViewing) — kontrolka WinForms w hoście WPF, jak RDP. Zdarzenia wiążemy przy połączeniu.
                var vnc = new RemoteViewing.Windows.Forms.VncControl
                {
                    AllowInput = true,
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    AllowRemoteCursor = true
                };
                var host = new WindowsFormsHost { Child = vnc };
                _owner.SessionContainer.Children.Add(host);
                host.UpdateLayout();
                session = new Session(server, vnc, host);
            }
            else
            {
                var rdp = new AxMsRdpClient11NotSafeForScripting();
                var host = new WindowsFormsHost();

                ((ISupportInitialize)rdp).BeginInit();
                rdp.Dock = System.Windows.Forms.DockStyle.Fill;
                host.Child = rdp;
                ((ISupportInitialize)rdp).EndInit();

                _owner.SessionContainer.Children.Add(host);
                host.UpdateLayout();
                try { ((System.Windows.Forms.Control)rdp).CreateControl(); } catch { }  // wymuś utworzenie kontrolki ActiveX

                session = new Session(server, rdp, host);
                session.Resizer = new RdpDynamicResolution(session, host);
                WireEvents(session);
                // W podziale: klik w panel (RDP przejmuje fokus klawiatury) czyni go aktywnym — karta i toolbar podążają.
                var focusTarget = session;
                host.GotKeyboardFocus += (s, e) => OnPaneFocused(focusTarget);
            }
            if (_owner.EffSavedPw(server) && CredentialStore.TryRead(_owner.EffCredTarget(server), out var savedPw))
                session.Password = savedPw;

            _owner._sessions.Add(session);
            session.TabButton = _owner._tabs.BuildTab(session);
            if (_owner._tabs.GroupOf(session) != null) _owner.RebuildTabStrip();   // serwer w grupie → renderuj w jej kontenerze
            else { _owner.TabStrip.Children.Add(session.TabButton); _owner.RefreshTabTitles(); }
            if (session.IsRest) _owner.SetTabStatus(session, ServerStatus.Online);   // narzędzie: gotowe od razu

            Activate(session);
            if (autoConnect) BeginConnect(session);
            _owner.PersistOpenSessions();
        }

        private bool CanAuto(Session s)
        {
            switch (s.Server.Protocol)
            {
                case RemoteProtocol.Telnet:
                case RemoteProtocol.Serial:
                    return true;   // logowanie (jeśli jest) dzieje się w terminalu
                case RemoteProtocol.Ssh:
                case RemoteProtocol.Sftp:
                    return !string.IsNullOrWhiteSpace(_owner.EffUser(s.Server))
                           && (!string.IsNullOrEmpty(s.Password) || !string.IsNullOrWhiteSpace(s.Server.PrivateKeyPath));
                case RemoteProtocol.Ftp:
                    return s.Server.FtpAnonymous
                           || (!string.IsNullOrWhiteSpace(_owner.EffUser(s.Server)) && !string.IsNullOrEmpty(s.Password));
                default:
                    return s.Server.UseWindowsAccount || !string.IsNullOrEmpty(s.Password);
            }
        }

        internal void Activate(Session session)
        {
            _owner._fs.HideFocusPeek();   // aktywacja sesji (np. klik z wysuniętego panelu) chowa peek (i przenosi panel z powrotem)
            _owner._active = session;
            // W podziale: aktywacja karty NIEbędącej panelem kończy podział (pokaż tę sesję pojedynczo).
            // Klik w kartę panelu tylko przenosi fokus (podział zostaje).
            if ((_owner._paneLeft != null || _owner._paneRight != null) && session != _owner._paneLeft && session != _owner._paneRight)
            { _owner._paneLeft = null; _owner._paneRight = null; }
            // Zwinięta grupa pokazuje aktywną kartę — po zmianie aktywnej trzeba przebudować pasek.
            if (_owner._tabs.HasCollapsedGroups) _owner.RebuildTabStrip();
            _owner.RefreshTabStyles();
            _owner.UpdateActiveRows();
            LoadToolbar(session);
            UpdateToolbarEnabled();
            UpdateToolbarMode();
            UpdateCanvas();
            SetStatus(session.Status, session.StatusKind);
            _owner.FsName.Text = session.Server.Name + " · " + session.Server.Host;
            _owner.UpdateImmersive();
        }

        // ---------- Podział ekranu (split-screen) ----------

        /// <summary>Wchodzi w podział: sesja `right` w prawym panelu, aktywna (lub pierwsza inna) RDP w lewym.
        /// Tylko RDP; wymaga dwóch różnych sesji RDP.</summary>
        internal void EnterSplit(Session right)
        {
            if (right == null || right.Server.Protocol != RemoteProtocol.Rdp) return;
            Session left = (_owner._active != null && _owner._active != right && _owner._active.Server.Protocol == RemoteProtocol.Rdp)
                ? _owner._active
                : _owner._sessions.FirstOrDefault(s => s != right && s.Server.Protocol == RemoteProtocol.Rdp);
            if (left == null) return;   // potrzebne dwie sesje RDP
            _owner._paneLeft = left;
            _owner._paneRight = right;
            Activate(right);            // fokus na nowy panel; Activate odświeży pasek/toolbar/status + UpdateCanvas (podział)
        }

        /// <summary>Kończy podział — pozostaje pojedynczy widok aktywnej sesji.</summary>
        internal void ExitSplit()
        {
            if (_owner._paneLeft == null && _owner._paneRight == null) return;
            _owner._paneLeft = null;
            _owner._paneRight = null;
            UpdateCanvas();
        }

        /// <summary>Klik w panel podziału (RDP przejął fokus klawiatury) → uczyń go aktywnym: podświetlenie
        /// karty i toolbar podążają za panelem, w którym pracujesz.</summary>
        private void OnPaneFocused(Session s)
        {
            if (_owner._paneLeft == null && _owner._paneRight == null) return;   // działa tylko w podziale
            if ((s != _owner._paneLeft && s != _owner._paneRight) || _owner._active == s) return;
            _owner._active = s;
            _owner.RefreshTabStyles();
            LoadToolbar(s);
            UpdateToolbarEnabled();
            SetStatus(s.Status, s.StatusKind);
        }

        /// <summary>Pokazuje strefę upuszczenia podziału na czas przeciągania karty (tylko RDP, ≥2 sesje RDP,
        /// bez aktywnego podziału, gdy jest gdzie ją położyć). Zwraca true, jeśli pokazano.</summary>
        internal bool ShowSplitDropZone(Session dragged)
        {
            if (dragged == null || dragged.Server.Protocol != RemoteProtocol.Rdp) return false;
            if (_owner._paneLeft != null || _owner._paneRight != null) return false;                 // już podzielone
            if (_owner._sessions.Count(x => x.Server.Protocol == RemoteProtocol.Rdp) < 2) return false;
            if (_owner.SessionContainer.ActualWidth < 100 || _owner.SessionContainer.ActualHeight < 100) return false;
            _owner.SplitDropBorder.Width = _owner.SessionContainer.ActualWidth;    // dopasuj do obszaru sesji (host renderuje 1:1)
            _owner.SplitDropBorder.Height = _owner.SessionContainer.ActualHeight;
            _owner._splitDropSession = dragged;
            _owner.SplitDropZone.IsOpen = true;
            return true;
        }

        internal void HideSplitDropZone()
        {
            _owner.SplitDropZone.IsOpen = false;
            _owner._splitDropSession = null;
        }

        /// <summary>
        /// Steruje kanwą: aktywna kontrolka RDP widoczna tylko gdy połączona; w przeciwnym razie
        /// nakładka (spinner „Łączenie…" albo „Rozłączono" + przycisk ponownego połączenia).
        /// </summary>
        private void UpdateCanvas()
        {
            bool has = _owner._active != null;
            _owner.EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has) _owner.BuildEmptyRecent();   // odśwież chipy „ostatnie" przy każdym powrocie do pustego stanu

            // Tryb podziału: dwie sesje RDP widoczne naraz (lewy panel = kol.0, prawy = kol.2, splitter w kol.1).
            if (_owner._paneLeft != null && _owner._paneRight != null)
            {
                foreach (var s in _owner._sessions)
                {
                    bool pane = s == _owner._paneLeft || s == _owner._paneRight;
                    if (pane) Grid.SetColumn(s.View, s == _owner._paneRight ? 2 : 0);
                    if (s.Resizer != null) s.Resizer.FitToWindow = pane;   // panele skalują pulpit do swojej połówki (zawsze się mieści)
                    s.View.Visibility = (pane && s.Connected) ? Visibility.Visible : Visibility.Collapsed;
                }
                if (_owner.PaneColRight.Width.GridUnitType != GridUnitType.Star)   // wejście w podział = 50/50; drag splittera zachowany
                    _owner.PaneColRight.Width = new GridLength(1, GridUnitType.Star);
                _owner.PaneSplitter.Visibility = Visibility.Visible;
                _owner.SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            // Bez podziału: pojedynczy widok w kol.0 (przywróć pełną szerokość po ewentualnym dragu splittera).
            _owner.PaneColLeft.Width = new GridLength(1, GridUnitType.Star);
            _owner.PaneColRight.Width = new GridLength(0);
            _owner.PaneSplitter.Visibility = Visibility.Collapsed;

            // Terminale (SSH/Telnet/Serial): widoczne od razu — statusy łączenia piszą do siebie.
            foreach (var s in _owner._sessions)
            {
                Grid.SetColumn(s.View, 0);
                if (s.Resizer != null) s.Resizer.FitToWindow = false;   // pojedynczy widok = natywna, ostra rozdzielczość
                s.View.Visibility = (s == _owner._active && (s.Connected || s.IsTerm || s.IsFiles)) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!has)
            {
                _owner.SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            // Nakładka nie dla terminali (HWND by ją zakrył) ani plików (panel ma własny pasek statusu).
            if (_owner._active.Connected || _owner._active.IsTerm || _owner._active.IsFiles)
            {
                _owner.SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            _owner.SessionOverlay.Visibility = Visibility.Visible;
            bool connecting = _owner._active.StatusKind == StatusKind.Connecting;
            _owner.OverlaySpinner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
            _owner.OverlayReconnect.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
            _owner.OverlayReconnect.Content = L("S.reconnect");
            _owner.OverlayTitle.Text = connecting
                ? string.Format(L("S.st.connecting"), _owner._active.Server.Host)
                : (_owner._active.StatusKind == StatusKind.Error ? L("S.st.disconnectedShort") : L("S.st.ready"));
            _owner.OverlayMsg.Text = connecting ? "" : _owner._active.Status;
        }

        internal void OverlayAction_Click(object sender, RoutedEventArgs e)
        {
            // Nie przez Connect_Click: pasek z hasłem bywa ukryty (tryb skupienia) — działaj na modelu
            // sesji i dopytaj dialogiem, gdy brakuje poświadczeń.
            if (_owner._active == null) return;
            if (CanAuto(_owner._active)) ConnectSession(_owner._active);
            else PromptAndConnect(_owner._active, null);
        }

        internal void LoadToolbar(Session s)
        {
            _owner.CfAvatar.Background = _owner.AvatarBrush(s.Server);
            _owner.CfAvatarText.Text = MainWindow.ServerInitials(s.Server);
            _owner.CfName.Text = s.Server.Name;
            _owner.CfHost.Text = s.Server.Host + ":" + s.Server.Port;
            // Konto Windows tylko dla RDP; Telnet/Serial nie mają pól poświadczeń w ogóle.
            _owner.WinAuthCheck.Visibility = s.Server.Protocol == RemoteProtocol.Rdp ? Visibility.Visible : Visibility.Collapsed;
            _owner.WinAuthCheck.IsChecked = s.Server.UseWindowsAccount;
            _owner.PassBox.Password = s.Password ?? "";
            UpdatePassVisibility();
        }

        /// <summary>Wysyła jedną komendę (z Enterem) do wszystkich połączonych sesji SSH naraz.</summary>
        internal void BroadcastToSsh()
        {
            var targets = _owner._sessions.Where(s => s.IsSsh && s.Connected).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(L("S.bc.none"), L("S.m.broadcast"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new InputDialog(L("S.m.broadcast"), string.Format(L("S.bc.prompt"), targets.Count), "") { Owner = _owner };
            if (dlg.ShowDialog() != true) return;
            string cmd = dlg.Value;
            if (string.IsNullOrEmpty(cmd)) return;

            int sent = 0;
            foreach (var s in targets)
            {
                try { s.Ssh.SendText(cmd + "\n"); sent++; } catch { /* sesja właśnie padła — pomiń */ }
            }
            SetStatus(string.Format(L("S.bc.sent"), sent), StatusKind.Ok);
        }

        internal void CloseOtherSessions(Session keep)
        {
            var others = _owner._sessions.Where(s => s != keep).ToList();
            int connected = others.Count(s => s.Connected);
            // Jedno zbiorcze potwierdzenie zamiast dialogu per sesja.
            if (connected > 0 && _owner._settings.ConfirmCloseConnected &&
                MessageBox.Show(string.Format(L("S.msg.closeothers"), connected),
                    L("S.m.closeothers"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            foreach (var s in others) CloseSession(s);
        }

        /// <summary>Otwiera drugą, niezależną sesję do tego samego serwera (osobna zakładka).</summary>
        internal void DuplicateSession(Session s) => OpenServer(s.Server, autoConnect: true, forceNew: true);

        internal void RequestCloseSession(Session session)
        {
            if (session.Connected && _owner._settings.ConfirmCloseConnected &&
                MessageBox.Show(string.Format(L("S.msg.closesession"), session.Server.Name), L("S.msg.closesession.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            CloseSession(session);
        }

        internal void CloseSession(Session session)
        {
            if (session.IsTerm)
            {
                try { session.Term.DisposeTerminal(); } catch { }
                _owner.SessionContainer.Children.Remove(session.Term);
            }
            else if (session.IsVnc)
            {
                // Wyzeruj Client PRZED Close/Dispose — zakolejkowany OnVncEnded (Closed z wątku
                // roboczego) zobaczy null i stanie się no-opem, zamiast dotykać zniszczonej kontrolki.
                var vc = session.Vnc.Client;
                session.Vnc.Client = null;
                try { vc?.Close(); } catch { }
                _owner.SessionContainer.Children.Remove(session.Host);
                try { session.Host.Dispose(); } catch { }
            }
            else if (session.IsFiles)
            {
                try { session.Files.DisposePanel(); } catch { }
                _owner.SessionContainer.Children.Remove(session.Files);
            }
            else if (session.IsRest)
            {
                try { session.Rest.DisposeConsole(); } catch { }
                _owner.SessionContainer.Children.Remove(session.Rest);
            }
            else
            {
                try { session.Rdp.Disconnect(); } catch { /* nie połączona */ }
                session.Resizer?.Dispose();

                _owner.SessionContainer.Children.Remove(session.Host);
                try { session.Host.Dispose(); } catch { }   // zwalnia hosta i kontrolkę ActiveX (HWND)
            }
            _owner._tabs.OnSessionClosed(session);   // odłącz kartę od paska/grupy + wyczyść wpisy elementów karty
            _owner._sessions.Remove(session);
            _owner.RebuildTabStrip();               // przebuduj pasek (kontenery grup + tytuły)
            _owner.PersistOpenSessions();

            // Zamknięcie panelu podziału → zakończ podział i pokaż drugi panel pojedynczo.
            Session survivingPane = session == _owner._paneLeft ? _owner._paneRight : (session == _owner._paneRight ? _owner._paneLeft : null);
            if (survivingPane != null)
            {
                _owner._paneLeft = null; _owner._paneRight = null;
                if (_owner._sessions.Contains(survivingPane)) { _owner._active = null; Activate(survivingPane); return; }
            }

            if (_owner._active == session)
            {
                _owner._active = null;
                if (_owner._sessions.Count > 0) Activate(_owner._sessions[_owner._sessions.Count - 1]);
                else
                {
                    _owner.UpdateActiveRows();
                    UpdateToolbarEnabled();
                    UpdateToolbarMode();
                    UpdateCanvas();
                    SetStatus("—", StatusKind.Info);
                }
            }
            _owner.UpdateImmersive();
        }

        // ---------- Połączenie ----------

        internal void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_owner._active == null) return;
            var s = _owner._active;
            s.Server.UseWindowsAccount = _owner.WinAuthCheck.IsChecked == true;
            if (!s.Server.UseWindowsAccount) s.Password = _owner.PassBox.Password;
            ConnectSession(s);
        }

        /// <summary>Łączy sesję; gdy brak poświadczeń (i nie konto Windows) — pyta o nie promptem.</summary>
        internal void BeginConnect(Session s)
        {
            if (s.IsRest) return;   // REST: narzędzie bez cyklu łączenia (wysyłka per żądanie w konsoli)
            if (CanAuto(s)) ConnectSession(s);
            else PromptAndConnect(s, null);
        }

        /// <summary>Pokazuje prompt poświadczeń i po zatwierdzeniu łączy (np. „Połącz jako…" lub po błędzie logowania).</summary>
        internal void PromptAndConnect(Session s, string reason)
        {
            var dlg = new CredentialPromptWindow(s.Server, s.Password, reason) { Owner = _owner };
            if (dlg.ShowDialog() != true) return;

            s.Server.UseWindowsAccount = false;
            // Jawne „Połącz jako…" (reason != null) = porzuć profil i użyj wpisanych poświadczeń. Prompt-fallback
            // przy braku hasła (reason == null) profil ZOSTAWIA — łączymy loginem z profilu + wpisanym hasłem.
            if (reason != null) s.Server.CredentialProfileId = "";
            s.Server.Username = dlg.EnteredUser;
            s.Server.Domain = dlg.EnteredDomain;
            s.Password = dlg.EnteredPassword;
            if (dlg.SavePassword)
            {
                s.Server.SavePassword = true;
                _owner.SaveCredential(s.Server, dlg.EnteredPassword);
            }
            else
            {
                // Odznaczenie „zapisz" ma być honorowane: usuń też ewentualny stary wpis z sejfu.
                s.Server.SavePassword = false;
                CredentialStore.Delete(s.Server.CredTarget);
            }
            _owner.PersistServers();
            if (s == _owner._active) LoadToolbar(s);
            ConnectSession(s);
        }

        internal void PassBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _owner.ConnectBtn.IsEnabled) Connect_Click(sender, e);
        }

        /// <summary>Łączy sesję na podstawie jej modelu (bez odczytu z formularza) — używane też z flyoutu.</summary>
        private void ConnectSession(Session s)
        {
            if (s.IsRest) return;   // REST: brak łączenia; wysyłka odbywa się w konsoli
            if (s.IsTerm) { ConnectTerm(s); return; }
            if (s.IsVnc) { ConnectVnc(s); return; }
            if (s.IsFiles) { ConnectFiles(s); return; }

            try { s.Rdp.Disconnect(); } catch { /* nie połączona */ }
            s.LoggedIn = false;

            try
            {
                RdpConnect.Apply(s.Rdp, s.Server, _owner._settings, _owner.EffUser(s.Server), _owner.EffDomain(s.Server), s.Password);

                // RemoteApp: program/alias zamiast pełnego pulpitu (ustawiane PRZED Connect).
                try
                {
                    var rp = s.Rdp.RemoteProgram2;
                    bool useApp = !string.IsNullOrWhiteSpace(s.Server.RemoteAppProgram);
                    rp.RemoteProgramMode = useApp;
                    if (useApp)
                    {
                        rp.RemoteApplicationName = string.IsNullOrWhiteSpace(s.Server.Name)
                            ? s.Server.RemoteAppProgram.Trim() : s.Server.Name.Trim();
                        rp.RemoteApplicationProgram = s.Server.RemoteAppProgram.Trim();
                        rp.RemoteApplicationArgs = s.Server.RemoteAppArgs ?? "";
                    }
                }
                catch { /* starsza kontrolka bez RemoteProgram2 — łączymy jako pełny pulpit */ }

                s.Rdp.Connect();
                SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
            }
            catch (Exception ex)
            {
                SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
            }
        }

        /// <summary>Jednorazowe (per instalacja) ostrzeżenie o braku szyfrowania: Telnet / klasyczne VNC.</summary>
        private void WarnUnencrypted(RemoteProtocol proto)
        {
            bool already = proto == RemoteProtocol.Telnet ? _owner._settings.TelnetWarned
                         : proto == RemoteProtocol.Vnc ? _owner._settings.VncWarned
                         : proto == RemoteProtocol.Ftp ? _owner._settings.FtpWarned : true;
            if (already) return;

            if (proto == RemoteProtocol.Telnet) _owner._settings.TelnetWarned = true;
            else if (proto == RemoteProtocol.Vnc) _owner._settings.VncWarned = true;
            else _owner._settings.FtpWarned = true;
            SettingsStore.Save(_owner._settings);

            string k = proto == RemoteProtocol.Telnet ? "telnet" : proto == RemoteProtocol.Vnc ? "vnc" : "ftp";
            MessageBox.Show(L("S.warn." + k), L("S.warn." + k + ".title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------- VNC (RemoteViewing) ----------

        /// <summary>Łączy sesję VNC: nowy VncClient, zdarzenia na wątek UI, handshake w tle (blokuje).</summary>
        private void ConnectVnc(Session s)
        {
            WarnUnencrypted(RemoteProtocol.Vnc);
            try
            {
                var client = new RemoteViewing.Vnc.VncClient();
                client.Connected += (o, e) => _owner.Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(s.Vnc?.Client, client)) OnVncConnected(s); }));
                client.ConnectionFailed += (o, e) => _owner.Dispatcher.BeginInvoke(new Action(() => OnVncEnded(s, client)));
                client.Closed += (o, e) => _owner.Dispatcher.BeginInvoke(new Action(() => OnVncEnded(s, client)));
                s.Vnc.Client = client;
                s.LoggedIn = false;

                char[] pw = (s.Password ?? "").ToCharArray();
                var opts = new RemoteViewing.Vnc.VncClientConnectOptions { ShareDesktop = true, Password = pw };
                opts.PasswordRequiredCallback = c => pw;   // gdy serwer poprosi — to samo hasło (puste => auth padnie)

                _owner.SetTabStatus(s, ServerStatus.Idle);
                SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
                if (s == _owner._active) UpdateCanvas();

                string host = s.Server.Host;
                int port = s.Server.Port > 0 ? s.Server.Port : 5900;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { client.Connect(host, port, opts); }
                    catch (Exception ex)
                    {
                        _owner.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
                            OnVncEnded(s, client);
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
            }
        }

        private void OnVncConnected(Session s)
        {
            s.Connected = true;
            s.LoggedIn = true;
            _owner.RecordRecent(s.Server);
            ConnectionLog.Append("CONNECTED", s.Server);
            _owner.SetTabStatus(s, ServerStatus.Online);
            SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
            if (s == _owner._active) { UpdateToolbarMode(); UpdateCanvas(); try { s.Vnc.Focus(); } catch { } }
        }

        // Failed i Closed mogą przyjść oba — strażnik po tożsamości klienta wykonuje obsługę raz.
        private void OnVncEnded(Session s, RemoteViewing.Vnc.VncClient client)
        {
            if (s.Vnc == null || !ReferenceEquals(s.Vnc.Client, client)) return;
            s.Vnc.Client = null;
            bool was = s.Connected;
            s.Connected = false;
            _owner.SetTabStatus(s, ServerStatus.Offline);
            ConnectionLog.Append(was ? "DISCONNECTED" : "FAILED", s.Server);
            if (!s.Server.SavePassword) s.Password = "";
            if (was) SetSessionStatus(s, string.Format(L("S.st.disconnected"), "VNC"), StatusKind.Error);
            else if (s.StatusKind != StatusKind.Error) SetSessionStatus(s, L("S.st.disconnectedShort"), StatusKind.Error);
            RefreshIfActive(s);
        }

        // Panel plików SFTP przy aktywnej sesji SSH (przycisk folderu na pasku stanu).
        internal void Files_Click(object sender, RoutedEventArgs e)
        {
            if (_owner._active?.IsSsh == true) _owner._active.Ssh.ToggleFiles();
        }

        internal void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_owner._active == null) return;
            if (_owner._active.IsRest) return;   // REST: brak połączenia do zerwania
            if (_owner._active.IsTerm) { _owner._active.Term.Disconnect(); return; }
            if (_owner._active.IsVnc) { try { _owner._active.Vnc.Client?.Close(); } catch { } return; }
            try { _owner._active.Rdp.Disconnect(); } catch (Exception ex) { SetSessionStatus(_owner._active, string.Format(L("S.st.disconnecting"), ex.Message), StatusKind.Error); }
        }

        internal void SendCtrlAltDel_Click(object sender, RoutedEventArgs e) => SendCtrlAltDel(_owner._active);

        // Wysyła zdalne Ctrl+Alt+Del. OCX RDP nie ma na to scriptowalnej metody, więc — jak w mstsc — dajemy
        // klientowi Ctrl+Alt+End, który z fokusem na kontrolce tłumaczy je na zdalną sekwencję SAS.
        internal void SendCtrlAltDel(Session s)
        {
            if (s == null || s.Server.Protocol != RemoteProtocol.Rdp || !s.Connected) return;
            if (s != _owner._active && s != _owner._paneLeft && s != _owner._paneRight) Activate(s);   // sesja musi być widoczna
            // Fokus MUSI trafić na kontrolkę OCX, nie na przycisk WPF — inaczej globalny MainWindow.keybd_event poszedłby
            // w próżnię. WindowsFormsHost.Focus() + WinForms Focus() nie zawsze przenoszą fokus za granicę hosta.
            try { s.Host?.Focus(); s.Rdp.Focus(); } catch { }
            // Po przetworzeniu fokusu (Input) wstrzykujemy Ctrl↓ Alt↓ End↓ End↑ Alt↑ Ctrl↑.
            _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                // MainWindow.keybd_event jest globalny — wyślij TYLKO gdy nasze okno jest na pierwszym planie, inaczej
                // klawisze trafiłyby do aplikacji, na którą użytkownik zdążył przełączyć (iniekcja jest odroczona).
                if (MainWindow.GetForegroundWindow() != new WindowInteropHelper(_owner).Handle) return;
                // Natywne MainWindow.SetFocus na uchwyt OCX tuż przed iniekcją — najpewniejszy sposób, by klawisze
                // trafiły do sesji RDP (nasze okno jest na pierwszym planie, więc MainWindow.SetFocus na dziecko działa).
                try { MainWindow.SetFocus(s.Rdp.Handle); } catch { }
                // End = klawisz ROZSZERZONY; bez MainWindow.KEYEVENTF_EXTENDEDKEY bywa mylony z numpad-1 (zależnie od NumLock),
                // przez co OCX nie rozpoznaje Ctrl+Alt+End jako zdalnego SAS.
                MainWindow.keybd_event(MainWindow.VK_CONTROL, 0, 0, UIntPtr.Zero);
                MainWindow.keybd_event(MainWindow.VK_MENU, 0, 0, UIntPtr.Zero);
                MainWindow.keybd_event(MainWindow.VK_END, 0, MainWindow.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                MainWindow.keybd_event(MainWindow.VK_END, 0, MainWindow.KEYEVENTF_EXTENDEDKEY | MainWindow.KEYEVENTF_KEYUP, UIntPtr.Zero);
                MainWindow.keybd_event(MainWindow.VK_MENU, 0, MainWindow.KEYEVENTF_KEYUP, UIntPtr.Zero);
                MainWindow.keybd_event(MainWindow.VK_CONTROL, 0, MainWindow.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // ---------- SSH ----------

        /// <summary>Łączy sesję terminalową (SSH/Telnet/Serial): inicjalizuje xterm i transport w tle.</summary>
        private async void ConnectTerm(Session s)
        {
            // SSH wymaga loginu (nie ma odpowiednika konta Windows) — dopytaj, jeśli brak.
            if (s.IsSsh && string.IsNullOrWhiteSpace(_owner.EffUser(s.Server))) { PromptAndConnect(s, null); return; }
            if (s.Server.Protocol == RemoteProtocol.Telnet) WarnUnencrypted(RemoteProtocol.Telnet);
            try
            {
                _owner.SetTabStatus(s, ServerStatus.Idle);
                SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
                if (s == _owner._active) UpdateCanvas();

                var (cols, rows) = await s.Term.InitAsync();
                string target = s.IsSsh
                    ? _owner.EffUser(s.Server) + "@" + s.Server.Host + ":" + s.Server.Port
                    : s.Server.Host + (s.Server.Protocol == RemoteProtocol.Serial ? " @" + s.Server.Port : ":" + s.Server.Port);
                s.Term.WriteLocal("\x1b[90m" + string.Format(L("S.st.connecting"), target) + "\x1b[0m\r\n");

                switch (s.Server.Protocol)
                {
                    case RemoteProtocol.Telnet:
                        await ((TelnetTerminalControl)s.Term).ConnectAsync(s.Server.Host, s.Server.Port);
                        break;
                    case RemoteProtocol.Serial:
                        await ((SerialTerminalControl)s.Term).ConnectAsync(s.Server.Host, s.Server.Port);
                        break;
                    default:
                        await s.Ssh.ConnectAsync(_owner.ConnectIdentity(s.Server), s.Password, cols, rows);
                        break;
                }
            }
            catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                _owner.SetTabStatus(s, ServerStatus.Offline);
                SetSessionStatus(s, L("S.ssh.nowebview"), StatusKind.Error);
                RefreshIfActive(s);
            }
            catch (Renci.SshNet.Common.SshAuthenticationException ex)
            {
                _owner.SetTabStatus(s, ServerStatus.Offline);
                s.Term.WriteLocal("\r\n\x1b[91m" + ex.Message + "\x1b[0m\r\n");
                SetSessionStatus(s, ex.Message + "  " + L("S.st.hint.creds"), StatusKind.Error);
                RefreshIfActive(s);
            }
            catch (Exception ex)
            {
                _owner.SetTabStatus(s, ServerStatus.Offline);
                s.Term.WriteLocal("\r\n\x1b[91m" + ex.Message + "\x1b[0m\r\n");
                SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
                RefreshIfActive(s);
            }
        }

        /// <summary>Zdarzenia terminala (SSH/Telnet/Serial) → stan sesji/karty (marshalowane na wątek UI).</summary>
        private void WireTermEvents(Session s)
        {
            s.Term.Connected += () => _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                s.MarkConnected();   // Connected = true + wpis „CONNECTED" (dedup B3/B4)
                _owner.RecordRecent(s.Server);
                _owner.SetTabStatus(s, ServerStatus.Online);
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
                if (s == _owner._active) { UpdateToolbarMode(); UpdateCanvas(); s.Term.FocusTerminal(); }
            }));
            if (s.Ssh != null)
                s.Ssh.TunnelStatus += (spec, ok, err) => _owner.Dispatcher.BeginInvoke(new Action(() =>
                    s.Term.WriteLocal(ok
                        ? "\x1b[92m" + string.Format(L("S.ssh.tunnel.up"), spec) + "\x1b[0m\r\n"
                        : "\x1b[91m" + string.Format(L("S.ssh.tunnel.fail"), spec, err) + "\x1b[0m\r\n")));
            s.Term.Disconnected += reason => _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                bool was = s.Connected;
                s.MarkDisconnected(was);   // Connected/LoggedIn=false + log + wyczyść hasło (dedup B3/B4)
                _owner.SetTabStatus(s, ServerStatus.Offline);

                string msg = string.Format(L("S.st.disconnected"),
                    string.IsNullOrWhiteSpace(reason) ? s.Server.Protocol.ToString().ToLowerInvariant() : reason);
                s.Term.WriteLocal("\r\n\x1b[91m" + msg + "\x1b[0m\r\n");
                SetSessionStatus(s, msg, StatusKind.Error);
                RefreshIfActive(s);
            }));
        }

        // Wspólne prompty klucza hosta (TOFU) i passphrase — używane przez sesje SSH i SFTP.
        private bool AskTrustHostKey(string hostPort, string fp, bool changed) => (bool)_owner.Dispatcher.Invoke(new Func<bool>(() =>
            MessageBox.Show(_owner,
                string.Format(L(changed ? "S.ssh.hostkey.changed" : "S.ssh.hostkey.new"), hostPort, fp),
                L("S.ssh.hostkey.title"), MessageBoxButton.YesNo,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
                changed ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes));

        private string AskKeyPassphrase(string path) => (string)_owner.Dispatcher.Invoke(new Func<string>(() =>
        {
            var dlg = new InputDialog(L("S.ssh.keypass.title"),
                string.Format(L("S.ssh.keypass.label"), System.IO.Path.GetFileName(path)),
                "", masked: true) { Owner = _owner };
            return dlg.ShowDialog() == true ? dlg.Value : null;
        }));

        // TOFU certyfikatu FTPS — ten sam wzorzec co AskTrustHostKey (SSH), inny magazyn (FtpsCertPinning).
        private bool AskTrustFtpsCert(string hostPort, string fp, bool changed) => (bool)_owner.Dispatcher.Invoke(new Func<bool>(() =>
            MessageBox.Show(_owner,
                string.Format(L(changed ? "S.ftps.cert.changed" : "S.ftps.cert.new"), hostPort, fp),
                L("S.ftps.cert.title"), MessageBoxButton.YesNo,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
                changed ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes));

        // SFTP jako osobny protokół: panel łączy się leniwie; identyczność (login z profilu) + hasło ustawiamy tutaj.
        private void ConnectFiles(Session s)
        {
            bool anon = s.Server.Protocol == RemoteProtocol.Ftp && s.Server.FtpAnonymous;
            if (!anon && string.IsNullOrWhiteSpace(_owner.EffUser(s.Server))) { PromptAndConnect(s, null); return; }
            if (s.Server.Protocol == RemoteProtocol.Ftp && s.Server.FtpEncryption == 2) WarnUnencrypted(RemoteProtocol.Ftp);
            _owner.SetTabStatus(s, ServerStatus.Idle);
            SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
            if (s == _owner._active) UpdateCanvas();
            s.FilesConn.SetIdentity(_owner.ConnectIdentity(s.Server), s.Password);
            s.Files.RefreshAsync();   // łączy leniwie; Connected/Failed aktualizują kartę i status
        }

        /// <summary>Zdarzenia panelu plików (SFTP/FTP) → stan sesji/karty (marshalowane na wątek UI).</summary>
        private void WireFilesEvents(Session s)
        {
            s.Files.Connected += () => _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                s.MarkConnected();   // Connected = true + wpis „CONNECTED" (dedup B3/B4)
                _owner.RecordRecent(s.Server);
                _owner.SetTabStatus(s, ServerStatus.Online);
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
                RefreshIfActive(s);
            }));
            s.Files.Failed += reason => _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                bool was = s.Connected;
                s.MarkDisconnected(was);   // Connected/LoggedIn=false + log + wyczyść hasło (dedup B3/B4)
                _owner.SetTabStatus(s, ServerStatus.Offline);
                SetSessionStatus(s, string.Format(L("S.st.disconnected"),
                    string.IsNullOrWhiteSpace(reason) ? "sftp" : reason), StatusKind.Error);
                RefreshIfActive(s);
            }));
        }

        private void WireEvents(Session s)
        {
            s.Rdp.OnConnecting += (o, a) =>
            {
                SetSessionStatus(s, L("S.st.connectingShort"), StatusKind.Connecting);
                _owner.SetTabStatus(s, ServerStatus.Idle);
                if (s == _owner._active) UpdateCanvas();
            };
            s.Rdp.OnConnected += (o, a) =>
            {
                s.MarkConnected();   // Connected = true + wpis „CONNECTED" (dedup B3/B4)
                _owner.RecordRecent(s.Server);
                _owner.SetTabStatus(s, ServerStatus.Online);
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
                RefreshIfActive(s);
            };
            s.Rdp.OnLoginComplete += (o, a) =>
            {
                s.LoggedIn = true;
                s.Resizer?.ApplyInitial();
                // Odśwież kanwę także dla panelu podziału (nie tylko aktywnej sesji), by po zalogowaniu stał się widoczny.
                if (s == _owner._active || s == _owner._paneLeft || s == _owner._paneRight) { UpdateToolbarMode(); UpdateCanvas(); try { s.Rdp.Focus(); } catch { } }
            };
            s.Rdp.OnDisconnected += (o, a) =>
            {
                bool wasLoggedIn = s.LoggedIn;
                // dedup B3/B4: Connected/LoggedIn=false + log DISCONNECTED/FAILED + wyczyść hasło (gdy nie zapisane w CredMan)
                s.MarkDisconnected(wasLoggedIn);
                _owner.SetTabStatus(s, ServerStatus.Offline);

                string msg = string.Format(L("S.st.disconnected"), DescribeDisconnect(s.Rdp, a.discReason));
                if (!wasLoggedIn)
                {
                    msg += "  " + (s.Server.UseWindowsAccount
                        ? L("S.st.hint.winauth")
                        : L("S.st.hint.creds"));
                }
                SetSessionStatus(s, msg, StatusKind.Error);
                RefreshIfActive(s);
            };
            s.Rdp.OnFatalError += (o, a) =>
            {
                s.Connected = false;
                _owner.SetTabStatus(s, ServerStatus.Offline);
                SetSessionStatus(s, string.Format(L("S.st.fatal"), a.errorCode), StatusKind.Error);
                RefreshIfActive(s);
            };
            // Fullscreen kontrolki (ścieżka multimon) — tylko komunikaty statusu.
            s.Rdp.OnEnterFullScreenMode += (o, a) =>
                SetSessionStatus(s, L("S.st.multimon"), StatusKind.Info);
            s.Rdp.OnLeaveFullScreenMode += (o, a) =>
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
        }
        internal void WinAuth_Changed(object sender, RoutedEventArgs e) => UpdatePassVisibility();

        private bool ActiveHasNoCreds()
            => _owner._active != null && (_owner._active.Server.Protocol == RemoteProtocol.Telnet
                                   || _owner._active.Server.Protocol == RemoteProtocol.Serial);

        private void UpdatePassVisibility()
        {
            _owner.CfPassGroup.Visibility = (ActiveHasNoCreds() || _owner.WinAuthCheck.IsChecked == true)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- Pasek sesji: dwa stany ----------

        internal void UpdateToolbarMode()
        {
            // W pełnym ekranie widocznością paska/zakładek steruje Enter/ExitFullscreen — nie dotykamy jej tutaj.
            if (!_owner._isFullscreen)
            {
                bool has = _owner._active != null;
                bool conn = has && _owner._active.Connected;
                bool immersive = _owner.IsImmersive();
                // Pasek połączenia (z hasłem) tylko PRZED połączeniem; po połączeniu — brak paska (więcej miejsca),
                // a akcje sesji przenoszą się na prawy koniec paska kart (_owner.SessionActions). W skupieniu: FocusControls.
                _owner.SessionToolbar.Visibility = (has && !immersive && !conn) ? Visibility.Visible : Visibility.Collapsed;
                _owner.SessionActions.Visibility = (conn && !immersive) ? Visibility.Visible : Visibility.Collapsed;
                _owner.TabStripHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_owner._active == null) return;

            _owner.FilesBtn.Visibility = _owner._active.IsSsh ? Visibility.Visible : Visibility.Collapsed;   // SFTP tylko dla SSH
            _owner.CadBtn.Visibility = _owner._active.Server.Protocol == RemoteProtocol.Rdp ? Visibility.Visible : Visibility.Collapsed;   // Ctrl+Alt+Del tylko dla RDP
        }

        internal void UpdateToolbarEnabled()
        {
            bool has = _owner._active != null;
            _owner.WinAuthCheck.IsEnabled = has;
            _owner.PassBox.IsEnabled = has;
            _owner.ConnectBtn.IsEnabled = has;
        }
        internal void OpenInNewWindow(ServerInfo server, string password = null)
        {
            if (server == null) return;
            _owner.RecordRecent(server);
            string pw = password ?? "";
            if (string.IsNullOrEmpty(pw) && _owner.EffSavedPw(server)) CredentialStore.TryRead(_owner.EffCredTarget(server), out pw);
            var win = new SessionWindow(server, _owner._settings, pw, _owner.EffUser(server), _owner.EffDomain(server), _owner.PersistServers, DockSessionFromWindow);
            _owner._sessionWindows.Add(win);
            win.Closed += (s, e) => _owner._sessionWindows.Remove(win);
            win.Show();
            win.Activate();
        }

        /// <summary>„Wyciąga" zakładkę do osobnego okna: zamyka kartę i otwiera okno sesji tego serwera
        /// (RDP łączy ponownie — wraca do tej samej sesji po stronie serwera). Przenosi hasło z pamięci.</summary>
        internal void TearOffToWindow(Session s)
        {
            if (s == null) return;
            var server = s.Server;
            var pw = s.Password;
            CloseSession(s);
            OpenInNewWindow(server, pw);
        }

        /// <summary>„Dokuje" okno sesji z powrotem jako kartę w managerze (callback wołany z SessionWindow):
        /// otwiera nową kartę tego serwera i łączy (reconnect wznawia sesję serwera). Hasło przeniesione z okna.</summary>
        private void DockSessionFromWindow(ServerInfo server, string password)
        {
            OpenServer(server, autoConnect: false, forceNew: true);
            if (_owner._active != null && _owner._active.Server == server)
            {
                _owner._active.Password = password;
                ConnectSession(_owner._active);
            }
            if (_owner.WindowState == WindowState.Minimized) _owner.WindowState = WindowState.Normal;
            _owner.Activate();   // Window.Activate — wysuń manager na wierzch
        }

        private static string DescribeDisconnect(AxMsRdpClient11NotSafeForScripting rdp, int reason)
        {
            uint ext = 0;
            try { ext = (uint)rdp.ExtendedDisconnectReason; } catch { }
            string d = null;
            try { d = rdp.GetErrorDescription((uint)reason, ext); } catch { }
            return RdpUtils.FormatDisconnectReason(d, reason, ext);
        }

        private void SetSessionStatus(Session s, string text, StatusKind kind = StatusKind.Info)
        {
            s.Status = text;
            s.StatusKind = kind;
            if (s == _owner._active) SetStatus(text, kind);
        }

        // internal: wołany też przez Services/ReachabilityService (status WOL/diagnozy).
        internal void SetStatus(string text, StatusKind kind = StatusKind.Info)
        {
            _owner.StatusText.Text = text;
            _owner.StatusText.ToolTip = (string.IsNullOrEmpty(text) || text == "—") ? null : text;
            var b = KindBrush(kind);
            _owner.StatusText.Foreground = b;
            _owner.CfStatusDot.Fill = b;
            _owner.CfStatusDot.Visibility = kind == StatusKind.Info ? Visibility.Collapsed : Visibility.Visible;
        }

        private Brush KindBrush(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Connecting: return _owner.Res("Idle");
                case StatusKind.Ok: return _owner.Res("Online");
                case StatusKind.Error: return _owner.Res("Danger");
                default: return _owner.Res("TextSec");
            }
        }
    }
}
