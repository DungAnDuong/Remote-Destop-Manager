# 07 – Test Report

**Run date:** 2026-11-05. **Scope:** Phases 1 to 5, version 0.6.0. **Reproduce:** `.\build\verify.ps1 -OutDir <folder>` (takes a few minutes, shows windows on screen; the script returns exit 1 if any configuration fails).

Convention per Section 18: record **Pass** only for what actually ran. Anything not run is marked **Not tested**, with no speculation.

## 1. Build and automated tests

Environment: Windows 10 Pro 22H2 x64 (build 19045), Visual Studio 2022 17.14.

| Configuration | Build | Errors | Tests passed | RDM.exe | SQLite native |
|---|---|---|---|---|---|
| Debug x86 | Success | 0 | 251/251 | x86 | x86 |
| Debug x64 | Success | 0 | 251/251 | x64 | x64 |
| Release x86 | Success | 0 | 251/251 | x86 | x86 |
| Release x64 | Success | 0 | 251/251 | x64 | x64 |

**Not included in the numbers above:** `RealDesktopTests` (real screen capture and real mouse/keyboard). This group has never run to completion, see section 3b. The session tests in the set of 241 use **simulated** screen and input.

**Remaining warning (1):** `NETSDK1233`, VS 2022 does not officially support .NET 10 for RDM.Server. The project owner has accepted this.

**Library vulnerabilities (`dotnet list package --vulnerable`):** no package is reported anymore. `build-installer.ps1` stops itself if any is found. **Scanners do not catch everything, see "Open security risks" in section 3c.**

**Correction:** earlier reports said "no NuGet vulnerability warnings". Since adding `Microsoft.Data.Sqlite` to the Server, that is no longer true: the build script only filtered errors, so it hid the `NU1903` warning about CVE-2025-6965. Patched on the Server; the `Server_RunsPatchedSqlite` test blocks any SQLite older than 3.50.2 from returning.

The Relay tests run the real `RDM.Server.exe` over real TCP and TLS on loopback, even when the test process is x86.

## 2. Operating system matrix

| Operating system | x86 | x64 |
|---|---|---|
| Windows 10 (build 19045) | **Partial Pass**: the x86 build runs under WOW64 on Windows x64; not yet run on real Windows 10 x86 | **Pass** |
| Windows 7 SP1 | Not tested | Not tested |
| Windows 8.1 | Not tested | Not tested |
| Windows 11 | Not applicable | Not tested |
| Server 2012, 2012 R2, 2016, 2019, 2022, 2025 | Not tested (WOW64) | Not tested |

**The biggest unverified risk:** TLS 1.2 from Windows 7 to the Relay. All TLS testing so far ran Windows 10 ↔ Windows 10 on loopback.

## 3. Running the real application

**`build\ui-smoke.ps1`** (Controller, Release build): create an account, sign in, open 12 pages, switch theme.

| Build | Result | Create account + sign in | RAM |
|---|---|---|---|
| x86 | Pass | 5.3 seconds | 57 MB |
| x64 | Pass | 4.3 seconds | 67 MB |

**`build\e2e-smoke.ps1`** (a real Relay Server and two real `RDM.exe`, operated through the interface, both x86 and x64):

| Step | Result |
|---|---|
| Server starts with a certificate made by `--generate-cert` | Pass |
| Client generates a Remote ID, configured via the Settings dialog, goes Online over TLS | Pass |
| Controller creates an account, configures the Relay, adds a device via the Add Client dialog | Pass |
| Controller shows Online, Dashboard counts correctly Online 1 / Offline 0 | Pass |
| Owner turns off Remote Access, Controller shows "Online (access disabled)" | Pass |
| Terminate the Client, Controller shows Offline | Pass |
| Stop the Relay, Controller shows "Unknown" and stays responsive | Pass |

This script checks via operations and processes; **displayed state is verified by looking at screenshots**: all steps on the x64 Debug build, and the Client Online/Offline steps on the x86 Release build. The script itself cannot read cell contents in a grid.

