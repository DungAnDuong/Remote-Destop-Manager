# RDM Relay Server: Deployment Guide

For system administrators. The Relay registers devices, reports Online/Offline status and carries remote sessions between computers that cannot reach each other directly, for example across the Internet.

**The Relay cannot read sessions.** They are end-to-end encrypted between the two computers; it only forwards ciphertext. Computers only make outbound connections to it, so they need no open inbound port and no router configuration.

## Quick deployment with built-in settings

Use this when you build RDM yourself and want installers that need no configuration on any computer. Sections 2 to 5 describe the manual procedure instead.

1. **Create the keys once**, from the source folder: `.\build\relay-init.ps1 -RelayHost <dns-name> -Port <port>`. This creates `deploy\relay\` (not in git). **Back it up privately:** if `relay.pfx` is lost or replaced, every computer needs a new installer.
2. **Build the installers:** `.\build\build-installer.ps1`. Release builds of RDM then include the host, port, pinned key and enrollment code.
3. **Install the Relay** on the server, from an elevated PowerShell using the same Windows account: `.\build\deploy-relay.ps1`. It installs a `RDMRelay` service running as LocalService and opens only that TCP port in Windows Firewall. It does not change other services.
4. **On the router,** forward that external TCP port to the server.
5. **Install `RDM_Setup_x64.exe` or `RDM_Setup_x86.exe`** on each computer. These builds always use the built-in settings and show no Relay information or settings anywhere in the interface. To move to another Relay, build and install a new version.

The enrollment code ships inside the installer, so anyone who has the installer can extract it. It stops casual registration, not a determined person: keep the installers private. To revoke it, you must change the code and reinstall every computer.

## 1. Requirements

- Windows Server 2016, Windows 10 or newer, 64-bit. The installer includes the .NET runtime.
- Open the TCP port used by devices, 7443 by default. The installer does not open it.
- Reachable from every computer: a public address or DNS name if computers connect from the Internet.
- Bandwidth for all relayed sessions. Usage depends on screen size and activity and has not been measured on real networks, so measure it before planning capacity.

## 2. TLS certificate

The Relay does not run without a certificate. Put the PFX password in the `RDM_CERT_PASSWORD` environment variable, not in a configuration file.

**With a domain and a CA certificate:** use that PFX file and leave the pinned key empty on every computer. ECDSA P-256 is recommended if you have Windows 7 computers.

**On an internal network:** generate a self-signed certificate. The host name must match the name computers use to connect.

```powershell
$env:RDM_CERT_PASSWORD = '<password>'
New-Item -ItemType Directory -Force C:\RDM
.\RDM.Server.exe --generate-cert C:\RDM\relay.pfx relay.company.local 825
```

The command prints a 64-character **pinned key**. Enter it in the settings of every computer. If you replace the certificate with a new key, update it everywhere.

## 3. Tokens

Generate long random strings for the Controller token and the device enrollment code, then hash them. The command reads from standard input so the token stays out of the command history.

```powershell
.\RDM.Server.exe --hash-token   # type the token, press Enter, copy the hash
```

Put the hashes in `appsettings.json`. Enter the original values only in RDM.

| Setting | Meaning |
|---|---|
| `Relay:CertificatePath` | Path of the PFX file |
| `Relay:ControllerTokenSha256` | If empty, every Controller is refused |
| `Relay:EnrollmentTokenSha256` | If empty, **anyone can register a new device and use your Relay's bandwidth**. **Set this when the Relay is reachable from the Internet.** Any registered device can request a session with any Remote ID, then still needs that computer's password or its owner's approval |
| `Relay:MaxAuthFailures`, `Relay:AuthBlockMinutes` | Blocks an IP after failed authentications, 20 attempts and 15 minutes by default. Computers behind one NAT address are blocked together |
| `Relay:MaxConnections` | Maximum simultaneous connections |
| `Relay:DatabasePath` | Device registry, `%ProgramData%\RDM\Relay\rdm-relay.db` by default |
| `Relay:MaxRelaySessions` | Maximum simultaneous relayed sessions, 1000 by default. This is a limit, not a tested capacity |
| `Relay:TicketSeconds` | How long a session ticket stays valid, 30 by default |
| `Relay:RelayIdleSeconds` | Closes a relayed session after this many seconds without data, 120 by default |
| `Relay:SessionRequestsPerMinute` | Requests allowed per device, 30 by default. One connection uses up to 2 |

## 4. Firewall and start

```powershell
New-NetFirewallRule -DisplayName "RDM Relay" -Direction Inbound -Protocol TCP -LocalPort 7443 -Action Allow
.\RDM.Server.exe
```

### Run as a Windows service

Running from a console stops the Relay when its window closes or you sign out. To keep it running across reboots, from an elevated PowerShell:

```powershell
sc.exe create RDMRelay binPath= "C:\Program Files\RDM\Relay Server\RDM.Server.exe" start= auto
# Service key values are readable by local users: use this only on a server where every local user is trusted.
New-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\RDMRelay -Name Environment -PropertyType MultiString -Value "RDM_CERT_PASSWORD=<password>"
sc.exe start RDMRelay
```

Set `Relay:CertificatePath` to an absolute path. **This procedure has not been tested yet**: check `/health` after starting, and restart the computer once to confirm that the service comes back.

## 5. Configure RDM

- **Every computer:** Settings button in the main window: Relay Server, Port, pinned key, enrollment code.
- **Admin computers, additionally:** Device Management, Settings: Host, Port, Controller token, pinned key.

## 6. How computers connect

1. RDM asks the Relay whether the Remote ID is online and for its addresses.
2. It tries a direct connection first, waiting 2 seconds per address.
3. If that fails, both computers connect out to the Relay using single-use tickets, and the session is relayed. The Remote Desktop status bar shows `via Relay` or `via Direct`.

RDM does not perform NAT hole punching: computers behind different routers normally use the Relay. Entering an IP address in the main window always connects directly, with `address:port` for a non-default port.

## 7. Check

`GET http://127.0.0.1:7080/health` returns the number of online devices and Controllers, active relayed sessions and bytes relayed. It listens on loopback only; do not expose it to the Internet.

## 8. Backup

Back up `rdm-relay.db`. Uninstalling does not delete it. If it is lost, the registry is empty: Remote IDs are no longer tied to their keys, and anyone could register one first unless an enrollment code is set.

## 9. Not available yet

NAT hole punching, revoking devices from a user interface, Server Manager, firewall policy, and a persistent Relay audit log (the Relay currently logs to the console or the service output).
