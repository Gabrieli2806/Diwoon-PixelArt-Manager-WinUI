# Diwoon PixelArt Manager

Windows app to keep your pixel art in one gallery and show it on a Divoom display: Bluetooth models
(Pixoo, Pixoo-Max, Timebox Evo, Ditoo…) and Wi-Fi models (Pixoo-64). It can also turn the display into a
PC monitor and show alerts for PC events.

> Unofficial app, not affiliated with or endorsed by Divoom. It may or may not work with your model;
> reports of tested models and bugs are welcome in the issues.

## Features

- **Gallery**: add images or whole folders (PNG, GIF, JPG, BMP, WebP, TIFF), or drag and drop them. A folder stays a
  folder in the gallery (open it, play it as a slideshow, remove it in one go); adding it again picks up new images.
  Search looks through every folder. The files stay where they are; removing from the gallery deletes nothing.
- **Live preview**: see exactly what the display will show, LED by LED, with animations playing.
- **Send to display**: one click (or double-click a card). Animated GIFs play on 16×16 Bluetooth displays and on
  Wi-Fi displays (long GIFs are reduced to 59 frames, the Pixoo-64 limit).
- **Slideshow**: the selected images (or all of them) one after another, in order or shuffled.
- **Editor**: draw at 16, 32 or 64 px with pencil, eraser, fill and color picker, undo / redo, live on the display,
  and save to the gallery (PNG in `Pictures\Diwoon PixelArt`). "Edit a copy" opens any gallery image in it.
- **Now playing**: cover art and progress of whatever Windows is playing (Spotify, browsers, media players), with the
  title on 32 / 64 LED displays and a scrolling announcement when the song changes.
- **AI agents**: when Claude Code, Codex or Antigravity (Gemini) needs an answer, a pulsing speech bubble in the
  agent's color stays on the display until you reply; a finished turn draws a check mark with sparkles and an error
  shakes a cross. Works with VS Code, the terminals and the desktop apps: the app only reads their conversation logs
  (`~/.claude`, `~/.codex`, `~/.gemini/antigravity`) and never writes to them.
- **App profiles**: while an app is in front (or just running), the display shows what you chose for it: PC
  monitor, now playing, timer, the app's own icon, an image, a gallery folder as a slideshow, the clock, or nothing. Add apps from the ones open
  right now or browse for any `.exe`; when no profile matches, your own choice comes back.
- **Timer and Pomodoro**: countdown on the display with a progress bar, focus / break phases and an alert at the end.
- **PC monitor**: CPU and GPU temperature, CPU / GPU / memory load and the time, drawn for 16, 32 or 64 LEDs and
  refreshed every few seconds. GPU temperature comes from the graphics driver; for the real CPU temperature, run
  [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) with its web server on;
  without it the display shows CPU load instead of a made-up temperature.
- **Alerts**: high CPU / GPU temperature, low battery, charger plugged or unplugged, and screen off while the PC is
  locked. Other programs can show their own alerts through the HTTP API. After an alert the previous content returns.
- **Device controls**: brightness, screen on / off, scoreboard. Bluetooth: clock (6 styles, any color), light
  (5 effects), VJ effects, visualizer, custom and cloud channels. Wi-Fi: channels, clock face, visualizer.
- **Runs in the background**: closing the window leaves it in the notification area. Right-click the icon for a
  menu that shows what the display is doing and toggles the PC monitor, now playing, Pomodoro and app profiles;
  optional start with Windows;
  connects to the last display at start and reconnects by itself when the connection drops.
- **Image conversion settings**: display size (automatic, 16×16, 32×32 or 64×64), pixel or smooth scaling,
  and fit / fill / stretch for non-square images.
- **Local HTTP API** (off by default) so scripts on the same PC can drive the display.
- Light and dark theme, Mica / Acrylic window effects.

## Getting started

1. Bluetooth displays: turn the display on and pair it in **Windows Settings → Bluetooth & devices**.
   Wi-Fi displays: set them up in the Divoom app so they are on your network.
2. Open the app, go to **Device**, pick the display and press **Connect**.
   If it is not listed, press **Scan** (about 10 seconds), or type a Wi-Fi display's IP address and press **Add**.
3. Go to **Gallery**, add images, select one and press **Send to display**.

## HTTP API

Enable it in **Settings → HTTP API**. It listens on `http://127.0.0.1:10119/` (port can be changed) and only
accepts connections from the same computer.

| Request | Body | Effect |
|---|---|---|
| `POST /img` | An image file | Converted with the gallery settings and shown |
| `POST /notify?icon=..&color=RRGGBB` | The text | Shows an alert, then the previous content comes back. Icons: `bell`, `check`, `cross`, `info`, `hot`, `battery`, `charging` |
| `POST /cmd?cmd=NN` | Command arguments | Bluetooth only: wrapped in a Divoom packet and sent |
| `POST /raw` | A complete packet / a JSON command | Bluetooth: sent unchanged. Wi-Fi: posted to the display's API, its answer is returned |

Python example:

```python
import requests
with open("art.png", "rb") as f:
    requests.post("http://127.0.0.1:10119/img", data=f.read())
```

From a terminal:

```
curl --data "Build done" "http://127.0.0.1:10119/notify?icon=check&color=3DDC84"
```

## Requirements

Windows 10 version 1809 or newer, x64, with Bluetooth. Nothing else to install.

## Known limits

- Pixoo-Max (32×32, Bluetooth) shows the first frame of animations: its animation format is not documented.
- Windows does not give the real CPU temperature to programs without administrator rights and a driver. Without
  LibreHardwareMonitor the app ignores the motherboard's fixed ACPI placeholder and shows CPU load.
- Notifications from other apps (Discord, mail…) cannot be read by an app installed this way; use `/notify` from
  your own scripts instead.
- Tested on a Timebox Evo. Other models follow the same protocols but have not been tried yet.

## Building from source

Needs the .NET 10 SDK.

```powershell
dotnet build DiwoonPixelArtManager\DiwoonPixelArtManager.csproj -c Debug -p:Platform=x64 -r win-x64
dotnet publish DiwoonPixelArtManager\DiwoonPixelArtManager.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish\win-x64
```

## Community

Discord: https://discord.gg/DhBbAzugZ9

## Credits

Based on [iebb/Diwoom](https://github.com/iebb/Diwoom) (Apache License 2.0). See `THIRD-PARTY-NOTICES.md`.
