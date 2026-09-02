using System.Collections.Generic;
using System.Linq;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Co ma pokazać licznik przy nagłówku grupy. Sama decyzja, bez WPF — dzięki temu da się ją
    /// sprawdzić testem, a nagłówek tylko rysuje wynik.
    ///
    /// Zasada: znacznik należy się stanowi WYMAGAJĄCEMU UWAGI, nie normie. Grupa rozwinięta pokazuje
    /// samą liczbę (stany widać w wierszach), zwinięta z kompletem działających serwerów też — ale
    /// zwinięta, w której coś nie odpowiada, musi to powiedzieć. Inaczej jedynym sposobem, żeby się
    /// o tym dowiedzieć, byłoby rozwinięcie każdej grupy po kolei.
    /// </summary>
    public readonly struct GroupCounter
    {
        public int Total { get; }
        public int Offline { get; }

        /// <summary>Czy pokazać „N/M" zamiast samego „M".</summary>
        public bool ShowsOffline { get; }

        private GroupCounter(int total, int offline, bool showsOffline)
        {
            Total = total; Offline = offline; ShowsOffline = showsOffline;
        }

        public static GroupCounter For(IEnumerable<ServerInfo> servers, bool collapsed)
        {
            var list = servers as IReadOnlyCollection<ServerInfo> ?? servers?.ToList();
            int total = list?.Count ?? 0;
            int offline = list?.Count(s => s.Status == ServerStatus.Offline) ?? 0;
            return new GroupCounter(total, offline, collapsed && offline > 0);
        }
    }
}
