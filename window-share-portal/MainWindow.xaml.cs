using System.Net;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WpfListBox = System.Windows.Controls.ListBox;

internal sealed partial class MainWindow : Window
{
    private readonly PortalRuntimeState _runtimeState;
    private readonly ClientConnectionTracker _connectionTracker;
    private readonly PortalLogStore _logStore;
    private readonly PortalServer _server;
    private readonly ClientApprovalService _approvalService;
    private readonly DispatcherTimer _refreshTimer;

    private string? _lastStaticListsSignature;
    private IReadOnlyList<ClientConnectionSnapshot> _lastRenderedSnapshots = Array.Empty<ClientConnectionSnapshot>();
    private bool _initialPlacementApplied;
    private bool _startupConnectAttempted;
    private bool _actionControlsLocked;
    private bool _updatingToggleLists;
    private bool _tokenVisible;
    private LogWindow? _logWindow;

    public MainWindow(
        PortalRuntimeState runtimeState,
        ClientConnectionTracker connectionTracker,
        PortalLogStore logStore,
        PortalServer server,
        ClientApprovalService approvalService)
    {
        _runtimeState = runtimeState;
        _connectionTracker = connectionTracker;
        _logStore = logStore;
        _server = server;
        _approvalService = approvalService;

        InitializeComponent();

        TokenPasswordBox.Password = _runtimeState.Token;
        TokenTextBox.Text = _runtimeState.Token;

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        ContentRendered += OnContentRendered;

        _approvalService.ApprovalRequested += OnApprovalRequested;
        AllowAllToggle.IsChecked = !_approvalService.ApprovalRequired;
        RefreshApprovalUi();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshDynamicState();

        RefreshDynamicState();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplySavedWindowPlacement();
        _refreshTimer.Start();
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        if (_startupConnectAttempted) return;
        _startupConnectAttempted = true;
        await EnsureServerStartedOnLaunchAsync();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
        SaveWindowPlacement();
        if (_logWindow is not null)
        {
            _logWindow.Close();
            _logWindow = null;
        }
    }

    private void RefreshDynamicState()
    {
        PopulateStaticListsIfChanged();
        UpdateHeader();
        UpdatePortDisplay();
        UpdateClientList();
        RefreshActionButtonStates();
        RefreshApprovalUi();
    }

