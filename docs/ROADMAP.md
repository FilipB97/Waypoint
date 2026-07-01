# Roadmap

Goal: make RDP Manager a full daily replacement for the Windows built-in Remote Desktop
Connection (`mstsc.exe`). This list comes from a full review of the current tool. Order
reflects the chosen priorities.

## Done

- ✅ **Credential prompt at connect time + per-server** — login dialog when no password is
  saved, plus "Połącz jako…" (connect as) for retrying with different credentials.
- ✅ **RD Gateway / jump-host** — `GatewayHostname` / `GatewayUsageMethod` applied via the
  RDP transport settings; configurable in the server dialog.
- ✅ **Import / export `.rdp`** — `Core/RdpFile` parser/serializer (mstsc format), sidebar
  import and per-server export, with unit tests.
- ✅ **Keyboard shortcuts** — `Ctrl+Tab` / `Ctrl+Shift+Tab` (cycle), `Alt+1..9` (jump),
  `Ctrl+W` (close), `Ctrl+F` / `Ctrl+K` (focus search), `Ctrl+±/0` (zoom).
- ✅ **Tabs** — right-click context menu (close / close others) and host suffix to
  disambiguate duplicate server names.
- ✅ **Recents view** — populated from `AppSettings.RecentIds`.
- ✅ **Release workflow** (`release.yml`) — self-contained x64 build published to GitHub
  Releases on `v*` tags.
- ✅ **Logging & diagnostics** — connection audit log (`connections.log`, metadata only,
  toggle in settings) and a per-server "Diagnostyka…" TCP port test.
- ✅ **Full screen "pin bar"** + keyboard focus for the "other connections" flyout;
  mouse-wheel horizontal scroll on the tab strip.

## Priority 1 — remaining big items (need local Windows verification)

- **Multi-monitor.** Per-monitor / spanned full screen (`UseMultimon`). Deferred — hard to
  verify without a multi-monitor setup.
- **MVVM refactor.** Extract `SessionsViewModel` / `ServerViewModel` (per
  `plan-wdrozenia-ui-fluent-mica.md` §4), add `INotifyPropertyChanged` and DI, and break up the
  monolithic `MainWindow.xaml.cs` — unlocks wider unit-test coverage. Large; changes UI
  behavior that unit tests can't catch, so it needs a dedicated, locally-verified PR.

## Priority 3 — UX polish (remaining)

- **Tabs:** drag-and-drop reorder, duplicate session (needs multiple sessions per server).
- **Redirection:** consider safer defaults (clipboard redirection is on by default today);
  add USB / serial redirection as an extension.
- **Diagnostics:** friendlier RDP disconnect-code descriptions (needs a verified code table).

## Priority 4 — distribution

- **Code signing** of the release binary (currently unsigned).
- **Profile import/export** (backup of server list & settings), and optional sync.
