using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GitStateWidget;

/// <summary>
/// The glance panel: one row per repo, click a row to unfold its branches. Borderless, draggable
/// by its header, remembers where it was. Closing hides it; the tray icon brings it back.
/// </summary>
public sealed class MainWindow : Window {
    private static readonly Brush Bg = Hex("#16181d");
    private static readonly Brush Card = Hex("#1e2128");
    private static readonly Brush CardHover = Hex("#242833");
    private static readonly Brush Line = Hex("#2c313a");
    private static readonly Brush Text = Hex("#e6e8eb");
    private static readonly Brush Dim = Hex("#868d99");
    private static readonly Brush Bad = Hex("#ff6b6b");
    private static readonly Brush Warn = Hex("#ffb454");
    private static readonly Brush Ok = Hex("#5ec27a");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    private readonly App _app;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _headline = new();
    private readonly TextBlock _stamp = new();
    private readonly StackPanel _body = new();
    private readonly TextBlock _refreshGlyph = new();
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _agoTick = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow(App app) {
        _app = app;
        Title = "Git State";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        MinWidth = 480;
        Width = 640;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Foreground = Text;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        Content = BuildChrome();
        ApplyConfig();
        RestorePlacement();

        _app.StatesChanged += Render;
        _saveDebounce.Tick += (_, _) => {
            _saveDebounce.Stop();
            SavePlacement();
        };
        _agoTick.Tick += (_, _) => Render();
        _agoTick.Start();
        LocationChanged += (_, _) => _saveDebounce.Start();
        SizeChanged += (_, _) => _saveDebounce.Start();
        KeyDown += (_, e) => {
            if (e.Key == Key.Escape) {
                Hide();
            }
        };
        Render();
    }

    public void ApplyConfig() {
        Topmost = _app.Config.AlwaysOnTop;
    }

    public void SetBusy(bool busy) {
        _refreshGlyph.Foreground = busy ? Warn : Dim;
        _refreshGlyph.ToolTip = busy ? "Scanning…" : "Refresh now";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) {
        // Closing the panel hides it; Exit lives in the tray menu.
        e.Cancel = true;
        Hide();
    }