| Quick connect without sign-in; fingerprint dialog; a wrong entry is refused | Pass |
| Connect from the list: the key is pinned so the fingerprint is not asked again | Pass |
| Signing out of the management part returns to the main window; the program does not exit | Pass |
| Owner accepts on the Client, the Remote window opens, an E2EE session | Pass |
| The "Being controlled" bar shows on the target machine | Pass |
| Owner clicks "Disconnect now": the session closes, the Controller reports the reason and does not auto-reconnect | Pass |
| Active Sessions and Connection History record correctly | Pass (verified via screenshots on x64) |

**Not yet run through the real interface:** the file picker for Import/Export and Backup/Restore; changing the Client password via its dialog; Edit Client; DPI other than 100%; dragging the window across monitors of different DPI; Full Screen; changing the Monitor and quality from the toolbar. Most of the underlying logic already has tests at the Service layer.

## 3c. Installers (version 0.6.0)

Built with `.\build\build-installer.ps1`, into `artifacts\installers`. Checked with `.\build\verify-installer.ps1`: each MSI is extracted to a temp folder, **not installed on the machine**.

| Installer | Size | Checked on the extracted files |
|---|---|---|
| `RDM_Setup_x64.exe` | 7.8 MB | All files present, right architecture, right SQLite native, has an icon, opens the window |
| `RDM_Setup_x86.exe` | 7.6 MB | As above |
| `RDM_Server_Setup.exe` | 44.2 MB | All files present, self-contained runtime, generates a certificate, answers `/health` |

**The Server installer's launch condition, verified on Windows 10:** the inverse condition `VersionNT < v10.0` is correctly blocked, writing nothing to the machine.

**Limit of `/layout`:** this mode does not evaluate conditions, so exit 0 proves nothing about the launch condition.

**The project owner installed an earlier build:** `C:\Program Files\RDM\RDM.exe` is running on the development machine, so the actual install apparently succeeded. This is an observation, not a test; upgrading to 0.6.0 has not been tried.

**Not tested (requires Administrator and makes system changes):** a full install verified by a tester item by item, the installer UI, writing to Program Files, shortcuts, firewall rules, auto-removing the 0.4.0 build, upgrade, uninstall, install on a clean machine or Windows 7.

**Designed behavior:**
- The installer checks for .NET Framework 4.8 and the Windows version. It does **not** bundle .NET 4.8: if missing, it reports and stops.
- **The installer opens the firewall for RDM.exe on the Domain and Private networks**, not on Public. This is a security change on every machine installed.
- **The installer auto-removes the 0.4.0 Controller/Client** if present. Remote ID and data live in `%LOCALAPPDATA%\RDM` so they are kept.
- **The Windows Service is not installed** because it is untested. An administrator runs `RDM.exe --install-service` if needed.
- **The Relay Server only installs files**, it does not run itself as a Windows service.
- Uninstalling does not touch user data (`%LOCALAPPDATA%\RDM`, `%ProgramData%\RDM\Relay`), and the Server's `appsettings.json` is kept.

**No code signing yet.** Windows SmartScreen will warn, and antivirus may block it because this is remote-control software. It should not be distributed widely until signed.

**Open security risk: the Controller's SQLite.** `System.Data.SQLite.Core` 1.0.119 is the latest on NuGet and embeds SQLite **3.46.1**, lower than 3.50.2 which fixes CVE-2025-6965. NuGet scanners do not flag this package. The flaw requires an attacker to run arbitrary SQL; the Controller only uses fixed parameterized statements, so the real-world risk is assessed as low. Fully eliminating it requires changing the SQLite driver. Kept for now and monitored for a new release as recommended; the project owner had no other opinion (2026-11-05). The Client does not use SQLite.

**Unexplained failure:** the e2e scenario on the x86 build once stopped midway at version 0.5.1, after the quick-connect step. The error message was lost to the output filter, and `verify.ps1` did not catch it at the time. The Windows session may have had a user active then. Three re-runs all passed; not reproduced. `verify.ps1` now requires every scenario to report completion and fails if not.

## 3d. Relay forwarding (Phase 5)

