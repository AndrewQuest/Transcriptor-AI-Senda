namespace Transcriptor.Core;

public sealed record DeliveryReceipt(bool PasteDispatched, string Message)
{
    public bool EnterAttempted { get; init; }
    public bool EnterDispatched { get; init; }
}

/// <summary>Platform adapters dispatch input; a receipt never acknowledges an external application's submission.</summary>
public static class TextDeliveryFlow
{
    public static async Task<DeliveryReceipt> DeliverAsync(
        Func<CancellationToken, Task<DeliveryReceipt>> paste, bool pressEnter,
        Func<Task<bool>> targetUnchanged, Func<bool> enter, Action invalidateCorrection,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(false, UiText.Get("Entrega cancelada. El texto está listo para copiar."));
        var receipt = await paste(cancellationToken);
        if (!receipt.PasteDispatched || !pressEnter) return receipt;
        if (cancellationToken.IsCancellationRequested)
            return receipt with { Message = UiText.Get("Pegado despachado; Enter cancelado.") };
        if (!await targetUnchanged())
            return receipt with { Message = UiText.Get("Pegado despachado; se omitió Enter porque cambió el foco o hay teclas pulsadas.") };
        if (cancellationToken.IsCancellationRequested)
            return receipt with { Message = UiText.Get("Pegado despachado; Enter cancelado.") };
        // Clear the correction receipt even if only Enter's key-down is accepted.
        invalidateCorrection();
        var dispatched = enter();
        return receipt with { EnterAttempted = true, EnterDispatched = dispatched,
            Message = UiText.Get(dispatched ? "Pegado y Enter despachados a la aplicación." : "Pegado despachado; no se pudo despachar Enter. No se reintentó.") };
    }
}
