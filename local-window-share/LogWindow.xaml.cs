using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

internal sealed partial class LogWindow : Window
{
    private static readonly SolidColorBrush BrushError = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf8, 0x51, 0x49)));
    private static readonly SolidColorBrush BrushWarning = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd2, 0x99, 0x22)));
    private static readonly SolidColorBrush BrushDefault = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8b, 0x94, 0x9e)));
    private static SolidColorBrush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    private readonly PortalLogStore _logStore;
    private CancellationTokenSource? _refreshCts;
    private long _lastRenderedLogSequenceId;

    public LogWindow(PortalLogStore logStore)
    {
        _logStore = logStore;
        InitializeComponent();

        Loaded += (_, _) => StartBackgroundRefreshLoop();
        Closing += (_, _) => { _refreshCts?.Cancel(); _refreshCts?.Dispose(); _refreshCts = null; };
    }

    private void StartBackgroundRefreshLoop()
    {
        // Initial synchronous paint
        RefreshLogs();

        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    var snapshot = GatherLogSnapshot();
                    if (snapshot is not null)
                        await Dispatcher.InvokeAsync(() => ApplyLogSnapshot(snapshot), DispatcherPriority.Background, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);
    }

    private LogSnapshot? GatherLogSnapshot()
    {
        var logs = _logStore.GetEntries();
        var lastId = logs.Count == 0 ? 0 : logs[^1].SequenceId;

        if (_lastRenderedLogSequenceId == lastId)
            return null;

        var items = logs.Select(entry => new LogItemViewModel
        {
            Time = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
            Level = entry.Level,
            Source = entry.Source,
            Message = entry.Message,
            LevelColor = entry.Level switch
            {
                "Error" => BrushError,
                "Warning" => BrushWarning,
                _ => BrushDefault,
            },
        }).ToList();

        return new LogSnapshot { Items = items, LastId = lastId, Count = logs.Count };
    }

    private void ApplyLogSnapshot(LogSnapshot s)
    {
        LogCountLabel.Text = s.Count == 0 ? "(\u30ED\u30B0\u306A\u3057)" : $"({s.Count} \u4EF6)";
        ClearLogsButton.IsEnabled = s.Count > 0;
        LogListBox.ItemsSource = s.Items;
        if (s.Items.Count > 0)
            LogListBox.ScrollIntoView(s.Items[^1]);
        _lastRenderedLogSequenceId = s.LastId;
    }

    private sealed class LogSnapshot
    {
        public List<LogItemViewModel> Items = [];
        public long LastId;
        public int Count;
    }

    private void RefreshLogs()
    {
        var logs = _logStore.GetEntries();
        var lastId = logs.Count == 0 ? 0 : logs[^1].SequenceId;

        LogCountLabel.Text = logs.Count == 0
            ? "(\u30ED\u30B0\u306A\u3057)"
            : $"({logs.Count} \u4EF6)";
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
                "Error" => BrushError,
                "Warning" => BrushWarning,
                _ => BrushDefault,
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
