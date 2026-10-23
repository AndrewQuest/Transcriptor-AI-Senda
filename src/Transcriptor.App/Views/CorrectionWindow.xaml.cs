using Transcriptor.Core;
using System.Windows;
using Transcriptor.App.Services;

namespace Transcriptor.App.Views;

public partial class CorrectionWindow : Window
{
    private readonly Func<string, Task<InsertResult>> _replace;
    public CorrectionWindow(string text, Func<string, Task<InsertResult>> replace)
    {
        InitializeComponent(); Editor.Text = text; _replace = replace;
        UiText.Changed += RefreshLanguage;
    }
    private void RefreshLanguage() => Feedback.Text = UiText.TranslateDisplayed(Feedback.Text);
    protected override void OnClosed(EventArgs e) { UiText.Changed -= RefreshLanguage; base.OnClosed(e); }
    private void CopyClicked(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(Editor.Text); Feedback.Text = UiText.Get("Corrección copiada."); }
        catch (System.Runtime.InteropServices.ExternalException) { Feedback.Text = UiText.Get("El portapapeles está ocupado. Vuelve a intentarlo."); }
    }
    private async void ReplaceClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Editor.Text)) { Feedback.Text = UiText.Get("Escribe el texto corregido antes de reemplazar."); return; }
        ReplaceButton.IsEnabled = false;
        try
        {
            var result = await _replace(Editor.Text);
            Feedback.Text = result.Message;
            if (result.Inserted) Close();
            else { Show(); Activate(); }
        }
        catch (Exception ex) { Feedback.Text = ex.Message; Show(); Activate(); }
        finally { ReplaceButton.IsEnabled = true; }
    }
}
