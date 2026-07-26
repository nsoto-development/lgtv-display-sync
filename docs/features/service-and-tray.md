# Feature: Service + tray packaging

## Purpose

Make `lgtv-display-sync` usable as an **interim daily driver** until ColorControl’s wake path works reliably under VPN: run as a real Windows service when non-interactive, offer an official autostart install path, and show a tray icon when the user launches it interactively so they know it is running.

## Roadmap

Tracks **[feature] work item #1 (P0)** on [`docs/roadmap.md`](../roadmap.md).

## v1 scope (agreed)

- **Dual-mode host:** detect service / non-interactive context and host via the Windows service model (session 0 capable). If the exe is launched directly (interactive console), keep today’s console-app behavior for now.
- **Official install / autostart:** a supported command or script (and README section) to create, configure, and start the Windows service — not the experimental `probe/ctx` session-0 helpers.
- **Tray affordance:** when running interactively, show a system tray icon (app already has `appicon.ico`) so the user can see the process is alive; minimal menu is enough for v1 (e.g. Exit / open log folder optional).
- Preserve existing CLI: `--pair`, `--test …`, `--watch-only`, and default watcher loop.

## Non-goals (v1)

- Full control panel / settings UI (tray + “it’s running” is enough).
- Replacing ColorControl’s broader feature set.
- Changing SSAP / WoL retry policy (already validated).
- Forcing tray UI when running as a pure session-0 service (tray is for interactive / user-visible runs).
- Microsoft Store packaging (WinUI unpackaged / normal EXE is fine).

## Future hooks

- Richer tray actions (force on/off, status) and a small modern status/settings surface (WinUI).
- Self-contained publish + single-folder install layout (roadmap P1).
- Retire or slim this feature if ColorControl absorbs the connect strategy.

## Code paths

| Area | Location |
|------|----------|
| Entry / watcher loop | `app/Program.cs` |
| Display events | `app/MonitorPowerWatcher.cs` |
| SSAP / WoL / keys | `app/Ssap.cs`, `app/Wol.cs`, `app/KeyStore.cs` |
| Icon | `app/appicon.ico` |
| Project (Win32 message loop; no WinForms) | `app/app.csproj` |

## Milestones

Execution order is the table order (drop WinForms before service/tray work).

| # | Milestone | Status | Deliverables |
|---|-----------|--------|--------------|
| M1 | Drop WinForms | Done | Replace `NativeWindow` / `Application.Run` with Win32 HWND message loop; remove `UseWindowsForms`; record WinUI unpackaged as later UI direction |
| M2 | Dual-mode Windows service host | Planned | Non-interactive → true service; direct launch → console app as today; session-0 display events still drive SSAP/WoL |
| M3 | Official service install / autostart | Planned | Documented install/uninstall (script or CLI); service starts on boot; README updated |
| M4 | System tray when interactive | Planned | Tray icon while running interactively; enough UX to confirm “it’s running”; WinUI (unpackaged) for any new UI — not WinForms |

**Quick gate:** each implementation thread names **one milestone** (e.g. “M1 only”), not the whole P0 item.

## UI stack (agreed direction)

- **Prefer WinUI 3 (Windows App SDK), unpackaged** — modern UI without Store. Ship as normal desktop EXE (`WindowsPackageType=None`); self-contained Windows App SDK if we want folder/XCopy deploy. Take the App SDK dependency in **M4** (tray), not M1.
- **Do not grow WinForms.** M1 replaces today’s `NativeWindow` + `Application.Run` with a Win32 message loop so later milestones never build on WinForms.
- **Service path stays headless.** Session-0 service has no tray/UI; interactive launch owns the WinUI/tray surface (M4).

## Open questions

- Prefer `Microsoft.Extensions.Hosting` worker + `UseWindowsService` vs lighter custom service base — pick whatever fits the post-M1 Win32 message loop for `MonitorPowerWatcher`.
- Service account / key path: `%LocalAppData%` under SYSTEM vs a configured `KeyFile` / ProgramData location — decide in M2/M3 so pairing under the service account is explicit.
- M4 tray: WinUI `AppWindow` + tray helper vs thin Win32 `NotifyIcon` first and WinUI window later — default toward WinUI if we’re already taking the App SDK dependency for modern UI.