**Every result below ran on one machine, over loopback.** They prove the protocol and code work, **not** that they work across the Internet.

| Item | Result | How |
|---|---|---|
| Full session over the Relay: image, text, changing frames | **Pass** | Real `RDM.Server.exe`, the target machine listens on no port, simulated screen and input |
| From the real interface: direct unreachable, auto-switches to Relay | **Pass** (x86, x64) | `e2e-smoke`: two `RDM.exe`, the advertised address unreachable, the status bar shows `via Relay`; Connection History screenshot verified on x64: earlier session `Direct`, later session `Relay` |
| Password, pinned key, emergency switch over the Relay | **Pass** | Wrong password refused; refusing the key does not prompt the owner; owner disconnect does not auto-reconnect |
| 5 concurrent sessions without data mixing | **Pass** | Each session sends its own text, each target verified to receive the right one |
| Tickets: reuse, forged, expired | **Pass** | All refused |
| Correct status replies: non-existent, Offline, access disabled, connecting to self | **Pass** | Correct codes, no ticket issued |
| Limits: requests per minute, concurrent sessions | **Pass** | Configured low then exceeded the threshold |
| One end leaves, the receiver does not read | **Pass** | The Relay closes the session. The "receiver does not read" test was verified: removing the fix makes the test fail |
| Relay log contains no session content or usernames | **Pass** | Scanned the Server output |
| Relay throughput on loopback | **~76–78 MB/s** | 256 KB blocks, **E2EE not included**, all four configurations. Only an upper bound for the forwarding part |

**Not tested:**
- **Over the Internet, NAT, firewalls, real routers, two different machines.**
- Network loss mid-session over the Relay; changing networks while connected.
- FPS and latency over the Relay on a real network. **The Relay's real bandwidth has not been measured.**
- Relay under load: only 5 concurrent sessions so far. `MaxRelaySessions` 1000 is a configuration limit, not a tested capacity.
- **The Relay running as a Windows service:** coded and documented, never run.
- Direct via a real LAN address while a Relay is present: only tried with an unreachable address.

**No TCP hole punching.** Section 19 has "NAT Traversal": currently achieved only in the sense of outbound connections through the Relay, with no direct NAT traversal. Saving Relay bandwidth would need a separate phase, possibly a coordination server, and still would not be guaranteed with symmetric NAT.

## 3e. Pre-packaged Relay (version 0.6.1)

The Release build carries `erp247.ddns.net:31408`, the pinned key and the enrollment code; the Debug build does not. Port 443 on the server is already served by Caddy for another website, so the Relay uses its own port.

**Checked:**
- 251/251 on 4 configurations; e2e and ui-smoke pass on x86 and x64.
- Applied once, keeping the owner's Relay, cleared and not re-filled: there is a test.
- The Relay string is in `RDM.Client.dll` in the Release build, absent in Debug. The e2e script sets `RDM_NO_RELAY_DEFAULTS=1` so it does not touch the real Relay.
- The Relay loads the real certificate and listens on loopback; the log reports the right pinned key, expiring 2036-11-02.

**Not checked:**
- `build\deploy-relay.ps1` has never run, because it needs Administrator: installing the service, running as LocalService, opening the firewall.
- Router port forwarding, a real device registering and a session over `erp247.ddns.net:31408`. No real device has ever registered with this Relay.
- The packaging code applied to a real install; the test only calls the function directly.

**Open issues:**
- One acceptance run had `e2e-smoke x64` end printing nothing, within 4 seconds after ui-smoke. No incident in the Windows log, cause unknown. Re-running immediately after passed 3/3. The scenario stopped even before the Relay-start step, but a machine-software-related environment fault cannot be fully ruled out. Kaspersky then reported `PDM:Trojan.Win32.Generic` on `build\e2e-smoke.ps1` and the file vanished from disk: possibly the same cause, not proven. Whether RDM.exe is similarly detected on another machine is unknown.
- This machine has Kaspersky. A PowerShell port probe saw a different self-signed RSA certificate with the same name and a thumbprint that changes each time: suspected to be an encrypted-connection scanning feature, not proven. RDM refuses any certificate with the wrong pinned key, so a machine with this kind of TLS-interception software will not connect to the Relay until RDM.exe is excluded.
- The enrollment code is in the installer, so anyone with the installer can extract it; revoking it requires reinstalling every machine.

