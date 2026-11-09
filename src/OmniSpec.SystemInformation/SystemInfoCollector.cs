using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.SystemInfo;
using OmniSpec.SystemInformation.Native;
using OmniSpec.SystemInformation.Wmi;

namespace OmniSpec.SystemInformation;

/// <summary>
/// Collects static system information from WMI, Win32 APIs and the registry. Runs at startup and on manual refresh.
/// Every section is collected independently so one failing source never hides the others.
/// Serial numbers and other unique identifiers are marked sensitive and hidden by default.
/// </summary>
public sealed class SystemInfoCollector : ISystemInfoProvider
{
    private readonly ILogger<SystemInfoCollector> _logger;

    public SystemInfoCollector(ILogger<SystemInfoCollector> logger) => _logger = logger;

    public Task<SystemInfoReport> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private SystemInfoReport Collect(CancellationToken ct)
    {
        var sections = new List<InfoSection>();
        CpuStaticInfo? cpu = null;
        var gpus = new List<GpuStaticInfo>();
        IReadOnlyList<DisplayInfo> displays = Array.Empty<DisplayInfo>();
        var osName = string.Empty;
        ulong? installedMemory = null;

        void Run(string title, string source, Func<InfoSection> collect)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                sections.Add(collect());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Collecting {Section} failed", title);
                sections.Add(InfoSection.Error(title, source, ex));
            }
        }

        Run("Operating System", DataSources.Wmi, () => CollectOs(out osName));
        Run("Processor", DataSources.Wmi, () => CollectCpu(out cpu));
        Run("Motherboard & Firmware", DataSources.Wmi, CollectBoard);
        Run("Memory", DataSources.Wmi, () => CollectMemory(out installedMemory));
        Run("Graphics", DataSources.Wmi, () => CollectGpus(gpus));
        Run("Displays", DataSources.DisplayConfig, () => CollectDisplays(out displays));
        Run("Audio Devices", DataSources.Wmi, CollectAudio);
        Run("Network Adapters", DataSources.Wmi, CollectNetworkAdapters);
        Run("Bluetooth", DataSources.Wmi, () => CollectPnpClass("Bluetooth", "SELECT Name, Manufacturer, Status FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth' AND Present = TRUE"));
        Run("USB Devices", DataSources.Wmi, () => CollectPnpClass("USB Devices", "SELECT Name, PNPClass, Status FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%' AND Present = TRUE"));
        Run("PCI Devices", DataSources.Wmi, () => CollectPnpClass("PCI Devices", "SELECT Name, PNPClass, Status FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\%' AND Present = TRUE"));
        Run("Power", DataSources.Wmi, CollectPower);

        return new SystemInfoReport(DateTimeOffset.Now, sections, cpu, gpus, displays, osName, Environment.MachineName, installedMemory);
    }

    // ------------------------------------------------------------------ OS
    private static InfoSection CollectOs(out string osName)
    {
        var items = new List<InfoItem>();
        var os = WmiQuery.Query("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, Locale FROM Win32_OperatingSystem");
        var cs = WmiQuery.Query("SELECT Manufacturer, Model, SystemType, HypervisorPresent, Domain, PartOfDomain FROM Win32_ComputerSystem");
        var o = os.Rows.FirstOrDefault();
        var c = cs.Rows.FirstOrDefault();

        string? displayVersion = null, edition = null, ubr = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            displayVersion = key?.GetValue("DisplayVersion") as string;
            edition = key?.GetValue("EditionID") as string;
            ubr = key?.GetValue("UBR")?.ToString();
        }
        catch { /* registry optional */ }

        osName = o?.Str("Caption") ?? RuntimeInformation.OSDescription;
        items.Add(InfoItem.Of("Windows edition", osName, DataSources.Wmi));
        items.Add(InfoItem.Of("Edition ID", edition, DataSources.Registry));
        items.Add(InfoItem.Of("Version", displayVersion is null ? o?.Str("Version") : $"{displayVersion} ({o?.Str("Version")})", DataSources.Registry));
        items.Add(InfoItem.Of("Build", ubr is null ? o?.Str("BuildNumber") : $"{o?.Str("BuildNumber")}.{ubr}", DataSources.Registry));
        items.Add(InfoItem.Of("Architecture", o?.Str("OSArchitecture") ?? RuntimeInformation.OSArchitecture.ToString(), DataSources.Wmi));
        items.Add(InfoItem.Of("Installation date", o?.Date("InstallDate")?.ToString("D", CultureInfo.CurrentCulture), DataSources.Wmi));
        var boot = o?.Date("LastBootUpTime");
        items.Add(InfoItem.Of("Last boot", boot?.ToString("f", CultureInfo.CurrentCulture), DataSources.Wmi));
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        items.Add(InfoItem.Of("Uptime (at collection)", $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m", DataSources.Win32));
        items.Add(InfoItem.Of("Computer manufacturer", c?.Str("Manufacturer"), DataSources.Wmi));
        items.Add(InfoItem.Of("Computer model", c?.Str("Model"), DataSources.Wmi));
        items.Add(InfoItem.Of("System type", c?.Str("SystemType"), DataSources.Wmi));
        items.Add(InfoItem.Of("Hostname", Environment.MachineName, DataSources.Win32, sensitive: true));
        items.Add(InfoItem.Of("Domain / Workgroup", c?.Str("Domain"), DataSources.Wmi, sensitive: true));
        items.Add(InfoItem.Of("Current user", Environment.UserName, DataSources.Win32, sensitive: true));
        items.Add(InfoItem.Of("Session", $"Session {System.Diagnostics.Process.GetCurrentProcess().SessionId}, {(IsElevated() ? "administrator (elevated)" : "standard user")}", DataSources.Win32));
        items.Add(InfoItem.Of("Hypervisor present", c?.Bool("HypervisorPresent") switch { true => "Yes", false => "No", _ => null }, DataSources.Wmi));
        items.Add(InfoItem.Of("Locale", CultureInfo.CurrentCulture.DisplayName, DataSources.Win32));
        if (!os.Ok) items.Add(InfoItem.Missing("WMI status", os.State, DataSources.Wmi, os.Error));
        return new InfoSection("Operating System", items);
    }

    // ------------------------------------------------------------------ CPU
    private static InfoSection CollectCpu(out CpuStaticInfo? cpu)
    {
        var items = new List<InfoItem>();
        var res = WmiQuery.Query("SELECT Name, Manufacturer, Caption, Family, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, SocketDesignation, VirtualizationFirmwareEnabled, SecondLevelAddressTranslationExtensions, VMMonitorModeExtensions, L2CacheSize, L3CacheSize, ProcessorId FROM Win32_Processor");
        var rows = res.Rows;
        var p = rows.FirstOrDefault();
        var topo = CpuTopology.Current;

        var name = p?.Str("Name") ?? "Unknown processor";
        var manufacturer = p?.Str("Manufacturer");
        var physical = topo.PhysicalCores > 0 ? topo.PhysicalCores : (int?)rows.Sum(r => (int)(r.U64("NumberOfCores") ?? 0));
        var logical = topo.LogicalProcessors.Count > 0 ? topo.LogicalProcessors.Count : Environment.ProcessorCount;
        var baseClock = p?.Dbl("MaxClockSpeed");

        items.Add(InfoItem.Of("Processor", name, DataSources.Wmi));
        items.Add(InfoItem.Of("Manufacturer", manufacturer switch { "GenuineIntel" => "Intel", "AuthenticAMD" => "AMD", var m => m }, DataSources.Wmi));
        items.Add(InfoItem.Of("Family / Model / Stepping", p?.Str("Caption"), DataSources.Wmi));
        items.Add(InfoItem.Of("Architecture", RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : RuntimeInformation.OSArchitecture.ToString(), DataSources.Win32));
        items.Add(InfoItem.Of("Socket", p?.Str("SocketDesignation"), DataSources.Wmi));
        if (rows.Count > 1) items.Add(InfoItem.Of("Physical packages", rows.Count.ToString(CultureInfo.InvariantCulture), DataSources.Wmi));
        items.Add(InfoItem.Of("Physical cores", physical?.ToString(CultureInfo.InvariantCulture), DataSources.Win32));
        items.Add(InfoItem.Of("Logical processors", logical.ToString(CultureInfo.InvariantCulture), DataSources.Win32));
        if (topo.IsHybrid)
        {
            items.Add(InfoItem.Of("Hybrid architecture", $"{topo.PerformanceCores} performance cores + {topo.EfficiencyCores} efficiency cores", DataSources.Win32));
        }
        items.Add(InfoItem.Of("Base frequency", baseClock is > 0 ? $"{baseClock:0} MHz" : null, DataSources.Wmi));
        items.Add(InfoItem.Missing("Max advertised frequency", MetricState.Unsupported, DataSources.Wmi, "Windows does not expose the advertised boost clock; see live clocks on the CPU page"));

        var virtFw = p?.Bool("VirtualizationFirmwareEnabled");
        var vmx = p?.Bool("VMMonitorModeExtensions");
        var hypervisor = NativeMethods.IsProcessorFeaturePresent(NativeMethods.PF_VIRT_FIRMWARE_ENABLED);
        string virt = (virtFw, vmx) switch
        {
            (true, _) => "Supported, enabled in firmware",
            (false, true) => "Supported, disabled in firmware",
            (false, false) => "Not supported",
            _ => hypervisor ? "Enabled (hypervisor running — firmware state hidden by Windows)" : "Unknown",
        };
        if ((virtFw is null || virtFw == false) && WmiQuery.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem").Rows.FirstOrDefault()?.Bool("HypervisorPresent") == true)
            virt = "Enabled (a hypervisor such as Hyper-V/VBS is running)";
        items.Add(InfoItem.Of("Virtualization", virt, DataSources.Wmi));
        items.Add(InfoItem.Of("SLAT", NativeMethods.IsProcessorFeaturePresent(NativeMethods.PF_SECOND_LEVEL_ADDRESS_TRANSLATION) ? "Yes" : p?.Bool("SecondLevelAddressTranslationExtensions") == true ? "Yes" : "Not reported", DataSources.Win32));
        items.Add(InfoItem.Of("NX / DEP", NativeMethods.IsProcessorFeaturePresent(NativeMethods.PF_NX_ENABLED) ? "Enabled" : "Not enabled", DataSources.Win32));
        items.Add(InfoItem.Of("Instruction sets", InstructionSets(), "CPUID (.NET hardware intrinsics)"));

        foreach (var (cacheName, value) in topo.CacheSummary())
            items.Add(InfoItem.Of($"{cacheName} cache", value, DataSources.Win32));
        if (topo.Caches.Count == 0)
        {
            items.Add(InfoItem.Of("L2 cache", p?.U64("L2CacheSize") is > 0 and var l2 ? $"{l2} KB" : null, DataSources.Wmi));
            items.Add(InfoItem.Of("L3 cache", p?.U64("L3CacheSize") is > 0 and var l3 ? $"{l3} KB" : null, DataSources.Wmi));
        }
        items.Add(InfoItem.Missing("Thermal characteristics (TjMax/TDP)", MetricState.Unsupported, DataSources.Wmi,
            "Not exposed by Windows; live thermal data is shown on the CPU page when sensors are available"));
        items.Add(InfoItem.Of("Processor ID", p?.Str("ProcessorId"), DataSources.Wmi, sensitive: true));

        var effClasses = topo.LogicalProcessors.Select(lp => lp.CoreType == CoreType.Efficiency ? 0 : 1).ToList();
        cpu = new CpuStaticInfo(name, manufacturer ?? "Unknown", physical, logical, baseClock, effClasses);
        if (!res.Ok) items.Add(InfoItem.Missing("WMI status", res.State, DataSources.Wmi, res.Error));
        return new InfoSection("Processor", items);
    }

    private static string InstructionSets()
    {
        var list = new List<string>();
        if (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86)
        {
            void Add(bool supported, string n) { if (supported) list.Add(n); }
            Add(Sse.IsSupported, "SSE"); Add(Sse2.IsSupported, "SSE2"); Add(Sse3.IsSupported, "SSE3"); Add(Ssse3.IsSupported, "SSSE3");
            Add(Sse41.IsSupported, "SSE4.1"); Add(Sse42.IsSupported, "SSE4.2"); Add(Popcnt.IsSupported, "POPCNT"); Add(Lzcnt.IsSupported, "LZCNT");
            Add(Bmi1.IsSupported, "BMI1"); Add(Bmi2.IsSupported, "BMI2"); Add(Aes.IsSupported, "AES-NI"); Add(Pclmulqdq.IsSupported, "PCLMULQDQ");
            Add(Avx.IsSupported, "AVX"); Add(Avx2.IsSupported, "AVX2"); Add(Fma.IsSupported, "FMA3"); Add(AvxVnni.IsSupported, "AVX-VNNI");
            Add(Avx512F.IsSupported, "AVX-512F"); Add(Avx512BW.IsSupported, "AVX-512BW"); Add(Avx512CD.IsSupported, "AVX-512CD");
            Add(Avx512DQ.IsSupported, "AVX-512DQ"); Add(Avx512Vbmi.IsSupported, "AVX-512VBMI");
            Add(X86Base.IsSupported && Environment.Is64BitOperatingSystem, "x86-64");
        }
        else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            void Add(bool supported, string n) { if (supported) list.Add(n); }
            Add(System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported, "NEON (AdvSIMD)");
            Add(System.Runtime.Intrinsics.Arm.Aes.IsSupported, "AES");
            Add(System.Runtime.Intrinsics.Arm.Crc32.IsSupported, "CRC32");
            Add(System.Runtime.Intrinsics.Arm.Sha256.IsSupported, "SHA256");
            Add(System.Runtime.Intrinsics.Arm.Dp.IsSupported, "DotProd");
            Add(System.Runtime.Intrinsics.Arm.Rdm.IsSupported, "RDM");
        }
        return string.Join(", ", list);
    }

    // ------------------------------------------------------------------ Board / BIOS
    private static InfoSection CollectBoard()
    {
        var items = new List<InfoItem>();
        var board = WmiQuery.Query("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard").Rows.FirstOrDefault();
        var bios = WmiQuery.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS").Rows.FirstOrDefault();

        items.Add(InfoItem.Of("Manufacturer", board?.Str("Manufacturer"), DataSources.Wmi));
        items.Add(InfoItem.Of("Model", board?.Str("Product"), DataSources.Wmi));
        var revision = board?.Str("Version");
        items.Add(revision is null or "Default string" or "To be filled by O.E.M."
            ? InfoItem.Missing("Board revision", MetricState.Unavailable, DataSources.Wmi, "Not provided by the manufacturer")
            : InfoItem.Of("Board revision", revision, DataSources.Wmi));
        items.Add(Chipset());
        items.Add(InfoItem.Of("Board serial", board?.Str("SerialNumber"), DataSources.Wmi, sensitive: true));

        items.Add(InfoItem.Of("BIOS manufacturer", bios?.Str("Manufacturer"), DataSources.Wmi));
        items.Add(InfoItem.Of("BIOS version", bios?.Str("SMBIOSBIOSVersion"), DataSources.Wmi));
        items.Add(InfoItem.Of("BIOS release date", bios?.Date("ReleaseDate")?.ToString("d", CultureInfo.CurrentCulture), DataSources.Wmi));
        var smbiosMajor = bios?.U64("SMBIOSMajorVersion");
        items.Add(InfoItem.Of("SMBIOS version", smbiosMajor is null ? null : $"{smbiosMajor}.{bios?.U64("SMBIOSMinorVersion")}", DataSources.Wmi));

        string? firmware = null;
        try
        {
            if (NativeMethods.GetFirmwareType(out var ft))
                firmware = ft switch { NativeMethods.FirmwareType.Uefi => "UEFI", NativeMethods.FirmwareType.Bios => "Legacy BIOS", _ => null };
        }
        catch { /* not available */ }
        items.Add(InfoItem.Of("Firmware type", firmware, DataSources.Win32));
        items.Add(SecureBoot(firmware));
        items.Add(InfoItem.Of("BIOS serial", bios?.Str("SerialNumber"), DataSources.Wmi, sensitive: true));
        return new InfoSection("Motherboard & Firmware", items);
    }

    private static InfoItem SecureBoot(string? firmware)
    {
        if (firmware == "Legacy BIOS") return InfoItem.Missing("Secure Boot", MetricState.Unsupported, DataSources.Registry, "Requires UEFI firmware");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            var v = key?.GetValue("UEFISecureBootEnabled");
            if (v is int i) return InfoItem.Of("Secure Boot", i == 1 ? "Enabled" : "Disabled", DataSources.Registry);
            return InfoItem.Missing("Secure Boot", MetricState.Unavailable, DataSources.Registry, "State not exposed");
        }
        catch (UnauthorizedAccessException)
        {
            return InfoItem.Missing("Secure Boot", MetricState.PermissionRequired, DataSources.Registry);
        }
        catch (Exception ex)
        {
            return InfoItem.Missing("Secure Boot", MetricState.Error, DataSources.Registry, ex.Message);
        }
    }

    private static InfoItem Chipset()
    {
        // The chipset is not exposed directly by Windows. Intel platforms name their LPC/eSPI controller after the chipset
        // (e.g. "Intel(R) Z690 Chipset LPC/eSPI Controller"), so we only report a chipset when such a device name exists.
        var res = WmiQuery.Query("SELECT Name FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\%' AND (Name LIKE '%LPC%' OR Name LIKE '%eSPI%' OR Name LIKE '%Chipset%')");
        var name = res.Rows.Select(r => r.Str("Name")).FirstOrDefault(n => n is not null && n.Contains("Chipset", StringComparison.OrdinalIgnoreCase))
                   ?? res.Rows.Select(r => r.Str("Name")).FirstOrDefault(n => n is not null && (n.Contains("LPC", StringComparison.OrdinalIgnoreCase) || n.Contains("eSPI", StringComparison.OrdinalIgnoreCase)));
        return name is null
            ? InfoItem.Missing("Chipset", MetricState.Unavailable, DataSources.Wmi, "Not identifiable from Windows device names on this platform")
            : InfoItem.Of("Chipset (from LPC/eSPI controller name)", name, DataSources.Wmi);
    }

    // ------------------------------------------------------------------ Memory
    private static InfoSection CollectMemory(out ulong? installed)
    {
        var items = new List<InfoItem>();
        installed = NativeMethods.GetPhysicallyInstalledSystemMemory(out var kb) ? kb * 1024 : null;
        var modules = WmiQuery.Query("SELECT BankLabel, DeviceLocator, Capacity, Manufacturer, PartNumber, SerialNumber, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor, DataWidth, ConfiguredVoltage FROM Win32_PhysicalMemory");
        var array = WmiQuery.Query("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray");

        var totalSlots = array.Rows.Sum(r => (long)(r.U64("MemoryDevices") ?? 0));
        items.Add(InfoItem.Of("Installed memory", installed is null ? null : Format.Bytes(installed.Value), DataSources.Win32));
        items.Add(InfoItem.Of("Usable by Windows", Format.Bytes((ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), DataSources.Win32));

        var types = modules.Rows.Select(r => MemoryType(r.U64("SMBIOSMemoryType"))).Where(t => t is not null).Distinct().ToList();
        items.Add(InfoItem.Of("Memory type", types.Count > 0 ? string.Join(", ", types) : null, DataSources.Wmi));
        var speed = modules.Rows.Select(r => r.U64("Speed")).Where(s => s is > 0).Distinct().ToList();
        items.Add(InfoItem.Of("Rated speed", speed.Count > 0 ? string.Join(", ", speed.Select(s => $"{s} MT/s")) : null, DataSources.Wmi));
        var configured = modules.Rows.Select(r => r.U64("ConfiguredClockSpeed")).Where(s => s is > 0).Distinct().ToList();
        items.Add(InfoItem.Of("Configured speed", configured.Count > 0 ? string.Join(", ", configured.Select(s => $"{s} MT/s")) : null, DataSources.Wmi));
        items.Add(InfoItem.Of("Modules installed", modules.Rows.Count.ToString(CultureInfo.InvariantCulture), DataSources.Wmi));
        items.Add(InfoItem.Of("Total slots", totalSlots > 0 ? totalSlots.ToString(CultureInfo.InvariantCulture) : null, DataSources.Wmi));
        items.Add(InfoItem.Missing("Channel configuration", MetricState.Unsupported, DataSources.Wmi,
            "Windows does not report channel mode; slot labels below indicate physical placement"));
        items.Add(InfoItem.Missing("Timings", MetricState.Unsupported, DataSources.Wmi, "Memory timings are not exposed by Windows"));

        var children = new List<InfoSection>();
        foreach (var m in modules.Rows)
        {
            var slot = m.Str("DeviceLocator") ?? m.Str("BankLabel") ?? $"Module {children.Count + 1}";
            var voltage = m.U64("ConfiguredVoltage");
            children.Add(new InfoSection(slot, new List<InfoItem>
            {
                InfoItem.Of("Capacity", m.U64("Capacity") is ulong cap ? Format.Bytes(cap) : null, DataSources.Wmi),
                InfoItem.Of("Type", MemoryType(m.U64("SMBIOSMemoryType")), DataSources.Wmi),
                InfoItem.Of("Form factor", FormFactor(m.U64("FormFactor")), DataSources.Wmi),
                InfoItem.Of("Manufacturer", CleanVendor(m.Str("Manufacturer")), DataSources.Wmi),
                InfoItem.Of("Part number", m.Str("PartNumber"), DataSources.Wmi),
                InfoItem.Of("Rated speed", m.U64("Speed") is > 0 and var s ? $"{s} MT/s" : null, DataSources.Wmi),
                InfoItem.Of("Configured speed", m.U64("ConfiguredClockSpeed") is > 0 and var cs ? $"{cs} MT/s" : null, DataSources.Wmi),
                InfoItem.Of("Configured voltage", voltage is > 0 ? $"{voltage / 1000.0:0.00} V" : null, DataSources.Wmi),
                InfoItem.Of("Bank label", m.Str("BankLabel"), DataSources.Wmi),
                InfoItem.Of("Slot", m.Str("DeviceLocator"), DataSources.Wmi),
                InfoItem.Of("Serial number", m.Str("SerialNumber"), DataSources.Wmi, sensitive: true),
            }));
        }
        if (!modules.Ok) items.Add(InfoItem.Missing("Module details", modules.State, DataSources.Wmi, modules.Error));
        return new InfoSection("Memory", items, children);
    }

    public static string? MemoryType(ulong? smbiosType) => smbiosType switch
    {
        18 => "DDR", 19 => "DDR2", 20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4",
        27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 31 => "Logical non-volatile", 32 => "HBM",
        33 => "HBM2", 34 => "DDR5", 35 => "LPDDR5", 36 => "HBM3",
        _ => null,
    };

    private static string? FormFactor(ulong? ff) => ff switch
    {
        8 => "DIMM", 12 => "SO-DIMM", 13 => "SRIMM", 9 => "TSOP", 11 => "RIMM", 0 => null, null => null, _ => $"Form factor {ff}",
    };

    private static string? CleanVendor(string? v) => v is null || v.All(ch => ch == '0' || ch == ' ') || v.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : v;

    // ------------------------------------------------------------------ GPUs
    private static InfoSection CollectGpus(List<GpuStaticInfo> gpus)
    {
        var res = WmiQuery.Query("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, Status, PNPDeviceID FROM Win32_VideoController");
        var children = new List<InfoSection>();
        foreach (var g in res.Rows)
        {
            var name = g.Str("Name") ?? "Graphics adapter";
            var driverDate = g.Date("DriverDate")?.ToString("d", CultureInfo.CurrentCulture);
            var ram = g.U64("AdapterRAM");
            gpus.Add(new GpuStaticInfo(name, g.Str("DriverVersion"), driverDate, ram, g.Str("VideoProcessor")));
            var items = new List<InfoItem>
            {
                InfoItem.Of("Name", name, DataSources.Wmi),
                InfoItem.Of("Vendor", Core.Live.SensorMapping.VendorOf(name, g.Str("PNPDeviceID") ?? ""), DataSources.Calculated),
                InfoItem.Of("Driver version", g.Str("DriverVersion"), DataSources.Wmi),
                InfoItem.Of("Driver date", driverDate, DataSources.Wmi),
                InfoItem.Of("Video processor", g.Str("VideoProcessor"), DataSources.Wmi),
                // AdapterRAM is a 32-bit field and wraps above 4 GB; live VRAM totals come from the GPU sensors instead.
                ram is > 0 and < 4_000_000_000UL
                    ? InfoItem.Of("Adapter memory (WMI)", Format.Bytes(ram.Value), DataSources.Wmi)
                    : InfoItem.Missing("Adapter memory (WMI)", MetricState.Unavailable, DataSources.Wmi, "WMI field limited to 4 GB; see GPU page for VRAM"),
                InfoItem.Of("Status", g.Str("Status"), DataSources.Wmi),
                InfoItem.Missing("PCIe link", MetricState.Unsupported, DataSources.Wmi, "PCIe generation/width is not exposed through standard Windows APIs"),
            };
            children.Add(new InfoSection(name, items));
        }
        var top = new List<InfoItem> { InfoItem.Of("Adapters", res.Rows.Count.ToString(CultureInfo.InvariantCulture), DataSources.Wmi) };
        if (!res.Ok) top.Add(InfoItem.Missing("WMI status", res.State, DataSources.Wmi, res.Error));
        return new InfoSection("Graphics", top, children);
    }

    // ------------------------------------------------------------------ Displays
    private static InfoSection CollectDisplays(out IReadOnlyList<DisplayInfo> displays)
    {
        displays = DisplayReader.ReadActiveDisplays();
        var children = displays.Select((d, i) => new InfoSection($"Display {i + 1}: {d.MonitorName}", new List<InfoItem>
        {
            InfoItem.Of("Monitor", d.MonitorName, DataSources.DisplayConfig),
            InfoItem.Of("Windows device", d.GdiDeviceName, DataSources.DisplayConfig),
            InfoItem.Of("Resolution", d.Width is null ? null : $"{d.Width} × {d.Height}", DataSources.DisplayConfig),
            InfoItem.Of("Refresh rate", d.RefreshRateHz is null ? null : $"{d.RefreshRateHz:0.##} Hz", DataSources.DisplayConfig),
            InfoItem.Of("Connection", d.Connection, DataSources.DisplayConfig),
            InfoItem.Of("HDR supported", d.HdrSupported switch { true => "Yes", false => "No", _ => null }, DataSources.DisplayConfig),
            InfoItem.Of("HDR enabled", d.HdrEnabled switch { true => "Yes", false => "No", _ => null }, DataSources.DisplayConfig),
            InfoItem.Of("Bits per color", d.BitsPerColor?.ToString(CultureInfo.InvariantCulture), DataSources.DisplayConfig),
        })).ToList();
        return new InfoSection("Displays", new List<InfoItem> { InfoItem.Of("Active displays", displays.Count.ToString(CultureInfo.InvariantCulture), DataSources.DisplayConfig) }, children);
    }

    // ------------------------------------------------------------------ Audio / Network / PnP
    private static InfoSection CollectAudio()
    {
        var res = WmiQuery.Query("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice");
        var items = res.Rows.Select(r => InfoItem.Of(r.Str("Name") ?? "Audio device", $"{r.Str("Manufacturer")} · {r.Str("Status")}".Trim(' ', '·'), DataSources.Wmi)).ToList();
        if (items.Count == 0) items.Add(InfoItem.Missing("Audio devices", res.Ok ? MetricState.Unavailable : res.State, DataSources.Wmi, res.Ok ? "None detected" : res.Error));
        return new InfoSection("Audio Devices", items);
    }

    private static InfoSection CollectNetworkAdapters()
    {
        var res = WmiQuery.Query("SELECT Name, Manufacturer, AdapterType, Speed, NetConnectionStatus, NetConnectionID, MACAddress FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
        var children = res.Rows.Select(r => new InfoSection(r.Str("NetConnectionID") ?? r.Str("Name") ?? "Adapter", new List<InfoItem>
        {
            InfoItem.Of("Adapter", r.Str("Name"), DataSources.Wmi),
            InfoItem.Of("Manufacturer", r.Str("Manufacturer"), DataSources.Wmi),
            InfoItem.Of("Type", r.Str("AdapterType"), DataSources.Wmi),
            InfoItem.Of("Status", NetStatus(r.U64("NetConnectionStatus")), DataSources.Wmi),
            InfoItem.Of("Link speed", r.U64("Speed") is > 0 and < ulong.MaxValue / 2 and var sp ? Format.BitRate(sp) : null, DataSources.Wmi),
            InfoItem.Of("MAC address", r.Str("MACAddress"), DataSources.Wmi, sensitive: true),
        })).ToList();
        return new InfoSection("Network Adapters", new List<InfoItem> { InfoItem.Of("Physical adapters", children.Count.ToString(CultureInfo.InvariantCulture), DataSources.Wmi) }, children);
    }

    private static string? NetStatus(ulong? s) => s switch
    {
        0 => "Disconnected", 1 => "Connecting", 2 => "Connected", 3 => "Disconnecting", 4 => "Hardware not present",
        5 => "Hardware disabled", 6 => "Hardware malfunction", 7 => "Media disconnected", 8 => "Authenticating",
        9 => "Authentication succeeded", 10 => "Authentication failed", 11 => "Invalid address", 12 => "Credentials required",
        _ => null,
    };

    private static InfoSection CollectPnpClass(string title, string wql)
    {
        var res = WmiQuery.Query(wql);
        // Device instance IDs can embed serial numbers, so only names and classes are shown.
        var grouped = res.Rows
            .Select(r => (Name: r.Str("Name") ?? "Unknown device", Class: r.Str("PNPClass") ?? r.Str("Manufacturer"), Status: r.Str("Status")))
            .GroupBy(x => (x.Name, x.Class))
            .OrderBy(g => g.Key.Class).ThenBy(g => g.Key.Name)
            .Select(g => InfoItem.Of(g.Count() > 1 ? $"{g.Key.Name} (×{g.Count()})" : g.Key.Name, g.Key.Class ?? g.First().Status ?? "Present", DataSources.Wmi))
            .ToList();
        if (grouped.Count == 0) grouped.Add(InfoItem.Missing("Devices", res.Ok ? MetricState.Unavailable : res.State, DataSources.Wmi, res.Ok ? "None detected" : res.Error));
        return new InfoSection(title, grouped);
    }

    // ------------------------------------------------------------------ Power
    private static InfoSection CollectPower()
    {
        var items = new List<InfoItem>();
        items.Add(InfoItem.Of("Active power plan", PowerPlan.ActiveName(), DataSources.Win32));
        var batteries = WmiQuery.Query("SELECT Name, Chemistry, DesignVoltage, Status FROM Win32_Battery");
        items.Add(InfoItem.Of("Batteries", batteries.Rows.Count.ToString(CultureInfo.InvariantCulture), DataSources.Wmi));
        foreach (var b in batteries.Rows)
        {
            items.Add(InfoItem.Of("Battery", b.Str("Name"), DataSources.Wmi));
            items.Add(InfoItem.Of("Chemistry", BatteryChemistry(b.U64("Chemistry")), DataSources.Wmi));
        }
        return new InfoSection("Power", items);
    }

    public static string? BatteryChemistry(ulong? c) => c switch
    {
        1 => "Other", 3 => "Lead acid", 4 => "Nickel cadmium", 5 => "Nickel metal hydride", 6 => "Lithium-ion", 7 => "Zinc air", 8 => "Lithium polymer",
        _ => null,
    };

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

/// <summary>Formatting helpers shared by collectors.</summary>
public static class Format
{
    public static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.##} {units[u]}";
    }

    public static string BitRate(ulong bitsPerSecond) =>
        bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1e9:0.##} Gbps" :
        bitsPerSecond >= 1_000_000 ? $"{bitsPerSecond / 1e6:0.##} Mbps" : $"{bitsPerSecond / 1e3:0.##} Kbps";
}
