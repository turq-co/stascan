// Проверка USB-подключения устройств ввода: сколько «чипов» (хабов и чипсет) стоит между устройством и CPU.
//
// Алгоритм и таблица контроллеров портированы из проекта cpu-direct-usb (USB Latency Analyzer V2):
//   https://github.com/MariusHeier/cpu-direct-usb
//   Copyright (c) 2025 Marius Heier, MIT License (полный текст в THIRD_PARTY_NOTICES.md).
// Таблица в оригинале собрана из Linux xhci-pci.c и pci.ids.
// Отличия от оригинала: хабы определяются по USB-классу 09, а не по слову «Hub» в имени;
// одинаковые устройства в разных портах не склеиваются; используется CfgMgr32 вместо Get-PnpDevice.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StaScan;

enum ControllerKind { Cpu, Thunderbolt, Chipset, AddOn, Unknown }

sealed record UsbController(string InstanceId, string Vid, string Did, string Name, ControllerKind Kind, int ChipCount, string Platform)
{
    public string KindText => Kind switch
    {
        ControllerKind.Cpu => "внутри CPU",
        ControllerKind.Thunderbolt => "Thunderbolt (напрямую)",
        ControllerKind.Chipset => "чипсет",
        ControllerKind.AddOn => "отдельный чип на плате / PCIe-карта",
        _ => "неизвестный",
    };
}

sealed record UsbInputDevice(string Name, string Kind, string Vid, string Pid, List<string> Hubs, UsbController Controller)
{
    public int ChipCount => Controller.ChipCount + Hubs.Count;
}

