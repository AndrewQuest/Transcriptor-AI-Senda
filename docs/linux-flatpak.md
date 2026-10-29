# Senda para Linux con Flatpak

Versión experimental para Linux x64 con interfaz GTK4/libadwaita, transcripción local con CPU y modelos Whisper Tiny, Base, Small, Medium y Large v3 Turbo multilingües. Incluye interfaz en español e inglés, selección de micrófono, cancelación e historial cifrado cuando el almacén del escritorio está disponible.

El audio permanece en RAM, con un máximo de dos minutos por grabación. La aceleración GPU no está disponible en esta versión.

**En GNOME y KDE con Wayland, utiliza Copiar y pega manualmente.** Las restricciones de Flatpak impiden verificar el campo de otras aplicaciones; esta alpha omite el pegado y Enter automáticos. La compatibilidad con micrófonos físicos aún no está confirmada.

## Instalar

Descarga el bundle `.flatpak` y su archivo de hashes de [la versión Linux](https://github.com/AndrewQuest/Transcriptor-AI-Senda/releases/tag/linux-v0.3.0-alpha.2). Desde la carpeta de descarga:

```sh
sha256sum -c Senda-Linux-SHA256SUMS.txt
flatpak --user remote-add --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak --user install ./Senda-0.3.0-alpha.2-Linux-x64.flatpak
flatpak run io.github.AndrewQuest.Senda
```

No necesitas instalar el SDK de .NET. La primera instalación del runtime y la descarga del modelo requieren Internet; después, la inferencia funciona sin conexión. Flathub proporciona el runtime GNOME. Senda todavía no está publicada en Flathub.

## Primer dictado

1. Abre Ajustes, elige el micrófono y descarga un modelo. Tiny, Base y Small consumen menos recursos; Medium y Turbo pueden tardar más en CPU.
2. Activa el atajo y autorízalo en el diálogo del escritorio.
3. Mantén el atajo para hablar y suéltalo para transcribir.
4. Pulsa **Copiar** y pega el resultado en tu aplicación. Usa **Esc** para cancelar.

## Modelos y ajustes

Elige un modelo instalado y pulsa **Usar como predeterminado**. Esa elección se guarda inmediatamente y se conserva al reiniciar. El catálogo de descarga es independiente: cambiarlo no activa otro modelo. Para borrar el predeterminado, elige primero otro. Los modelos borrados siguen en el catálogo para descargarlos de nuevo.

Pulsa **Guardar ajustes**, siempre visible al pie de Ajustes, para aplicar las demás preferencias. Al actualizar desde la alpha anterior se conservan Base/Small, la configuración y el historial cifrado.

## Permisos y límites

- **Micrófono:** necesario para capturar audio.
- **Atajo global:** requiere autorización de GlobalShortcuts; la combinación y su disponibilidad dependen del escritorio.
- **Entrega de texto:** requiere poder comprobar el campo de destino. Si no es posible, el resultado queda disponible para copiar.
- **Enter automático:** inicialmente apagado. Su advertencia permanece visible. Se omite si no se pueden verificar el foco, los modificadores o la autorización de teclado.
- **Estado:** se muestra mediante notificaciones; su colocación depende del escritorio.
- **Historial:** cifrado, con una clave respaldada por el almacén de secretos del escritorio; hasta 500 entradas y 30 días. Si el almacén no está disponible, no se guarda texto plano.
- **Segundo plano e inicio de sesión:** requieren autorización del escritorio.
- **Red:** necesaria para descargar modelos y dependencias iniciales.

La aplicación no solicita acceso general a tu carpeta personal ni a los dispositivos de entrada del sistema. La integración de atajos y permisos puede variar entre escritorios.

## Actualizar

Cierra Senda e instala el nuevo bundle con `flatpak --user install ./nombre-del-paquete.flatpak`. Los datos se conservan al actualizar la misma aplicación para el mismo usuario.

## Desinstalar

```sh
flatpak --user uninstall io.github.AndrewQuest.Senda
```

Añade `--delete-data` para eliminar también los ajustes, historial y modelos de esa instalación Linux. Los datos de Senda en Windows se guardan por separado.
