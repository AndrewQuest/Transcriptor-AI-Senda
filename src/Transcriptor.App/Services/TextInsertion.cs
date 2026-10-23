using Transcriptor.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace Transcriptor.App.Services;

public sealed record InsertResult(bool Inserted, string Message);

/// <summary>Uses the standard paste gesture. A correction is allowed only when its UIA range is unchanged.</summary>
public sealed class TextInsertion
{
    private IntPtr _window;
    private AutomationElement? _element;
    private TextPatternRange? _insertedRange;
    private string? _documentAfterPaste;
    private string? _originalText;

    public async Task<InsertResult> InsertAsync(IntPtr target, string text)
    {
        Reset();
        if (target == IntPtr.Zero) return new(false, UiText.Get("Texto listo para copiar. No hay una ventana de destino."));
        // Never activate a different window if the user switched apps during transcription.
        if (NativeMethods.GetForegroundWindow() != target)
            return new(false, UiText.Get("Cambiaste de ventana. El texto está listo para copiar."));
        var result = await PasteAsync(target, text);
        if (!result.Inserted) return result;
        _window = target;
        _originalText = text;
        await Task.Run(() => TryCaptureCorrection(text));
        return result;
    }

    public async Task<InsertResult> CorrectAsync(string corrected)
    {
        if (_element is null || _insertedRange is null || _documentAfterPaste is null)
            return new(false, UiText.Get("Esta aplicación no permite reemplazar con seguridad. Copia la corrección."));
        try
        {
            // Inspect before changing focus or selection. Any edit invalidates the receipt.
            if (!await Task.Run(CanReplace))
                return new(false, UiText.Get("El texto de destino cambió. Copia la corrección para evitar sobrescribirlo."));
            if (!NativeMethods.SetForegroundWindow(_window)) return new(false, UiText.Get("No se pudo activar la ventana original. Copia la corrección."));
            await Task.Delay(100);
            var selected = await Task.Run(() =>
            {
                if (!CanReplace()) return false;
                _element.SetFocus();
                var focused = AutomationElement.FocusedElement;
                if (focused is null || !focused.GetRuntimeId().SequenceEqual(_element.GetRuntimeId())) return false;
                if (!CanReplace()) return false;
                _insertedRange.Select();
                return true;
            });
            if (!selected)
                return new(false, UiText.Get("Cambió el campo de destino. Copia la corrección."));
            var result = await PasteAsync(_window, corrected);
            if (result.Inserted) { _originalText = corrected; await Task.Run(() => TryCaptureCorrection(corrected)); }
            return result;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { Reset(); return new(false, UiText.Get("No se puede reemplazar en esta aplicación. Copia la corrección.")); }
    }

    private bool CanReplace() => _element is not null && _insertedRange is not null &&
        _element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
        ((TextPattern)pattern).DocumentRange.GetText(200001) == _documentAfterPaste &&
        _insertedRange.GetText(-1) == _originalText;

    private void TryCaptureCorrection(string text)
    {
        _element = null; _insertedRange = null; _documentAfterPaste = null;
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword || !element.TryGetCurrentPattern(TextPattern.Pattern, out var p)) return;
            var pattern = (TextPattern)p;
            var selection = pattern.GetSelection();
            if (selection.Length != 1 || selection[0].GetText(-1) != "") return;
            var range = selection[0].Clone();
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -new System.Globalization.StringInfo(text).LengthInTextElements);
            if (range.GetText(-1) != text) return;
            var document = pattern.DocumentRange.GetText(200001);
            if (document.Length > 200000) return;
            _element = element; _insertedRange = range; _documentAfterPaste = document;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException) { }
    }

    public static bool IsPasswordField()
    {
        try { return AutomationElement.FocusedElement?.Current.IsPassword == true; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    private static async Task<InsertResult> PasteAsync(IntPtr target, string text)
    {
        for (var i = 0; i < 20 && NativeMethods.ModifiersDown; i++) await Task.Delay(50);
        if (NativeMethods.ModifiersDown || NativeMethods.GetForegroundWindow() != target)
            return new(false, UiText.Get("Suelta las teclas del atajo. El texto está listo para copiar."));
        if (await Task.Run(IsPasswordField)) return new(false, UiText.Get("No se inserta dictado en campos de contraseña."));
        if (NativeMethods.GetForegroundWindow() != target) return new(false, UiText.Get("Cambiaste de ventana. El texto está listo para copiar."));
        DataObject? previous = null;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is not null)
            {
                previous = new DataObject();
                foreach (var format in data.GetFormats(autoConvert: false))
                {
                    var value = data.GetData(format, autoConvert: false);
                    if (value is not null) previous.SetData(format, value);
                }
            }
            Clipboard.SetText(text);
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            try
            {
                var inputs = new[] { Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true) };
                if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) != inputs.Length)
                {
                    // Release only keys injected by this app if Windows accepted a partial sequence.
                    var release = new[] { Key(0x56, true), Key(0x11, true) };
                    NativeMethods.SendInput((uint)release.Length, release, Marshal.SizeOf<NativeMethods.INPUT>());
                    return new(false, UiText.Get("Windows bloqueó el pegado. Puede ocurrir en aplicaciones ejecutadas como administrador; copia el texto."));
                }
                await Task.Delay(350);
                return new(true, UiText.Get("Texto enviado a la aplicación. Puedes corregirlo desde Senda."));
            }
            finally
            {
                // Do not erase something copied by the user while the paste was in progress.
                if (NativeMethods.GetClipboardSequenceNumber() == sequence)
                {
                    if (previous is null) Clipboard.Clear(); else Clipboard.SetDataObject(previous, copy: true);
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        { return new(false, UiText.Get("El portapapeles está ocupado. El texto se conserva para copiarlo.")); }
    }

    private static NativeMethods.INPUT Key(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KEYBDINPUT { Key = key, Flags = up ? 2u : 0u } }
    };

    public void Reset() { _window = IntPtr.Zero; _element = null; _insertedRange = null; _documentAfterPaste = null; _originalText = null; }
}
