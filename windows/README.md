# Ceiling Light for Windows

A small tray app that controls the EGLO connect.z ceiling light over Bluetooth. Works with any browser
(including Firefox), because it doesn't use the browser at all.

## Install
The control panel uses the Microsoft Edge WebView2 Runtime, which ships with Windows 11 and current Windows 10.

1. Open the repository on GitHub → **Releases** → **Ceiling Light for Windows** → download `CeilingLamp.exe`.
2. Put it somewhere permanent, e.g. `C:\Users\<you>\Apps\CeilingLamp.exe`, and run it.
   Windows SmartScreen may warn because the file isn't signed: click **More info → Run anyway**.
3. The settings window opens on first start: **Import** with your EGLO account (or copy the values
   from the website's settings), make sure the Bluetooth address is filled in, tick **Start with Windows**, **Save**.

## Use
- **Left click** the tray icon: control panel with the lamp preview, on/off, brightness, front warmth,
  a colour wheel for the back light, scenes and your own saved looks. The pin keeps it open; drag the top bar to move it.
- **Middle click**: turn the light on/off with its last settings.
- **Right click**: menu with scenes, settings, start with Windows, exit.

The app lets go of the light after 30 seconds without use (configurable), so the phone can use it too.
Settings are stored in `%APPDATA%\CeilingLamp\settings.json`.

## Build yourself
`dotnet publish windows/CeilingLamp.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
(needs the .NET 8 SDK on Windows). GitHub builds it automatically on every push that touches `windows/`.
