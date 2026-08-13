using Xunit;

namespace RdpManager.Tests
{
    /// <summary>
    /// Serializuje klasy testowe, które OPRÓŻNIAJĄ <c>HealthNotices</c>.
    ///
    /// <para><c>HealthNotices</c> to statyczny, globalny zbiornik, a <c>Drain()</c> jest NISZCZĄCY (zwraca
    /// zawartość i czyści). xUnit domyślnie zrównolegla klasy testowe — każda klasa to osobna kolekcja — więc
    /// dwie klasy sprawdzające kwarantannę uszkodzonego pliku potrafiły sobie nawzajem ukraść wpis:</para>
    /// <code>
    /// Ftps:       Drain()                 -> czyści zbiornik
    /// Ftps:       Load(uszkodzony)        -> Add(FileQuarantined, "ftps_certs.json")
    /// KnownHosts: Drain()                 -> ZABIERA cudzy wpis
    /// Ftps:       Drain()                 -> []   => Assert.Contains pada na pustej kolekcji
    /// </code>
    /// <para>Objaw był losowy i zależny od przeplotu: „Assert.Contains() Failure: Filter not matched in
    /// collection / Collection: []" — raz w CI, raz w Release, przy 411 pozostałych testach zielonych.</para>
    ///
    /// <para>Wspólna kolekcja serializuje TYLKO te klasy; reszta suity nadal biegnie równolegle. Klasy, które
    /// jedynie DODAJĄ wpisy (StoreTests, RestTests, ProfileBackupTests — też mają ścieżki kwarantanny), nie
    /// muszą tu być: nadmiarowe wpisy w zbiorniku nie psują <c>Assert.Contains</c>, psuje wyłącznie kradzież
    /// przez równoległy <c>Drain()</c>.</para>
    ///
    /// <para>Nazwa kolekcji („HealthNotices") musi być identyczna tutaj i w atrybutach
    /// <c>[Collection("HealthNotices")]</c> na klasach testowych — xUnit wiąże je po samym łańcuchu znaków,
    /// a literówka nie jest błędem kompilacji, tylko cichym powrotem do równoległości.</para>
    /// </summary>
    [CollectionDefinition("HealthNotices")]
    public class HealthNoticesCollection { }
}
