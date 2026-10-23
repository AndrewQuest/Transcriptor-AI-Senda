using System.Diagnostics;
using System.Text;
using Transcriptor.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Transcriptor.Inference;

public sealed class WhisperEngine(Func<AppSettings> settings) : ITranscriptionEngine
{
    private static readonly object RuntimeSync = new();
    private static string? _runtimeAcceleration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedPath;
    private bool _disposed;

    public static bool RequiresRestart(AppSettings config)
    {
        lock (RuntimeSync)
            return RuntimeOptions.LoadedLibrary.HasValue && _runtimeAcceleration != NormalizeAcceleration(config.Acceleration);
    }

    private static string NormalizeAcceleration(string value) => value == "vulkan" ? "vulkan" : "cpu";

    private static void ConfigureRuntime(AppSettings config)
    {
        lock (RuntimeSync)
        {
            var acceleration = NormalizeAcceleration(config.Acceleration);
            // The native runtime remains loaded for the lifetime of this process.
            if (RuntimeOptions.LoadedLibrary.HasValue)
            {
                if (_runtimeAcceleration != acceleration)
                    throw new InvalidOperationException(UiText.Get("Guardaste otro modo de aceleración. Sal de Senda desde la bandeja y vuelve a abrirla para aplicarlo."));
                return;
            }
            RuntimeOptions.RuntimeLibraryOrder = acceleration == "vulkan"
                ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            _runtimeAcceleration = acceleration;
        }
    }

    public async Task<Transcript> TranscribeAsync(AudioClip audio, string language, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _unload?.Change(Timeout.Infinite, Timeout.Infinite);
            var config = settings();
            if (!File.Exists(config.ModelPath)) throw new FileNotFoundException(UiText.Get("Elige o descarga un modelo en Ajustes antes de dictar."));
            return await Task.Run(async () =>
            {
                var watch = Stopwatch.StartNew();
                ConfigureRuntime(config);
                var gpuActive = 0;
                // Observe only the backend notice from our pinned native version. Do not retain logs or text.
                using var backendLog = LogProvider.AddLogger((_, message) =>
                {
                    if (message?.Contains("whisper_backend_init_gpu: using Vulkan", StringComparison.Ordinal) == true)
                        Interlocked.Exchange(ref gpuActive, 1);
                });
                var loadTime = TimeSpan.Zero;
                if (_factory is null || _loadedPath != config.ModelPath)
                {
                    _factory?.Dispose();
                    _factory = null;
                    _loadedPath = null;
                    _factory = WhisperFactory.FromPath(config.ModelPath, new WhisperFactoryOptions
                    {
                        UseGpu = config.Acceleration == "vulkan"
                    });
                    _loadedPath = config.ModelPath;
                    loadTime = watch.Elapsed;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var builder = _factory.CreateBuilder()
                    .WithLanguage(language)
                    .WithThreads(Math.Clamp(config.CpuThreads, 1, Math.Max(1, Environment.ProcessorCount)))
                    .WithNoContext();
                // Whisper accepts examples of transcription style, not LLM instructions.
                // Leave auto-detection unprompted to avoid biasing the detected language.
                if (config.UsePunctuationPrompt && language is "es" or "en")
                    builder.WithPrompt(language == "es"
                        ? "Hola. ¿Cómo estás? Estoy bien, gracias. Podemos revisarlo mañana; hoy ya terminamos."
                        : "Hello. How are you? I'm fine, thanks. We can review it tomorrow; we're finished today.");
                using var processor = builder.Build();
                using var wave = new MemoryStream(audio.WaveData, writable: false);
                var text = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(wave, cancellationToken))
                    text.Append(segment.Text);
                var runtime = RuntimeOptions.LoadedLibrary switch
                {
                    RuntimeLibrary.Vulkan => Volatile.Read(ref gpuActive) == 1 ? "GPU Vulkan" : UiText.Get("CPU (Vulkan sin GPU activa)"),
                    RuntimeLibrary.CpuNoAvx => UiText.Get("CPU sin AVX"),
                    _ => config.Acceleration == "vulkan" ? UiText.Get("CPU (Vulkan no disponible)") : "CPU"
                };
                return new Transcript(text.ToString().Trim(), audio.Duration, watch.Elapsed, "Whisper · " + runtime)
                    { ModelLoadDuration = loadTime };
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
            ScheduleUnload();
        }
    }

    private void ScheduleUnload()
    {
        if (_disposed) return;
        // One shot timer replaces any previous unload, without a polling loop.
        _unload?.Dispose();
        _unload = new Timer(_ => UnloadIfIdle(), null,
            TimeSpan.FromMinutes(Math.Max(1, settings().UnloadAfterMinutes)), Timeout.InfiniteTimeSpan);
    }

    private Timer? _unload;
    private void UnloadIfIdle()
    {
        if (!_gate.Wait(0)) return;
        try { _factory?.Dispose(); _factory = null; _loadedPath = null; }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _unload?.Dispose();
        await _gate.WaitAsync();
        try { _factory?.Dispose(); _factory = null; }
        finally { _gate.Release(); }
    }
}
