package de.ceilinglight

data class Scene(val name: String, val state: LampState)

/**
 * Turns the lamp state into commands. On EGLO lights where front and back share one zone,
 * white/warmth settings drive the front, colour settings drive the back, and both share on/off and brightness.
 */
class LampController(private val store: SettingsStore, val link: LampLink) {
    private val s get() = store.settings
    val state: LampState get() = s.state
    val stateListeners = mutableListOf<() -> Unit>()

    companion object {
        val SCENES = listOf(
            Scene("Bright", LampState(true, 100, 5000, 210, 15)),
            Scene("Cozy", LampState(true, 45, 2700, 28, 85)),
            Scene("Reading", LampState(true, 85, 4000, 30, 50)),
            Scene("Movie", LampState(true, 25, 2700, 235, 90)),
            Scene("Night", LampState(true, 8, 2700, 15, 95)),
        )
    }

    private val dest get() = s.meshId
    private fun changed() { store.save(); stateListeners.toList().forEach { it() } }

    fun setOn(on: Boolean) {
        state.on = on
        if (on) applyAll() else {
            link.send("power", dest, Protocol.off(s.frontZone))
            if (s.backZone != s.frontZone) link.send("power2", dest, Protocol.off(s.backZone))
        }
        changed()
    }

    fun toggle() = setOn(!state.on)

    fun setBrightness(pct: Int) {
        state.brightness = pct.coerceIn(1, 100); state.on = true
        link.send("power", dest, Protocol.level(s.frontZone, state.brightness))
        if (s.backZone != s.frontZone) link.send("power2", dest, Protocol.level(s.backZone, state.brightness))
        changed()
    }

    fun setKelvin(k: Int) {
        state.kelvin = k.coerceIn(2700, 6500)
        if (state.on) link.send("ct", dest, Protocol.colorTemp(s.frontZone, state.kelvin))
        changed()
    }

    fun setColor(hue: Int, sat: Int) {
        state.hue = ((hue % 360) + 360) % 360; state.saturation = sat.coerceIn(0, 100)
        if (!state.on) { state.on = true; applyAll() }
        else link.send("color", dest, Protocol.hueSat(s.backZone, state.hue, state.saturation))
        changed()
    }

    /** Applies a complete look (scenes, saved looks). */
    fun applyState(st: LampState) {
        s.state = st.copy()
        applyAll()
        changed()
    }

    /** Sends the complete current look, so the light comes back as you left it. */
    fun applyAll() {
        if (!state.on) { link.send("power", dest, Protocol.off(s.frontZone)); return }
        link.send("power", dest, Protocol.level(s.frontZone, state.brightness))
        if (s.backZone != s.frontZone) link.send("power2", dest, Protocol.level(s.backZone, state.brightness))
        link.send("ct", dest, Protocol.colorTemp(s.frontZone, state.kelvin))
        link.send("color", dest, Protocol.hueSat(s.backZone, state.hue, state.saturation))
    }
}
