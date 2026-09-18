using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GitStateWidget;

/// <summary>
/// The always-present mirror of the panel: a dot the colour of the worst repo, the headline as
/// tooltip, and the housekeeping menu. Left-click toggles the panel.
/// </summary>
public sealed class TrayIcon : IDisposable {
    private readonly App _app;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _onTop;
    private readonly ToolStripMenuItem _startup;
    private readonly ToolStripMenuItem _fetch;
    private Icon? _current;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public TrayIcon(App app) {
        _app = app;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / hide panel", null, (_, _) => _app.TogglePanel());
        menu.Items.Add("Refresh now", null, (_, _) => _ = _app.RefreshAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Add repo…", null, (_, _) => AddRepo());
        menu.Items.Add("Open config file", null, (_, _) => OpenConfig());
        menu.Items.Add("Reload config", null, (_, _) => _app.ReloadConfig());
        menu.Items.Add(new ToolStripSeparator());
        _fetch = new ToolStripMenuItem("Fetch before each scan") { CheckOnClick = true };
        _fetch.Click += (_, _) => {
            _app.Config.Fetch = _fetch.Checked;
            _app.Config.Save();
        };
        menu.Items.Add(_fetch);
        _onTop = new ToolStripMenuItem("Always on top") { CheckOnClick = true };
        _onTop.Click += (_, _) => {
            _app.Config.AlwaysOnTop = _onTop.Checked;
            _app.Config.Save();
            _app.ApplyPanelConfig();
            _app.ShowPanel();
        };
        menu.Items.Add(_onTop);
        _startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startup.Click += (_, _) => {
            try {
                Startup.Set(_startup.Checked);
            } catch (Exception ex) {
                _startup.Checked = Startup.IsEnabled();
                MessageBox.Show("Could not update the Run key: " + ex.Message, "Git State", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _app.ExitApp());
        menu.Opening += (_, _) => {
            _onTop.Checked = _app.Config.AlwaysOnTop;
            _fetch.Checked = _app.Config.Fetch;
            _startup.Checked = Startup.IsEnabled();
        };

        _icon = new NotifyIcon {
            Text = "Git State",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) => {
            if (e.Button == MouseButtons.Left) {
                _app.TogglePanel();
            }
        };
        SetState(Severity.Ok, "Git State – scanning…");
        _app.StatesChanged += () => SetState(_app.Overall, "Git State – " + _app.Headline);
    }

    private void SetState(Severity sev, string tooltip) {
        var color = sev switch {
            Severity.Alarm => Color.FromArgb(0xFF, 0x6B, 0x6B),
            Severity.Warn => Color.FromArgb(0xFF, 0xB4, 0x54),
            Severity.Info => Color.FromArgb(0x5E, 0xC2, 0x7A),
            _ => Color.FromArgb(0x5E, 0xC2, 0x7A)
        };
        var old = _current;
        _current = Draw(color, sev == Severity.Alarm);
        _icon.Icon = _current;
        if (old != null) {
            var h = old.Handle;
            old.Dispose();
            DestroyIcon(h);
        }
        // NotifyIcon caps tooltip text at 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private static Icon Draw(Color color, bool ring) {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            if (ring) {
                using var pen = new Pen(Color.FromArgb(90, color), 3);
                g.DrawEllipse(pen, 3, 3, 26, 26);
            }
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 8, 8, 16, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void AddRepo() {
        using var dlg = new FolderBrowserDialog {
            Description = "Pick the root of a git repository to watch",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath)) {
            _app.AddRepo(dlg.SelectedPath);
        }
    }

    private static void OpenConfig() {
        try {
            Process.Start(new ProcessStartInfo(Config.FilePath) { UseShellExecute = true });
        } catch (Exception ex) {
            MessageBox.Show("Could not open " + Config.FilePath + "\n" + ex.Message, "Git State", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void Dispose() {
        _icon.Visible = false;
        _icon.Dispose();
        if (_current != null) {
            var h = _current.Handle;
            _current.Dispose();
            DestroyIcon(h);
        }
    }
}
