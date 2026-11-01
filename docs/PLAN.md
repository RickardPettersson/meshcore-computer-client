# MeshCore Computer Client — Plan

A .NET 10 console app that talks to any MeshCore **companion radio** firmware over USB serial.
It hosts a REST API and a Bootstrap website from the same process.

## 1. What we're talking to

> Sources, firmware commit and full byte layouts: see [PROTOCOL.md](PROTOCOL.md).

All MeshCore companion builds use the same binary protocol. That covers T-Echo, Heltec, RAK4631, T-Deck,
Xiao and others. Only the transport differs (USB serial, BLE or WiFi/TCP), so one client works with every
unit. Things that differ per device are read at runtime from `DEVICE_INFO`: max contacts, max channels,
firmware version and model.

### USB serial framing (from `ArduinoSerialInterface.cpp`)

| Direction | Frame |
|---|---|
| App → radio | `'<'` (0x3C) · len LSB · len MSB · payload |
| Radio → app | `'>'` (0x3E) · len LSB · len MSB · payload |

- Baud rate is 115200, 8N1. Assert DTR, because some ESP32 boards need it.
- The first payload byte is the command or response code. Integers are little-endian and strings are UTF-8.
- There are **no request IDs**. Send one command at a time and wait for its response, with a timeout of about 5 s.
  Responses use codes 0x00–0x7F. Unsolicited **push** frames (0x80+) can arrive at any moment, including
  between a command and its response.

### Startup handshake

1. `CMD_APP_START` (0x01) with app version 3 and app name → `SELF_INFO` (0x05)
2. `CMD_DEVICE_QUERY` (0x16, ver 3) → `DEVICE_INFO` (0x0D)
3. `CMD_SET_DEVICE_TIME` (0x06), to sync the clock
4. `CMD_GET_CONTACTS` (0x04) → `CONTACTS_START`, then N × `CONTACT`, then `END_OF_CONTACTS`
5. `CMD_GET_CHANNEL` (0x1F) for each index `0..max_channels-1` → `CHANNEL_INFO` (0x12)
6. Drain the message queue with `CMD_SYNC_NEXT_MESSAGE` (0x0A) until `NO_MORE_MESSAGES`
7. Then listen. On `PUSH_MSG_WAITING` (0x83), drain again.

### Feature coverage (commands 1–66, pushes 0x80–0x90)

| Area | Commands |
|---|---|
| Identity / device | self info, device info, battery and storage, time get/set, reboot, factory reset, stats |
| Radio | radio params, TX power, tuning params, other params, custom vars |
| Advert | send advert (zero-hop/flood), set name, set lat/lon, advert path |
| Contacts | list, get by key, add/update, remove, reset path, share, export/import, auto-add config |
| Messages | send direct text (0x02), send channel text (0x03), sync queue, ACK/confirm tracking |
| Channels | get/set channel (name + 16-byte secret), channel datagrams |
| Repeaters/rooms | login/logout, status request, telemetry request, trace path, path discovery, binary req, CLI command |
| Security | export/import private key, device PIN, signing |

## 2. Solution layout

```
meshcore-computer-client/
├─ MeshCore.slnx
├─ src/
│  ├─ MeshCore.Protocol/      # pure library: framing, codes, encoders/decoders, models. No I/O.
│  ├─ MeshCore.Client/        # transports (ITransport: Serial now; BLE/TCP later), MeshCoreClient,
│  │                          # command queue, push event stream, reconnect, port auto-detect
│  ├─ MeshCore.Data/          # EF Core + SQLite: messages, contacts cache, channels, ACK state
│  └─ MeshCore.Host/          # the console app (Microsoft.NET.Sdk.Web)
│     ├─ Api/                 # REST API controllers  → /api/v1/*
│     ├─ Web/                 # MVC controllers, views, BFF proxy, SignalR hub
│     └─ wwwroot/             # Bootstrap 5.3, Bootstrap Icons, site.js/css
└─ tests/
   ├─ MeshCore.Protocol.Tests/   # golden byte-frame tests
   └─ MeshCore.Client.Tests/     # fake-transport tests (no hardware needed)
```

