using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

internal sealed partial class LogWindow : Window
{
    private readonly PortalLogStore _logStore;
    private readonly DispatcherTimer _refreshTimer;
    private long _lastRenderedLogSequenceId;

    public LogWindow(PortalLogStore logStore)
    {
        _logStore = logStore;
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshLogs();
        Loaded += (_, _) => { RefreshLogs(); _refreshTimer.Start(); };
        Closing += (_, _) => _refreshTimer.Stop();
    }

    private void RefreshLogs()
    {
        var logs = _logStore.GetEntries();
        var lastId = logs.Count == 0 ? 0 : logs[^1].SequenceId;

        LogCountLabel.Text = logs.Count == 0
            ? "(ログなし)"
            : $"({logs.Count} 件)";
        ClearLogsButton.IsEnabled = logs.Count > 0;

        if (_lastRenderedLogSequenceId == lastId)
            return;

        var items = logs.Select(entry => new LogItemViewModel
        {
            Time = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
            Level = entry.Level,
            Source = entry.Source,
            Message = entry.Message,
            LevelColor = entry.Level switch
            {
                "Error" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf8, 0x51, 0x49)),
                "Warning" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd2, 0x99, 0x22)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8b, 0x94, 0x9e)),
            },
        }).ToList();

        LogListBox.ItemsSource = items;
        if (items.Count > 0)
            LogListBox.ScrollIntoView(items[^1]);

        _lastRenderedLogSequenceId = lastId;
    }

    private void OnCopySelectedLogs(object sender, RoutedEventArgs e)
    {
        CopyLogs(selectedOnly: true);
    }

    private void OnCopyAllLogs(object sender, RoutedEventArgs e)
    {
        CopyLogs(selectedOnly: false);
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _logStore.Clear();
        _lastRenderedLogSequenceId = -1;
        RefreshLogs();
    }

    private void OnLogListKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.C &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            CopyLogs(selectedOnly: true);
            e.Handled = true;
        }
    }

    private void CopyLogs(bool selectedOnly)
    {
        var items = LogListBox.ItemsSource as IList<LogItemViewModel>;
        if (items is null || items.Count == 0)
            return;

        IEnumerable<LogItemViewModel> source;
        if (selectedOnly && LogListBox.SelectedItems.Count > 0)
            source = LogListBox.SelectedItems.Cast<LogItemViewModel>();
        else
            source = items;

        var rows = source
            .Select(i => $"{i.Time}\t{i.Level}\t{i.Source}\t{i.Message}")
            .ToArray();

        if (rows.Length == 0)
            return;

        var text = $"Time\tLevel\tSource\tMessage{Environment.NewLine}{string.Join(Environment.NewLine, rows)}";
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
        }
    }
}
