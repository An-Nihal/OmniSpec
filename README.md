# OmniSpec

**Your Entire PC. One Clear View.**

OmniSpec is a free, open-source Windows desktop app that shows what is inside your PC and how it is doing right now:
hardware configuration, live CPU/GPU/RAM/network telemetry, drive health, sensors, FPS for games, battery health and
processes. It includes a floating desktop widget and an in-game overlay.

It is **offline and privacy-first**: no accounts, no telemetry, no analytics, no ads and no network uploads.
It also **never shows made-up numbers**. Every value carries a state (*Available, Not Supported, Unavailable,
Permission Required, Error*) and a data source you can see by hovering over it.

> Status: **1.0.0**. Built with .NET 10 and WPF for Windows 10/11 x64.

---

## Features

| Area | What you get |
|---|---|
| **Overview** | CPU, GPU and RAM gauges, network throughput, power state, drive health summary, 5-minute utilization and temperature charts |
| **System information** | OS edition/version/build, install date, uptime, CPU (family/model, socket, cores/threads, hybrid P/E layout, caches, instruction sets, virtualization), motherboard, BIOS/UEFI, Secure Boot, memory modules (type, speed, part number, slot), GPUs and drivers, displays (resolution, refresh rate, HDR support/state, bit depth, connection), audio, network, Bluetooth, USB and PCI devices, power plan and batteries. Export to **JSON** or **TXT** |
| **CPU** | Utilization (total and per logical processor, with P/E-core labels), effective clock, per-core clocks, package temperature, per-core temperatures, package power, core voltage, throttling/performance limit, CPU fan, history charts with min/max/avg |
| **GPU** | Multi-GPU selection (integrated and dedicated). Utilization, core/hot-spot/memory temperatures, core and memory clocks, power, voltage, fan RPM and duty, dedicated/shared VRAM, memory controller, video engine and encode/decode load, driver version and date, history charts |
| **Memory** | In use, available, cached, free, committed and commit limit, virtual memory, paged/non-paged pools, standby/modified lists, a documented memory-pressure indicator, usage history, top memory consumers, per-module details |
| **Storage** | Every physical drive (NVMe, SATA SSD/HDD, USB where possible): model, firmware, bus, capacity, partitions and volume usage. Plus the official Windows health status, reliability counters, SMART/NVMe data, wear, estimated remaining life, temperature, power-on hours, error counters and a **Healthy / Good / Warning / Critical / Unknown** verdict that lists its reasons and sources |
| **FPS & gaming** | Per-application FPS via Intel PresentMon. Current, average, min, max, 1% low, 0.1% low, frame time, CPU/GPU/display frame time, frame pacing, stutters, dropped frames. Also a frame-time graph, system metrics during the session, a session summary, CSV export and a click-through overlay |
| **Sensors** | Every sensor the hardware library exposes (temperatures, fans, pumps, voltages, power, clocks, loads), grouped by device, with current/min/max/avg, history, per-sensor warning limits, and a coverage panel explaining *why* a category is missing |
| **Network** | Per-adapter state, type, link speed, download/upload rates, utilization, totals, connection uptime and throughput charts |
| **Battery** | Charge, status, AC state, design/full-charge/current capacity, cycle count, voltage, charge rate, estimated wear and runtime. Measured values and estimates are shown separately |
| **Processes** | Searchable, sortable list with CPU %, memory, private bytes, disk read/write, threads, top CPU/memory and details. Read-only: OmniSpec never terminates or modifies processes |
| **Desktop widget** | Borderless, draggable and resizable. Always-on-top or pinned to the desktop, adjustable opacity, compact mode, lockable position, selectable metrics, remembers its position, toggled from the tray |
| **Alerts** | CPU/GPU/drive temperature, drive health, low disk space, high RAM, battery wear and custom per-sensor limits. Each alert is shown once per episode as a Windows notification |
| **History & export** | Bounded in-memory history (configurable), optional daily CSV logging to disk, JSON/TXT system reports, frame-capture CSV |
| **Settings** | Dark/light theme, accent colors, polling intervals, history length, privacy, hardware components, alert limits, widget metrics, PresentMon path, tray behaviour, start with Windows, automatic administrator mode |

## Install

Download the latest release from the **Releases** page:

