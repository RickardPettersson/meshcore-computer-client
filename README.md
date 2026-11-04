# MeshCore Computer Client

A .NET 10 app that connects to a [MeshCore](https://github.com/meshcore-dev/MeshCore) **companion radio** over
WiFi (TCP) or USB serial. It stores messages, contacts and channels in SQLite, and will host a REST API and a
responsive website (see [docs/PLAN.md](docs/PLAN.md)).

**Status:** phases 1–6 of 8 done. See [docs/STATUS.md](docs/STATUS.md) for progress, tested hardware and code size.

Developer reference for the radio protocol, with pinned firmware sources: [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Requirements

- .NET 10 SDK
- A MeshCore companion radio running the **WiFi** or **USB** companion firmware (the BLE build is not supported)

## Run

Set the radio address in `src/MeshCore.Host/appsettings.json` (`MeshCore:Connection`), or pass it on the command line:

```bash
dotnet run --project src/MeshCore.Host -- --MeshCore:Connection=192.168.1.212:5000
```

When started in a terminal you get a console chat. Type text to send it to the current channel, or `/help` for the commands.
Data is stored in `src/MeshCore.Host/data/`: the SQLite database, encryption keys, and `appsettings.auth.json` with the users.

## Website

Open **http://localhost:5080** on this computer, or **http://&lt;this computer's IP&gt;:5080** from a phone on the same network.

1. The first visit asks you to create the **Admin** account.
2. As Admin, add people under **Settings → Users**. Each new user gets a temporary password and chooses their own at first sign-in.
3. On a phone you get a bottom tab bar. On a desktop you get a sidebar. Light/dark follows the system, with a toggle in Settings.
4. Chats are **live**: new messages appear without reloading, delivery ticks go ✓ → ✓✓, and each user has their own unread badges.
   On a phone, use the browser's "Add to Home Screen" to open it like an app.

Windows may ask to allow the app through the firewall the first time, so phones can reach port 5080.
The site uses plain HTTP for now; HTTPS for LAN use is planned (Phase 8).

| Port | What | Reachable from |
|---|---|---|
| 5080 | Website | your network (`Kestrel:Endpoints:Web`) |
| 5081 | REST API | this computer only (`Kestrel:Endpoints:Api`) |

## REST API

The API runs on http://127.0.0.1:5081/api/v1. In Development, you can explore and try every endpoint at http://127.0.0.1:5081/scalar.

```bash
# First run: create the Admin (only works once)
curl -X POST http://127.0.0.1:5081/api/v1/auth/setup -H "content-type: application/json" -d "{\"username\":\"admin\",\"password\":\"choose a long password\"}"
```

Send the returned `accessToken` as `Authorization: Bearer <token>`. Later sign-ins use `POST /api/v1/auth/token`.
Admins add more users with `POST /api/v1/users`. Each new user must change their temporary password at first sign-in.

**Live updates:** connect a SignalR client to `http://127.0.0.1:5081/api/v1/hub`. Send the token as a bearer header,
or as `?access_token=` for WebSockets. It sends `message` (new message or delivery status), `radio`, `contacts` and `channels` events.

> The WiFi firmware serves **one client at a time**. While this app runs, don't connect the phone app or
> `meshcore-cli` to the same radio. If something else takes the radio over, the app waits 2 minutes before reconnecting.

## Try it without a radio

The built-in simulator acts like a companion radio. It has contacts, channels, nodes that chat now and then, ACKs, and occasional replies.
Nothing is transmitted.

```bash
dotnet run --project src/MeshCore.Host -- --MeshCore:Connection=simulator --MeshCore:DataDirectory=data-sim
```

## Diagnostic CLI

```bash
dotnet run --project tools/MeshCore.Cli -- info --connect 192.168.1.212
```

The CLI commands are `ports`, `info`, `contacts`, `channels`, `messages`, `listen`, `send`, `send-channel` and `advert`.

## Develop

```bash
dotnet test MeshCore.slnx
dotnet tool restore
dotnet ef migrations add <Name> --project src/MeshCore.Data --startup-project src/MeshCore.Host --output-dir Migrations
```
