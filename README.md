# Ceiling Light: setup guide

A fast Bluetooth remote for your EGLO connect.z ceiling light (white front light + RGB back light).
It talks to the light directly from Chrome on your Android phone. No hub, no EGLO cloud during use,
and it opens instantly because it is cached on the phone.

## 1. Put the app online (once, about 5 minutes)

Chrome only allows Bluetooth on pages served over `https://`, so the folder needs a free host.
The easiest option is GitHub Pages:

1. Create a free account at github.com (skip if you have one).
2. Click **New repository**, name it e.g. `light`, choose **Public**, create it.
3. Click **uploading an existing file** and drag in all files from this folder
   (`index.html`, `protocol.js`, `sw.js`, `manifest.webmanifest`, the three `icon-*.png`). Commit.
4. Go to **Settings → Pages**, under *Branch* choose `main` and `/ (root)`, click **Save**.
5. After a minute your app is at `https://<your-username>.github.io/light/`.

The page contains no personal data. Your light's key is stored only on your phone.

Alternative: drag the folder onto https://app.netlify.com/drop (free account required).

## 2. Install it on your phone

1. Open the address in **Chrome on Android**.
2. Chrome menu (⋮) → **Add to home screen** → **Install**.
3. Start it from the home-screen icon. It now opens like a normal app.

## 3. Connect to your light

1. **Close the EGLO / AwoX app completely.** A light accepts only one Bluetooth connection at a time.
2. In the app, tap the gear icon.
3. **Import from your EGLO account**: enter the email and password you use in the EGLO/AwoX app and
   tap **Import**. This fetches the light's Bluetooth key once. If several lights are listed, tap your ceiling light.
4. Tap **Done**, then **Tap to connect** and pick your light in Chrome's list.
   It usually shows up as "EGLO…", "SML…" or similar. Allow Bluetooth/Location if Android asks.
5. In settings, use **Blink front** and **Blink back** to check the zones. If they are swapped,
   swap the two zone numbers.

From then on the app reconnects by itself when you open it.

### If the import fails
The EGLO server may refuse requests from websites. Then fill in *Mesh name* and *Mesh password*
by hand. You can get them with the script `Get-AwoxMeshCredentials.ps1` from
https://github.com/BastiAKA/awoxble2mqtt-api (Windows PowerShell; uses your EGLO login).
After that, tap **Scan mesh** to find the Mesh ID automatically.

## Good to know
- The app shows the state it last sent. Changes made with the wall switch or remote don't appear in the app.
- Built-in scenes: Bright, Cozy, Reading, Movie, Night. Tap **Save current** to add your own.
- This uses a reverse-engineered protocol. It doesn't change anything on the light, and the EGLO app keeps working.
- After editing any file, raise `VERSION` in `sw.js` so phones pick up the new version.
