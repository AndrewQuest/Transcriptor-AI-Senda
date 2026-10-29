using System.Diagnostics;
using NAudio.Wave;
using Transcriptor.Core;

namespace Transcriptor.App.Services;

public sealed class MicrophoneCapture(Func<int> deviceIndex, Func<int, IWaveIn>? inputFactory = null,
    TimeSpan? timeout = null) : IAudioCapture
{
    private readonly object _sync = new();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(5);
    private IWaveIn? _input;
    private MemoryStream? _stream;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _ready;
    private TaskCompletionSource<AudioClip>? _stopped;
    private Timer? _watchdog;
    private bool _stopRequested;
    private long _bytes, _lastData, _lastLevel;
    private float _peak;
    public event Action<float>? LevelChanged;
    public event Action<Exception>? Failed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IWaveIn input;
        Task ready;
        lock (_sync)
        {
            if (_input is not null) throw new InvalidOperationException(UiText.Get("El micrófono ya está grabando."));
            _bytes = 0; _peak = 0; _lastLevel = 0; _lastData = Stopwatch.GetTimestamp(); _stopRequested = false;
            _stream = new MemoryStream();
            _writer = new WaveFileWriter(_stream, new WaveFormat(16000, 16, 1));
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ready = _ready.Task;
            try
            {
                input = _input = inputFactory is null ? CreateInput(deviceIndex()) : inputFactory(deviceIndex());
                input.DataAvailable += DataAvailable;
                input.RecordingStopped += RecordingStopped;
                input.StartRecording();
            }
            catch { Cleanup(); throw; }
        }
        try
        {
            // A successful driver call is not proof that the microphone is delivering audio.
            await ready.WaitAsync(_timeout, cancellationToken);
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_input != input) throw new OperationCanceledException(cancellationToken);
                _watchdog = new Timer(_ => CheckForStall(input), null, _timeout, _timeout);
            }
        }
        catch (TimeoutException)
        {
            CancelInput(input);
            throw new IOException(UiText.Get("El micrófono no entregó audio. Revisa el dispositivo y vuelve a dictar."));
        }
        catch { CancelInput(input); throw; }
    }

    private static IWaveIn CreateInput(int index)
    {
        if (WaveIn.DeviceCount == 0)
            throw new InvalidOperationException(UiText.Get("No hay micrófono disponible. Conecta uno y revisa los permisos de Windows."));
        if (index < -1 || index >= WaveIn.DeviceCount)
            throw new InvalidOperationException(UiText.Get("El micrófono seleccionado no está disponible. Elige uno en Ajustes."));
        return new WaveInEvent { DeviceNumber = index, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
    }

    private void DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            // Late callbacks from a cancelled device must never enter the next dictation.
            if (sender != _input || _writer is null || e.BytesRecorded <= 0) return;
            if (_bytes + e.BytesRecorded > 16000L * 2 * 120)
            {
                SignalFailure(new InvalidOperationException(UiText.Get("Se alcanzó el límite de dos minutos de grabación.")));
                return;
            }
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _bytes += e.BytesRecorded;
            _lastData = Stopwatch.GetTimestamp();
            _ready?.TrySetResult();
            float peak = 0;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
            _peak = Math.Max(_peak, peak);
            if (_lastLevel == 0 || Stopwatch.GetElapsedTime(_lastLevel).TotalMilliseconds >= 80)
            {
                _lastLevel = Stopwatch.GetTimestamp();
                LevelChanged?.Invoke(peak);
            }
        }
    }

    private void CheckForStall(IWaveIn input)
    {
        lock (_sync)
        {
            if (_input != input || _stopRequested || Stopwatch.GetElapsedTime(_lastData) < _timeout) return;
            SignalFailure(new IOException(UiText.Get("El micrófono dejó de entregar audio. Revisa el dispositivo y vuelve a dictar.")));
        }
    }

    private void SignalFailure(Exception error)
    {
        if (_stopRequested) return;
        _stopRequested = true;
        _watchdog?.Change(Timeout.Infinite, Timeout.Infinite);
        _ready?.TrySetException(error);
        _stopped?.TrySetException(error);
        // The subscriber schedules cancellation on the UI thread, outside the device callback.
        Failed?.Invoke(error);
    }

    public async Task<AudioClip> StopAsync(CancellationToken cancellationToken)
    {
        Task<AudioClip> task;
        IWaveIn input;
        lock (_sync)
        {
            if (_input is null || _stopped is null) throw new InvalidOperationException(UiText.Get("La grabación ya terminó."));
            task = _stopped.Task; input = _input; _stopRequested = true;
            _watchdog?.Change(Timeout.Infinite, Timeout.Infinite);
            input.StopRecording();
        }
        try { return await task.WaitAsync(_timeout, cancellationToken); }
        catch (TimeoutException)
        {
            CancelInput(input);
            throw new IOException(UiText.Get("El micrófono no terminó de responder. Vuelve a dictar."));
        }
        catch { CancelInput(input); throw; }
    }

    private void RecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (sender != _input) return;
            var stopped = _stopped;
            var error = e.Exception ?? (!_stopRequested
                ? new IOException(UiText.Get("El micrófono se desconectó o dejó de grabar. Revisa el dispositivo.")) : null);
            if (error is not null)
            {
                _ready?.TrySetException(error);
                stopped?.TrySetException(error);
                Cleanup();
                Failed?.Invoke(error);
                return;
            }
            _writer!.Flush();
            var audio = new AudioClip(_stream!.ToArray(), TimeSpan.FromSeconds(_bytes / 32000d), _peak);
            Cleanup();
            stopped?.TrySetResult(audio);
        }
    }

    private void CancelInput(IWaveIn input)
    {
        lock (_sync) { if (_input == input) Cancel(); }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _ready?.TrySetCanceled(); _stopped?.TrySetCanceled();
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _watchdog?.Dispose(); _watchdog = null;
        var input = _input; _input = null;
        if (input is not null)
        {
            input.DataAvailable -= DataAvailable; input.RecordingStopped -= RecordingStopped;
            try { input.Dispose(); }
            catch (NAudio.MmException) { /* A removed device may reject its final reset/close call. */ }
        }
        _writer?.Dispose(); _writer = null;
        _stream?.Dispose(); _stream = null;
        _ready = null; _stopped = null;
    }

    public void Dispose() => Cancel();
}