**Managed mode (0.6.3):** the Release build has packaged configuration that does not show the domain, port, pinned key, enrollment code or the word Relay/Server in the interface; the transport shows as `Internet`. Every startup re-applies the packaged configuration, replacing the "once only" rule of 0.6.1. Result: 251/251 on Debug x86, Debug x64 and Release x86; Release x64 250/251. The failing test was `LanDiscoveryTests.FindsRightId_ReturnsSessionPort` (3-second timeout); re-running just that group passed 8/8. The query sends only one UDP packet with no retry, so a lost packet means not found; the cause of the packet loss was not determined. **The Managed interface has not been checked by eye or by script**, because `ui-smoke` and `e2e-smoke` were not run due to the Kaspersky warning. The log file still contains the word "Relay" but does not record the domain. The domain can still be found with a network-connection viewer or by reading the DLL file.

**Tray interface and Remember password (0.6.5):** the controlled machine drops the red bar and the balloon, leaving only a quiet indicator on the main window's tray icon (a "Being controlled by …" tooltip and a Disconnect command) — still public, not a pop-up alert. Closing the window only minimizes to the tray; full exit is via the Exit menu. The sign-in box has a "Remember password" option (stored by DPAPI per Windows account). The Managed build removes the port field in Settings. The interface has not been checked by eye or by script.

**Phase 6 - File Transfer and Chat (0.7.0):** chat and file transfer run on the session's existing E2EE channel, opening no new port. Received files are saved to `Documents\RDM Received Files`, the filename trimmed to its name only (preventing directory traversal), never overwriting, never executed. The receiver accepts automatically because the session is authenticated; both sides see it in the chat panel. On the controller: the tray menu has "Send File..." and "Chat" (the chat panel docks to the right of the window). On the controlled machine: the chat window opens when a message or file arrives. The engine tests (`SessionTransferTests`) pass 5/5: chat arrives; a 300 KB file intact; a traversal name trimmed to the correct receive folder; no overwrite (changed to "(1)"); an oversized chunk refused. **The chat/file interface has not been checked by eye or between two real machines.**

**File manager, nicer chat, multiple monitors (0.8.0):**
- AnyDesk-style file transfer: a two-pane File Manager window (controller on the left, remote on the right), browsing the remote machine's folders/drives, with Upload/Download. The folder-browsing protocol (FsList/FsListReply/FsGet) and file transfer with a target folder run on the same E2EE channel. As required, the controlled machine **shows nothing** during a file transfer (only chat opens a window). Files are still saved safely: the name is trimmed against traversal, no overwrite, no auto-run; the target folder is an absolute path the user browsed to (full system access like AnyDesk, limited by the permissions of the signed-in account).
- Chat redrawn as bubbles, with a title bar and a close button.
- Multiple monitors: a floating button bar at the bottom of the Remote window (1, 2, ..., All) and shortcuts Ctrl+1..9 to choose a monitor, Ctrl+0 to view all. "All Monitors" captures the whole virtual area, added automatically on the target when there are 2 or more monitors.
- Engine tests 8/8 (added: browsing returns the right entries, upload saves to the right target folder, download lands in the right local folder).
- **The interface of these parts has not been checked by eye or between two real machines.** File-transfer security: currently auto-accepts, with no per-file consent step yet.

**Exit full screen (0.7.0):** in full-screen mode there is no toolbar, so an F11 key was added (intercepted at the controller, not sent to the target) and a floating "Exit Full Screen" button at the top edge; the tray menu still has the Full Screen item.

