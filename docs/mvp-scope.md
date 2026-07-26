# MVP scope — lgtv-display-sync

## Context

- **MVP** = what a usable **interim v1** means for this utility (launch bar), not execution order.
- **Roadmap / backlog:** [`docs/roadmap.md`](roadmap.md).
- **Why it exists:** root [`README.md`](../README.md) — VPN + LG webOS `wss:3001` TLS stall that breaks ColorControl’s wake path on a dedicated PC↔TV Ethernet segment.
- **Features:** `docs/features/` — SSOT for product capabilities.

## MVP bar (v1 target)

1. **Display ↔ TV sync (done):** Windows display off → TV standby or screen-off; display on → WoL + screen/power on, with short SSAP timeouts and spaced retries that ride through VPN-aggravated TLS stall waves.
2. **Config + pairing (done):** `config.json` (git-ignored) for IP / MAC / `OffAction`; first-run `--pair` or ColorControl key migrate into `%LocalAppData%\lgtv-display-sync\`.
3. **Dual-mode host:** when installed/started as a Windows service (non-interactive / session 0), run as a true service; when launched directly by the user, remain a normal console app (for now).
4. **Official service setup:** a documented, supported way to register the Windows service so it starts automatically (no ad-hoc `probe/ctx` scripts as the install story).
5. **Visible service status:** a `--tray` companion (user session) so the operator can see whether the Windows service is running and start/stop it / open logs — not a tray inside the service process.

## Non-goals (MVP)

- Not a ColorControl fork or full LG control suite (no picture modes, multi-device UI, etc.).
- No requirement to fix ColorControl itself — this is an interim companion until that path works again under VPN.
- No polished GUI beyond the `--tray` companion menu; no MSI/store packaging required for v1 (scripts / `sc.exe` / simple install helper is enough).
- No multi-TV management.
- Interactive console mode stays a console watcher; tray is opt-in via `--tray` (companion to the service, not WinForms).

## When to update this doc

- MVP bar or non-goals change.
- Something shipped that **changes the launch bar** (tighten wording to match reality).
- Upstream ColorControl (or equivalent) fully covers this VPN case and the interim role ends.
