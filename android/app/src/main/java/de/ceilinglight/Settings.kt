package de.ceilinglight

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID

data class LampState(
    var on: Boolean = true,
    var brightness: Int = 80,   // 1..100 %
    var kelvin: Int = 4000,     // front light warmth, 2700..6500
    var hue: Int = 260,         // back light colour, 0..359
    var saturation: Int = 80,   // 0..100 %
) {
    fun toJson(): JSONObject = JSONObject()
        .put("on", on).put("brightness", brightness).put("kelvin", kelvin).put("hue", hue).put("saturation", saturation)

    companion object {
        fun fromJson(o: JSONObject?): LampState {
            if (o == null) return LampState()
            return LampState(
                on = o.optBoolean("on", true),
                brightness = o.optInt("brightness", 80).coerceIn(1, 100),
                kelvin = o.optInt("kelvin", 4000).coerceIn(2700, 6500),
                hue = ((o.optInt("hue", 260) % 360) + 360) % 360,
                saturation = o.optInt("saturation", 80).coerceIn(0, 100),
            )
        }
    }
}

data class SavedScene(val name: String, val state: LampState) {
    fun toJson(): JSONObject = JSONObject().put("name", name).put("state", state.toJson())
}

class Settings {
    var name = "Ceiling light"
    var meshName = ""
    var meshPassword = ""
    var meshId = 0
    /** Bluetooth address of the light, e.g. A4:C1:38:12:34:56. */
    var mac = ""
    /** On EGLO lights with one shared channel, front and back use the same zone. */
    var frontZone = 1
    var backZone = 1
    /** Seconds without use before the light is released for other devices; 0 = never. */
    var releaseAfterSeconds = 30
    var state = LampState()
    val customScenes = mutableListOf<SavedScene>()

    val isConfigured get() = meshName.isNotEmpty() && meshPassword.isNotEmpty() && meshId > 0 && mac.length == 17

    /** What the interface shows in its settings view. */
    fun toUiJson(): JSONObject = JSONObject()
        .put("name", name).put("meshName", meshName).put("meshPassword", meshPassword).put("meshId", meshId).put("mac", mac)
        .put("frontZone", frontZone).put("backZone", backZone).put("releaseAfterSeconds", releaseAfterSeconds)

    fun toJson(): JSONObject = toUiJson()
        .put("state", state.toJson())
        .put("customScenes", JSONArray().also { a -> customScenes.forEach { a.put(it.toJson()) } })

    fun customJson(): JSONArray = JSONArray().also { a -> customScenes.forEach { a.put(it.toJson()) } }

    companion object {
        fun fromJson(o: JSONObject): Settings = Settings().apply {
            name = o.optString("name", name)
            meshName = o.optString("meshName", "")
            meshPassword = o.optString("meshPassword", "")
            meshId = o.optInt("meshId", 0)
            mac = o.optString("mac", "")
            frontZone = o.optInt("frontZone", 1)
            backZone = o.optInt("backZone", 1)
            releaseAfterSeconds = o.optInt("releaseAfterSeconds", 30)
            state = LampState.fromJson(o.optJSONObject("state"))
            val arr = o.optJSONArray("customScenes") ?: JSONArray()
            for (i in 0 until arr.length()) {
                val s = arr.optJSONObject(i) ?: continue
                customScenes.add(SavedScene(s.optString("name", "My look"), LampState.fromJson(s.optJSONObject("state"))))
            }
        }
    }
}

/** Keeps the settings in the app's private storage. */
class SettingsStore(context: Context) {
    private val prefs = context.getSharedPreferences("ceilinglight", Context.MODE_PRIVATE)
    val settings: Settings = try {
        prefs.getString("settings", null)?.let { Settings.fromJson(JSONObject(it)) } ?: Settings()
    } catch (e: Exception) { Settings() }

    @Synchronized
    fun save() { prefs.edit().putString("settings", settings.toJson().toString()).apply() }
}

