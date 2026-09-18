using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace GitStateWidget;

public partial class App : Application {
    private static Mutex? _single;
    private Config _config = new();
    private MainWindow? _window;
    private TrayIcon? _tray;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _scanCts;
    private bool _scanning;

    public Config Config => _config;
    public IReadOnlyList<RepoState> States { get; private set; } = Array.Empty<RepoState>();
    public DateTimeOffset? LastScan { get; private set; }
    public event Action? StatesChanged;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\GitStateWidget", out var createdNew);
        if (!createdNew) {
            Shutdown();
            return;
        }

        _config = Config.Load();
        _tray = new TrayIcon(this);
        _window = new MainWindow(this);
        if (!_config.StartHidden) {
            _window.Show();
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_config.IntervalMinutes) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void ApplyInterval() {
        if (_timer != null) {
            _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, _config.IntervalMinutes));
        }
    }

    public async Task RefreshAsync() {
        if (_scanning) {
            return;
        }
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        _window?.SetBusy(true);
        try {
            var repos = _config.Repos.ToList();
            var tasks = repos.Select(r => Scanner.ScanAsync(r, _config.Fetch, _scanCts.Token));
            var results = await Task.WhenAll(tasks);
            States = results;
            LastScan = DateTimeOffset.Now;
            StatesChanged?.Invoke();
        } finally {
            _scanning = false;
            _window?.SetBusy(false);
        }
    }

    public Severity Overall => States.Count == 0 ? Severity.Ok : States.Max(s => s.Severity);

    public string Headline {
        get {
            if (_config.Repos.Count == 0) {
                return "No repos configured";
            }
            if (States.Count == 0) {
                return "Scanning…";
            }
            var alarms = States.Count(s => s.Severity == Severity.Alarm);
            var warns = States.Count(s => s.Severity == Severity.Warn);
            if (alarms > 0) {
                return Scanner.Plural(alarms, "repo") + " need" + (alarms == 1 ? "s" : "") + " attention";
            }
            if (warns > 0) {
                return Scanner.Plural(warns, "repo") + " behind or unfetched";
            }
            return "All repos pushed and current";
        }
    }

    public void ShowPanel() {
        if (_window == null) {
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    public void TogglePanel() {
        if (_window == null) {
            return;
        }
        if (_window.IsVisible) {
            _window.Hide();
        } else {
            ShowPanel();
        }
    }

    public void AddRepo(string path) {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (_config.Repos.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) {
            return;
        }
        _config.Repos.Add(new RepoConfig { Name = name, Path = path });
        _config.Save();
        _ = RefreshAsync();
    }

    public void ApplyPanelConfig() {
        _window?.ApplyConfig();
    }

    public void ReloadConfig() {
        _config = Config.Load();
        ApplyInterval();
        _window?.ApplyConfig();
        _ = RefreshAsync();
    }

    public void ExitApp() {
        _scanCts?.Cancel();
        _timer?.Stop();
        _tray?.Dispose();
        _window?.SavePlacement();
        _config.Save();
        Shutdown();
    }
}
