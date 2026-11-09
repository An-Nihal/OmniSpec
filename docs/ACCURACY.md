# Data accuracy, sources and definitions

OmniSpec's first rule: **never show a fabricated value.** This document describes where each value comes from and
how derived values are calculated.

## Metric states

Every value in OmniSpec is a `Metric<T>` with a state, a source and a timestamp.

| State | Meaning | Shown as |
|---|---|---|
| Available | Read successfully | the value |
| Unsupported | The hardware, driver or OS does not provide it | *Not Supported* |
| Unavailable | Supported in principle, but no value right now (e.g. a drive that does not report temperature) | *Unavailable* |
| PermissionRequired | Needs administrator rights and/or the PawnIO driver | *Permission Required* (amber) |
| Error | Reading failed | *Error* |

Hover over any value to see its source.

## Sensor normalization

Hardware libraries sometimes return placeholders when a sensor could not actually be read. OmniSpec discards
(`SensorNormalizer`):

- **Temperatures:** ≤ 0 °C or > 150 °C.
- **Loads and percentages:** outside 0–100%.
- **CPU power and clocks:** exactly 0 (a running CPU always draws power and clocks above 0). GPU clocks may legitimately
  be 0 when idle.
- **Negative values:** for example a GPU "Fullscreen FPS" factor of −1.
- **Low-level CPU sensors without access:** all CPU temperature/power/clock/voltage sensors when low-level access is
  unavailable. Without PawnIO, LibreHardwareMonitor reports fixed placeholder values for these.

## CPU

| Value | Source |
|---|---|
| Utilization, per-logical-processor load | Windows performance counters "Processor Information \ % Processor Utility" (same as Task Manager) |
| Effective clock | base clock (registry `~MHz`) × "% Processor Performance" (same method as Task Manager) |
| Throttling | LibreHardwareMonitor "Distance to TjMax" when available; otherwise "% Performance Limit" (100% = not limited) |
| P/E core type | `GetLogicalProcessorInformationEx` efficiency class |
| Temperatures, power, voltage, per-core clocks | LibreHardwareMonitor (requires administrator + PawnIO) |

## Memory

- **Cached** = standby lists + modified page list (Task Manager's definition), from performance counters.
- **Memory pressure** = max(physical usage %, commit charge %): < 70% Low, 70–85% Moderate, 85–95% High, ≥ 95% Critical.
  This is OmniSpec's own heuristic and is labelled as such.
- **Not reported:** timings and channel configuration. Windows does not expose them.

## Storage health

Sources, in order of authority:

1. **Windows health status:** `MSFT_PhysicalDisk.HealthStatus` (Healthy / Warning / Unhealthy) and `OperationalStatus`.
2. **Windows Storage Reliability Counters:** `MSFT_StorageReliabilityCounter` gives temperature, wear, power-on hours,
   error counters and latencies. Usually requires administrator rights.
3. **SMART / NVMe health log:** read through LibreHardwareMonitorLib (DiskInfoToolkit). A drive is matched to these
   sensors only by index plus model, or by a unique model name. Identical drives are never guessed.

The verdict (`DriveHealthEvaluator`) is the worst level triggered by these rules:

| Condition | Level |
|---|---|
| Windows status *Unhealthy*; operational status *Predictive Failure / Failed Media / Lost Communication*; NVMe available spare below its threshold; NVMe critical warning | **Critical** |
| Windows status *Warning*; operational status *Degraded / Stressed / IO error*; endurance used ≥ 90% (including ≥ 100%); uncorrected read/write errors > 0; media/data integrity errors > 0; device-reported remaining life ≤ 10% | **Warning** |
| Endurance used 50–90% | **Good** |
| Windows status *Healthy* and no adverse finding | **Healthy** |
| No indicator available | **Unknown** |

- **Endurance used** = NVMe *Percentage Used* or, for SSDs only, the Windows reliability *Wear* counter. Both express
  consumed rated endurance: **100% means the rated endurance has been reached**, not "100% health".
- **Estimated remaining life** = 100 − endurance used. It is labelled as an estimate and only shown when the drive
  reports wear. Otherwise it uses LibreHardwareMonitor's *Remaining Life* value, which comes from verified vendor
  attribute mappings.
- **Not interpreted:** vendor-specific raw SMART attributes. Unknown attributes are listed "as reported" only.

## FPS (PresentMon)

OmniSpec runs the PresentMon console application with
`--process_id <pid> --output_stdout --stop_existing_session --session_name OmniSpec_PresentMon --terminate_on_proc_exit --no_console_stats`
plus any extra arguments from Settings. It parses the CSV output by header name, which supports both PresentMon 1.x
(`MsBetweenPresents`, ...) and 2.x (`FrameTime`, `CPUBusy`, `GPUTime`, `DisplayedTime`, ...).

| Metric | Definition |
|---|---|
| Frame time | PresentMon `FrameTime` (2.x) or `MsBetweenPresents` (1.x) |
| Current FPS | frames in the last 1 s ÷ their total frame time |
| Average FPS | total frames ÷ total frame time (not the mean of per-frame FPS) |
| Minimum / maximum FPS | 1000 ÷ slowest / fastest single frame time |
| 1% low / 0.1% low | 1000 ÷ 99th / 99.9th percentile frame time (linear interpolation) |
| Frame pacing | standard deviation of frame time |
| Stutter frame | frame time > 2 × median and more than 8 ms above the median |
| Dropped frame | PresentMon `Dropped = 1` (1.x) or `DisplayedTime = NA` (2.x) |
| CPU / GPU / display frame time | averages of `CPUBusy`, `GPUTime` (or `GPUBusy`) and `DisplayedTime` |

The capture timeline is built from cumulative frame times, which is exact for FPS purposes.

## Battery

- **Measured or reported:** charge, AC state, capacities, voltage, rate and cycle count, from
  `GetSystemPowerStatus` and the battery WMI classes in `root\wmi`.
- **Estimated:**
  - **Wear** = 1 − full-charge capacity ÷ design capacity. Shown only when both capacities are reported.
  - **Runtime** is the Windows estimate.

## Network

- **Rates:** deltas of interface byte counters over measured elapsed time.
- **Utilization:** (download + upload) × 8 ÷ link speed.
- **Connection uptime:** measured from when OmniSpec observed the link come up. Windows does not expose link-up time.
