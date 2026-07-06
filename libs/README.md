# libs/ — proprietary Dyno Dynamics assemblies (not committed)

These three DLLs are **not** included in this repository — they are Dyno Dynamics'
copyrighted binaries and are not redistributable. The build references them via
`HintPath` in `DynoDynamicsCanFeeder.csproj`, so you must supply your own copies
from a licensed **DigitalDyno** installation before building.

Copy them here from your install (default path shown):

| File            | Source (typical) |
|-----------------|------------------|
| `DynoDotNet.dll`| `C:\Program Files (x86)\Dyno\DynoClientECU\DynoDotNet.dll` |
| `Lib_ECU.dll`   | `C:\Program Files (x86)\Dyno\DynoClientECU\Lib_ECU.dll` |
| `Lib_CAN.dll`   | `C:\Program Files (x86)\Dyno\DynoClientECU\Lib_CAN.dll` |

They target the .NET Framework 2.0 CLR, which is why the feeder targets **net48**
(they will not load on .NET 8). See the project README for details.
