# MeshCore Computer Client — Plan

A .NET 10 console app that talks to any MeshCore **companion radio**: over **WiFi (TCP)** with the companion
WiFi firmware (the primary setup, a Heltec V4 at `192.168.1.212:5000`), or over **USB serial** with the companion USB firmware.
It hosts a REST API and a Bootstrap website from the same process.

## 1. What we're talking to

> Sources, firmware commit and full byte layouts: see [PROTOCOL.md](PROTOCOL.md).

All MeshCore companion builds use the same binary protocol. That covers T-Echo, Heltec, RAK4631, T-Deck,
Xiao and others. Only the transport differs (USB serial, BLE or WiFi/TCP), so one client works with every
unit. Things that differ per device are read at runtime from `DEVICE_INFO`: max contacts, max channels,
firmware version and model.

### Framing (USB serial and WiFi/TCP share it; see `ArduinoSerialInterface.cpp`, `SerialWifiInterface.cpp`)

| Direction | Frame |
|---|---|
| App → radio | `'<'` (0x3C) · len LSB · len MSB · payload |
| Radio → app | `'>'` (0x3E) · len LSB · len MSB · payload |

- WiFi: a TCP connection to the radio, default port 5000. The radio serves **one client at a time**.
- Serial: 115200 baud, 8N1, with DTR asserted (nRF52 boards need it).
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
│  ├─ MeshCore.Client/        # transports (ITransport: TCP + Serial; BLE later), MeshCoreClient,
│  │                          # command queue, push event stream, reconnect, port auto-detect
│  ├─ MeshCore.Data/          # EF Core + SQLite: messages, contacts cache, channels, ACK state
│  └─ MeshCore.Host/          # the console app (Microsoft.NET.Sdk.Web)
│     ├─ Api/                 # REST API controllers  → /api/v1/*
│     ├─ Web/                 # MVC controllers, views, BFF proxy, SignalR hub
│     └─ wwwroot/             # Bootstrap 5.3, Bootstrap Icons, site.js/css
├─ tools/
│  └─ MeshCore.Cli/           # meshcore-cli: diagnostic console (ports, info, contacts, channels, listen, send)
└─ tests/
   ├─ MeshCore.Protocol.Tests/   # golden byte-frame tests
   └─ MeshCore.Client.Tests/     # fake-transport tests (no hardware needed)
```

Why split it: the protocol and client libraries can be tested without a radio, could ship as a NuGet
package, and don't depend on ASP.NET.

## 3. Device layer (`MeshCore.Client`)

- **`StreamTransport`** is the shared base: a background read loop, `FrameDecoder` (resyncs on `'>'`), and a write lock.
  - **`TcpTransport`** connects over WiFi. It uses a connect timeout, TCP keep-alive, and treats the socket closing as "link lost".
  - **`SerialTransport`** uses `System.IO.Ports`.
- **`MeshCoreEndpoint`** parses one address setting: `192.168.1.212[:5000]`, `tcp://host:port`, `COM11`, `/dev/ttyACM0`, or `auto`.
  `auto` probes USB serial ports.
- **Port auto-detect** (USB only): list COM ports and rank them by USB VID/PID:
  - nRF52 / T-Echo / RAK: Adafruit `239A`, Nordic `1915`
  - ESP32: CP210x `10C4`, CH340 `1A86`, native `303A`
  - It then **probes** each port with `APP_START` and keeps the first that answers `SELF_INFO`.
  - Setting `MeshCore:Connection` to an address skips detection.
- **`MeshCoreClient`**:
  - Uses a `SemaphoreSlim(1)` command lane, sending one request and awaiting the matching response code with a timeout.
  - Exposes pushes as `IAsyncEnumerable` / `Channel<T>` events: `MessageReceived`, `AdvertReceived`, `SendConfirmed`, `PathUpdated`…
  - Uses typed methods (`GetContactsAsync`, `SendTextAsync`, `GetChannelAsync`, …), with one encoder/decoder per command.
- **`RadioConnectionService`** (`BackgroundService`) connects, runs the handshake, and hands the client to **`MeshRadio`**.
  `MeshRadio` is the single owner of the radio: it syncs contacts, channels and messages into SQLite, sends messages, and raises events for the UI.
  It reconnects with backoff when the cable is unplugged or WiFi drops, and publishes connection state.
  When another client takes over the radio over WiFi, it waits `TakeoverBackoff` (default 2 min) instead of fighting
  for the connection, and shows "radio in use elsewhere" in the UI.
- **Persistence is required.** `SYNC_NEXT_MESSAGE` *removes* messages from the radio's queue, so the app
  must store them (SQLite) or they are lost. The contacts and channels caches are refreshed on connect and on push.

## 4. REST API (`/api/v1`, ASP.NET Core controllers + OpenAPI)

**Built in Phase 4** (🔒 = Admin only):

