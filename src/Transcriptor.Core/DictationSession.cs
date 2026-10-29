namespace Transcriptor.Core;

/// <summary>Serializes one microphone session, including cancellation during native inference.</summary>
public sealed class DictationSession(IAudioCapture capture, ITranscriptionEngine engine)
{
    private CancellationTokenSource? _operation;
    private int _busy;
    private TaskCompletionSource? _starting;
    private long _operationId;
    private Exception? _captureFailure;
    public long OperationId => Interlocked.Read(ref _operationId);
    public DictationState State { get; private set; }
    public event Action<DictationState>? StateChanged;
    public event Action<Transcript>? Completed;
    public event Action<string>? Failed;
    public event Action<AudioClip>? AudioCaptured;

    public async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _operation = new CancellationTokenSource();
        _captureFailure = null;
        Interlocked.Increment(ref _operationId);
        var operation = _operation;
        var starting = _starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            SetState(DictationState.Starting);
            await capture.StartAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            SetState(DictationState.Recording);
        }
        catch (OperationCanceledException) { FinishCancellation(); }
        catch (Exception ex) { capture.Cancel(); Fail(ex.Message); }
        finally { starting.TrySetResult(); }
    }

    public async Task StopAsync(string language)
    {
        var operation = _operation;
        if (State == DictationState.Starting && _starting is not null) await _starting.Task;
        if (operation != _operation) return;
        if (State != DictationState.Recording || _operation is null) return;
        var cancellation = _operation.Token;
        SetState(DictationState.Transcribing);
        try
        {
            var audio = await capture.StopAsync(cancellation);
            AudioCaptured?.Invoke(audio);
            if (audio.Duration < TimeSpan.FromMilliseconds(250))
            {
                Fail(UiText.Get("Dictado demasiado corto. Mantén el atajo mientras hablas."));
                return;
            }
            if (audio.Peak < 0.003f)
            {
                Fail(UiText.Get("No se detectó voz. Acerca el micrófono e inténtalo otra vez."));
                return;
            }
            var result = await engine.TranscribeAsync(audio, language, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                Fail(UiText.Get("No se reconoció texto. Puedes volver a dictar."));
                return;
            }
            // Keep the session busy until the consumer has accepted the result.
            Completed?.Invoke(result);
            Finish(DictationState.Completed);
        }
        catch (OperationCanceledException) { FinishCancellation(); }
        catch (Exception ex) { capture.Cancel(); Fail(ex.Message); }
    }

    public void Cancel()
    {
        _operation?.Cancel();
        capture.Cancel();
        // Native inference may still own the model. StopAsync releases the session when it exits.
        if (State == DictationState.Recording) Finish(DictationState.Cancelled);
    }

    public void CaptureFailed(Exception error)
    {
        if (State is not (DictationState.Starting or DictationState.Recording or DictationState.Transcribing)) return;
        _captureFailure = error;
        _operation?.Cancel();
        capture.Cancel();
        if (State == DictationState.Recording) Fail(error.Message);
    }

    private void FinishCancellation()
    {
        if (_captureFailure is not null) Fail(_captureFailure.Message);
        else Finish(DictationState.Cancelled);
    }

    private void Fail(string message)
    {
        Failed?.Invoke(message);
        Finish(DictationState.Error);
    }

    private void Finish(DictationState state)
    {
        _operation?.Dispose();
        _operation = null;
        SetState(state);
        Interlocked.Exchange(ref _busy, 0);
    }

    private void SetState(DictationState state) { State = state; StateChanged?.Invoke(state); }
}
