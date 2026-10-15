# Roadmap

Goal: one modern app for every remote connection, replacing `mstsc.exe`, PuTTY, WinSCP and Postman
day to day, with an easy way over from dated managers (mRemoteNG, RDCMan). Order reflects current
priorities. Last updated for **v1.9.4**.

## Done

### Windows (v1.1 → v1.9.4)

- ✅ **Protocols** — tabbed **RDP** (official `mstscax`, dynamic resolution, RD Gateway, RemoteApp,
  `/admin` session), **SSH**, **Telnet** and **serial** on an embedded xterm.js terminal, **VNC**,
  web links.
- ✅ **SSH** — host-key verification (TOFU), key passphrase prompt, keyboard-interactive,
  **port-forward tunnels**, search, links, **transcript** export, terminal themes that follow the app.
- ✅ **Files** — dual-pane **SFTP / FTP / FTPS** manager (recursive transfers, preview, filter),
  FTPS certificate pinning.
- ✅ **File editor** — Monaco editor for remote files with safe atomic save (encoding, line endings,
  owner/permissions preserved) and **save via sudo** for root-owned files.
- ✅ **REST / API client** — collections and folders, environments with `{{variables}}`,
  Bearer/Basic with inheritance, pre-request and test scripts (`pm.*`), history, **Postman import**.
- ✅ **Organisation** — groups, tags, pinned servers, drag-and-drop, reachability dots
  and latency, dashboard and recents, connection log, command palette, snippets.
- ✅ **Credentials** — Windows Credential Manager, shared **credential profiles**, "connect as",
  password generator.
- ✅ **Migration** — imports from mstsc history, `.rdp`, **mRemoteNG**, **RDCMan**,
  **Remote Desktop Manager**, **FileZilla**; profile export/import.
- ✅ **App** — multi-monitor session windows, focus mode, full screen, themes and palette presets,
  English / Polish, tray icon and global quick-connect hotkey, Wake-on-LAN, **auto-update**,
  optional Authenticode signing in the release workflow.

### macOS (new in v1.9.4)

- ✅ **Native SwiftUI app** (`mac/`, universal Apple silicon + Intel, macOS 14+) with feature parity:
  SSH (system `ssh`, Keychain), RDP via Microsoft Windows App, SFTP/FTP/FTPS with dual pane,
  Monaco file editor with sudo save, Telnet/serial, VNC via Screen Sharing, REST client,
  credential profiles, imports, themes. Shares data formats with Windows (`servers.json`,
  `credprofiles.json`, `rest.json`, …) and ships as `Waypoint-<version>-mac.zip` in every release.
  Details: [mac/README.md](../mac/README.md).

## Next

- **Trusted code signing** — today the exe is optionally self-signed (SmartScreen still warns);
  Azure Trusted Signing or SignPath (OSS). On macOS: Developer ID + notarization (no right-click →
  Open on first launch).
- **Connection diagnostics** — DNS, ping, port and protocol banner in one click (today: TCP port check).
- **winget** manifest (`winget install waypoint`).
- **ARM64 build** for Windows on ARM.
- **Screen capture & recording** of sessions.

## Hardening & polish

Full write-up: [REVIEW.md](REVIEW.md). The 2026-07 review items are done (PRs #105–#112), except:

- **B1/B2 — `MainWindow.xaml.cs` size.** In progress: `SessionManager`, `ServerTreeController`,
  `TabStripController`, `FullscreenController`, `UpdateService` and `ReachabilityService` are
  extracted (≈5100 → ≈3400 lines). Continue one controller per PR with manual regression testing
  ([REFACTOR-MAINWINDOW.md](REFACTOR-MAINWINDOW.md)).
- **M2** — FTP "Auto" falls back to plaintext without a warning (also D6: one-time plaintext warning).
- **M3** — REST variables have no "secret" type (plaintext in `environments.json`).
- **D3** — SSH agent / Pageant support (macOS already uses the system agent via `ssh`).
- **D4** — continuous session logging (today: manual transcript export) and configurable scrollback.
- **D7** — per-request / per-collection REST timeout (today a fixed 60 s).

## Later / ideas

- Portable mode (settings next to the exe).
- Tabs in multiple main windows on macOS (today: a tab can be moved to its own window).
- Embedded VNC viewer on macOS (today: Screen Sharing).
