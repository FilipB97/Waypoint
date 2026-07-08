# Waypoint — app review (2026-07)

Static review of `src/RdpManager` (~16k lines) + tests, across four axes: **security,
resilience/correctness, accessibility/UI-UX, and functionality/architecture**. Key findings were
manually verified against the code (file:line references below). This document is the source of
truth for the review; [`ROADMAP.md`](ROADMAP.md) links back to specific IDs from here.

## 0. Summary and top priorities (do these first)

Domain code is clean and well tested (REST parsing/scripting, self-healing persistence,
known-hosts, imports). Risk concentrates in: (1) file transfer and the REST client, (2)
`MainWindow` as a god object, (3) accessibility gaps. Two real security issues need prompt
attention.

**Top 7:** **H1** FTPS accepts any certificate (MITM) · **H2** path traversal / "zip-slip" on
directory download · **A1** no REST response size limit (OOM) · **A2** file transfer has no
cancel/overwrite-policy/progress · **1.1** no dialog closes on Escape · **1.3** icon buttons have
no screen-reader name · **L2/M4** Jint sandbox limits + URL scheme validation.

## 1. Security

### Critical / high

- **H1 — FTPS accepts any certificate (active MITM).** [`FtpFs.cs:40`](../src/RdpManager/FtpFs.cs#L40)
  `ValidateAnyCertificate = true`. An active MITM can present any certificate, capturing the FTP
  login and the entire transfer. *Fix:* TOFU/pinning like SSH (`Core/KnownHosts.cs`) — SHA-256 of
  the cert per host:port, prompt on new/changed (reject by default), show the fingerprint.
- **H2 — Path traversal on directory download ("zip-slip").**
  [`FileTransferPanel.xaml.cs:117-130`](../src/RdpManager/FileTransferPanel.xaml.cs#L117-L130)
  (`dest = Path.Combine(localParentDir, remoteName)`, `remoteName` comes from the remote server's
  listing — `RemoteFs.cs:71`, `FtpFs.cs:51`). A malicious server can return `..\` or an absolute
  path → writes outside the chosen directory (e.g. into Startup ⇒ persistence/RCE). *Fix:*
  sanitize every server-provided name (reject `/`, `\`, `..`) and enforce containment:
  `Path.GetFullPath(dest).StartsWith(rootFull + sep)`. Also applies to
  `DualFilePanel.xaml.cs:58,69`.

### Medium

- **M1 — Update check only extracts the signing cert, doesn't verify the Authenticode signature.**
  [`Core/CodeSign.cs:35`](../src/RdpManager/Core/CodeSign.cs#L35)
  (`X509Certificate.CreateFromSignedFile` without `WinVerifyTrust`). Pinning only checks that the
  file *contains* a cert with that thumbprint — not that the signature actually covers the file's
  content. *Fix:* `WinVerifyTrust` (`WINTRUST_ACTION_GENERIC_VERIFY_V2`, allow a self-signed
  chain but confirm the digest) → then compare thumbprints; alternatively compare a SHA-256 from
  the GitHub API (over TLS) against the downloaded bytes.
- **M2 — FTP "Auto" silently allows plaintext.** `FtpFs.cs:36-39` (`Auto`); import maps plain FTP
  to Auto (`Core/ExternalImport.cs:237`); the unencrypted warning only fires for `None`
  (`MainWindow.xaml.cs:3579`). *Fix:* treat Auto as "require TLS, warn/refuse if it can't
  upgrade"; warn whenever the connection is actually plaintext.
- **M3 — REST environment variables are stored in plaintext on disk; no "secret" variable type.**
  [`Models/RestModel.cs:87`](../src/RdpManager/Models/RestModel.cs#L87) →
  `RestStore.Save` (`RestStore.cs:47`) → `rest.json`. *Fix:* an `IsSecret` flag on `RestVariable`,
  value goes to `CredentialStore` (like `AuthSecret`), only a reference/blank in the JSON.
- **M4 — Web-panel entries launch without scheme validation.**
  [`MainWindow.xaml.cs:2150-2160`](../src/RdpManager/MainWindow.xaml.cs#L2150-L2160)
  (`Process.Start(url, UseShellExecute=true)`). A host containing `://` (e.g. `file://`, a custom
  handler) — RDCMan import (`ExternalImport.cs:167,171`) gives a "click → launch" primitive.
  *Fix:* `Uri.TryCreate` + allow only `http`/`https`.

### Low / informational

- **L1** — secrets held in memory as `string` (not zeroed); consider `SecureString`/`byte[]` for
  the RDP password path (`RdpConnect.cs:48`). Defense-in-depth.
- **L2 — Jint sandbox has no memory/instruction limit (DoS).** `RestScript.cs:39` has
  `LimitRecursion` + a 5s timeout, no `LimitMemory`/`MaxStatements`. CLR access is disabled
  (good). *Fix:* add the limits; warn when importing collections that contain scripts.
- **L3** — RDP defaults to `AuthenticationLevel=2` (warn), not "require". `ServerInfo.cs:80`,
  `RdpConnect.cs:23`. Matches mstsc; consider "require" for high-value hosts.
- **L4** — TOCTOU on the update file in `%TEMP%` (`MainWindow.xaml.cs:406→424`,
  `App.xaml.cs:155`). Re-verify at bootstrap or verify after acquiring a handle.

**Done well (don't change):** no secret serialization (`ServerInfo`/`RestModel`/
`CredentialProfile` — `[JsonIgnore]`, passwords in Credential Manager/DPAPI); no secret logging
(`ConnectionLog`, `PersistLog`); solid SSH TOFU (`KnownHosts.cs`, `SshConnectionFactory.cs:84-118`);
XXE is impossible (`XDocument.Parse` on .NET 8, DTD prohibited); Jint has `AllowClr` off; atomic
writes (`AtomicFile.cs`) with `.bak`/`.corrupt`; RDP runs in-process (no `.rdp` with an embedded
password, no argument injection); WOL validates the MAC; `HttpClient` strips `Authorization` on a
cross-host redirect; REST history records URLs with `{{placeholders}}` unsubstituted (secrets
never land in `rest.json`).

## 2. Resilience and correctness

- **A1 [high] No REST response size limit (OOM, corrupts binaries).**
  [`RestClient.cs:51-66`](../src/RdpManager/RestClient.cs#L51-L66) (`ResponseContentRead` +
  `ReadAsStringAsync`). *Fix:* `ResponseHeadersRead`, check `ContentLength`, cap at N MB (stream +
  truncate), message "response too large — not displayed"; binaries → write to a file instead of
  a string.
- **A2 [bug] Recursive transfer has no cancellation, overwrite policy, or progress.**
  [`FileTransferPanel.xaml.cs:99-130`](../src/RdpManager/FileTransferPanel.xaml.cs#L99-L130). No
  `CancellationToken` (closing the tab → `DisposePanel` nulls `_fs` mid-transfer → half-copy);
  upload always `overwrite:true`, download always `File.Create` (clobbers without asking); only
  per-file status text rather than a progress bar (README promises "with progress"). *Fix:*
  thread a `CancellationToken` through, an overwrite policy (skip/overwrite/rename, ask-once),
  real byte-level progress.
- **A3 [bug] The `_busy` guard silently drops clicks.** `FileTransferPanel.xaml.cs:147` —
  Upload/Download/NewFolder/Delete during another operation returns `false` with no feedback
  (also `DualFilePanel.xaml.cs:50-60`). *Fix:* disable the buttons during an operation (visible
  state) or queue the request.
- **A4 [risk] Reachability probing spawns a thread per server.**
  `MainWindow.xaml.cs:4971-4975` + `Probe:5037-5057` (blocking `WaitOne(1500)`, no `EndConnect` on
  timeout). Hundreds of servers flood the thread pool every 30s. *Fix:*
  `ConnectAsync().WaitAsync(timeout, ct)` + a `SemaphoreSlim` for concurrency.
- **A5 [risk] The global dispatcher handler swallows ALL UI exceptions.**
  `App.xaml.cs:59-65` (`Handled=true`). Justified for SEH from `mstscax`, but masks real NREs.
  *Fix:* only swallow `SEHException`/`COMException` from RDP; re-throw/toast the rest (Debug).
- **A6 [smell] Wide `async void` surface without guards.** e.g. `RestConsole.Send_Click` doesn't
  protect `RenderResponse`/`RecordHistory` (I/O). *Fix:* try/catch that restores UI state
  (`SendBtn.IsEnabled=true`).
- **A7 [bug] `RestConsole` leaks its `CancellationTokenSource` and can wedge "Send".**
  `RestConsole.xaml.cs:463-477` (the old CTS is never `Dispose`d; `IsEnabled=true` isn't in a
  `finally`). *Fix:* `using`/dispose the previous CTS; re-enable in `finally`.
- **A8 [bug] The REST environment editor doesn't honor Cancel.** `RestEnvWindow.xaml.cs:40,47-49`
  edits the SAME `RestVariable` objects the collection holds. *Fix:* edit a deep copy, merge only
  in `Close_Click`.
- **A9 [low risk] Possible infinite recursion on a cyclic `ParentId`.**
  `RestAuthResolve.cs:22-26`, `RestConsole` (`BuildFolderNode`/`DeleteFolder`). *Fix:* a
  visited-Id set.
- **A10 [nit]** Variable substitution is single-pass (no nested `{{...}}`) — `RestClient.Subst:175-180`.
- **A11 [nit]** `CredentialStore` uses `CRED_PERSIST_LOCAL_MACHINE` despite docs saying "current
  user" (`CredentialStore.cs:15,35`) — not a leak, but a comment/consistency fix.

**Solid (leave alone):** `XtermControl` (`_disposed` + `Interlocked`), terminals null the
transport before dispose, the "tab closed mid-connect" race is handled, no `.Result`/`.Wait()` on
the UI thread.

## 3. UI/UX and accessibility

### Accessibility (most systemic)

- **1.1** No dialog closes on Escape (`IsCancel` used 0×). *Fix:* `IsCancel="True"` on the
  Cancel/Close button in 9 windows (`ServerEditWindow.xaml:257`, `CredentialPromptWindow.xaml:41`,
  `CredentialProfileWindow.xaml:36`, `InputDialog.xaml:27`, `SessionRestoreWindow.xaml:32`,
  `RestAuthWindow.xaml:80`, `RestEnvWindow.xaml:125`, `AboutWindow.xaml:42`,
  `ReleaseNotesWindow.xaml:39`) + an Escape handler for `PasswordGeneratorWindow`.
- **1.2** `ServerEditWindow` (the largest form) has no `IsDefault`/`IsCancel`
  (`:257-258`). *Fix:* at least Escape→Cancel (a blanket Enter is risky given multiline fields).
- **1.3** ~30 icon buttons have no `AutomationProperties.Name` (used only 6× in the rail).
  *Fix:* add Name (same string as the tooltip) in `RestConsole.xaml`, `FileTransferPanel.xaml`,
  `RestEnvWindow.xaml`, `DualFilePanel.xaml`.
- **1.4** No mnemonics/access keys (`AccessText` used 0×). *Fix:* `_` in the main button/menu
  labels for the import menu.
- **1.5** No visible keyboard-focus indicator on the custom templates (`RailBtn`, `IconBtn`,
  `SettingsNavItem`, `FtIconBtn`/`FtRow`, `RestIconBtn`, `EnvIconBtn`). *Fix:* an
  `IsKeyboardFocused` trigger (accent ring) or a shared `FocusVisualStyle`.
- **1.6** `TextTer` (#6C6F78 / #888B93) is below WCAG AA for 10-11.5px text carrying information
  (59 uses). *Fix:* darken/lighten to ~4.5:1 contrast, or stop using it for informational text
  under 12px.

### Visual consistency / design system

- **2.1** 4 raw hex values outside the palette (`MainWindow.xaml:641,647`, `SessionWindow.xaml:25`)
  plus 4× `White` fills; the split-overlay colors don't exist in the palette (won't theme). *Fix:*
  move into the palette/tokens.
- **2.2** No `CornerRadius` token — 6 ad-hoc values (2/5/6/7/8/10). *Fix:* `RadiusSm/Md/Lg`.
- **2.3** Inconsistent elevation/shadow (cards and the sidebar have none; two different recipes
  for the same "floating" bars).
- **2.4** Margins/padding follow no scale; dialog button rows differ (`18,4,18,16` vs
  `16,12,16,12` vs `20,14,20,18`).
- **2.5** Font sizes are unordered (10-22 with halves). *Fix:* a 4-5-step ramp as resources.
- **2.6** Mica is advertised but hidden behind an opaque `CanvasBrush` on content
  (`MainWindow.xaml:344,382,390,398`) — confirm this is a deliberate choice (RDP legibility) or
  drop the backdrop.
- **2.7** Two full-screen bars, two different looks (`MainWindow` FsPopup vs
  `SessionWindow.xaml:86`, which uses the tooltip key `S.tip.minimize` as its VISIBLE label).

### Interaction / affordances

- **3.1** The server tree has no empty state / "no results" (`RenderTree`
  `MainWindow.xaml.cs:1562-1607`). *Fix:* a placeholder distinguishing "no servers" from "no
  results for '<query>'" (mirroring `S.dash.norecent`).
- **3.2** No onboarding / first-run experience (empty rail + empty sidebar).
- **3.3** Search boxes have no "×" clear button (4 boxes: `SearchBox`, `SettingsSearch`,
  `QuickSwitchSearch`, `FsFlyoutSearch`).
- **3.4** `ServerEditWindow` validation is `MessageBox`-only, no inline feedback and no focus on
  the offending field (`ServerEditWindow.xaml.cs:313-367`). *Fix:* inline (red border/`InfoBadge`)
  + `Focus()` on the bad field.
- **3.5** "Tools"/"About" in the rail sit outside the selection model (`Nav_Click`/`ShowView`
  `:504-529`).
- **3.6** Password reveal only exists in `ServerEditWindow` (`:152-155`); missing from
  `CredentialPromptWindow`, `CredentialProfileWindow`, the REST auth fields — inconsistent.

**Localization** — very good: 469 keys, 100% PL/EN parity, no literals in `MessageBox.Show`.
Remaining nits: `MainWindow.xaml:373` `Content="Połącz ponownie"` (dead design-time literal),
`SessionWindow.xaml:5,24` `Title="Sesja"`, `SessionWindow.xaml:86` uses the WRONG key
(`S.tip.minimize` as visible Content). *Fix:* → `DynamicResource`.

### Feel / micro-interactions

- **5.1** Almost no motion — only 2 animations in the whole app
  (`MainWindow.xaml.cs:708-714`, `:2124-2135`); the custom templates snap between states while
  `ui:Button` animates, so the two feel inconsistent side by side. *Fix:* a ~120ms fade on
  hover/press in `RailBtn`/`IconBtn`/`FtRow`/`RestIconBtn`/`SettingsNavItem`.
- **5.2** Inconsistent loading states — the session spinner is good, but REST "Send" and file
  transfer only show text (no ring/bar). *Fix:* a progress ring on "Send" + a real transfer
  progress bar (ties into A2).
- **5.3** The REST response status pill (`StatusPill`) is a good pattern — just needs its `White`
  text tokenized (see 2.1).

## 4. Architecture and maintainability

- **B1 [high] `MainWindow.xaml.cs` is a god object (5096 lines, ~200 methods).** Update+download+
  bootstrap (`:288-497`), tray+hotkey+P/Invoke, fullscreen/focus state machines, the settings
  form, profile CRUD, the dashboard, tree rendering+drag/drop+multiselect, the tab strip+groups+
  split, lifecycle for 8 protocols, reachability/WOL. *Incremental approach:* extract
  `UpdateService`, `SessionManager`, `ServerTreeController`, `FullscreenController`/
  `FocusModeController`, `TabStripController` (mechanical move-method, not an MVVM rewrite).
- **B2 [high] MVVM in name only.** `MainViewModel` (78 lines) is just a list holder; all state and
  logic lives in code-behind, UI is built imperatively. This is the source of the low
  testability. *Fix:* move session/tab state into a `SessionsViewModel` + `DataTemplate`.
- **B3/B4 [smell] Duplicated RDP lifecycle** between `MainWindow.WireEvents:3612-3671` and
  `SessionWindow.WireEvents:160-189`, plus a repeated `if (s==_active){...}` epilogue (~10×).
  *Fix:* a shared `OnSessionConnected/Disconnected`.
- **B5 [smell] No `SchemaVersion` in the JSON files** (`AppSettings`/`ServerInfo`/
  `RestCollection`). `RestRequest.AuthType` already changed meaning (added 3=Inherit) with no
  marker. *Fix:* an `int SchemaVersion` + a `Migrate()` step.
- **B6 [nit]** `RestStore.Put` does a read-modify-write of the whole file per call (`:58-63`).

## 5. Test coverage gaps

Well covered (pure logic): URL/query/form building, `Subst`, Postman import (scripts/urlencoded/
auth), REST scripts (+ legacy `postman.*`), auth inheritance, store round-trip/corruption,
known-hosts, WOL, the password generator.

- **C1** `RestClient.SendAsync` is untested (timeout/headers/Bearer/Basic) — stub an
  `HttpMessageHandler`.
- **C2** No test for a large response / cancellation (A1/A7).
- **C3 [highest value]** Recursive transfer is untested — `UploadTree`/`DownloadTree` can be
  tested end-to-end against two `LocalFs` instances (temp dirs), covering recursion, overwrite
  behavior, and path traversal (H2/A2).
- **C4** Session/tab/split lifecycle is untestable as written (welded into code-behind — see
  B1/B2); `TabGroup` could be extracted.
- **C5** No SchemaVersion/migration tests (B5).
- **C6** No test for Postman secret-blanking behavior (`PostmanImport.cs:63-81`).

## 6. Proposed features / quick wins

- **D2 [quick]** Import OpenSSH `~/.ssh/known_hosts` into `known_hosts.json` (already promised in
  ROADMAP/README).
- **D5 [quick]** Terminal theme following the app's Dark/Light + font size from settings (today
  hardcoded in `XtermControl.BuildHtml:221-222` — a Light-theme user gets a dark terminal).
- **D6** A one-time warning for plaintext FTP/FTPS (ties into H1/M2, mirrors
  `WarnUnencrypted:3344`).
- **D4** Session logging / SSH transcript (hook at `SshTerminalControl.OnShellData:174`).
- **D7** Per-request / per-collection REST timeout (today a hardcoded 60s, `RestClient.cs:41`).
- **D3** SSH agent / Pageant + passphrase-agent support (`SshConnectionFactory` — today just a
  password or an on-disk key).
- **M3** REST secret-typed variables (see the security section).
- Additional ideas: import `~/.ssh/config` (SSH host migration), encrypted profile export/backup,
  reachability history/chart (`ConnectionStats` already exists), bulk operations on selected
  servers.
- **D8 [nit]** `TryResizePty` uses reflection (`SshTerminalControl.cs:198-211`) — add a one-time
  capability check.

## 7. Suggested implementation order (for a separate session)

1. **Cheap + critical security:** H2, M4, L2 (small, high ROI), then H1 (FTPS TOFU), M1
   (WinVerifyTrust).
2. **Resilience + tests:** A1 (response limit), A2/A3 (cancel/overwrite/progress) + C3 tests on
   `LocalFs`, A4 (async probing), A7.
3. **Accessibility (cheap, high ROI):** 1.1/1.2 (Escape), 1.3 (Automation Name), 1.5 (focus), 3.1
   (empty state).
4. **Feel/look:** design tokens (2.2/2.4/2.5), micro-animations (5.1), inline validation (3.4),
   terminal theming (D5).
5. **Architecture (separate PRs):** extract `UpdateService`/`SessionManager` from `MainWindow`
   (B1) + `SchemaVersion` (B5).
