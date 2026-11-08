# 02 – Architecture and Data Flow (Steps 3–4)

## 0. Architecture change: a single TeamViewer-style program

By the project owner's decision, the Controller and Client are merged into **one program, `RDM.exe`**, replacing the two separate programs in Sections 3, 14 and 15 of the specification. The main window has two parts: "Allow control" and "Control another machine". A quick connection needs no RDM sign-in; the device-management part opens in its own window and requires sign-in.

The code still separates responsibilities: `RDM.Client` and `RDM.Controller` are libraries, `RDM.UI` holds shared interface pieces, and `RDM.App` is the runnable program. The sections below still say "Controller" and "Client" by the machine's **role** in a session.

Finding a machine on the LAN: `RDM.exe` answers UDP 7445 when the Remote ID matches. The returned address is only a hint; identity is verified by keys in the E2EE handshake.

## 1. Overview

```mermaid
flowchart LR
  subgraph CTRL[Admin machine]
    C[RDM Controller<br/>.NET 4.8 WinForms]
    DB[(RDM_Database.db<br/>SQLite)]
    C --- DB
  end
  subgraph VPS[VPS / Windows Server]
    R[RDM Relay Server<br/>ASP.NET Core]
    RDB[(Device registry<br/>no session keys)]
    R --- RDB
    M[Server Manager] --> R
  end
  subgraph TARGET[Controlled machine]
    S[RDM Client Service<br/>Session 0]
    A[RDM Client Agent<br/>user session]
    S -- Named Pipe with ACL --> A
  end
  C -- "TLS 1.2+ (coordination)" --> R
  A -- "TLS 1.2+ (enrollment, heartbeat)" --> R
  C <-. "E2EE: P2P if possible" .-> A
  C <== "E2EE over Relay<br/>(Relay sees only ciphertext)" ==> R
  R <== "E2EE over Relay" ==> A
```

## 2. Per-project responsibilities

| Project | Framework | Platform | Responsibility | Dependencies |
|---|---|---|---|---|
| RDM.Shared | netstandard2.0 | x86, x64 | Models, DTOs, Protocol, Security (crypto, KeyManager) | references no other project |
| RDM.Data | net48 | x86, x64 | SQLite, Repository, Migration, Backup, data business services | Shared |
| RDM.UI | net48 WinForms | x86, x64 | Theme, owner-drawn controls, icons | references no other project |
| RDM.Controller | net48 library | x86, x64 | Controller role: management UI, control session | Shared, Data, UI |
| RDM.Client | net48 library | x86, x64 | Controlled role: screen capture, input, Service | Shared, UI |
| RDM.App | net48 WinForms, `RDM.exe` | x86, x64 | Runnable program, main window | all of the above |
| RDM.Server | ASP.NET Core | x64 | Registry, authentication, coordination, relay | Shared |
| RDM.Tests | net48 xUnit | x86, x64 | Unit, Database, Security, Network Tests | Shared, Data, Controller |

Dependency rule: UI → Service → Repository → SQLite. Forms do not write SQL. Permissions are checked at the Service layer, not just by hiding a button on the UI.

**Framework choice for the Relay:** the spec allows ASP.NET Core. The proposal is **.NET 10 LTS**, because .NET 8 reaches end of support on 2026-11-10.

## 3. Enrollment and Online/Offline flow

```mermaid
sequenceDiagram
  participant A as Client Agent
  participant R as Relay
  participant C as Controller
  A->>A: First time: generate an Ed25519 key and a Remote ID
  A->>R: TLS: Hello
  R-->>A: Challenge(random nonce)
  A->>R: DeviceAuth(RemoteID, PublicKey, Sign(nonce, RemoteID, PublicKey), device info)
  Note over R: First time: bind RemoteID to PublicKey.<br/>After that: a different key is refused (ID takeover protection).
  R-->>A: AuthResult(Ok, Heartbeat interval)
  loop Every 15 seconds
    A->>R: Heartbeat(AccessEnabled)
    R-->>A: HeartbeatAck
  end
  Note over R: More than 45 seconds without a Heartbeat is Offline
  C->>R: TLS: ControllerAuth(token), SubscribeAll
  R-->>C: PresenceSnapshot (all registered devices)
  R-->>C: PresenceChanged(RemoteID, Online/Offline, AccessEnabled)
```

**Changed from the original design:** devices authenticate by signing a nonce instead of using a DeviceToken. Reason: there is no token left to steal, the signature cannot be replayed, and the Relay only keeps the public key.

**Enrollment protection:** set `Relay:EnrollmentTokenSha256` so only devices with the code can enroll. Left empty, anyone who connects to the Relay can enroll a new Remote ID, so it should be set when the Relay is open to the Internet.

