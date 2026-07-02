# Waypoint

**Waypoint** is a modern, tabbed Remote Desktop client for Windows, built with WPF (.NET 8) and the
Windows 11 **Fluent / Mica** design. It wraps the official Microsoft RDP ActiveX control
(`mstscax`) and aims to be a comfortable daily replacement for the built-in
Remote Desktop Connection (`mstsc.exe`).

> Status: early but usable. See the [roadmap](docs/ROADMAP.md) for what's next.

## Download

Grab the latest **`Waypoint-<version>-win-x64.exe`** from the
[Releases](https://github.com/FilipB97/Waypoint/releases) page and run it —
it's a self-contained single file, no .NET install required (Windows 10/11 x64).

> Not code-signed yet, so SmartScreen may warn on first run: *More info → Run anyway*.

## Features

- **Tabbed sessions** — multiple simultaneous RDP connections, switch without disconnecting.
- **Server list with groups**, search/filter, and reachability dots (background TCP probe on the RDP port).
- **Full-screen mode** with an auto-hiding toolbar and an "other connections" flyout to
  switch sessions without leaving full screen.
- **Dynamic resolution** — the session resolution follows the window/full-screen size for a
  crisp 1:1 image (falls back to smart-sizing on older hosts).
- **Credential handling** via the Windows Credential Manager (DPAPI) — passwords are never
  written to app files.
- **Per-server options** — port, domain/Windows account, clipboard/drive/printer/audio
  redirection, server-identity verification level, and RD Gateway / jump-host.
- **Credential prompt** at connect time and "connect as" for retrying with other credentials.
- **Import / export `.rdp`** files for easy migration from `mstsc`.
- **Keyboard shortcuts** — `Ctrl+Tab` cycle tabs, `Alt+1..9` jump, `Ctrl+W` close,
  `Ctrl+F`/`Ctrl+K` focus search, `Ctrl+±/0` zoom, `F11` full screen.
- **Diagnostics & audit** — per-server TCP port test and an optional connection log.
- **Multiple sessions per server**, duplicate tab, tab reorder, and **drag-and-drop server reordering** in the tree.
- **Profile export / import** — back up all servers and settings to one file.
- **Multi-monitor, mstsc-style** — open any session in a **standalone window**, drag it to
  another monitor and go full screen there; run several sessions on several screens at once.
  **Tear off** a tab into its own window and **dock** it back to a tab (seamless reconnect).
- **Dashboard & recents**, UI zoom (Ctrl+scroll), dark Fluent theme.

> **Known limitation:** keyboard shortcuts (Ctrl+Tab, Alt+1..9, F11, …) work while focus
> is on the app chrome. Inside a connected RDP session the keyboard goes to the remote
> desktop — same as `mstsc`.

## Requirements

- Windows 10 / 11 (x64) — the app relies on the Windows RDP ActiveX control.
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build (the runtime to run a published build).

## Build & run

```powershell
# from the repo root
dotnet restore RdpManager.sln
dotnet build   RdpManager.sln -c Release
dotnet run     --project src/RdpManager/RdpManager.csproj -c Release
```

Or open `RdpManager.sln` in Visual Studio 2022 (17.8+) and press F5.

To produce the self-contained single-file build shipped on Releases:

```powershell
dotnet publish src/RdpManager/RdpManager.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
# -> src/RdpManager/bin/Release/net8.0-windows/win-x64/publish/Waypoint.exe
```

## Tests

```powershell
dotnet test RdpManager.sln -c Release
```

Unit tests cover the pure, UI-independent logic (helpers, settings/server-list serialization).
UI and RDP-control behavior require a real Windows desktop session and are verified manually.

## Security model

- **Passwords** are stored in the **Windows Credential Manager** (DPAPI, tied to the current
  Windows user) under the target `RdpManager:<server-id>` — the same secure store `mstsc` uses.
  They are **never** serialized to disk by the app.
- **Server list** (`%APPDATA%\RdpManager\servers.json`) and **settings**
  (`%APPDATA%\RdpManager\settings.json`) contain only non-secret metadata (host, port,
  username, redirection flags). The password field is `[JsonIgnore]`.
- In-memory session passwords are cleared on disconnect when they are not saved.
- **Server identity verification** is configurable per server and defaults to *warn on failure*
  (RDP `AuthenticationLevel = 2`) to mitigate man-in-the-middle attacks. You can raise it to
  *require* or lower it to *don't check* per connection.

Found a vulnerability? See [SECURITY.md](SECURITY.md).

## RDP interop assemblies

`src/RdpManager/libs/AxMSTSCLib.dll` and `MSTSCLib.dll` are interop wrappers generated with
`AxImp.exe` from the system component `C:\Windows\System32\mstscax.dll`. They only expose type
definitions for the Microsoft RDP ActiveX control that ships with Windows; the control itself
is provided by your Windows installation.

## Contributing

Issues and pull requests are welcome. Please build and run the tests before opening a PR.

## License

[MIT](LICENSE) © 2026 Filip Benklewski
