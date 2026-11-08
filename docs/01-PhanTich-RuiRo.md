# 01 – Requirements and Compatibility Risk Analysis (Steps 1–2)

## 1. Scope summary

Three independent components: **Controller** (.NET Framework 4.8, WinForms, SQLite), **Client** (.NET Framework 4.8, Service + Agent in the user session), **Relay Server** (ASP.NET Core). The top priority is security (Section 8). Seven deployment phases run in sequence.

Because this product controls computers remotely, the **owner's consent** constraint is a functional requirement, not an option: the Client always shows a notice while a session is active, always offers an emergency disconnect, and Unattended Access is only enabled when the owner configures it deliberately. No hidden-run mode is built.

## 2. Specification points that need adjustment

The points below **conflict with platform reality**. The project owner needs to confirm the proposed handling.

| # | Specification | Reality | Proposal |
|---|---|---|---|
| A1 | Windows 8 (RTM) x86/x64: "Yes" | .NET Framework 4.8 **does not support** Windows 8 RTM (only 8.1 and later) | Drop Windows 8 RTM; require an upgrade to 8.1 (free). The installer refuses and states the reason |
| A2 | Windows Server 2012 → 2025 x86: "Yes" | From Server 2008 R2 onward Windows Server is **x64 only** | Read as: the x86 build *runs under WOW64* on x64 Server. The x64 build is recommended. Server Core may lack WOW64 |
| A3 | Windows 10 x86 | Yes, but Windows 11 has no x86 | As per the table; keep as is |
| A4 | TLS 1.3 "when the platform supports it" | Schannel only has TLS 1.3 from Windows 11 / Server 2022 | Choose dynamically per OS. Do not pass the `Tls13` flag on older OSes |
| A5 | AES-256-GCM on .NET Framework 4.8 | .NET Framework 4.8 **has no** `AesGcm` class | Use BouncyCastle.Cryptography (managed, runs on both x86/x64, no native files) |
| A6 | Full database encryption | The free `System.Data.SQLite` no longer supports encryption; SEE is paid; SQLCipher requires a driver change | Choose **per-field encryption** following the alternative branch the spec allows (Section 7). See `03-Database.md` |
| A7 | "Real testing" on 10 operating systems | Requires VMs/real machines; the current environment only has Windows 10 x64 | The report only records Pass for what actually ran. Other OSes are marked **"Not tested"** |
| A8 | Support validity | Windows 7/8.1/Server 2012 are already out of Microsoft support | Still supported per the spec but a warning is shown in the Security Center |

**A9. Language:** the project owner requires the entire software interface and all messages to be in English. Code comments and internal documentation stay in Vietnamese per Section 16. The document bundled with the installer is `docs/RELAY-DEPLOYMENT.md` in English; `docs/08-TrienKhaiRelay.md` is internal only.

## 3. Compatibility risk matrix

Level: **H**igh / **M**edium / **L**ow. Items marked "to verify" have not been confirmed on a real machine.

| Risk | Level | Impact | Mitigation |
|---|---|---|---|
| **TLS 1.2 on Windows 7 / Server 2012**: the ECDHE-RSA-GCM cipher suite is not available on Windows 7 | H | Cannot connect to the Relay | The Relay uses an **ECDSA P-256** certificate (ECDHE-ECDSA-AES-GCM is available on Windows 7). On older OSes, the installer checks for the required update. If the minimum is not met, it **refuses the Internet connection** rather than downgrading (to verify on a Windows 7 VM) |
| **Session 0 Isolation**: the Service cannot capture or control the desktop | H | No image, no input | The Service only supervises; it uses `WTSQueryUserToken` + `CreateProcessAsUser` to launch the Agent in the console session |
| **Secure Desktop / UAC**: BitBlt and SendInput cannot reach the Winlogon desktop | H | Image lost when UAC appears | The Agent runs as SYSTEM on the `Winlogon` desktop when Unattended; in normal mode it clearly reports "UAC is showing, waiting for the user at the machine" |
| **UIPI**: a normal-privilege Agent cannot send input to an elevated window | M | Loss of control over admin apps | As above; the limit is shown on the toolbar |
| **Ctrl+Alt+Del**: cannot be simulated with SendInput | M | Cannot send the SAS | `SendSAS` (sas.dll, Win7+) from the Service; requires the `SoftwareSASGeneration` policy. The installer asks permission to configure it |
| **SQLite native x86/x64**: loading the wrong `SQLite.Interop.dll` causes `BadImageFormatException` | H | The app does not start | Build per architecture; test x86 and x64 separately; the installer checks the files |
| **Missing VC++ runtime** on older Windows for `SQLite.Interop.dll` | M | DLL load error on a clean machine | To verify on a clean VM; added to the Phase 7 checklist |
| **Icon font**: `Segoe MDL2 Assets` is not on Windows 7 | M | Icons become empty boxes | Use `Segoe UI Symbol` (Windows 7 and later) |
| **DPI**: Per-Monitor V2 is not on Windows 7/8.1 | L | Blur when dragging across monitors of different DPI | Declare System DPI Aware and scale by DPI at startup |
| **BitBlt and layered / hardware-accelerated windows** | M | Missing overlay content, black video | Enable `CAPTUREBLT`; note the limit |
| **Disconnected RDP session**: the desktop does not render | M | BitBlt produces a black screen | Detect via `WTSGetActiveConsoleSessionId` and report the state |
| **BitBlt + JPEG performance** at 4K | M | Does not reach 15 FPS | Tile diff, send only changed regions, lower quality by bandwidth |
| **H.264** via Media Foundation: not on Server without Desktop Experience, and missing on N editions | M | Cannot encode | JPEG/PNG is the baseline; H.264 is only an option when detected |
| **Vietnamese keyboard**: Unikey/IME sends characters via Backspace + Unicode | M | Wrong diacritics | Normal characters sent with `KEYEVENTF_UNICODE`; function keys sent by scan code |
| **Alt+Tab, Windows key** captured by the Controller's own OS | M | Cannot reach the remote machine | A low-level keyboard hook while the Remote window is focused |
| **Symmetric NAT**: P2P fails | H | No direct connection | A Relay fallback is always available. E2EE applies to every transport |
| **Code signing**: unsigned files are easily blocked by SmartScreen/AV; remote-control software is often flagged by AV | H | The installer is blocked | A code-signing certificate is needed (EV if possible). Windows 7 needs KB4474419/KB4490628 to validate SHA-2 |
| **.NET 4.8 on Windows 7**: needs KB4019990 | L | Install failure | The installer checks for it |
| **DPAPI**: keys are bound to user/machine; losing the profile loses the keys | H | Credentials cannot be decrypted | Backups carry a password-protected re-wrapped copy of the key (see `04-Security.md`) |

## 4. Order of resolution

1. Build the testable core first (crypto, database), then the UI.
2. Put x86 and x64 testing into the build script from the start.
3. The E2EE protocol is designed now even though it is only coded in Phase 4, so the Relay does not need rework later.
