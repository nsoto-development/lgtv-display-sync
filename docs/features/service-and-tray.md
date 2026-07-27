# Feature: Service + tray packaging

## Purpose

Make `lgtv-display-sync` usable as an **interim daily driver** until ColorControl’s wake path works reliably under VPN: run as a real Windows service when non-interactive, offer an official autostart install path, and provide a user-session **`--tray` companion** so the operator can see and control that service from the system tray.

## Roadmap

Tracks **[feature] work item #1 (P0)** on [`docs/roadmap.md`](../roadmap.md).

## v1 scope (agreed)

- **Dual-mode host:** detect service / non-interactive context and host via the Windows service model (session 0 capable). If the exe is launched directly **without** `--tray`, keep today’s console-app watcher behavior.
- **Official install / autostart:** a supported command or script (and README section) to create, configure, and start the Windows service — not the experimental `probe/ctx` session-0 helpers.
- **Tray companion (`--tray`):** same EXE, user session only. Does **not** run the display watcher / SSAP loop. Shows SCM status for `lgtv-display-sync` (**Running** / **Stopped** / **Not installed**), and a minimal menu: **Start**, **Stop**, **Open log folder**, **Exit** (quit tray only; leave service as-is). Hide the console window in this mode. Icon: `appicon.ico`. Stack: thin Win32 `NotifyIcon` (no WinForms; no App SDK in M4). Start/Stop prompts UAC via a one-shot elevated child (`--elevated-service-ctl`); the tray process itself is not elevated.
- Preserve existing CLI: `--pair`, `--test …`, `--watch-only`, and default console watcher loop.

Session 0 isolation: the **service process** cannot own a tray icon. The tray is always a separate interactive process (`--tray`).

## Non-goals (v1)

- Full control panel / settings UI.
- Replacing ColorControl’s broader feature set.
- Changing SSAP / WoL retry policy (already validated).
- Tray UI inside the session-0 service process (impossible under Session 0 isolation).
- Running a second watcher from `--tray` (companion only).
- Autostart registration for the tray companion (Run key / Startup) — launch `…exe --tray` manually for v1.
- Microsoft Store packaging (WinUI unpackaged / normal EXE is fine when a real window is needed later).

## Future hooks

- Tray login autostart (Run key / Startup) if interim use lasts.
- Richer tray actions (force on/off) and a small modern status/settings surface (WinUI unpackaged).
- Self-contained publish + single-folder install layout (roadmap P1).
- Retire or slim this feature if ColorControl absorbs the connect strategy.

## Code paths

| Area | Location |
|------|----------|
| Entry / watcher loop | `app/Program.cs` |
| Windows service worker | `app/WatcherHostedService.cs` |
| Tray companion (`--tray`) | `app/TrayCompanion.cs` |
| Data paths (ProgramData + local override) | `app/AppPaths.cs`, `app/KeyStore.cs` |
| Display events | `app/MonitorPowerWatcher.cs` |
| SSAP / WoL / keys | `app/Ssap.cs`, `app/Wol.cs`, `app/KeyStore.cs` |
| Icon | `app/appicon.ico` |
| Project (Win32 message loop; Hosting Windows Services) | `app/app.csproj` |
| Service install / uninstall | `app/scripts/install-service.ps1`, `app/scripts/uninstall-service.ps1` (copied to build output root) |

## Data directory

- **Default (interactive + service):** `%ProgramData%\nsoto.dev\lg-tv-display-sync\` with `config\` (client keys) and `log\` (`log.txt`).
- **Load override:** `--keyfile` / config `KeyFile`, else existing `%LocalAppData%\lgtv-display-sync\{ip}_ClientKey.txt`, else `config\{ip}_ClientKey.txt`, else a flat ProgramData key (migrated into `config\`); ColorControl migrate still applies when none found.
- **Save:** explicit path if set, otherwise `config\` under ProgramData.
- **Service account:** LocalSystem (set by `install-service.ps1` in the build output). Install creates `config\` + `log\`, grants SYSTEM modify on the tree, and copies a legacy LocalAppData (or flat) key into `config\` when missing. The script resolves `lgtv-display-sync.exe` as a sibling of itself (no hardcoded configuration path).
- **Tray “Open log folder”:** opens `log\` (not the ProgramData root).

## Service identity (M3)

| Field | Value |
|-------|--------|
| SCM name | `lgtv-display-sync` (matches `AddWindowsService` in `Program.cs`) |
| Display name | `LG TV Power Resume Sync Utility (nsoto.dev)` |
| Description | Watches Windows display on/off and syncs an LG webOS TV (Wake-on-LAN + SSAP). Runs in session 0 so resume still works when no user is logged on. |

## Milestones

Execution order is the table order (drop WinForms before service/tray work).

| # | Milestone | Status | Deliverables |
|---|-----------|--------|--------------|
| M1 | Drop WinForms | Done | Replace `NativeWindow` / `Application.Run` with Win32 HWND message loop; remove `UseWindowsForms`; record WinUI unpackaged as later UI direction |
| M2 | Dual-mode Windows service host | Done | Non-interactive → true service (`UseWindowsService` + hosted Win32 pump); direct launch → console; ProgramData data dir with local key override |
| M3 | Official service install / autostart | Done | `app/scripts/` install + uninstall copied flat to bin; sibling-exe resolution; LocalSystem auto-start; ProgramData key copy + SYSTEM ACL; README updated |
| M4 | `--tray` service companion | Done | Flag-gated NotifyIcon companion: SCM status (Running/Stopped/Not installed), Start/Stop, open log folder, Exit; no watcher in tray process; console default unchanged; README note |

**Quick gate:** each implementation thread names **one milestone** (e.g. “M4 only”), not the whole P0 item.

## UI stack (agreed direction)

- **M4:** thin Win32 `Shell_NotifyIcon` + message-only HWND. No WinForms. No Windows App SDK yet.
- **Later (when a real window is needed):** prefer WinUI 3 (Windows App SDK), unpackaged — normal desktop EXE (`WindowsPackageType=None`).
- **Do not grow WinForms.** M1 already replaced `NativeWindow` + `Application.Run` with a Win32 message loop.
- **Service path stays headless.** Session-0 service has no tray/UI; `--tray` is the user-visible surface.

## Risks (M4)

- Start/Stop of a LocalSystem service needs admin rights. The tray stays non-elevated; each Start/Stop launches a short elevated child (`--elevated-service-ctl`) so Windows prompts UAC once per action (cancel leaves the service unchanged). No always-on elevated tray / helper.
