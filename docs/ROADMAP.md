# Roadmap

Goal: a modern, lightweight **RDP + SSH** connection manager that replaces `mstsc.exe` day to
day and gives users of dated managers (mRemoteNG, RDCMan) an easy way over. Order reflects
current priorities.

## Done (v1.1)

- ✅ **Tabbed RDP sessions** — live control per tab, switch without disconnecting; duplicate
  tab, middle-click close, drag-and-drop tab reorder, `Ctrl+Tab` / `Alt+1..9` / `Ctrl+W`.
- ✅ **Embedded SSH terminal** — WebView2 + xterm.js (bundled offline) + SSH.NET; password
  and private-key auth, keyboard-interactive; copy-on-select, `Ctrl+Shift+C/V`,
  Ctrl+wheel font size; local status lines printed in-terminal.
- ✅ **SSH host-key verification (TOFU)** — SHA256 fingerprint prompt on first connect,
  stored in `known_hosts.json`; changed key = loud warning, defaults to reject.
- ✅ **Migration imports** — mstsc registry history, `.rdp` files (multi-select),
  **mRemoteNG** `confCons.xml` (RDP + SSH, folder paths preserved), **RDCMan** `.rdg`
  (inherited credentials); dedup by host:port; passwords intentionally not migrated.
- ✅ **Multi-monitor, mstsc-style** — standalone session windows (drag to a monitor, go
  full screen; maximizing a session window = full screen); tear-off a tab ↔ dock back.
- ✅ **Focus mode** — maximized main window hides all chrome except the tab strip;
  window controls move onto the strip. Distinct from true full screen (F11).
- ✅ **Full-screen** with auto-hiding, draggable, pinnable bar + "other connections" flyout.
- ✅ **Dynamic resolution** — session = window/monitor physical pixels, DPI-safe.
- ✅ **Credentials in Windows Credential Manager** (DPAPI); prompt at connect; "connect as".
- ✅ **Server tree** — groups, favorites (pinned), collapsible groups, one-click group
  rename, search, drag-and-drop reorder, background reachability dots.
- ✅ **Quick connect** (`host`, `host:port`, `user@host`, `DOMAIN\user@host`).
- ✅ **Per-server RDP options** — port, domain/Windows account, redirections
  (clipboard/drives/printers/audio), server-identity verification, RD Gateway.
- ✅ **Themes** (dark / light / system) and **localization** (English / Polish), both
  switchable live.
- ✅ **Dashboard, recents, connection audit log, per-server TCP diagnostics, profile
  export/import, UI zoom, single-instance guard.**
- ✅ **Packaging** — self-contained single-file exe; GitHub Actions release workflow
  (`v*` tag or manual dispatch); `scripts/release.ps1`.

## Next

- **Publish v1.1** — merge to `master`, tag, GitHub Release; then a **winget** manifest
  (`winget install waypoint`) for low-friction installs.
- **SSH tunnels / port forwarding** — local forwards per server (SSH.NET supports it);
  the killer feature for "database behind a jump host" workflows.
- **Auto-update** — check GitHub Releases, offer one-click update (important while unsigned).
- **SSH key passphrase** + OpenSSH agent / Pageant support; import OpenSSH `known_hosts`
  (see [REVIEW.md](REVIEW.md) D2).
- **Code signing** — Azure Trusted Signing or SignPath (OSS) to remove the SmartScreen warning.
- **Tray icon + global Quick Connect hotkey.**
- **RDP admin/console session** (`/admin`) and **Wake-on-LAN**.

## Hardening & polish (from the 2026-07 review)

Full write-up: [REVIEW.md](REVIEW.md). IDs below refer to its findings.

**Critical**
- **H1** — FTPS accepts any certificate; needs TOFU/pinning like the SSH known-hosts model.
- **H2** — path traversal / "zip-slip" on directory download (unsanitized remote file names).

**Soon**
- **A1** — no size limit on REST responses (OOM / binary corruption risk).
- **A2** — file transfer has no cancellation, overwrite policy, or real progress.
- **1.1 / 1.3 / 1.5** — accessibility: no dialog closes on Escape, icon buttons lack
  screen-reader names, no visible keyboard-focus ring on custom controls.
- **M1 / M4 / L2** — security: update check doesn't verify the Authenticode signature (only
  extracts the cert), web-panel launch has no URL scheme allowlist, Jint sandbox has no
  memory/instruction limit.
- **A4** — reachability probing spawns a thread per server instead of using async sockets.

**Later**
- Design tokens (corner radius, spacing, type scale) + micro-animations on hover/press
  (REVIEW.md 2.2–2.5, 5.1).
- `MainWindow` god-object refactor — extract `UpdateService`, `SessionManager`,
  `ServerTreeController`, focus/fullscreen controllers (REVIEW.md B1/B2).
- `SchemaVersion` + migration step for the JSON stores (REVIEW.md B5).
- New features: SSH agent/Pageant support (D3), session logging (D4), REST per-request
  timeout (D7), REST secret-typed variables (M3).

## Later / ideas

- SFTP file browser attached to SSH sessions.
- Sub-groups / tags in the server tree; shared credential profiles.
- Light terminal theme following the app theme; terminal font/color settings
  (see [REVIEW.md](REVIEW.md) D5).
- Session logging (SSH transcript), configurable scrollback (see [REVIEW.md](REVIEW.md) D4).
- Portable mode (settings next to the exe).
- ARM64 build.
