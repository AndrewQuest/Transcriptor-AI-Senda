# Senda 0.3.0-alpha.1 — Linux experimental

Flatpak para Linux x64 con interfaz GTK4/libadwaita y transcripción local con CPU. Incluye modelos Whisper Base/Small multilingües descargados por separado, interfaz en español e inglés, selección de micrófono, audio en RAM, cancelación e historial cifrado cuando el almacén del escritorio está disponible.

## Funciones y límites

- El atajo mantenido requiere autorización del escritorio mediante GlobalShortcuts.
- En GNOME y KDE con Wayland, utiliza **Copiar** y pega manualmente. Las restricciones del sandbox impiden comprobar el campo externo, por lo que el pegado y Enter automáticos no están disponibles en esta alpha.
- El estado del dictado se muestra mediante notificaciones del escritorio.
- El audio permanece en memoria, con un máximo de dos minutos por grabación.
- La compatibilidad con micrófonos físicos aún no está confirmada.
- La aceleración GPU no está disponible en esta versión.

## Instalación

El paquete incluye los componentes necesarios del motor; no necesitas instalar el SDK. La primera instalación del runtime GNOME y la descarga del modelo requieren Internet. Después, la transcripción funciona sin conexión.

```sh
sha256sum -c Senda-Linux-SHA256SUMS.txt
flatpak --user remote-add --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak --user install ./Senda-0.3.0-alpha.1-Linux-x64.flatpak
flatpak run io.github.AndrewQuest.Senda
```

Senda se distribuye desde GitHub; Flathub proporciona el runtime GNOME. Consulta la [guía de instalación y permisos](https://github.com/AndrewQuest/Transcriptor-AI-Senda/blob/main/docs/linux-flatpak.md).

## English

Experimental Linux x64 Flatpak with a native GTK interface and local CPU transcription in Spanish and English. Base/Small models are downloaded separately. Desktop permissions and shortcuts depend on the compositor.

**On GNOME and KDE Wayland, use Copy and paste manually.** Sandbox restrictions prevent target-field verification, so automatic insertion and Enter are unavailable in this alpha. Physical microphone compatibility is not yet confirmed. GPU acceleration is unavailable.

Verify SHA-256 before installing. The package includes the speech engine components; no .NET SDK is required. Internet is needed for the initial GNOME runtime and model downloads. Dictation then works offline.
