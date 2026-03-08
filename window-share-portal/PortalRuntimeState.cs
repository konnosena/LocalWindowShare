internal sealed class PortalRuntimeState
{
    private readonly object _sync = new();
    private readonly PortalSettingsStore _settingsStore;
    private string _token;
    private string _tokenModeLabel;
    private int _port;
    private PortalManualAccessRules _manualAccessRules;
    private PortalDisabledAccessRules _disabledAccessRules;
    private NetworkAccessPolicy _accessPolicy;
    private PortalWindowPlacement? _windowPlacement;

    public PortalRuntimeState(PortalSettings settings, PortalSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        _token = settings.Token;
        _tokenModeLabel = settings.TokenModeLabel;
        _port = settings.Port;
        _manualAccessRules = settings.ManualAccessRules ?? PortalManualAccessRules.Empty;
        _disabledAccessRules = settings.DisabledAccessRules ?? PortalDisabledAccessRules.Empty;
        _accessPolicy = new NetworkAccessPolicy(settings.Port, _manualAccessRules, _disabledAccessRules);
        _windowPlacement = settings.WindowPlacement;
        SettingsPath = settingsStore.SettingsPath;
    }

    public string Token => Volatile.Read(ref _token);

    public string TokenModeLabel => Volatile.Read(ref _tokenModeLabel);

    public int Port
    {
        get
        {
            lock (_sync)
            {
                return _port;
            }
        }
    }

    public NetworkAccessPolicy CurrentAccessPolicy
    {
        get
        {
            lock (_sync)
            {
                return _accessPolicy;
            }
        }
    }

    public string ListenUrl => CurrentAccessPolicy.PrimaryDisplayUrl;

    public IReadOnlyList<AccessListItem> ListenUrlEntries => CurrentAccessPolicy.DisplayUrlEntries.ToArray();

    public IReadOnlyList<string> ListenUrls => CurrentAccessPolicy.DisplayUrls.ToArray();

    public IReadOnlyList<string> AllowedAddressLabels => CurrentAccessPolicy.AllowedAddressLabels.ToArray();

    public IReadOnlyList<AccessListItem> AllowedNetworkEntries => CurrentAccessPolicy.AllowedNetworkEntries.ToArray();

    public IReadOnlyList<string> AllowedNetworkLabels => CurrentAccessPolicy.AllowedNetworkLabels.ToArray();

    public string SettingsPath { get; }

    public PortalManualAccessRules ManualAccessRules
    {
        get
        {
            lock (_sync)
            {
                return new PortalManualAccessRules(
                    _manualAccessRules.BindAddresses.ToArray(),
                    _manualAccessRules.AllowedAddresses.ToArray(),
                    _manualAccessRules.AllowedNetworks.ToArray());
            }
        }
    }

    public PortalDisabledAccessRules DisabledAccessRules
    {
        get
        {
            lock (_sync)
            {
                return new PortalDisabledAccessRules(
                    _disabledAccessRules.BindAddresses.ToArray(),
                    [],
                    []);
            }
        }
    }

    public PortalWindowPlacement? WindowPlacement
    {
        get
        {
            lock (_sync)
            {
                return _windowPlacement;
            }
        }
    }

    public void UpdateToken(string token)
    {
        var normalizedToken = token.Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            throw new InvalidOperationException("Token must not be empty.");
        }

        var currentPort = Port;
        var windowPlacement = WindowPlacement;
        var manualAccessRules = ManualAccessRules;
        var disabledAccessRules = DisabledAccessRules;
        _settingsStore.Save(normalizedToken, currentPort, windowPlacement, manualAccessRules, disabledAccessRules);
        Volatile.Write(ref _token, normalizedToken);
        Volatile.Write(ref _tokenModeLabel, "GUI settings");
    }

    public void UpdatePort(int port)
    {
        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        var token = Token;
        var manualAccessRules = ManualAccessRules;
        var disabledAccessRules = DisabledAccessRules;
        var nextAccessPolicy = new NetworkAccessPolicy(port, manualAccessRules, disabledAccessRules);
        var windowPlacement = WindowPlacement;
        _settingsStore.Save(token, port, windowPlacement, manualAccessRules, disabledAccessRules);

        lock (_sync)
        {
            _port = port;
            _accessPolicy = nextAccessPolicy;
        }
    }

    public void UpdateWindowPlacement(PortalWindowPlacement windowPlacement)
    {
        var token = Token;
        var port = Port;
        var manualAccessRules = ManualAccessRules;
        var disabledAccessRules = DisabledAccessRules;
        _settingsStore.Save(token, port, windowPlacement, manualAccessRules, disabledAccessRules);

        lock (_sync)
        {
            _windowPlacement = windowPlacement;
        }
    }

    public void UpdateManualAccessRules(PortalManualAccessRules manualAccessRules)
    {
        var normalizedAccessRules = new PortalManualAccessRules(
            manualAccessRules.BindAddresses
                .Select(NetworkAccessPolicy.NormalizeBindAddressOrThrow)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            manualAccessRules.AllowedAddresses
                .Select(NetworkAccessPolicy.NormalizeAllowedAddressOrThrow)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            manualAccessRules.AllowedNetworks
                .Select(NetworkAccessPolicy.NormalizeAllowedNetworkOrThrow)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        var token = Token;
        var port = Port;
        var windowPlacement = WindowPlacement;
        var disabledAccessRules = DisabledAccessRules;
        var nextAccessPolicy = new NetworkAccessPolicy(port, normalizedAccessRules, disabledAccessRules);
        _settingsStore.Save(token, port, windowPlacement, normalizedAccessRules, disabledAccessRules);

        lock (_sync)
        {
            _manualAccessRules = normalizedAccessRules;
            _accessPolicy = nextAccessPolicy;
        }
    }

    public void UpdateDisabledAccessRules(PortalDisabledAccessRules disabledAccessRules)
    {
        var normalizedDisabledRules = new PortalDisabledAccessRules(
            disabledAccessRules.BindAddresses
                .Select(NetworkAccessPolicy.NormalizeBindAddressOrThrow)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            disabledAccessRules.AllowedAddresses
                .Select(NetworkAccessPolicy.NormalizeAllowedAddressOrThrow)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            disabledAccessRules.AllowedNetworks
                .Select(NetworkAccessPolicy.NormalizeAllowedNetworkOrThrow)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        var token = Token;
        var port = Port;
        var windowPlacement = WindowPlacement;
        var manualAccessRules = ManualAccessRules;
        var nextAccessPolicy = new NetworkAccessPolicy(port, manualAccessRules, normalizedDisabledRules);
        _settingsStore.Save(token, port, windowPlacement, manualAccessRules, normalizedDisabledRules);

        lock (_sync)
        {
            _disabledAccessRules = normalizedDisabledRules;
            _accessPolicy = nextAccessPolicy;
        }
    }

    public void SetBindAddressEnabled(string value, bool isEnabled)
    {
        var normalized = NetworkAccessPolicy.NormalizeBindAddressOrThrow(value);
        var current = DisabledAccessRules;
        UpdateDisabledAccessRules(new PortalDisabledAccessRules(
            SetEnabledState(current.BindAddresses, normalized, isEnabled),
            current.AllowedAddresses,
            current.AllowedNetworks));
    }


    private static string[] SetEnabledState(IEnumerable<string> currentDisabledValues, string rawValue, bool isEnabled)
    {
        return isEnabled
            ? currentDisabledValues.Where(existing => !string.Equals(existing, rawValue, StringComparison.OrdinalIgnoreCase)).ToArray()
            : currentDisabledValues.Append(rawValue).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
