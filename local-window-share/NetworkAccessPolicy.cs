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

    private readonly IReadOnlyList<IPAddress> _allowedAddresses;
    private readonly IReadOnlyList<IPAddress> _blockedAddresses;
    private readonly IReadOnlyList<IpNetworkRange> _allowedNetworks;
    private readonly IReadOnlyList<IpNetworkRange> _blockedNetworks;

    public NetworkAccessPolicy(
        int port,
        PortalManualAccessRules? manualAccessRules = null,
        PortalDisabledAccessRules? disabledAccessRules = null)
    {
        Port = port;
        manualAccessRules ??= PortalManualAccessRules.Empty;
        disabledAccessRules ??= PortalDisabledAccessRules.Empty;

        var bindAddresses = new List<IPAddress>();
        var allowedAddresses = new List<IPAddress>();
        var blockedAddresses = new List<IPAddress>();
        var allowedNetworks = new List<IpNetworkRange>();
        var blockedNetworks = new List<IpNetworkRange>();
        var displayUrlEntries = new List<AccessListItem>();
        var allowedAddressEntries = new List<AccessListItem>();
        var allowedNetworkEntries = new List<AccessListItem>();
        var seenBindAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAllowedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabledBindAddresses = new HashSet<string>(disabledAccessRules.BindAddresses, StringComparer.OrdinalIgnoreCase);
        var disabledAllowedAddresses = new HashSet<string>(disabledAccessRules.AllowedAddresses, StringComparer.OrdinalIgnoreCase);
        var disabledAllowedNetworks = new HashSet<string>(disabledAccessRules.AllowedNetworks, StringComparer.OrdinalIgnoreCase);

        AddBindAddress(IPAddress.Loopback, isManual: false);
        AddBindAddress(IPAddress.IPv6Loopback, isManual: false);
        AddAllowedAddress(IPAddress.Loopback, isManual: false);
        AddAllowedAddress(IPAddress.IPv6Loopback, isManual: false);
        AddAllowedNetwork(IpNetworkRange.FromAddress(IPAddress.Loopback, 8), "127.0.0.0/8 (loopback)", isManual: false);
        AddAllowedNetwork(IpNetworkRange.FromAddress(IPAddress.IPv6Loopback, 128), "::1/128 (loopback)", isManual: false);

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

                AddBindAddress(address, isManual: false);
                AddAllowedAddress(address, isManual: false);

                var prefixLength = TryGetPrefixLength(unicast);
                if (prefixLength is null)
                {
                    continue;
                }

                var network = IpNetworkRange.FromAddress(address, prefixLength.Value);
                AddAllowedNetwork(network, $"{network.CidrNotation} ({adapterLabel})", isManual: false);
            }
        }

        foreach (var bindAddress in manualAccessRules.BindAddresses)
        {
            AddBindAddress(ParseBindAddressOrThrow(bindAddress), isManual: true);
        }

        foreach (var allowedAddress in manualAccessRules.AllowedAddresses)
        {
            AddAllowedAddress(ParseAllowedAddressOrThrow(allowedAddress), isManual: true);
        }

        foreach (var allowedNetwork in manualAccessRules.AllowedNetworks)
        {
            var network = ParseAllowedNetworkOrThrow(allowedNetwork);
            AddAllowedNetwork(network, network.CidrNotation, isManual: true);
        }

        BindAddresses = bindAddresses.ToArray();
        _allowedAddresses = allowedAddresses.ToArray();
        _blockedAddresses = blockedAddresses.ToArray();
        _allowedNetworks = allowedNetworks.ToArray();
        _blockedNetworks = blockedNetworks.ToArray();
        DisplayUrlEntries = displayUrlEntries.ToArray();
        AllowedAddressEntries = allowedAddressEntries.ToArray();
        AllowedNetworkEntries = allowedNetworkEntries.ToArray();
        DisplayUrls = DisplayUrlEntries.Where(entry => entry.IsEnabled).Select(entry => entry.DisplayText).ToArray();
        AllowedAddressLabels = AllowedAddressEntries.Where(entry => entry.IsEnabled).Select(entry => entry.DisplayText).ToArray();
        AllowedNetworkLabels = AllowedNetworkEntries.Where(entry => entry.IsEnabled).Select(entry => entry.DisplayText).ToArray();
        if (DisplayUrls.Count == 0)
        {
            throw new InvalidOperationException("少なくとも 1 つのアクセス URL を ON にしてください。");
        }

        PrimaryDisplayUrl = DisplayUrls.FirstOrDefault(url => !url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) && !url.Contains("[::1]", StringComparison.OrdinalIgnoreCase))
            ?? DisplayUrls.First();

        return;

        void AddBindAddress(IPAddress address, bool isManual)
        {
            var normalized = NormalizeAddress(address);
            var rawValue = normalized.ToString();
            if (!seenBindAddresses.Add(rawValue))
            {
                return;
            }

            var isEnabled = !disabledBindAddresses.Contains(rawValue);
            if (isEnabled)
            {
                bindAddresses.Add(normalized);
            }

            displayUrlEntries.Add(new AccessListItem(rawValue, $"http://{FormatAddressForUrl(normalized)}:{Port}/", isEnabled, isManual));
        }

        void AddAllowedAddress(IPAddress address, bool isManual)
        {
            var normalized = NormalizeAddress(address);
            var rawValue = normalized.ToString();
            if (!seenAllowedAddresses.Add(rawValue))
            {
                return;
            }

            var isEnabled = !disabledAllowedAddresses.Contains(rawValue);
            if (isEnabled)
            {
                allowedAddresses.Add(normalized);
            }
            else
            {
                blockedAddresses.Add(normalized);
            }

            allowedAddressEntries.Add(new AccessListItem(rawValue, rawValue, isEnabled, isManual));
        }

        void AddAllowedNetwork(IpNetworkRange network, string displayText, bool isManual)
        {
            if (!seenNetworks.Add(network.CidrNotation))
            {
                return;
            }

            var isEnabled = !disabledAllowedNetworks.Contains(network.CidrNotation);
            if (isEnabled)
            {
                allowedNetworks.Add(network);
            }
            else
            {
                blockedNetworks.Add(network);
            }

            allowedNetworkEntries.Add(new AccessListItem(network.CidrNotation, displayText, isEnabled, isManual));
        }
    }

    public int Port { get; }

    public IReadOnlyList<IPAddress> BindAddresses { get; }

    public IReadOnlyList<AccessListItem> DisplayUrlEntries { get; }

    public IReadOnlyList<string> DisplayUrls { get; }

    public string PrimaryDisplayUrl { get; }

    public IReadOnlyList<AccessListItem> AllowedAddressEntries { get; }

    public IReadOnlyList<string> AllowedAddressLabels { get; }

    public IReadOnlyList<AccessListItem> AllowedNetworkEntries { get; }

    public IReadOnlyList<string> AllowedNetworkLabels { get; }

    public bool IsAllowed(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        var normalized = NormalizeAddress(remoteAddress);
        if (_allowedAddresses.Any(address => address.Equals(normalized)))
        {
            return true;
        }

        if (_blockedAddresses.Any(address => address.Equals(normalized)))
        {
            return false;
        }

        if (_blockedNetworks.Any(network => network.Contains(normalized)))
        {
            return false;
        }

        return _allowedNetworks.Any(network => network.Contains(normalized));
    }

    public static string NormalizeBindAddressOrThrow(string? value)
    {
        return ParseBindAddressOrThrow(value).ToString();
    }

    public static string NormalizeAllowedAddressOrThrow(string? value)
    {
        return ParseAllowedAddressOrThrow(value).ToString();
    }

    public static string NormalizeAllowedNetworkOrThrow(string? value)
    {
        return ParseAllowedNetworkOrThrow(value).CidrNotation;
    }

    public static bool TryNormalizeBindAddressInput(string? value, out string normalized)
    {
        return TryNormalize(() => NormalizeBindAddressOrThrow(value), out normalized);
    }

    public static bool TryNormalizeAllowedAddressInput(string? value, out string normalized)
    {
        return TryNormalize(() => NormalizeAllowedAddressOrThrow(value), out normalized);
    }

    public static bool TryNormalizeAllowedNetworkInput(string? value, out string normalized)
    {
        return TryNormalize(() => NormalizeAllowedNetworkOrThrow(value), out normalized);
    }

    public static bool IsLoopbackBindAddress(string? value)
    {
        return TryNormalize(() => NormalizeBindAddressOrThrow(value), out var normalized) &&
            IPAddress.TryParse(normalized, out var address) &&
            IPAddress.IsLoopback(address);
    }

    public static bool IsLoopbackAllowedAddress(string? value)
    {
        return TryNormalize(() => NormalizeAllowedAddressOrThrow(value), out var normalized) &&
            IPAddress.TryParse(normalized, out var address) &&
            IPAddress.IsLoopback(address);
    }

    public static bool IsLoopbackAllowedNetwork(string? value)
    {
        if (!IpNetworkRange.TryParse(value, out var network))
        {
            return false;
        }

        return network.AddressFamily == AddressFamily.InterNetwork &&
            network.PrefixLength == 8 &&
            network.NetworkAddress.Equals(IPAddress.Parse("127.0.0.0")) ||
            network.AddressFamily == AddressFamily.InterNetworkV6 &&
            network.PrefixLength == 128 &&
            network.NetworkAddress.Equals(IPAddress.IPv6Loopback);
    }

    public static PortalDisabledAccessRules CreateDefaultDisabledAccessRules(int port, PortalManualAccessRules? manualAccessRules = null)
    {
        var policy = new NetworkAccessPolicy(port, manualAccessRules, PortalDisabledAccessRules.Empty);
        return new PortalDisabledAccessRules(
            policy.DisplayUrlEntries
                .Where(entry => !IsLoopbackBindAddress(entry.RawValue))
                .Select(entry => entry.RawValue)
                .ToArray(),
            policy.AllowedAddressEntries
                .Where(entry => !IsLoopbackAllowedAddress(entry.RawValue))
                .Select(entry => entry.RawValue)
                .ToArray(),
            policy.AllowedNetworkEntries
                .Where(entry => !IsLoopbackAllowedNetwork(entry.RawValue))
                .Select(entry => entry.RawValue)
                .ToArray());
    }

    public static bool IsIpv4AddressValue(string? value)
    {
        return TryNormalize(() => NormalizeBindAddressOrThrow(value), out var normalized) &&
            IPAddress.TryParse(normalized, out var address) &&
            address.AddressFamily == AddressFamily.InterNetwork;
    }

    public static bool IsIpv6AddressValue(string? value)
    {
        return TryNormalize(() => NormalizeBindAddressOrThrow(value), out var normalized) &&
            IPAddress.TryParse(normalized, out var address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6;
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

    private static IPAddress ParseBindAddressOrThrow(string? value)
    {
        var address = ParseAddressOrThrow(value, "アクセス URL");
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            throw new InvalidOperationException("アクセス URL には 0.0.0.0 や :: のようなワイルドカードではなく、この PC に割り当てられた具体的な IP を指定してください。");
        }

        if (!IPAddress.IsLoopback(address) && !IsAssignedToLocalInterface(address))
        {
            throw new InvalidOperationException($"アクセス URL 用の IP {address} は、この PC のネットワーク インターフェイスに割り当てられていません。");
        }

        return address;
    }

    private static IPAddress ParseAllowedAddressOrThrow(string? value)
    {
        var address = ParseAddressOrThrow(value, "許可 IP");
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            throw new InvalidOperationException("許可 IP には具体的な IP アドレスを指定してください。");
        }

        return address;
    }

    private static IpNetworkRange ParseAllowedNetworkOrThrow(string? value)
    {
        if (!IpNetworkRange.TryParse(value, out var network))
        {
            throw new InvalidOperationException("許可ネットワークは CIDR 形式で入力してください。例: 192.168.1.0/24");
        }

        return network;
    }

    private static IPAddress ParseAddressOrThrow(string? value, string label)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException($"{label} が空です。");
        }

        if (IPAddress.TryParse(candidate, out var address))
        {
            return NormalizeAddress(address);
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (IPAddress.TryParse(uri.Host, out var addressFromUrl))
            {
                return NormalizeAddress(addressFromUrl);
            }

            throw new InvalidOperationException($"{label} には IP アドレス、または IP アドレスを含む URL を指定してください。");
        }

        throw new InvalidOperationException($"{label} には有効な IP アドレス、または IP アドレスを含む URL を指定してください。");
    }

    private static bool IsAssignedToLocalInterface(IPAddress address)
    {
        var normalized = NormalizeAddress(address);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (NormalizeAddress(unicast.Address).Equals(normalized))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string FormatAddressForUrl(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        return $"[{address.ToString().Replace("%", "%25", StringComparison.Ordinal)}]";
    }

    private static bool TryNormalize(Func<string> normalize, out string normalized)
    {
        try
        {
            normalized = normalize();
            return true;
        }
        catch
        {
            normalized = string.Empty;
            return false;
        }
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

    public static bool TryParse(string? value, out IpNetworkRange network)
    {
        network = null!;
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var separatorIndex = candidate.IndexOf('/');
        if (separatorIndex <= 0 || separatorIndex == candidate.Length - 1)
        {
            return false;
        }

        var addressPart = candidate[..separatorIndex];
        var prefixPart = candidate[(separatorIndex + 1)..];
        if (!IPAddress.TryParse(addressPart, out var address) || !int.TryParse(prefixPart, out var prefixLength))
        {
            return false;
        }

        var normalizedAddress = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var maxPrefixLength = normalizedAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            return false;
        }

        network = FromAddress(normalizedAddress, prefixLength);
        return true;
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

internal sealed record AccessListItem(string RawValue, string DisplayText, bool IsEnabled, bool IsManual)
{
    public override string ToString() => DisplayText;
}
