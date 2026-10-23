using System.Diagnostics;
using System.Text.Json;
using Transcriptor.Core;
using Transcriptor.Inference;
using Whisper.net.Logger;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("Transcriptor.Smoke --model <ggml.bin> --audio <pcm16-mono-16k.wav> [--language auto|es|en] [--acceleration cpu|vulkan] [--threads N] [--punctuation] [--repeat N] [--diagnostics] [--expect <fragment>]");
    return args.Length == 0 ? 2 : 0;
}
try
{
    string Option(string name, string? fallback = null)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback ?? throw new ArgumentException("Falta " + name);
    }
    var model = Path.GetFullPath(Option("--model"));
    var wave = await File.ReadAllBytesAsync(Option("--audio"));
    using var stream = new MemoryStream(wave);
    using var reader = new BinaryReader(stream);
    if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Se requiere WAV RIFF.");
    reader.ReadUInt32();
    if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Se requiere WAV.");
    var valid = false; var bytes = 0;
    while (stream.Position + 8 <= stream.Length)
    {
        var chunk = new string(reader.ReadChars(4)); var length = reader.ReadUInt32();
        var start = stream.Position;
        if (start + length > stream.Length) throw new InvalidDataException("WAV truncado.");
        if (chunk == "fmt " && length >= 16)
        {
            var format = reader.ReadUInt16(); var channels = reader.ReadUInt16(); var rate = reader.ReadUInt32();
            reader.ReadUInt32(); reader.ReadUInt16(); var bits = reader.ReadUInt16();
            valid = format == 1 && channels == 1 && rate == 16000 && bits == 16;
        }
        if (chunk == "data") bytes = checked((int)length);
        stream.Position = start + length + (length % 2);
    }
    if (!valid || bytes == 0) throw new InvalidDataException("Se requiere PCM 16-bit, mono, 16000 Hz con audio.");
    var acceleration = Option("--acceleration", "cpu");
    if (acceleration is not ("cpu" or "vulkan")) throw new ArgumentException("Aceleración válida: cpu o vulkan.");
    var settings = new AppSettings { ModelPath = model, Acceleration = acceleration,
        CpuThreads = Math.Clamp(int.Parse(Option("--threads", new AppSettings().CpuThreads.ToString())), 1, Environment.ProcessorCount),
        UsePunctuationPrompt = args.Contains("--punctuation") };
    using var logger = args.Contains("--diagnostics") ? LogProvider.AddConsoleLogging(WhisperLogLevel.Debug) : null;
    await using var engine = new WhisperEngine(() => settings);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var repeats = Math.Clamp(int.Parse(Option("--repeat", "1")), 1, 20);
    var expected = Option("--expect", "");
    var passed = true;
    for (var iteration = 1; iteration <= repeats; iteration++)
    {
        var result = await engine.TranscribeAsync(new(wave, TimeSpan.FromSeconds(bytes / 32000d), 1), Option("--language", "auto"), cancellation.Token);
        Console.WriteLine(result.Text);
        Console.WriteLine(JsonSerializer.Serialize(new { audioSeconds = result.AudioDuration.TotalSeconds,
            processingSeconds = result.ProcessingDuration.TotalSeconds, peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            runtime = Environment.Version.ToString(), threads = settings.CpuThreads, engine = result.Engine,
            modelLoadSeconds = result.ModelLoadDuration.TotalSeconds, inferenceSeconds = (result.ProcessingDuration - result.ModelLoadDuration).TotalSeconds,
            iteration, punctuation = settings.UsePunctuationPrompt }));
        passed &= !string.IsNullOrWhiteSpace(result.Text) && result.Text.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
    return passed ? 0 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelado."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