static class UsbTopology
{
    public static (List<UsbInputDevice> devices, List<UsbController> controllers) Scan()
    {
        var all = Cm.ListDeviceIds("USB");

        // Все контроллеры: родители корневых хабов
        var controllers = new Dictionary<string, UsbController>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in all.Where(i => i.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)))
        {
            var ctrlId = Cm.ParentId(id);
            if (ctrlId != null && !controllers.ContainsKey(ctrlId))
                controllers[ctrlId] = Classify(ctrlId);
        }

        // Устройства ввода: интерфейсы класса HID (03); узел устройства — сам узел или родитель-композит
        var nodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // device node -> kind
        foreach (var id in all)
        {
            var compat = Cm.GetStringList(id, Cm.CompatibleIds);
            if (!compat.Any(c => c.StartsWith(@"USB\Class_03", StringComparison.OrdinalIgnoreCase))) continue;

            var node = id.Contains("&MI_", StringComparison.OrdinalIgnoreCase) ? Cm.ParentId(id) ?? id : id;
            string kind = compat.Any(c => c.Contains("SubClass_01&Prot_02", StringComparison.OrdinalIgnoreCase)) ? "мышь"
                        : compat.Any(c => c.Contains("SubClass_01&Prot_01", StringComparison.OrdinalIgnoreCase)) ? "клавиатура"
                        : "";
            nodes.TryGetValue(node, out var prev);
            nodes[node] = MergeKinds(prev, kind);
        }

        var devices = new List<UsbInputDevice>();
        foreach (var (node, kind) in nodes)
        {
            var hubs = new List<string>();
            UsbController? ctrl = null;
            var current = Cm.ParentId(node);
            for (int i = 0; current != null && i < 15; i++)
            {
                if (current.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
                {
                    var ctrlId = Cm.ParentId(current);
                    if (ctrlId != null)
                    {
                        if (!controllers.TryGetValue(ctrlId, out ctrl))
                            controllers[ctrlId] = ctrl = Classify(ctrlId);
                    }
                    break;
                }
                if (Cm.GetStringList(current, Cm.CompatibleIds).Any(c => c.StartsWith(@"USB\Class_09", StringComparison.OrdinalIgnoreCase)))
                    hubs.Add(DisplayName(current));
                current = Cm.ParentId(current);
            }
            if (ctrl == null) continue;

            var m = Regex.Match(node, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
            devices.Add(new UsbInputDevice(DisplayName(node), kind, m.Groups[1].Value.ToUpper(), m.Groups[2].Value.ToUpper(), hubs, ctrl));
        }

        return (devices.OrderBy(d => d.ChipCount).ThenBy(d => d.Name).ToList(),
                controllers.Values.OrderBy(c => c.ChipCount).ThenBy(c => c.Name).ToList());
    }

    static string MergeKinds(string? a, string b)
    {
        var parts = new[] { a, b }.Where(s => !string.IsNullOrEmpty(s)).SelectMany(s => s!.Split(" + ")).Distinct();
        return string.Join(" + ", parts);
    }

    static string DisplayName(string id)
    {
        foreach (var key in new[] { Cm.BusReportedDeviceDesc, Cm.FriendlyName, Cm.DeviceDesc })
        {
            var s = Cm.GetString(id, key)?.Trim();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return id;
    }

    static UsbController Classify(string instanceId)
    {
        var vid = Regex.Match(instanceId, "VEN_([0-9A-Fa-f]{4})").Groups[1].Value.ToLowerInvariant();
        var did = Regex.Match(instanceId, "DEV_([0-9A-Fa-f]{4})").Groups[1].Value.ToLowerInvariant();
        var friendly = DisplayName(instanceId);

        UsbController Make(ControllerKind k, int chips, string name, string platform) =>
            new(instanceId, vid, did, name, k, chips, platform);

        if (vid == "8086" && IntelCpu.TryGetValue(did, out var e)) return Make(ControllerKind.Cpu, 0, e.Name, e.Platform);
        if (vid == "8086" && IntelThunderbolt.TryGetValue(did, out e)) return Make(ControllerKind.Thunderbolt, 0, e.Name, e.Platform);
        if (vid == "8086" && IntelPch.TryGetValue(did, out e)) return Make(ControllerKind.Chipset, 1, e.Name, e.Platform);
        if (vid == "1022" && AmdCpu.TryGetValue(did, out e)) return Make(ControllerKind.Cpu, 0, e.Name, e.Platform);
        if (vid == "1022" && AmdChipset.TryGetValue(did, out e)) return Make(ControllerKind.Chipset, 1, e.Name, e.Platform);
        if (ThirdParty.TryGetValue($"{vid}:{did}", out e)) return Make(ControllerKind.AddOn, 1, e.Name, e.Platform);
        if (AddOnVendors.TryGetValue(vid, out var vendor)) return Make(ControllerKind.AddOn, 1, $"{vendor} Controller", "PCIe / отдельный чип");
        if (vid == "8086") return Make(ControllerKind.Chipset, 1, friendly, $"Intel, нет в базе (DID {did}), считаю чипсетом");
        if (vid == "1022") return Make(ControllerKind.Chipset, 1, friendly, $"AMD, нет в базе (DID {did}), считаю чипсетом");
        return Make(ControllerKind.Unknown, 1, friendly, $"VID {vid} DID {did}, нет в базе");
    }

    readonly record struct Entry(string Name, string Platform);

    static Dictionary<string, Entry> T(params (string did, string name, string platform)[] rows) =>
        rows.ToDictionary(r => r.did, r => new Entry(r.name, r.platform), StringComparer.OrdinalIgnoreCase);

    // ── Таблицы из cpu-direct-usb (MIT) ──────────────────────────────────────────────

    static readonly Dictionary<string, Entry> IntelCpu = T(
        ("8a13", "Ice Lake Thunderbolt 3 USB Controller", "Ice Lake (10th Gen)"),
        ("9a13", "Tiger Lake-LP Thunderbolt 4 USB Controller", "Tiger Lake (11th Gen)"),
        ("9a17", "Tiger Lake-H Thunderbolt 4 USB Controller", "Tiger Lake-H (11th Gen)"),
        ("461e", "Alder Lake-P Thunderbolt 4 USB Controller", "Alder Lake (12th Gen)"),
        ("464e", "Alder Lake-N Processor USB 3.2 xHCI Controller", "Alder Lake-N"),
        ("a71e", "Raptor Lake-P Thunderbolt 4 USB Controller", "Raptor Lake (13th Gen)"),
        ("7ec0", "Meteor Lake-P Thunderbolt 4 USB Controller", "Meteor Lake (Core Ultra)"),
        ("a831", "Lunar Lake-M Thunderbolt 4 USB Controller", "Lunar Lake"));

    static readonly Dictionary<string, Entry> IntelThunderbolt = T(
        ("5782", "JHL9580 Thunderbolt 5 USB Controller", "Barlow Ridge Host 80G"),
        ("5785", "JHL9540 Thunderbolt 4 USB Controller", "Barlow Ridge Host 40G"),
        ("5787", "JHL9480 Thunderbolt 5 USB Controller", "Barlow Ridge Hub 80G"),
        ("57a5", "JHL9440 Thunderbolt 4 USB Controller", "Barlow Ridge Hub 40G"),
        ("1138", "Thunderbolt 4 USB Controller [Maple Ridge 4C]", "Maple Ridge 4C"),
        ("1135", "Thunderbolt 4 USB Controller [Maple Ridge 2C]", "Maple Ridge 2C"),
        ("0b27", "Thunderbolt 4 USB Controller [Goshen Ridge]", "Goshen Ridge"),
        ("15e9", "JHL7540 Thunderbolt 3 USB Controller [Titan Ridge 2C]", "Titan Ridge 2C"),
        ("15ec", "JHL7540 Thunderbolt 3 USB Controller [Titan Ridge 4C]", "Titan Ridge 4C"),
        ("15f0", "JHL7440 Thunderbolt 3 USB Controller [Titan Ridge DD]", "Titan Ridge DD"),
        ("15b5", "DSL6340 USB 3.1 Controller [Alpine Ridge 2C]", "Alpine Ridge 2C"),
        ("15b6", "DSL6540 USB 3.1 Controller [Alpine Ridge 4C]", "Alpine Ridge 4C"),
        ("15c1", "JHL6240 Thunderbolt 3 USB 3.1 Controller [Alpine Ridge LP]", "Alpine Ridge LP"),
        ("15d4", "JHL6540 Thunderbolt 3 USB Controller [Alpine Ridge 4C]", "Alpine Ridge 4C C-step"),
        ("15db", "JHL6340 Thunderbolt 3 USB 3.1 Controller [Alpine Ridge 2C]", "Alpine Ridge 2C C-step"));

    static readonly Dictionary<string, Entry> IntelPch = T(
        ("7f6e", "800 Series PCH USB 3.1 xHCI HC", "800 Series PCH"),
        ("7a60", "Raptor Lake USB 3.2 Gen 2x2 XHCI Host Controller", "700 Series PCH"),
        ("7a61", "Raptor Lake USB 3.2 Gen 1x1 xDCI Device Controller", "700 Series PCH"),
        ("7ae0", "Alder Lake-S PCH USB 3.2 Gen 2x2 XHCI Controller", "600 Series PCH (Desktop)"),
        ("51ed", "Alder Lake PCH USB 3.2 xHCI Host Controller", "600 Series PCH"),
        ("54ed", "Alder Lake-N PCH USB 3.2 Gen 2x1 xHCI Host Controller", "Alder Lake-N PCH"),
        ("7e7d", "Meteor Lake-P USB 3.2 Gen 2x1 xHCI Host Controller", "Meteor Lake PCH"),
        ("777d", "Arrow Lake USB 3.2 xHCI Controller", "Arrow Lake"),
        ("a87d", "Lunar Lake-M USB 3.2 Gen 2x1 xHCI Host Controller", "Lunar Lake PCH"),
        ("a0ed", "Tiger Lake-LP USB 3.2 Gen 2x1 xHCI Host Controller", "500 Series PCH"),
        ("43ed", "Tiger Lake-H USB 3.2 Gen 2x1 xHCI Host Controller", "500 Series PCH-H"),
        ("a3af", "Comet Lake PCH-V USB Controller", "400 Series PCH"),
        ("02ed", "Comet Lake PCH-LP USB 3.1 xHCI Host Controller", "400 Series PCH-LP"),
        ("06ed", "Comet Lake USB 3.1 xHCI Host Controller", "400 Series PCH"),
        ("a36d", "Cannon Lake PCH USB 3.1 xHCI Host Controller", "300 Series PCH"),
        ("9ded", "Cannon Point-LP USB 3.1 xHCI Controller", "300 Series PCH-LP"),
        ("a2af", "200 Series/Z370 Chipset Family USB 3.0 xHCI Controller", "200 Series PCH"),
        ("a12f", "100 Series/C230 Series Chipset Family USB 3.0 xHCI Controller", "100 Series PCH"),
        ("9d2f", "Sunrise Point-LP USB 3.0 xHCI Controller", "100 Series PCH-LP"),
        ("8cb1", "9 Series Chipset Family USB xHCI Controller", "9 Series PCH"),
        ("9cb1", "Wildcat Point-LP USB xHCI Controller", "9 Series PCH-LP"),
        ("8c31", "8 Series/C220 Series Chipset Family USB xHCI", "8 Series PCH"),
        ("9c31", "8 Series USB xHCI HC", "8 Series PCH-LP"),
        ("1e31", "7 Series/C210 Series Chipset Family USB xHCI Host Controller", "7 Series PCH"),
        ("8d31", "C610/X99 series chipset USB xHCI Host Controller", "X99/C610 (HEDT/Server)"),
        ("a1af", "C620 Series Chipset Family USB 3.0 xHCI Controller", "C620 (Server)"));

    static readonly Dictionary<string, Entry> AmdCpu = T(
        ("15b6", "Raphael/Granite Ridge USB 3.1 xHCI", "Ryzen 7000/9000 Desktop (AM5)"),
        ("15b7", "Raphael/Granite Ridge USB 3.1 xHCI", "Ryzen 7000/9000 Desktop (AM5)"),
        ("15b8", "Raphael/Granite Ridge USB 2.0 xHCI", "Ryzen 7000/9000 Desktop (AM5)"),
        ("1587", "Strix Halo USB 3.1 xHCI", "Strix Halo (Zen 5)"),
        ("1588", "Strix Halo USB 3.1 xHCI", "Strix Halo (Zen 5)"),
        ("1589", "Strix Halo USB 3.1 xHCI", "Strix Halo (Zen 5)"),
        ("158b", "Strix Halo USB 3.1 xHCI", "Strix Halo (Zen 5)"),
        ("158d", "Strix Halo USB4 Host Router", "Strix Halo (Zen 5)"),
        ("158e", "Strix Halo USB4 Host Router", "Strix Halo (Zen 5)"),
        ("161a", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("161b", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("161c", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("161d", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("161e", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("161f", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("15d6", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("15d7", "Rembrandt USB4 XHCI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("162e", "Rembrandt USB4/Thunderbolt NHI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("162f", "Rembrandt USB4/Thunderbolt NHI controller", "Ryzen 6000 Mobile (Zen 3+)"),
        ("15c4", "Phoenix USB4/Thunderbolt NHI controller", "Ryzen 7040 Mobile (Zen 4)"),
        ("15c5", "Phoenix USB4/Thunderbolt NHI controller", "Ryzen 7040 Mobile (Zen 4)"),
        ("1668", "Pink Sardine USB4/Thunderbolt NHI controller", "Pink Sardine"),
        ("1669", "Pink Sardine USB4/Thunderbolt NHI controller", "Pink Sardine"),
        ("1639", "Renoir/Cezanne USB 3.1", "Ryzen 4000/5000 APU (Zen 2/3)"),
        ("15e0", "Raven USB 3.1", "Ryzen 2000 APU (Zen)"),
        ("15e1", "Raven USB 3.1", "Ryzen 2000 APU (Zen)"),
        ("15e5", "Raven2 USB 3.1", "Ryzen 3000 APU (Zen+)"),
        ("149c", "Matisse USB 3.0 Host Controller", "Ryzen 3000/5000 Desktop (Zen 2/3)"),
        ("148c", "Starship USB 3.0 Host Controller", "EPYC Rome / Threadripper 3rd Gen"),
        ("145f", "Zeppelin USB 3.0 xHCI Compliant Host Controller", "Ryzen 1000 (Zen)"),
        ("145c", "Family 17h USB 3.0 Host Controller", "Ryzen 1000 (Zen)"),
        ("162c", "VanGogh USB2", "Steam Deck (Van Gogh)"),
        ("163a", "VanGogh USB0", "Steam Deck (Van Gogh)"),
        ("163b", "VanGogh USB1", "Steam Deck (Van Gogh)"),
        ("15d4", "FireFlight USB 3.1", "FireFlight"),
        ("15d5", "FireFlight USB 3.1", "FireFlight"),
        ("13ed", "Ariel USB 3.1 Type C (Gen2 + DP Alt)", "Ariel"),
        ("13ee", "Ariel USB 3.1 Type A (Gen2 x 2 ports)", "Ariel"),
        ("1557", "Turin USB 3.1 xHCI", "EPYC Turin"));

    static readonly Dictionary<string, Entry> AmdChipset = T(
        ("43fc", "800 Series Chipset USB 3.x XHCI Controller", "X870/B850 (AM5)"),
        ("43fd", "800 Series Chipset USB 3.x XHCI Controller", "X870/B850 (AM5)"),
        ("43f7", "600 Series Chipset USB 3.2 Controller", "X670/B650 (AM5)"),
        ("43ee", "500 Series Chipset USB 3.1 XHCI Controller", "X570/B550 (AM4)"),
        ("43ec", "A520 Series Chipset USB 3.1 XHCI Controller", "A520 (AM4)"),
        ("43d5", "400 Series Chipset USB 3.1 xHCI Compliant Host Controller", "X470/B450 (AM4)"),
        ("43b9", "X370 Series Chipset USB 3.1 xHCI Controller", "X370 (AM4)"),
        ("43ba", "X399 Series Chipset USB 3.1 xHCI Controller", "X399 (Threadripper)"),
        ("43bb", "300 Series Chipset USB 3.1 xHCI Controller", "B350 (AM4)"),
        ("43bc", "A320 USB 3.1 XHCI Host Controller", "A320 (AM4)"),
        ("7814", "FCH USB XHCI Controller", "Legacy FCH"),
        ("7812", "FCH USB XHCI Controller", "Legacy FCH"));

    static readonly Dictionary<string, Entry> ThirdParty = T(
        ("1b21:1042", "ASMedia ASM1042 SuperSpeed USB Host Controller", "PCIe Add-in"),
        ("1b21:1142", "ASMedia ASM1042A USB 3.0 Host Controller", "PCIe Add-in"),
        ("1b21:1242", "ASMedia ASM1142 USB 3.1 Host Controller", "PCIe Add-in"),
        ("1b21:1343", "ASMedia ASM1143 USB 3.1 Host Controller", "PCIe Add-in"),
        ("1b21:2142", "ASMedia ASM2142/ASM3142 USB 3.1 Host Controller", "PCIe Add-in"),
        ("1b21:3042", "ASMedia ASM3042 USB 3.2 Gen 1 xHCI Controller", "PCIe Add-in"),
        ("1b21:3142", "ASMedia ASM3142 USB 3.2 Gen 2x1 xHCI Controller", "PCIe Add-in"),
        ("1b21:3242", "ASMedia ASM3242 USB 3.2 Host Controller", "PCIe Add-in"),
        ("1b21:2425", "ASMedia ASM4242 USB 4 / Thunderbolt 3 Host Router", "PCIe Add-in"),
        ("1b21:2426", "ASMedia ASM4242 USB 3.2 xHCI Controller", "PCIe Add-in"),
        ("1106:3483", "VIA VL805/806 xHCI USB 3.0 Controller", "PCIe Add-in"),
        ("1106:3432", "VIA VL800/801 xHCI USB 3.0 Controller", "PCIe Add-in"),
        ("1b73:1000", "Fresco Logic FL1000G USB 3.0 Host Controller", "PCIe Add-in"),
        ("1b73:1009", "Fresco Logic FL1009 USB 3.0 Host Controller", "PCIe Add-in"),
        ("1b73:1100", "Fresco Logic FL1100 USB 3.0 Host Controller", "PCIe Add-in"),
        ("1b73:1400", "Fresco Logic FL1400 USB 3.0 Host Controller", "PCIe Add-in"),
        ("1b6f:7023", "Etron EJ168 USB 3.0 Host Controller", "PCIe Add-in"),
        ("1b6f:7052", "Etron EJ188/EJ198 USB 3.0 Host Controller", "PCIe Add-in"),
        ("1912:0014", "Renesas uPD720201 USB 3.0 Host Controller", "PCIe Add-in"),
        ("1912:0015", "Renesas uPD720202 USB 3.0 Host Controller", "PCIe Add-in"));

    static readonly Dictionary<string, string> AddOnVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1b21"] = "ASMedia", ["1106"] = "VIA", ["1b73"] = "Fresco Logic",
        ["1912"] = "Renesas", ["1b6f"] = "Etron", ["104c"] = "Texas Instruments",
    };

    // ── CfgMgr32 ─────────────────────────────────────────────────────────────────────

    static class Cm
    {
        const int CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
        const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        static readonly Guid DeviceGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
        public static readonly DEVPROPKEY DeviceDesc = new() { fmtid = DeviceGuid, pid = 2 };
        public static readonly DEVPROPKEY CompatibleIds = new() { fmtid = DeviceGuid, pid = 4 };
        public static readonly DEVPROPKEY FriendlyName = new() { fmtid = DeviceGuid, pid = 14 };
        public static readonly DEVPROPKEY BusReportedDeviceDesc = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_ID_List_SizeW(out int length, string? filter, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int length, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out int parent, int devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_IDW(int devInst, char[] buffer, int length, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_PropertyW(int devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref int size, int flags);

        public static List<string> ListDeviceIds(string enumerator)
        {
            const int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out int len, enumerator, flags) != CR_SUCCESS || len == 0) return new();
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW(enumerator, buf, len, flags) != CR_SUCCESS) return new();
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static string? ParentId(string id)
        {
            if (CM_Locate_DevNodeW(out int inst, id, 0) != CR_SUCCESS) return null;
            if (CM_Get_Parent(out int parent, inst, 0) != CR_SUCCESS) return null;
            var buf = new char[512];
            return CM_Get_Device_IDW(parent, buf, buf.Length, 0) == CR_SUCCESS ? new string(buf).TrimEnd('\0') : null;
        }

        static byte[]? GetRaw(string id, DEVPROPKEY key)
        {
            if (CM_Locate_DevNodeW(out int inst, id, 0) != CR_SUCCESS) return null;
            int size = 0;
            int cr = CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
            if (cr != CR_BUFFER_SMALL || size == 0) return null;
            var buf = new byte[size];
            return CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) == CR_SUCCESS ? buf : null;
        }

        public static string? GetString(string id, DEVPROPKEY key)
        {
            var raw = GetRaw(id, key);
            return raw == null ? null : Encoding.Unicode.GetString(raw).TrimEnd('\0');
        }

        public static List<string> GetStringList(string id, DEVPROPKEY key)
        {
            var raw = GetRaw(id, key);
            return raw == null ? new() : Encoding.Unicode.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }
}
