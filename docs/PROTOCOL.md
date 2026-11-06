# MeshCore Companion Protocol — Developer Reference

This page lists the sources used to build `MeshCore.Protocol`, and the byte layouts taken from them.
Use it to check or update the library when the firmware changes.

> **The firmware source is the real specification.** The official protocol doc covers only BLE, is incomplete,
> and calls itself "still in development". Wherever the doc and the source disagree, the source in
> `examples/companion_radio/MyMesh.cpp` wins.

## 1. Sources

The layouts were checked against MeshCore `main` at commit
[`81dbae5`](https://github.com/meshcore-dev/MeshCore/tree/81dbae592ffad40b4212bc0b37e82b38da08f4bc)
(2026-10-15, the same code as release
[`companion-v1.18.0`](https://github.com/meshcore-dev/MeshCore/releases/tag/companion-v1.18.0),
`FIRMWARE_VER_CODE` 14). Links below point to that commit, so they stay valid when `main` moves on.

| Source | What it provides |
|---|---|
| [`examples/companion_radio/MyMesh.cpp`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/MyMesh.cpp) | **Main reference.** All `CMD_*`, `RESP_CODE_*`, `PUSH_CODE_*`, `ERR_CODE_*` values; command parsing in `handleCmdFrame()`; reply layouts; `writeContactRespFrame()`. Key spots: `CMD_APP_START` [L1076](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/MyMesh.cpp#L1076), `CMD_SET_ADVERT_NAME` [L1259](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/MyMesh.cpp#L1259), `CMD_REBOOT` [L1512](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/MyMesh.cpp#L1512) |
| [`examples/companion_radio/MyMesh.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/companion_radio/MyMesh.h) | `FIRMWARE_VER_CODE` |
| [`src/helpers/ArduinoSerialInterface.cpp`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/ArduinoSerialInterface.cpp) | **USB/serial framing**: `writeFrame()` (`'>'` + LE16 length) and the `checkRecvFrame()` state machine (`'<'` + LE16 length) |
| [`src/helpers/wifi/SerialWifiInterface.cpp`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/wifi/SerialWifiInterface.cpp) / [`.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/wifi/SerialWifiInterface.h) | **WiFi/TCP transport**: a TCP server using the same `'<'`/`'>'` + LE16 header; one client at a time; 4-frame send queue |
| [`src/helpers/BaseSerialInterface.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/BaseSerialInterface.h) | `MAX_FRAME_SIZE` = 176 |
| [`src/MeshCore.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/MeshCore.h) | `PUB_KEY_SIZE` = 32, `MAX_PATH_SIZE` = 64, `MAX_PACKET_PAYLOAD` = 184 |
| [`src/Packet.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/Packet.h) | How `path_len` is packed: `getPathHashSize()`, `getPathHashCount()` |
| [`src/helpers/ContactInfo.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/ContactInfo.h) | `ContactInfo` struct, contact flag bits, `OUT_PATH_UNKNOWN` = 0xFF |
| [`src/helpers/AdvertDataHelpers.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/AdvertDataHelpers.h) | `ADV_TYPE_*` |
| [`src/helpers/TxtDataHelpers.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/TxtDataHelpers.h) | `TXT_TYPE_*` |
| [`docs/companion_protocol.md`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/docs/companion_protocol.md) | Official (BLE-only, incomplete) protocol doc: recommended connect sequence, one-command-at-a-time rule, channel handling |

Other implementations that are useful to compare against (not used as sources):
[meshcore.js](https://github.com/meshcore-dev/meshcore.js) (JavaScript, used by the official web app) and
[meshcore_py](https://github.com/meshcore-dev/meshcore_py) (Python).

## 2. Transports and framing

Every byte-stream transport (USB serial and WiFi/TCP) uses the same framing, so `FrameEncoder` and
`FrameDecoder` are shared by both through `StreamTransport`. BLE is different: each GATT write or notification carries
exactly one frame, with no header.

| Firmware build | Transport | Implemented in | Address |
|---|---|---|---|
| Companion **WiFi** | TCP client to the radio's server, default port **5000** | `TcpTransport` | `192.168.1.212`, `192.168.1.212:5000`, `tcp://host:port` |
| Companion **USB** | Serial 115200 8N1 | `SerialTransport` | `COM11`, `/dev/ttyACM0` |
| Companion **BLE** | Not supported (yet) | — | — |

`MeshCoreEndpoint.Parse` turns any address above into the right transport.

| Direction | Bytes | Implemented in |
|---|---|---|
| App → radio | `3C` (`'<'`) · len LSB · len MSB · payload | `FrameEncoder`, `FrameMarker.ToRadio` |
| Radio → app | `3E` (`'>'`) · len LSB · len MSB · payload | `FrameDecoder`, `FrameMarker.FromRadio` |

- The serial line is 115200 baud, 8N1.
- A frame with length 0 is ignored. On receive, the firmware cuts any frame longer than `MAX_FRAME_SIZE` (176) down to that size.
  `FrameDecoder` instead drops such a frame and resyncs on the next start byte.
- The first payload byte is the code. Codes `0x00`–`0x7F` are responses and `0x80`+ are pushes.
- Commands have **no request ID**. Send one command, wait for its reply, then send the next.
  Pushes can arrive in between.
- Multi-byte integers are little-endian. Strings are UTF-8, and fixed-width fields are padded with NUL.
- Assert **DTR** when opening the port. nRF52 boards (T-Echo, RAK) only send over USB CDC while DTR is set.
  `SerialTransport` asserts both DTR and RTS, like pyserial does. With both asserted, the ESP32 auto-reset circuit stays idle.

### WiFi (TCP) specifics — from `SerialWifiInterface.cpp`

- **One client at a time.** When a new TCP client connects, the radio closes the previous one without warning.
  The old client sees the socket close (EOF), which `StreamTransport` reports as an `EndOfStreamException`.
  So this app must be the **only** thing connected to the radio. A phone app, or a second copy of this app, will keep kicking it off.
- The radio queues at most **4 outgoing frames** and drops frames beyond that. It sends one per loop pass and
  reads new commands only when that queue is empty. Waiting for each reply before sending the next command keeps us well within this.
- The radio drops frames whose header doesn't start with `'<'`, and skips frames larger than `MAX_FRAME_SIZE`.
- The radio sends nothing to detect a dead connection, so `TcpTransport` enables **TCP keep-alive** (15 s idle, then 3 probes 5 s apart).
  A radio that loses power is then noticed in about 30 s.
- Tested against a **Heltec V4.3 OLED** with companion WiFi firmware v1.18.0. That build has **private key export enabled**, so `EXPORT_PRIVATE_KEY` returns the key.
  The app keeps that Admin-only and never stores or logs it.

### USB vs BLE companion firmware

Companion firmware is built either for **USB** (`companion_radio_usb`) or for **BLE** (`companion_radio_ble`)
on most boards. The BLE build still shows up as a USB serial port, but it never reads the protocol from it.
Writes stall until they time out, and nothing comes back. To use this app over USB, flash the
*Companion (USB)* build, for example with the [MeshCore web flasher](https://flasher.meshcore.co.uk).
The USB build doesn't advertise over BLE, so the phone app can't connect at the same time.

## 3. Frame layouts used by the library

All offsets include the code byte at `[0]`.

### Commands (app → radio) — `Commands.cs`

| Command | Layout | Reply |
|---|---|---|
| `APP_START` 0x01 | `[1]` app ver (firmware ignores it) · `[2..7]` reserved · `[8..]` app name. **Must be ≥ 8 bytes.** | `SELF_INFO` |
| `DEVICE_QUERY` 0x16 | `[1]` app protocol ver. **3 or higher turns on V3 message frames (with SNR).** | `DEVICE_INFO` |
| `GET_CONTACTS` 0x04 | optional `[1..4]` `since` (uint32 unix time) | `CONTACTS_START`, `CONTACT`×N, `END_OF_CONTACTS` |
| `GET_DEVICE_TIME` 0x05 | — | `CURR_TIME` |
| `SET_DEVICE_TIME` 0x06 | `[1..4]` unix time. **The firmware refuses to move the clock backwards** (`ERR` 6). | `OK` |
| `SEND_SELF_ADVERT` 0x07 | optional `[1]` = 1 to flood; otherwise zero-hop | `OK` |
| `SET_ADVERT_NAME` 0x08 | `[1..]` name, cut to 31 bytes | `OK` |
| `SEND_TXT_MSG` 0x02 | `[1]` txt_type · `[2]` attempt · `[3..6]` timestamp · `[7..12]` recipient key prefix · `[13..]` text | `SENT`, then `PUSH 0x82` |
| `SEND_CHANNEL_TXT_MSG` 0x03 | `[1]` txt_type (only plain) · `[2]` channel idx · `[3..6]` timestamp · `[7..]` text | **`OK`** (not `SENT`; channel messages get no ACK) |
| `SYNC_NEXT_MESSAGE` 0x0A | — | a message frame or `NO_MORE_MESSAGES` |
| `GET_CHANNEL` 0x1F | `[1]` idx | `CHANNEL_INFO` |
| `SET_CHANNEL` 0x20 | `[1]` idx · `[2..33]` name · `[34..49]` 16-byte secret (50 bytes total; 32-byte secrets are rejected) | `OK` |
| `GET_BATT_AND_STORAGE` 0x14 | — | `BATT_AND_STORAGE` |
| `REBOOT` 0x13 | `[1..6]` the literal text `"reboot"` | none |
| `REMOVE_CONTACT` 0x0F / `RESET_PATH` 0x0D | `[1..32]` public key | `OK` |

### Responses and pushes (radio → app) — `FrameParser.cs`

| Frame | Layout |
|---|---|
| `OK` 0x00 | — |
| `ERR` 0x01 | `[1]` error code: 1 unsupported · 2 not found · 3 table full · 4 bad state · 5 file I/O · 6 illegal arg |
| `SELF_INFO` 0x05 | `[1]` adv type · `[2]` tx power · `[3]` max tx power · `[4..35]` pub key · `[36..39]` lat µ° · `[40..43]` lon µ° · `[44]` multi_acks · `[45]` advert loc policy · `[46]` telemetry modes · `[47]` manual add · `[48..51]` freq kHz · `[52..55]` bw Hz · `[56]` SF · `[57]` CR · `[58..]` name |
| `DEVICE_INFO` 0x0D | `[1]` fw ver code · `[2]` max contacts **÷ 2** · `[3]` max channels · `[4..7]` BLE PIN · `[8..19]` build date · `[20..59]` model · `[60..79]` version string · `[80]` repeat enabled (v9+) · `[81]` path hash mode (v10+). Older firmware sends fewer bytes. |
| `CONTACTS_START` 0x02 | `[1..4]` total count |
| `CONTACT` 0x03 / `PUSH_NEW_ADVERT` 0x8A | `[1..32]` pub key · `[33]` type · `[34]` flags · `[35]` out_path_len · `[36..99]` out_path (always 64 bytes) · `[100..131]` name · `[132..135]` last advert · `[136..139]` lat · `[140..143]` lon · `[144..147]` lastmod. 148 bytes total. |
| `END_OF_CONTACTS` 0x04 | `[1..4]` most recent lastmod. Pass it as `since` next time. |
| `CURR_TIME` 0x09 | `[1..4]` unix time |
| `SENT` 0x06 | `[1]` 1 = flooded · `[2..5]` expected ACK · `[6..9]` est. timeout ms |
| `CONTACT_MSG_RECV_V3` 0x10 | `[1]` SNR×4 (int8) · `[2..3]` reserved · `[4..9]` sender prefix · `[10]` path_len · `[11]` txt_type · `[12..15]` timestamp · if signed: `[16..19]` signer prefix · then text |
| `CONTACT_MSG_RECV` 0x07 (legacy) | `[1..6]` prefix · `[7]` path_len · `[8]` txt_type · `[9..12]` timestamp · then (signer) text |
| `CHANNEL_MSG_RECV_V3` 0x11 | `[1]` SNR×4 · `[2..3]` reserved · `[4]` channel idx · `[5]` path_len · `[6]` txt_type · `[7..10]` timestamp · `[11..]` text (`"name: message"`) |
| `CHANNEL_MSG_RECV` 0x08 (legacy) | `[1]` channel idx · `[2]` path_len · `[3]` txt_type · `[4..7]` timestamp · `[8..]` text |
| `NO_MORE_MESSAGES` 0x0A | — |
| `CHANNEL_INFO` 0x12 | `[1]` idx · `[2..33]` name · `[34..49]` secret |
| `BATT_AND_STORAGE` 0x0C | `[1..2]` battery mV · `[3..6]` used KB · `[7..10]` total KB (storage only on newer firmware) |
| `PUSH_ADVERT` 0x80 / `PUSH_PATH_UPDATED` 0x81 | `[1..32]` pub key |
| `PUSH_SEND_CONFIRMED` 0x82 | `[1..4]` ACK code (matches `SENT` expected ACK) · `[5..8]` round trip ms |
| `PUSH_MSG_WAITING` 0x83 | — (drain with `SYNC_NEXT_MESSAGE`) |

### Node settings, contacts, repeaters and identity — `Commands.Advanced.cs`

Checked against `handleCmdFrame()` in the pinned `MyMesh.cpp` (lines 1266–1830 at that commit).

| Command | Layout | Reply |
|---|---|---|
| `SET_ADVERT_LATLON` 0x0E | `[1..4]` lat µ° (int32) · `[5..8]` lon µ° · optional `[9..12]` alt (future) | `OK`, or `ERR` 6 if out of range |
| `SET_RADIO_PARAMS` 0x0B | `[1..4]` freq **kHz** · `[5..8]` bw **Hz** · `[9]` SF · `[10]` CR · optional `[11]` repeat (v9+). The firmware accepts 150–2500 MHz, 7–500 kHz, SF 5–12, CR 5–8. | `OK` |
| `SET_RADIO_TX_POWER` 0x0C | `[1]` dBm (**signed**, −9…max) | `OK` |
| `GET_TUNING_PARAMS` 0x2B | — | `TUNING_PARAMS` 0x17: `[1..4]` rx_delay_base × 1000 · `[5..8]` airtime_factor × 1000 |
| `SET_TUNING_PARAMS` 0x15 | same two uint32 values | `OK` |
| `SET_OTHER_PARAMS` 0x26 | `[1]` manual_add_contacts · `[2]` telemetry modes `(env<<4)\|(loc<<2)\|base` · `[3]` advert_loc_policy · `[4]` multi_acks (later bytes optional) | `OK` |
| `ADD_UPDATE_CONTACT` 0x09 | Same as the contact frame: key · type · flags · out_path_len · out_path(64) · name(32) · last_advert · lat · lon · optional lastmod (`updateContactFromFrame`) | `OK`, or `ERR` 3 (table full) |
| `GET_CONTACT_BY_KEY` 0x1E · `SHARE_CONTACT` 0x10 · `LOGOUT` 0x1D · `SEND_STATUS_REQ` 0x1B | `[1..32]` pub key | `CONTACT` / `OK` / `OK` / `SENT` + push 0x87 |
| `EXPORT_CONTACT` 0x11 | `[1..32]` pub key, or **nothing for our own advert** | `EXPORT_CONTACT` 0x0B: `[1..]` signed advert packet |
| `IMPORT_CONTACT` 0x12 | `[1..]` exported advert (must be > 98 bytes) | `OK`, or `ERR` 6 if it doesn't verify |
| `SEND_LOGIN` 0x1A | `[1..32]` pub key · `[33..]` password (blank = guest) | `SENT`, then push 0x85 / 0x86 |
| `SEND_TELEMETRY_REQ` 0x27 | `[1..3]` **reserved** · `[4..35]` pub key. A 4-byte frame means "our own telemetry". | `SENT`, then push 0x8B |
| `SEND_TRACE_PATH` 0x24 | `[1..4]` tag · `[5..8]` auth · `[9]` flags (hash size = `1 << (flags & 3)`) · `[10..]` path hashes | `SENT`, then push 0x89 with the same tag |
| `EXPORT_PRIVATE_KEY` 0x17 | — | `PRIVATE_KEY` 0x0E: `[1..64]`, or `DISABLED` 0x0F (`ENABLE_PRIVATE_KEY_EXPORT`) |
| `IMPORT_PRIVATE_KEY` 0x18 | `[1..64]` | `OK`, `ERR` 6 (invalid key), or `DISABLED` |
| `SEND_TXT_MSG` 0x02 with `txt_type` 1 (CLI) | as a direct message | `SENT` with **expected_ack 0**: CLI commands are never ACKed. The reply arrives as a contact message with `txt_type` 1. |
| `RUN_CLI_COMMAND` 0x42 (v14+) | `[1..]` command text, run **on the companion itself** (`handleCommand`), e.g. `get wifi.ip`, `set wifi.ssid …`, `set name …` | `CLI_REPLY` 0x1D: `[1..]` reply text (`"> …"` or `"Unknown command"`) |

**Companion CLI WiFi commands** (builds with `ENABLE_WIFI_INTERFACE`; the stored values override the compiled `WIFI_SSID`/`WIFI_PWD`
and apply after a reboot): `get/set wifi.ssid`, `set wifi.pwd`, `get wifi.pwd` (prints it in plain text), `set wifi.clear`,
`get/set wifi.enabled`, `get wifi.status`, `get wifi.ip`. `wifi_enabled` defaults to 1, and WiFi only starts when an SSID is set
(`examples/companion_radio/main.cpp`, `NodePrefs.h`).

| Push | Layout |
|---|---|
| `LOGIN_SUCCESS` 0x85 | `[1]` permissions (bit 0 = admin) · `[2..7]` prefix · new servers add `[8..11]` server time · `[12]` ACL permissions · `[13]` firmware level. Legacy "OK" logins stop after the prefix. |
| `LOGIN_FAIL` 0x86 | `[1]` reserved · `[2..7]` prefix |
| `STATUS_RESPONSE` 0x87 | `[1]` reserved · `[2..7]` prefix · `[8..]` the server's status struct. Repeaters send `RepeaterStats` (56 bytes, below). |
| `TELEMETRY_RESPONSE` 0x8B | `[1]` reserved · `[2..7]` prefix · `[8..]` CayenneLPP |
| `TRACE_DATA` 0x89 | `[1]` reserved · `[2]` path_len (bytes) · `[3]` flags · `[4..7]` tag · `[8..11]` auth · path hashes · one SNR×4 per hop · final SNR×4 (how we heard the last hop) |
| `CONTACT_DELETED` 0x8F | `[1..32]` key of a contact the radio overwrote to make room |
| `CONTACTS_FULL` 0x90 | — |

**`RepeaterStats`** comes from [`examples/simple_repeater/MyMesh.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/examples/simple_repeater/MyMesh.h), little-endian:
- battery mV (u16) · TX queue length (u16) · noise floor (i16) · last RSSI (i16)
- packets received / sent (u32 ×2) · TX airtime s (u32) · uptime s (u32)
- sent flood / direct, received flood / direct (u32 ×4)
- error events (u16) · last SNR×4 (i16) · direct / flood duplicates (u16 ×2)
- RX airtime s (u32) · receive errors (u32)

Room servers send a different struct; the app shows those bytes raw.

**CayenneLPP** values are **big-endian** (unlike the rest of the protocol): channel, type, then the value. The types and scales
come from [`LPPDataHelpers.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/sensors/LPPDataHelpers.h).
For example, 116 is voltage (u16 ÷ 100), 103 is temperature (i16 ÷ 10), and 136 is GPS (3 × 3 signed bytes: lat and lon ÷ 10000, altitude ÷ 100).
`CayenneLpp.Decode` stops at the first unknown type, or at zero padding.

**Trace path built by the app:** the contact's out-path, then the target's hash, then the out-path reversed, so the trace
comes back to us. A contact with an unknown route can't be traced. 3-byte path hashes can't be traced either, because the trace flags only allow 1, 2 or 4 bytes.

### The `path_len` byte (`PathLength`)

- `0xFF` means "none": a contact with an unknown out-path, or a message that was sent direct rather than flooded.
- Otherwise the low 6 bits are the hop count and the top 2 bits are the hash size minus one.
  So the path takes `hops × hashSize` bytes. See `Packet::getPathHashSize()`.

## 4. Gotchas found in the source

- `SYNC_NEXT_MESSAGE` **removes** the message from the radio's queue, so the app must save it itself.
  `MeshRadio.SyncMessagesAsync` stores each message before it pops the next one.
- **Text limit is 160 bytes** (`MAX_TEXT_LEN` = 10 × 16-byte cipher blocks, in
  [`BaseChatMesh.h`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/BaseChatMesh.h)).
  The radio rejects a longer direct message.
- **Channel messages get our name added by the radio.**
  [`BaseChatMesh::sendGroupMessage`](https://github.com/meshcore-dev/MeshCore/blob/81dbae592ffad40b4212bc0b37e82b38da08f4bc/src/helpers/BaseChatMesh.cpp#L518)
  writes `"<node name>: <text>"` and **silently cuts it off** at 160 bytes.
  So the real limit is `160 − len(name) − 2`, which is `ProtocolLimits.MaxChannelTextBytes(name)`.
  `MeshRadio` rejects longer text before sending, so nothing is cut off without the user knowing.
  The app sends only the text; receivers see `"name: text"`.
- `DEVICE_INFO[2]` holds `MAX_CONTACTS / 2`, so the parser multiplies it by 2.
- `SEND_CHANNEL_TXT_MSG` doesn't check the frame length before reading the timestamp.
  `Commands.SendChannelTextMessage` always writes the full header.
- `CHANNEL_INFO` writes the name with `strcpy`, so the parser reads the name only up to the first NUL inside its 32 bytes.

## 5. Updating for a new firmware release

1. Diff the [source](https://github.com/meshcore-dev/MeshCore) between the commit above and the new tag.
   Focus on `examples/companion_radio/MyMesh.cpp`, and on `FIRMWARE_VER_CODE` in `MyMesh.h`.
2. Update `Codes.cs`, `Commands.cs` and `FrameParser.cs`. Add the new bytes to the tests in
   `tests/MeshCore.Protocol.Tests`, written by hand from the firmware layout, not with the library's own writer.
3. Update the commit, tag and tables in this file.
