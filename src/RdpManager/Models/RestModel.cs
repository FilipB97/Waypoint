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

        /// <summary>Rodzaj uwierzytelniania: 0 = brak, 1 = Bearer (token), 2 = Basic (login + hasło).</summary>
        public int AuthType { get; set; }
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
    }

    /// <summary>Zmienna środowiskowa (PR3) — podstawiana w URL/nagłówkach/body jako {{klucz}}.</summary>
    public sealed class RestVariable
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>Środowisko (PR3): nazwany zestaw zmiennych (np. „dev", „prod").</summary>
    public sealed class RestEnvironment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<RestVariable> Variables { get; set; } = new List<RestVariable>();
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
        public string BaseUrl { get; set; } = "";
        public List<RestFolder> Folders { get; set; } = new List<RestFolder>();
        public List<RestRequest> Requests { get; set; } = new List<RestRequest>();
        public List<RestEnvironment> Environments { get; set; } = new List<RestEnvironment>();
        public string ActiveEnvironmentId { get; set; } = "";
        public List<RestHistoryEntry> History { get; set; } = new List<RestHistoryEntry>();

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }
}
