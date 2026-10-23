# Componentes externos

La aplicación usa paquetes NuGet con versiones fijadas. Sus licencias y avisos originales deben acompañar la distribución según corresponda:

- **Whisper.net / Whisper.net.Runtime / Whisper.net.Runtime.NoAvx / Whisper.net.Runtime.Vulkan 1.9.1**, MIT: https://github.com/sandrohanea/whisper.net
- **whisper.cpp / GGML**, MIT: https://github.com/ggml-org/whisper.cpp (incluido por el paquete de runtime).
- **OpenAI Whisper**, MIT: https://github.com/openai/whisper. El modelo alternativo se descarga por separado desde https://huggingface.co/ggerganov/whisper.cpp.
- **NAudio 2.2.1**, MIT: https://github.com/naudio/NAudio
- **.NET 10**, MIT y avisos de terceros del runtime: https://github.com/dotnet/runtime
- **Interfaz Linux:** los paquetes Rust se fijan en `linux/Cargo.lock`. El Flatpak incluye sus avisos originales y un inventario de nombre, versión y licencia en `/app/share/licenses/senda/rust/`. GTK4 y libadwaita proceden del runtime GNOME. Esta lista no asigna una licencia a Senda: el repositorio aún no contiene una licencia de distribución del código propio.
- **xUnit**, Apache-2.0, y **Microsoft.NET.Test.Sdk**, MIT: herramientas de pruebas, no distribuidas con la aplicación.

Los paquetes y runtimes conservan sus archivos de licencia donde los incluyen. Antes de distribuir una versión pública, reunir los avisos completos de sus dependencias transitivas y revisar la licencia de cualquier modelo adicional. El proyecto no incluye ni redistribuye archivos de Cactus/Whistle y no afirma una licencia para ellos.
