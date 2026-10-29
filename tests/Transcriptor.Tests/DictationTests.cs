using Transcriptor.Core;

namespace Transcriptor.Tests;

public sealed class DictationTests
{
    [Fact]
    public async Task RecordsTranscribesAndEmitsOnlyOneResult()
    {
        var capture = new Capture(); var engine = new Engine();
        var session = new DictationSession(capture, engine);
        var results = new List<Transcript>(); session.Completed += results.Add;
        await session.StartAsync(); await session.StartAsync();
        await session.StopAsync("es"); await session.StopAsync("es");
        Assert.Equal(1, capture.Starts); Assert.Equal(1, engine.Calls);
        Assert.Equal("es", engine.Language); Assert.Single(results);
        Assert.Equal(DictationState.Completed, session.State);
    }

    [Fact]
    public async Task SilenceDoesNotInvokeModelOrEmitText()
    {
        var capture = new Capture { Clip = new([], TimeSpan.FromSeconds(2), 0) };
        var engine = new Engine(); var session = new DictationSession(capture, engine);
        string? message = null; session.Failed += text => message = text;
        await session.StartAsync(); await session.StopAsync("auto");
        Assert.Equal(0, engine.Calls); Assert.Contains("voz", message); Assert.Equal(DictationState.Error, session.State);
    }

    [Fact]
    public async Task CancellingRecordingAllowsNextDictation()
    {
        var capture = new Capture(); var session = new DictationSession(capture, new Engine());
        await session.StartAsync(); session.Cancel(); await session.StopAsync("auto");
        Assert.Equal(DictationState.Cancelled, session.State);
        await session.StartAsync(); Assert.Equal(2, capture.Starts); Assert.Equal(DictationState.Recording, session.State);
    }

    [Fact]
    public async Task CancellationDuringInferenceSuppressesLateResultAndBlocksOverlappingRecording()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new Capture(); var engine = new Engine { Wait = release.Task };
        var session = new DictationSession(capture, engine);
        var emitted = false; session.Completed += _ => emitted = true;
        await session.StartAsync(); var stop = session.StopAsync("auto");
        session.Cancel(); await session.StartAsync(); Assert.Equal(1, capture.Starts);
        release.SetResult(); await stop;
        Assert.False(emitted); Assert.Equal(DictationState.Cancelled, session.State);
        await session.StartAsync(); Assert.Equal(2, capture.Starts);
    }

    [Fact]
    public async Task ModelFailureAllowsRetryAndPreservesError()
    {
        var capture = new Capture(); var session = new DictationSession(capture, new Engine { Throw = true });
        string? error = null; session.Failed += message => error = message;
        await session.StartAsync(); await session.StopAsync("es");
        Assert.Equal(DictationState.Error, session.State); Assert.Equal("invalid model", error);
        await session.StartAsync(); Assert.Equal(2, capture.Starts);
    }

    [Fact]
    public async Task DeviceFailureEndsRecordingWithoutCallingModel()
    {
        var capture = new Capture(); var engine = new Engine(); var session = new DictationSession(capture, engine);
        await session.StartAsync(); session.CaptureFailed(new IOException("device removed"));
        await session.StopAsync("es");
        Assert.Equal(DictationState.Error, session.State); Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task ReleaseWhileOpeningWaitsForCaptureAndProducesOneResult()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new Capture { StartWait = ready.Task };
        var engine = new Engine(); var session = new DictationSession(capture, engine);
        var start = session.StartAsync();
        Assert.Equal(DictationState.Starting, session.State);
        var stop = session.StopAsync("es");
        Assert.False(stop.IsCompleted);
        ready.SetResult();
        await Task.WhenAll(start, stop);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(DictationState.Completed, session.State);
    }

    [Fact]
    public async Task CancelDuringOpeningSuppressesLateStartAndAllowsRetry()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new Capture { StartWait = ready.Task };
        var session = new DictationSession(capture, new Engine());
        var start = session.StartAsync(); var stop = session.StopAsync("es");
        session.Cancel(); await Task.WhenAll(start, stop);
        Assert.Equal(DictationState.Cancelled, session.State);
        capture.StartWait = null;
        await session.StartAsync();
        Assert.Equal(DictationState.Recording, session.State);
    }

    [Fact]
    public async Task DeviceErrorDuringOpeningIsReportedInsteadOfSilentCancellation()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new DictationSession(new Capture { StartWait = ready.Task }, new Engine());
        string? failure = null; session.Failed += error => failure = error;
        var start = session.StartAsync(); session.CaptureFailed(new IOException("device unavailable"));
        await start;
        Assert.Equal("device unavailable", failure);
        Assert.Equal(DictationState.Error, session.State);
    }

    [Fact]
    public async Task ShortCaptureKeepsAnExplicitErrorAndAllowsRetry()
    {
        var engine = new Engine();
        var capture = new Capture { Clip = new([1, 2], TimeSpan.FromMilliseconds(40), .5f) };
        var session = new DictationSession(capture, engine);
        await session.StartAsync(); await session.StopAsync("es");
        Assert.Equal(DictationState.Error, session.State);
        Assert.Equal(0, engine.Calls);
        await session.StartAsync(); Assert.Equal(2, capture.Starts);
    }

    private sealed class Capture : IAudioCapture
    {
        public int Starts; public AudioClip Clip = new([1, 2], TimeSpan.FromSeconds(2), .5f); public Task? StartWait;
        public event Action<float>? LevelChanged { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public async Task StartAsync(CancellationToken cancellationToken) { Starts++; if (StartWait is not null) await StartWait.WaitAsync(cancellationToken); }
        public Task<AudioClip> StopAsync(CancellationToken cancellationToken) => Task.FromResult(Clip);
        public void Cancel() { } public void Dispose() { }
    }
    private sealed class Engine : ITranscriptionEngine
    {
        public int Calls; public string? Language; public bool Throw; public Task? Wait;
        public async Task<Transcript> TranscribeAsync(AudioClip audio, string language, CancellationToken cancellationToken)
        {
            Calls++; Language = language;
            if (Wait is not null) await Wait; // Simulate native code completing after cancellation.
            if (Throw) throw new IOException("invalid model");
            return new("Hola mundo", audio.Duration, TimeSpan.FromMilliseconds(50), "test");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
