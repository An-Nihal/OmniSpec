# Contributing to OmniSpec

Thanks for your interest in improving OmniSpec! This document explains how to set up a development
environment, how the code is organised, and the rules every contribution must follow.

By participating in this project you agree to abide by the [Code of Conduct](CODE_OF_CONDUCT.md).
Security issues must **not** be reported in public issues; see [SECURITY.md](SECURITY.md).

## Development setup

### Prerequisites

- Windows 10 or 11 (x64). OmniSpec is a WPF application and only builds and runs on Windows.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The required version is pinned in
  [`global.json`](global.json) (`10.0.100`, rolling forward to the latest feature band).
- Optional: Visual Studio 2026, JetBrains Rider or VS Code with the C# Dev Kit.

### Build

```powershell
git clone <your fork URL>
cd OMNISPEC
dotnet build OmniSpec.slnx
```

`OmniSpec.slnx` uses the XML solution format, which is supported by the .NET 10 SDK and current IDEs.

### Run the tests

```powershell
dotnet test OmniSpec.slnx -c Debug --filter "Category!=Hardware"
```

- Tests that need physical hardware are tagged `[Trait("Category", "Hardware")]`. They are excluded in CI and
  by the command above. Run them locally on real hardware with `--filter "Category=Hardware"` when your change
  touches hardware access.
- Run tests in the **Debug** configuration. This is the configuration CI uses.

### Run the app

```powershell
dotnet run --project src/OmniSpec.App -c Debug
```

To work on the UI without relying on your own hardware, start a Debug build with the `--mock` flag:

```powershell
dotnet run --project src/OmniSpec.App -c Debug -- --mock
```

The mock provider is **development-only**. It is compiled only into Debug builds, and all mock data is clearly
labelled **MOCK** in the UI. It never ships in Release builds.

### Portable mode

If a file named `portable.txt` exists next to `OmniSpec.exe`, OmniSpec stores its settings and logs in a `data`
folder beside the executable instead of `%LOCALAPPDATA%\OmniSpec`. This is handy for testing a clean
configuration without touching your normal settings.

### Publishing a portable single-file build locally

```powershell
dotnet publish src/OmniSpec.App/OmniSpec.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:Version=1.0.0 -o publish/win-x64
```

Official releases are built by the GitHub Actions release workflow (`.github/workflows/release.yml`).

## Architecture

The solution is split into layers. Dependencies flow downward; `OmniSpec.Core` depends on nothing else in
the solution.

| Project | Responsibility |
| --- | --- |
| `src/OmniSpec.Core` | Models, interfaces and pure logic (metric states, sensor mapping, calculations). No I/O, no UI, no platform calls. |
| `src/OmniSpec.Hardware` | Hardware sensor provider built on LibreHardwareMonitor. |
| `src/OmniSpec.SystemInformation` | Static system information from WMI, Win32 and Windows Storage APIs. |
| `src/OmniSpec.Monitoring` | Polling, provider orchestration and PresentMon-based FPS measurement. |
| `src/OmniSpec.Infrastructure` | Settings, logging and report export. |
| `src/OmniSpec.App` | WPF user interface using MVVM (CommunityToolkit.Mvvm). Produces `OmniSpec.exe`. |
| `tests/OmniSpec.Tests` | xUnit tests. |

Keep logic that can be tested without hardware in `OmniSpec.Core`, and keep view models free of direct
hardware or WMI calls.

## Golden rules

These rules are non-negotiable. Pull requests that break them will not be merged.

1. **Never fabricate values.** Every metric carries a state (`Available`, `Unsupported`, `Unavailable`,
   `PermissionRequired` or `Error`) and a source. If a value cannot be read, report the appropriate state;
   do not substitute a guess, a default, an estimate presented as a measurement, or a value from another sensor.
2. **Never interpret vendor-specific raw SMART attributes without a verified mapping.** Many SMART attribute
   IDs mean different things on different drives and firmware. Only decode an attribute when there is a
   documented, verified mapping for that vendor/model; otherwise show the raw value as raw.
