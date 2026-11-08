# Remote Desktop Manager (RDM)

Remote computer control software for enterprise IT administration.

## Status

**Not a finished product yet.** Phases 1 through 6 have working code: E2EE sessions, screen viewing and control, direct connection with Relay fallback, file transfer, chat, and a two-pane file manager. **Real mouse/keyboard input, the Windows Service, and connections over the real Internet/NAT have not been fully verified** — all Relay testing so far has run over loopback; see the [test report](docs/07-BaoCaoKiemThu.md). There is no TCP hole punching yet. Installers exist but are not code-signed.

| Phase | Scope | Status |
|---|---|---|
| 1 | Foundation, Controller UI, client/group management, Import/Export | Done |
| 2 | SQLite, encryption, DPAPI, PBKDF2, Key Management, Audit Log, Backup/Restore | Done |
| 3 | RDM Client, Remote ID, registration, Heartbeat, Online/Offline, access authorization | Done |
| 4 | Direct session, E2EE, screen capture, mouse/keyboard, Windows Service | Code complete; pending real input and Service testing |
| 5 | Data Relay, NAT traversal, Server Manager | Code complete; loopback-tested only |
| 6 | File Transfer, Chat, File Manager, remote file operations | Code complete; pending real-machine testing |
| 7 | Cross-platform testing, installers | In progress (installers built, not signed) |

Real-world test results: [docs/07-BaoCaoKiemThu.md](docs/07-BaoCaoKiemThu.md).

### UAC / lock-screen support (experimental)

An optional component lets the controller see the UAC prompt and the lock screen of
the remote machine. It consists of two standalone projects — `RDM.DesktopAgent`
(captures the active input desktop, including the secure desktop when run as SYSTEM)
and `RDM.HostService` (a LocalSystem Windows service that launches and supervises the
agent in the active session). These are **not yet wired into the shipping app or the
installer**; they are built and bundled separately for validation.

## Build requirements

- Visual Studio 2022 with the .NET desktop workload (.NET Framework 4.8 targeting pack).
- .NET SDK x64 with .NET 10 (for RDM.Server).

## Build and test

```powershell
.\build\build.ps1 -Configuration Release -Test   # Build and test both x86 and x64
.\build\verify.ps1 -OutDir C:\temp\rdm-verify     # Acceptance: 4 configurations, runs the real exe
```

When opening in Visual Studio, pick the `x86` or `x64` configuration; the solution has no Any CPU.

**Note:** the scripts use an explicit path to the x64 dotnet because, on the current
machine, dotnet x86 comes first in PATH and cannot run the SDK.

## Installers

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build-installer.ps1    # Build 3 installers into artifacts\installers
powershell -ExecutionPolicy Bypass -File .\build\verify-installer.ps1   # Verify without installing on the machine
```

Installers are not code-signed and **have never been run against a real install**.
The icon is generated from `icon.png` by `build\make-icon.ps1`. Details in
[section 3c of the report](docs/07-BaoCaoKiemThu.md).

## Real-desktop testing

Run while you are at the machine with no work in progress. The mouse will move for about 10 seconds.

```powershell
.\build\real-desktop-test.ps1 -Platform x64
.\build\real-desktop-test.ps1 -Platform x86
```

## Run

```powershell
.\src\RDM.App\bin\x64\Release\RDM.exe
```

The main window opens immediately — no sign-in is needed for a quick connection. The
**Device Management** area prompts you to create an Administrator account on first
use; there is no default password. Data lives in `%LOCALAPPDATA%\RDM\`. Set
`RDM_DATA_DIR` and `RDM_CLIENT_DATA_DIR` to use different folders for testing.

For Online/Offline status you need a running Relay Server and a configured Client and
Controller: see [docs/08-TrienKhaiRelay.md](docs/08-TrienKhaiRelay.md).

## Layout

| Folder | Contents |
|---|---|
| `src/RDM.Shared` | Models, crypto, `KeyManager`, session protocol and transfers |
| `src/RDM.Data` | SQLite, repositories, migrations, backup, business services |
| `src/RDM.App` | The `RDM.exe` program, TeamViewer-style main window |
| `src/RDM.UI` | Theme, shared controls, icons, chat panel |
| `src/RDM.Controller` | Controller role: management UI, remote desktop and file manager sessions |
| `src/RDM.Client` | Controlled role: screen capture, input, Service |
| `src/RDM.Server` | Relay Server: Device Registry, Heartbeat, Online/Offline |
| `src/RDM.DesktopAgent` | Experimental secure-desktop capture agent (UAC/lock screen) |
| `src/RDM.HostService` | Experimental LocalSystem service that launches the agent |
| `tests/RDM.Tests` | Unit, database, security, UI tests |
| `docs` | Design (01–06), test report (07), Relay deployment (08) |
| `build` | Build, test, and acceptance scripts |

## Libraries

See [docs/05-NuGet.md](docs/05-NuGet.md).
