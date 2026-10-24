using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Win32;

namespace CeilingLamp;

public sealed class LampState
{
    public bool On { get; set; } = true;
    public int Brightness { get; set; } = 80;   // 1..100 %
    public int Kelvin { get; set; } = 4000;     // front light warmth, 2700..6500
    public int Hue { get; set; } = 260;         // back light colour, 0..359
    public int Saturation { get; set; } = 80;   // 0..100 %

    public LampState Clone() => (LampState)MemberwiseClone();
}

public sealed class SavedScene
{
    public string Name { get; set; } = "";
    public LampState State { get; set; } = new();
}

public sealed class AppSettings
{
    public string Name { get; set; } = "Ceiling light";
    public string MeshName { get; set; } = "";
    public string MeshPassword { get; set; } = "";
    public int MeshId { get; set; }
    /// <summary>Bluetooth address of the light, e.g. A4:C1:38:12:34:56. Windows needs it to connect.</summary>
    public string Mac { get; set; } = "";
    /// <summary>Zone for white/front. On EGLO lights with one shared channel, front and back use the same zone.</summary>
    public int FrontZone { get; set; } = 1;
    public int BackZone { get; set; } = 1;
    /// <summary>Seconds without use before the light is released for other devices; 0 = never.</summary>
    public int ReleaseAfterSeconds { get; set; } = 30;
    public LampState State { get; set; } = new();
    /// <summary>Looks the user saved with "Save current look".</summary>
    public List<SavedScene> CustomScenes { get; set; } = new();
    /// <summary>Turn the light on with its last look when the app starts with Windows.</summary>
    public bool TurnOnAtStartup { get; set; }

    public bool IsConfigured => MeshName.Length > 0 && MeshPassword.Length > 0 && MeshId > 0 && Mac.Length > 0;

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CeilingLamp");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* not fatal */ }
    }

    // ---- Start with Windows (per-user Run key) ----
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CeilingLamp";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(RunValue, false);
        }
    }
}

/// <summary>Reads the light's Bluetooth key and address from the EGLO / AwoX account (same as the official app).</summary>
public static class CloudImport
{
    private const string AppId = "55O69FLtoxPt67LLwaHGpHmVWndhZGn9Wty8PLrJ";
    private const string ClientKey = "PyR3yV65rytEicteNlQHSVNpAGvCByOrsLiEqJtI";
    private static readonly string[] Bases =
    {
        "https://l4hparse-hc-prod.awox.cloud/parse/",
        "https://l4hparse-prod.awox.cloud/parse/",
    };

    public sealed record Light(string Name, int MeshId, string Mac, string Type);
    public sealed record Result(string MeshName, string MeshPassword, List<Light> Lights);

    public static async Task<Result> FetchAsync(string email, string password)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var installId = Guid.NewGuid().ToString();
        Exception? last = null;
        // The server treats emails case-sensitively; accounts are normally stored in lower case.
        var users = new[] { email.Trim().ToLowerInvariant(), email.Trim() }.Distinct().ToArray();

        foreach (var b in Bases)
        foreach (var user in users)
        {
            try
            {
                var login = await PostAsync(http, b + "login", installId, null, new { username = user, password, _method = "GET" });
                var session = login.GetProperty("sessionToken").GetString()!;
                var objectId = login.GetProperty("objectId").GetString()!;
                var where = new { where = new { owner = new { __type = "Pointer", className = "_User", objectId } }, _method = "GET" };
                var creds = (await PostAsync(http, b + "classes/Credential", installId, session, where)).GetProperty("results");
                var devs = (await PostAsync(http, b + "classes/Device", installId, session, where)).GetProperty("results");

                JsonElement? cred = null;
                foreach (var c in creds.EnumerateArray()) if (Str(c, "service") == "zigbee") { cred = c; break; }
                if (cred is null) foreach (var c in creds.EnumerateArray()) if (Str(c, "service") == "mesh") { cred = c; break; }
                if (cred is null) throw new InvalidOperationException("No Bluetooth key found in this account.");

                var lights = new List<Light>();
                foreach (var d in devs.EnumerateArray())
                {
                    if (!d.TryGetProperty("address", out var a) || a.ValueKind == JsonValueKind.Null) continue;
                    var id = a.ValueKind == JsonValueKind.Number ? a.GetInt32() : int.TryParse(a.GetString(), out var n) ? n : 0;
                    if (id < 0) id += 65536;
                    var mac = (Str(d, "macAddress") ?? "").ToUpperInvariant();
                    lights.Add(new Light((Str(d, "displayName") ?? mac).Trim(), id, mac, Str(d, "type") ?? ""));
                }
                return new Result(Str(cred.Value, "client_id") ?? "", Str(cred.Value, "access_token") ?? "", lights);
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("Import failed.");
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string url, string installId, string? session, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("x-parse-application-id", AppId);
        req.Headers.Add("x-parse-client-key", ClientKey);
        req.Headers.Add("x-parse-installation-id", installId);
        if (session != null) req.Headers.Add("x-parse-session-token", session);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : resp.StatusCode.ToString();
            throw new InvalidOperationException(msg == "Invalid username/password." ? "The EGLO server did not accept this email and password." : msg);
        }
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