/** Reads the light's Bluetooth key and address from the EGLO / AwoX account (same as the official app). */
object CloudImport {
    private const val APP_ID = "55O69FLtoxPt67LLwaHGpHmVWndhZGn9Wty8PLrJ"
    private const val CLIENT_KEY = "PyR3yV65rytEicteNlQHSVNpAGvCByOrsLiEqJtI"
    private val BASES = listOf("https://l4hparse-hc-prod.awox.cloud/parse/", "https://l4hparse-prod.awox.cloud/parse/")

    data class Light(val name: String, val meshId: Int, val mac: String)
    data class Result(val meshName: String, val meshPassword: String, val lights: List<Light>)

    /** Blocking; call from a background thread. */
    fun fetch(email: String, password: String): Result {
        val installId = UUID.randomUUID().toString()
        // The server treats emails case-sensitively; accounts are normally stored in lower case.
        val users = listOf(email.trim().lowercase(), email.trim()).distinct()
        var last: Exception? = null
        for (base in BASES) for (user in users) {
            try {
                val login = post(base + "login", installId, null, JSONObject().put("username", user).put("password", password).put("_method", "GET"))
                val session = login.getString("sessionToken")
                val where = JSONObject()
                    .put("where", JSONObject().put("owner", JSONObject().put("__type", "Pointer").put("className", "_User").put("objectId", login.getString("objectId"))))
                    .put("_method", "GET")
                val creds = post(base + "classes/Credential", installId, session, where).getJSONArray("results")
                val devs = post(base + "classes/Device", installId, session, where).getJSONArray("results")

                var cred: JSONObject? = null
                for (i in 0 until creds.length()) if (creds.getJSONObject(i).optString("service") == "zigbee") cred = creds.getJSONObject(i)
                if (cred == null) for (i in 0 until creds.length()) if (creds.getJSONObject(i).optString("service") == "mesh") cred = creds.getJSONObject(i)
                if (cred == null) throw IllegalStateException("No Bluetooth key found in this account.")

                val lights = mutableListOf<Light>()
                for (i in 0 until devs.length()) {
                    val d = devs.getJSONObject(i)
                    if (!d.has("address") || d.isNull("address")) continue
                    var id = d.optString("address").toIntOrNull() ?: 0
                    if (id < 0) id += 65536
                    val mac = d.optString("macAddress", "").uppercase()
                    lights.add(Light(d.optString("displayName", mac).trim(), id, mac))
                }
                return Result(cred.optString("client_id"), cred.optString("access_token"), lights)
            } catch (e: Exception) { last = e }
        }
        throw last ?: IllegalStateException("Import failed.")
    }

    private fun post(url: String, installId: String, session: String?, body: JSONObject): JSONObject {
        val c = URL(url).openConnection() as HttpURLConnection
        try {
            c.requestMethod = "POST"
            c.connectTimeout = 15000; c.readTimeout = 15000
            c.doOutput = true
            c.setRequestProperty("content-type", "application/json")
            c.setRequestProperty("x-parse-application-id", APP_ID)
            c.setRequestProperty("x-parse-client-key", CLIENT_KEY)
            c.setRequestProperty("x-parse-installation-id", installId)
            if (session != null) c.setRequestProperty("x-parse-session-token", session)
            c.outputStream.use { it.write(body.toString().toByteArray()) }
            val ok = c.responseCode in 200..299
            val text = (if (ok) c.inputStream else c.errorStream)?.bufferedReader()?.use { it.readText() } ?: ""
            val json = try { JSONObject(text) } catch (e: Exception) { JSONObject() }
            if (!ok) {
                val msg = json.optString("error", "HTTP ${c.responseCode}")
                throw IllegalStateException(if (msg == "Invalid username/password.") "The EGLO server did not accept this email and password." else msg)
            }
            return json
        } finally { c.disconnect() }
    }
}
