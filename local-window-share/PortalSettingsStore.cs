using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class PortalSettingsStore
{
    private const int CurrentAccessRulesVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public PortalSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalWindowShare",
            "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public PortalSettings Load(string? initialToken, int initialPort)
    {
        PortalSettingsDocument? payload = null;
        try
        {
            if (File.Exists(_settingsPath))
            {
                payload = JsonSerializer.Deserialize<PortalSettingsDocument>(File.ReadAllText(_settingsPath));
            }
        }
        catch
        {
        }

        var persistedToken = ReadPersistedToken(payload);
        var token = !string.IsNullOrWhiteSpace(persistedToken)
            ? persistedToken.Trim()
            : !string.IsNullOrWhiteSpace(initialToken)
            ? initialToken.Trim()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var port = payload?.Port is > 0 and <= 65535
            ? payload.Port.Value
            : initialPort;
        var windowPlacement = NormalizeWindowPlacement(payload?.WindowPlacement);
        var manualAccessRules = NormalizeManualAccessRules(payload?.ManualAccessRules);
        var disabledAccessRules = ResolveDisabledAccessRules(payload, port, manualAccessRules);
        var loadedFromSettings = !string.IsNullOrWhiteSpace(persistedToken) || payload?.Port is > 0 and <= 65535;

        var clientApprovalRequired = payload?.ClientApprovalRequired ?? true;
        var approvedClients = payload?.ApprovedClients ?? [];

        Save(token, port, windowPlacement, manualAccessRules, disabledAccessRules,
            clientApprovalRequired, approvedClients);
        return new PortalSettings(
            token,
            port,
            windowPlacement,
            manualAccessRules,
            disabledAccessRules,
            loadedFromSettings
                ? "GUI settings"
                : string.IsNullOrWhiteSpace(initialToken) ? "Generated at first launch" : "Seeded from environment",
            clientApprovalRequired,
            approvedClients);
    }

    public void Save(
        string token,
        int port,
        PortalWindowPlacement? windowPlacement,
        PortalManualAccessRules? manualAccessRules,
        PortalDisabledAccessRules? disabledAccessRules,
        bool? clientApprovalRequired = null,
        ApprovedClientEntry[]? approvedClients = null)
    {
        var normalizedToken = token.Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            throw new InvalidOperationException("Token must not be empty.");
        }

        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        var directoryPath = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var temporaryPath = _settingsPath + ".tmp";
        var normalizedAccessRules = NormalizeManualAccessRules(manualAccessRules);
        var normalizedDisabledAccessRules = NormalizeDisabledAccessRules(disabledAccessRules);
        var document = new PortalSettingsDocument
        {
            TokenProtected = ProtectToken(normalizedToken),
            Port = port,
            WindowPlacement = windowPlacement,
            ManualAccessRules = normalizedAccessRules,
            DisabledAccessRules = normalizedDisabledAccessRules,
            AccessRulesConfigured = true,
            AccessRulesVersion = CurrentAccessRulesVersion,
            ClientApprovalRequired = clientApprovalRequired,
            ApprovedClients = approvedClients,
        };

        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    public void SaveClientApproval(bool approvalRequired, ApprovedClientEntry[] approvedClients)
    {
        PortalSettingsDocument? existing = null;
        try
        {
            if (File.Exists(_settingsPath))
                existing = JsonSerializer.Deserialize<PortalSettingsDocument>(File.ReadAllText(_settingsPath));
        }
        catch { }

        if (existing is null)
            return;

        var token = ReadPersistedToken(existing);
        if (string.IsNullOrWhiteSpace(token) || existing.Port is not (> 0 and <= 65535))
            return;

        Save(token, existing.Port.Value, existing.WindowPlacement,
            existing.ManualAccessRules, existing.DisabledAccessRules,
            approvalRequired, approvedClients);
    }

    private static PortalWindowPlacement? NormalizeWindowPlacement(PortalWindowPlacement? placement)
    {
        if (placement is null)
        {
            return null;
        }

        return placement.Value.Width < 160 || placement.Value.Height < 120
            ? null
            : placement;
    }

    private static PortalManualAccessRules NormalizeManualAccessRules(PortalManualAccessRules? manualAccessRules)
    {
        var source = manualAccessRules ?? PortalManualAccessRules.Empty;
        return new PortalManualAccessRules(
            NormalizeValues(source.BindAddresses, NetworkAccessPolicy.TryNormalizeBindAddressInput),
            NormalizeValues(source.AllowedAddresses, NetworkAccessPolicy.TryNormalizeAllowedAddressInput),
            NormalizeValues(source.AllowedNetworks, NetworkAccessPolicy.TryNormalizeAllowedNetworkInput));
    }

    private static PortalDisabledAccessRules ResolveDisabledAccessRules(
        PortalSettingsDocument? payload,
        int port,
        PortalManualAccessRules manualAccessRules)
    {
        var defaultDisabledRules = NetworkAccessPolicy.CreateDefaultDisabledAccessRules(port, manualAccessRules);
        if (payload?.AccessRulesConfigured != true)
        {
            return defaultDisabledRules;
        }

        var normalizedExistingRules = NormalizeDisabledAccessRules(payload.DisabledAccessRules);
        if (payload.AccessRulesVersion >= CurrentAccessRulesVersion)
        {
            return normalizedExistingRules;
        }

        return new PortalDisabledAccessRules(
            normalizedExistingRules.BindAddresses
                .Concat(defaultDisabledRules.BindAddresses)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            normalizedExistingRules.AllowedAddresses
                .Concat(defaultDisabledRules.AllowedAddresses)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            normalizedExistingRules.AllowedNetworks
                .Concat(defaultDisabledRules.AllowedNetworks)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static PortalDisabledAccessRules NormalizeDisabledAccessRules(PortalDisabledAccessRules? disabledAccessRules)
    {
        var source = disabledAccessRules ?? PortalDisabledAccessRules.Empty;
        return new PortalDisabledAccessRules(
            NormalizeValues(source.BindAddresses, NetworkAccessPolicy.TryNormalizeBindAddressInput),
            NormalizeValues(source.AllowedAddresses, NetworkAccessPolicy.TryNormalizeAllowedAddressInput),
            NormalizeValues(source.AllowedNetworks, NetworkAccessPolicy.TryNormalizeAllowedNetworkInput));
    }

    private static string[] NormalizeValues(IEnumerable<string>? values, TryNormalizeValue tryNormalize)
    {
        if (values is null)
        {
            return [];
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!tryNormalize(value, out var current) || !seen.Add(current))
            {
                continue;
            }

            normalized.Add(current);
        }

        return normalized.ToArray();
    }

    private static string? ReadPersistedToken(PortalSettingsDocument? payload)
    {
        if (!string.IsNullOrWhiteSpace(payload?.TokenProtected))
        {
            try
            {
                return UnprotectToken(payload.TokenProtected);
            }
            catch
            {
            }
        }

        return payload?.Token;
    }

    private static string ProtectToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectToken(string tokenProtected)
    {
        var protectedBytes = Convert.FromBase64String(tokenProtected);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class PortalSettingsDocument
    {
        public string? Token { get; init; }

        public string? TokenProtected { get; init; }

        public int? Port { get; init; }

        public PortalWindowPlacement? WindowPlacement { get; init; }

        public PortalManualAccessRules? ManualAccessRules { get; init; }

        public PortalDisabledAccessRules? DisabledAccessRules { get; init; }

        public bool? AccessRulesConfigured { get; init; }

        public int? AccessRulesVersion { get; init; }

        public bool? ClientApprovalRequired { get; init; }

        public ApprovedClientEntry[]? ApprovedClients { get; init; }
    }

    private delegate bool TryNormalizeValue(string? value, out string normalized);
}

internal sealed record PortalSettings(
    string Token,
    int Port,
    PortalWindowPlacement? WindowPlacement,
    PortalManualAccessRules ManualAccessRules,
    PortalDisabledAccessRules DisabledAccessRules,
    string TokenModeLabel,
    bool ClientApprovalRequired,
    ApprovedClientEntry[] ApprovedClients);

internal sealed record PortalManualAccessRules(string[] BindAddresses, string[] AllowedAddresses, string[] AllowedNetworks)
{
    public static PortalManualAccessRules Empty { get; } = new([], [], []);
}

internal sealed record PortalDisabledAccessRules(string[] BindAddresses, string[] AllowedAddresses, string[] AllowedNetworks)
{
    public static PortalDisabledAccessRules Empty { get; } = new([], [], []);
}

internal readonly record struct PortalWindowPlacement(int Left, int Top, int Width, int Height, bool IsMaximized);
