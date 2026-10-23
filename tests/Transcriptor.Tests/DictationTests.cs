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
        Assert.Equal(0, engine.Calls); Assert.Contains("voz", message); Assert.Equal(DictationState.Idle, session.State);
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

    private sealed class Capture : IAudioCapture
    {
        public int Starts; public AudioClip Clip = new([1, 2], TimeSpan.FromSeconds(2), .5f);
        public event Action<float>? LevelChanged { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
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
