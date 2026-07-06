# Dyno Dynamics CAN Feeder

Bridges a live CAN bus (**IXXAT** or **OBDX**) into **Dyno Dynamics DigitalDyno**, decoding
user-chosen DBC signals (engine **RPM**, wideband **lambda**, anything in a DBC) and republishing
them to DynoServer as an "ECU" producer — the same role `DynoClientECU.exe` plays, but sourced
from any DBC-described device instead of a hard-coded per-brand protocol.

> **Build prerequisite:** three proprietary Dyno Dynamics assemblies
> (`DynoDotNet.dll`, `Lib_ECU.dll`, `Lib_CAN.dll`) are **not** included — copy them from a
> licensed DigitalDyno install into `libs\`. See [`libs/README.md`](libs/README.md).

## How it works

```
CAN bus --> IXXAT / OBDX (UltimateCANScanner adapters) --> DbcDecoder --> signal routes
        --> ECU_Packet --> ProducerConnection (DynoDotNet.dll) --> DynoServer --> DigitalDyno
```

The dyno multiplies the raw u16 we send by the channel's `Scale` (+`Offset`) from
`sys_ecuchanneldef`, so `feeder.config.json` route Scale/Offset must match the DB rows.

## Reused code (do not diverge — update from source project)

Copied from `D:\Projects\UltimateCANScanner\src`:
`Can/ICanAdapter.cs`, `Can/CanBusService.cs` (IXXAT VCI4), `Can/CanFrame.cs`, `Can/CanDeviceInfo.cs`,
`Can/Obdx/*` (OBDX DVI, USB/WiFi), `Decoding/DbcDecoder.cs`, `Decoding/DbcMessageInfo.cs`.

## Build (on the dyno PC)

Prereqs: .NET SDK (builds net48), IXXAT VCI4 driver installed (`C:\Program Files\HMS\Ixxat VCI`).

```
cd D:\Projects\Dyno\DynoDynamicsCanFeeder
dotnet build -c Release
```

### Porting notes — VERIFY on first build (net48 vs the net8 source)

These are the known deltas from the net8 UltimateCANScanner sources; fix if the compiler flags them:
- **OBDX BLE** removed. `Can/Obdx/BleObdxTransport.cs` here is a throwing net48 stub (BLE needs
  WinRT). Use OBDX over USB (`serial:`) or WiFi (`tcp:`).
- **J2534** dropped. `AdapterFactory.cs` replaces UCS's `CanAdapters.cs` (IXXAT + OBDX only).
- Watch for net5+ only APIs in the copied files (e.g. `Enum.GetValues<T>()`), range/index on
  arrays, and `record struct` — all OK under `<LangVersion>latest`, but confirm the build is clean.
- `System.IO.Ports` / `System.Text.Json` come via NuGet (netstandard2.0 assets) for net48.

## Use (GUI)

Just run `DynoDynamicsCanFeeder.exe` — the WinForms GUI exposes every option:
- **Adapter** (IXXAT / OBDX) and **Device / endpoint** (Refresh enumerates; for OBDX WiFi type
  `tcp:<ip>:<port>` directly).
- **Bit rate**, **Dyno server** (blank = auto-discover), **DBC file** (Browse).
- **Signal -> channel routes** grid (add manually, or double-click a name in the **DBC signals**
  list). Set Channel/Scale/Offset per the DB rows; tick **Tacho** for the RPM channel.
- **Live decoded values** + **Status log** show what's flowing once you press **Start**.
- **Load/Save Config** read and write the same `feeder*.config.json` files.

Headless (unattended): `DynoDynamicsCanFeeder.exe --headless feeder.aem.config.json`.

## Configure (files)

The shipped `feeder*.config.json` presets (Haltech / AEM / ECOtrons / GM RPM / OBDX-WiFi) can be
loaded from the GUI or edited by hand: adapter, bitrate, DBC path, and signal->channel routes.

## Register the ECU in DigitalDyno

Preferred — **in-app editor** (no SQL): click **Database...** in the main window (or run
`DynoDynamicsCanFeeder.exe --dbeditor`). It:
- Auto-locates `Documents\<ver>\databases\dyno-e.db` (or Browse), lists existing ECUs.
- **New ECU** pre-fills "CAN DBC Feeder" with a free Protocol ID (>= 20).
- **Import from feeder routes** fills the channel table from your routes (channel #, scale,
  offset, guessed unit) and sets the Tacho channel.
- **Save to database** backs up dyno-e.db, then inserts/updates the ECU and replaces its channels
  in one transaction. Close DigitalDyno first; restart it to see the new ECU, then select it.

Fallback — `feeder-ecu.sql` (edit `UnitID`s to real `sys_unit` ids, then
`sqlite3 dyno-e.db < feeder-ecu.sql`). Units: rpm=161, Lambda=280, AFR=290.

## Run & verify (needs hardware)

1. Start DynoServer + DigitalDyno; select the feeder ECU.
2. `DynoDynamicsCanFeeder.exe feeder.config.json` — console shows `[server] register: Succeeded` and
   `[can] ... on <device>`.
3. Rev the engine / apply the wideband; confirm RPM + lambda update live in DigitalDyno.
4. Cross-check raw CAN with UltimateCANScanner to confirm ids/scaling before trusting values.

## Status

**Compiles clean** (net48 x64, 0 warnings) against the real DynoDotNet.dll/Lib_ECU.dll +
Ixxat.Vci4/DbcParserLib/System.IO.Ports. The net48 down-level fixes are done (PolySharp for
Index/Range/init; manual Math.Clamp/Convert.ToHexString/Dictionary.Remove/array-slice edits).

**Source DBCs authored + verified** (`tools/DbcVerify`, no hardware needed) — all in `DBC/`:
`AEM_X-Series.dbc`, `ECOtrons_ALM.dbc`, `Haltech_WB2.dbc` (use this, NOT the vendor
`Haltech_dash.dbc`, which mis-decodes under the big-endian convention). The GM `ECM_HS.dbc` is a
vendor DBC and is **not** shipped here — drop your own copy into `DBC/` to use `feeder.gm-rpm.config.json`
(DbcVerify skips its check when absent). Configs shipped: `feeder.config.json` (Haltech RPM+lambda),
`feeder.aem.config.json`, `feeder.ecotrons.config.json`, `feeder.gm-rpm.config.json`,
`feeder.aem-obdx-wifi.config.json`. Re-run `cd tools/DbcVerify && dotnet run` after editing any DBC.

**Only remaining step: hardware verification** — the dyno PC with the VCI/OBDX driver, DynoServer
running, and a live bus. One feeder instance handles one bus/DBC; run multiple instances (or merge
DBCs) to pull RPM + wideband from different buses at once.
