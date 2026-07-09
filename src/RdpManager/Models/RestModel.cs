using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Models
{
    /// <summary>Para klucz–wartość z przełącznikiem (parametry zapytania, nagłówki). Wyłączone są pomijane przy wysyłce.</summary>
    public sealed class RestKeyValue
    {
        public bool Enabled { get; set; } = true;
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// Pojedyncze żądanie HTTP zapisane w kolekcji. Sekret uwierzytelniania (token / hasło Basic)
    /// NIE jest tu trzymany — idzie do Windows Credential Manager pod <see cref="AuthCredTarget"/>.
    /// </summary>
    public sealed class RestRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = "";
        public List<RestKeyValue> QueryParams { get; set; } = new List<RestKeyValue>();
        public List<RestKeyValue> Headers { get; set; } = new List<RestKeyValue>();
        public string Body { get; set; } = "";
        public string BodyContentType { get; set; } = "application/json";
        /// <summary>Pola treści application/x-www-form-urlencoded jako tabela klucz/wartość (edytor przyjazny jak w Postmanie).
        /// Gdy niepuste, mają pierwszeństwo przy wysyłce nad <see cref="Body"/> (zob. RestClient.Build).</summary>
        public List<RestKeyValue> FormFields { get; set; } = new List<RestKeyValue>();

        /// <summary>Rodzaj uwierzytelniania: 0 = brak, 1 = Bearer (token), 2 = Basic (login + hasło),
        /// 3 = dziedzicz z folderu-rodzica / kolekcji (zob. RestAuthResolve). Domyślne dla nowych żądań —
        /// jak w Postmanie ("Inherit auth from parent").</summary>
        public int AuthType { get; set; } = 3;
        /// <summary>Login dla Basic (hasło idzie do Credential Manager, nie do JSON).</summary>
        public string AuthUsername { get; set; } = "";

        /// <summary>Folder-rodzic w drzewie kolekcji (puste = korzeń). Używane od PR2 (foldery).</summary>
        public string FolderId { get; set; } = "";

        /// <summary>Skrypt JS uruchamiany PRZED wysyłką (pm.request/pm.environment). To kod, nie sekret — może być w JSON.</summary>
        public string PreScript { get; set; } = "";
        /// <summary>Skrypt JS uruchamiany PO odpowiedzi (pm.response/pm.test/pm.environment.set). Kod, nie sekret.</summary>
        public string TestScript { get; set; } = "";

        /// <summary>Klucz sekretu uwierzytelniania w Credential Manager (token/hasło Basic — nigdy w JSON).</summary>
        [JsonIgnore]
        public string AuthCredTarget => "RdpManager:rest:" + Id;

        /// <summary>Sekret auth (token/hasło Basic) w PAMIĘCI na czas sesji — przeżywa przełączanie żądań w drzewie.
        /// Wczytywany z Credential Manager przy otwarciu, zapisywany do niego przy „Zapisz". Nigdy do JSON.</summary>
        [JsonIgnore]
        public string AuthSecret { get; set; } = "";

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }

    /// <summary>Folder w drzewie kolekcji (PR2). Zagnieżdżanie przez <see cref="ParentId"/> („" = korzeń).</summary>
    public sealed class RestFolder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string ParentId { get; set; } = "";

        /// <summary>Uwierzytelnianie na poziomie folderu: 0=brak,1=Bearer,2=Basic,3=dziedzicz z nadfolderu/kolekcji
        /// (domyślne — jak w Postmanie). Używane, gdy żądanie w tym folderze samo ma AuthType=3.</summary>
        public int AuthType { get; set; } = 3;
        public string AuthUsername { get; set; } = "";

        /// <summary>Klucz sekretu auth folderu w Credential Manager (nigdy w JSON).</summary>
        [JsonIgnore]
        public string AuthCredTarget => "RdpManager:restfolder:" + Id;

        /// <summary>Sekret auth folderu w PAMIĘCI na czas sesji. Nigdy do JSON.</summary>
        [JsonIgnore]
        public string AuthSecret { get; set; } = "";
    }

    /// <summary>Zmienna środowiskowa (PR3) — podstawiana w URL/nagłówkach/body jako {{klucz}}.</summary>
    public sealed class RestVariable
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>Środowisko: nazwany zestaw zmiennych (np. „dev", „prod"). Od PR środowisk globalnych
    /// przechowywane w environments.json (EnvironmentStore), wspólne dla wszystkich kolekcji; kolekcja
    /// wskazuje wybrane środowisko przez <see cref="RestCollection.ActiveEnvironmentId"/>.</summary>
    public sealed class RestEnvironment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<RestVariable> Variables { get; set; } = new List<RestVariable>();

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }

    /// <summary>Wpis historii wysłanego żądania.</summary>
    public sealed class RestHistoryEntry
    {
        public string Method { get; set; } = "";
        public string Url { get; set; } = "";
        public int Status { get; set; }
        public long ElapsedMs { get; set; }
        public string WhenIso { get; set; } = "";

        /// <summary>Druga linia na liście historii: status · czas · kiedy.</summary>
        [JsonIgnore]
        public string Summary => (Status > 0 ? Status.ToString() : "—") + " · " + ElapsedMs + " ms · " + WhenIso;
    }

    /// <summary>
    /// Kolekcja REST dla jednego wpisu na liście („wpis = jedno API"). Trzyma drzewo żądań/folderów,
    /// środowiska i historię. Zapisywana w rest.json pod Id wpisu (<see cref="ServerInfo"/>).
    /// </summary>
    public sealed class RestCollection
    {
        /// <summary>Wersja kształtu pól. CELOWO bez domyślnej wartości = CurrentSchemaVersion — inaczej stary
        /// plik (bez tego pola) i świeży obiekt byłyby nie do odróżnienia (System.Text.Json nie zeruje pól
        /// nieobecnych w JSON, tylko zostawia wartość z inicjalizatora). 0 = nieoznaczone/sprzed wprowadzenia
        /// znacznika. RestStore.Save wpisuje bieżącą wersję. RestRequest/RestFolder.AuthType dostały wartość
        /// 3=Inherit bez żadnego znacznika wersji; stare pliki nie są przez to zepsute (0/1/2 znaczą to samo
        /// co wcześniej), ale nie ma jak odróżnić kolekcji sprzed tej zmiany. Sam znacznik na przyszłość,
        /// na razie bez kroku Migrate() (B5 z przeglądu).</summary>
        public int SchemaVersion { get; set; }

        /// <summary>Publiczne dla testów (C5) i ewentualnej przyszłej migracji.</summary>
        public const int CurrentSchemaVersion = 2;

        public string BaseUrl { get; set; } = "";
        public List<RestFolder> Folders { get; set; } = new List<RestFolder>();
        public List<RestRequest> Requests { get; set; } = new List<RestRequest>();
        public List<RestEnvironment> Environments { get; set; } = new List<RestEnvironment>();
        public string ActiveEnvironmentId { get; set; } = "";
        public List<RestHistoryEntry> History { get; set; } = new List<RestHistoryEntry>();

        /// <summary>Uwierzytelnianie domyślne całej kolekcji (korzeń dziedziczenia): 0=brak,1=Bearer,2=Basic.
        /// Bez opcji "dziedzicz" — kolekcja nie ma rodzica.</summary>
        public int AuthType { get; set; }
        public string AuthUsername { get; set; } = "";

        /// <summary>Sekret auth kolekcji w PAMIĘCI na czas sesji. Cel w Credential Manager liczony po Id wpisu
        /// (serwera) — kolekcja go nie zna, patrz RestConsole. Nigdy do JSON.</summary>
        [JsonIgnore]
        public string AuthSecret { get; set; } = "";

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }
}
