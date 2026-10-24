using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CeilingLamp.Ui;

/// <summary>Follows the Windows light/dark app setting; same palette as the website.</summary>
public sealed class Theme
{
    public Color Bg, Surface, Surface2, Line, Fg, Muted, Danger, Ok;
    public bool Dark;

    public static Theme Current()
    {
        var dark = true;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) dark = v == 0;
        }
        catch { }
        return dark
            ? new Theme { Dark = true, Bg = Hex("#0e1116"), Surface = Hex("#171b22"), Surface2 = Hex("#222833"), Line = Hex("#2c3340"), Fg = Hex("#eef0f4"), Muted = Hex("#98a1b0"), Danger = Hex("#ff8a7a"), Ok = Hex("#6fd3a0") }
            : new Theme { Dark = false, Bg = Hex("#f3f4f6"), Surface = Hex("#ffffff"), Surface2 = Hex("#e9ebef"), Line = Hex("#d7dbe2"), Fg = Hex("#171a1f"), Muted = Hex("#5d6573"), Danger = Hex("#b42318"), Ok = Hex("#1f7a4d") };
    }

    public static Color Hex(string h) => ColorTranslator.FromHtml(h);

    // ---- colour helpers (same maths as the website) ----
    public static Color FromHsv(double h, double s, double v)
    {
        double F(int n) { var k = (n + h / 60) % 6; return v - v * s * Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1)); }
        return Color.FromArgb((int)Math.Round(F(5) * 255), (int)Math.Round(F(3) * 255), (int)Math.Round(F(1) * 255));
    }

    public static Color FromKelvin(int k)
    {
        var t = k / 100.0;
        var r = t <= 66 ? 255 : 329.7 * Math.Pow(t - 60, -0.1332);
        var g = t <= 66 ? 99.47 * Math.Log(t) - 161.12 : 288.12 * Math.Pow(t - 60, -0.0755);
        var b = t >= 66 ? 255 : t <= 19 ? 0 : 138.52 * Math.Log(t - 10) - 305.04;
        static int C(double x) => (int)Math.Clamp(Math.Round(x), 0, 255);
        return Color.FromArgb(C(r), C(g), C(b));
    }

    // ---- Windows 11 rounded corners and dark title bars ----
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Round(IntPtr hwnd)
    {
        try { var pref = 2; DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); } catch { }
    }

    public static void DarkTitleBar(IntPtr hwnd, bool dark)
    {
        try { var v = dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); } catch { }
    }
}
