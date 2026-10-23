using Transcriptor.Core;

namespace Transcriptor.Tests;

public sealed class DeliveryTests
{
    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public async Task EnterRequiresOptInSuccessfulPasteAndUnchangedTarget(bool enabled, bool pasted, bool current, bool expected)
    {
        var sends = 0; var invalidations = 0;
        var receipt = await TextDeliveryFlow.DeliverAsync(_ => Task.FromResult(new DeliveryReceipt(pasted, "paste")), enabled,
            () => Task.FromResult(current), () => { sends++; return true; }, () => invalidations++, default);
        Assert.Equal(pasted, receipt.PasteDispatched);
        Assert.Equal(expected, receipt.EnterDispatched);
        Assert.Equal(expected ? 1 : 0, sends);
        Assert.Equal(sends, invalidations);
    }

    [Fact]
    public async Task EscapeAfterPasteKeepsPasteReceiptButCancelsEnter()
    {
        using var cancel = new CancellationTokenSource();
        var result = await TextDeliveryFlow.DeliverAsync(_ => { cancel.Cancel(); return Task.FromResult(new DeliveryReceipt(true, "paste")); }, true,
            () => Task.FromResult(true), () => throw new Exception("Enter must not be dispatched"), () => throw new Exception("No Enter attempt"), cancel.Token);
        Assert.True(result.PasteDispatched);
        Assert.False(result.EnterAttempted);
    }

    [Fact]
    public async Task CancellationDuringFocusInspectionStillPreventsEnter()
    {
        using var cancel = new CancellationTokenSource();
        var result = await TextDeliveryFlow.DeliverAsync(_ => Task.FromResult(new DeliveryReceipt(true, "paste")), true,
            () => { cancel.Cancel(); return Task.FromResult(true); }, () => throw new Exception("Cancelled"), () => { }, cancel.Token);
        Assert.False(result.EnterAttempted);
    }

    [Fact]
    public async Task PartialEnterFailureInvalidatesCorrectionAndNeverRetries()
    {
        var sends = 0; var invalidated = false;
        var result = await TextDeliveryFlow.DeliverAsync(_ => Task.FromResult(new DeliveryReceipt(true, "paste")), true,
            () => Task.FromResult(true), () => { Assert.True(invalidated); sends++; return false; }, () => invalidated = true, default);
        Assert.True(result.PasteDispatched);
        Assert.True(result.EnterAttempted);
        Assert.False(result.EnterDispatched);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task CancelledDeliveryDoesNotPaste()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await TextDeliveryFlow.DeliverAsync(_ => throw new Exception("Must not paste"), true,
            () => Task.FromResult(true), () => true, () => { }, cancel.Token);
        Assert.False(result.PasteDispatched);
    }

    [Theory]
    [InlineData(-1920, 0, 0, 1080, 396, 124, 20, -1158, 936)]
    [InlineData(1920, -1080, 3840, 0, 594, 186, 30, 2583, -216)]
    [InlineData(0, 40, 3840, 2080, 792, 248, 40, 1524, 1792)]
    [InlineData(0, 0, 300, 100, 396, 124, 20, 0, 0)]
    public void PositionHandlesNegativeOriginsDpiAndSmallWorkAreas(int left, int top, int right, int bottom, int width, int height, int margin, int x, int y)
    {
        Assert.Equal(new OverlayPosition(x, y), OverlayGeometry.BottomCenter(new(left, top, right, bottom), width, height, margin));
    }
}
