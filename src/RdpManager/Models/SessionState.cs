namespace RdpManager.Models
{
    /// <summary>
    /// Żywy stan SESJI — czym innym niż <see cref="ServerStatus"/>, czyli osiągalnością serwera.
    ///
    /// Karta pokazywała dotąd status serwera: „rozłączona sesja" i „serwer nieosiągalny" dzieliły
    /// jeden znacznik, choć znaczą co innego (serwer może odpowiadać na ping, gdy sesja poległa na
    /// uwierzytelnieniu). Startowo karta dostawała nawet <c>ServerStatus.Offline</c>, co znaczyło
    /// „rozłączona", a wyglądało jak „serwer nie żyje".
    ///
    /// Nie ma tu stanu „ponowne łączenie": <c>AutoReconnect</c> jest flagą oddawaną kontrolce RDP,
    /// która ponawia we własnym zakresie i nie zgłasza aplikacji osobnego zdarzenia. Stan, którego
    /// nic nie potrafi wyprodukować, byłby martwym kodem z własnym kształtem, kolorem i tłumaczeniem.
    /// </summary>
    public enum SessionState
    {
        /// <summary>Trwa nawiązywanie połączenia.</summary>
        Connecting,
        /// <summary>Połączona i działa — stan normalny, bez znacznika.</summary>
        Connected,
        /// <summary>Była połączona, połączenie się zakończyło.</summary>
        Disconnected,
        /// <summary>Nie udało się połączyć (uwierzytelnienie, host, brak środowiska).</summary>
        Failed
    }
}
