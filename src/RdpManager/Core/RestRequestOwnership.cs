namespace RdpManager.Core
{
    /// <summary>
    /// Czy zakończone żądanie REST może jeszcze zmienić UI konsoli (odpowiedź, status, historia, przycisk).
    /// Nie, gdy zastąpiło je nowsze żądanie albo zostało anulowane (zamknięcie konsoli) — spóźniona
    /// odpowiedź, wyjątek ani finally starego żądania nie mogą nadpisać stanu nowego.
    /// </summary>
    public static class RestRequestOwnership
    {
        public static bool IsCurrent(bool stillOwner, bool cancelled) => stillOwner && !cancelled;
    }
}
