using System;

namespace RdpManager.Models
{
    /// <summary>
    /// Zapisana komenda wysyłana do terminala kliknięciem albo skrótem. Treść może zawierać zmienne
    /// serwera (<see cref="RdpManager.Core.SnippetVars"/>), więc jeden snippet działa na wszystkich
    /// maszynach — „ssh {user}@{host}" nie wymaga kopii per serwer.
    /// </summary>
    public sealed class CommandSnippet
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "";

        /// <summary>Treść komendy; może być wielowierszowa (każdy wiersz wysyłany jak osobno wpisany).</summary>
        public string Command { get; set; } = "";

        /// <summary>
        /// Czy dopisać Enter na końcu. Domyślnie tak, ale bywa odwrotnie: snippet z niebezpiecznym
        /// poleceniem albo szkieletem do dopisania argumentów lepiej tylko WPISAĆ i zostawić kursor.
        /// </summary>
        public bool SendEnter { get; set; } = true;

        public CommandSnippet Clone() => new CommandSnippet
        {
            Id = Id,
            Name = Name,
            Command = Command,
            SendEnter = SendEnter
        };
    }
}
