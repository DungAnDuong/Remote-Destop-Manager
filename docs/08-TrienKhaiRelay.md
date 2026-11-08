# 08 – Relay Server Deployment (Registry and Online/Offline)

Written for system administrators. The Relay registers devices, reports Online/Offline, and forwards sessions between machines that cannot reach each other directly. The Relay only sees ciphertext; it cannot read the session. The full customer-facing guide is in `RELAY-DEPLOYMENT.md`.

## 1. Requirements

- Windows Server or Windows x64 with the .NET 10 ASP.NET Core Runtime. Running on Linux has not been tested.
- Open a TCP port for devices and the Controller, 7443 by default.

## 2. TLS certificate

The Relay does not run without a certificate. Put the PFX password in the `RDM_CERT_PASSWORD` environment variable, not in a configuration file.

**With a domain and a CA certificate:** use that PFX file; leave the pinned key empty on the Client and Controller. ECDSA P-256 is recommended if you have Windows 7 machines.

**On an internal network:** generate a self-signed certificate. The hostname must match the name the Client and Controller use to connect.

```powershell
$env:RDM_CERT_PASSWORD = '<password>'
.\RDM.Server.exe --generate-cert C:\RDM\relay.pfx relay.company.local 825
```

The command prints a 64-character **pinned key**. Enter it in the Settings of every Client and Controller. When you replace the certificate with a new key, update the pinned key everywhere.

## 3. Tokens

Generate long random strings for the Controller token and the device enrollment code, then hash them. The command reads from stdin so the token stays out of the command-line history.

```powershell
.\RDM.Server.exe --hash-token   # type the token, press Enter, receive the hash
```

Write the hash into `appsettings.json`; the plaintext is only entered in the Controller or Client.

| Configuration key | Meaning |
|---|---|
| `Relay:CertificatePath` | Path of the PFX file |
| `Relay:ControllerTokenSha256` | If empty, every Controller is refused |
| `Relay:EnrollmentTokenSha256` | If empty, **anyone can register a new device and use the Relay's bandwidth**. **Must be set when open to the Internet.** A registered device can request a session with any Remote ID, but still needs the password or the owner's acceptance |
| `Relay:MaxAuthFailures`, `Relay:AuthBlockMinutes` | Block an IP after this many failed authentications, 20 attempts and 15 minutes by default. Many machines behind one NAT address are blocked together |
| `Relay:MaxConnections` | Maximum simultaneous connections |
| `Relay:MaxRelaySessions` | Maximum simultaneous relayed sessions, 1000 by default. A configuration limit, not a tested capacity |
| `Relay:TicketSeconds`, `Relay:RelayIdleSeconds`, `Relay:SessionRequestsPerMinute` | Ticket lifetime 30 seconds; close a session after 120 seconds without data; 30 requests per minute per device |

## 4. Configuring the Client and Controller

- **Every RDM machine:** the Settings button in the main window → Relay Server, Port, pinned key, enrollment code.
- **Admin machines, additionally:** Device Management → Settings → Host, Port, Controller token, pinned key. Optionally auto-add new devices.

## 5. Check

`GET http://127.0.0.1:7080/health` returns the number of Online devices and Controllers; this endpoint only listens on loopback. Do not expose it to the Internet.

## 6. Backup

Back up the `%ProgramData%\RDM\Relay\rdm-relay.db` file (changeable via `Relay:DatabasePath`). Uninstalling does not delete it. If this file is lost, the registry is empty: Remote IDs are no longer tied to their keys, and anyone could register ahead of the former owner if no enrollment code is set.

## 7. Direct and relayed

RDM tries direct first via the addresses the Relay provides, waiting 2 seconds per address, on the default port 7444 (changeable in Settings). The RDM installer opens the firewall on the Domain and Private networks. If that fails, both machines connect out to the Relay using single-use tickets. There is no TCP hole punching, so two machines behind two routers normally go through the Relay. Entering an IP in the main window always goes direct, in the form `address:port` for a non-default port. The address the Relay provides is not trusted: machine identity is verified by the pinned key.

## 8. Not available yet

TCP hole punching, revoking devices from the interface, Server Manager, firewall policy, a persistent Relay audit log (currently only logs to the console).

Running the Relay as a Windows service is coded (`UseWindowsService`) but **not tested**; the steps are in `RELAY-DEPLOYMENT.md`.
