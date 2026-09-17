# CellPort

**[简体中文](README.md) | English**

**A Windows-native manager for DJI 1st-gen 4G modules (Quectel EG25-G) — SMS · Calls · Device info · eSIM / eUICC profile switching · AT console.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-blue)
![.NET](https://img.shields.io/badge/.NET-9.0%20WPF-512BD4)
![Release](https://img.shields.io/github/v/release/xmgzxmgz/CellPort)
![License](https://img.shields.io/badge/license-MIT-green)

CellPort is a **Windows-native** (no WSL required) manager for the DJI 1st-gen 4G module,
feature-matched to CellDock on macOS: SMS, voice calls, device status,
**eSIM / eUICC profile management (including switching)** and an AT console.

> The hardware is the DJI 4G module gen-1 (in fact a Quectel EG25-G / MDM9607 core).
> Any EG25-G / EC25 series module should work in theory.

**📥 Download without building**: grab a package from [Releases](https://github.com/xmgzxmgz/CellPort/releases/latest):

| Package | When to use |
|---|---|
| `CellPort-v*-win-x64.zip` (~0.4 MB) | You already have [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0) |
| `CellPort-v*-win-x64-selfcontained.zip` (~60 MB) | No runtime needed — unzip and run |

Unzip and run `CellPort.exe`.

## Features

- **SMS**: receive (`+CMTI` / `+CMT` unsolicited), send (PDU mode), long-SMS (UDH 8/16-bit) reassembly,
  UCS2 Chinese, GSM 7-bit packing, conversation-style UI
- **Voice calls**: dial, answer, hang up, DTMF, mute, call log
- **Device status**: model, firmware, operator, signal (dBm/%), network mode, band, SIM state,
  IMEI / ICCID / IMSI / own number, registration, data attach, USB interface list
- **eSIM / eUICC** (the core of this project):
  - Logical-channel access: EID (dual path) + profile list (ICCID / carrier / ISD-P AID / state / class)
  - **Profile enable / disable switching** (verified on real hardware), with guided module reboot & auto-reconnect
  - **Profile delete** (`BF33`) and **nickname edit** (`BF29`, addressed by ICCID, ≤64 ASCII chars) — byte-exact with lpac
- **USSD queries**: `AT+CUSD` wrapper with GSM7-packed / UCS2 auto-decoding and chained-response hints
- **SIM phonebook**: read SM storage capacity and contacts, Chinese names (UCS2) auto-decoded, click a number to copy
- **SMS export**: one-click CSV (Excel-friendly, BOM) / JSON per conversation
- **Runtime logs**: `%LOCALAPPDATA%\CellPort\logs`, 7-day retention, global exception handlers
- **Update check**: one-click check against GitHub Releases from the Settings page
- **AT console**: run any command, history, quick commands
- **Connection strategy**: auto port detection + port cache + Bluetooth virtual-port filtering,
  cold start ~600ms, warm start ~60ms
- **Quality**: 30 unit tests for the Core layer (GSM7 standard vectors, ES10c byte-exact encoding, BCD, export escaping),
  GitHub Actions auto-release on tag push
- Dark / light / system theme; SMS forwarding to Bark / Feishu / DingTalk

## Hardware background (verified)

| Item | Value |
| --- | --- |
| Module model | Baiwang **QDC507** |
| Module core | Quectel **EG25-G** (Qualcomm MDM9607, Linux 3.18.44 ARMv7) |
| USB VID:PID (DJI custom) | `2ca3:4006` |
| USB VID:PID (standard Quectel) | `2c7c:0125` |
| Firmware on test unit | `QDC507GLEFM21` |

## Key finding: "this module can't read eUICC"? — use the right channel

Early in development we wrongly concluded that the QDC507 firmware's `AT+CSIM` passthrough
(~8-byte APDU limit) made ISD-R unreachable. The limit is real:

| APDU | Length | Result |
|---|---|---|
| `00A40000023F00` (SELECT MF) | 7 B / 14 chars | `9000` OK |
| Same + 1 byte | 8 B / 16 chars | `612E` (executed) |
| Same + 2 bytes | 9 B / 18 chars | **no response at all** |
| `00A4040000` + 16-byte AID | 21 B / 42 chars | **no response at all** |

But SELECT ISD-R was never meant to go through CSIM — the [lpac](https://github.com/estkme-group/lpac)
embedded in CellDock uses **3GPP TS 27.007 logical-channel commands**, bypassing the limit entirely:

1. `AT+CCHO="<ISD-R AID>"` — the modem firmware selects ISD-R internally, returns `+CCHO: <channel>`;
2. `AT+CGLA=<ch>,<len>,"<hex>"` — raw APDU passthrough on that channel for ES10x;
3. `AT+CCHC=<ch>` — close the channel.

QDC507 firmware (`QDC507GLEFM21_01.001.02.001`) supports all three. CellPort implements a full
ES10c client on top of this:

- **STORE DATA framing** (aligned with lpac `es10x_transmit_iter`): CLA=`0x80|ch`, INS=`0xE2`,
  P1=`0x11` (middle) / `0x91` (last), P2=block seq, max 120 bytes per block;
  `61xx` → `80 C0 00 00 <sw2>` (GET RESPONSE) loop.
- **GetProfilesInfo** (`BF2D 00`): response parsed `BF2D → A0 → E3`
  (`5A` ICCID / `4F` ISD-P AID / `9F70` state / `90` nickname / `91` carrier / `92` profile name / `95` class).
- **GetEid**: SGP.22 `BF3E 02 5C 01 5A` first, fallback to SGP.02 `GET DATA (0x80|ch) CA 9F 7F 00`.

Verified on a 9eSIM writable eUICC card: **all 7 profiles readable**
(clubsim / giffgaff / Vodafone DE / Top_Connect / Lifecell / ChinaTelecom Macau / WEBBING, with ICCID and state).

## eSIM switching: ES10c EnableProfile / DisableProfile

Switching uses `BF31` (enable) / `BF32` (disable), byte-exact with lpac `es10c.c`:

```
BF31 <len> A0 <len> 4F <len> <ISD-P AID> 81 01 00     ← refreshFlag = FALSE
Response: BF31 <len> 80 01 <code>                      ← 0 = success
```

Real-hardware results (9eSIM):

- Enable a disabled profile → `Code=0 success`; the eUICC **auto-disables** the currently
  enabled profile (single-active card);
- Re-enable the original profile → `Code=0 success`;
- Disable an already-disabled profile → `Code=2` (invalidProfile, normal for this card).

**Take effect**: after writing, the modem's SIM session does not refresh automatically —
a `AT+CFUN=1,1` module reboot (~40 s) is required. The app asks for confirmation, then reboots,
waits for ports, reconnects and re-reads the profile list.

## Connection strategy

The QDC507 exposes 6 serial ports (COM4~COM9); the AT port drifts between COM6/COM7 and
COM8/COM9 are Bluetooth virtual ports (`BTHENUM`, filtered out). Pipeline:

| Scenario | Latency | Notes |
|---|---|---|
| Cold start (no cache) | ~600ms | Two-phase: known port first → full parallel scan on miss |
| Warm start (port cache) | ~60ms | Last good port persisted at `%APPDATA%\CellPort\settings.json` |

Failure logs: `%APPDATA%\CellPort\last-connect.log` (full `[1/5]`~`[5/5]` trace).

## Build & run

Requirements: Windows 10+, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0),
Quectel USB driver (usually bundled with Windows).

```bash
git clone https://github.com/xmgzxmgz/CellPort.git
cd CellPort
dotnet build CellPort.sln -c Release
dotnet run --project src/CellPort.App
```

Or `scripts/publish.ps1` for double-config packaging; `dotnet test` for the unit tests.

> Tip: launching directly triggers auto-connect; `CellPort.exe --page esim` opens a specific page
> (sms / calls / device / esim / console / settings).

## Roadmap

| Item | Status | Notes |
|---|---|---|
| Profile delete / nickname edit | ✅ v1.0.1 | ES10c `BF33` / `BF29` |
| USSD / phonebook / SMS export | ✅ v1.0.1 | |
| Runtime logs / update check | ✅ v1.0.1 | |
| **Profile download (ES10b + SM-DP+)** | ⏸ deferred | Requires a real SM-DP+ server to validate the protocol implementation; shipping an unverifiable implementation would be irresponsible — will land once a testable SM-DP+ is available |

## Known limitations

- **EID**: both SGP.22 `BF3E` and SGP.02 `GET DATA 9F7F` are rejected by ISD-R (`6A80` / `6D00`)
  on early-spec cards like 9eSIM — the card does not provide it; CellDock can't read it either.
  Profile management is unaffected.
- Module reboot (or power cycle) is required after switching for the new profile to take effect —
  modem-side limitation; the app guides and auto-reconnects.
- The data interface (MI_04, ECM) is handled by Windows; the app does not intervene.

## FAQ

**Q: Bottom-left shows "not connected" on start?**
Not a hardware fault. The app reuses the last good port (warm ~60ms) first, then falls back to a full scan;
check `%APPDATA%\CellPort\last-connect.log` or click "Connect".

**Q: EID shows "not provided by this card"?**
EID and profile listing are independent paths; the message only means EID is unreadable
(common on SGP.02 cards). Profile list and switching still work.

**Q: Other modules supported?**
Anything EG25-G / EC25-based with `AT+CCHO` / `AT+CGLA` firmware support should work;
verify with `AT+CCHO=?` in the AT console.

## Acknowledgements

- [CellDock for Mac](https://github.com/celldock/celldock-for-mac) — the blueprint and inspiration
- [lpac](https://github.com/estkme-group/lpac) — eUICC protocol reference (ES10x / BER-TLV)

## License

[MIT](LICENSE)
