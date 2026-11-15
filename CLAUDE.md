# Diwoon PixelArt Manager

Lo común a todas las apps está en `..\CLAUDE.md`. Aquí solo lo específico de esta.

## Qué hace y en qué estado está

App personal (no es un encargo) para gestionar una galería de pixel art y mostrarla en pantallas Divoom por
Bluetooth (Pixoo, Pixoo-Max, Timebox Evo, Ditoo) y Wi-Fi (Pixoo-64). Además: monitor de PC en la pantalla,
alertas de eventos del PC, presentación, bandeja del sistema, inicio con Windows y reconexión automática. Basada en https://github.com/iebb/Diwoom (Apache-2.0), con el
diseño de GPixelArt. Versión 1.0.0. Se publicará en GitHub por si le sirve a alguien; no va a la Store.

- Páginas: Gallery (galería, preview LED, envío, presentación), Editor (pixel art 16/32/64), Widgets (now playing,
  temporizador / Pomodoro), Profiles (perfiles por app, como las listas de apps de GCapture), Monitor (sensores, monitor en pantalla, alertas), Device (conexión Bluetooth / Wi-Fi y
  canales), Settings (tema, conversión, inicio, API HTTP).
- Galería: las imágenes guardan la carpeta con la que se añadieron (`LibraryItem.Folder`); la galería muestra esas
  carpetas como tarjetas apiladas y las imágenes sueltas debajo. El editor guarda en la carpeta `Pictures\Diwoon PixelArt`.
  Miniaturas: solo de las tarjetas visibles (`ContainerContentChanging`), 4 a la vez, con caché. Añadir lee 6 a la vez.
- Diagnóstico: `%LocalAppData%\DiwoonPixelArtManager\app.log` (conexiones, ajustes que no se pudieron leer, errores).
- Temperatura de CPU real: Windows no la da sin admin + driver. Sin LibreHardwareMonitor se descarta el ACPI fijo de
  la placa (solo se acepta si cambia) y se muestra la carga. Pendiente de decidir con el usuario: lector con admin
  (PawnIO + tarea programada) para tenerla sin configurar nada.
- Editor: dibujar no se ha probado con ratón real (la prueba automática hizo clic fuera de la ventana).
- Probado de verdad con la Timebox Evo del usuario (Bluetooth): conexión, imagen, GIF, monitor y alertas por `/notify`.
  **Wi-Fi (Pixoo-64) no se ha probado con hardware**: está hecho según la API documentada por Divoom.
- No probado: bloqueo de sesión (pantalla apagada) y alertas de batería (este PC es de escritorio).
- Pixoo-Max (32×32): las animaciones se mandan como primer frame, su formato no está documentado.
- Datos del usuario en `%LocalAppData%\DiwoonPixelArtManager` (`settings.json`, `library.json`).

## Comandos

```powershell
# Build de desarrollo y ejecutar
dotnet build DiwoonPixelArtManager\DiwoonPixelArtManager.csproj -c Debug -p:Platform=x64 -r win-x64
DiwoonPixelArtManager\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\DiwoonPixelArtManager.exe

# Build para publicar (carpeta autocontenida, ~214 MB, no hace falta instalar nada)
dotnet publish DiwoonPixelArtManager\DiwoonPixelArtManager.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish\win-x64
```

- Publicar: `git tag v1.2.0 && git push origin v1.2.0`. `.github/workflows/release.yml` genera el zip portable, el
  instalador .exe (Inno Setup, `installer/DiwoonPixelArtManager.iss`) y el .msix firmado con `signtool`
  (`scripts/New-SigningCertificate.ps1`), y crea la release. Cada push deja los mismos archivos como artifacts.
  Para que el certificado del .msix sea siempre el mismo, poner en los secrets del repo `MSIX_CERTIFICATE` (pfx en
  base64, sujeto `CN=Gabrieli2806`) y `MSIX_CERTIFICATE_PASSWORD`; sin ellos cada build usa uno nuevo.
- La build normal no está empaquetada; `Package.appxmanifest` solo se usa con `-p:WindowsPackageType=MSIX`.
  En el paquete, "iniciar con Windows" usa la StartupTask del manifiesto en vez de la clave Run.
- No hay pruebas automáticas. Sin pantalla, la API HTTP responde 503.
- `curl` desde PowerShell 5: un POST sin cuerpo da 411 (HttpListener exige Content-Length); mandar el texto en el cuerpo.

## Dónde tocar

- `Services/DivoomProtocol.cs`: paquetes Bluetooth (mismo formato de imagen que Diwoom: paleta de 255 colores, 1 byte por píxel).
- `Services/PixelImageService.cs`, `ImageResizer.cs`, `ColorQuantizer.cs`: carga con WIC, composición de GIF, escalado y paleta.
- `Services/Devices/`: `BluetoothTransport` (RFCOMM con `InTheHand.Net.Bluetooth`, trozos de 666 bytes, 40 ms entre
  paquetes) y `WifiTransport` (JSON a `http://IP/post`). `DivoomConnection` elige uno, recuerda el último y reconecta.
- `Services/DisplayPlayer.cs`: qué se muestra (imagen, presentación, monitor, now playing, temporizador) y la vuelta
  tras una alerta. `NowPlayingService` (sesión multimedia de Windows) y `TimerService` alimentan esas pantallas.
- Los singletons con `JsonOptions` deben declarar las opciones antes de `Instance` (inicialización estática en orden).
- `Services/Monitoring/`: sensores sin admin (PDH, D3DKMT para la temperatura de GPU) y LibreHardwareMonitor si está.
- `Services/Rendering/`: fuente 3×5, iconos 7×7 y las pantallas generadas (monitor y alertas).
- `Services/AlertService.cs`, `TrayService.cs` (icono de bandeja y eventos de bloqueo), `StartupService.cs`.
- `Services/Profiles/`: `AppProfileService` mira cada segundo la app en primer plano (y cada 3 s las que corren) y
  pasa el perfil a `DisplayPlayer.SetProfile`; con la ventana de esta app delante no cambia nada. `DesktopApps`
  (ventanas y procesos, adaptado de GCapture), `AppIconService` (icono del .exe vía la shell).
- `Services/Agents/`: lectura (solo lectura) de los transcripts de Claude Code, Codex y Antigravity, portada de
  `C:\src\personal\apps\AI-live-preview\server\sources`. `AgentAlertService`: "te necesita" se queda en pantalla
  (`DisplayPlayer.SetAttention`) mientras dure y pasa de 8 s (Claude marca "esperando" unos segundos mientras revisa
  permisos solo); terminado / error se reproducen una vez. Animaciones en `Services/Rendering/AgentScreens.cs`.
  El log anota por qué una sesión pasa a "esperando".
- `Views/TrayMenuWindow`: menú de la bandeja como ventana WinUI acrílica, mismo diseño que el de GCapture.

## Diferencias con el diseño o stack común

- Sin empaquetar (no MSIX): exe autocontenido para descargar desde GitHub.
- Imágenes con WIC en vez de una librería: ImageSharp 2.x tiene CVEs sin parchear y la 3.x cambió de licencia.
- La interfaz está en inglés.