**Race condition in the emergency switch (fixed in 0.6.5):** when the owner disconnects, the host sends an "owner ended" frame then closes the socket immediately; in the Release build, the socket was closed (with RST) before the frame arrived, so the Controller mistook it for a connection loss (byUser=false) and could auto-reconnect. The new-connection block (OwnerBlock) still held, but the "intentional" signal was unreliable. `OwnerDisconnectAll_EndsSession_AndReleasesInput` failed 100% in Release (both x86 and x64), passed in Debug. Fix: after sending the frame, wait briefly for the Controller to finish reading and close first (a graceful close). After the fix: 10/10 on both Release x86 and x64.

**Bugs found and fixed in this round:**
- `--hash-token` received a BOM when PowerShell 5.1 piped it through, corrupting the hash so the Relay would refuse every new device. Confirmed by SHA-256 comparison and fixed on both sides.
- The Controller could show Online incorrectly if an Offline message arrived before an older snapshot. Found during code reading, the send order was fixed; no test reproduces this exact timing.
- `Controller_SnapshotIncludesOfflineRegisteredDevices` and e2e relied on fixed waits: changed to condition-based waiting.

## 3b. Real screen and input: the most important missing part

| Item | Status |
|---|---|
| Real screen capture, over E2EE, displayed on the Controller | **Partial Pass:** seen once during an `e2e-smoke` run of the x86 Release build, on a single 1920x1200 monitor. No measurement |
| **Real mouse via SendInput** | **Not tested** |
| **Real keyboard, key combos, Vietnamese, Unikey** | **Not tested** |
| **Low-level hook, Alt+Tab, Windows key** | **Not tested** |
| **FPS on a changing screen** (target 15–30) | **Not measured.** The only time it was seen, the screen was nearly static |
| Multiple monitors | **Not tested** (only tested with a simulated screen) |
| When the Windows session is disconnected from the display | **Pass:** no error, the Controller reports the state clearly |

Reason: for most of the testing, the Windows session was disconnected from the display (Session 1, console on Session 4), so BitBlt was refused. Real input tests are not run automatically because, if a user is at the machine, the test text could be typed into another application.

**How to complete it:** while sitting at the machine with nothing in progress, run `.\build\real-desktop-test.ps1` then `.\build\real-desktop-test.ps1 -Platform x86`. The mouse will move for about 10 seconds. The test prints the measured FPS.

**Windows Service (`--install-service`): never run.** It needs Administrator and is a system change, so it is not auto-installed on the development machine. It includes Ctrl+Alt+Del via `SendSAS` and launching the Agent in the user session. Best tried on a VM.

**Not two different machines:** every process ran on one machine over loopback. The Section 19 criterion of "two different Windows computers" and real NAT/firewalls remains untested.

## 4. Measured performance

| Metric | x86 | x64 |
|---|---|---|
| PBKDF2 600,000 iterations, one hash | ~2.0–2.3 seconds | ~1.7–1.8 seconds |
| Import 5,000 devices | ~0.75 seconds | ~0.9 seconds |
| Read 5,000 devices | ~65 ms | ~75 ms |
| Search within 5,000 devices | 5–7 ms | 7–8 ms |

| E2EE channel throughput (256 KB blocks, loopback) | ~12 MB/s | ~16 MB/s |
| Relay throughput, E2EE excluded (256 KB blocks, loopback) | ~77 MB/s | ~76 MB/s |

This throughput measures only the encrypted channel using BouncyCastle's AES-GCM (managed code). Enough for ordinary JPEG images, but it may be the bottleneck in "Lossless" mode (PNG) or on high-resolution screens. Real measurement is needed (section 3b) before deciding whether to switch to CNG's AES.

The forwarding part is many times faster than the E2EE channel, so within one session E2EE is the bottleneck, not the Relay.

**Not measured:** real FPS; bandwidth on a real network; the Relay under many connections. The `MaxConnections` default of 10,000 is a configuration limit, not a tested capacity.

## 5. Security testing (Section 8.9)

