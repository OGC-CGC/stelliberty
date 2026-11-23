using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Stelliberty.Application.Diagnostics;
using Stelliberty.Application.Settings;

namespace Stelliberty.Infrastructure.Platform;

public static class SystemOutboundInterfaces
{
    private const string SystemConfiguration = "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private static readonly IPAddress DefaultRouteV4 = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress DefaultRouteV6 = IPAddress.Parse("2606:4700:4700::1111");
    private static readonly IPNetwork Ipv4LinkLocal = IPNetwork.Parse("169.254.0.0/16");

    public static IReadOnlyList<string> ListNames()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(IsSelectable)
                .Select(adapter => adapter.Name).Order(StringComparer.Ordinal).ToArray();
        }
        catch (NetworkInformationException exception)
        {
            AppLogger.Warning($"Outbound interface list is unavailable: {exception.Message}");
            return [];
        }
    }

    public static string? Select(AppSettings settings, string? activeTunDevice)
    {
        var mode = settings.OutboundInterfaceMode;
        var macPhysical = OperatingSystem.IsMacOS() && mode is (OutboundInterfaceMode.Physical or OutboundInterfaceMode.Virtual)
            ? ReadMacPhysicalInterfaces()
            : null;
        var defaultV4 = DefaultSourceAddress(DefaultRouteV4);
        var defaultV6 = settings.IsIpv6Enabled ? DefaultSourceAddress(DefaultRouteV6) : null;

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => IsSelectable(adapter) && adapter.OperationalStatus == OperationalStatus.Up
                && !string.Equals(adapter.Name, activeTunDevice, StringComparison.Ordinal)
                && !(settings.IsTunEnabled && string.Equals(adapter.Name, settings.TunDevice, StringComparison.Ordinal)))
            .Where(adapter => mode switch
            {
                OutboundInterfaceMode.Physical => IsPhysical(adapter, macPhysical) == true,
                OutboundInterfaceMode.Virtual => IsPhysical(adapter, macPhysical) == false,
                OutboundInterfaceMode.Blacklist => !settings.OutboundInterfaceBlacklist.Contains(adapter.Name, StringComparer.Ordinal),
                OutboundInterfaceMode.Whitelist => settings.OutboundInterfaceWhitelist.Contains(adapter.Name, StringComparer.Ordinal),
                _ => false
            })
            .Select(adapter => Describe(adapter, defaultV4, defaultV6, settings.IsIpv6Enabled))
            .Where(candidate => candidate.HasUsableAddress &&
                (candidate.IsDefaultV4 || candidate.IsDefaultV6 || candidate.HasGateway || candidate.IsPointToPoint))
            .OrderByDescending(candidate => candidate.IsDefaultV4)
            .ThenByDescending(candidate => candidate.IsDefaultV6)
            .ThenByDescending(candidate => candidate.HasGateway)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }

    private static bool IsSelectable(NetworkInterface adapter) => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback;

    private static (string Name, bool HasUsableAddress, bool IsDefaultV4, bool IsDefaultV6, bool HasGateway, bool IsPointToPoint)
        Describe(NetworkInterface adapter, IPAddress? defaultV4, IPAddress? defaultV6, bool allowIpv6)
    {
        var properties = adapter.GetIPProperties();
        var addresses = properties.UnicastAddresses;
        var hasGateway = properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork
            ? !gateway.Address.Equals(IPAddress.Any)
            : allowIpv6 && !gateway.Address.Equals(IPAddress.IPv6Any));
        var pointToPoint = adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
            || (OperatingSystem.IsMacOS() && adapter.Name.StartsWith("utun", StringComparison.Ordinal));
        return (adapter.Name, addresses.Any(unicast => IsUsableAddress(unicast.Address, allowIpv6)),
            addresses.Any(unicast => unicast.Address.Equals(defaultV4)), addresses.Any(unicast => unicast.Address.Equals(defaultV6)),
            hasGateway, pointToPoint);
    }

    private static bool IsUsableAddress(IPAddress address, bool allowIpv6) => !IPAddress.IsLoopback(address)
        && (address.AddressFamily == AddressFamily.InterNetwork
            ? !address.Equals(IPAddress.Any) && !Ipv4LinkLocal.Contains(address)
            : allowIpv6 && !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal);

    private static IPAddress? DefaultSourceAddress(IPAddress destination)
    {
        try
        {
            // UDP connect 只查询路由和本地地址，不发送数据包。
            using var socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(destination, 443));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static bool? IsPhysical(NetworkInterface adapter, HashSet<string>? macPhysical)
    {
        if (OperatingSystem.IsMacOS())
        {
            return macPhysical?.Contains(adapter.Name);
        }

        if (OperatingSystem.IsLinux())
        {
            var path = Path.Combine("/sys/class/net", adapter.Name);
            if (!Directory.Exists(path))
            {
                return null;
            }

            return Directory.Exists(Path.Combine(path, "device"));
        }

        if (OperatingSystem.IsWindows())
        {
            var properties = adapter.GetIPProperties();
            var index = adapter.Supports(NetworkInterfaceComponent.IPv4)
                ? properties.GetIPv4Properties()?.Index
                : properties.GetIPv6Properties()?.Index;
            if (index is > 0)
            {
                var row = new MibIfRow { InterfaceIndex = (uint)index.Value };
                return GetIfEntry2(ref row) == 0 ? (row.InterfaceAndOperStatusFlags & 1) != 0 : null;
            }
        }

        return null;
    }

    private static HashSet<string>? ReadMacPhysicalInterfaces()
    {
        var interfaces = SCNetworkInterfaceCopyAll();
        if (interfaces == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var count = CFArrayGetCount(interfaces);
            for (nint index = 0; index < count; index++)
            {
                var entry = CFArrayGetValueAtIndex(interfaces, index);
                var type = ReadCfString(SCNetworkInterfaceGetInterfaceType(entry));
                if (type is ("Ethernet" or "IEEE80211") && ReadCfString(SCNetworkInterfaceGetBSDName(entry)) is { } name)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        finally
        {
            CFRelease(interfaces);
        }
    }

    private static string? ReadCfString(IntPtr value)
    {
        var buffer = new byte[256];
        return value != IntPtr.Zero && CFStringGetCString(value, buffer, buffer.Length, Utf8)
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0))
            : null;
    }

    [DllImport(SystemConfiguration)]
    private static extern IntPtr SCNetworkInterfaceCopyAll();

    [DllImport(SystemConfiguration)]
    private static extern IntPtr SCNetworkInterfaceGetInterfaceType(IntPtr entry);

    [DllImport(SystemConfiguration)]
    private static extern IntPtr SCNetworkInterfaceGetBSDName(IntPtr entry);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, [Out] byte[] buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref MibIfRow row);

    // MIB_IF_ROW2 包含完整统计字段，GetIfEntry2 会写入整块结构。
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct MibIfRow
    {
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(1152)] public byte InterfaceAndOperStatusFlags;
    }
}
