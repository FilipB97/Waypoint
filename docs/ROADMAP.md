# Roadmap

Waypoint is a personal, zero-cost project: one user, no paid services. That rules out paid code
signing (OV/EV certificates) and anything that needs a hosted backend. Releases are signed with a
**self-signed** certificate, and auto-update pins that publisher, so updates stay protected even
though SmartScreen still warns on a fresh download.

Builds are published on [Releases](https://github.com/FilipB97/Waypoint/releases).
What the app already does is listed in the [README](../README.md#features).

## Next — SSH workflow gaps

- **SSH jump host (ProxyJump)**: connect through a bastion. Today only RDP has a gateway
  (`GatewayHostname`); SSH/SFTP go direct. SSH.NET can chain it over a forwarded port.
- **SSH agent / Pageant** ([REVIEW.md](REVIEW.md) D3), including the built-in Windows OpenSSH agent.
- **Import `~/.ssh/config` and `~/.ssh/known_hosts`** (D2): hosts, users, keys and ProxyJump in one
  go, the same way the mRemoteNG/RDCMan imports work.
- **Dynamic (`-D`, SOCKS) and remote (`-R`) tunnels**, since only local `-L` forwards exist today.
  Also a small view of active tunnels.
- **Terminal settings**: configurable scrollback (hardcoded to 5000 in `XtermControl`) and optional
  automatic session logging to a dated file (D4). Saving a transcript by hand already works.

## Then — organizing many servers

- **Nested groups.** `ServerInfo.Group` is a flat string today, even though mRemoteNG/RDCMan imports
  carry folder paths.
- **Settings inherited from the group** (domain, credential profile, gateway, redirections).
- **Search filters** such as `tag:prod proto:ssh`, plus saved views. Tags already exist on servers.
- **Sync across my own machines** by pointing the data folder (servers, snippets, REST collections)
  at OneDrive or similar. Secrets stay in each machine's Credential Manager.
- **Portable mode**: data next to the exe instead of `%APPDATA%`.

## Credentials & security

- **Free password managers** as a credential source: KeePassXC and Bitwarden CLI.
- **M2**: FTP "Auto" encryption mode silently falls back to plaintext. Warn or remove it.
- **M3**: REST variables have no "secret" type (plaintext in `rest.json`).
- **L3**: consider defaulting new RDP entries to "require" server identity verification.

## Later — new modules and bigger features

- **Discovery**: subnet scan (ping + 22/3389/5900) with "add found hosts"; import from Proxmox /
  Hyper-V.
- **Run a command on many hosts** with results in a table, extending broadcast and snippets.
- **REST client**: per-request / per-collection timeout (D7, today fixed at 60 s), export to cURL,
  import OpenAPI/Swagger, run a whole collection with a test report.
- **File manager**: resumable transfer queue, directory compare/sync, and edit a remote file in a
  local editor with upload on save.
- **Dashboard**: availability/latency history per server from the background probe.

## Code health (ongoing)

- **The `MainWindow` refactor is done.** All six steps of
  [REFACTOR-MAINWINDOW.md](REFACTOR-MAINWINDOW.md) landed (PRs #155–#165). The file went from ~6000
  to ~3400 lines. Candidates for further extraction are the remaining sections: the REST sidebar and
  its context menu, settings, credential profiles, dashboard/recents, snippets and the tray/hotkey.
  One section per PR, same move-method rules.
- **Empty `catch {}` blocks** (~80): keep the deliberate ones, and send the rest to `PersistLog` so
  failures stay diagnosable.
- **Enable `Nullable`** file by file.
- **UI smoke test in CI** (FlaUI on `windows-latest`): launch, add a server, open the editor, close.
- Tip: the solution also **builds on Linux** with `dotnet build RdpManager.sln -p:EnableWindowsTargeting=true`,
  which is enough to catch compile errors. Running the tests still needs Windows.

## Not planned

- **Paid code signing** (OV/EV, Azure Trusted Signing): no budget. The self-signed certificate plus
  publisher pinning covers auto-update.
- **winget / package managers**: not worth it for a single user, and in-app auto-update already
  delivers new builds.

## Done

- **v1.1**: tabbed RDP, embedded SSH terminal (xterm.js), SSH host-key TOFU, mstsc/.rdp/mRemoteNG/RDCMan
  import, multi-monitor session windows, focus mode, full screen, dynamic resolution, Credential
  Manager, server tree, quick connect, themes, EN/PL.
- **After v1.1, up to v1.9.1**: SSH local tunnels and key passphrases, tray + global quick-connect
  hotkey, auto-update with publisher pinning, RDP admin session, RemoteApp, Wake-on-LAN, VNC, Telnet,
  serial, FileZilla import, dual-pane SFTP/FTP/FTPS manager, REST client (collections, environments,
  Postman import, scripts), the "Compass" redesign, shared credential profiles, command palette,
  command snippets, SSH broadcast and transcript, WebView2 dashboard, tab-strip styles and
  server-list density options, plus the hardening from the 2026-07 review ([REVIEW.md](REVIEW.md)).
