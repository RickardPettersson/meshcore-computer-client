# Setup Guide

How to install MeshCore Computer Client on a computer that's always on, connect it to your radio, and use it
from phones and desktops on your network.

## 1. What you need

- A **MeshCore companion radio** running the **WiFi** or **USB** companion firmware. The BLE build doesn't work.
  - **WiFi:** note the radio's IP address and port (default 5000), e.g. `192.168.1.212:5000`. Heltec V4: see [HELTEC-V4-WIFI.md](HELTEC-V4-WIFI.md) to build and flash the WiFi firmware.
  - **USB:** plug it in; the app finds it by itself, or you can name the port (`COM11`, `/dev/ttyACM0`).
- A computer on the same network: Windows 10/11, Linux, or a Raspberry Pi (64-bit OS).
- To **build** the app: the .NET 10 SDK. **Running** a published build needs no .NET install.

## 2. Get it

**Windows, easiest:** download the latest `MeshCoreComputerClient-<version>-win-x64.zip` from the
[Releases page](https://github.com/RickardPettersson/meshcore-computer-client/releases) and unzip it, e.g. to `C:\MeshCore`.
It's self-contained: **no .NET install needed**. It includes `meshcore-cli.exe`, the service scripts and these docs.
To check the download, compare `Get-FileHash <zip> -Algorithm SHA256` with the `.sha256` file next to it.

**Or build it yourself** (any platform, needs the .NET 10 SDK):

```bash
git clone https://github.com/RickardPettersson/meshcore-computer-client.git
cd meshcore-computer-client
dotnet publish src/MeshCore.Host -p:PublishProfile=win-x64       # or linux-x64, linux-arm64 (Raspberry Pi)
```

The result goes in `artifacts/publish/<platform>/`. It's one file (`MeshCore.Host.exe` or `MeshCore.Host`) plus
`appsettings.json` and the `wwwroot` folder. Copy that folder wherever you want the app to live, e.g. `C:\MeshCore` or `/opt/meshcore`.

## 3. Configure

Edit `appsettings.json` in that folder:

| Setting | What it is |
|---|---|
| `MeshCore:Connection` | `192.168.1.212:5000` (WiFi), `COM11` / `/dev/ttyACM0` (USB), `auto` (find a USB radio) or `simulator` (no radio) |
| `Kestrel:Endpoints:Web:Url` | Website over HTTP, default `http://0.0.0.0:5080` (whole network) |
| `Web:Https` | Website over HTTPS: `Enabled` (default on), `Port` (5443), `RedirectHttp`, and optionally your own `CertificatePath` / `CertificatePassword` (.pfx) |
| `Kestrel:Endpoints:Api:Url` | REST API, default `http://127.0.0.1:5081` (this computer only) |
| `MeshCore:DataDirectory` | Where the database, users, keys, certificates and logs go (default `data` next to the app) |
| `Serilog:MinimumLevel` | Log detail (`Information` by default; set `Override:MeshCore` to `Debug` when troubleshooting) |

You can also override any setting on the command line, e.g. `MeshCore.Host.exe --MeshCore:Connection=COM7`.

## 4. First start

Run `MeshCore.Host.exe` (or `./MeshCore.Host` on Linux). The console shows the radio connecting and the addresses it listens on.

1. Open **http://localhost:5080** and create the **Admin** account. That only works while there are no users.
2. Under **Settings → Users**, add accounts for others. Each new user picks their own password at first sign-in.
3. Under **Device → Node settings**, give your node a short name.

Started in a terminal, the app also gives you a console chat (`/help`). Stop it with Ctrl+C.

## 5. Phones: HTTPS and "install as app"

Phones need HTTPS for secure cookies, offline support, location and app install. The app makes its own certificate for your network:

1. On the phone, open **http://&lt;computer's IP&gt;:5080/Account/Certificate**. The page is also linked from the sign-in page and Settings.
2. Tap **Download certificate** and follow the steps for iPhone or Android on that page.
3. Open **https://&lt;computer's IP&gt;:5443**. There's no warning, and you can now **Add to Home Screen** to use it like an app.

The certificate covers `localhost`, the computer's name and its current IP addresses. If the computer's IP changes,
the app issues a new site certificate signed by the same authority on its next start, so phones keep trusting it.
For a stable address, give the computer a fixed IP (DHCP reservation) in your router.

Want to skip installing the certificate? Phones can accept the browser warning instead. Want your own certificate
(e.g. from your router or a domain)? Set `Web:Https:CertificatePath`.

## 6. Start automatically

### Windows service

From an **Administrator** PowerShell:

```powershell
.\tools\service\install-windows-service.ps1 -AppFolder "C:\MeshCore"
```

This creates the **MeshCoreComputerClient** service and sets it up as follows:
- It starts with Windows, runs as the unprivileged `LocalService` account, and restarts if it stops.
- It opens ports 5080 and 5443 in Windows Firewall, for **private** networks only.

Logs are in `C:\MeshCore\data\logs`. To remove the service, run `.\tools\service\uninstall-windows-service.ps1`; your data is kept.

### Linux (systemd), e.g. a Raspberry Pi

```bash
sudo useradd --system --home /opt/meshcore meshcore
sudo cp -r artifacts/publish/linux-arm64 /opt/meshcore && sudo chown -R meshcore /opt/meshcore
sudo usermod -aG dialout meshcore          # USB radios only
sudo cp tools/service/meshcore.service /etc/systemd/system/
sudo systemctl enable --now meshcore
journalctl -u meshcore -f                  # or /opt/meshcore/data/logs
```

## 7. Back up, move, update

- **Back up** the `data` folder. It holds everything: messages (`meshcore.db`), users (`appsettings.auth.json`),
  encryption keys (`keys/`) and certificates (`https/`). Stop the app first so the database is consistent.
- The **`keys/` folder belongs to `appsettings.auth.json`** and the certificates. On Windows they're protected with DPAPI for the account
  that runs the app. If you restore to another computer or account, users have to sign in again and a new certificate
  is made: phones then need the new certificate.
- **Update:** stop the app or service, replace the exe and `wwwroot` (keep `appsettings.json` and `data`), and start it again.
  Database changes are applied automatically on start.

## 8. Troubleshooting

| Symptom | Check |
|---|---|
| Status pill says **"In use elsewhere"** | The WiFi radio serves one client at a time. Close the phone app or `meshcore-cli`. The app retries after 2 minutes. |
| **"Offline"**, can't reach the WiFi radio | Is it on the same network, and does `ping <ip>` work? If ping works but the port doesn't, power-cycle the radio. |
| USB radio not found | Companion **USB** firmware? Try `meshcore-cli ports`, then set `MeshCore:Connection` to the port. |
| Phone can't open the site | Same WiFi network? Firewall allows 5080/5443 on a *private* network? Use the computer's IP, not its name. |
| Browser warns about the certificate | Install the certificate (section 5), or check the address is one the certificate covers (listed in the log at start). |
| Something else | Look in `data/logs/meshcore-<date>.log`. Set `Serilog:MinimumLevel:Override:MeshCore` to `Debug` for more detail. |
