# Project Status

*Last updated: 2026-11-07 · All 8 phases done, plus releases 1.0.x and photo sharing*

This page shows how far the project has come: what works today, what has been tested on real hardware,
and what's next. The full design is in [PLAN.md](PLAN.md), and the protocol reference is in [PROTOCOL.md](PROTOCOL.md).

## Progress

```
Phase 1  Protocol library            ██████████  done
Phase 2  Client + transports         ██████████  done
Phase 3  Storage + radio service     ██████████  done
Phase 4  REST API + auth             ██████████  done
Phase 5  Website (MVC BFF)           ██████████  done
Phase 6  Live chat UI                ██████████  done
Phase 7  Advanced radio features     ██████████  done
Phase 8  Polish + packaging          ██████████  done
```

| # | Phase | Status | Result |
|---|---|---|---|
| 1 | Protocol library | ✅ Done | Framing, all command/response/push codes, builders and parsers for the core commands |
| 2 | Client + transports | ✅ Done | WiFi (TCP) and USB serial, USB port auto-detect, `meshcore-cli` diagnostic tool |
| 3 | Storage + radio service | ✅ Done | SQLite storage, background connection with reconnect and takeover backoff, sending with ACK tracking, console chat |
| 4 | REST API + auth | ✅ Done | `/api/v1` for the device, contacts, channels and messages; first-run Admin setup; users and roles; JWT with instant revocation; lockout and rate limits; OpenAPI + Scalar |
| 5 | Website | ✅ Done | Responsive Bootstrap 5.3 site (bottom tabs on phones, sidebar on desktop) as an MVC proxy to the API: setup, sign-in, forced password change, dashboard, chats, contacts, channels, device, user admin |
| 6 | Live chat UI | ✅ Done | SignalR live updates, delivery ticks that update by themselves, per-user unread badges, toasts, sending without reload, catch-up after reconnect, PWA manifest + offline page, radio simulator |
| 7 | Advanced features | ✅ Done | Node settings (name, location, LoRa, TX power, tuning, preferences), contact cards (export/import), favourites, rename, heard-nodes list, repeater login/status/telemetry/trace, remote CLI in the chat, private key backup/restore |
| 8 | Polish | ✅ Done | HTTPS for phones (own local CA + site certificate, help page to install it), daily log files, Windows service / systemd, single-file builds for Windows, Linux and Raspberry Pi, setup guide |

