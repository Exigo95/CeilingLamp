package de.ceilinglight

import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/** Quick Settings tile next to Wi-Fi and the flashlight: tap to switch the light. */
class LampTileService : TileService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val lamp get() = Lamp.get(this)
    private val onState: () -> Unit = { scope.launch { updateTile() } }

    override fun onStartListening() {
        super.onStartListening()
        lamp.controller.stateListeners.add(onState)
        updateTile()
    }

    override fun onStopListening() {
        lamp.controller.stateListeners.remove(onState)
        super.onStopListening()
    }

    override fun onClick() {
        super.onClick()
        if (!lamp.settings.isConfigured || !lamp.link.hasPermissions()) {
            openApp(); return
        }
        qsTile?.let { it.state = Tile.STATE_UNAVAILABLE; it.updateTile() } // shows "working" until done
        scope.launch {
            withTimeoutOrNull(9500) { lamp.quickToggle() }
            updateTile()
        }
    }

    private fun openApp() {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION", "StartActivityAndCollapseDeprecated")
            startActivityAndCollapse(intent)
        }
    }

    private fun updateTile() {
        val tile = qsTile ?: return
        val s = lamp.settings
        tile.label = s.name
        tile.state = if (!s.isConfigured) Tile.STATE_INACTIVE else if (s.state.on) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        if (Build.VERSION.SDK_INT >= 29) tile.subtitle = if (!s.isConfigured) "Set up" else if (s.state.on) "${s.state.brightness} %" else "Off"
        tile.updateTile()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
