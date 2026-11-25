# Monita Traffic Monitor

Monita is a local Windows 10 desktop prototype for viewing observed TCP/UDP traffic by process, Windows services hosted by that process, and the network adapter associated with its local IP address. It includes daily history and a configurable bundle limit/reset day, and saves records under `%LOCALAPPDATA%\Monita\TrafficMonitor`.

The app icon is a network-usage meter. On the current day's usage list, select an app to pause/resume its entire process, end it, or block its network access. Process controls ask for confirmation, validate that the selected PID still matches its executable, and are disabled for Windows/system processes and processes hosting Windows services. Apps paused by Monita are resumed when Monita exits.

Network-only pause adds temporary inbound and outbound Windows Firewall rules for the executable and all its instances. The default block is 30 minutes, can be removed sooner from Monita, and has a scheduled cleanup if Monita exits. Startup monitoring is optional; enabling it creates a highest-privilege sign-in task and starts Monita hidden in the tray.

## Install

Download and run `Monita-Setup-x64.exe`. The installer includes the .NET runtime, installs Monita for all users, and creates a Start menu shortcut. A desktop shortcut can be selected during setup. Monita requires Windows 10 or later and administrator privileges to collect network traffic.

To build the installer from source, install the .NET 8 SDK and Inno Setup 6, then run:

```powershell
.\installer\build-installer.ps1
```

The installer is written to `artifacts\Monita-Setup-x64.exe`.

## Run from source

The app must run as administrator to start the Windows kernel network trace. Open the project with the .NET 8 SDK and build or publish it:

```powershell
dotnet publish .\TrafficMonitor.csproj -c Release -r win-x64 --self-contained true
```

The executable is written under `bin\Release\net8.0-windows\win-x64\publish`.
Set the monthly limit in GB and the billing-cycle reset day in the app. Windows tray notifications warn at 80% and 100% of global and per-app limits.
Per-app limits use the same billing cycle. Monita also shows short-window upload/download rates, observed TCP/UDP endpoints, a 30-day usage chart, current-vs-previous-cycle totals, CSV export, tray notifications, and a System/unresolved-traffic diagnostic with adapter-counter comparisons.

## Current limitations

- Process/service attribution is best-effort. If several services share an `svchost.exe` process, the traffic is shown for that process and its listed services; bytes cannot be divided among those services.
- Kernel/System traffic may remain unattributed, and virtual adapters or VPN routing may not resolve to a physical adapter.
- Totals are ETW-observed TCP/UDP byte counts, not a carrier's billing measurement; link-layer overhead and some non-TCP/UDP traffic may not be represented.
- Collection begins when the app is running; traffic from earlier periods cannot be reconstructed.
- The bundle warning is displayed in the app; it does not yet send a Windows notification or launch the app automatically at sign-in.
- Process pause suspends the whole app, which becomes unresponsive until resumed. Ending an app may lose unsaved work.
- Network blocks apply to every instance of the selected executable. Their scheduled cleanup uses the executable's current path; moving or deleting Monita before expiry can prevent that cleanup.
- Connection details are summaries of observed endpoints, not packet captures or a guarantee that a connection is still active. Only the most recent 100 endpoint summaries per app/adapter are retained.
- Adapter comparisons are approximate because Windows interface counters and ETW totals cover different layers and may include different overhead.
