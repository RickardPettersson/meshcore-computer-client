# Project Status

*Last updated: 2026-11-04 · Phases 1–3 of 8 done*

This page shows how far the project has come: what works today, what has been tested on real hardware,
and what's next. The full design is in [PLAN.md](PLAN.md), and the protocol reference is in [PROTOCOL.md](PROTOCOL.md).

## Progress

```
Phase 1  Protocol library            ██████████  done
Phase 2  Client + transports         ██████████  done
Phase 3  Storage + radio service     ██████████  done
Phase 4  REST API + auth             ░░░░░░░░░░  next
Phase 5  Website (MVC BFF)           ░░░░░░░░░░
Phase 6  Live chat UI                ░░░░░░░░░░
Phase 7  Advanced radio features     ░░░░░░░░░░
Phase 8  Polish + packaging          ░░░░░░░░░░
```

| # | Phase | Status | Result |
|---|---|---|---|
| 1 | Protocol library | ✅ Done | Framing, all command/response/push codes, builders and parsers for the core commands |
| 2 | Client + transports | ✅ Done | WiFi (TCP) and USB serial, USB port auto-detect, `meshcore-cli` diagnostic tool |
| 3 | Storage + radio service | ✅ Done | SQLite storage, background connection with reconnect and takeover backoff, sending with ACK tracking, console chat |
| 4 | REST API + auth | ⏳ Next | `/api/v1` for the radio, contacts, channels and messages; first-run setup; users and roles; JWT; OpenAPI |
| 5 | Website | Planned | Responsive MVC site as a proxy to the API; setup, login and user admin pages |
| 6 | Live chat UI | Planned | Chat in phone and desktop browsers, SignalR updates, delivery ticks, unread markers per user, installable as an app |
| 7 | Advanced features | Planned | Rename node, radio settings, add/remove contacts, repeater login/status/telemetry, key export |
| 8 | Polish | Planned | HTTPS for LAN use, file logging, single-file build, setup guide |

## What works today

- **Connect to a radio** over WiFi (`192.168.1.212:5000`) or USB (`COM11`, or auto-detect).
  - Reconnects by itself when the link drops.
  - If another app takes the WiFi radio over, it waits 2 minutes instead of fighting for the connection.
- **Startup with the radio**: reads node and device info, sets the radio's clock, and syncs contacts and channels.
- **Receive messages**, direct and channel. Each one is stored in SQLite before the next is fetched.
  Signal strength and hop count are recorded.
- **Send messages**, direct and channel, with the length checked up front.
  - Direct messages are marked Delivered when the recipient's ACK arrives, or NotConfirmed when it doesn't come in time.
- **Console chat** in the host app: `/dm`, `/ch`, `/history`, `/contacts`, `/channels`, `/status`, `/sync`.
- **Diagnostic CLI**: `meshcore-cli ports | info | contacts | channels | messages | listen | send | send-channel | advert`.
- **Status endpoint**: `GET http://127.0.0.1:5081/healthz`.

## Tested on real hardware

| Device | Firmware | Link | Result |
|---|---|---|---|
| Heltec V4.3 OLED | Companion WiFi v1.18.0 | TCP 192.168.1.212:5000 | ✅ Info, contacts, channels, messages, listen, host service, takeover backoff |
| LilyGo T-Echo | Companion **BLE** | USB COM11 | ❌ Not usable: the BLE build ignores USB serial (see PROTOCOL.md) |

Not tested over the air yet: sending a real message and receiving its ACK. The tests cover these with a scripted radio.

## Size of the code base

| Project | Purpose | Files | Lines |
|---|---|---:|---:|
| `src/MeshCore.Protocol` | Byte-level protocol, no I/O | 16 | 1,097 |
| `src/MeshCore.Client` | Transports, client, port discovery | 12 | 1,289 |
| `src/MeshCore.Data` | EF Core + SQLite storage | 3 | 528 |
| `src/MeshCore.Host` | The app: radio service, console chat | 6 | 756 |
| `tools/MeshCore.Cli` | Diagnostic CLI | 1 | 332 |
| **Application code** | | **38** | **4,002** |
| `tests/*` (3 projects) | Unit and integration tests | 11 | 1,642 |
| `docs/` + README | Plan, protocol reference, status | 4 | ~500 |

C# lines exclude generated EF migrations.

**107 automated tests**, all passing: 55 protocol, 35 client, and 17 host tests.
None of them need a radio: they use a scripted fake radio, a local TCP server, and an in-memory SQLite database.

## Known gaps and decisions

- **One WiFi client at a time.** This is how the firmware works. The phone app and this app can't both use the radio over WiFi.
- **BLE isn't supported.** USB or WiFi companion firmware is needed. A BLE transport could be added later behind the same `ITransport` interface.
- **Node name is still the default** (`010A2CFB`). It's added in front of every channel message, so it limits channel text to 150 bytes.
  Renaming is in Phase 7.
- **No authentication yet.** The status endpoint only listens on `127.0.0.1`. Login and users come in Phase 4.

## History

| Commit | Phase |
|---|---|
| `5ec733e` | Phase 1: protocol library |
| `9795be4` | Phase 2: client with WiFi (TCP) and USB serial transports |
| `90282e5` | Phase 3: SQLite storage, radio connection service and console chat |
