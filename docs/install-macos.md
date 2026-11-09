# Install on macOS

For Macs with Apple silicon (M1 and newer) or an Intel processor, on a macOS version Apple still updates. No .NET install needed.
Other systems: [Windows](install-windows.md), [Linux](install-linux.md).

Which Mac? Apple menu → *About This Mac*: "Apple M…" means **osx-arm64**, "Intel" means **osx-x64**.

## 1. Download and unpack

The easiest way is in Terminal, where macOS doesn't add its "downloaded from the internet" flag:

```bash
mkdir -p ~/MeshCore && cd ~/MeshCore
curl -LO https://github.com/RickardPettersson/meshcore-computer-client/releases/download/v<version>/MeshCoreComputerClient-<version>-osx-arm64.tar.gz
tar -xzf MeshCoreComputerClient-<version>-osx-arm64.tar.gz --strip-components 1
```

Optional: check the download with `shasum -a 256 -c <file>.sha256`.

**Downloaded with a browser instead?** The app is signed, but not notarized by Apple (that needs a paid Apple developer account).
So after a browser download, macOS says the app "can't be opened" or that it "can't check it for malicious software". Unpack it,
then clear the flag once, in the app folder:

```bash
cd ~/MeshCore
xattr -dr com.apple.quarantine .
```

The service script below does this for you too.

## 2. Set your radio

Open `~/MeshCore/appsettings.json` (e.g. `open -e appsettings.json`) and set `MeshCore:Connection`. Use `192.168.1.212:5000` for WiFi,
or the USB port, e.g. `/dev/cu.usbmodem1101` (`ls /dev/cu.usb*` lists them; `auto` finds one by itself). The other settings are in the
[setup guide](SETUP.md#settings).

## 3. First start

```bash
cd ~/MeshCore
./MeshCore.Host
```

macOS may ask whether to accept incoming network connections: allow it, so phones can reach the site. Open **http://localhost:5080**
and continue with [first steps](SETUP.md#first-steps). Stop the app with Ctrl+C.

## 4. Start automatically (at login)

```bash
cd ~/MeshCore
./service/install-macos-service.sh
```

The app then starts now and every time you log in, and restarts if it stops. Keep the app in that folder: the service runs it from there.

- **Restart after changing settings:** `launchctl kickstart -k gui/$(id -u)/meshcore-computer-client`
- **Stop it starting:** `./service/install-macos-service.sh --uninstall` (your data is kept)
- It runs while you're logged in. If phones should reach it all the time, keep the Mac from sleeping (System Settings → Energy / Battery).

## 5. Update

1. If it runs at login: `launchctl bootout gui/$(id -u)/meshcore-computer-client`. Otherwise stop it with Ctrl+C.
2. Unpack the new version over the old folder, but **keep your `appsettings.json`**. For example, unpack it into a new folder and
   copy everything except `appsettings.json` and `data` into `~/MeshCore`.
3. Run `./service/install-macos-service.sh` again (or `./MeshCore.Host`). Database changes are applied automatically.

## Where things are

| What | Where |
|---|---|
| Program | `~/MeshCore/MeshCore.Host`, `meshcore-cli` |
| Settings | `~/MeshCore/appsettings.json` |
| Data: messages, users, keys, certificates | `~/MeshCore/data` (back this up, see [backup](SETUP.md#back-up-and-move)) |
| Logs | `~/MeshCore/data/logs` |

## macOS problems

| Symptom | Check |
|---|---|
| "can't be opened" / "can't check it for malicious software" / "is damaged" | Run `xattr -dr com.apple.quarantine .` in the app folder (step 1). |
| `Bad CPU type in executable` | Wrong package: an Intel Mac needs osx-x64. (An Apple silicon Mac can run osx-x64 under Rosetta, but osx-arm64 is faster.) |
| Phones can't open the site | System Settings → Network → Firewall: allow `MeshCore.Host`. Is the Mac awake? |
| USB radio not found | `ls /dev/cu.usb*`; set `MeshCore:Connection` to the port it lists. |

More problems: [troubleshooting](SETUP.md#troubleshooting).
