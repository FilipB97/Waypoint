# Roadmap

Goal: make Waypoint a full daily replacement for the Windows built-in Remote Desktop
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
- ✅ **Profile import/export** — whole profile (servers + settings, no passwords) to a single
  JSON file (`Core/ProfileBackup`, with tests); buttons in Settings.
- ✅ **Multiple sessions per server + duplicate tab**, and tab reorder (move left/right).
- ✅ **MVVM foundation** — `ViewModelBase` + `MainViewModel` own the server collection,
  recents and filtering (`INotifyPropertyChanged`, `ObservableCollection`); code-behind
  delegates to it; unit-tested. (Full XAML data-binding is the next step.)
- ✅ **Multi-monitor** *(experimental, untested on real 2+ monitor hardware)* — per-server
  `UseAllMonitors` sets the RDP control's `UseMultimon` and full screen uses the control's
  own spanning mode; gated on monitor count > 1, so single-monitor behavior is unchanged.
  Mapped to/from `.rdp` (`use multimon`).

## Remaining (need local Windows verification)

- **Multi-monitor — real-hardware validation.** The code path ships gated; verify spanning,
  DPI mix and the control's connection bar once a 2+ monitor setup is available.
- **MVVM — complete the migration.** Bind the XAML directly to the ViewModels (per-item
  `ServerViewModel`/`SessionViewModel`, `ItemsControl` bindings) and add DI, replacing the
  remaining imperative UI construction. Changes UI behavior that unit tests can't catch.
- **Tabs:** true drag-and-drop reorder (context-menu move left/right ships today).
- **Redirection:** safer defaults (clipboard on by default today); USB / serial redirection.
- **Diagnostics:** friendlier RDP disconnect-code descriptions (needs a verified code table).
- **Code signing** of the release binary (currently unsigned).