| Item | Result | What was checked |
|---|---|---|
| SQL Injection | **Pass** | Controller: search box, storage fields. Relay: malicious Remote ID |
| Brute Force | **Pass** | Controller locks after 5 failures. Client locks 15 minutes, persisted across restart. Relay blocks IPs, even with the right token |
| Unauthorized Access | **Pass** | Calling the Service directly with a low role; Controller with the wrong token; a device missing the enrollment code |
| Database Theft | **Pass** | The DB file, WAL and Client state file have no plaintext secrets. Another machine cannot unlock the key (**simulated**) |
| MITM (Controller/Client ↔ Relay) | **Pass** | An untrusted certificate and a wrong pinned key are both refused |
| MITM on the E2EE channel | **Pass** | A proxy swapping a temporary key is caught by the signature. An impersonator with a valid key is blocked by key pinning, **before** the password proof is sent |
| Relay cannot read content | **Pass** | Scanned all data through the proxy: no plaintext usernames, passwords, keystrokes, JPEG images |
| Certificate Validation | **Partial Pass** | Trust, pinned key, wrong hostname despite the right pin. **Not checked:** expired certificate, revocation, a real CA chain |
| Replay Attack | **Pass** | Replaying the enrollment signature; the password proof bound to the transcript; replaying a command packet in a session closes the session and the command does not run twice |
| Modify, drop, inject packets in a session (added) | **Pass** | Flip bits, drop a packet, inject an unencrypted frame: the session closes, the command does not execute |
| Session rekey (added) | **Pass** | Transparent rekey while transmitting |
| Emergency switch (added) | **Pass** | Owner disconnect: the Controller does not auto-reconnect; Unattended with the right password is still blocked for 60 seconds |
| Controller key pinning (added) | **Pass** | An Operator cannot unpin; a changed key is Audited at Critical. **Not tested via the interface:** the warning when a pinned key changes |
| Remote ID takeover (added) | **Pass** | A different key is refused, even after the Relay restarts |
| Old TLS (added) | **Pass** | The Server refuses TLS 1.1 |
| Malicious frames (added) | **Pass** | A huge frame before authentication is closed; 3,000 garbage samples only cause a protocol error |
| Logs contain no secrets | **Pass** | The Relay log has no tokens, even wrong ones |
| File Tampering (ciphertext) | **Pass** | Flip each bit, copy ciphertext to another row, a tampered Client state file |
| Key Rotation | **Pass** | Reads back correctly, the revoked key is refused, rollback on failure |
| Backup/Restore Security | **Pass** | Wrong password, flipped bits, a garbage file; restore to another machine (**simulated**) |
| CSV/Formula Injection (added) | **Pass** | Formula cells are neutralized on Export |
| Standard test vectors (added) | **Pass** | AES-256-GCM, PBKDF2-HMAC-SHA256, Ed25519 (RFC 8032) |
| Session Hijacking | **Partial Pass** | Every packet needs its own session key; reused, forged, expired tickets are refused. No scenario yet where an attacker obtains a valid ticket and enters ahead of the real machine (by design this only causes a connection error, see `04-Security.md`) |
| Denial of service via the Relay (added) | **Partial Pass** | Request limits, session count, ticket expiry, receiver not reading. **Not checked:** a registered device using up all bandwidth, many devices enumerating Remote IDs together |
| Token Theft | **Not tested** | Devices do not use tokens. The Controller token has no revocation mechanism yet |
| Path Traversal | **Not tested** | Belongs to Phase 6, no code yet |

This is testing written by the development team itself, not yet an independent security assessment.

## 6. Progress against Section 18

| Item | Status | Item | Status |
|---|---|---|---|
| Install, uninstall | No code | Startup | Pass (Windows 10) |
| Remote ID registration | Pass (loopback) | SQLite | Pass (Windows 10) |
| Data encryption | Pass (storage) | Password management | Pass |
| Online/Offline | Pass (loopback) | Authentication | Pass (Relay, local) |
| TLS | Pass (to the Relay, loopback) | E2EE | Pass (loopback, including through an attack proxy) |
| LAN, Internet connection | **Not tested on a real network.** Relay passes on loopback (section 3d) | Screen | Partial Pass (section 3b) |
| Mouse, Keyboard | **Not tested with real input** (section 3b) | | |
| File Transfer, Chat | No code | Multi-Monitor | No code |
| Multi-Session | Multiple sessions over one Relay: passes on loopback. Multiple Controllers into one machine: not supported | | |
| Reconnect | Pass (to the Relay); session: real network loss not tried | Backup/Restore, Audit Log | Pass |

