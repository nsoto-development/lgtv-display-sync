# lgtv-display-sync — product roadmap

Ordered **backlog**: **priority tiers** (P0/P1/P2) group **numbered work items**.

**Work item kinds** (optional tags): `[feature]`, `[bugfix]`, `[chore]`, `[debt]`.

**Context:** interim daily driver until [ColorControl](https://github.com/Maassoft/ColorControl) handles the VPN + webOS TLS stall case reliably again. See the root [`README.md`](../README.md) for the problem statement and how the app works today.

---

## Status (where we are)

- **Shipped (prototype):** dual-mode host — Windows service when started by SCM (session 0), console when launched directly; watches `GUID_CONSOLE_DISPLAY_STATE`, drives LG webOS SSAP (short timeout + spaced retries), and wakes the TV via Wake-on-LAN. Validated end-to-end with the VPN connected (true power-off → WoL power-on). Official autostart: [`app/scripts/install-service.ps1`](../app/scripts/install-service.ps1) (copied next to the exe on build). `--tray` companion shows SCM status and Start/Stop from the user session.
- **Proven experimentally:** a process in session 0 / SYSTEM **does** receive display OFF→ON and can run the full SSAP + WoL path; `--watch-only` exists for logging without touching the TV.
- **Current focus:** P1 — first-run/ops notes and release artifact ([`docs/roadmap.md`](roadmap.md) P1).
- **Pre-launch / MVP:** see [`mvp-scope.md`](mvp-scope.md).

---

## Priority framework

**P-tiers are importance bands — not work units.** A large `[feature]` work item may take several milestones (M1, M2, …) in `docs/features/<topic>.md`.

| Tier | Meaning |
|------|--------|
| **P0** | Must have — product is not viable for interim daily use without this |
| **P1** | Strong improvements after P0 |
| **P2** | Nice-to-have / polish after P1 sticks |

---

## P0

1. [feature] **Service + tray packaging** — **Done.** Dual-mode host, official service install/autostart, and `--tray` companion. SSOT: [`docs/features/service-and-tray.md`](features/service-and-tray.md).

---

## P1

1. [chore] First-run / ops notes for non-dev use (config + pairing checklist beyond the README usage block).
2. [chore] Release artifact (e.g. self-contained publish layout) so the service install path does not depend on a Debug build tree.

---

## P2

1. [feature] Optional richer tray menu (open log folder, force on/off test actions) — only if interim use lasts long enough to need it.

---

## When to update

- **Ship a milestone** → update status in the feature doc; mark the **work item** **Done** only when all milestones for that item are complete (or the whole item was one milestone).
- **Reprioritize** → move work items between P-tiers or reorder the backlog.
- **ColorControl fixed upstream** → revisit whether this tool stays as a permanent companion or is retired; update MVP / non-goals accordingly.
