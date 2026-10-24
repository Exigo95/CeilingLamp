package de.ceilinglight

import android.Manifest
import android.annotation.SuppressLint
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.webkit.JavascriptInterface
import android.webkit.WebView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

/** Shows the shared dark interface (app.html) and connects it to the lamp. */
class MainActivity : ComponentActivity() {
    private lateinit var web: WebView
    private lateinit var lamp: Lamp
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var ready = false
    private var startView = "light"

    private val onStatus: () -> Unit = { runOnUiThread { post(JSONObject().put("type", "status").put("status", statusJson())) } }
    private val onState: () -> Unit = { runOnUiThread { post(JSONObject().put("type", "state").put("state", lamp.controller.state.toJson())) } }
    private val onLog: (String) -> Unit = { line -> runOnUiThread { post(JSONObject().put("type", "log").put("line", line)) } }

    private val askPermissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        lamp.refreshShortcuts()
        if (lamp.link.hasPermissions()) lamp.link.connectInBackground()
        else post(JSONObject().put("type", "toast").put("text", "Bluetooth access is needed to control the light"))
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        lamp = Lamp.get(this)
        window.statusBarColor = Color.rgb(0x12, 0x14, 0x17)
        window.navigationBarColor = Color.rgb(0x12, 0x14, 0x17)

        web = WebView(this)
        web.setBackgroundColor(Color.rgb(0x12, 0x14, 0x17))
        web.settings.javaScriptEnabled = true
        web.settings.domStorageEnabled = true
        web.settings.textZoom = 100
        web.isVerticalScrollBarEnabled = false
        web.addJavascriptInterface(Bridge(), "AndroidHost")
        setContentView(web)