| Resource | Endpoints |
|---|---|
| `auth` | `GET /auth/setup-required`, `POST /auth/setup` (first run only), `POST /auth/token`, `GET /auth/me`, `POST /auth/change-password` |
| `users` 🔒 | `GET /users`, `GET /users/{id}`, `POST /users`, `PUT /users/{id}` (role, enabled), `POST /users/{id}/reset-password`, `DELETE /users/{id}` |
| `device` | `GET /device` (connection + node + hardware), `GET /device/battery`, `GET /device/time`, `POST /device/time` 🔒, `POST /device/advert`, `POST /device/reboot` 🔒 |
| `contacts` | `GET /contacts[?includeRemoved]`, `GET /contacts/{key}`, `POST /contacts/refresh`, `POST /contacts/{key}/reset-path` 🔒, `DELETE /contacts/{key}` 🔒 |
| `channels` | `GET /channels`, `GET /channels/{idx}`, `GET /channels/{idx}/secret` 🔒, `PUT /channels/{idx}` 🔒 (random key if none given), `DELETE /channels/{idx}` 🔒 |
| `messages` | `GET /messages?contact=&channel=&before=&after=&take=` (paged from the DB; `after` catches up oldest-first), `POST /messages/direct`, `POST /messages/channel`, `POST /messages/sync` |
| `conversations` | `GET /conversations` (chat list with the signed-in user's unread counts), `POST /conversations/read` (per-user read marker) |
| real-time | SignalR hub: `/api/v1/hub` on the API port (bearer token, or `?access_token=` for WebSockets) and `/hubs/mesh` on the website port (cookie). Events: `message` (new or changed status), `radio`, `contacts`, `channels` |

**Built in Phase 7** (🔒 = Admin only):

| Resource | Endpoints |
|---|---|
| `device` settings | `PUT /device/name` 🔒, `PUT /device/location` 🔒, `PUT /device/radio` 🔒 (freq, BW, SF, CR), `PUT /device/tx-power` 🔒, `GET`/`PUT /device/tuning` (PUT 🔒), `PUT /device/preferences` 🔒, `GET /device/contact-card`, `GET`/`PUT /device/private-key` 🔒 (501 when the firmware has it disabled) |
| `contacts` | `GET /contacts/discovered`, `POST /contacts/discovered/{key}` 🔒, `POST /contacts/import` 🔒, `PUT /contacts/{key}/favourite` 🔒, `PUT /contacts/{key}/name` 🔒, `POST /contacts/{key}/share`, `GET /contacts/{key}/export` |
| repeaters / rooms | `POST /contacts/{key}/login`, `/logout`, `/status`, `/telemetry`, `/trace`, and `/cli` 🔒. Each waits for the remote node's answer (timeout → 504). |

- **Secure by default.** The fallback policy requires a signed-in user who isn't waiting on a forced password change.
  Only `setup-required`, `setup`, `token`, `/healthz` and the API docs allow anonymous access.
  - **`SignedIn` policy:** for `me` and `change-password` only, so users with a pending password change can still reach them.
  - **`Admin` policy:** Admin role required.
- **JWT** access tokens are HMAC-SHA512 signed and valid for 8 hours (`Auth:TokenLifetime`).
  - Claims: `sub`, `name`, `role`, `sstamp` (security stamp) and `mcp` (must change password).
  - Every request checks the security stamp against the user file. A password reset, role change or disable revokes that user's tokens immediately.
- **Rate limit** on `setup`, `token` and `change-password`: 10 requests per minute per IP address. Accounts lock for 15 minutes after 5 wrong passwords.
  An unknown username takes the same time to answer as a wrong password, so usernames can't be guessed that way.
- **OpenAPI** uses .NET 10's built-in `Microsoft.AspNetCore.OpenApi`, with a Bearer security scheme.
  The **Scalar** explorer is at `/scalar` in Development, or when `Api:EnableDocs=true`.
- Errors are returned as **ProblemDetails** (RFC 9457) by `ApiExceptionHandler`.
  - Radio offline → 503, radio timeout → 504.
  - Radio `ERR` codes: not found → 404, table full / bad state / refused value → 409, unsupported or disabled → 501.
  - Validation → 400. Account rules: duplicate or last Admin → 409, deleting yourself → 403.

## 5. Website — ASP.NET MVC as a BFF (Backend-for-Frontend)

```
Browser ──cookie──► MVC (Razor views + /bff/*) ──JWT──► REST API ──► MeshCoreClient ──WiFi/USB──► radio
```

**Built in Phase 5:**

- The browser **never sees the JWT**. It holds only the `MeshCore.Auth` cookie (HttpOnly, SameSite=Strict, Secure when
  `Web:RequireHttps` is on) and an antiforgery token.
- **Sign-in:**
  - MVC calls `POST /api/v1/auth/token` and copies the token's claims into a cookie identity.
  - The JWT itself goes inside the encrypted cookie ticket (`StoreTokens`).
  - The cookie expires together with the token, with no sliding expiry.
- **Calling the API:**
  - `MeshApiClient` (typed `HttpClient`) is the website's only way to data.
  - `ApiTokenHandler` (`DelegatingHandler`) adds the user's token to each call.
  - If the API answers 401, for example because a user was disabled or reset, the cookie is cleared and the user goes back to sign-in.
- **Two sign-in schemes, one set of policies:**
  - A policy scheme forwards `/api/*` to JWT bearer and everything else to the cookie.
  - The API **never accepts the cookie**, so other sites can't make a signed-in browser call it (CSRF).
  - Every website form is protected by `[AutoValidateAntiforgeryToken]` on the `WebController` base class.
- **Separate ports in one process** (`Kestrel:Endpoints`):
  - The API is on `127.0.0.1:5081` and the website on `0.0.0.0:5080`.
  - With `Web:SeparatePorts` (on by default), routes only match on their own port (`RequireHost`).
  - The API can't be reached through the website's port, and the API port isn't reachable from the LAN at all.
- **Pages** are rendered on the server:
  - Dashboard, Chats (list and thread with a send form), Contacts, Channels (add, edit, show key, delete), and Device.
  - Settings, with Users for Admins.
  - Every page works without JavaScript.
- **Behaviour:**
  - First-run gate middleware sends every page to `/Account/Setup` until an Admin exists.
  - A filter sends users with a temporary password to change it before anything else.
  - API failures become messages on the page, or a friendly error page when the radio is offline.
- **Security headers** on every page:
  - A strict CSP (`default-src 'self'`, no inline script or style). Everything is served locally, with Bootstrap restored by LibMan.
  - `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy` and `Permissions-Policy`.

**Live chat (Phase 6):** each signed-in page opens one SignalR connection to `/hubs/mesh` with its cookie. `live.js` keeps the unread badge, tab title, radio status pill and chat list current and shows toasts. `chat.js` adds messages and updates delivery ticks in the open thread, sends with `fetch`, catches up after reconnecting, and marks messages read. JS calls go
to the MVC side (`/Messages/Send`, `MarkRead`, `Updates`, `UnreadCount`, `List`), which calls the API, so no proxy is needed. Without JavaScript every page still works with plain form posts.

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

**Storage** goes in `appsettings.auth.json` inside the app's data folder (`MeshCore:DataDirectory`, default `data/`).
That folder is always writable, even when the app itself is installed somewhere read-only.
`JsonUserStore` reads and writes the file directly instead of going through `IConfiguration`, because users are
data, not settings. Writes are atomic (write to a temp file, then rename), and all updates go through one lock,
so rules such as "only while no users exist" and "not the last Admin" can't race each other.

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
  "MeshCore": { "Connection": "192.168.1.212:5000", "AppName": "MeshCoreComputerClient" },
  "Endpoints": { "Web": "http://0.0.0.0:5080", "Api": "http://127.0.0.1:5081" },
  "ConnectionStrings": { "Mesh": "Data Source=meshcore.db" },
  "Auth": { "TokenLifetimeMinutes": 60, "LockoutAttempts": 5, "LockoutMinutes": 15 }
}
```

Users and the encrypted JWT key are written by the app to `appsettings.auth.json` (see §5). That file is listed in `.gitignore`.

`Connection` takes any address `MeshCoreEndpoint` understands: `192.168.1.212:5000`, `COM11`, or `auto` to probe USB.
Command-line overrides work out of the box, e.g. `MeshCore.Host.exe --MeshCore:Connection=COM7`.

## 8. Build phases

| # | Phase | Done when |
|---|---|---|
| 1 | Solution scaffold, protocol library (framing + codes + core codecs), unit tests | Golden-frame tests pass |
| 2 | TCP + serial transports, endpoint parsing, USB auto-detect, `MeshCoreClient`, handshake, `meshcore-cli` diagnostic tool | ✅ `meshcore-cli info` works against the Heltec V4 over WiFi |
| 3 | SQLite persistence (EF Core migrations), `RadioConnectionService` + `MeshRadio`, send direct/channel messages with ACK tracking, console chat | ✅ Connected to the Heltec; takeover backoff verified |
| 4 | REST API + OpenAPI + JWT + roles/policies + first-run setup + user management endpoints | ✅ Verified against the Heltec; a User gets 403 on admin endpoints (tested) |
| 5 | MVC BFF: setup/login/change-password, responsive layout (bottom tabs / sidebar), Dashboard, Chats, Contacts, Channels, Device, Settings → Users | ✅ Checked at phone and desktop sizes against the Heltec; Admin adds a user who signs in (tested) |
| 6 | Responsive Messages chat UI + SignalR live updates + ACK ticks + per-user unread + PWA manifest + radio simulator | ✅ Live chat checked at phone and desktop sizes against the simulator (PWA install not yet verified on a phone) |
| 7 | Advanced: repeater login/status/telemetry/trace/CLI, radio settings, contact management, key import/export | ✅ All features checked against the simulator; read-only values checked on the Heltec |
| 8 | Polish: HTTPS for LAN (own CA + site certificate), log files (Serilog), Windows service / systemd, single-file publish (win-x64, linux-x64, linux-arm64), setup guide | ✅ Published exe run from another folder: HTTPS verified with a strict client, logs written, Heltec connected |

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
