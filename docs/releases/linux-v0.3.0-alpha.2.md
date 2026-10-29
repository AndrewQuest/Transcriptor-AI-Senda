# Senda 0.3.0-alpha.2 — Linux experimental

- Catálogo multilingüe Whisper Tiny, Base, Small, Medium y Large v3 Turbo, con nombres de OpenAI y descargas verificadas mediante SHA-256.
- Lista de modelos instalados, elección explícita del predeterminado y borrado con confirmación de modelos sin uso. Los modelos borrados pueden descargarse otra vez.
- El selector de descarga es independiente del modelo activo. Guardar otras preferencias no cambia el predeterminado; un archivo ausente se muestra como no disponible.
- Botón **Guardar ajustes** siempre visible al desplazarse por Ajustes.
- Icono de Senda coherente entre Windows y Linux.

La actualización conserva los ajustes, modelos e historial de la alpha anterior. El identificador Flatpak y el formato del historial cifrado no cambian. Se mantiene el protocolo local versión 1; los comandos existentes para Base y Small siguen admitidos.

Inferencia en CPU. Medium y Turbo pueden consumir más memoria y tardar más que los modelos pequeños. La GPU no está habilitada en Linux.

En GNOME y KDE con Wayland utiliza **Copiar** y pega manualmente. La aplicación omite pegado y Enter automáticos cuando no puede verificar el campo de destino. La compatibilidad con micrófonos físicos sigue pendiente de confirmación.

## Actualizar

Sal de Senda e instala el nuevo bundle con el mismo usuario:

```sh
sha256sum -c Senda-Linux-SHA256SUMS.txt
flatpak --user install ./Senda-0.3.0-alpha.2-Linux-x64.flatpak
flatpak run io.github.AndrewQuest.Senda
```

No desinstales con `--delete-data` si quieres conservar tus datos.

## English

Adds the multilingual Whisper Tiny, Base, Small, Medium and Large v3 Turbo catalog, installed model management with a persistent default, deletion confirmation, and a fixed Save settings button. Downloads and the active model are selected separately. The Flatpak application ID, encrypted history format and version 1 worker protocol remain compatible with the previous alpha.

CPU inference only. On GNOME and KDE Wayland, copy the transcript and paste manually. Physical microphone compatibility remains unconfirmed.
