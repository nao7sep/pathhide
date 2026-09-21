using Avalonia.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using PathHide.I18n;

namespace PathHide.ViewModels;

public enum OperationalResultOwner
{
    PathStore,
    Visibility,
    Scan,
    Window,
    LogReveal,
}

/// <summary>
/// A dismissible problem report under the path list. It holds its <see cref="Message"/>, not the
/// words, so a language change re-renders it where it stands (<see cref="Retranslate"/>).
/// </summary>
public sealed partial class OperationalResultViewModel(
    OperationalResultOwner owner,
    Message message,
    bool isError) : ObservableObject
{
    public OperationalResultOwner Owner { get; } = owner;

    public Message Message { get; } = message;

    public bool IsError { get; } = isError;

    public string Text => Localizer.Of(Message);

    public AutomationLiveSetting LiveSetting => IsError
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;

    /// <summary>Called by the window's view model when the language changes.</summary>
    internal void Retranslate() => OnPropertyChanged(nameof(Text));
}

public enum PathAddResultSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>The result of adding paths, shown in the path list's own strip.</summary>
public sealed partial class PathAddResultViewModel(
    Message message,
    PathAddResultSeverity severity) : ObservableObject
{
    public Message Message { get; } = message;

    public PathAddResultSeverity Severity { get; } = severity;

    public bool IsWarning => Severity == PathAddResultSeverity.Warning;

    public bool IsError => Severity == PathAddResultSeverity.Error;

    public string Text => Localizer.Of(Message);

    public AutomationLiveSetting LiveSetting => IsError
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;

    /// <summary>Called by the window's view model when the language changes.</summary>
    internal void Retranslate() => OnPropertyChanged(nameof(Text));
}