Why split it: the protocol and client libraries can be tested without a radio, could ship as a NuGet
package, and don't depend on ASP.NET.

## 3. Device layer (`MeshCore.Client`)

- **`SerialTransport`** uses `System.IO.Ports`, a background read loop and a frame parser state machine.
  It resyncs on the `'>'` start byte if a frame is corrupted.
- **Port auto-detect**: list COM ports and rank them by USB VID/PID:
  - nRF52 / T-Echo / RAK: Adafruit `239A`, Nordic `1915`
  - ESP32: CP210x `10C4`, CH340 `1A86`, native `303A`
  - It then **probes** each port with `APP_START` and keeps the first that answers `SELF_INFO`.
  - Setting `MeshCore:Serial:Port` (e.g. `COM5`) overrides detection.
- **`MeshCoreClient`**:
  - Uses a `SemaphoreSlim(1)` command lane, sending one request and awaiting the matching response code with a timeout.
  - Exposes pushes as `IAsyncEnumerable` / `Channel<T>` events: `MessageReceived`, `AdvertReceived`, `SendConfirmed`, `PathUpdated`…
  - Uses typed methods (`GetContactsAsync`, `SendTextAsync`, `GetChannelAsync`, …), with one encoder/decoder per command.
- **`DeviceHostedService`** (`BackgroundService`) connects, runs the handshake and drains messages to the DB.
  It reconnects with backoff when the cable is unplugged and publishes connection state.
- **Persistence is required.** `SYNC_NEXT_MESSAGE` *removes* messages from the radio's queue, so the app
  must store them (SQLite) or they are lost. The contacts and channels caches are refreshed on connect and on push.

## 4. REST API (`/api/v1`, ASP.NET Core controllers + OpenAPI)

| Resource | Endpoints |
|---|---|
| `device` | `GET /device`, `GET /device/battery`, `GET /device/status` (connection), `POST /device/time`, `POST /device/reboot`, `POST /device/advert` |
| `device/radio` | `GET/PUT` radio params, tx power, tuning |
| `contacts` | `GET /contacts`, `GET /contacts/{key}`, `PUT`, `DELETE`, `POST /contacts/{key}/reset-path`, `/share`, `/export`, `POST /contacts/import` |
| `channels` | `GET /channels`, `GET/PUT/DELETE /channels/{idx}` |
| `messages` | `GET /messages?contact=&channel=&before=&take=` (paged from DB), `POST /messages/direct`, `POST /messages/channel` |
| `repeaters` | `POST /{key}/login`, `/logout`, `/status`, `/telemetry`, `/trace`, `/cli` |
| `auth` | `GET /auth/setup-required`, `POST /auth/setup` (first run only), `POST /auth/token`, `POST /auth/change-password` |
| `users` (Admin only) | `GET /users`, `POST /users`, `PUT /users/{id}` (role, enabled), `POST /users/{id}/reset-password`, `DELETE /users/{id}` |
| real-time | SignalR hub `/hubs/mesh`: new message, ACK, advert, connection state |

- Every endpoint except setup and token needs a **JWT bearer** token. The token carries claims for user id, username and role.
  It is checked with policies: `RequireUser` for reading and sending, `RequireAdmin` for user management and device or radio settings.
- **OpenAPI** uses .NET 10's built-in `Microsoft.AspNetCore.OpenApi`, with the Scalar UI enabled in Development.
- Errors are returned as **ProblemDetails**. Device `ERR` codes map to HTTP status: not found → 404, table full → 409, unsupported → 501, device offline → 503.

## 5. Website — ASP.NET MVC as a BFF (Backend-for-Frontend)

```
Browser ──cookie──► MVC (Razor views + /bff/*) ──JWT──► REST API ──► MeshCoreClient ──USB──► radio
```

- The browser **never sees the JWT**. It holds only an HttpOnly, Secure, SameSite=Strict auth cookie
  plus an antiforgery token.
