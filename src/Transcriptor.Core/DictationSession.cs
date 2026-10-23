namespace Transcriptor.Core;

/// <summary>Serializes one microphone session, including cancellation during native inference.</summary>
public sealed class DictationSession(IAudioCapture capture, ITranscriptionEngine engine)
{
    private CancellationTokenSource? _operation;
    private int _busy;
    public DictationState State { get; private set; }
    public event Action<DictationState>? StateChanged;
    public event Action<Transcript>? Completed;
    public event Action<string>? Failed;

    public async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _operation = new CancellationTokenSource();
        try
        {
            SetState(DictationState.Recording);
            await capture.StartAsync(_operation.Token);
        }
        catch (OperationCanceledException) { Finish(DictationState.Cancelled); }
        catch (Exception ex) { capture.Cancel(); Fail(ex.Message); }
    }

    public async Task StopAsync(string language)
    {
        if (State != DictationState.Recording || _operation is null) return;
        var cancellation = _operation.Token;
        SetState(DictationState.Transcribing);
        try
        {
            var audio = await capture.StopAsync(cancellation);
            if (audio.Duration < TimeSpan.FromMilliseconds(250) || audio.Peak < 0.003f)
            {
                Failed?.Invoke(UiText.Get("No se detectó voz. Acerca el micrófono e inténtalo otra vez."));
                Finish(DictationState.Idle);
                return;
            }
            var result = await engine.TranscribeAsync(audio, language, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                Failed?.Invoke(UiText.Get("No se reconoció texto. Puedes volver a dictar."));
                Finish(DictationState.Idle);
                return;
            }
            // Keep the session busy until the consumer has accepted the result.
            Completed?.Invoke(result);
            Finish(DictationState.Completed);
        }
        catch (OperationCanceledException) { Finish(DictationState.Cancelled); }
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
        _operation?.Cancel();
        capture.Cancel();
        if (State == DictationState.Recording) Fail(error.Message);
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
