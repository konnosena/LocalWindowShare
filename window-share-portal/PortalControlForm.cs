using System.Drawing;
using System.Security.Cryptography;

internal sealed class PortalControlForm : Form
{
    private const int BaseMinimumWidth = 1080;
    private const int BaseMinimumHeight = 760;
    private const int BasePreferredLeftPaneWidth = 560;
    private const int BaseLeftPaneMinWidth = 460;
    private const int BaseRightPaneMinWidth = 420;

    private readonly PortalRuntimeState _runtimeState;
    private readonly ClientConnectionTracker _connectionTracker;
    private readonly PortalLogStore _logStore;
    private readonly PortalServer _server;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly SplitContainer _contentSplit;
    private readonly Label _serverStateLabel;
    private readonly Label _machineNameLabel;
    private readonly Label _portLabel;
    private readonly Label _backendLabel;
    private readonly Label _tokenSourceLabel;
    private readonly Label _settingsPathLabel;
    private readonly Button _connectionToggleButton;
    private readonly TextBox _portTextBox;
    private readonly Button _applyPortButton;
    private readonly TextBox _tokenTextBox;
    private readonly CheckBox _showTokenCheckBox;
    private readonly Label _passwordHintLabel;
    private readonly ListBox _accessUrlsListBox;
    private readonly ListBox _allowedIpsListBox;
    private readonly ListBox _allowedNetworksListBox;
    private readonly ListView _clientListView;
    private readonly Label _clientSummaryLabel;
    private readonly ListView _logListView;
    private readonly Label _logSummaryLabel;
    private readonly Button _clearLogsButton;
    private string? _lastStaticListsSignature;
    private IReadOnlyList<ClientConnectionSnapshot> _lastRenderedSnapshots = Array.Empty<ClientConnectionSnapshot>();
    private long _lastRenderedLogSequenceId;
    private bool _initialPlacementApplied;
    private bool _startupConnectAttempted;

    public PortalControlForm(
        PortalRuntimeState runtimeState,
        ClientConnectionTracker connectionTracker,
        PortalLogStore logStore,
        PortalServer server)
    {
        _runtimeState = runtimeState;
        _connectionTracker = connectionTracker;
        _logStore = logStore;
        _server = server;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Window Share Portal";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(BaseMinimumWidth, BaseMinimumHeight);
        Size = new Size(1280, 860);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var serverGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            Text = "サーバー状態",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };
        root.Controls.Add(serverGroup, 0, 0);

        var serverLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        serverGroup.Controls.Add(serverLayout);

        _serverStateLabel = CreateInfoLabel();
        _machineNameLabel = CreateInfoLabel();
        _portLabel = CreateInfoLabel();
        _backendLabel = CreateInfoLabel();
        _tokenSourceLabel = CreateInfoLabel();
        _settingsPathLabel = CreateInfoLabel();
        _connectionToggleButton = new Button
        {
            AutoSize = true,
        };
        _connectionToggleButton.Click += async (_, _) => await ToggleServerAsync();
        _portTextBox = new TextBox
        {
            Width = 90,
            TextAlign = HorizontalAlignment.Right,
        };
        _applyPortButton = new Button
        {
            Text = "ポート適用",
            AutoSize = true,
        };
        _applyPortButton.Click += async (_, _) => await ApplyPortAsync();

        serverLayout.Controls.Add(_serverStateLabel, 0, 0);
        serverLayout.Controls.Add(_machineNameLabel, 0, 1);
        serverLayout.Controls.Add(_portLabel, 0, 2);
        serverLayout.Controls.Add(_backendLabel, 0, 3);
        serverLayout.Controls.Add(_tokenSourceLabel, 0, 4);
        serverLayout.Controls.Add(_settingsPathLabel, 0, 5);

