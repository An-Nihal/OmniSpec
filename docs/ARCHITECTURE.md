# Architecture

```
            ┌──────────────────────────── OmniSpec.App (WPF, MVVM) ─────────────────────────────┐
            │ MainWindow · pages (DataTemplates) · WidgetWindow · OverlayWindow · Tray · Themes │
            │ ViewModels receive coalesced snapshots on the UI thread (active page only)        │
            └───────────────▲──────────────────────────────────────────────▲────────────────────┘
                            │ events (background thread)                   │ settings / export
            ┌───────────────┴─────────── OmniSpec.Monitoring ──────────┐  ┌┴── OmniSpec.Infrastructure ──┐
            │ MonitoringService: one loop per data class               │  │ JsonSettingsStore / Service  │
            │  · sensor loop (1 s) → LiveSnapshot → history, alerts    │  │ FileLoggerProvider           │
            │  · storage loop (60 s) → DriveState                      │  │ Exporters (CSV / JSON / TXT) │
            │  · process loop (2 s, only while subscribed)             │  │ HistoryCsvLogger (optional)  │
            │ Providers: CPU (perf counters), memory, network,         │  └──────────────────────────────┘
            │ battery, processes · FpsMonitor + PresentMon provider    │
            └──────▲─────────────────────────▲─────────────────────────┘
                   │                         │
   ┌───────────────┴──── OmniSpec.Hardware ──┐ ┌┴──── OmniSpec.SystemInformation ─────────────┐
   │ LibreHardwareMonitorProvider            │ │ SystemInfoCollector (WMI/CIM, Win32, registry)│
   │ (+ Debug-only MockHardwareProvider)     │ │ StorageInfoProvider (MSFT_PhysicalDisk, ...)  │
   └───────────────▲─────────────────────────┘ │ DisplayReader (QueryDisplayConfig, HDR)       │
                   │                           │ CpuTopology (P/E cores, caches) · PowerPlan   │
                   │                           └──────────────▲────────────────────────────────┘
            ┌──────┴──────────────── OmniSpec.Core (no Windows dependencies) ─┴────────────┐
            │ Metric<T> + MetricState · HardwareSnapshot · IHardwareSensorProvider · SensorQuery │
            │ SensorNormalizer · SensorMapping · DriveHealthEvaluator · FrameStatistics          │
            │ PresentMonCsvParser · MetricHistory/RingBuffer · AlertEngine/AlertRules · Settings │
            └────────────────────────────────────────────────────────────────────────────────────┘
```

## Key decisions

- **Core holds all pure logic** (health interpretation, sensor mapping, frame statistics, parsing, alerts), so it is
  unit-testable without hardware.
- **Provider interfaces.** `IHardwareSensorProvider` is the extension point for other sensor sources, such as vendor
  SDKs; `IFrameRateProvider` plays the same role for FPS (e.g. a future PresentMon API/service provider). Other data
  classes have their own interfaces too: `ICpuUsageProvider`, `IMemoryProvider`, `INetworkProvider`, `IBatteryProvider`,
  `IProcessProvider`, `ISystemInfoProvider` and `IStorageInfoProvider`.
- **Single owner of polling.** `MonitoringService` starts each loop once, uses `PeriodicTimer` with live-adjustable
  periods, honours cancellation, and is disposed on shutdown. Process sampling only runs while a page subscribes.
- **UI efficiency:**
  - **Coalesced updates:** background events are coalesced; only the active page and the widget update, and nothing
    updates while the window is hidden.
  - **Custom charts:** `LineChart` and `RingGauge` are drawn with `DrawingContext` instead of a charting library. This
    keeps memory and CPU low and avoids a large dependency.
- **Bounded memory:**
  - **History:** each metric's history is a fixed-size ring buffer sized from the history length and polling interval.
  - **FPS sessions:** capped at 500k frames.
- **Mock isolation.** `MockHardwareProvider` is excluded from Release builds of `OmniSpec.Hardware` (`Compile Remove`),
  and every mock snapshot is labelled `MOCK`. The test project compiles the mock file itself.
- **Privilege model.** OmniSpec runs unelevated by default. It can restart elevated through UAC. Automatic
  administrator mode registers a highest-privileges scheduled task, only when the exe is under Program Files; see
  `ElevatedLaunch`.

## Adding a hardware provider

1. Implement `IHardwareSensorProvider` (Core). Return `null` values or omit sensors you cannot read. Never return
   placeholders.
2. Map vendor sensor names to the typed models in `SensorMapping`, or extend the candidate name lists.
3. Register it in `App.ConfigureServices`.
4. Add unit tests with synthetic `HardwareSnapshot`s.
