namespace CeilingLamp;

public sealed record Scene(string Name, LampState State);

/// <summary>Turns the lamp state into commands. On EGLO lights where front and back share one zone,
/// white/warmth settings drive the front, colour settings drive the back, and both share on/off and brightness.</summary>
public sealed class LampController
{
    private readonly AppSettings _s;
    public LampConnection Link { get; }
    public LampState State => _s.State;
    public event Action? StateChanged;

    public static readonly Scene[] Scenes =
    {
        new("Bright",  new LampState { On = true, Brightness = 100, Kelvin = 5000, Hue = 210, Saturation = 15 }),
        new("Cozy",    new LampState { On = true, Brightness = 45,  Kelvin = 2700, Hue = 28,  Saturation = 85 }),
        new("Reading", new LampState { On = true, Brightness = 85,  Kelvin = 4000, Hue = 30,  Saturation = 50 }),
        new("Movie",   new LampState { On = true, Brightness = 25,  Kelvin = 2700, Hue = 235, Saturation = 90 }),
        new("Night",   new LampState { On = true, Brightness = 8,   Kelvin = 2700, Hue = 15,  Saturation = 95 }),
    };

    public LampController(AppSettings settings, LampConnection link) { _s = settings; Link = link; }

    private int Dest => _s.MeshId;
    private void Changed() { _s.Save(); StateChanged?.Invoke(); }

    public void SetOn(bool on)
    {
        State.On = on;
        if (on) ApplyAll(); else Link.Send("power", Dest, Protocol.Off(_s.FrontZone));
        if (!on && _s.BackZone != _s.FrontZone) Link.Send("power2", Dest, Protocol.Off(_s.BackZone));
        Changed();
    }

    public void Toggle() => SetOn(!State.On);

    public void SetBrightness(int pct)
    {
        State.Brightness = Math.Clamp(pct, 1, 100); State.On = true;
        Link.Send("power", Dest, Protocol.Level(_s.FrontZone, State.Brightness));
        if (_s.BackZone != _s.FrontZone) Link.Send("power2", Dest, Protocol.Level(_s.BackZone, State.Brightness));
        Changed();
    }

    public void SetKelvin(int k)
    {
        State.Kelvin = Math.Clamp(k, 2700, 6500);
        if (State.On) Link.Send("ct", Dest, Protocol.ColorTemp(_s.FrontZone, State.Kelvin));
        Changed();
    }

    public void SetColor(int hue, int sat)
    {
        State.Hue = ((hue % 360) + 360) % 360; State.Saturation = Math.Clamp(sat, 0, 100);
        if (!State.On) { State.On = true; ApplyAll(); }
        else Link.Send("color", Dest, Protocol.HueSat(_s.BackZone, State.Hue, State.Saturation));
        Changed();
    }

    /// <summary>Applies a complete look (scenes, saved looks).</summary>
    public void ApplyState(LampState st)
    {
        _s.State = st.Clone();
        State.Brightness = Math.Clamp(State.Brightness, 1, 100);
        State.Kelvin = Math.Clamp(State.Kelvin, 2700, 6500);
        ApplyAll();
        Changed();
    }

    public void ApplyScene(Scene sc) => ApplyState(sc.State);

    /// <summary>Sends the complete current look (used when turning on, so it comes back as you left it).</summary>
    public void ApplyAll()
    {
        if (!State.On) { Link.Send("power", Dest, Protocol.Off(_s.FrontZone)); return; }
        Link.Send("power", Dest, Protocol.Level(_s.FrontZone, State.Brightness));
        if (_s.BackZone != _s.FrontZone) Link.Send("power2", Dest, Protocol.Level(_s.BackZone, State.Brightness));
        Link.Send("ct", Dest, Protocol.ColorTemp(_s.FrontZone, State.Kelvin));
        Link.Send("color", Dest, Protocol.HueSat(_s.BackZone, State.Hue, State.Saturation));
    }
}
