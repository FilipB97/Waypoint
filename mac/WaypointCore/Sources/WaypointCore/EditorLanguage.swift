import Foundation

/// Nazwa pliku (i pierwsza linia) → identyfikator języka Monaco. Port `EditorLanguage.cs` — ta sama
/// tabela, żeby ten sam plik był tak samo kolorowany na Macu i w Windows.
public enum EditorLanguage {
    public static let plain = "plaintext"

    private static let byExt: [String: String] = [
        ".yml": "yaml", ".yaml": "yaml",
        ".json": "json", ".jsonc": "json", ".json5": "json",
        ".ini": "ini", ".cfg": "ini", ".conf": "ini", ".cnf": "ini", ".env": "ini",
        ".properties": "ini", ".toml": "ini", ".service": "ini", ".timer": "ini",
        ".socket": "ini", ".mount": "ini", ".target": "ini", ".network": "ini", ".desktop": "ini",
        ".sh": "shell", ".bash": "shell", ".zsh": "shell", ".ksh": "shell",
        ".xml": "xml", ".xsd": "xml", ".xsl": "xml", ".svg": "xml", ".csproj": "xml",
        ".config": "xml", ".plist": "xml",
        ".html": "html", ".htm": "html",
        ".css": "css", ".scss": "scss", ".less": "less",
        ".js": "javascript", ".mjs": "javascript", ".cjs": "javascript",
        ".ts": "typescript", ".tsx": "typescript", ".jsx": "javascript",
        ".py": "python", ".sql": "sql", ".md": "markdown", ".markdown": "markdown",
        ".ps1": "powershell", ".psm1": "powershell", ".psd1": "powershell",
        ".php": "php", ".rb": "ruby", ".go": "go", ".rs": "rust", ".java": "java",
        ".cs": "csharp", ".lua": "lua", ".pl": "perl", ".pm": "perl",
        ".tf": "hcl", ".hcl": "hcl", ".bat": "bat", ".cmd": "bat",
        ".c": "cpp", ".h": "cpp", ".cpp": "cpp", ".hpp": "cpp", ".cc": "cpp",
        ".kt": "kotlin", ".swift": "swift", ".r": "r", ".dart": "dart", ".proto": "protobuf",
        ".graphql": "graphql", ".tcl": "tcl", ".vb": "vb",
    ]

    private static let byName: [String: String] = [
        "dockerfile": "dockerfile", "containerfile": "dockerfile",
        ".bashrc": "shell", ".bash_profile": "shell", ".bash_aliases": "shell", ".bash_logout": "shell",
        ".profile": "shell", ".zshrc": "shell", ".zprofile": "shell", "crontab": "shell",
        "makefile": plain,
        ".gitconfig": "ini", ".editorconfig": "ini", "my.cnf": "ini", "php.ini": "ini",
        "sshd_config": plain, "ssh_config": plain,
    ]

    private static let byInterpreter: [String: String] = [
        "sh": "shell", "bash": "shell", "zsh": "shell", "dash": "shell", "ksh": "shell",
        "python": "python", "python3": "python", "python2": "python",
        "node": "javascript", "perl": "perl", "ruby": "ruby", "php": "php",
        "pwsh": "powershell", "lua": "lua",
    ]

    public static func language(for fileName: String, firstLine: String? = nil) -> String {
        var name = fileName
        if let i = name.lastIndex(where: { $0 == "/" || $0 == "\\" }) { name = String(name[name.index(after: i)...]) }
        let lower = name.lowercased()
        if let id = byName[lower] { return id }
        if lower.hasPrefix("dockerfile.") { return "dockerfile" }
        // Kropka na początku („.env") to nazwa ukryta, ale „.env" jest też rozszerzeniem.
        if let dot = lower.lastIndex(of: "."), let id = byExt[String(lower[dot...])] { return id }
        return fromShebang(firstLine) ?? plain
    }

    /// „#!/usr/bin/env python3", „#!/bin/bash -e" → interpreter; nil, gdy to nie shebang.
    public static func fromShebang(_ line: String?) -> String? {
        guard let line, line.hasPrefix("#!") else { return nil }
        let parts = line.dropFirst(2).split(whereSeparator: { $0 == " " || $0 == "\t" }).map(String.init)
        guard var prog = parts.first else { return nil }
        if let s = prog.lastIndex(of: "/") { prog = String(prog[prog.index(after: s)...]) }
        if prog == "env" {
            guard let p = parts.dropFirst().first(where: { !$0.hasPrefix("-") }) else { return nil }
            prog = p
        }
        if let id = byInterpreter[prog] { return id }
        if let d = prog.firstIndex(of: "."), let id = byInterpreter[String(prog[..<d])] { return id }   // python3.12
        return nil
    }
}
