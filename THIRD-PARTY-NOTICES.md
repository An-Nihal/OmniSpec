# Third-party notices

OmniSpec's own source code is licensed under the MIT License (see `LICENSE`). OmniSpec redistributes or uses the
following third-party components. Their licenses are **not** changed by OmniSpec's license. Each component remains
under its own terms.

## Redistributed in OmniSpec builds

| Component | Version | License | Source |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | Mozilla Public License 2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit (dependency of LibreHardwareMonitorLib) | 1.1.2 | Mozilla Public License 2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD (dependency of LibreHardwareMonitorLib) | 1.4.2 | Mozilla Public License 2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp (dependency of LibreHardwareMonitorLib) | 2.6.4 | Apache License 2.0 | https://www.zer7.com/software/hidsharp |
| Mono.Posix.NETStandard (dependency of LibreHardwareMonitorLib) | 1.0.0 | MIT | https://github.com/mono/mono |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection / Logging (+ Abstractions, Debug) | 10.0.x | MIT | https://github.com/dotnet/runtime |
| System.Management, System.Diagnostics.PerformanceCounter, System.IO.Ports and other .NET packages | 10.0.x | MIT | https://github.com/dotnet/runtime |
| .NET runtime and Windows Desktop runtime (bundled in self-contained builds) | 10.0.x | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms |

### Mozilla Public License 2.0 components

LibreHardwareMonitorLib, DiskInfoToolkit and RAMSPDToolkit-NDD are used **unmodified**, as binary NuGet packages.
The MPL-2.0 is a file-level copyleft license:

- **Where to get the source:** the source code of these components is available at the repositories above (for the
  exact versions, see the NuGet package versions listed here).
- **Your rights:** you may obtain, modify and redistribute those files under the terms of the MPL-2.0
  (https://mozilla.org/MPL/2.0/).
- **OmniSpec's own code:** OmniSpec only links to these libraries. Its own files remain under the MIT License, as the
  MPL-2.0 permits ("Larger Work").

If OmniSpec ever ships a modified version of an MPL-2.0 file, that file's source will be published under the MPL-2.0.

### Apache License 2.0 components

HidSharp is distributed under the Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0). No NOTICE file
modifications have been made.

## Bundled tool

| Component | Version | License | Notes |
|---|---|---|---|
| Intel PresentMon (console application) | 2.6.0 | MIT (Copyright Intel Corporation) | Shipped unmodified as `tools\PresentMon-2.6.0-x64.exe`, with its license in `tools\PresentMon-LICENSE.txt`. Source: https://github.com/GameTechDev/PresentMon. OmniSpec starts it as a child process only while capturing frames; it never installs the PresentMon service or SDK. |

## Included in the installer only

| Component | Version | License | Notes |
|---|---|---|---|
| PawnIO driver setup (`PawnIO_setup.exe`, by namazso) | 2.2.0 | GNU GPL v2.0 | Signed kernel driver used by LibreHardwareMonitorLib for low-level sensors. Shipped **unmodified** inside the OmniSpec installer (not the portable zip), taken from the official release https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0 and verified by SHA-256 `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`. Installed silently by setup when not already present. Its license text is installed to `licenses\PawnIO-COPYING.txt`. **Corresponding source code:** https://github.com/namazso/PawnIO. OmniSpec's own code only communicates with the driver through LibreHardwareMonitorLib and is not a derivative of it. |

## Trademarks

Intel, AMD, NVIDIA, Windows and other product names are trademarks of their respective owners. They are used only to
identify hardware and software. OmniSpec is not affiliated with or endorsed by these companies.
