using CeilingLamp.Ui;

namespace CeilingLamp;

/// <summary>Tray icon: left click opens the control panel, middle click toggles the light, right click shows a menu.</summary>
public sealed class TrayApp : ApplicationContext
{
    public static readonly List<string> LogLines = new();

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LampConnection _link;
    private readonly LampController _lamp;
    private readonly NotifyIcon _tray;
    private readonly Icon _iconOn = LoadIcon("lamp.ico"), _iconOff = LoadIcon("lamp-off.ico");
    private readonly MainWindow _window;
    private readonly ToolStripMenuItem _toggleItem, _startupItem, _scenesItem;

    public TrayApp(bool startedByWindows)
    {
        _link = new LampConnection(_settings);
        _lamp = new LampController(_settings, _link);
        _link.Log += line => { lock (LogLines) { LogLines.Add(line); if (LogLines.Count > 200) LogLines.RemoveAt(0); } };

        _window = new MainWindow(_settings, _lamp);
        _ = _window.Handle; // create the window (and start WebView2) now, hidden, so it opens instantly later
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _link.StatusChanged += () => ui.Post(_ => UpdateTray(), null);
        _lamp.StateChanged += () => ui.Post(_ => UpdateTray(), null);

        _toggleItem = new ToolStripMenuItem("Turn off", null, (_, _) => _lamp.Toggle());
        _scenesItem = new ToolStripMenuItem("Scenes");
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            try { AppSettings.StartWithWindows = !AppSettings.StartWithWindows; } catch { }
        });
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        _scenesItem.DropDown.Renderer = menu.Renderer;
        menu.Items.Add("Open control panel", null, (_, _) => _window.ShowAt(null));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_scenesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => _window.ShowAt(null, "settings"));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) =>
        {
            _startupItem.Checked = AppSettings.StartWithWindows;
            _toggleItem.Text = _lamp.State.On ? "Turn off" : "Turn on";
            _scenesItem.DropDownItems.Clear();
            foreach (var sc in LampController.Scenes) _scenesItem.DropDownItems.Add(sc.Name, null, (_, _) => _lamp.ApplyScene(sc));
            if (_settings.CustomScenes.Count > 0) _scenesItem.DropDownItems.Add(new ToolStripSeparator());
            foreach (var sc in _settings.CustomScenes.ToList()) _scenesItem.DropDownItems.Add(sc.Name, null, (_, _) => _lamp.ApplyState(sc.State));
            foreach (ToolStripItem i in _scenesItem.DropDownItems) i.ForeColor = DarkMenuRenderer.Text;
            if (_scenesItem.DropDown is ToolStripDropDownMenu dd) dd.ShowImageMargin = false;
        };
        foreach (ToolStripItem i in menu.Items) i.ForeColor = DarkMenuRenderer.Text;

        _tray = new NotifyIcon { Icon = _iconOn, Visible = true, ContextMenuStrip = menu, Text = "Ceiling light" };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_window.Visible) { _window.Hide(); return; }
                if ((DateTime.UtcNow - _window.HiddenAt).TotalMilliseconds < 300) return; // the click that just closed it
                _window.ShowAt(Cursor.Position);
            }
            else if (e.Button == MouseButtons.Middle) _lamp.Toggle();
        };
        UpdateTray();

        if (!_settings.IsConfigured) _window.ShowAt(null, "settings");
        else if (startedByWindows && _settings.TurnOnAtStartup) _ = TurnOnLaterAsync();
        else if (!startedByWindows) _window.ShowAt(null);
    }

    private async Task TurnOnLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)); // give Bluetooth time to start after login
        _lamp.SetOn(true);
    }

    private void UpdateTray()
    {
        var s = _lamp.State;
        _tray.Icon = s.On ? _iconOn : _iconOff;
        var text = $"{_settings.Name}: {(s.On ? $"on, {s.Brightness} %" : "off")} ({_link.StatusText})";
        _tray.Text = text.Length > 63 ? text[..63] : text; // Windows limit
    }

    public static Icon LoadIcon(string name)
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing icon " + name);
        return new Icon(s);
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _link.Dispose();
        _settings.Save();
        _window.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Dark tray menu to match the control panel.</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public static readonly Color Bg = Color.FromArgb(0x1a, 0x1d, 0x22), Hover = Color.FromArgb(0x2e, 0x33, 0x3b), Text = Color.FromArgb(0xec, 0xea, 0xe6), Line = Color.FromArgb(0x2e, 0x33, 0x3b);
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Text; base.OnRenderItemText(e); }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Text; base.OnRenderArrow(e); }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, r, Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