**Since the 8 phases:** Windows releases built by GitHub Actions (no .NET needed), CI with test failures as annotations,
the Heltec V4 WiFi guide, tap a channel sender to message them privately (with a help page for senders who aren't contacts yet),
photo sharing (shrink, strip EXIF/GPS, upload to Litterbox/Catbox and send the short link), clickable links in messages,
battery % next to volts, and an hourly battery history with graphs for this radio and contacts marked "my device".

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
- **REST API** at `http://127.0.0.1:5081/api/v1`, with the interactive Scalar explorer at `/scalar` (Development).
  - The first Admin is created with `POST /auth/setup`. Admins add users, and each new user must change their temporary password.
  - Users can read and send. Admins also manage users, channels, contacts, the clock and reboot.
- **Status endpoint**: `GET http://127.0.0.1:5081/healthz` (anonymous, state only).
- **Run it for real**: a single-file build for Windows, Linux or Raspberry Pi, HTTPS for phones, daily log files in `data/logs`, and install scripts for a Windows service or systemd unit. See [SETUP.md](SETUP.md).
- **Website** at `http://<this computer>:5080` for phone and desktop browsers, with **live chat**:
  - New messages appear in the open chat without reloading, and delivery ticks go from ✓ to ✓✓ by themselves.
  - Unread badges are per user, and toasts appear for messages in other chats.
- **Node settings** (Admin): name, location, LoRa settings (with warnings), TX power, tuning, preferences, and private key backup and restore.
- **Contacts**: details page, favourite, rename, share nearby, export and import contact cards, and a "heard nodes" list to add from.
- **Repeaters and room servers**: log in (guest or admin), status (battery, noise, packet counts, uptime), telemetry, and route trace with SNR per hop. Admins can send remote CLI commands from the chat, and the replies appear live.
- **Radio simulator** (`MeshCore:Connection = "simulator"`): pretend contacts and channels that chat, ACK and reply, for demos and development without transmitting.
  - First visit: create the Admin. Admins add users under Settings → Users.
  - Pages: dashboard, chats (read and send), contacts, channels (add, edit, share key), device (adverts, clock, reboot), light/dark theme.

## Tested on real hardware

| Device | Firmware | Link | Result |
|---|---|---|---|
| Heltec V4.3 OLED | Companion WiFi v1.18.0 | TCP 192.168.1.212:5000 | ✅ Info, contacts, channels, messages, listen, host service, takeover backoff, REST API, website at phone and desktop sizes, Phase 7 read-only values (preferences, tuning, contact card, key export available), published single-file exe with HTTPS |
| Built-in simulator | — | in-process | ✅ Live chat end to end, plus every Phase 7 feature: settings, rename, repeater login, status, telemetry, trace, CLI in chat |
| LilyGo T-Echo | Companion **BLE** | USB COM11 | ❌ Not usable: the BLE build ignores USB serial (see PROTOCOL.md) |

Not tested over the air yet: sending a real message and receiving its ACK. The tests cover these with a scripted radio.

## Size of the code base

| Project | Purpose | Files | Lines |
|---|---|---:|---:|
| `src/MeshCore.Protocol` | Byte-level protocol, no I/O | 19 | 1,523 |
| `src/MeshCore.Client` | Transports, client, port discovery | 13 | 1,465 |
| `src/MeshCore.Data` | EF Core + SQLite storage | 3 | 660 |
| `src/MeshCore.Host` | The app: radio service, REST API, auth, website (C# + Razor), live hub, simulator, hosting, console chat | 65 | 6,452 |
| `tools/MeshCore.Cli` | Diagnostic CLI | 1 | 332 |
| `wwwroot` (own CSS/JS) | Layout, theme, live updates, chat, service worker | 6 | 647 |
| **Application code** | | **107** | **11,079** |
| `tests/*` (3 projects) | Unit and integration tests | 19 | 3,279 |
| `docs/` + README | Plan, protocol reference, status | 4 | ~500 |

C# and Razor lines exclude generated EF migrations and the Bootstrap library files.

**204 automated tests**, all passing: 78 protocol, 42 client, and 84 host tests (API, website, live hub, unread, simulator, radio features, HTTPS certificates including a real TLS handshake).
None of them need a radio: they use a scripted fake radio, a local TCP server, in-memory SQLite, and the whole app hosted in memory.

## Known gaps and decisions

- **One WiFi client at a time.** This is how the firmware works. The phone app and this app can't both use the radio over WiFi.
- **BLE isn't supported.** USB or WiFi companion firmware is needed. A BLE transport could be added later behind the same `ITransport` interface.
- **Node name is still the default** (`010A2CFB`). It's added in front of every channel message, so it limits channel text to 150 bytes.
  You can now rename it under Device → Node settings.
- **HTTPS uses the app's own certificate authority.** Phones install it once from `/Account/Certificate`, or accept a browser warning. Installing it on a real phone hasn't been tried yet; the chain was verified with a strict TLS client.
- **Installing as an app** (service worker, offline page) needs the HTTPS address on phones. Not yet verified on a real phone.
- **GitHub Actions release** (`.github/workflows/release.yml`) hasn't run on GitHub yet. The same packaging script was run locally, and its zip worked with no .NET available.
- **The service install scripts haven't been run here.** They need Administrator rights (or sudo) and change the system. The app's service support itself is in place (`UseWindowsService`, `UseSystemd`, app-folder paths).
- **Remote repeater features haven't been tried over the air yet.** Your radio knows 6 repeaters and a room server, all with unknown routes. Status, telemetry and login send packets, so they're left for you to try. Trace needs a learned route first.
- **Live connections stay open after a user is disabled** until they reload. API calls are refused immediately; the open SignalR connection only carries notifications.

## History

| Commit | Phase |
|---|---|
| `5ec733e` | Phase 1: protocol library |
| `9795be4` | Phase 2: client with WiFi (TCP) and USB serial transports |
| `90282e5` | Phase 3: SQLite storage, radio connection service and console chat |
| `1b9915d` | Phase 4: REST API, authentication and user management |
| `1219178` | Phase 5: responsive website (MVC BFF) |
| `a0af635` | Phase 6: live chat, unread markers, PWA, simulator |
| `9e90531` | Phase 7: node settings, contacts, repeaters, identity |
| `5f76a00` | Phase 8: HTTPS, logging, services, single-file builds, setup guide |
