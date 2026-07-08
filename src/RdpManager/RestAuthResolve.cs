using System.Collections.Generic;
using System.Linq;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Rozwiązuje dziedziczone uwierzytelnianie REST (jak w Postmanie "Inherit auth from parent"):
    /// żądanie z AuthType=Inherit patrzy na swój folder; folder z AuthType=Inherit patrzy na nadfolder;
    /// na końcu łańcucha — na korzeń kolekcji (który nie dziedziczy, bo nie ma rodzica).
    /// Współdzielone przez RestConsole (wysyłka + podpowiedź) i RestAuthWindow (podpowiedź w edytorze).
    /// </summary>
    public static class RestAuthResolve
    {
        public const int Inherit = 3;

        /// <summary>Pierwszy jawny (nie-Inherit) poziom auth, zaczynając od folderu o Id <paramref name="startFolderId"/>
        /// ("" = korzeń, więc od razu zwraca kolekcję). <c>SourceFolder</c> = null, gdy rozwiązano do kolekcji.</summary>
        public static (int Type, string Username, string Secret, RestFolder SourceFolder) Resolve(RestCollection coll, string startFolderId)
        {
            var folder = coll.Folders.FirstOrDefault(f => f.Id == startFolderId);
            var visited = new HashSet<string>();
            while (folder != null)
            {
                if (folder.AuthType != Inherit) return (folder.AuthType, folder.AuthUsername, folder.AuthSecret, folder);
                if (!visited.Add(folder.Id)) break;   // cykliczny ParentId (A9 z przeglądu) — zatrzymaj się, nie wisimy w pętli
                folder = coll.Folders.FirstOrDefault(f => f.Id == folder.ParentId);
            }
            return (coll.AuthType, coll.AuthUsername, coll.AuthSecret, null);
        }
    }
}
