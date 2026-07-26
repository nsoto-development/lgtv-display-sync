# lgtv-display-sync

A small Windows utility that lets an **LG webOS TV be used as a monitor**: it syncs the
TV's power/screen state to the Windows display state, and wakes the TV over the network.

- Windows turns the display **off** (idle / lock / DPMS) → put the **TV to standby** (or just screen‑off).
- Windows turns the display **on** (mouse / key) → **Wake‑on‑LAN** the TV and turn its screen on.

It runs as an ordinary **user‑session app** (no Windows service, no elevation).

---

## Why this exists (the real problem)

This started as a prototype to fix a real, specific setup:

- An **LG G5** is the machine's **only display**, driven over a **dedicated, isolated Ethernet
  segment** (`192.168.100.0/24`) — separate from the PC's internet connection (Wi‑Fi). A near
  air‑gapped topology: the TV link carries no internet, just PC↔TV control.
- [ColorControl](https://github.com/Maassoft/ColorControl) handled "display sleeps → TV off,
  wake → TV on" and worked fine.
- After **ProtonVPN** (WireGuard) was installed, **waking the TV stopped working**: lock the PC,
  the display sleeps, move the mouse — and the TV screen never comes back. **Only with the VPN
  connected.** VPN off always worked, on any IP scheme.

The tell‑tale detail: the machine could still `ping` and open a TCP socket to the TV with the VPN
on, so it *looked* reachable — but the control session never established, so no "screen on" was
ever sent.

## What the investigation found

Using packet captures and a purpose‑built, instrumented SSAP probe (see [`probe/`](probe/)) that
times each connect phase separately, the failure was localized precisely:

- The webOS control channel is a **secure WebSocket** (`wss://<tv>:3001`). With the VPN on, the
  handshake **intermittently stalls at the TLS step**: TCP connects instantly, the client sends its
  `ClientHello`, the TV **TCP‑ACKs it but never returns a `ServerHello`** for the whole timeout,
  then RSTs the connection ~15 s later.
- It's **intermittent and clustered** — long stretches connect in ~130 ms, then a "bad period"
  produces a run of stalls. It's aggravated by **connection churn** (each stalled socket lingers on
  the TV for ~15 s, and webOS has a small connection budget).
- It is **not** the IP scheme, **not** LAN‑vs‑tunnel routing (the control channel stays on Ethernet
  with the correct source), and **not** simply source‑binding — a minimal fresh connect reproduces
  and clears it.

**Why ColorControl fails and this doesn't:** ColorControl uses a **5‑second connect timeout with a
burst of retries** at the wake moment, which stalls and storms right through a bad period. This tool
instead does a **fresh connect with a short (~2.5 s) timeout and gentle, spaced retries** — riding
over the stall waves — and keeps at most one warm connection. Validated end‑to‑end with the VPN
connected: real display sleep/wake driving **true power‑off → WoL power‑on** on its own.

## How it works

| Piece | File | What it does |
|---|---|---|
| Monitor watcher | [`app/MonitorPowerWatcher.cs`](app/MonitorPowerWatcher.cs) | Registers `GUID_CONSOLE_DISPLAY_STATE`; raises display off/on |
| SSAP client | [`app/Ssap.cs`](app/Ssap.cs) | Phased `wss:3001` connect (short timeout), screen/power commands |
| Wake‑on‑LAN | [`app/Wol.cs`](app/Wol.cs) | Magic packet to the TV subnet's directed broadcast, from the matching NIC |
| Key store | [`app/KeyStore.cs`](app/KeyStore.cs) | Own client‑key; first‑run pairing; one‑time migrate from ColorControl |
| Controller | [`app/Program.cs`](app/Program.cs) | Wires events → actions with cancel‑previous + gentle retry |

webOS SSAP used: register/pairing handshake, `.../power/turnOffScreen` · `turnOnScreen`,
`system/turnOff` (standby), plus WoL for power‑on.

## Usage

```bash
# build
dotnet build app -c Debug

# one‑shot tests (no display cycle needed)
app/bin/Debug/net9.0-windows/lgtv-display-sync.exe --test on        # WoL + screen on
app/bin/Debug/net9.0-windows/lgtv-display-sync.exe --test off       # screen off
app/bin/Debug/net9.0-windows/lgtv-display-sync.exe --test poweroff  # TV -> standby
app/bin/Debug/net9.0-windows/lgtv-display-sync.exe --test poweron   # WoL + reconnect

# first‑run pairing (no ColorControl): accept the prompt on the TV
app/bin/Debug/net9.0-windows/lgtv-display-sync.exe --pair

# run it (reacts to display sleep/wake); Ctrl+C to stop
app/bin/Debug/net9.0-windows/lgtv-display-sync.exe
```

**Config:** copy `app/config.json.example` → `app/config.json` and set your TV's `Ip`, `Mac`, and
`OffAction` (`"power"` for standby, `"screen"` for panel‑off). `config.json` is git‑ignored.

**Pairing:** on first run with no key, the tool shows a prompt on the TV, waits, and saves the
returned client‑key to `%LocalAppData%\lgtv-display-sync\`. If ColorControl already paired this TV,
its key is reused automatically once.

## Status

Working **prototype**, validated end‑to‑end under ProtonVPN. Not yet packaged for autostart.

Known open question: whether an equivalent **session‑0 Windows service** receives display
**transition** events (SSAP + WoL and the initial registration event do work from SYSTEM/session 0;
real off→on transitions are untested). A cleaned‑up version of these findings may be worth
contributing back to ColorControl as a fix.

## Repo layout

```
app/     the utility
probe/   instrumented SSAP connect probe used to diagnose the VPN stall
```
