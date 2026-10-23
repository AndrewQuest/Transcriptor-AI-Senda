using System.Text.Json;
using Transcriptor.Core;

namespace Transcriptor.Tests;

public sealed class WorkerFrameTests
{
    [Fact]
    public async Task FramingPreservesBinaryAndSeparatesTheNextCommand()
    {
        using var stream = new MemoryStream();
        await WorkerFrames.WriteAsync(stream, new WorkerCommand(1, "a", "transcribe", 4));
        await stream.WriteAsync(new byte[] { 0, 10, 255, 0 });
        await WorkerFrames.WriteAsync(stream, new WorkerCommand(1, "b", "cancel"));
        stream.Position = 0;
        var first = await WorkerFrames.ReadAsync(stream, default);
        Assert.Equal(new byte[] { 0, 10, 255, 0 }, first!.Audio);
        Assert.Equal("b", (await WorkerFrames.ReadAsync(stream, default))!.Command.Id);
        Assert.Null(await WorkerFrames.ReadAsync(stream, default));
    }
    [Theory]
    [InlineData(-1)] [InlineData(3_840_045)]
    public async Task RejectsInvalidAudioLengthBeforeAllocating(int size)
    {
        using var stream = new MemoryStream();
        await WorkerFrames.WriteAsync(stream, new WorkerCommand(1, "x", "transcribe", size)); stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerFrames.ReadAsync(stream, default));
    }
    [Fact]
    public async Task TruncatedAudioCannotBecomeAnotherCommand()
    {
        using var stream = new MemoryStream();
        await WorkerFrames.WriteAsync(stream, new WorkerCommand(1, "x", "transcribe", 100)); stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => WorkerFrames.ReadAsync(stream, default));
    }
    [Fact]
    public async Task RejectsWrongProtocolAndPayloadOnNonAudioCommands()
    {
        foreach (var command in new[] { new WorkerCommand(2, "x", "cancel"), new WorkerCommand(1, "x", "cancel", 50) })
        {
            using var stream = new MemoryStream(); await WorkerFrames.WriteAsync(stream, command); stream.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkerFrames.ReadAsync(stream, default));
        }
    }
}