        if (!lamp.settings.isConfigured) startView = "settings"
        web.loadDataWithBaseURL("https://ceilinglight.local/", loadHtml(), "text/html", "utf-8", null)

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (ready) post(JSONObject().put("type", "back")) else finish()
            }
        })

        lamp.link.statusListeners.add(onStatus)
        lamp.controller.stateListeners.add(onState)
        lamp.link.logListeners.add(onLog)
    }

    override fun onResume() {
        super.onResume()
        lamp.uiVisible = true
        if (!lamp.link.hasPermissions()) {
            askPermissions.launch(
                if (Build.VERSION.SDK_INT >= 31) arrayOf(Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.BLUETOOTH_SCAN)
                else arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
            )
        } else if (lamp.settings.isConfigured) {
            lamp.link.connectInBackground() // be ready so the first change reacts quickly
        }
        if (ready) post(JSONObject().put("type", "state").put("state", lamp.controller.state.toJson()))
    }

    override fun onPause() {
        super.onPause()
        lamp.uiVisible = false
    }

    override fun onDestroy() {
        lamp.link.statusListeners.remove(onStatus)
        lamp.controller.stateListeners.remove(onState)
        lamp.link.logListeners.remove(onLog)
        scope.cancel()
        web.destroy()
        super.onDestroy()
    }

    // ---------- page ----------

    /** app.html talks to "window.chrome.webview" (the Windows host); this shim provides the same API here. */
    private fun loadHtml(): String {
        val html = assets.open("app.html").bufferedReader().use { it.readText() }
        val shim = """
            <script>
            (function () {
              var ls = [];
              window.chrome = window.chrome || {};
              window.chrome.webview = {
                postMessage: function (m) { AndroidHost.post(JSON.stringify(m)); },
                addEventListener: function (t, f) { ls.push(f); }
              };
              window.__host = function (m) { ls.forEach(function (f) { f({ data: m }); }); };
            })();
            </script>
        """.trimIndent()
        return html.replaceFirst("<head>", "<head>\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n$shim")
    }

    private fun post(msg: JSONObject) {
        if (!ready || isDestroyed) return
        web.evaluateJavascript("window.__host($msg)", null)
    }

    private fun statusJson() = JSONObject().put("state", lamp.link.state.name).put("text", lamp.link.statusText)

    private fun sendInit() {
        val s = lamp.settings
        val log = synchronized(lamp.link.log) { lamp.link.log.toList() }
        post(JSONObject()
            .put("type", "init")
            .put("platform", "android")
            .put("settings", s.toUiJson())
            .put("state", s.state.toJson())
            .put("status", statusJson())
            .put("scenes", JSONArray().also { a -> LampController.SCENES.forEach { a.put(JSONObject().put("name", it.name).put("state", it.state.toJson())) } })
            .put("custom", s.customJson())
            .put("startup", false)
            .put("pinned", false)
            .put("log", JSONArray(log))
            .put("view", startView))
    }

    inner class Bridge {
        @JavascriptInterface
        fun post(json: String) {
            runOnUiThread { try { handle(JSONObject(json)) } catch (e: Exception) { toast("Something went wrong: ${e.message}") } }
        }
    }

    private fun toast(text: String) = post(JSONObject().put("type", "toast").put("text", text))

    private fun handle(m: JSONObject) {
        val c = lamp.controller
        val s = lamp.settings
        when (m.optString("cmd")) {
            "ready" -> { ready = true; sendInit() }
            "close", "exit" -> finish()
            "connect" -> lamp.link.connectInBackground()

            "power" -> c.setOn(m.getBoolean("on"))
            "brightness" -> c.setBrightness(m.getInt("value"))
            "kelvin" -> c.setKelvin(m.getInt("value"))
            "color" -> c.setColor(m.getInt("hue"), m.getInt("sat"))
            "apply" -> c.applyState(LampState.fromJson(m.getJSONObject("state")))

            "saveScene" -> {
                s.customScenes.add(SavedScene(m.optString("name", "My look"), LampState.fromJson(m.optJSONObject("state"))))
                lamp.store.save()
                post(JSONObject().put("type", "custom").put("custom", s.customJson()))
            }
            "deleteScene" -> {
                val i = m.getInt("index")
                if (i in s.customScenes.indices) { s.customScenes.removeAt(i); lamp.store.save() }
                post(JSONObject().put("type", "custom").put("custom", s.customJson()))
            }

            "saveSettings" -> {
                val p = m.getJSONObject("settings")
                s.name = p.optString("name", "Ceiling light")
                s.meshName = p.optString("meshName")
                s.meshPassword = p.optString("meshPassword")
                s.meshId = p.optInt("meshId")
                s.mac = p.optString("mac").trim().uppercase().replace('-', ':')
                s.frontZone = p.optInt("frontZone", 1)
                s.backZone = p.optInt("backZone", 1)
                s.releaseAfterSeconds = p.optInt("releaseAfterSeconds", 30)
                lamp.store.save()
                lamp.link.release("Settings saved, connects when you use it")
                lamp.refreshShortcuts()
                post(JSONObject().put("type", "settingsSaved").put("settings", s.toUiJson()).put("startup", false))
            }

            "blink" -> scope.launch {
                lamp.link.release("Testing…")
                repeat(2) {
                    lamp.link.send("blink", s.meshId, Protocol.off(s.frontZone)); delay(700)
                    lamp.link.send("blink", s.meshId, Protocol.on(s.frontZone)); delay(700)
                }
                c.applyAll()
            }

            "import" -> scope.launch {
                val email = m.optString("email"); val password = m.optString("password")
                val reply = withContext(Dispatchers.IO) {
                    try {
                        val r = CloudImport.fetch(email, password)
                        JSONObject().put("type", "import").put("ok", true).put("meshName", r.meshName).put("meshPassword", r.meshPassword)
                            .put("lights", JSONArray().also { a -> r.lights.forEach { a.put(JSONObject().put("name", it.name).put("meshId", it.meshId).put("mac", it.mac)) } })
                    } catch (e: Exception) {
                        JSONObject().put("type", "import").put("ok", false).put("error", "Import failed: ${e.message}")
                    }
                }
                post(reply)
            }

            "scan" -> scope.launch {
                if (!lamp.link.hasPermissions()) { post(JSONObject().put("type", "scan").put("devices", JSONArray())); return@launch }
                lamp.link.release()
                val found = lamp.link.scanAsync(6000).await()
                // EGLO / AwoX lights use Telink chips: addresses start with A4:C1:38
                val devices = found.map { (mac, name, rssi) -> JSONObject().put("mac", mac).put("name", name).put("rssi", rssi).put("likely", mac.startsWith("A4:C1:38")) }
                    .sortedByDescending { it.getBoolean("likely") }.take(15)
                post(JSONObject().put("type", "scan").put("devices", JSONArray(devices)))
            }
            // "resize", "pin" and "drag" only matter for the Windows window
        }
    }
}
