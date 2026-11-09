# Install on Windows

For Windows 10 and 11 (64-bit). No .NET install needed. Other systems: [Linux](install-linux.md), [macOS](install-macos.md).

## 1. Download and unpack

1. From the [Releases page](https://github.com/RickardPettersson/meshcore-computer-client/releases), download
   `MeshCoreComputerClient-<version>-win-x64.zip`.
2. Unzip it where the app will live, e.g. `C:\MeshCore`.
3. Optional: check the download. `Get-FileHash <zip> -Algorithm SHA256` must match the `.sha256` file.

## 2. Set your radio

Open `C:\MeshCore\appsettings.json` in Notepad and set `MeshCore:Connection` to your radio. Use `192.168.1.212:5000` for WiFi,
or `COM11` for USB (`auto` finds a USB radio by itself). The other settings are in the [setup guide](SETUP.md#settings).

## 3. First start

Double-click `MeshCore.Host.exe`, or run it in a terminal. The window shows the radio connecting and the addresses the app listens on.
Windows may ask whether to allow the app through the firewall: allow it on **private** networks, so phones can reach it.

Then open **http://localhost:5080** and continue with [first steps](SETUP.md#first-steps). Close the window (or press Ctrl+C) to stop the app.

## 4. Start automatically (Windows service)

From an **Administrator** PowerShell, in the app folder:

```powershell
.\service\install-windows-service.ps1 -AppFolder "C:\MeshCore"
```

This creates the **MeshCoreComputerClient** service:
- it starts with Windows, runs as the unprivileged `LocalService` account, and restarts if it stops;
- it opens ports 5080 and 5443 in Windows Firewall, for **private** networks only.

Manage it under *Services* (`services.msc`) or with `Restart-Service MeshCoreComputerClient` after changing `appsettings.json`.
Logs are in `C:\MeshCore\data\logs`. To remove the service, run `.\service\uninstall-windows-service.ps1`; your data is kept.

## 5. Update

1. Stop the app, or the service: `Stop-Service MeshCoreComputerClient`.
2. Unzip the new version over the old folder, but **keep your `appsettings.json`** (don't overwrite it) and the `data` folder.
3. Start it again: `Start-Service MeshCoreComputerClient`. Database changes are applied automatically.

## Where things are

| What | Where |
|---|---|
| Program | `C:\MeshCore\MeshCore.Host.exe`, `meshcore-cli.exe` |
| Settings | `C:\MeshCore\appsettings.json` |
| Data: messages, users, keys, certificates | `C:\MeshCore\data` (back this up, see [backup](SETUP.md#back-up-and-move)) |
| Logs | `C:\MeshCore\data\logs` |

The encryption keys in `data\keys` are protected with Windows DPAPI for the account that runs the app. After restoring to
another computer or account, users have to sign in again, and phones need the new certificate.

## Windows problems

| Symptom | Check |
|---|---|
| Phones can't open the site | Did you allow the firewall prompt? Is the network set to *Private* in Windows settings? The service script opens the ports for private networks only. |
| USB radio not found | Try `meshcore-cli ports`; set `MeshCore:Connection` to the `COM` port it lists. |
| The service won't start | Look in `C:\MeshCore\data\logs` and in *Event Viewer → Windows Logs → Application*. |

More problems: [troubleshooting](SETUP.md#troubleshooting).