- On login, MVC calls `POST /api/v1/auth/token`. It stores the JWT inside the encrypted auth cookie
  (as an ASP.NET auth ticket) and adds it to API calls from a `DelegatingHandler` on a typed `HttpClient`.
- Pages (`Dashboard`, `Messages`, `Contacts`, `Channels`, `Device`, `Settings`) are rendered server-side by MVC
  using the typed API client.
- Interactive JS (sending a message, live updates) calls the **MVC's own `/bff/api/*`**. A **YARP** reverse-proxy
  route there swaps the cookie for the bearer token. SignalR is proxied the same way.
- The API and the web UI are **separate Kestrel endpoints in one process**: the API on `:5081` (localhost only by
  default) and the web UI on `:5080`. The API can still be opened up to other clients, such as a mobile app or Home Assistant.

### Users and login (multi-user from day one)

Every login needs a **username and a password**. Passwords are stored only as a **one-way hash** made with
ASP.NET Identity `PasswordHasher`, which uses PBKDF2-HMAC-SHA512 with a random salt per user and 100k iterations.
The hash can't be turned back into the password.

**Roles**

| Role | Can do |
|---|---|
| `Admin` | Everything a `User` can do, plus manage users, device and radio settings, contacts and channel edits, keys, reboot and factory reset |
| `User` | Read and send messages, view contacts, channels and device status, change own password |

**First run**

1. A middleware checks `GET /api/v1/auth/setup-required`, which is true while no users exist. If true, every page redirects to `/Setup`.
2. `/Setup` asks for a username and password (entered twice). MVC posts them to `POST /api/v1/auth/setup`.
   This creates the **first user as `Admin`**. The endpoint works **only while the user list is empty**, and a lock stops two setups racing each other.
3. There is **no self-registration**. Only an Admin can add more users, under **Settings → Users**.
   - Adding a user means choosing a username, a temporary password and a role.
   - New users and password resets get a `MustChangePassword` flag, so the person picks their own password at first login.
   - An Admin can also disable users, reset passwords, change roles and delete users.
   - The **last Admin can't be demoted, disabled or deleted**, so you can't lock yourself out.
4. After setup, `/Account/Login` handles login. There is lockout after 5 failed attempts, logout, and a change-password page under Settings.
   Every login and user change is logged.

**Storage** goes in `appsettings.auth.json`. The app writes this file itself at runtime and layers it on top of `appsettings.json`.
Writes are atomic (write to a temp file, then rename) and guarded by a lock.

```json
{
  "Auth": {
    "JwtKeyProtected": "CfDJ8...",
    "Users": [
      { "Id": "6f1c…", "Username": "admin", "PasswordHash": "AQAAAAIAAYag…", "Role": "Admin",
        "Enabled": true, "MustChangePassword": false, "SecurityStamp": "…", "CreatedUtc": "2026-11-01T18:00:00Z" }
    ]
  }
}
```

- Access to the file goes through an `IUserStore` interface. Users could move into the SQLite DB later without changing anything else.
- The JWT signing key is generated on first run. It is stored encrypted with the **Data Protection API**,
  with keys kept in `./keys` and protected with DPAPI on Windows.
- `SecurityStamp` changes on a password reset, disable or role change. Existing tokens and cookies for that user are then rejected on the next request.
- Data on the radio (contacts, channels, messages) is **shared** by all users, since there is one radio.
  Each user has their own **read/unread state**, stored in SQLite. Sent messages record which user sent them.

## 6. Frontend — responsive, mobile-first

- **Bootstrap 5.3.x** (latest) plus Bootstrap Icons, served locally via LibMan from `wwwroot/lib`. No CDN, so it works offline on a field laptop.
- Plain ES modules (`site.js`, `messages.js`) and the SignalR JS client. No SPA framework and no build step.
- Dark/light mode follows the system setting by default, using Bootstrap's `data-bs-theme`, with a toggle to override it.

The interface is designed for the phone first and scaled up to desktop, so it works the same in mobile and desktop browsers.

