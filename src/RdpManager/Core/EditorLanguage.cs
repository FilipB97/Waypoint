using System;
using System.Collections.Generic;

namespace RdpManager.Core
{
    /// <summary>
    /// Nazwa pliku (i pierwsza linia) → identyfikator języka Monaco. Pliki na serwerach często nie mają
    /// rozszerzenia albo mają „.conf" na wszystko, więc poza rozszerzeniem liczą się znane nazwy
    /// (Dockerfile, crontab, .bashrc) i shebang. Nieznane → zwykły tekst (bez podświetlania, ale edytowalny).
    /// Każdy zwracany identyfikator musi istnieć w osadzonej paczce (basic-languages albo language/json).
    /// </summary>
    public static class EditorLanguage
    {
        public const string Plain = "plaintext";

        private static readonly Dictionary<string, string> ByExt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".yml"] = "yaml", [".yaml"] = "yaml",
            [".json"] = "json", [".jsonc"] = "json", [".json5"] = "json",
            // Pliki klucz=wartość i sekcje [x]: składnia INI koloruje je rozsądnie (komentarze, klucze).
            [".ini"] = "ini", [".cfg"] = "ini", [".conf"] = "ini", [".cnf"] = "ini", [".env"] = "ini",
            [".properties"] = "ini", [".toml"] = "ini", [".service"] = "ini", [".timer"] = "ini",
            [".socket"] = "ini", [".mount"] = "ini", [".target"] = "ini", [".network"] = "ini", [".desktop"] = "ini",
            [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell", [".ksh"] = "shell",
            [".xml"] = "xml", [".xsd"] = "xml", [".xsl"] = "xml", [".svg"] = "xml", [".csproj"] = "xml",
            [".config"] = "xml", [".plist"] = "xml",
            [".html"] = "html", [".htm"] = "html",
            [".css"] = "css", [".scss"] = "scss", [".less"] = "less",
            [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
            [".ts"] = "typescript", [".tsx"] = "typescript", [".jsx"] = "javascript",
            [".py"] = "python", [".sql"] = "sql", [".md"] = "markdown", [".markdown"] = "markdown",
            [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell",
            [".php"] = "php", [".rb"] = "ruby", [".go"] = "go", [".rs"] = "rust", [".java"] = "java",
            [".cs"] = "csharp", [".lua"] = "lua", [".pl"] = "perl", [".pm"] = "perl",
            [".tf"] = "hcl", [".hcl"] = "hcl", [".bat"] = "bat", [".cmd"] = "bat",
            [".c"] = "cpp", [".h"] = "cpp", [".cpp"] = "cpp", [".hpp"] = "cpp", [".cc"] = "cpp",
            [".kt"] = "kotlin", [".swift"] = "swift", [".r"] = "r", [".dart"] = "dart", [".proto"] = "protobuf",
            [".graphql"] = "graphql", [".tcl"] = "tcl", [".vb"] = "vb"
        };

        private static readonly Dictionary<string, string> ByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dockerfile"] = "dockerfile", ["Containerfile"] = "dockerfile",
            [".bashrc"] = "shell", [".bash_profile"] = "shell", [".bash_aliases"] = "shell", [".bash_logout"] = "shell",
            [".profile"] = "shell", [".zshrc"] = "shell", [".zprofile"] = "shell", ["crontab"] = "shell",
            ["Makefile"] = Plain,
            [".gitconfig"] = "ini", [".editorconfig"] = "ini", ["my.cnf"] = "ini", ["php.ini"] = "ini",
            ["sshd_config"] = Plain, ["ssh_config"] = Plain
        };

        private static readonly Dictionary<string, string> ByInterpreter = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sh"] = "shell", ["bash"] = "shell", ["zsh"] = "shell", ["dash"] = "shell", ["ksh"] = "shell",
            ["python"] = "python", ["python3"] = "python", ["python2"] = "python",
            ["node"] = "javascript", ["perl"] = "perl", ["ruby"] = "ruby", ["php"] = "php",
            ["pwsh"] = "powershell", ["lua"] = "lua"
        };

        public static string For(string fileName, string firstLine = null)
        {
            string name = fileName ?? "";
            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name.Substring(slash + 1);

            if (ByName.TryGetValue(name, out var id)) return id;
            if (name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)) return "dockerfile";

            int dot = name.LastIndexOf('.');
            // Kropka na początku (".env", ".vimrc") to nazwa ukryta, ale ".env" jest też rozszerzeniem.
            if (dot >= 0 && ByExt.TryGetValue(name.Substring(dot), out id)) return id;

            string fromShebang = FromShebang(firstLine);
            return fromShebang ?? Plain;
        }

        /// <summary>„#!/usr/bin/env python3", „#!/bin/bash -e" → interpreter; null, gdy to nie shebang.</summary>
        public static string FromShebang(string firstLine)
        {
            if (string.IsNullOrEmpty(firstLine) || !firstLine.StartsWith("#!", StringComparison.Ordinal)) return null;
            var parts = firstLine.Substring(2).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            string prog = parts[0];
            int s = prog.LastIndexOf('/');
            if (s >= 0) prog = prog.Substring(s + 1);
            if (prog == "env")
            {
                prog = null;
                for (int i = 1; i < parts.Length; i++)
                    if (!parts[i].StartsWith("-", StringComparison.Ordinal)) { prog = parts[i]; break; }
                if (prog == null) return null;
            }
            // python3.12 → python3
            if (ByInterpreter.TryGetValue(prog, out var id)) return id;
            int d = prog.IndexOf('.');
            if (d > 0 && ByInterpreter.TryGetValue(prog.Substring(0, d), out id)) return id;
            return null;
        }
    }
}
