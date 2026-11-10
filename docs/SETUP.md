# Setup Guide

How to set up MeshCore Computer Client on a computer that's always on, connect it to your radio, and use it
from phones and desktops on your network.

## Install

First install it on your computer. Every package is self-contained: **no .NET install needed**.

| Computer | Package | Guide |
|---|---|---|
| Windows 10/11 (64-bit) | `MeshCoreComputerClient-<version>-win-x64.zip` | [Install on Windows](install-windows.md) |
| Ubuntu, Debian, Linux Mint, Raspberry Pi OS (64-bit) | `meshcore-computer-client_<version>_amd64.deb` / `_arm64.deb` | [Install on Linux](install-linux.md) |
| Other Linux (64-bit) | `MeshCoreComputerClient-<version>-linux-x64.tar.gz` / `-linux-arm64.tar.gz` | [Install on Linux](install-linux.md#other-linux-targz) |
| Mac (Apple silicon or Intel) | `MeshCoreComputerClient-<version>-osx-arm64.tar.gz` / `-osx-x64.tar.gz` | [Install on macOS](install-macos.md) |

Download them from the [Releases page](https://github.com/RickardPettersson/meshcore-computer-client/releases).
Each guide covers downloading, starting it automatically, updating, where its files are, and problems specific to that system.
This page covers everything that's the same everywhere.

## What you need

- A **MeshCore companion radio** running the **WiFi** or **USB** companion firmware. The BLE build doesn't work.
  - **WiFi:** note the radio's IP address and port (default 5000), e.g. `192.168.1.212:5000`. Heltec V4: see [HELTEC-V4-WIFI.md](HELTEC-V4-WIFI.md) to build and flash the WiFi firmware.
  - **USB:** plug it in. The app finds it by itself, or you can name the port (`COM11`, `/dev/ttyACM0`, `/dev/cu.usbmodem1101`).
- A computer on the same network as your phones, ideally one that's always on.

## Settings

The settings are in `appsettings.json`. Where it is depends on how you installed: next to the program, or
`/etc/meshcore-computer-client/appsettings.json` with the Linux `.deb`. Restart the app after changing it.

| Setting | What it is |
|---|---|
| `MeshCore:Connection` | `192.168.1.212:5000` (WiFi), `COM11` / `/dev/ttyACM0` / `/dev/cu.usbmodem1101` (USB on Windows / Linux / macOS), `auto` (find a USB radio) or `simulator` (no radio) |
| `Kestrel:Endpoints:Web:Url` | Website over HTTP, default `http://0.0.0.0:5080` (whole network) |
| `Web:Https` | Website over HTTPS: `Enabled` (default on), `Port` (5443), `RedirectHttp`, and optionally your own `CertificatePath` / `CertificatePassword` (.pfx) |
| `Kestrel:Endpoints:Api:Url` | REST API, default `http://127.0.0.1:5081` (this computer only) |
| `MeshCore:DataDirectory` | Where the database, users, keys, certificates and logs go (default `data` next to the app; `/var/lib/meshcore-computer-client` with the `.deb`) |
| `BatteryLog` | Battery history of "my devices": `Enabled`, `Interval` (`01:00:00`), `Retention` (`90.00:00:00`). See [Battery history](#battery-history). |
| `Images` | Photo sharing in chats: `Enabled`, `DefaultExpiry` (`1h`, `12h`, `24h`, `72h` or `keep`), `MaxDimension` (1600 px). See [Images](#images). |
| `Serilog:MinimumLevel` | Log detail (`Information` by default; set `Override:MeshCore` to `Debug` when troubleshooting) |

You can also override any setting on the command line, e.g. `MeshCore.Host --MeshCore:Connection=COM7`, or with an environment
variable, e.g. `MeshCore__Connection=COM7` (two underscores).

## First steps

1. Open **http://localhost:5080** (or **http://&lt;computer's IP&gt;:5080** from another device) and create the **Admin** account.
   That only works while there are no users.
2. Under **Settings → Users**, add accounts for others. Each new user picks their own password at first sign-in.
3. Under **Device → Node settings**, give your node a short name.

Started in a terminal, the app also gives you a console chat (`/help`).

| Port | What | Reachable from |
|---|---|---|
| 5080 | Website over HTTP (redirects to HTTPS) | your network |
| 5443 | Website over HTTPS | your network |
| 5081 | REST API | this computer only |

## Phones: HTTPS and install as app

Phones need HTTPS for secure cookies, offline support, location and app install. The app makes its own certificate for your network:

1. On the phone, open **http://&lt;computer's IP&gt;:5080/Account/Certificate**. The page is also linked from the sign-in page and Settings.
2. Tap **Download certificate** and follow the steps for iPhone or Android on that page.
3. Open **https://&lt;computer's IP&gt;:5443**. There's no warning, and you can now **Add to Home Screen** to use it like an app.

The certificate covers `localhost`, the computer's name and its current IP addresses. If the computer's IP changes,
the app issues a new site certificate signed by the same authority on its next start, so phones keep trusting it.
For a stable address, give the computer a fixed IP (DHCP reservation) in your router.

Want to skip installing the certificate? Phones can accept the browser warning instead. Want your own certificate
(e.g. from your router or a domain)? Set `Web:Https:CertificatePath`.

## Battery history

The app records the battery of **this radio** and of every contact you mark as **My device** (open the contact → *Mark as my
device*; Admin only). **My devices** in the menu lists them with the last check, battery and temperature. Tap a name to open the
contact (or the Device page for this radio), or its battery for graphs over 24 hours to 90 days.

- **How often:** once an hour (`BatteryLog:Interval`). The schedule follows the last check, so restarting the app doesn't cause
  extra checks. Admins can press **Check now**.
- **How:** this radio is read directly. Other devices get a telemetry request over the mesh: one small packet per device per hour.
  Repeaters and room servers you're logged in to are asked for their status if telemetry doesn't answer.
- **The other device must allow telemetry.** In the MeshCore app's telemetry settings, choose *Allow*, or *favourites* and add this
  node as a favourite there. Otherwise every check shows as "refused" or "no answer".
- **No answer is recorded too.** A line that slopes down and then stops points to a flat battery. A steady line that suddenly turns
  into "no answer" points to something else: power, a reboot, range or the route.
- **Temperature** is recorded when the device sends one in its telemetry (most boards measure their own temperature, so it reads
  a little above the air around it). This radio's own temperature isn't available.
- **Percent** is estimated from the voltage the same way as the MeshCore app: 3.0 V is 0%, 4.2 V is 100%, in a straight line
  (single-cell Li-ion/LiPo). It reads high while charging and dips while transmitting.
- **Storage:** readings older than 90 days are deleted (`BatteryLog:Retention`). Unmarking a device keeps its history.

## Images

The 🖼 button in a chat uploads a photo and puts its link in the message. MeshCore messages are at most 160 bytes, so the
photo itself can't travel over the radio; the link does, and the recipient opens it on a phone with internet.

- **Where it goes:** [Litterbox](https://litterbox.catbox.moe) for 1 hour to 3 days (then deleted), or [Catbox](https://catbox.moe)
  for "keep" (deleted after 2 years without views). Both are free and need no account. Links look like
  `https://litter.catbox.moe/a1b2c3.jpg`.
- **What's sent:** the app shrinks the photo to 1600 px, turns it upright and re-encodes it as a JPEG. **No metadata is sent**:
  EXIF with GPS location and camera details, IPTC and XMP are all removed. Nothing is stored on this computer.
- **Who can see it:** uploads aren't listed or searchable, but anyone with the link can open it. Short random links can be
  guessed by scanners, and the Public channel can be read by everyone on the mesh. For anything private, use a short expiry and a private
  message or private channel.
- **Rules:** the hosts' own rules apply (for example, no commercial use of Catbox). Each user can upload 20 photos per 10 minutes.
- **Turn it off:** set `Images:Enabled` to `false`.

## Back up and move

- **Back up** the data folder (`data` next to the app, or `/var/lib/meshcore-computer-client` with the `.deb`). It holds everything:
  messages (`meshcore.db`), users (`appsettings.auth.json`), encryption keys (`keys/`) and certificates (`https/`).
  Stop the app first so the database is consistent. Back up `appsettings.json` too if you changed it.
- The **`keys/` folder belongs to `appsettings.auth.json`** and the certificates. On Windows they're protected with DPAPI for the account
  that runs the app. On Linux and macOS they're plain files: keep the data folder readable only by the account that runs the app.
- If you restore to another computer or account, users may have to sign in again and a new certificate may be made: phones then
  need the new certificate.
- **Updating** is covered in each install guide. Database changes are applied automatically on start.

## Build it yourself

Needs the .NET 10 SDK, on any of the systems above:

```bash
git clone https://github.com/RickardPettersson/meshcore-computer-client.git
cd meshcore-computer-client
dotnet publish src/MeshCore.Host -p:PublishProfile=win-x64       # or linux-x64, linux-arm64, osx-arm64, osx-x64
```

The result goes in `artifacts/publish/<platform>/`: one program file (`MeshCore.Host.exe` or `MeshCore.Host`) plus
`appsettings.json` and the `wwwroot` folder. `./build/package.ps1 -Version 1.0.0 -Runtime <platform>` makes the same package as a
release. It needs PowerShell, and the `.deb` is only made on a Debian or Ubuntu machine. Build macOS packages on a Mac so they get signed.

## Troubleshooting

| Symptom | Check |
|---|---|
| Status pill says **"In use elsewhere"** | The WiFi radio serves one client at a time. Close the phone app or `meshcore-cli`. The app retries after 2 minutes. |
| **"Offline"**, can't reach the WiFi radio | Is it on the same network, and does `ping <ip>` work? If ping works but the port doesn't, power-cycle the radio. |
| USB radio not found | Companion **USB** firmware? Try `meshcore-cli ports`, then set `MeshCore:Connection` to the port. |
| Phone can't open the site | Same WiFi network? Firewall allows 5080/5443? Use the computer's IP, not its name. |
| Browser warns about the certificate | Install the certificate ([phones](#phones-https-and-install-as-app)), or check the address is one the certificate covers (listed in the log at start). |
| Something else | Look in the log files (`logs` in the data folder). Set `Serilog:MinimumLevel:Override:MeshCore` to `Debug` for more detail. |

Problems specific to one system: [Windows](install-windows.md#windows-problems), [Linux](install-linux.md#linux-problems),
[macOS](install-macos.md#macos-problems).
