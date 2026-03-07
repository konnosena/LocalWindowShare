using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

internal sealed class NetworkAccessPolicy
{
    private static readonly string[] VpnKeywords =
    [
        "vpn",
        "wireguard",
        "tailscale",
        "zerotier",
        "softether",
        "openvpn",
        "tap",
        "tun",
        "wg",
    ];

    private readonly IReadOnlyList<IpNetworkRange> _allowedNetworks;

    public NetworkAccessPolicy(int port)
    {
        Port = port;

        var bindAddresses = new List<IPAddress>
        {
            IPAddress.Loopback,
            IPAddress.IPv6Loopback,
        };

        var allowedNetworks = new List<IpNetworkRange>
        {
            IpNetworkRange.FromAddress(IPAddress.Loopback, 8),
            IpNetworkRange.FromAddress(IPAddress.IPv6Loopback, 128),
        };

        var allowedNetworkLabels = new List<string>
        {
            "127.0.0.0/8 (loopback)",
            "::1/128 (loopback)",
        };

        var seenBindAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            IPAddress.Loopback.ToString(),
            IPAddress.IPv6Loopback.ToString(),
        };
        var seenNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.0/8",
            "::1/128",
        };

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var adapterKind = GetAllowedAdapterKind(adapter);
            if (adapterKind == AllowedAdapterKind.None)
            {
                continue;
            }

            var adapterLabel = string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = NormalizeAddress(unicast.Address);
                if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                {
                    continue;
                }

                if (IPAddress.IsLoopback(address))
                {
                    continue;
                }

                if (adapterKind == AllowedAdapterKind.LocalLan && !IsPrivateOrLinkLocalAddress(address))
                {
                    continue;
                }

                if (seenBindAddresses.Add(address.ToString()))
                {
                    bindAddresses.Add(address);
                }

                var prefixLength = TryGetPrefixLength(unicast);
                if (prefixLength is null)
                {
                    continue;
                }

                var network = IpNetworkRange.FromAddress(address, prefixLength.Value);
                if (seenNetworks.Add(network.CidrNotation))
                {
                    allowedNetworks.Add(network);
                    allowedNetworkLabels.Add($"{network.CidrNotation} ({adapterLabel})");
                }
            }
        }

        BindAddresses = bindAddresses.ToArray();
        _allowedNetworks = allowedNetworks.ToArray();
        AllowedNetworkLabels = allowedNetworkLabels.ToArray();

        var displayUrls = BindAddresses
            .Select(address => $"http://{FormatAddressForUrl(address)}:{Port}/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        DisplayUrls = displayUrls;
        PrimaryDisplayUrl = displayUrls.FirstOrDefault(url => !url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) && !url.Contains("[::1]", StringComparison.OrdinalIgnoreCase))
            ?? displayUrls.First();
    }

    public int Port { get; }

    public IReadOnlyList<IPAddress> BindAddresses { get; }

    public IReadOnlyList<string> DisplayUrls { get; }

    public string PrimaryDisplayUrl { get; }

    public IReadOnlyList<string> AllowedNetworkLabels { get; }

    public bool IsAllowed(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        var normalized = NormalizeAddress(remoteAddress);
        return _allowedNetworks.Any(network => network.Contains(normalized));
    }

    private static AllowedAdapterKind GetAllowedAdapterKind(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return AllowedAdapterKind.None;
        }

        var adapterText = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        var isVpnAdapter = adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
            VpnKeywords.Any(keyword => adapterText.Contains(keyword, StringComparison.Ordinal));

        if (isVpnAdapter)
        {
            return AllowedAdapterKind.Vpn;
        }

        var isLocalLanAdapter = adapter.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.Wireless80211;

        if (!isLocalLanAdapter)
        {
            return AllowedAdapterKind.None;
        }

        return adapter.GetIPProperties().UnicastAddresses
            .Select(unicast => NormalizeAddress(unicast.Address))
            .Any(IsPrivateOrLinkLocalAddress)
            ? AllowedAdapterKind.LocalLan
            : AllowedAdapterKind.None;
    }

    private static bool IsPrivateOrLinkLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Loopback) ||
                   (bytes[0] & 0xFE) == 0xFC ||
                   (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }

        return false;
    }

    private static int? TryGetPrefixLength(UnicastIPAddressInformation unicast)
    {
        try
        {
            if (unicast.PrefixLength > 0)
            {
                return unicast.PrefixLength;
            }
        }
        catch
        {
        }

        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask is not null)
        {
            return CountMaskBits(unicast.IPv4Mask.GetAddressBytes());
        }

        return null;
    }

    private static int CountMaskBits(byte[] bytes)
    {
        var count = 0;
        foreach (var value in bytes)
        {
            var current = value;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((current & 0x80) == 0)
                {
                    return count;
                }

                count++;
                current <<= 1;
            }
        }

        return count;
    }

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static string FormatAddressForUrl(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        return $"[{address.ToString().Replace("%", "%25", StringComparison.Ordinal)}]";
    }
}

internal enum AllowedAdapterKind
{
    None,
    LocalLan,
    Vpn,
}

internal sealed class IpNetworkRange
{
    private readonly byte[] _networkBytes;

    private IpNetworkRange(AddressFamily family, byte[] networkBytes, int prefixLength)
    {
        AddressFamily = family;
        _networkBytes = networkBytes;
        PrefixLength = prefixLength;
        NetworkAddress = new IPAddress(networkBytes);
    }

    public AddressFamily AddressFamily { get; }

    public IPAddress NetworkAddress { get; }

    public int PrefixLength { get; }

    public string CidrNotation => $"{NetworkAddress}/{PrefixLength}";

    public bool Contains(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (normalized.AddressFamily != AddressFamily)
        {
            return false;
        }

        var candidateBytes = normalized.GetAddressBytes();
        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (_networkBytes[i] != candidateBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (_networkBytes[fullBytes] & mask) == (candidateBytes[fullBytes] & mask);
    }

    public static IpNetworkRange FromAddress(IPAddress address, int prefixLength)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var bytes = normalized.GetAddressBytes();
        var networkBytes = ApplyMask(bytes, prefixLength);
        return new IpNetworkRange(normalized.AddressFamily, networkBytes, prefixLength);
    }

    private static byte[] ApplyMask(byte[] bytes, int prefixLength)
    {
        var masked = (byte[])bytes.Clone();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = fullBytes + (remainingBits > 0 ? 1 : 0); i < masked.Length; i++)
        {
            masked[i] = 0;
        }

        if (remainingBits > 0 && fullBytes < masked.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            masked[fullBytes] &= mask;
        }

        return masked;
    }
}
