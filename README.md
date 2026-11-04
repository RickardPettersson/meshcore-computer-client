# MeshCore Computer Client

A .NET 10 app that connects to a [MeshCore](https://github.com/meshcore-dev/MeshCore) **companion radio** over
WiFi (TCP) or USB serial. It stores messages, contacts and channels in SQLite, and will host a REST API and a
responsive website (see [docs/PLAN.md](docs/PLAN.md)).

**Status:** phases 1–4 of 8 done. See [docs/STATUS.md](docs/STATUS.md) for progress, tested hardware and code size.

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

## REST API

The API runs on http://127.0.0.1:5081/api/v1. In Development, you can explore and try every endpoint at http://127.0.0.1:5081/scalar.

```bash
# First run: create the Admin (only works once)
curl -X POST http://127.0.0.1:5081/api/v1/auth/setup -H "content-type: application/json" -d "{\"username\":\"admin\",\"password\":\"choose a long password\"}"
```

Send the returned `accessToken` as `Authorization: Bearer <token>`. Later sign-ins use `POST /api/v1/auth/token`.
Admins add more users with `POST /api/v1/users`. Each new user must change their temporary password at first sign-in.

> The WiFi firmware serves **one client at a time**. While this app runs, don't connect the phone app or
> `meshcore-cli` to the same radio. If something else takes the radio over, the app waits 2 minutes before reconnecting.

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