| | Phone (< 768px) | Desktop (≥ 992px) |
|---|---|---|
| Navigation | Fixed **bottom tab bar**: Chats, Contacts, Channels, Device, More | Top navbar plus a sidebar |
| Messages | One pane at a time: conversation list → tap → full-screen thread with a back button | Two panes: conversation list on the left, thread on the right |
| Lists (contacts, users) | Cards / list-group with actions in an offcanvas or dropdown | Tables with inline actions |
| Forms / dialogs | Full-screen modals (`modal-fullscreen-sm-down`) | Centred modals |

- The chat composer sticks to the bottom of the screen. It shows a live byte counter, because MeshCore text is limited to about 160 bytes.
  It stays usable with the on-screen keyboard open (`dvh` units, safe-area insets for iPhone notches).
- Touch targets are at least 44px. Text inputs use 16px fonts so iOS doesn't zoom in.
  The page width follows the viewport (`viewport-fit=cover`) and never scrolls sideways.
- Unread badges and delivery ticks (from `SEND_CONFIRMED`) are shown. A toast appears for new messages when you're viewing a different conversation.
- The app has a **PWA manifest** and icons, so it can be added to a phone's home screen and opens like an app.
- It is tested at 375px (phone), 768px (tablet) and 1280px+ (desktop) in Chrome, Safari/iOS and Firefox.

## 7. Configuration (`appsettings.json`)

```json
{
  "MeshCore": { "Serial": { "Port": "auto", "BaudRate": 115200 }, "AppName": "MeshCoreComputerClient" },
  "Endpoints": { "Web": "http://0.0.0.0:5080", "Api": "http://127.0.0.1:5081" },
  "ConnectionStrings": { "Mesh": "Data Source=meshcore.db" },
  "Auth": { "TokenLifetimeMinutes": 60, "LockoutAttempts": 5, "LockoutMinutes": 15 }
}
```

Users and the encrypted JWT key are written by the app to `appsettings.auth.json` (see §5). That file is listed in `.gitignore`.

Command-line overrides work out of the box, e.g. `MeshCore.Host.exe --MeshCore:Serial:Port=COM7`.

## 8. Build phases

| # | Phase | Done when |
|---|---|---|
| 1 | Solution scaffold, protocol library (framing + codes + core codecs), unit tests | Golden-frame tests pass |
| 2 | Serial transport, auto-detect, `MeshCoreClient`, handshake; console prints self info, contacts and channels | Works against the T-Echo |
| 3 | SQLite persistence, message sync service, send direct/channel messages | Can chat from the console log |
| 4 | REST API + OpenAPI + JWT + roles/policies + first-run setup + user management endpoints | Scalar UI exercises every endpoint; a User gets 403 on admin endpoints |
| 5 | MVC BFF: setup/login/change-password, responsive layout (bottom tabs / sidebar), Dashboard, Contacts, Channels, Device, Settings → Users | Works on a phone and a desktop; admin can add a user who then logs in |
| 6 | Responsive Messages chat UI + SignalR live updates + ACK ticks + per-user unread + PWA manifest | Real-time chat in phone and desktop browsers |
| 7 | Advanced: repeater login/status/telemetry/trace/CLI, radio settings, key import/export | Full feature coverage |
| 8 | Polish: reconnect UX, logging (Serilog → console/file), `dotnet publish` single-file exe, README | One exe to run |

## 9. Decisions

| Decision | Choice |
|---|---|
| Login | Username + password |
| Password storage | One-way PBKDF2 hash (never encrypted or reversible) |
| Users | Multi-user from day one. The first user becomes Admin, and only an Admin can add users. |
| Credential file | `appsettings.auth.json`, written by the app and layered over `appsettings.json` |
| Web UI | Responsive and mobile-first (phone + desktop), Bootstrap 5.3, installable as a PWA |
| Message storage | SQLite |
| API exposure | Localhost-only by default. The web UI is reachable on the LAN, so phones can use it. |

**Note:** to reach the site from a phone on your network, it should run over **HTTPS**, because login cookies are `Secure`.
Phase 8 adds a self-signed certificate on first run, plus an option to use your own.
Until then, plain HTTP on the LAN works with relaxed cookie settings in Development.
