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

- Full WinForms or WPF control panel.
- Replacing ColorControl’s broader feature set.
- Changing SSAP / WoL retry policy (already validated).
- Forcing tray UI when running as a pure session-0 service (tray is for interactive / user-visible runs).

## Future hooks

- Richer tray actions (force on/off, status).
- Self-contained publish + single-folder install layout (roadmap P1).
- Retire or slim this feature if ColorControl absorbs the connect strategy.

## Code paths

| Area | Location |
|------|----------|
| Entry / watcher loop | `app/Program.cs` |
| Display events | `app/MonitorPowerWatcher.cs` |
| SSAP / WoL / keys | `app/Ssap.cs`, `app/Wol.cs`, `app/KeyStore.cs` |
| Icon | `app/appicon.ico` |
| Project (WinForms already referenced) | `app/app.csproj` |

## Milestones

| # | Milestone | Status | Deliverables |
|---|-----------|--------|--------------|
| M1 | Dual-mode Windows service host | Planned | Non-interactive → true service; direct launch → console app as today; session-0 display events still drive SSAP/WoL |
| M2 | Official service install / autostart | Planned | Documented install/uninstall (script or CLI); service starts on boot; README updated |
| M3 | System tray when interactive | Planned | Tray icon while running as console/user app; enough UX to confirm “it’s running” |

**Quick gate:** each implementation thread names **one milestone** (e.g. “M1 only”), not the whole P0 item.

## Open questions

- Prefer `Microsoft.Extensions.Hosting` worker + `UseWindowsService` vs lighter custom service base — pick whatever fits the existing WinForms message pump for `MonitorPowerWatcher`.
- Service account / key path: `%LocalAppData%` under SYSTEM vs a configured `KeyFile` / ProgramData location — decide in M1/M2 so pairing under the service account is explicit.