        var portRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
        };
        portRow.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "待ち受けポート",
            Padding = new Padding(0, 7, 0, 0),
        });
        portRow.Controls.Add(_portTextBox);
        portRow.Controls.Add(_applyPortButton);
        serverLayout.Controls.Add(portRow, 0, 6);

        var serverButtonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        serverButtonRow.Controls.Add(_connectionToggleButton);
        serverLayout.Controls.Add(serverButtonRow, 0, 7);

        var passwordGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            Text = "接続パスワード",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };
        root.Controls.Add(passwordGroup, 0, 1);

        var passwordLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        passwordGroup.Controls.Add(passwordLayout);

        var passwordRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
        };
        passwordLayout.Controls.Add(passwordRow, 0, 0);

        _tokenTextBox = new TextBox
        {
            Width = 420,
            UseSystemPasswordChar = true,
            Text = _runtimeState.Token,
        };
        passwordRow.Controls.Add(_tokenTextBox);

        _showTokenCheckBox = new CheckBox
        {
            Text = "表示する",
            AutoSize = true,
            Padding = new Padding(0, 7, 0, 0),
        };
        _showTokenCheckBox.CheckedChanged += (_, _) =>
        {
            _tokenTextBox.UseSystemPasswordChar = !_showTokenCheckBox.Checked;
        };
        passwordRow.Controls.Add(_showTokenCheckBox);

        var applyTokenButton = new Button
        {
            Text = "保存",
            AutoSize = true,
        };
        applyTokenButton.Click += (_, _) => ApplyToken();
        passwordRow.Controls.Add(applyTokenButton);

        var generateTokenButton = new Button
        {
            Text = "ランダム生成",
            AutoSize = true,
        };
        generateTokenButton.Click += (_, _) =>
        {
            _tokenTextBox.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        };
        passwordRow.Controls.Add(generateTokenButton);

        _passwordHintLabel = CreateInfoLabel();
        _passwordHintLabel.Text = "起動後に自動で接続を ON にします。";
        passwordLayout.Controls.Add(_passwordHintLabel, 0, 1);

        _contentSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
        };
        root.Controls.Add(_contentSplit, 0, 2);
        Load += (_, _) => HandleInitialLayout();
        DpiChanged += (_, _) => ApplyScaledLayout();

        var leftLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        _contentSplit.Panel1.Controls.Add(leftLayout);

        _accessUrlsListBox = CreateListGroup(leftLayout, 0, 0, "アクセス URL");
        _allowedIpsListBox = CreateListGroup(leftLayout, 1, 0, "現在許可している IP");
        _allowedNetworksListBox = CreateListGroup(leftLayout, 2, 0, "許可ネットワーク");

        var rightLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        _contentSplit.Panel2.Controls.Add(rightLayout);

        var clientsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "現在接続している環境",
            Padding = new Padding(12),
        };
        rightLayout.Controls.Add(clientsGroup, 0, 0);

        var clientsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        clientsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        clientsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        clientsGroup.Controls.Add(clientsLayout);

        _clientSummaryLabel = CreateInfoLabel();
        clientsLayout.Controls.Add(_clientSummaryLabel, 0, 0);

        _clientListView = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            View = View.Details,
        };
        _clientListView.Columns.Add("Remote IP", 170);
        _clientListView.Columns.Add("Environment", 190);
        _clientListView.Columns.Add("Last Path", 240);
        _clientListView.Columns.Add("Status", 110);
        _clientListView.Columns.Add("Last Seen", 170);
        _clientListView.Columns.Add("User-Agent", 320);
        clientsLayout.Controls.Add(_clientListView, 0, 1);

        var logsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "詳細ログ",
            Padding = new Padding(12),
        };
        rightLayout.Controls.Add(logsGroup, 0, 1);

        var logsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        logsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logsGroup.Controls.Add(logsLayout);

        var logsHeaderRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        logsHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logsHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logsLayout.Controls.Add(logsHeaderRow, 0, 0);

        _logSummaryLabel = CreateInfoLabel();
        _logSummaryLabel.Text = "WebRTC / browser / server のログを表示します。";
        logsHeaderRow.Controls.Add(_logSummaryLabel, 0, 0);

        _clearLogsButton = new Button
        {
            Text = "ログをクリア",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
        };
        _clearLogsButton.Click += (_, _) => ClearLogs();
        logsHeaderRow.Controls.Add(_clearLogsButton, 1, 0);

        _logListView = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = true,
            View = View.Details,
            Font = new Font("Cascadia Mono", 9F),
        };
        _logListView.Columns.Add("Time", 155);
        _logListView.Columns.Add("Level", 80);
        _logListView.Columns.Add("Source", 100);
        _logListView.Columns.Add("Message", 720);
        _logListView.KeyDown += HandleLogListKeyDown;
        var logContextMenu = new ContextMenuStrip();
        var copySelectedLogsItem = new ToolStripMenuItem("選択をコピー");
        copySelectedLogsItem.Click += (_, _) => CopyLogsToClipboard(selectedOnly: true);
        logContextMenu.Items.Add(copySelectedLogsItem);
        var copyAllLogsItem = new ToolStripMenuItem("全件コピー");
        copyAllLogsItem.Click += (_, _) => CopyLogsToClipboard(selectedOnly: false);
        logContextMenu.Items.Add(copyAllLogsItem);
        _logListView.ContextMenuStrip = logContextMenu;
        logsLayout.Controls.Add(_logListView, 0, 1);

        PopulateStaticListsIfChanged();
        RefreshDynamicState();

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000,
        };
        _refreshTimer.Tick += (_, _) => RefreshDynamicState();
        _refreshTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_startupConnectAttempted)
        {
            return;
        }

        _startupConnectAttempted = true;
        await EnsureServerStartedOnLaunchAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowPlacement();
        base.OnFormClosing(e);
    }

    private void PopulateStaticListsIfChanged()
    {
        var listenUrls = _runtimeState.ListenUrls;
        var allowedAddresses = _runtimeState.AllowedAddressLabels;
        var allowedNetworks = _runtimeState.AllowedNetworkLabels;
        var signature = string.Join("\u001f", listenUrls) + "\u001e" +
            string.Join("\u001f", allowedAddresses) + "\u001e" +
            string.Join("\u001f", allowedNetworks);

        if (string.Equals(_lastStaticListsSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        ReplaceListBoxItems(_accessUrlsListBox, listenUrls);
        ReplaceListBoxItems(_allowedIpsListBox, allowedAddresses);
        ReplaceListBoxItems(_allowedNetworksListBox, allowedNetworks);
        _lastStaticListsSignature = signature;
    }

    private void RefreshDynamicState()
    {
        PopulateStaticListsIfChanged();
        SetControlText(_serverStateLabel, $"稼働状態: {(_server.IsRunning ? "Listening" : "Stopped")}");
        SetControlText(_machineNameLabel, $"ホスト名: {Environment.MachineName}");
        SetControlText(_portLabel, $"待受ポート: {_runtimeState.Port}");
        SetControlText(_backendLabel, $"WebRTC backend: {_server.WebRtcBackendName}");
        SetControlText(_tokenSourceLabel, $"パスワードソース: {_runtimeState.TokenModeLabel}");
        SetControlText(_settingsPathLabel, $"設定ファイル: {_runtimeState.SettingsPath}");
        SetControlText(_connectionToggleButton, _server.IsRunning ? "接続をOFF" : "接続をON");
        if (!_portTextBox.Focused)
        {
            SetControlText(_portTextBox, _runtimeState.Port.ToString());
        }

        var snapshots = _connectionTracker.GetSnapshots();
        var activeCount = snapshots.Count(snapshot => snapshot.IsActive);
        var summaryText = !_server.IsRunning
            ? "接続は OFF です。スマホからはアクセスできません。"
            : activeCount == 0
            ? "直近の接続はありません。20 秒以内のアクセスを active と表示します。"
            : $"active 接続 {activeCount} 件 / 直近履歴 {snapshots.Count} 件";
        SetControlText(_clientSummaryLabel, summaryText);

        if (!_lastRenderedSnapshots.SequenceEqual(snapshots))
        {
            _clientListView.BeginUpdate();
            _clientListView.Items.Clear();
            foreach (var snapshot in snapshots)
            {
                var item = new ListViewItem(snapshot.RemoteAddress);
                item.SubItems.Add(snapshot.EnvironmentLabel);
                item.SubItems.Add($"{snapshot.LastMethod} {snapshot.LastPath}");
                item.SubItems.Add($"{(snapshot.IsActive ? "active" : "recent")} / {snapshot.LastStatusCode}");
                item.SubItems.Add(snapshot.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(snapshot.UserAgent);
                _clientListView.Items.Add(item);
            }
            _clientListView.EndUpdate();
            _lastRenderedSnapshots = snapshots.ToArray();
        }

        var logs = _logStore.GetEntries();
        var lastSequenceId = logs.Count == 0 ? 0 : logs[^1].SequenceId;
        SetControlText(_logSummaryLabel, logs.Count == 0
            ? "まだログはありません。Ctrl+C または右クリックでコピーできます。"
            : $"ログ {logs.Count} 件 / 最新 {logs[^1].TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} / Ctrl+C または右クリックでコピー");
        _clearLogsButton.Enabled = logs.Count > 0;

        if (_lastRenderedLogSequenceId == lastSequenceId)
        {
            return;
        }

        _logListView.BeginUpdate();
        _logListView.Items.Clear();
        foreach (var entry in logs)
        {
            var item = new ListViewItem(entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
            item.SubItems.Add(entry.Level);
            item.SubItems.Add(entry.Source);
            item.SubItems.Add(entry.Message);
            _logListView.Items.Add(item);
        }

        if (_logListView.Items.Count > 0)
        {
            _logListView.EnsureVisible(_logListView.Items.Count - 1);
        }

        _logListView.EndUpdate();
        _lastRenderedLogSequenceId = lastSequenceId;
    }

    private void ClearLogs()
    {
        _logStore.Clear();
        _lastRenderedLogSequenceId = -1;
        _passwordHintLabel.Text = "詳細ログをクリアしました。";
        RefreshDynamicState();
    }

    private void HandleLogListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopyLogsToClipboard(selectedOnly: true);
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
    }

    private void CopyLogsToClipboard(bool selectedOnly)
    {
        var text = BuildLogClipboardText(selectedOnly);
        if (string.IsNullOrWhiteSpace(text))
        {
            _passwordHintLabel.Text = "コピーできるログがありません。";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _passwordHintLabel.Text = selectedOnly && _logListView.SelectedItems.Count > 0
                ? $"選択ログ {_logListView.SelectedItems.Count} 件をコピーしました。"
                : $"ログ {_logListView.Items.Count} 件をコピーしました。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Log copy failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string BuildLogClipboardText(bool selectedOnly)
    {
        var sourceItems = selectedOnly && _logListView.SelectedItems.Count > 0
            ? _logListView.SelectedItems.Cast<ListViewItem>()
            : _logListView.Items.Cast<ListViewItem>();

        var rows = sourceItems
            .Select(item => string.Join("\t", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(subItem => subItem.Text)))
            .ToArray();
        if (rows.Length == 0)
        {
            return string.Empty;
        }

        return $"Time\tLevel\tSource\tMessage{Environment.NewLine}{string.Join(Environment.NewLine, rows)}";
    }

    private void ApplyToken()
    {
        try
        {
            _runtimeState.UpdateToken(_tokenTextBox.Text);
            _passwordHintLabel.Text = "接続パスワードを保存しました。既存の接続は再ログインが必要です。";
            RefreshDynamicState();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Password update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task EnsureServerStartedOnLaunchAsync()
    {
        if (_server.IsRunning)
        {
            RefreshDynamicState();
            return;
        }

        _connectionToggleButton.Enabled = false;
        _applyPortButton.Enabled = false;

        try
        {
            _passwordHintLabel.Text = "起動時の自動接続を開始しています...";
            await _server.StartAsync();
            _passwordHintLabel.Text = "起動時に接続を ON にしました。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Auto-start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _passwordHintLabel.Text = "起動時の自動接続に失敗しました。手動で接続を ON にしてください。";
        }
        finally
        {
            _connectionToggleButton.Enabled = true;
            _applyPortButton.Enabled = true;
            RefreshDynamicState();
        }
    }

    private async Task ToggleServerAsync()
    {
        _connectionToggleButton.Enabled = false;
        _applyPortButton.Enabled = false;

        try
        {
            if (_server.IsRunning)
            {
                _passwordHintLabel.Text = "接続を停止しています...";
                await _server.StopAsync();
                _passwordHintLabel.Text = "接続を OFF にしました。";
            }
            else
            {
                _passwordHintLabel.Text = "接続を開始しています...";
                await _server.StartAsync();
                _passwordHintLabel.Text = "接続を ON にしました。";
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Connection toggle failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connectionToggleButton.Enabled = true;
            _applyPortButton.Enabled = true;
            RefreshDynamicState();
        }
    }

    private async Task ApplyPortAsync()
    {
        if (!int.TryParse(_portTextBox.Text.Trim(), out var nextPort) || nextPort is <= 0 or > 65535)
        {
            MessageBox.Show(this, "待ち受けポートは 1 から 65535 の範囲で指定してください。", "Invalid port", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var currentPort = _runtimeState.Port;
        if (nextPort == currentPort)
        {
            _passwordHintLabel.Text = "待ち受けポートは変更されていません。";
            return;
        }

        var wasRunning = _server.IsRunning;
        _connectionToggleButton.Enabled = false;
        _applyPortButton.Enabled = false;

        try
        {
            if (wasRunning)
            {
                _passwordHintLabel.Text = $"ポート {nextPort} へ切り替えるため接続を再起動しています...";
                await _server.StopAsync();
            }

            _runtimeState.UpdatePort(nextPort);

            if (wasRunning)
            {
                await _server.StartAsync();
                _passwordHintLabel.Text = $"待ち受けポートを {nextPort} に変更して再起動しました。";
            }
            else
            {
                _passwordHintLabel.Text = $"待ち受けポートを {nextPort} に変更しました。接続を ON にすると反映されます。";
            }
        }
        catch (Exception exception)
        {
            if (_runtimeState.Port != currentPort)
            {
                try
                {
                    _runtimeState.UpdatePort(currentPort);
                }
                catch
                {
                }
            }

            if (wasRunning && !_server.IsRunning)
            {
                try
                {
                    await _server.StartAsync();
                }
                catch
                {
                }
            }

            _portTextBox.Text = currentPort.ToString();
            MessageBox.Show(this, $"Port update failed.\r\n\r\n{exception.Message}", "Port update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connectionToggleButton.Enabled = true;
            _applyPortButton.Enabled = true;
            RefreshDynamicState();
        }
    }

    private void ApplyScaledLayout()
    {
        var scaledMinimumWidth = ScaleLogicalWidth(BaseMinimumWidth);
        var scaledMinimumHeight = ScaleLogicalHeight(BaseMinimumHeight);
        MinimumSize = new Size(scaledMinimumWidth, scaledMinimumHeight);

        _contentSplit.Panel1MinSize = ScaleLogicalWidth(BaseLeftPaneMinWidth);
        _contentSplit.Panel2MinSize = ScaleLogicalWidth(BaseRightPaneMinWidth);

        var availableLeftWidth = Math.Max(_contentSplit.Panel1MinSize, _contentSplit.Width - _contentSplit.Panel2MinSize);
        var preferredLeftWidth = ScaleLogicalWidth(BasePreferredLeftPaneWidth);
        _contentSplit.SplitterDistance = Math.Min(availableLeftWidth, preferredLeftWidth);
    }

    private void HandleInitialLayout()
    {
        ApplyScaledLayout();
        ApplySavedWindowPlacement();
    }

    private void ApplySavedWindowPlacement()
    {
        if (_initialPlacementApplied)
        {
            return;
        }

        _initialPlacementApplied = true;
        var placement = _runtimeState.WindowPlacement;
        if (placement is null)
        {
            return;
        }

        var bounds = GetVisibleBounds(placement.Value);
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        WindowState = placement.Value.IsMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var referenceBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (referenceBounds.Width <= 0 || referenceBounds.Height <= 0)
            {
                return;
            }

            _runtimeState.UpdateWindowPlacement(new PortalWindowPlacement(
                referenceBounds.Left,
                referenceBounds.Top,
                referenceBounds.Width,
                referenceBounds.Height,
                WindowState == FormWindowState.Maximized));
        }
        catch
        {
        }
    }

    private Rectangle GetVisibleBounds(PortalWindowPlacement placement)
    {
        var width = Math.Max(MinimumSize.Width, placement.Width);
        var height = Math.Max(MinimumSize.Height, placement.Height);
        var targetBounds = new Rectangle(placement.Left, placement.Top, width, height);
        var visibleScreen = Screen.AllScreens.FirstOrDefault(screen => screen.WorkingArea.IntersectsWith(targetBounds));
        var workingArea = visibleScreen?.WorkingArea ?? Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;

        if (workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            return targetBounds;
        }

        width = Math.Min(width, workingArea.Width);
        height = Math.Min(height, workingArea.Height);
        var left = Math.Min(Math.Max(targetBounds.Left, workingArea.Left), workingArea.Right - width);
        var top = Math.Min(Math.Max(targetBounds.Top, workingArea.Top), workingArea.Bottom - height);
        return new Rectangle(left, top, width, height);
    }

    private int ScaleLogicalWidth(int value)
    {
        return (int)Math.Round(value * DeviceDpi / 96d);
    }

    private int ScaleLogicalHeight(int value)
    {
        return (int)Math.Round(value * DeviceDpi / 96d);
    }

    private static Label CreateInfoLabel()
    {
        return new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
    }

    private static void ReplaceListBoxItems(ListBox listBox, IReadOnlyList<string> items)
    {
        listBox.BeginUpdate();
        try
        {
            listBox.Items.Clear();
            listBox.Items.AddRange(items.Cast<object>().ToArray());
        }
        finally
        {
            listBox.EndUpdate();
        }
    }

    private static void SetControlText(Control control, string text)
    {
        if (!string.Equals(control.Text, text, StringComparison.Ordinal))
        {
            control.Text = text;
        }
    }

    private static ListBox CreateListGroup(TableLayoutPanel parent, int row, int column, string title)
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = title,
            Padding = new Padding(12),
        };
        parent.Controls.Add(group, column, row);

        var listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            IntegralHeight = false,
            Font = new Font("Cascadia Mono", 10F),
        };
        group.Controls.Add(listBox);
        return listBox;
    }
}
