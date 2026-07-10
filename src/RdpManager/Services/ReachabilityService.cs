using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager.Services
{
    /// <summary>
    /// Osiągalność serwerów w tle: cykliczna sonda TCP host:port → kropki statusu + opóźnienie w drzewie,
    /// próbki opóźnień do wykresu na pulpicie, Wake-on-LAN i „Diagnozuj". Wyniesione 1:1 z MainWindow
    /// (PR 2 planu docs/REFACTOR-MAINWINDOW.md, wzorzec „back-reference move-method") — bez zmian logiki.
    /// Słowniki wierszy drzewa (kropka/opóźnienie) na razie zostają w MainWindow (przejdą do
    /// ServerTreeController w PR 3) i są tu czytane przez <c>_owner.</c>; wtedy zastąpi je szew SetRowStatus.
    /// </summary>
    internal sealed class ReachabilityService
    {
        private readonly MainWindow _owner;

        private DispatcherTimer _reachTimer;
        private bool _reachBusy;
        // Średnie opóźnienie osiągalnych hostów per cykl (do wykresu na pulpicie); ostatnie 48, bez utrwalania.
        private readonly List<double> _latencySamples = new List<double>();

        // Limit jednoczesnych sond — setki serwerów naraz zalewałyby pulę wątków/gniazd (A4 z przeglądu).
        private static readonly SemaphoreSlim ProbeConcurrency = new SemaphoreSlim(32);
        // Limit czasu sondy (ms) — z Ustawień → Połączenie (ProbeTimeoutSeconds). Domyślnie 1500 do czasu
        // wczytania ustawień; aktualizowany w Start()/ApplySettings().
        private static int _probeTimeoutMs = 1500;

        private static string L(string key) => LocalizationManager.S(key);

        public ReachabilityService(MainWindow owner) => _owner = owner;

        /// <summary>Próbki opóźnień z ostatnich cykli — czytane przez wykres na pulpicie (BuildDashboard).</summary>
        internal IReadOnlyList<double> LatencySamples => _latencySamples;

        /// <summary>Start z Window_Loaded: interwał + limit czasu z ustawień; pierwsza sonda gdy włączone.</summary>
        internal void Start()
        {
            _probeTimeoutMs = Math.Clamp(_owner._settings.ProbeTimeoutSeconds, 1, 60) * 1000;
            _reachTimer = new DispatcherTimer(DispatcherPriority.Background, _owner.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(_owner._settings.ReachabilityIntervalSec, 5, 3600))
            };
            _reachTimer.Tick += (s, a) => CheckNow();
            if (_owner._settings.ReachabilityEnabled) { _reachTimer.Start(); CheckNow(); }
        }

        /// <summary>Po zapisie ustawień: limit czasu + interwał + wł/wył cyklu wg ustawienia.</summary>
        internal void ApplySettings()
        {
            _probeTimeoutMs = Math.Clamp(_owner._settings.ProbeTimeoutSeconds, 1, 60) * 1000;
            if (_reachTimer == null) return;
            _reachTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_owner._settings.ReachabilityIntervalSec, 5, 3600));
            if (_owner._settings.ReachabilityEnabled)
            {
                if (!_reachTimer.IsEnabled) _reachTimer.Start();
                CheckNow();
            }
            else
            {
                _reachTimer.Stop();
            }
        }

        internal async void CheckNow()
        {
            if (_reachBusy) return;
            _reachBusy = true;
            try
            {
                var servers = _owner._vm.Servers.ToList();
                var results = await Task.WhenAll(servers.Select(async srv =>
                {
                    // Serial (COM), WWW i REST (URL) — sonda TCP host:port nie ma sensu, zostaw bieżący status/opóźnienie.
                    var r = srv.Protocol == RemoteProtocol.Serial || srv.Protocol == RemoteProtocol.Http || srv.Protocol == RemoteProtocol.Rest
                        ? (srv.Status, srv.LatencyMs) : await ProbeAsync(srv.Host, srv.Port);
                    return new KeyValuePair<ServerInfo, (ServerStatus status, int rttMs)>(srv, r);
                }));

                foreach (var kv in results)
                {
                    kv.Key.Status = kv.Value.status;
                    kv.Key.LatencyMs = kv.Value.rttMs;
                    _owner._tree.SetRowStatus(kv.Key, kv.Value.status, kv.Value.rttMs);   // szew (PR 3): kropka + opóźnienie wiersza
                }
                // Zapamiętaj średnie opóźnienie osiągalnych hostów z tego cyklu — do wykresu na pulpicie.
                var reachable = results.Where(kv => kv.Value.rttMs >= 0).Select(kv => (double)kv.Value.rttMs).ToList();
                if (reachable.Count > 0)
                {
                    _latencySamples.Add(reachable.Average());
                    if (_latencySamples.Count > 48) _latencySamples.RemoveAt(0);
                }
                _owner._vm.RaiseCounts();   // odśwież liczniki pulpitu (Osiągalne)
            }
            catch
            {
                // problemy sieciowe nie mogą wywrócić UI
            }
            finally
            {
                _reachBusy = false;
            }
        }

        // Wake-on-LAN: magic packet broadcastem na podstawie MAC z ustawień serwera.
        internal void WakeServer(ServerInfo server)
        {
            if (!Core.WakeOnLan.TryParseMac(server.MacAddress, out var mac))
            {
                MessageBox.Show(string.Format(L("S.se.mac.bad"), server.MacAddress),
                    L("S.m.wol"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Core.WakeOnLan.Send(mac);
                _owner.SetStatus(string.Format(L("S.st.wolsent"), server.Name), StatusKind.Ok);
            }
            catch (Exception ex)
            {
                _owner.SetStatus(string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
            }
        }

        internal async void DiagnoseServer(ServerInfo server)
        {
            string host = server.Host;
            int port = server.Port;
            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show(L("S.msg.diag.nohost"), L("S.msg.diag.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _owner.SetStatus(string.Format(L("S.st.diagnosing"), host, port), StatusKind.Connecting);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var probe = await ProbeAsync(host, port);
            sw.Stop();
            bool ok = probe.status == ServerStatus.Online;
            long elapsed = ok && probe.rttMs >= 0 ? probe.rttMs : sw.ElapsedMilliseconds;

            string msg = RdpUtils.FormatDiagnostics(host, port, ok, elapsed,
                L("S.diag.open"), L("S.diag.closed"));
            _owner.SetStatus(msg, ok ? StatusKind.Ok : StatusKind.Error);
            MessageBox.Show(msg, string.Format(L("S.msg.diag.titlefmt"), server.Name ?? host),
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // Zwraca status oraz zmierzone opóźnienie połączenia TCP (ms); rttMs = -1 gdy nieosiągalny/nieznany.
        private static async Task<(ServerStatus status, int rttMs)> ProbeAsync(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return (ServerStatus.Offline, -1);
            await ProbeConcurrency.WaitAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var c = new TcpClient())
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(_probeTimeoutMs)))
                {
                    // Token anuluje SAMO ConnectAsync przy timeout — nie zostaje porzucone, nieobserwowane
                    // zadanie łączenia (wcześniej WaitAsync porzucał je, a jego późniejszy wyjątek był nieobserwowany).
                    await c.ConnectAsync(host, port, cts.Token);
                    sw.Stop();
                    return c.Connected ? (ServerStatus.Online, (int)sw.ElapsedMilliseconds) : (ServerStatus.Offline, -1);
                }
            }
            catch
            {
                return (ServerStatus.Offline, -1);   // timeout (TimeoutException) albo błąd połączenia
            }
            finally { ProbeConcurrency.Release(); }
        }
    }
}
