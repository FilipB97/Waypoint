using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager.ViewModels
{
    /// <summary>
    /// Domenowy ViewModel: właściciel listy serwerów i „ostatnich”, plus filtrowanie i liczniki.
    /// Czysta, testowalna logika (bez WPF). Warstwa widoku renderuje z tej kolekcji.
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        /// <summary>Wszystkie serwery (źródło prawdy dla drzewa, flyoutu i pulpitu).</summary>
        public ObservableCollection<ServerInfo> Servers { get; } = new ObservableCollection<ServerInfo>();

        /// <summary>Id serwerów w kolejności ostatnich połączeń (współdzielone z AppSettings).</summary>
        public List<string> RecentIds { get; private set; } = new List<string>();

        public int Total => Servers.Count;
        public int OnlineCount => Servers.Count(s => s.Status == ServerStatus.Online);

        /// <summary>Podłącza listę „ostatnich” z ustawień (ta sama referencja — zmiany zapisują się z ustawieniami).</summary>
        public void UseRecentIds(List<string> ids) => RecentIds = ids ?? new List<string>();

        public void LoadServers(IEnumerable<ServerInfo> servers)
        {
            Servers.Clear();
            if (servers != null)
                foreach (var s in servers) Servers.Add(s);
            RaiseCounts();
        }

        public ServerInfo FindById(string id) => Servers.FirstOrDefault(s => s.Id == id);

        public void Add(ServerInfo server)
        {
            Servers.Add(server);
            RaiseCounts();
        }

        public void Remove(ServerInfo server)
        {
            Servers.Remove(server);
            RaiseCounts();
        }

        public IEnumerable<ServerInfo> Filter(string text) => Servers.Where(s => RdpUtils.MatchesFilter(s, text));

        /// <summary>Przenosi serwer na początek listy „ostatnich”, przycinając do <paramref name="max"/>.</summary>
        public void RecordRecent(string id, int max = 15)
        {
            if (string.IsNullOrEmpty(id)) return;
            RecentIds.Remove(id);
            RecentIds.Insert(0, id);
            if (RecentIds.Count > max)
                RecentIds.RemoveRange(max, RecentIds.Count - max);
        }

        /// <summary>Serwery odpowiadające „ostatnim” id, w kolejności, pomijając usunięte.</summary>
        public IEnumerable<ServerInfo> RecentServers()
        {
            foreach (var id in RecentIds)
            {
                var s = FindById(id);
                if (s != null) yield return s;
            }
        }

        /// <summary>Wywołać po zmianie statusów serwerów (odświeża liczniki pulpitu).</summary>
        public void RaiseCounts()
        {
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(OnlineCount));
        }
    }
}
