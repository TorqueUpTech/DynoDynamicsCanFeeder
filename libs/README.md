# libs/ — Dyno Dynamics assemblies

The three DLLs the feeder references at build/run time:

| File             | Origin |
|------------------|--------|
| `DynoDotNet.dll` | Dyno Dynamics `DynoServer` producer/consumer library |
| `Lib_ECU.dll`    | Dyno Dynamics `DynoClientECU` (`ECU_Packet` etc.) |
| `Lib_CAN.dll`    | Dyno Dynamics `DynoClientECU` CAN interface |

They are **Dyno Dynamics' proprietary IP** and are **not in this repository** (`libs/*.dll` is
gitignored). Copy them from a DigitalDyno install (DynoServer / DynoClientECU folders) into this
folder before building. Never commit or redistribute them.

They target the .NET Framework 2.0 CLR, which is why the feeder targets **net48** (they will not
load on .NET 8).
