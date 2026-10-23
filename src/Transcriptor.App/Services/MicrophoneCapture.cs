using NAudio.Wave;
using Transcriptor.Core;

namespace Transcriptor.App.Services;

public sealed class MicrophoneCapture(Func<int> deviceIndex) : IAudioCapture
{
    private readonly object _sync = new();
    private WaveInEvent? _input;
    private MemoryStream? _stream;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<AudioClip>? _stopped;
    private long _bytes;
    private float _peak;
    private DateTime _lastLevel;
    public event Action<float>? LevelChanged;
    public event Action<Exception>? Failed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_input is not null) throw new InvalidOperationException(UiText.Get("El micrófono ya está grabando."));
            if (WaveIn.DeviceCount == 0) throw new InvalidOperationException(UiText.Get("No hay micrófono disponible. Conecta uno y revisa los permisos de Windows."));
            _bytes = 0; _peak = 0;
            _stream = new MemoryStream();
            _writer = new WaveFileWriter(_stream, new WaveFormat(16000, 16, 1));
            _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _input = new WaveInEvent { DeviceNumber = deviceIndex(), WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            _input.DataAvailable += DataAvailable;
            _input.RecordingStopped += RecordingStopped;
            try { _input.StartRecording(); }
            catch { Cleanup(); throw; }
        }
        return Task.CompletedTask;
    }

    private void DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            if (_writer is null) return;
            // Hard limit bounds RAM and accidental recording. Audio never touches disk.
            if (_bytes + e.BytesRecorded > 16000L * 2 * 120)
            {
                _stopped?.TrySetException(new InvalidOperationException(UiText.Get("El dictado tiene un límite de dos minutos. Divide el texto en fragmentos más cortos.")));
                _input?.StopRecording();
                Failed?.Invoke(new InvalidOperationException(UiText.Get("Se alcanzó el límite de dos minutos de grabación.")));
                return;
            }
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _bytes += e.BytesRecorded;
            float peak = 0;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
            _peak = Math.Max(_peak, peak);
            if ((DateTime.UtcNow - _lastLevel).TotalMilliseconds >= 80)
            {
                _lastLevel = DateTime.UtcNow;
                LevelChanged?.Invoke(peak);
            }
        }
    }

    public async Task<AudioClip> StopAsync(CancellationToken cancellationToken)
    {
        Task<AudioClip> task;
        lock (_sync)
        {
            if (_input is null || _stopped is null) throw new InvalidOperationException(UiText.Get("La grabación ya terminó."));
            task = _stopped.Task;
            _input.StopRecording();
        }
        return await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private void RecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (sender != _input) return;
            var stopped = _stopped;
            if (e.Exception is not null)
            {
                stopped?.TrySetException(e.Exception);
                Cleanup();
                Failed?.Invoke(e.Exception);
                return;
            }
            _writer?.Flush();
            var audio = new AudioClip(_stream?.ToArray() ?? [], TimeSpan.FromSeconds(_bytes / 32000d), _peak);
            Cleanup();
            stopped?.TrySetResult(audio);
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _stopped?.TrySetCanceled();
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (_input is not null)
        {
            _input.DataAvailable -= DataAvailable;
            _input.RecordingStopped -= RecordingStopped;
            _input.Dispose(); _input = null;
        }
        _writer?.Dispose(); _writer = null;
        _stream?.Dispose(); _stream = null;
        _stopped = null;
    }

    public void Dispose() => Cancel();
}
