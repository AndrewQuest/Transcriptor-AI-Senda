using System.IO;
using NAudio.Wave;
using Transcriptor.App.Services;

namespace Transcriptor.App.Tests;

public sealed class MicrophoneCaptureTests
{
    [Fact]
    public async Task WaitsForRealBuffersAndProducesAReadableWaveInMemory()
    {
        var input = new Input();
        using var capture = new MicrophoneCapture(() => -1, _ => input);
        var start = capture.StartAsync(default);
        Assert.False(start.IsCompleted);
        input.Emit(); await start;
        var clip = await capture.StopAsync(default);
        using var reader = new WaveFileReader(new MemoryStream(clip.WaveData));
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(32000, reader.Length); Assert.Equal(1, clip.Duration.TotalSeconds);
        Assert.True(clip.Peak > .1f); Assert.True(input.Disposed);
    }

    [Fact]
    public async Task NoBuffersTimesOutAndTheNextDictationOpensAFreshDevice()
    {
        var failed = new Input(); var next = new Input { EmitOnStart = true };
        var inputs = new Queue<Input>([failed, next]);
        using var capture = new MicrophoneCapture(() => -1, _ => inputs.Dequeue(), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<IOException>(() => capture.StartAsync(default));
        Assert.True(failed.Disposed);
        await capture.StartAsync(default);
        Assert.Equal(1, (await capture.StopAsync(default)).Duration.TotalSeconds);
    }

    [Fact]
    public async Task LateCallbacksFromACancelledDeviceCannotEnterTheNextDictation()
    {
        var previous = new Input(); var next = new Input();
        var inputs = new Queue<Input>([previous, next]);
        using var capture = new MicrophoneCapture(() => -1, _ => inputs.Dequeue());
        var first = capture.StartAsync(default); previous.Emit(); await first;
        capture.Cancel();
        var second = capture.StartAsync(default);
        previous.EmitLate(); Assert.False(second.IsCompleted);
        next.Emit(); await second;
        var clip = await capture.StopAsync(default);
        Assert.Equal(1, clip.Duration.TotalSeconds);
    }

    [Fact]
    public async Task MissingStopAcknowledgementTimesOutWithoutPoisoningTheNextCapture()
    {
        var broken = new Input { EmitOnStart = true, AcknowledgeStop = false };
        var next = new Input { EmitOnStart = true };
        var inputs = new Queue<Input>([broken, next]);
        using var capture = new MicrophoneCapture(() => -1, _ => inputs.Dequeue(), TimeSpan.FromMilliseconds(100));
        await capture.StartAsync(default);
        await Assert.ThrowsAsync<IOException>(() => capture.StopAsync(default));
        Assert.True(broken.Disposed);
        await capture.StartAsync(default); await capture.StopAsync(default);
    }

    [Fact]
    public async Task DriverStoppingUnexpectedlyReportsFailure()
    {
        var input = new Input { EmitOnStart = true };
        using var capture = new MicrophoneCapture(() => -1, _ => input);
        Exception? failure = null; capture.Failed += error => failure = error;
        await capture.StartAsync(default); input.StopRecording();
        Assert.IsType<IOException>(failure); Assert.True(input.Disposed);
    }

    [Fact]
    public async Task BufferStallIsReportedAndDoesNotSilentlyTranscribePartialAudio()
    {
        var input = new Input { EmitOnStart = true };
        using var capture = new MicrophoneCapture(() => -1, _ => input, TimeSpan.FromMilliseconds(100));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Failed += error => failed.TrySetResult(error);
        await capture.StartAsync(default);
        Assert.IsType<IOException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<IOException>(() => capture.StopAsync(default));
    }

    [Fact]
    public async Task CancellationWhileOpeningDisposesTheDevice()
    {
        var input = new Input(); using var cancellation = new CancellationTokenSource();
        using var capture = new MicrophoneCapture(() => -1, _ => input);
        var start = capture.StartAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(input.Disposed);
    }

    private sealed class Input : IWaveIn
    {
        public bool EmitOnStart { get; init; }
        public bool AcknowledgeStop { get; init; } = true;
        public bool Disposed { get; private set; }
        private EventHandler<WaveInEventArgs>? _late;
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { _late = DataAvailable; if (EmitOnStart) Emit(); }
        public void StopRecording() { if (AcknowledgeStop) RecordingStopped?.Invoke(this, new StoppedEventArgs()); }
        private static byte[] Samples()
        {
            var data = new byte[32000];
            for (var i = 0; i < data.Length; i += 2) { data[i] = 0; data[i + 1] = 0x20; }
            return data;
        }
        public void Emit() => DataAvailable?.Invoke(this, new(Samples(), 32000));
        public void EmitLate() => _late?.Invoke(this, new(Samples(), 32000));
        public void Dispose() => Disposed = true;
    }
}
