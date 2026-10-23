using System.Windows;
using System.Windows.Markup;
using Transcriptor.Core;

namespace Transcriptor.App.Services;

/// <summary>Refreshes dynamic text resources without replacing views, user text, or theme resources.</summary>
public sealed class LocalizationService : IDisposable
{
    private readonly Application _application;
    private readonly ResourceDictionary _strings = new();

    public LocalizationService(Application application)
    {
        _application = application;
        application.Resources.MergedDictionaries.Add(_strings);
        UiText.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        _application.Dispatcher.VerifyAccess();
        foreach (var source in UiText.Catalog.Keys) _strings[source] = UiText.Get(source);
        foreach (Window window in _application.Windows) window.Language = XmlLanguage.GetLanguage(UiText.Language);
    }

    public void Dispose()
    {
        UiText.Changed -= Refresh;
        _application.Resources.MergedDictionaries.Remove(_strings);
    }
}
