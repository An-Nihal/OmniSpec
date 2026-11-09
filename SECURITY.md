# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 1.x     | Yes       |
| < 1.0   | No        |

Security fixes are released as new 1.x versions. Please update to the latest release before reporting.

## Reporting a vulnerability

**Please do not report security vulnerabilities in public issues, discussions or pull requests.**

Report vulnerabilities privately through GitHub's private vulnerability reporting:

1. Go to the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Describe the issue, the affected OmniSpec version, your Windows version, and steps to reproduce.
   Include a proof of concept if you have one.

The maintainers will acknowledge your report, investigate, and keep you informed of progress. Once a fix is
released, we are happy to credit you in the release notes unless you prefer to remain anonymous.

## Scope and security model

Useful context when assessing OmniSpec's attack surface:

- **Privileges.** Installed copies start elevated through the scheduled task described below, because several
  sensors (CPU temperature/power, motherboard sensors, SMART) require administrator rights. The portable build runs
  with standard user rights and only elevates when the user starts an action that needs it (e.g. FPS capture).
- **Automatic administrator mode.** The installer (which already runs elevated) sets this up; users can turn it off in
  Settings › Sensor access. OmniSpec registers the scheduled task
  `\OmniSpec\OmniSpec (administrator)` with "run with highest privileges" for that user, so later starts need no UAC
  prompt. Creating the task always requires one UAC consent. To prevent privilege escalation, registration is refused
  unless `OmniSpec.exe` is located under Program Files (writable only by administrators); the installer therefore
  installs per-machine. The task is removed by the Settings toggle or on uninstall. Reports about weaknesses in this
  mechanism are especially welcome.
- **The app never installs drivers.** The OmniSpec application does not install or bundle any kernel driver or
  service. The *installer* bundles the official signed `PawnIO_setup.exe` 2.2.0 (fetched in CI from a pinned
  release URL and verified against a pinned SHA-256) and installs it silently when PawnIO is not already present.
- **PresentMon is bundled unmodified.** The Intel PresentMon console exe (signed by Intel) is fetched in CI from a
  pinned release, verified by SHA-256 and shipped in `tools\`. It runs only while the user captures FPS.
- **No network access.** OmniSpec works offline. It has no telemetry, no update check, and makes no network
  uploads.
- **Local data only.** Settings and logs are stored in `%LOCALAPPDATA%\OmniSpec`, or in a `data` folder beside
  `OmniSpec.exe` when portable mode is enabled (a `portable.txt` file next to the exe).

Reports about vulnerabilities in third-party components (for example LibreHardwareMonitorLib, PawnIO or
PresentMon) should also be sent to those projects. If such a vulnerability affects OmniSpec specifically,
please report it to us as well.
