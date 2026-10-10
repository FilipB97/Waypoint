using System;

namespace RdpManager.Core
{
    public enum SudoOutcome { AtomicReplace, InPlace }

    /// <summary>
    /// Zapis przez sudo — część niezależna od SSH (ten sam skrypt co w wersji na macOS, mac/WaypointCore
    /// SudoWrite.swift). Wykonuje się w CAŁOŚCI po stronie serwera, więc zerwane połączenie nie przerwie
    /// zapisu w połowie: plik tymczasowy obok prawdziwego, właściciel i uprawnienia skopiowane
    /// (chown/chmod --reference, GNU), atomowe mv; bez GNU coreutils — zapis w miejscu (cat &gt; plik),
    /// który zachowuje właściciela i uprawnienia.
    /// </summary>
    public static class SudoScript
    {
        /// <summary>Argumenty: $1 = plik z treścią, $2 = plik docelowy. Wypisuje ATOMIC albo INPLACE.</summary>
        public const string Script =
            "set -u\n" +
            "src=$1; dst=$2\n" +
            "[ -f \"$dst\" ] || { echo \"brak pliku: $dst\" >&2; exit 2; }\n" +
            "dir=$(dirname -- \"$dst\"); base=$(basename -- \"$dst\")\n" +
            "if tmp=$(mktemp \"$dir/.$base.waypoint-XXXXXXXX\" 2>/dev/null); then\n" +
            "  if chown --reference=\"$dst\" -- \"$tmp\" 2>/dev/null && chmod --reference=\"$dst\" -- \"$tmp\" 2>/dev/null \\\n" +
            "     && cat -- \"$src\" > \"$tmp\" && mv -f -- \"$tmp\" \"$dst\"; then\n" +
            "    echo ATOMIC; exit 0\n" +
            "  fi\n" +
            "  rm -f -- \"$tmp\"\n" +
            "fi\n" +
            "if cat -- \"$src\" > \"$dst\"; then echo INPLACE; exit 0; fi\n" +
            "exit 3";

        /// <summary>Polecenie zdalne. Hasło idzie na stdin (-S), nigdy w linii poleceń; pusty prompt (-p '').</summary>
        public static string RemoteCommand(string source, string destination)
            => "sudo -S -p '' -- sh -c " + ShellQuote(Script) + " waypoint-sudo " + ShellQuote(source) + " " + ShellQuote(destination);

        /// <summary>Cytowanie dla powłoki POSIX: '…' z podmianą ' na '\''.</summary>
        public static string ShellQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        /// <summary>Losowa ścieżka pliku z treścią w /tmp (tworzony z CreateNew i 0600).</summary>
        public static string StagingPath() => "/tmp/.waypoint-sudo-" + Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>Wynik polecenia: ATOMIC/INPLACE albo wyjątek mówiący, czy to odmowa sudo.</summary>
        public static SudoOutcome Interpret(int exitStatus, string stdout, string stderr)
        {
            stdout ??= ""; stderr = (stderr ?? "").Trim();
            if (exitStatus == 0 && stdout.Contains("ATOMIC")) return SudoOutcome.AtomicReplace;
            if (exitStatus == 0 && stdout.Contains("INPLACE")) return SudoOutcome.InPlace;
            string low = stderr.ToLowerInvariant();
            bool denied = low.Contains("incorrect password") || low.Contains("sorry, try again") || low.Contains("is not in the sudoers")
                || low.Contains("not allowed to execute") || low.Contains("a password is required") || low.Contains("no password was provided");
            throw new SudoException(denied, stderr.Length > 0 ? stderr : "kod " + exitStatus);
        }
    }

    public sealed class SudoException : Exception
    {
        /// <summary>sudo odrzuciło hasło albo użytkownik nie ma uprawnień sudo (plik nietknięty).</summary>
        public bool Denied { get; }
        public SudoException(bool denied, string message) : base(message) { Denied = denied; }
    }
}
