using System;
using System.Globalization;
using PathHide.I18n;

namespace PathHide.Tests.I18n;

internal static class TestLanguage
{
    internal static IDisposable Speaking(string tag)
    {
        var preference = Localizer.Preference;
        var system = Localizer.SystemLanguage;
        var formatting = Localizer.Current.Culture;
        void Restore()
        {
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = formatting;
                Localizer.Use(preference, [system]);
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
        try { Localizer.Use(tag, [system]); }
        catch
        {
            try { Restore(); }
            catch { /* Preserve the original handler failure; Use already restored the state. */ }
            throw;
        }
        return new Scope(Restore);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
