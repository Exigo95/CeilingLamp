using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CeilingLamp.Ui;

/// <summary>
/// Borderless, rounded window that hosts the dark interface (app.html) in WebView2.
/// The page sends commands as JSON messages; this class applies them to the lamp and reports back.
/// </summary>
public sealed class MainWindow : Form
{
    private const int CssWidth = 380;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly Color Night = Color.FromArgb(0x12, 0x14, 0x17);

    private readonly AppSettings _s;
    private readonly LampController _lamp;
    private readonly WebView2 _web;
    private bool _ready, _pinned;
    private string _view = "light";
    private Point? _anchor;
    private int _cssHeight = 640;

    public DateTime HiddenAt { get; private set; }

    public MainWindow(AppSettings settings, LampController lamp)
    {
        _s = settings; _lamp = lamp;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = Night; Text = "Ceiling Light";
        try { Icon = TrayApp.LoadIcon("lamp.ico"); } catch { }
        ClientSize = new Size(Px(CssWidth), Px(_cssHeight));

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Night };
        Controls.Add(_web);

        Deactivate += (_, _) => { if (!_pinned) { Hide(); HiddenAt = DateTime.UtcNow; } };
        _lamp.Link.StatusChanged += () => Ui(() => Post(new { type = "status", status = StatusDto() }));
        _lamp.StateChanged += () => Ui(() => Post(new { type = "state", state = _lamp.State }));
        _lamp.Link.Log += line => Ui(() => Post(new { type = "log", line }));
    }

    // ---------- window chrome ----------
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* drop shadow */ return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Round(Handle);
        Theme.DarkTitleBar(Handle, true);
        if (!_initStarted) { _initStarted = true; _ = _web.Handle; _ = InitAsync(); }
    }
    private bool _initStarted;

    private int Px(int css) => (int)Math.Round(css * DeviceDpi / 96.0);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---------- showing ----------
    public void ShowAt(Point? anchor, string view = "light")
    {
        _anchor = anchor; _view = view;
        if (_ready) Post(new { type = "view", view });
        Place();
        Show(); Activate();
        _web.Focus();
        if (_s.IsConfigured) _ = _lamp.Link.ConnectAsync(); // be ready so the first change reacts quickly
    }

    private void Place()
    {
        var wa = Screen.FromPoint(_anchor ?? Cursor.Position).WorkingArea;
        var h = Math.Min(Px(_cssHeight), wa.Height - Px(24));
        ClientSize = new Size(Px(CssWidth), h);
        if (_pinned && Visible) { Top = Math.Min(Top, wa.Bottom - Height - Px(12)); return; }
        var a = _anchor ?? new Point(wa.Right, wa.Bottom);
        var x = Math.Clamp(a.X - Width / 2, wa.Left + Px(12), Math.Max(wa.Left + Px(12), wa.Right - Width - Px(12)));
        var y = Math.Clamp(a.Y - Height - Px(12), wa.Top + Px(12), Math.Max(wa.Top + Px(12), wa.Bottom - Height - Px(12)));
        Location = new Point(x, y);
    }

    // ---------- WebView2 ----------
    private async Task InitAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CeilingLamp", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.WebMessageReceived += OnMessage;
            core.NavigateToString(LoadHtml());
        }
        catch (Exception ex)
        {
            MessageBox.Show("The control panel needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 and most Windows 10 PCs.\n\n" +
                            "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start Ceiling Light again.\n\nDetails: " + ex.Message,
                "Ceiling Light", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string LoadHtml()
    {
        using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("app.html") ?? throw new InvalidOperationException("app.html missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void Post(object msg)
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg, Json));
    }

    private object StatusDto() => new { state = _lamp.Link.State.ToString(), text = _lamp.Link.StatusText };

    private object SettingsDto() => new
    {
        name = _s.Name, meshName = _s.MeshName, meshPassword = _s.MeshPassword, meshId = _s.MeshId, mac = _s.Mac,
        frontZone = _s.FrontZone, backZone = _s.BackZone, releaseAfterSeconds = _s.ReleaseAfterSeconds, turnOnAtStartup = _s.TurnOnAtStartup,
    };

    private void SendInit()
    {
        string[] log; lock (TrayApp.LogLines) log = TrayApp.LogLines.ToArray();
        Post(new
        {
            type = "init", settings = SettingsDto(), state = _lamp.State, status = StatusDto(),
            scenes = LampController.Scenes.Select(sc => new { name = sc.Name, state = sc.State }),
            custom = _s.CustomScenes, startup = AppSettings.StartWithWindows, pinned = _pinned, log, view = _view,
        });
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            var cmd = m.GetProperty("cmd").GetString();
            switch (cmd)
            {
                case "ready": _ready = true; SendInit(); break;
                case "resize": _cssHeight = Math.Max(200, m.GetProperty("height").GetInt32()); Place(); break;
                case "close": Hide(); HiddenAt = DateTime.UtcNow; break;
                case "pin": _pinned = m.GetProperty("value").GetBoolean(); break;
                case "drag": ReleaseCapture(); SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero); break;
                case "connect": _ = _lamp.Link.ConnectAsync(); break;

                case "power": _lamp.SetOn(m.GetProperty("on").GetBoolean()); break;
                case "brightness": _lamp.SetBrightness(m.GetProperty("value").GetInt32()); break;
                case "kelvin": _lamp.SetKelvin(m.GetProperty("value").GetInt32()); break;
                case "color": _lamp.SetColor(m.GetProperty("hue").GetInt32(), m.GetProperty("sat").GetInt32()); break;
                case "apply":
                    var st = m.GetProperty("state").Deserialize<LampState>(Json);
                    if (st != null) _lamp.ApplyState(st);
                    break;

                case "saveScene":
                    var ns = m.GetProperty("state").Deserialize<LampState>(Json) ?? _lamp.State.Clone();
                    _s.CustomScenes.Add(new SavedScene { Name = m.GetProperty("name").GetString() ?? "My look", State = ns });
                    _s.Save();
                    Post(new { type = "custom", custom = _s.CustomScenes });
                    break;
                case "deleteScene":
                    var i = m.GetProperty("index").GetInt32();
                    if (i >= 0 && i < _s.CustomScenes.Count) { _s.CustomScenes.RemoveAt(i); _s.Save(); }
                    Post(new { type = "custom", custom = _s.CustomScenes });
                    break;

                case "saveSettings":
                    var p = m.GetProperty("settings");
                    _s.Name = p.GetProperty("name").GetString() ?? "Ceiling light";
                    _s.MeshName = p.GetProperty("meshName").GetString() ?? "";
                    _s.MeshPassword = p.GetProperty("meshPassword").GetString() ?? "";
                    _s.MeshId = p.GetProperty("meshId").GetInt32();
                    _s.Mac = (p.GetProperty("mac").GetString() ?? "").Trim().ToUpperInvariant().Replace('-', ':');
                    _s.FrontZone = p.GetProperty("frontZone").GetInt32();
                    _s.BackZone = p.GetProperty("backZone").GetInt32();
                    _s.ReleaseAfterSeconds = p.GetProperty("releaseAfterSeconds").GetInt32();
                    _s.TurnOnAtStartup = p.GetProperty("turnOnAtStartup").GetBoolean();
                    try { AppSettings.StartWithWindows = m.GetProperty("startup").GetBoolean(); } catch { }
                    _s.Save();
                    _lamp.Link.Release("Settings saved, connects when you use it");
                    Post(new { type = "settingsSaved", settings = SettingsDto(), startup = AppSettings.StartWithWindows });
                    break;

                case "blink":
                    _lamp.Link.Release("Testing…");
                    for (var k = 0; k < 2; k++)
                    {
                        _lamp.Link.Send("blink", _s.MeshId, Protocol.Off(_s.FrontZone)); await Task.Delay(700);
                        _lamp.Link.Send("blink", _s.MeshId, Protocol.On(_s.FrontZone)); await Task.Delay(700);
                    }
                    _lamp.ApplyAll();
                    break;

                case "import":
                    try
                    {
                        var r = await CloudImport.FetchAsync(m.GetProperty("email").GetString() ?? "", m.GetProperty("password").GetString() ?? "");
                        Post(new { type = "import", ok = true, meshName = r.MeshName, meshPassword = r.MeshPassword,
                                   lights = r.Lights.Select(l => new { name = l.Name, meshId = l.MeshId, mac = l.Mac }) });
                    }
                    catch (Exception ex) { Post(new { type = "import", ok = false, error = "Import failed: " + ex.Message }); }
                    break;

                case "scan":
                    _lamp.Link.Release();
                    var found = await LampConnection.ScanAsync(TimeSpan.FromSeconds(6));
                    // EGLO / AwoX lights use Telink chips: addresses start with A4:C1:38
                    var devices = found.Select(f => new { mac = f.Mac, name = f.Name, rssi = (int)f.Rssi, likely = f.Mac.StartsWith("A4:C1:38") })
                                       .OrderByDescending(d => d.likely).ThenByDescending(d => d.rssi).Take(15);
                    Post(new { type = "scan", devices });
                    break;
            }
        }
        catch (Exception ex)
        {
            Post(new { type = "toast", text = "Something went wrong: " + ex.Message });
        }
    }
}
