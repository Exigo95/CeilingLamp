# Ceiling Light for Android

Native Android app for the EGLO connect.z ceiling light. Uses the same interface as the Windows app
(`windows/CeilingLamp.Tray/Ui/app.html`, copied in at build time) and talks to the light over Bluetooth directly.

- **App**: lamp preview, on/off, brightness, front warmth, back colour wheel, scenes, your own saved looks, settings.
- **Home-screen widget** "Ceiling light switch": one tap turns the light on with its last look, or off.
- **Quick Settings tile** "Ceiling light": the same, from the pull-down menu.

The app lets go of the light after 30 seconds without use (widget and tile: right after switching),
so the PC app can use it too.

## Install
GitHub builds the app on every push: repository → **Releases** → **Ceiling Light for Android** → `CeilingLight.apk`.
Open that page on the phone, download, install (allow installs from the browser when Android asks).
All builds are signed with the same key (`app/ceilinglight.keystore`), so new versions install over old ones.

## Build yourself
Open the `android` folder in Android Studio and press Run, or run `./gradlew assembleRelease` (JDK 17).