**Scope of one Relay:** one Relay serves one organization. A Controller with the token sees every device; there is no multi-tenant separation yet.

## 4. Connection flow (Quick Connect)

```mermaid
sequenceDiagram
  participant U as Administrator
  participant C as Controller
  participant R as Relay
  participant A as Client Agent
  participant O as Machine owner
  U->>C: Enter Remote ID, password, CONNECT
  C->>R: 1-2. SessionRequest(RemoteID, query only)
  R-->>C: SessionTicket(status, internal IP, public IP, port)
  C->>A: Try each address directly, wait 2 seconds
  opt Direct fails
    C->>R: SessionRequest(RemoteID, request relay)
    R->>A: IncomingSession(target's ticket)
    R-->>C: SessionTicket(Controller's ticket)
    C->>R: RelayJoin(ticket)
    A->>R: RelayJoin(ticket)
    R-->>C: RelayReady
    R-->>A: RelayReady
  end
  C->>A: 3. E2EE handshake (see 04-Security.md), identical on both paths. Compare key against the pinned key
  C->>A: Channel-bound password proof
  alt Unattended not enabled
    A->>O: Ask: Accept / Reject
    O-->>A: Accept
  end
  A-->>C: 4. SessionAccepted + granted permissions
  C->>C: 5. Open Remote Desktop, 6. Write ConnectionHistory
  A->>O: Always show the "A control session is active" bar + Disconnect button
```

**Relay path design:**
- **Ticket:** 32 random bytes, one per side, single-use, expires after 30 seconds. The Relay issues two tickets as a pair, so sessions are never mismatched.
- **From RelayReady onward** the Relay only copies raw bytes between the two TLS connections; it does not parse the content.
- **Any machine can request a session,** no Controller token required: access is decided by the target machine via password or the owner's acceptance. The Controller token is only for viewing all device status.
- **A meaningful error does not trigger another path:** a wrong pinned key, wrong password, or owner rejection is the final answer. Only when the machine is unreachable, or the wrong machine is reached due to a duplicate internal IP across two networks, is the next path tried.
- **No TCP hole punching.** Beyond NAT it goes through the Relay, so Relay bandwidth is a real cost at deployment.

## 5. In-session data flow

```mermaid
flowchart LR
  subgraph Agent
    CAP[BitBlt] --> DIFF[64x64 tile diff] --> ENC[JPEG/PNG] --> SEAL1[AES-256-GCM]
    OPEN2[Decrypt] --> INP[SendInput]
  end
  subgraph Controller
    OPEN1[Decrypt] --> DEC[Decompress] --> VIEW[Draw screen]
    UI[Mouse, keyboard] --> SEAL2[AES-256-GCM]
  end
  SEAL1 == ciphertext ==> OPEN1
  SEAL2 == ciphertext ==> OPEN2
```

The channels (Screen, Input, File, Chat, Control) are multiplexed over one stream, with a credit window so a large file does not starve input.

The framing loop is designed not to block the UI: capture and network run on separate Tasks with a `CancellationToken`, and the Controller keeps only the latest frame, dropping old frames when the network is slow.

## 6. Session 0 Isolation

```mermaid
flowchart TB
  SVC[Windows Service - LocalSystem, Session 0] -->|WTSQueryUserToken + CreateProcessAsUser| AG[Agent in the console session]
  SVC -->|Session-change events: logon, logoff, RDP| SVC
  SVC -->|SendSAS| CAD[Ctrl+Alt+Del]
  AG -->|BitBlt, SendInput| DESK[User desktop]
  SVC -.->|Only when Unattended is enabled by the owner| AGW[Agent on the Winlogon desktop]
```

The Service does not capture or control the desktop itself. The Client still works without the Service installed (instant-support mode), but then there is no Unattended, UAC cannot be passed, and there is no Ctrl+Alt+Del.

## 7. Deployment sequence

| Phase | Scope | Current status in |
|---|---|---|
| 1–2 | Foundation, UI, Database, storage security | `07-BaoCaoKiemThu.md` |
| 3 | Client, Remote ID, enrollment, Heartbeat, Online/Offline, access authorization, plus the Relay's Device Registry. The Windows Service moves to Phase 4 | `07-BaoCaoKiemThu.md` |
| 4 | Direct session, E2EE, Screen Capture, mouse, keyboard, Windows Service. **Real mouse, keyboard and Service not yet tested** | `07-BaoCaoKiemThu.md`, section 3b |
| 5 | Data Relay, Internet connection, concurrent sessions. **Loopback-tested only; no TCP hole punching** | `07-BaoCaoKiemThu.md`, section 3d |
| 6–7 | File Transfer, Chat, extensions, final packaging | Not started |
