# 05 – NuGet Libraries (Step 7)

Criteria: compatible with .NET Framework 4.8, runs on both x86 and x64. Managed libraries are preferred to reduce native DLL loading risk.

| Package | Used in | Purpose | Native? | Compatibility notes |
|---|---|---|---|---|
| `BouncyCastle.Cryptography` 2.5.1 | Shared | AES-256-GCM, PBKDF2; from Phase 4: X25519, Ed25519, HKDF | No | .NET 4.8 has no `AesGcm`; netstandard2.0 has no `Rfc2898DeriveBytes` with SHA-256 |
| `System.Security.Cryptography.ProtectedData` | Shared | DPAPI | No | Windows only |
| `System.Data.SQLite.Core` 1.0.119 | Data | SQLite | **Yes**: `x86\` and `x64\SQLite.Interop.dll` | Includes both architectures. Embeds SQLite 3.46.1, which lacks the CVE-2025-6965 fix, see `07-BaoCaoKiemThu.md` |
| `NLog` | Shared, Data, Controller | Logging | No | Supports .NET Framework |
| `ClosedXML` | Data | Excel Import/Export | No | Pulls in the OpenXML SDK |
| `Microsoft.Data.Sqlite` 10.0.14 | Server | The Relay's device registry, separate from the Controller's database | Yes (bundled with the package) | Server only, does not affect older Windows |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | Tests | Testing | No | Run with `vstest.console` using `/Platform:x86` or `x64` |

**No ORM:** repositories are written in plain ADO.NET with parameterization; fewer dependencies and easier to control on older Windows.

**Charts:** drawn with GDI+, no added library.

**To evaluate in later phases:** Media Foundation via P/Invoke for H.264 (no added NuGet if possible); Inno Setup or WiX for the installer (this machine already has WiX 5.0.2).

The exact versions are recorded in each `.csproj`, and real compatibility test results are in `07-BaoCaoKiemThu.md`. Compatibility with Windows 7 and Server 2012 is based only on the packages' target frameworks and **has not been tested** on those operating systems.