## 7. Test-found bugs fixed

| Bug | Found by | Note |
|---|---|---|
| The Client's Disable Remote Access button was clipped out of the frame | Looking at a screenshot | The script could still click it so the logic test missed it. Added a test that every button sits fully within its frame |
| Building the solution x86 made RDM.Server unrunnable | x86 acceptance | `RDM.Shared` was locked to x86; changed to neutral IL |
| The x86 build reported the wrong Windows edition | Looking at a screenshot | Misread `WOW6432Node`; added a test |
| `verify.ps1` returned exit 0 with a failing test | Reading the result | Fixed |
| **Owner clicks Disconnect now but the Controller auto-reconnects;** with Unattended the emergency switch was ineffective | Design review | Fixed on both ends, with a test |
| **Integer overflow in image-region bounds checking:** a malicious target could bypass the check | Test with boundary values | Fixed |
| The fingerprint dialog ran on a background thread and would error on the first connection | Code review | Fixed, a three-process scenario exercises this path |
| Someone without the password could spam permission dialogs onto the owner's screen | Design review | Added a 10-second timeout, with a test |
| Active Sessions still listed a session the owner had disconnected | Looking at a screenshot | Fixed |
| **The Client installer contained only the exe, unrunnable,** though the build reported success | `verify-installer.ps1` | Fixed; the build now stops if WiX reports missing files |
| The Relay's SQLite had CVE-2025-6965 | The `NU1903` warning | Patched to 3.53.3 |
| Vietnamese in the Relay console was corrupted | Reading the log | Fixed |
| Relay tests occasionally failed from port contention | Reading the Server log | Fixed the test harness; the Server had no bug |
| **The Server installer refused to install on every OS:** the condition required `VersionNT >= 1000`, but Windows Installer reported 603 even on Windows 10 | The project owner ran a real install | Moved the condition to Burn, where the version reads correctly; verified with the inverse condition; `verify-installer` blocks a recurrence |
| Unaccented Vietnamese text in the interface ("Xoay") that an accented-text scanner missed | Looking at a screenshot | Fixed, a test blocks additional distinctive unaccented words |
| The database-scan test could read short when SQLite merged the WAL midway | A random failure on Release x64 | Fixed: rescan from the start, skip nothing |
| **The test script saved a real screenshot of the runner's desktop** when the session had a display | Looking at a screenshot | Deleted the image; by default it no longer saves |
| A newly added device had empty IP/OS | Looking at a screenshot | Fixed, with a test at the Service layer |
| **Hang during cleanup right after requesting a ticket:** the result continued on the receive thread, and if the caller blocked waiting for the connection to stop, both sides waited for each other | A test that hung for over 10 minutes, with step-by-step markers | Fixed; the owner-permission and fingerprint dialogs use the same code pattern so they were fixed too |
| **A receiver that deliberately does not read holds a Relay session forever** because the write had no timeout | Code review | Fixed, the test was verified |
| One end dying exactly on receiving `RelayReady` left the other waiting forever, holding the connection | Code review | Fixed. **No test reproduces this exact timing** |
| LAN discovery reported a socket-not-bound error when every send failed | A random failure on Debug x64 | Fixed, with a test. Why the sends failed in that run was not determined |
| Could not connect to a machine with a changed port: entering an IP always used 7444 | Writing the e2e scenario | Added the `address:port` form, with a test |

## 8. Known limitations

