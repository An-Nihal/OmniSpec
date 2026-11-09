# Changelog

All notable changes to OmniSpec are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-10

### Added
- **Full sensor access out of the box:**
  - **Installer defaults:** automatic administrator mode, and optional download + install of the PawnIO driver
    (verified by SHA-256).
  - **Bundled tool:** Intel PresentMon 2.6.0 for FPS capture.
  - **Clean display:** values that a machine cannot provide are left out of the UI instead of showing permission
    messages.
- **Dashboard** with Overview, System Information, CPU, GPU, Memory, Storage, FPS & Gaming, Sensors, Network, Battery,
  Processes and Settings pages. Dark and light themes with accent colors.
- **System information** via WMI/CIM, Win32 and the registry:
  - **Processor:** CPU topology including hybrid P/E cores, caches and instruction sets.
  - **Board and firmware:** motherboard, BIOS/UEFI, Secure Boot.
  - **Memory:** modules.
  - **Graphics and displays:** GPUs, and displays with HDR state via the Display Configuration API.
  - **Devices:** audio, network, Bluetooth, USB and PCI.
  - **Power:** power plan and batteries.
  - **Export:** JSON and TXT, with sensitive identifiers hidden by default.
- **Live monitoring:**
  - **Hardware sensors:** LibreHardwareMonitorLib 0.9.6.
  - **CPU utilization:** Windows performance counters, per logical processor with P/E labels.
  - **Memory:** a memory breakdown.
  - **Network:** throughput per adapter.
  - **Battery:** health.
  - **Processes:** a read-only process monitor.
- **Accuracy safeguards:**
  - **Metric state and source:** every metric carries a state (Available, Not Supported, Unavailable, Permission
    Required, Error) and a data source.
  - **Placeholder rejection:** sensor normalization discards placeholder readings such as `0 °C` or `0 W` from
    sensors that could not be read.
- **Storage health:**
  - **Sources:** Windows health status, Storage Reliability Counters and SMART/NVMe data.
  - **Verdict:** a conservative Healthy/Good/Warning/Critical/Unknown verdict that lists every finding with its source.
- **FPS capture** with the user-supplied Intel PresentMon console application:
  - **Statistics:** current/average/min/max FPS, 1% and 0.1% lows, frame time, CPU/GPU/display frame time, pacing,
    stutters and dropped frames.
  - **Output:** session summary, CSV export and a click-through in-game overlay.
- **Floating desktop widget:**
  - **Placement:** draggable and resizable; always-on-top or pinned to the desktop; lockable.
  - **Display:** opacity presets, compact mode and selectable metrics.
- **System tray**, Windows notifications and de-duplicated alerts (including custom per-sensor limits).
- **History:**
  - **In memory:** bounded history with min/max/avg.
  - **On disk:** optional daily CSV logging.
  - **Export:** CSV export.
- **Optional automatic administrator mode:** a scheduled task with highest privileges, registered with one UAC
  consent and only for Program Files installs.
- **Portable mode** (`portable.txt`), single-instance handling and saved window/widget positions.
- **Tests:** xUnit tests, with hardware-dependent tests tagged `Category=Hardware`, and a Debug-only mock hardware
  provider.
- **Releases:**
  - **Workflows:** GitHub Actions build and release workflows.
  - **Installer:** an Inno Setup installer.
  - **Artifacts:** a portable zip and SHA-256 checksums.

[Unreleased]: https://github.com/An-Nihal/OmniSpec/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/An-Nihal/OmniSpec/releases/tag/v1.0.0
