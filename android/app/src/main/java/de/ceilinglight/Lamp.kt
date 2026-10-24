package de.ceilinglight

import android.content.ComponentName
import android.content.Context
import android.service.quicksettings.TileService
import kotlinx.coroutines.delay

/** One shared lamp connection for the app screen, the widget and the Quick Settings tile. */
class Lamp private constructor(private val ctx: Context) {
    val store = SettingsStore(ctx)
    val settings: Settings get() = store.settings
    val link = LampLink(ctx) { store.settings }
    val controller = LampController(store, link)

    /** True while the app screen is open; then the normal idle release applies. */
    @Volatile var uiVisible = false

    init {
        // keep the widget and the tile in step with the light
        controller.stateListeners.add { refreshShortcuts() }
    }

    fun refreshShortcuts() {
        LampWidget.updateAll(ctx)
        try { TileService.requestListeningState(ctx, ComponentName(ctx, LampTileService::class.java)) } catch (_: Exception) {}
    }

    /** Turns the light on (with its last look) or off from the widget or tile, then lets go of it quickly. */
    suspend fun quickToggle() {
        if (!settings.isConfigured) return
        controller.toggle()
        link.awaitDrained(9000)
        if (!uiVisible) {
            delay(1500)
            if (!uiVisible && !link.isBusy) link.release()
        }
    }

    companion object {
        @Volatile private var instance: Lamp? = null
        fun get(context: Context): Lamp =
            instance ?: synchronized(this) { instance ?: Lamp(context.applicationContext).also { instance = it } }
    }
}
