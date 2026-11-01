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

## 2. Serial framing

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

### The `path_len` byte (`PathLength`)

- `0xFF` means "none": a contact with an unknown out-path, or a message that was sent direct rather than flooded.
- Otherwise the low 6 bits are the hop count and the top 2 bits are the hash size minus one.
  So the path takes `hops × hashSize` bytes. See `Packet::getPathHashSize()`.

## 4. Gotchas found in the source

- `SYNC_NEXT_MESSAGE` **removes** the message from the radio's queue, so the app must save it itself.
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