- `OmniSpec-<version>-win-x64-setup.exe`: installer (recommended). It installs to *Program Files* and sets up full
  sensor access in one go. The options below are pre-selected:
  - **Always run OmniSpec as administrator.** Windows confirms once during setup; after that OmniSpec starts with
    full access without a UAC prompt.
  - **Install the PawnIO sensor driver.** Needed for CPU temperature/power and motherboard fans/voltages. Setup
    downloads the official, signed `PawnIO_setup.exe` 2.2.0, verifies its SHA-256 and runs it. The option is offered
    only if PawnIO is not already installed.
  - Optional: a desktop icon and "start when I sign in".
- `OmniSpec-<version>-win-x64-portable.zip`: self-contained `OmniSpec.exe` (no .NET runtime needed) plus the bundled
  PresentMon. The included `portable.txt` keeps settings and logs in a `data` folder next to the exe.
- `SHA256SUMS.txt`: checksums. Verify with `Get-FileHash <file> -Algorithm SHA256`.

Intel PresentMon 2.6.0 (MIT) is bundled in `tools\` for FPS measurement, so no extra download is needed.

## Sensor access: administrator rights and drivers

With the installer defaults (administrator mode + PawnIO), every sensor your hardware exposes is shown and nothing
asks for permission. The app never shows "permission required" messages: if a value cannot be read on a particular
machine, OmniSpec simply leaves it out rather than showing a placeholder. Exports (JSON/TXT) still record which
values were unavailable and why.

OmniSpec runs as a **standard user by default**, and everything below works without elevation:

- **System information:** everything except a few firmware details.
- **CPU:** utilization and effective clock, read through Windows performance counters.
- **GPU:** load, temperatures, clocks, power, fans and VRAM, through vendor drivers and D3D.
- **Memory, network, battery and processes.**
- **Storage:** identity, Windows health status and partitions.

Some sensors need ring-0 hardware access or protected storage APIs:

| Needs administrator | Also needs the PawnIO driver |
|---|---|
| Storage reliability counters, SMART/NVMe data and drive temperatures | CPU temperature, power and voltage. Motherboard Super I/O sensors (fans, pumps, voltages, VRM/chipset temperatures). RAM SPD temperatures |

- **PawnIO** is an open-source (GPL-2.0), signed kernel driver used by LibreHardwareMonitor. The OmniSpec app itself
  never installs drivers. The *installer* offers to install it (opt-out checkbox), and you can also get it from
  <https://pawnio.eu/>. Without it, those sensors are left out, never shown as `0 °C`.
- **Restart as administrator** (sidebar or banner) elevates once via UAC.
- **Automatic administrator mode** (installer checkbox, or *Settings › Hardware access › Always run as administrator*):
  OmniSpec registers a Windows scheduled task, `\OmniSpec\OmniSpec (administrator)`, with "run with highest privileges".
  Windows asks for consent once; after that, OmniSpec always starts elevated **without a UAC prompt**, including from
  the Start menu and at sign-in. For security this is only allowed when OmniSpec is installed under *Program Files*.
  Pointing an auto-elevating task at a user-writable exe would let other programs gain administrator rights. Turn it
  off in Settings or by uninstalling.

## FPS measurement (Intel PresentMon)

FPS is application-specific; there is no meaningful "desktop FPS", so OmniSpec measures one target process at a time.

1. The **PresentMon console application** 2.6.0 (MIT) is bundled in `tools\`. A different version can be selected in
   *Settings › FPS & gaming*. PresentMon runs only while you capture; no service is installed.
2. Pick the game in *FPS & Gaming › Target application* and press **Start capture**.
3. PresentMon uses ETW, which needs administrator rights. With automatic administrator mode this is already the case;
   otherwise OmniSpec restarts itself elevated when you start a capture.

Metric definitions (average FPS = frames ÷ total frame time; 1% low = 1000 ÷ 99th-percentile frame time, etc.) are
documented in [docs/ACCURACY.md](docs/ACCURACY.md). The overlay is a click-through, always-on-top window. It shows over
borderless and windowed games; exclusive full-screen games may hide it.

## Drive health: how to read it

- **Windows health status** is the official verdict from the Windows Storage Management API.
- **Endurance used** is device-reported wear: the NVMe *Percentage Used*, or the Windows reliability *Wear* counter
  for SSDs. **100% means the rated endurance has been reached, not 100% life left.**
- **Estimated remaining life** is calculated as 100 − endurance used, and only shown when the drive reports wear.
- OmniSpec never interprets vendor-specific raw SMART attributes and never fabricates a health percentage.
  Missing data stays *Unavailable*.

Full rules: [docs/ACCURACY.md](docs/ACCURACY.md).

## Privacy

- **Offline:** no network connections except your own clicks on links (GitHub/PresentMon/PawnIO pages).
- **No tracking:** no telemetry, analytics, crash upload, accounts or ads.
- **Identifiers hidden by default:** serial numbers, MAC/IP addresses, hostname and user name stay hidden until you
  enable *Settings › Privacy*, and exports exclude them unless that setting is on.
- **Local logs:** stored in `%LOCALAPPDATA%\OmniSpec\logs` and kept for 7 days.

## Resource use

- **Polling:** sensors every 1 s (configurable 0.5–5 s); storage health every 60 s (15–600 s); processes only while a
  page that shows them is open; static information at startup and on refresh.
- **Background cost:** only the visible page updates, and a hidden window costs almost nothing.
- **Measured** on a Ryzen 7 7800X3D with the published build in the tray: ≈0.15% total CPU and ≈140 MB private memory.

## Building from source

Requirements: Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build OmniSpec.slnx
dotnet test  OmniSpec.slnx --filter "Category!=Hardware"   # hardware-independent tests (what CI runs)
dotnet test  OmniSpec.slnx                                  # also runs tests that read this machine's hardware
dotnet run --project src/OmniSpec.App                       # Debug build
dotnet run --project src/OmniSpec.App -- --mock             # Debug only: clearly labelled MOCK data for UI work

# Portable self-contained single-file exe
dotnet publish src/OmniSpec.App/OmniSpec.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

**Command-line options:**

- `--minimized`: start in the tray.
- `--page=<name>`: open a page.
- `--no-elevate`: skip the automatic-administrator hand-off.
- Debug builds only: `--mock`, and `--screenshot=<file.png>` (renders the window off-screen and exits).

**Mock data:** the development mock provider is compiled out of Release builds, so production builds can never show
mock data.

**Releases:** tagging `v1.2.3` runs `.github/workflows/release.yml`. It builds and tests, then publishes the exe, the
portable zip, the Inno Setup installer and `SHA256SUMS.txt`, with release notes taken from `CHANGELOG.md`.

## Architecture

```
src/
  OmniSpec.Core               Models, interfaces and pure logic: metric states, sensor mapping/normalization,
                              disk-health evaluator, frame statistics, PresentMon CSV parser, history, alerts, settings
  OmniSpec.Hardware           LibreHardwareMonitor sensor provider (+ Debug-only mock provider)
  OmniSpec.SystemInformation  WMI/CIM, Win32 (CPU topology, firmware, display config/HDR), Storage Management API
  OmniSpec.Monitoring         Polling service, CPU/memory/network/battery/process providers, PresentMon FPS capture
  OmniSpec.Infrastructure     JSON settings, local file logging, CSV/JSON/TXT exporters, optional history logger
  OmniSpec.App                WPF (MVVM): dashboard, widget, overlay, tray, themes, custom lightweight charts
tests/OmniSpec.Tests          xUnit tests (hardware-dependent tests tagged Category=Hardware)
```

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Known limitations

- **CPU and motherboard sensors** need administrator rights plus the user-installed PawnIO driver (see above).
- **Not exposed by Windows**, so shown as *Not Supported* rather than guessed:
  - **Memory:** timings and channel mode.
  - **CPU:** advertised boost clock, TjMax/TDP.
  - **GPU:** architecture and PCIe link generation/width.
  - **Chipset:** reported only when Windows names it, through the LPC/eSPI controller on Intel platforms.
- **SMART behind USB bridges and RAID controllers** is often blocked by the bridge or controller.
- **Per-process network usage** would need ETW kernel tracing and is not provided.
- **Connection uptime** is measured from when OmniSpec saw the link come up; Windows does not expose link-up time.
- **Fan control, overclocking and voltage changes** are intentionally not implemented. Monitoring is read-only.

## License

OmniSpec is licensed under the [MIT License](LICENSE). Third-party components keep their own licenses. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), notably LibreHardwareMonitorLib (MPL-2.0).

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) and
[SECURITY.md](SECURITY.md).
