# Instalar y actualizar Senda

## Windows

Descarga el instalador EXE o el ZIP portable desde [Releases](https://github.com/AndrewQuest/Transcriptor-AI-Senda/releases). Elige el paquete de Windows; la numeración es independiente de la versión Linux.

### Instalador recomendado

1. Ejecuta el EXE con tu usuario habitual.
2. Conserva la ubicación de instalación propuesta.
3. Abre Senda desde el menú Inicio.
4. En Ajustes, descarga Base o Small y espera a que esté preparado.

Requiere Windows 11 x64 y un micrófono. No necesitas instalar el SDK de .NET. Los ejecutables todavía no tienen firma de editor; comprueba el origen de la descarga y su SHA-256.

### Actualizar

Senda no comprueba ni instala actualizaciones automáticamente.

1. Sal desde el icono de Senda junto al reloj. Cerrar la ventana principal la deja en segundo plano.
2. Descarga el nuevo instalador de Windows.
3. Ejecútalo con el mismo usuario y conserva la misma ubicación.
4. Abre Senda desde Inicio y comprueba la versión en la esquina inferior izquierda.

El instalador permite actualizar la instalación existente sin desinstalar previamente. Los archivos de la aplicación se guardan por defecto en `%LOCALAPPDATA%\Programs\Senda`. Los modelos, ajustes e historial se guardan por separado en `%LOCALAPPDATA%\Senda`.

### ZIP portable

Extrae todo el ZIP en una carpeta y ejecuta `Senda.exe`. Conserva las bibliotecas y carpetas incluidas.

Para actualizar una copia portable, sal desde la bandeja y extrae la nueva versión en otra carpeta. Actualiza tus accesos directos. Las copias portables y la instalación del menú Inicio se actualizan por separado.

### Desinstalar

Utiliza Aplicaciones instaladas de Windows. La desinstalación conserva modelos, ajustes e historial. Borrar manualmente `%LOCALAPPDATA%\Senda` elimina esos datos.

## Linux

La versión experimental se distribuye como Flatpak. Consulta la [guía de instalación y permisos](linux-flatpak.md).

## Ayuda

Consulta el [README](../README.md) para configurar el modelo, dictar y resolver problemas frecuentes.