    private UIElement BuildChrome() {
        var root = new Border {
            Background = Bg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 12)
        };
        var dock = new DockPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) => {
            if (e.ClickCount == 1) {
                DragMove();
            }
        };

        var titles = new StackPanel();
        var kicker = new TextBlock {
            Text = "GIT STATE",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Dim,
            Margin = new Thickness(0, 0, 0, 2)
        };
        _headline.FontSize = 16;
        _headline.FontWeight = FontWeights.SemiBold;
        _stamp.Foreground = Dim;
        _stamp.FontSize = 11.5;
        titles.Children.Add(kicker);
        titles.Children.Add(_headline);
        titles.Children.Add(_stamp);
        Grid.SetColumn(titles, 0);
        header.Children.Add(titles);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _refreshGlyph.Text = "↻";
        buttons.Children.Add(HeaderButton(_refreshGlyph, "Refresh now", () => _ = _app.RefreshAsync()));
        buttons.Children.Add(HeaderButton(new TextBlock { Text = "✕" }, "Hide (Esc). Tray icon brings it back", Hide));
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var footer = new TextBlock {
            Text = "Red is work that exists in one place only. Dirty trees and merged branches are informational.",
            Foreground = Dim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        dock.Children.Add(_body);
        root.Child = dock;
        return root;
    }

    private static Button HeaderButton(TextBlock glyph, string tip, Action onClick) {
        glyph.FontSize = 15;
        glyph.Foreground = Dim;
        var b = new Button {
            Content = glyph,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 6, 0),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false
        };
        b.Template = FlatButtonTemplate();
        b.Click += (_, _) => onClick();
        return b;
    }

    private static ControlTemplate FlatButtonTemplate() {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        t.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, CardHover, null));
        t.Triggers.Add(hover);
        return t;
    }

    private void Render() {
        _headline.Text = _app.Headline;
        _headline.Foreground = SevBrush(_app.Overall, headline: true);
        _stamp.Text = _app.LastScan == null
            ? "not scanned yet"
            : _app.LastScan.Value.ToString("ddd d MMM, HH:mm") + (_app.Config.Fetch ? " · fetched" : " · no fetch, behind counts may be stale")
              + " · every " + _app.Config.IntervalMinutes + " min";

        _body.Children.Clear();
        if (_app.Config.Repos.Count == 0) {
            _body.Children.Add(new TextBlock {
                Text = "Right-click the tray icon and choose “Add repo…”, or edit\n" + Config.FilePath,
                Foreground = Dim,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var states = _app.States.Count == 0
            ? _app.Config.Repos.Select(c => new RepoState { Config = c }).ToList()
            : _app.States.ToList();

        var card = new Border {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        var list = new StackPanel();
        for (int i = 0; i < states.Count; i++) {
            list.Children.Add(RepoBlock(states[i], last: i == states.Count - 1, scanned: _app.States.Count > 0));
        }
        card.Child = list;
        _body.Children.Add(card);
    }

    private UIElement RepoBlock(RepoState r, bool last, bool scanned) {
        var block = new StackPanel();
        var row = new Border {
            Padding = new Thickness(12, 9, 12, 9),
            Background = Brushes.Transparent,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, last && !_expanded.Contains(r.Name) ? 0 : 1),
            Cursor = Cursors.Hand,
            Tag = r.Name
        };
        row.MouseEnter += (_, _) => row.Background = CardHover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.PreviewMouseLeftButtonDown += (_, e) => {
            if (!_expanded.Remove(r.Name)) {
                _expanded.Add(r.Name);
            }
            e.Handled = true;
            Render();
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

        var dot = Dot(r.Severity, 9);
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetColumn(dot, 0);
        g.Children.Add(dot);

        var nameCol = new StackPanel();
        var nameLine = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        nameLine.Inlines.Add(new System.Windows.Documents.Run(r.Name) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(r.CurrentBranch)) {
            nameLine.Inlines.Add(new System.Windows.Documents.Run("  " + r.CurrentBranch) { FontFamily = Mono, Foreground = Dim, FontSize = 12 });
        }
        nameCol.Children.Add(nameLine);
        var sub = new TextBlock { Foreground = Dim, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        if (r.Error != null) {
            sub.Text = r.Error;
            sub.Foreground = Bad;
        } else if (!scanned) {
            sub.Text = "scanning…";
        } else {
            var bits = new List<string>();
            var others = r.Branches.Count - 1;
            if (others > 0) {
                var alarms = r.Branches.Count(b => !b.IsCurrent && b.Severity == Severity.Alarm);
                bits.Add(Scanner.Plural(others, "other branch") + (alarms > 0 ? ", " + alarms + " unbacked" : ""));
            }
            if (r.Dirty > 0) {
                bits.Add(r.Dirty + " modified");
            }
            if (r.Untracked > 0) {
                bits.Add(r.Untracked + " untracked");
            }
            if (bits.Count == 0) {
                bits.Add("clean");
            }
            if (r.FetchError != null) {
                bits.Add("fetch failed: " + r.FetchError);
            }
            sub.Text = string.Join(" · ", bits);
            if (r.FetchError != null) {
                sub.Foreground = Warn;
            }
        }
        nameCol.Children.Add(sub);
        Grid.SetColumn(nameCol, 1);
        g.Children.Add(nameCol);

        var cur = r.Current;
        if (cur != null && r.Error == null) {
            var counts = CountsBlock(cur.Upstream == null ? null : (cur.AheadUpstream, cur.BehindUpstream), cur.UpstreamGone, wide: true);
            counts.VerticalAlignment = VerticalAlignment.Top;
            counts.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(counts, 2);
            g.Children.Add(counts);

            var verdict = new TextBlock {
                Text = cur.Verdict,
                Foreground = SevBrush(cur.Severity),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 1, 0, 0),
                MinWidth = 110,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(verdict, 3);
            g.Children.Add(verdict);
        }

        var chevron = new TextBlock {
            Text = _expanded.Contains(r.Name) ? "▾" : "▸",
            Foreground = Dim,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0)
        };
        Grid.SetColumn(chevron, 4);
        g.Children.Add(chevron);

        row.Child = g;
        block.Children.Add(row);

        if (_expanded.Contains(r.Name) && r.Error == null && scanned) {
            block.Children.Add(BranchList(r, last));
        }
        return block;
    }

    private UIElement BranchList(RepoState r, bool last) {
        var wrap = new Border {
            Background = Bg,
            Padding = new Thickness(12, 6, 12, 8),
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1)
        };
        var panel = new StackPanel();

        var head = BranchGrid();
        AddCell(head, 1, Header("branch"));
        AddCell(head, 2, Header("vs upstream"));
        AddCell(head, 3, Header(r.DefaultBranch == null ? "vs default" : "vs " + r.DefaultBranch[(r.DefaultBranch.IndexOf('/') + 1)..]));
        AddCell(head, 4, Header("last commit"));
        AddCell(head, 5, Header(""));
        panel.Children.Add(head);

        foreach (var b in r.Branches) {
            var g = BranchGrid();
            g.Margin = new Thickness(0, 3, 0, 3);
            var d = Dot(b.Severity, 7);
            d.Margin = new Thickness(0, 5, 0, 0);
            d.VerticalAlignment = VerticalAlignment.Top;
            AddCell(g, 0, d);

            var name = new TextBlock {
                FontFamily = Mono,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = b.Hash + "  " + b.Subject
            };
            name.Inlines.Add(new System.Windows.Documents.Run(b.Name) { FontWeight = b.IsCurrent ? FontWeights.Bold : FontWeights.Normal });
            if (b.IsCurrent) {
                name.Inlines.Add(new System.Windows.Documents.Run("  ◀") { Foreground = Dim, FontSize = 9 });
            }
            AddCell(g, 1, name);

            AddCell(g, 2, CountsBlock(b.Upstream == null ? null : (b.AheadUpstream, b.BehindUpstream), b.UpstreamGone, wide: false));
            if (b.IsDefault) {
                AddCell(g, 3, new TextBlock { Text = "default", Foreground = Dim, FontSize = 12, TextAlignment = TextAlignment.Center });
            } else if (b.AheadDefault == null) {
                AddCell(g, 3, new TextBlock { Text = "–", Foreground = Dim, TextAlignment = TextAlignment.Center });
            } else {
                AddCell(g, 3, CountsBlock((b.AheadDefault.Value, b.BehindDefault ?? 0), false, wide: false, aheadIsAlarm: false));
            }
            AddCell(g, 4, new TextBlock { Text = Scanner.Ago(b.When), Foreground = Dim, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            AddCell(g, 5, new TextBlock {
                Text = b.Verdict,
                Foreground = SevBrush(b.Severity),
                FontSize = 12,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = b.Verdict
            });
            panel.Children.Add(g);
        }

        wrap.Child = panel;
        return wrap;
    }

    private static Grid BranchGrid() {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star), MinWidth = 150 });
        return g;
    }

    private static void AddCell(Grid g, int col, UIElement e) {
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    private static TextBlock Header(string text) {
        return new TextBlock {
            Text = text.ToUpperInvariant(),
            Foreground = Dim,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
            TextAlignment = text.StartsWith("vs") ? TextAlignment.Center : TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    /// <summary>▲ahead ▼behind, or "no upstream" / "gone".</summary>
    private static TextBlock CountsBlock((int ahead, int behind)? counts, bool gone, bool wide, bool aheadIsAlarm = true) {
        var t = new TextBlock {
            FontFamily = Mono,
            FontSize = 12.5,
            TextAlignment = TextAlignment.Center,
            MinWidth = wide ? 70 : 0
        };
        if (gone) {
            t.Text = "gone";
            t.Foreground = Warn;
            return t;
        }
        if (counts == null) {
            t.Text = wide ? "no upstream" : "none";
            t.Foreground = Bad;
            return t;
        }
        var (a, b) = counts.Value;
        var up = new System.Windows.Documents.Run("▲" + a) {
            Foreground = a > 0 ? (aheadIsAlarm ? Bad : Text) : Dim,
            FontWeight = a > 0 && aheadIsAlarm ? FontWeights.Bold : FontWeights.Normal
        };
        var down = new System.Windows.Documents.Run(" ▼" + b) {
            Foreground = b > 0 ? (aheadIsAlarm ? Warn : Dim) : Dim
        };
        t.Inlines.Add(up);
        t.Inlines.Add(down);
        return t;
    }

    private static Border Dot(Severity sev, double size) {
        var d = new Border {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = SevBrush(sev),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (sev == Severity.Alarm) {
            d.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x6B, 0x6B));
            d.BorderThickness = new Thickness(3);
            d.Width = size + 6;
            d.Height = size + 6;
            d.CornerRadius = new CornerRadius((size + 6) / 2);
            d.Margin = new Thickness(-3);
        }
        return d;
    }

    private static Brush SevBrush(Severity sev, bool headline = false) {
        return sev switch {
            Severity.Alarm => Bad,
            Severity.Warn => Warn,
            Severity.Info => headline ? Ok : Dim,
            _ => Ok
        };
    }

    private static Brush Hex(string hex) {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private void RestorePlacement() {
        var c = _app.Config;
        if (c.Width is > 300) {
            Width = c.Width.Value;
        }
        if (c.Left != null && c.Top != null) {
            var area = SystemParameters.VirtualScreenWidth;
            var areaH = SystemParameters.VirtualScreenHeight;
            var left = Math.Clamp(c.Left.Value, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + area - 100);
            var top = Math.Clamp(c.Top.Value, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + areaH - 60);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        } else {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + 24;
        }
    }

    public void SavePlacement() {
        if (WindowState != WindowState.Normal) {
            return;
        }
        var c = _app.Config;
        c.Left = Left;
        c.Top = Top;
        c.Save();
    }
}
