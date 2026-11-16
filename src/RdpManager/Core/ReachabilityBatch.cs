namespace RdpManager.Core
{
    /// <summary>
    /// Czy wyniki partii sond osiągalności wolno jeszcze nanieść na listę. Nie, gdy partię anulowano
    /// (sonda wyłączona w Ustawieniach), gdy zastąpiła ją nowsza albo gdy aplikacja się zamyka — inaczej
    /// spóźnione wyniki nadpisywałyby kropki i liczniki bieżącego stanu.
    /// </summary>
    public static class ReachabilityBatch
    {
        public static bool IsCurrent(int batch, int currentBatch, bool cancelled, bool stopped)
            => !cancelled && !stopped && batch == currentBatch;
    }
}
