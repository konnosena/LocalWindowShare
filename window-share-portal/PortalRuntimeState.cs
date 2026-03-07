internal sealed class PortalRuntimeState
{
    private readonly object _sync = new();
    private readonly PortalSettingsStore _settingsStore;
    private string _token;
    private string _tokenModeLabel;
    private int _port;
    private NetworkAccessPolicy _accessPolicy;
    private PortalWindowPlacement? _windowPlacement;

    public PortalRuntimeState(PortalSettings settings, PortalSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        _token = settings.Token;
        _tokenModeLabel = settings.TokenModeLabel;
        _port = settings.Port;
        _accessPolicy = new NetworkAccessPolicy(settings.Port);
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

    public IReadOnlyList<string> ListenUrls => CurrentAccessPolicy.DisplayUrls.ToArray();

    public IReadOnlyList<string> AllowedAddressLabels => CurrentAccessPolicy.BindAddresses
        .Select(address => address.ToString())
        .ToArray();

    public IReadOnlyList<string> AllowedNetworkLabels => CurrentAccessPolicy.AllowedNetworkLabels.ToArray();

    public string SettingsPath { get; }

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
        _settingsStore.Save(normalizedToken, currentPort, windowPlacement);
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
        var nextAccessPolicy = new NetworkAccessPolicy(port);
        var windowPlacement = WindowPlacement;
        _settingsStore.Save(token, port, windowPlacement);

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
        _settingsStore.Save(token, port, windowPlacement);

        lock (_sync)
        {
            _windowPlacement = windowPlacement;
        }
    }
}
