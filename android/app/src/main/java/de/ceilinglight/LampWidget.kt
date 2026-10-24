package de.ceilinglight

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/** Home-screen button: one tap turns the light on with its last look, or off. */
class LampWidget : AppWidgetProvider() {

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        ids.forEach { manager.updateAppWidget(it, views(context)) }
    }

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action != ACTION_TOGGLE) return
        val lamp = Lamp.get(context)
        if (!lamp.settings.isConfigured || !lamp.link.hasPermissions()) { updateAll(context); return }
        val done = goAsync() // keeps the receiver alive while Bluetooth does its work (max ~10 s)
        scope.launch {
            try { withTimeoutOrNull(9500) { lamp.quickToggle() } }
            finally { updateAll(context); done.finish() }
        }
    }

    companion object {
        const val ACTION_TOGGLE = "de.ceilinglight.TOGGLE"
        private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

        fun updateAll(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val ids = manager.getAppWidgetIds(ComponentName(context, LampWidget::class.java))
            if (ids.isEmpty()) return
            val v = views(context)
            ids.forEach { manager.updateAppWidget(it, v) }
        }

        private fun views(context: Context): RemoteViews {
            val lamp = Lamp.get(context)
            val s = lamp.settings
            val on = s.state.on
            val v = RemoteViews(context.packageName, R.layout.widget_lamp)
            v.setImageViewResource(R.id.icon, if (on) R.drawable.widget_on else R.drawable.widget_off)
            v.setTextViewText(R.id.label, if (!s.isConfigured) "Set up" else if (on) "On" else "Off")
            v.setContentDescription(R.id.root, "${s.name}: ${if (on) "on" else "off"}. Tap to switch.")
            val pi = if (s.isConfigured && lamp.link.hasPermissions()) {
                val toggle = Intent(context, LampWidget::class.java).setAction(ACTION_TOGGLE)
                PendingIntent.getBroadcast(context, 0, toggle, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            } else {
                // not set up yet: the widget opens the app instead
                PendingIntent.getActivity(context, 1, Intent(context, MainActivity::class.java), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            }
            v.setOnClickPendingIntent(R.id.root, pi)
            return v
        }
    }
}