    private void UpdateHeader()
    {
        var isRunning = _server.IsRunning;

        if (isRunning)
        {
            StatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x3a, 0x1a));
            StatusBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3f, 0xb9, 0x50));
            StatusBadgeText.Text = "\u25CF \u5F85\u53D7\u4E2D";
            ServerToggleButton.Style = (Style)FindResource("DangerButton");
            ServerToggleIcon.Text = "\u23FB";
            ServerToggleText.Text = "\u505C\u6B62";
        }
        else
        {
            StatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x1a, 0x1a));
            StatusBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf8, 0x51, 0x49));
            StatusBadgeText.Text = "\u25CF \u505C\u6B62\u4E2D";
            ServerToggleButton.Style = (Style)FindResource("PrimaryButton");
            ServerToggleIcon.Text = "\u23FB";
            ServerToggleText.Text = "\u958B\u59CB";
        }

        HeaderInfoText.Text = $"\u30DD\u30FC\u30C8 {_runtimeState.Port}  \u2502  {Environment.MachineName}";
        TokenSourceLabel.Text = $"\u30BD\u30FC\u30B9: {_runtimeState.TokenModeLabel}";
        SettingsPathLabel.Text = _runtimeState.SettingsPath;
        HostnameLabel.Text = $"\u30DB\u30B9\u30C8: {Environment.MachineName}";
    }

    private void UpdatePortDisplay()
    {
        if (!PortTextBox.IsFocused)
        {
            var portStr = _runtimeState.Port.ToString();
            if (PortTextBox.Text != portStr)
                PortTextBox.Text = portStr;
        }
    }

    private void PopulateStaticListsIfChanged()
    {
        var listenUrls = _runtimeState.ListenUrlEntries;
        var listenUrlsIpv4 = listenUrls.Where(e => NetworkAccessPolicy.IsIpv4AddressValue(e.RawValue)).ToArray();
        var listenUrlsIpv6 = listenUrls.Where(e => NetworkAccessPolicy.IsIpv6AddressValue(e.RawValue)).ToArray();
        var allowedNetworks = _runtimeState.AllowedNetworkEntries;

        var signature = string.Join("\u001f", listenUrlsIpv4.Select(FormatEntrySignature)) + "\u001e" +
            string.Join("\u001f", listenUrlsIpv6.Select(FormatEntrySignature)) + "\u001e" +
            string.Join("\u001f", allowedNetworks.Select(FormatEntrySignature));

        if (string.Equals(_lastStaticListsSignature, signature, StringComparison.Ordinal))
            return;

        _updatingToggleLists = true;
        try
        {
            Ipv4ListBox.ItemsSource = listenUrlsIpv4.Select(e => ToAccessItemVmWithCidr(e, allowedNetworks)).ToList();
            Ipv6ListBox.ItemsSource = listenUrlsIpv6.Select(e => ToAccessItemVmWithCidr(e, allowedNetworks)).ToList();
        }
        finally
        {
            _updatingToggleLists = false;
        }

        _lastStaticListsSignature = signature;
    }

    private void UpdateClientList()
    {
        var snapshots = _connectionTracker.GetSnapshots();
        var activeCount = snapshots.Count(s => s.IsActive);

        ClientCountLabel.Text = !_server.IsRunning
            ? "(\u30AA\u30D5\u30E9\u30A4\u30F3)"
            : activeCount == 0
            ? "(\u63A5\u7D9A\u306A\u3057)"
            : $"({activeCount} \u4EF6\u63A5\u7D9A\u4E2D / \u5C65\u6B74 {snapshots.Count} \u4EF6)";

        if (_lastRenderedSnapshots.SequenceEqual(snapshots))
            return;

        var selectedClientId = GetSelectedClientId();
        var items = snapshots.Select(s => new ClientItemViewModel
        {
            ClientId = s.ClientId,
            RemoteAddress = s.RemoteAddress,
            EnvironmentLabel = s.EnvironmentLabel,
            LastPath = $"{s.LastMethod} {s.LastPath}",
            StatusLabel = s.IsActive ? "\u63A5\u7D9A" : "\u5C65\u6B74",
            StatusColor = s.IsActive
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3f, 0xb9, 0x50))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x48, 0x4f, 0x58)),
            LastSeen = s.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            UserAgent = s.UserAgent,
            IsActive = s.IsActive,
        }).ToList();

        ClientListBox.ItemsSource = items;

        if (!string.IsNullOrWhiteSpace(selectedClientId))
        {
            var match = items.FirstOrDefault(i => string.Equals(i.ClientId, selectedClientId, StringComparison.Ordinal));
            if (match is not null)
                ClientListBox.SelectedItem = match;
        }

        _lastRenderedSnapshots = snapshots.ToArray();
    }

    private void RefreshActionButtonStates()
    {
        if (_actionControlsLocked) return;
        DisconnectButton.IsEnabled = ClientListBox.SelectedItem is not null;
    }

    private void SetActionControlsLocked(bool locked)
    {
        _actionControlsLocked = locked;
        Ipv4ListBox.IsEnabled = !locked;
        Ipv6ListBox.IsEnabled = !locked;
        if (locked)
        {
            DisconnectButton.IsEnabled = false;
            return;
        }
        RefreshActionButtonStates();
    }

    // --- Event Handlers ---

    private async void OnServerToggleClick(object sender, RoutedEventArgs e)
    {
        await ToggleServerAsync();
    }

    private void OnToggleTokenVisibility(object sender, RoutedEventArgs e)
    {
        _tokenVisible = !_tokenVisible;
        if (_tokenVisible)
        {
            TokenTextBox.Text = TokenPasswordBox.Password;
            TokenPasswordBox.Visibility = Visibility.Collapsed;
            TokenTextBox.Visibility = Visibility.Visible;
        }
        else
        {
            TokenPasswordBox.Password = TokenTextBox.Text;
            TokenPasswordBox.Visibility = Visibility.Visible;
            TokenTextBox.Visibility = Visibility.Collapsed;
        }
    }

    private void OnGenerateToken(object sender, RoutedEventArgs e)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        TokenPasswordBox.Password = token;
        TokenTextBox.Text = token;
    }

    private void OnSaveToken(object sender, RoutedEventArgs e)
    {
        ApplyToken();
    }

    private async void OnApplyPort(object sender, RoutedEventArgs e)
    {
        await ApplyPortAsync();
    }

    private void OnAccessToggleClick(object sender, RoutedEventArgs e)
    {
        if (_updatingToggleLists || _actionControlsLocked) return;
        if (sender is not ToggleButton toggle) return;
        if (toggle.DataContext is not AccessItemViewModel item) return;

        var listBox = FindParentListBox(toggle);
        if (listBox is null) return;

        if (!string.Equals(listBox.Tag?.ToString(), "BindAddress", StringComparison.Ordinal))
            return;

        _ = ApplyAccessToggleAsync(item.RawValue, toggle.IsChecked == true);
    }

    private void OnClientSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshActionButtonStates();
    }

    private async void OnDisconnectClient(object sender, RoutedEventArgs e)
    {
        await DisconnectSelectedClientAsync();
    }

    private void OnOpenLogWindow(object sender, RoutedEventArgs e)
    {
        if (_logWindow is not null)
        {
            if (_logWindow.WindowState == WindowState.Minimized)
                _logWindow.WindowState = WindowState.Normal;
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow(_logStore) { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    private void OnAllowAllToggleClick(object sender, RoutedEventArgs e)
    {
        var allowAll = AllowAllToggle.IsChecked == true;
        _approvalService.SetApprovalRequired(!allowAll);
        RefreshApprovalUi();
        StatusBarText.Text = allowAll
            ? "\u5168\u8A31\u53EF\u30E2\u30FC\u30C9\u3092 ON \u306B\u3057\u307E\u3057\u305F\u3002"
            : "\u30AF\u30E9\u30A4\u30A2\u30F3\u30C8\u627F\u8A8D\u30E2\u30FC\u30C9\u3092 ON \u306B\u3057\u307E\u3057\u305F\u3002";
    }

    private void OnRevokeApprovedClient(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string ip)
            return;

        _approvalService.RevokeClient(ip);
        RefreshApprovalUi();
        StatusBarText.Text = $"{ip} \u306E\u627F\u8A8D\u3092\u53D6\u308A\u6D88\u3057\u307E\u3057\u305F\u3002";
    }

    private void OnApprovalRequested(PendingApprovalRequest request)
    {
        Dispatcher.BeginInvoke(() => RefreshApprovalUi());
    }

    private void OnApprovePendingClient(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string ip)
            return;

        _approvalService.ResolvePendingApproval(ip, true);
        RefreshApprovalUi();
        StatusBarText.Text = $"{ip} \u3092\u8A31\u53EF\u3057\u307E\u3057\u305F\u3002";
    }

    private void OnDenyPendingClient(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string ip)
            return;

        _approvalService.ResolvePendingApproval(ip, false);
        RefreshApprovalUi();
        StatusBarText.Text = $"{ip} \u3092\u62D2\u5426\u3057\u307E\u3057\u305F\u3002";
    }

    private void RefreshApprovalUi()
    {
        var approvalRequired = _approvalService.ApprovalRequired;
        AllowAllToggle.IsChecked = !approvalRequired;
        ApprovalModeLabel.Text = approvalRequired
            ? "\u65B0\u898F\u30AF\u30E9\u30A4\u30A2\u30F3\u30C8\u306F\u627F\u8A8D\u304C\u5FC5\u8981\u3067\u3059"
            : "\u5168\u3066\u306E\u30AF\u30E9\u30A4\u30A2\u30F3\u30C8\u304C\u81EA\u52D5\u8A31\u53EF\u3055\u308C\u307E\u3059";

        var pending = _approvalService.GetPendingApprovals();
        PendingApprovalsPanel.ItemsSource = pending.Select(p => new PendingApprovalViewModel
        {
            IpAddress = p.IpAddress,
            EnvironmentLabel = ClientConnectionTracker.DescribeClientEnvironment(p.UserAgent),
        }).ToList();

        ApprovedClientsListBox.ItemsSource = _approvalService.ApprovedClients;
    }

    // --- Async Operations ---

    private async Task EnsureServerStartedOnLaunchAsync()
    {
        if (_server.IsRunning) { RefreshDynamicState(); return; }

        ServerToggleButton.IsEnabled = false;
        ApplyPortButton.IsEnabled = false;
        SetActionControlsLocked(true);
        try
        {
            StatusBarText.Text = "\u8D77\u52D5\u6642\u306E\u81EA\u52D5\u63A5\u7D9A\u3092\u958B\u59CB\u3057\u3066\u3044\u307E\u3059...";
            await _server.StartAsync();
            StatusBarText.Text = "\u8D77\u52D5\u6642\u306B\u63A5\u7D9A\u3092 ON \u306B\u3057\u307E\u3057\u305F\u3002";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "\u81EA\u52D5\u63A5\u7D9A\u5931\u6557",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusBarText.Text = "\u81EA\u52D5\u63A5\u7D9A\u306B\u5931\u6557\u3057\u307E\u3057\u305F\u3002\u624B\u52D5\u3067\u958B\u59CB\u3057\u3066\u304F\u3060\u3055\u3044\u3002";
        }
        finally
        {
            ServerToggleButton.IsEnabled = true;
            ApplyPortButton.IsEnabled = true;
            SetActionControlsLocked(false);
            RefreshDynamicState();
        }
    }

    private async Task ToggleServerAsync()
    {
        ServerToggleButton.IsEnabled = false;
        ApplyPortButton.IsEnabled = false;
        SetActionControlsLocked(true);
        try
        {
            if (_server.IsRunning)
            {
                StatusBarText.Text = "\u63A5\u7D9A\u3092\u505C\u6B62\u3057\u3066\u3044\u307E\u3059...";
                await _server.StopAsync();
                StatusBarText.Text = "\u63A5\u7D9A\u3092 OFF \u306B\u3057\u307E\u3057\u305F\u3002";
            }
            else
            {
                StatusBarText.Text = "\u63A5\u7D9A\u3092\u958B\u59CB\u3057\u3066\u3044\u307E\u3059...";
                await _server.StartAsync();
                StatusBarText.Text = "\u63A5\u7D9A\u3092 ON \u306B\u3057\u307E\u3057\u305F\u3002";
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "\u63A5\u7D9A\u5207\u66FF\u5931\u6557",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ServerToggleButton.IsEnabled = true;
            ApplyPortButton.IsEnabled = true;
            SetActionControlsLocked(false);
            RefreshDynamicState();
        }
    }

    private async Task ApplyPortAsync()
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out var nextPort) || nextPort is <= 0 or > 65535)
        {
            System.Windows.MessageBox.Show(this, "\u30DD\u30FC\u30C8\u306F 1\uFF5E65535 \u306E\u7BC4\u56F2\u3067\u6307\u5B9A\u3057\u3066\u304F\u3060\u3055\u3044\u3002",
                "\u7121\u52B9\u306A\u30DD\u30FC\u30C8", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var currentPort = _runtimeState.Port;
        if (nextPort == currentPort)
        {
            StatusBarText.Text = "\u30DD\u30FC\u30C8\u306F\u5909\u66F4\u3055\u308C\u3066\u3044\u307E\u305B\u3093\u3002";
            return;
        }

        var wasRunning = _server.IsRunning;
        ServerToggleButton.IsEnabled = false;
        ApplyPortButton.IsEnabled = false;
        SetActionControlsLocked(true);
        try
        {
            if (wasRunning)
            {
                StatusBarText.Text = $"\u30DD\u30FC\u30C8 {nextPort} \u3078\u5207\u308A\u66FF\u3048\u308B\u305F\u3081\u518D\u8D77\u52D5\u4E2D...";
                await _server.StopAsync();
            }
            _runtimeState.UpdatePort(nextPort);
            if (wasRunning)
            {
                await _server.StartAsync();
                StatusBarText.Text = $"\u30DD\u30FC\u30C8\u3092 {nextPort} \u306B\u5909\u66F4\u3057\u3066\u518D\u8D77\u52D5\u3057\u307E\u3057\u305F\u3002";
            }
            else
            {
                StatusBarText.Text = $"\u30DD\u30FC\u30C8\u3092 {nextPort} \u306B\u5909\u66F4\u3057\u307E\u3057\u305F\u3002\u63A5\u7D9A\u3092 ON \u306B\u3059\u308B\u3068\u53CD\u6620\u3055\u308C\u307E\u3059\u3002";
            }
        }
        catch (Exception ex)
        {
            if (_runtimeState.Port != currentPort)
            {
                try { _runtimeState.UpdatePort(currentPort); } catch { }
            }
            if (wasRunning && !_server.IsRunning)
            {
                try { await _server.StartAsync(); } catch { }
            }
            PortTextBox.Text = currentPort.ToString();
            System.Windows.MessageBox.Show(this, $"\u30DD\u30FC\u30C8\u5909\u66F4\u5931\u6557\u3002\n\n{ex.Message}",
                "\u30DD\u30FC\u30C8\u5909\u66F4\u5931\u6557", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ServerToggleButton.IsEnabled = true;
            ApplyPortButton.IsEnabled = true;
            SetActionControlsLocked(false);
            RefreshDynamicState();
        }
    }

    private void ApplyToken()
    {
        try
        {
            var token = _tokenVisible ? TokenTextBox.Text : TokenPasswordBox.Password;
            _runtimeState.UpdateToken(token);
            StatusBarText.Text = "\u63A5\u7D9A\u30D1\u30B9\u30EF\u30FC\u30C9\u3092\u4FDD\u5B58\u3057\u307E\u3057\u305F\u3002\u65E2\u5B58\u306E\u63A5\u7D9A\u306F\u518D\u30ED\u30B0\u30A4\u30F3\u304C\u5FC5\u8981\u3067\u3059\u3002";
            RefreshDynamicState();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "\u30D1\u30B9\u30EF\u30FC\u30C9\u4FDD\u5B58\u5931\u6557",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ApplyAccessToggleAsync(string rawValue, bool isEnabled)
    {
        var previousRules = _runtimeState.DisabledAccessRules;
        var wasRunning = _server.IsRunning;
        ServerToggleButton.IsEnabled = false;
        ApplyPortButton.IsEnabled = false;
        SetActionControlsLocked(true);
        try
        {
            StatusBarText.Text = isEnabled
                ? "\u30CD\u30C3\u30C8\u30EF\u30FC\u30AF\u3092 ON \u306B\u3057\u3066\u3044\u307E\u3059..."
                : "\u30CD\u30C3\u30C8\u30EF\u30FC\u30AF\u3092 OFF \u306B\u3057\u3066\u3044\u307E\u3059...";

            if (wasRunning) await _server.StopAsync();
            _runtimeState.SetBindAddressEnabled(rawValue, isEnabled);
            if (wasRunning) await _server.StartAsync();

            var result = isEnabled
                ? "\u30CD\u30C3\u30C8\u30EF\u30FC\u30AF\u3092 ON \u306B\u3057\u307E\u3057\u305F\u3002"
                : "\u30CD\u30C3\u30C8\u30EF\u30FC\u30AF\u3092 OFF \u306B\u3057\u307E\u3057\u305F\u3002";
            StatusBarText.Text = wasRunning
                ? $"{result} \u63A5\u7D9A\u3092\u518D\u8D77\u52D5\u3057\u3066\u53CD\u6620\u3057\u307E\u3057\u305F\u3002"
                : $"{result} \u63A5\u7D9A\u3092 ON \u306B\u3059\u308B\u3068\u53CD\u6620\u3055\u308C\u307E\u3059\u3002";
        }
        catch (Exception ex)
        {
            if (!AreDisabledAccessRulesEquivalent(_runtimeState.DisabledAccessRules, previousRules))
            {
                try { _runtimeState.UpdateDisabledAccessRules(previousRules); } catch { }
            }
            if (wasRunning && !_server.IsRunning)
            {
                try { await _server.StartAsync(); } catch { }
            }
            StatusBarText.Text = "\u30A2\u30AF\u30BB\u30B9\u8A2D\u5B9A\u306E\u66F4\u65B0\u306B\u5931\u6557\u3057\u307E\u3057\u305F\u3002";
            System.Windows.MessageBox.Show(this, ex.Message, "\u30A2\u30AF\u30BB\u30B9\u8A2D\u5B9A\u5931\u6557",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ServerToggleButton.IsEnabled = true;
            ApplyPortButton.IsEnabled = true;
            SetActionControlsLocked(false);
            _lastStaticListsSignature = null;
            RefreshDynamicState();
        }
    }

    private async Task DisconnectSelectedClientAsync()
    {
        if (ClientListBox.SelectedItem is not ClientItemViewModel client)
        {
            StatusBarText.Text = "\u5207\u65AD\u3059\u308B\u30AF\u30E9\u30A4\u30A2\u30F3\u30C8\u3092\u9078\u629E\u3057\u3066\u304F\u3060\u3055\u3044\u3002";
            return;
        }

        var snapshot = _lastRenderedSnapshots.FirstOrDefault(s =>
            string.Equals(s.ClientId, client.ClientId, StringComparison.Ordinal));
        if (snapshot is null) return;

        var result = System.Windows.MessageBox.Show(this,
            $"{snapshot.EnvironmentLabel} ({snapshot.RemoteAddress}) \u3092\u5207\u65AD\u3057\u307E\u3059\u304B\uFF1F\n\u6B21\u56DE\u30A2\u30AF\u30BB\u30B9\u6642\u306F\u518D\u30ED\u30B0\u30A4\u30F3\u304C\u5FC5\u8981\u3067\u3059\u3002",
            "\u30AF\u30E9\u30A4\u30A2\u30F3\u30C8\u5207\u65AD", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        ServerToggleButton.IsEnabled = false;
        ApplyPortButton.IsEnabled = false;
        SetActionControlsLocked(true);
        try
        {
            StatusBarText.Text = "\u30AF\u30E9\u30A4\u30A2\u30F3\u30C8\u3092\u5207\u65AD\u3057\u3066\u3044\u307E\u3059...";
            await _server.DisconnectClientAsync(snapshot.ClientId);
            StatusBarText.Text = $"{snapshot.EnvironmentLabel} ({snapshot.RemoteAddress}) \u3092\u5207\u65AD\u3057\u307E\u3057\u305F\u3002";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "\u30AF\u30E9\u30A4\u30A2\u30F3\u30C8\u306E\u5207\u65AD\u306B\u5931\u6557\u3057\u307E\u3057\u305F\u3002";
            System.Windows.MessageBox.Show(this, ex.Message, "\u5207\u65AD\u5931\u6557",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ServerToggleButton.IsEnabled = true;
            ApplyPortButton.IsEnabled = true;
            SetActionControlsLocked(false);
            RefreshDynamicState();
        }
    }

    // --- Window Placement ---

    private void ApplySavedWindowPlacement()
    {
        if (_initialPlacementApplied) return;
        _initialPlacementApplied = true;
        var placement = _runtimeState.WindowPlacement;
        if (placement is null) return;

        var p = placement.Value;
        var width = Math.Max(MinWidth, p.Width);
        var height = Math.Max(MinHeight, p.Height);
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        var left = Math.Min(Math.Max(p.Left, vl), vl + vw - width);
        var top = Math.Min(Math.Max(p.Top, vt), vt + vh - height);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left; Top = top; Width = width; Height = height;
        if (p.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new System.Windows.Rect(Left, Top, Width, Height)
                : RestoreBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            _runtimeState.UpdateWindowPlacement(new PortalWindowPlacement(
                (int)bounds.Left, (int)bounds.Top,
                (int)bounds.Width, (int)bounds.Height,
                WindowState == WindowState.Maximized));
        }
        catch { }
    }

    // --- Helpers ---

    private string? GetSelectedClientId()
    {
        return (ClientListBox.SelectedItem as ClientItemViewModel)?.ClientId;
    }

    private static string FormatEntrySignature(AccessListItem item)
    {
        return $"{item.RawValue}|{item.DisplayText}|{item.IsEnabled}|{item.IsManual}";
    }

    private static AccessItemViewModel ToAccessItemVmWithCidr(AccessListItem bindItem, IReadOnlyList<AccessListItem> networkEntries)
    {
        return new AccessItemViewModel
        {
            RawValue = bindItem.RawValue,
            DisplayText = bindItem.DisplayText,
            CidrLabel = FindMatchingCidr(bindItem.RawValue, networkEntries),
            IsEnabled = bindItem.IsEnabled,
            IsManual = bindItem.IsManual,
        };
    }

    private static string FindMatchingCidr(string bindAddressRaw, IReadOnlyList<AccessListItem> networkEntries)
    {
        if (!IPAddress.TryParse(bindAddressRaw, out var address))
            return "";

        foreach (var entry in networkEntries)
        {
            if (IpNetworkRange.TryParse(entry.RawValue, out var network) && network.Contains(address))
                return entry.RawValue;
        }

        return "";
    }

    private static bool AreDisabledAccessRulesEquivalent(PortalDisabledAccessRules left, PortalDisabledAccessRules right)
    {
        return left.BindAddresses.SequenceEqual(right.BindAddresses, StringComparer.OrdinalIgnoreCase) &&
            left.AllowedAddresses.SequenceEqual(right.AllowedAddresses, StringComparer.OrdinalIgnoreCase) &&
            left.AllowedNetworks.SequenceEqual(right.AllowedNetworks, StringComparer.OrdinalIgnoreCase);
    }

    private static WpfListBox? FindParentListBox(DependencyObject element)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is WpfListBox listBox) return listBox;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

}

// --- View Model Classes ---

internal sealed class AccessItemViewModel
{
    public string RawValue { get; init; } = "";
    public string DisplayText { get; init; } = "";
    public string CidrLabel { get; init; } = "";
    public bool IsEnabled { get; set; }
    public bool IsManual { get; init; }
}

internal sealed class ClientItemViewModel
{
    public string ClientId { get; init; } = "";
    public string RemoteAddress { get; init; } = "";
    public string EnvironmentLabel { get; init; } = "";
    public string LastPath { get; init; } = "";
    public string StatusLabel { get; init; } = "";
    public SolidColorBrush StatusColor { get; init; } = new(System.Windows.Media.Colors.Gray);
    public string LastSeen { get; init; } = "";
    public string UserAgent { get; init; } = "";
    public bool IsActive { get; init; }
}

internal sealed class PendingApprovalViewModel
{
    public string IpAddress { get; init; } = "";
    public string EnvironmentLabel { get; init; } = "";
}

internal sealed class LogItemViewModel
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "";
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";
    public SolidColorBrush LevelColor { get; init; } = new(System.Windows.Media.Colors.Gray);
}
