# 06 – Interface Design (Step 8)

**From version 0.5.0:** opening `RDM.exe` shows a two-column window (Allow control | Control another machine). Section 1 below describes the **Device Management** window, opened from a button in the main window after signing in. Section 3 below is the left column of the main window.

## 1. Controller

```
+----------------+------------------------------------------------------+
| REMOTE DESKTOP |  Dashboard                        [Dark/Light] admin |
| MANAGER        +------------------------------------------------------+
|                |  +-------+ +-------+ +-------+ +-------+ +-------+   |
| > Dashboard    |  | Total | |Online | |Offline| |Active | |Connec-|   |
|   My Computers |  |Devices| |       | |       | |Session| | tions |   |
|   Groups       |  +-------+ +-------+ +-------+ +-------+ +-------+   |
|   Recent       |  +----------------------+ +-----------------------+ |
|   Favorites    |  | Quick Connect        | | Online / Offline      | |
|   Active Sess. |  +----------------------+ +-----------------------+ |
|   File Transfer|  +----------------------+ +-----------------------+ |
|   History      |  | Connection History   | | Recent Activities     | |
|   Users        |  +----------------------+ +-----------------------+ |
|   Security     |                                                      |
|   Settings     |                                                      |
|   About        |                                                      |
+----------------+------------------------------------------------------+
```

- **Color:** a dark-blue sidebar in both modes. Content is light gray / white (Light) or navy (Dark). All colors live in `Theme`, never hard-coded in a Form.
- **Font:** Segoe UI; icons use Segoe UI Symbol so they exist on Windows 7.
- **Owner-drawn controls:** `ModernButton` (rounded, hover), `Card` (rounded), `SidebarButton`, `ModernInput`, `StatCard`, `BarChart`, `PresenceBar`, `GridView<T>`.
- **Charts:** no two-part pie chart for Online/Offline; use `PresenceBar` with labels and numbers. The single-series bar chart uses the `Accent` color (contrast against light and dark backgrounds checked with the validator). Status colors always come with a symbol and text. Full figures are on the Connection History page.
- **DPI:** System DPI Aware, every size through `Dpi.S()`.
- **Minimum size:** 1024x768.
- **Data grids:** `DataGridView` in `VirtualMode`, with search and filter at the SQL layer, so it handles thousands of devices.

## 2. The principle of honesty in the interface

A feature belonging to an unfinished phase **must not be simulated**. The corresponding page clearly states "Not implemented, belongs to Phase N". The CONNECT button is likewise disabled with the same reason. Device status shows **"Unknown"** until real data arrives from the Relay: writing "Offline" when it is unknown is also a false claim. No sample data is generated.

## 3. Client (Phase 3)

```
+-------------------------------------+
| RDM                                 |
|  Remote ID    123 456 789   [Copy]  |
|  Status       Online                |
|  Computer     PC-KT-01              |
|  OS           Windows 10 x64        |
|  IP           10.0.0.12             |
|-------------------------------------|
|  Access password    [Change Password]|
|  [ ] Enable Unattended Access       |
|  [ ] Disable Remote Access          |
|-------------------------------------|
|  Connection history                 |
|  [Disconnect All]        [Settings] |
+-------------------------------------+
```

During a session: a floating bar is always shown at the top naming who is in control, with a **Disconnect now** button. This bar cannot be turned off by configuration.