**A single TeamViewer-style program (the project owner's choice, replacing the separate programs in Sections 3, 14, 15):**
- **Every installed machine has the full management code.** The ability to read data is still blocked: the management part requires sign-in, and the database is per Windows account.
- **No RDM login for quick connect:** anyone who can use that Windows account can connect out to other machines, but still needs the target's password or the owner's acceptance. Stored passwords cannot be read. Audit records `windows:<name>`.
- **Each machine opens TCP 7444 and UDP 7445 while RDM runs.** Turning off Remote Access closes both.
- **LAN discovery via UDP broadcast is untested on a real network**, only on loopback. It does not cross routers; use the Relay or enter an IP then.

**Phase 5:**
- **No TCP hole punching** (section 3d). Two machines behind two routers go through the Relay, so Relay bandwidth is a real cost.
- **Trying direct takes up to 2 seconds per address** before switching to the Relay, so a relayed connection is a few seconds slower.
- **The Relay knows who connects to whom, when, and how much data**, and records the Remote ID in its log. It does not know the content.
- **A Relay with an empty enrollment code lets anyone use its bandwidth.** There is no per-device bandwidth quota yet. The deployment guide requires setting the code when open to the Internet.
- **The target machine must have Relay configuration on both ends** to forward. If the controlling side has not configured a Relay, only the direct path is available.
- **No IPv6** for the forwarding path at the tested level; only IPv4 loopback was tried.

**Phase 4:**
- **Direct connection not tried between two real machines.** The installer opens the port on the Domain and Private networks.
- **The P2P channel uses E2EE, without an additional TLS wrapper.** This deviates from the wording of Section 8.1 ("TLS 1.2 or later" for P2P too). Reason: a self-signed TLS with no root of trust adds nothing; E2EE already encrypts and authenticates both ends with key pinning. Kept as recommended; the project owner had no other opinion (2026-11-05).
- **The Windows Service has never run.** Without the Service: closing the Client window or signing out of Windows means Offline; no Ctrl+Alt+Del.
- **The Secure Desktop cannot be controlled** (UAC, the login screen, the lock screen), even with the Service: the Agent runs under the user's token. The Controller reports clearly instead of a black screen. No SYSTEM-privilege Agent was built because of the high risk. Not done, as recommended; the project owner had no other opinion (2026-11-05).
- **An application running as Administrator on the target does not receive input** from a normal-privilege Agent (UIPI).
- **Ctrl+Alt+Del via Special Keys is only available with the Service.**
- **One session per Client.** Multiple Controllers at once is not supported.
- **The first connection relies on the user comparing the fingerprint.** Skipping that step (disabling StrictPinning then clicking blindly) lets a malicious Relay intrude and crack the password offline. A weak password raises this risk.
- **Image decoding uses GDI+ on data from the target.** Only after the pinned key is authenticated, but a compromised target could exploit a GDI+ flaw on the Controller.
- **DPI:** the app declares System DPI Aware; monitors of different DPI are untested.
- **Reconnect:** not tried with real network loss; only a test of an intentional Client disconnect.
- **H.264:** not available, uses JPEG and PNG.

**Phase 3:**
- **The Relay logs to the console**, with no persistent Audit Log, device revocation or Server Manager yet. The Controller token has no revocation mechanism beyond changing the configuration.
- **IP-based brute-force blocking:** many machines behind one NAT IP are blocked together if one tries wrong passwords repeatedly.
- **Device info (name, IP, OS) is self-reported by the device**, for display only. A name set by the administrator is not overwritten.

**Phases 1–2:**
- **Key rotation is manual only.** "Periodic key change by policy" (Section 8.4) is not automated.
- **Unencrypted metadata:** machine names, IPs, Remote IDs, history, and usernames in `AuditLogs` and `ConnectionHistory` are still readable if the DB file is stolen.
- **Key revocation does not disable old backups**, because a backup carries its own copy of the key. The dialog has a warning.
- **Wiping keys from memory** is only "best-effort" because .NET's GC may have copied the array.
- **The title bar** stays light in Dark Mode.
- **The x86 build also carries `x64\SQLite.Interop.dll`** (and vice versa). Harmless at runtime; the Phase 7 installer will remove it.
- **No testing on a clean machine yet**, so it is unknown whether `SQLite.Interop.dll` needs the VC++ runtime installed.
- **The UI test once hung** when using nested `Application.DoEvents` to switch pages (stuck in `DestroyWindow`). Running with a real message loop did not hang. The root cause is not fully determined; watch for it if Phase 4 uses a nested message loop.
