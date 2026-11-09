# Install on Linux

For 64-bit Linux on a PC (Intel/AMD) or ARM, e.g. a Raspberry Pi 3/4/5 with a **64-bit** OS. No .NET install needed.
Other systems: [Windows](install-windows.md), [macOS](install-macos.md).

- **Ubuntu, Debian, Linux Mint, Raspberry Pi OS:** use the [.deb package](#ubuntu-debian-raspberry-pi-os-deb). It installs a service,
  and updates like any other package.
- **Other Linux:** use the [.tar.gz](#other-linux-targz).

Which CPU? `uname -m` says `x86_64` (PC: `amd64` / `linux-x64`) or `aarch64` (ARM: `arm64` / `linux-arm64`). If it says `armv7l`,
you have a 32-bit OS, which isn't supported; install the 64-bit Raspberry Pi OS.

## Ubuntu, Debian, Raspberry Pi OS (.deb)

### Install

Download the `.deb` for your CPU from the [Releases page](https://github.com/RickardPettersson/meshcore-computer-client/releases), then:

```bash
sudo apt install ./meshcore-computer-client_<version>_arm64.deb    # or _amd64.deb on a PC
```

The package does the following:
- installs the app in `/opt/meshcore-computer-client`, and puts `meshcore-cli` on the PATH;
- creates an unprivileged `meshcore` user, which may use USB serial ports (group `dialout`);
- starts the `meshcore-computer-client` service, which also starts at boot and restarts if it stops.

### Set your radio

```bash
sudo nano /etc/meshcore-computer-client/appsettings.json     # MeshCore:Connection, e.g. 192.168.1.212:5000 or /dev/ttyACM0
sudo systemctl restart meshcore-computer-client
```

Then open **http://localhost:5080**, or **http://&lt;this computer's IP&gt;:5080** from another device, and continue with
[first steps](SETUP.md#first-steps). The other settings are in the [setup guide](SETUP.md#settings).

### Every day

| What | How |
|---|---|
| Status | `systemctl status meshcore-computer-client` |
| Logs | `journalctl -u meshcore-computer-client -f`, or the files in `/var/lib/meshcore-computer-client/logs` |
| Restart after changing settings | `sudo systemctl restart meshcore-computer-client` |
| Stop it starting at boot | `sudo systemctl disable --now meshcore-computer-client` (`enable --now` turns it back on) |
| Diagnostic CLI | `meshcore-cli info --connect 192.168.1.212` (stop the service first: a WiFi radio serves one client at a time) |

### Update

Install the newer `.deb` the same way: `sudo apt install ./meshcore-computer-client_<new version>_arm64.deb`. The service restarts
on the new version. Your settings and data are kept. If you changed `appsettings.json` and the new version's default has changed
too, apt asks which one to keep; the default answer keeps yours. Database changes are applied automatically.

### Remove

```bash
sudo apt remove meshcore-computer-client     # stops and removes the app; keeps settings and data
sudo apt purge meshcore-computer-client      # also deletes settings and data (messages, users, keys, certificates)
```

### Where things are

| What | Where |
|---|---|
| Program | `/opt/meshcore-computer-client`, and `meshcore-cli` in `/usr/bin` |
| Settings | `/etc/meshcore-computer-client/appsettings.json` (readable by root and the service only) |
| Data: messages, users, keys, certificates | `/var/lib/meshcore-computer-client` (back this up, see [backup](SETUP.md#back-up-and-move)) |
| Logs | `journalctl -u meshcore-computer-client`, and `/var/lib/meshcore-computer-client/logs` |
| Service | `meshcore-computer-client.service`. To change how it runs without editing the file: `sudo systemctl edit meshcore-computer-client` |

The service may only write to its data folder; the rest of the system is read-only to it.

## Other Linux (.tar.gz)

Download `MeshCoreComputerClient-<version>-linux-x64.tar.gz` (PC) or `-linux-arm64.tar.gz` (ARM) from the
[Releases page](https://github.com/RickardPettersson/meshcore-computer-client/releases), then unpack it:

```bash
curl -LO https://github.com/RickardPettersson/meshcore-computer-client/releases/download/v<version>/MeshCoreComputerClient-<version>-linux-arm64.tar.gz
tar -xzf MeshCoreComputerClient-<version>-linux-arm64.tar.gz
cd MeshCoreComputerClient-<version>-linux-arm64
```

Optional: check the download with `sha256sum -c <file>.sha256`.

Set your radio in `appsettings.json` (`MeshCore:Connection`), then try it with `./MeshCore.Host`. Open **http://localhost:5080** and
continue with [first steps](SETUP.md#first-steps). Stop it with Ctrl+C.

The app needs a few common system libraries: glibc, libstdc++, ICU (`libicu`) and OpenSSL. Most distributions have them installed.

### Start automatically (systemd)

From the unpacked folder:

```bash
sudo ./service/install-linux-service.sh
```

It copies the app to `/opt/meshcore`, creates the `meshcore` user (group `dialout`, for USB radios), and installs and starts the
`meshcore` service. Settings: `/opt/meshcore/appsettings.json`, then `sudo systemctl restart meshcore`. Logs: `journalctl -u meshcore -f`
or `/opt/meshcore/data/logs`.

**Update:** unpack the new version and run its `install-linux-service.sh`. Your `appsettings.json` and `data` folder are kept.
**Remove:** `sudo ./service/install-linux-service.sh --uninstall` removes the service and keeps `/opt/meshcore`.

Moving from the `.tar.gz` to the `.deb`? Uninstall the `meshcore` service first, so the two don't fight over the radio and the ports.
Then copy `/opt/meshcore/data/*` to `/var/lib/meshcore-computer-client/` while the new service is stopped, and run
`sudo chown -R meshcore:meshcore /var/lib/meshcore-computer-client`.

## Linux problems

| Symptom | Check |
|---|---|
| `cannot execute binary file` / `Exec format error` | Wrong CPU: `uname -m` says `aarch64` → arm64, `x86_64` → amd64/x64. A 32-bit OS isn't supported. |
| Phones can't open the site | Firewall on? With `ufw`: `sudo ufw allow from 192.168.1.0/24 to any port 5080,5443 proto tcp` (your network's addresses). |
| USB radio not found | `ls /dev/ttyACM* /dev/ttyUSB*`; set `MeshCore:Connection` to it. Running by hand? Your own user must be in `dialout`: `sudo usermod -aG dialout $USER`, then log in again. |
| apt says a dependency can't be installed | Your distribution is older than Ubuntu 20.04 / Debian 11, or isn't Debian-based: use the `.tar.gz`. |
| The service won't start | `journalctl -u meshcore-computer-client -n 50` shows why, e.g. a typo in `appsettings.json`. |

More problems: [troubleshooting](SETUP.md#troubleshooting).