3. **No telemetry and no network calls.** OmniSpec works offline and never uploads data. Do not add analytics,
   crash reporting services, update checks or any other outbound network traffic.
4. **No hardware control.** OmniSpec reads; it does not write. Fan control, voltage changes, overclocking or
   any other hardware control requires a separate, reviewed safety design before any code is accepted.
5. **Mock data never in Release.** Mock/demo providers must stay behind Debug-only compilation, must be
   clearly labelled MOCK, and must never be reachable in a Release build.

## Coding style

- Formatting and analyzer preferences are defined in [`.editorconfig`](.editorconfig). Run
  `dotnet format OmniSpec.slnx` before submitting if your editor does not apply them automatically.
- Use file-scoped namespaces, nullable reference types, and `_camelCase` for private fields.
- Prefer immutable records for models and keep public APIs small.
- Hardware and system calls can fail on real machines: catch failures at the provider boundary, log them, and
  surface them as `Error`/`Unavailable` states rather than crashing the UI.
- Polling happens on background threads; never block the UI thread with hardware, WMI or file I/O.
- Write tests for new logic in `OmniSpec.Core`. Tag any test that requires real hardware with
  `[Trait("Category", "Hardware")]`.
- Do not add new third-party dependencies without discussing it in an issue first, and update the
  third-party notices when a dependency is added or changed.

## Pull request process

1. Open an issue first for anything larger than a small fix, so the approach can be agreed on.
2. Fork the repository and create a topic branch from `main`.
3. Keep pull requests focused: one logical change per PR.
4. Make sure `dotnet build OmniSpec.slnx` succeeds and
   `dotnet test OmniSpec.slnx -c Debug --filter "Category!=Hardware"` passes.
5. Update documentation (README, CHANGELOG under an "Unreleased" heading, third-party notices) when behaviour,
   settings or dependencies change.
6. Fill in the pull request template checklist. CI must be green before review.
7. A maintainer will review your change; please be patient and responsive to feedback.

For hardware-related changes, mention the hardware you tested on (CPU, GPU, motherboard, storage) in the PR
description.

## Adding a new hardware provider

Live sensor providers implement `IHardwareSensorProvider`, defined in the `OmniSpec.Core.Hardware` namespace
(`src/OmniSpec.Core/Hardware/HardwareModels.cs`).

1. Create your provider class in an appropriate project (usually `src/OmniSpec.Hardware`) implementing
   `IHardwareSensorProvider`.
2. Implement the members:
   - `Name`: a human-readable name used as the metric source.
   - `IsMock`: must be `false` for any real provider.
   - `Status`: a `ProviderStatus` reflecting whether initialization succeeded.
   - `Privileges` / `LowLevelAccessAvailable`: report honestly whether elevated or ring-0 sensors can be read,
     so the UI can show `PermissionRequired` instead of empty values.
   - `Initialize(HardwareProviderOptions)`: open the underlying library once; respect the enabled categories.
   - `Poll()`: refresh and return a `HardwareSnapshot`. It is called from a background thread and never
     concurrently.
   - `Dispose()`: release all native handles.
3. When a sensor exists but returns no value, its `Value` must be `null` (or the sensor omitted). Never invent
   readings.
4. Do not install, load or bundle kernel drivers from your provider. OmniSpec never installs drivers.
5. Add unit tests for any mapping logic (put pure logic in `OmniSpec.Core` where possible) and tag tests that
   need the real device with `[Trait("Category", "Hardware")]`.
6. Register the provider where the app composes its services, and document any new third-party dependency
   and its license.

## Reporting bugs and requesting features

Use the issue forms on GitHub:

- **Bug report** for crashes and incorrect behaviour.
- **Hardware support** for missing or wrong sensors. Please attach the TXT report exported from OmniSpec
  (Export).
- **Feature request** for new ideas.

Remember to remove serial numbers and other personal information from logs and reports before posting them.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
