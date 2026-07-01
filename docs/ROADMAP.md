# Roadmap

Goal: make RDP Manager a full daily replacement for the Windows built-in Remote Desktop
Connection (`mstsc.exe`). This list comes from a full review of the current tool. Order
reflects the chosen priorities.

## Priority 1 — mstsc parity / daily use

- **Credential prompt at connect time + per-server.** Show a login dialog when no password is
  saved, allow "retry with different credentials" after an auth failure, with an optional save.
  Today the password can only be typed in the session bar.
- **RD Gateway / jump-host.** Support `IMsRdpClientTransportSettings` (`GatewayHostname`,
  `GatewayUsageMethod`, `GatewayProfileUsageMethod`) to connect through a TS Gateway / bastion.
- **Import / export `.rdp`.** Parse existing `.rdp` files from `mstsc` (map `full address`,
  `username`, `gatewayhostname`, redirection keys, …) and export back — eases migration.
- **Multi-monitor.** Per-monitor / spanned full screen (`UseMultimon`).

## Priority 2 — architecture & quality

- **MVVM refactor.** Extract `SessionsViewModel` / `ServerViewModel` (per
  `plan-wdrozenia-ui-fluent-mica.md` §4), add `INotifyPropertyChanged` and DI, and break up the
  monolithic `MainWindow.xaml.cs` — this also unlocks wider unit-test coverage.
- **Logging / diagnostics.** Optional connection audit log, built-in reachability diagnostics
  (ping / port), clearer RDP error codes in the UI.

## Priority 3 — UX polish

- **Keyboard shortcuts:** `Ctrl+Tab` (cycle tabs), `Alt+1..9` (jump to tab), `Ctrl+T`
  (new session), `Ctrl+F` / `Ctrl+K` (focus search).
- **Tabs:** overflow/scroll menu for many sessions, drag-and-drop reorder, context menu
  (duplicate session), show port in the title on name collisions.
- **Recents view** is currently a stub (`AppSettings.RecentIds` exists but the view is not
  populated).
- **Full screen:** a "pin bar" option and keyboard accessibility for the "other connections"
  flyout; fully populate the "all servers" section (`FlyoutServers`).
- **Redirection:** consider safer defaults (clipboard redirection is on by default today);
  add USB / serial redirection as an extension.

## Priority 4 — distribution

- **Release workflow** (`release.yml`): signed, self-contained x64 build published to
  GitHub Releases on `v*` tags.
- **Profile import/export** (backup of server list & settings), and optional sync.
