# Heltec V4: MeshCore Companion over WiFi

How to put the MeshCore **companion WiFi** firmware on a **Heltec WiFi LoRa 32 V4** so this app can reach it over
your network (`MeshCore:Connection = <ip>:5000`). With WiFi, the radio can sit anywhere with power and a WiFi signal,
for example high up in a window, instead of next to the computer.

> **Why you build it yourself:** the WiFi companion build needs your WiFi name and password inside it, so MeshCore
> doesn't offer it ready-made. The [web flasher](https://flasher.meshcore.io) and the release files only have **BLE** and
> **USB** companion builds for the V4. This is also what the official FAQ says (7.6). Building takes about 15 minutes
> the first time; you don't need to know C++.

Sources, for the curious: `variants/heltec_v4/platformio.ini` (env `heltec_v4_companion_radio_wifi`),
`examples/companion_radio/main.cpp` and `MyMesh.cpp` in [MeshCore](https://github.com/meshcore-dev/MeshCore) at
`companion-v1.18.0`.

## 1. What you need

- A **Heltec V4** with its LoRa antenna attached. Never power it on without the antenna; that can damage the radio.
- A USB-C **data** cable (some cables only charge) and a computer.
- **2.4 GHz WiFi.** The ESP32-S3 can't use 5 GHz networks.
- [Git](https://git-scm.com/) and **PlatformIO**. Either install
  [VS Code](https://code.visualstudio.com/) with the PlatformIO extension, or use the command line only: install
  [Python](https://www.python.org/) and run `pip install platformio`.

Which build: the plain **Heltec V4** with the small OLED screen uses `heltec_v4_companion_radio_wifi`.
The V4 with a TFT screen and the V4 "R8" are different boards (`variants/heltec_v4_tft`, `variants/heltec_v4_r8`), so check
those folders for their own WiFi build before following this guide.

## 2. Back up first (if the radio already runs MeshCore)

Flashing with a full erase gives the radio a **new identity**: a new public key, so friends must re-add you, and no contacts or channels.
To keep the identity:
- In this app: **Device → Node settings → Show private key**. This works if the firmware allows export, which the v1.18 builds do.
- Or use the MeshCore phone app's export.
- Write down your channel names and keys too: **Channels → 🔑**.

A plain update without erasing (step 5, option B) normally keeps everything.

## 3. Get the firmware source

```bash
git clone https://github.com/meshcore-dev/MeshCore.git
cd MeshCore
git checkout companion-v1.18.0          # or a newer companion-v* release tag
```

## 4. Put your WiFi name and password in

Open `variants/heltec_v4/platformio.ini`, find the section `[env:heltec_v4_companion_radio_wifi]` and change these two lines:

```ini
  -D WIFI_SSID='"myssid"'
  -D WIFI_PWD='"mypwd"'
```

to your network, keeping both kinds of quotes exactly as shown:

```ini
  -D WIFI_SSID='"MyHomeWiFi"'
  -D WIFI_PWD='"my wifi password"'
```

- Names and passwords with spaces work. Ones containing `'` or `"` need escaping. It's easier to use the CLI afterwards (step 8).
- **Don't commit or share this file** now that it contains your password.
- Optional: the radio listens on TCP port **5000**. To change it, add a line `-D TCP_PORT=5000` with your port.

## 5. Build and flash

Plug the Heltec into the computer.

**Option A, fresh install** (new radio, or switching from Meshtastic or another firmware). This erases everything:
```bash
pio run -e heltec_v4_companion_radio_wifi -t erase
pio run -e heltec_v4_companion_radio_wifi -t upload
```

**Option B, update** (already MeshCore and you want to keep identity, contacts and channels):
```bash
pio run -e heltec_v4_companion_radio_wifi -t upload
```

The first build downloads the ESP32 toolchain, which takes a while. If the upload can't find the board, hold the **BOOT**
button (labelled `PRG` on some boards), tap **RST**, release BOOT, and run the upload again. In VS Code you can do the same from
the PlatformIO sidebar: *heltec_v4_companion_radio_wifi → Upload*.

## 6. Check that it joined your WiFi

This build prints WiFi messages on the USB port. Watch them with:

```bash
pio device monitor -b 115200
```

Press **RST** and look for `WiFi: connecting to MyHomeWiFi` and then `WiFi: WiFi connected successfully!`.
Wrong password or a 5 GHz-only network? It keeps retrying every 10 seconds. Fix step 4 and flash again.

**Find its IP address** in your router's list of connected devices; it usually shows up as `esp32s3-…`. While you're in
the router, **reserve that address** (DHCP reservation) so it never changes.

Check it from this project:

```bash
dotnet run --project tools/MeshCore.Cli -- info --connect 192.168.1.212
```

## 7. Connect MeshCore Computer Client

In `appsettings.json`:

```json
"MeshCore": { "Connection": "192.168.1.212:5000" }
```

Start the app and the status pill turns green. Then, in the website:

1. **Device → Node settings → Radio (LoRa) settings:** set your region's preset. **Every node in your mesh must use the same
   values**; ask your local community. For example, many EU meshes moved to 869.618 MHz, BW 62.5 kHz, SF 8, CR 5 in 2025–26, and
   the official FAQ lists presets.
2. **Name:** short is good, because it's added in front of every channel message.
3. **TX power:** on the V4, the setting goes through a built-in amplifier.
   - The firmware default **10** gives about **22 dBm** at the antenna. That's "standard" in the official FAQ.
   - **22** gives about **28 dBm** (0.5–0.6 W), "high".
   - Check what's legal where you live (in the EU 869.4–869.65 MHz band, 500 mW ERP). Don't go above 22 in the app.

## 8. Change WiFi later without rebuilding (firmware v14+ / v1.18)

The WiFi name and password you flashed are only defaults. The radio also stores its own WiFi settings, which win once
they're set. While it's reachable, you can change them with companion CLI commands:

```bash
meshcore-cli cli get wifi.status --connect 192.168.1.212     # > connected
meshcore-cli cli get wifi.ip     --connect 192.168.1.212     # > 192.168.1.212
meshcore-cli cli set wifi.ssid NewNetwork --connect 192.168.1.212
meshcore-cli cli set wifi.pwd  new password --connect 192.168.1.212
meshcore-cli cli reboot --connect 192.168.1.212              # the new WiFi applies after a reboot
```

- `set wifi.clear` drops the stored settings, so the flashed defaults apply again after a reboot.
- `set wifi.enabled 0` turns WiFi off.
- Double-check the name and password before rebooting: if they're wrong, the radio can't come back on WiFi, and you'd fix it by flashing again (step 5, option B).
- `get wifi.pwd` prints the stored password in plain text, so don't run it where others can see.

## 9. Things to know

- **One WiFi client at a time.** When another client (the phone app, `meshcore-cli`) connects, the radio drops the current one.
  MeshCore Computer Client then shows *"In use elsewhere"* and waits 2 minutes before reconnecting.
- **WiFi, not Bluetooth.** This build doesn't advertise over BLE, so the phone app can't connect over Bluetooth. Use this app's website
  on your phone instead.
- **Power.** WiFi keeps the radio awake, so run it from USB power rather than a small battery.
- **Updating firmware later.** Repeat steps 3–5 with the new release tag. Your WiFi edits in `platformio.ini` need re-applying after
  `git checkout`, or they're kept if you use `git stash` / `git stash pop`. The stored WiFi settings from step 8 survive an update.

## 10. Troubleshooting

| Symptom | Fix |
|---|---|
| `pio` not found | Restart the terminal after installing PlatformIO, or use VS Code's PlatformIO terminal. |
| Upload: "No serial data received" / port not found | Data cable? Try BOOT + RST (step 5). On Windows, close anything else using the COM port. |
| Monitor shows WiFi retrying forever | Check the name and password in step 4, and that the network is 2.4 GHz. Some mesh WiFi systems need the 2.4 GHz band named separately. |
| App says "Could not reach …:5000" | Right IP (check the router)? Same network? Can you `ping` it? |
| App says "In use elsewhere" | Close the phone app or `meshcore-cli` connected to the same radio. |
| No messages from others | The radio settings (step 7.1) must match your local mesh exactly. |
