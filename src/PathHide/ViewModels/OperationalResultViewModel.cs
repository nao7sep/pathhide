using Avalonia.Automation;

namespace PathHide.ViewModels;

public enum OperationalResultOwner
{
    PathStore,
    Visibility,
    Scan,
    Window,
    LogReveal,
}

public sealed record OperationalResultViewModel(
    OperationalResultOwner Owner,
    string Message,
    bool IsError)
{
    public AutomationLiveSetting LiveSetting => IsError
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;
}

public enum PathAddResultSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record PathAddResultViewModel(
    string Message,
    PathAddResultSeverity Severity)
{
    public bool IsWarning => Severity == PathAddResultSeverity.Warning;

    public bool IsError => Severity == PathAddResultSeverity.Error;

    public string AccessibleName => Message;

    public AutomationLiveSetting LiveSetting => IsError
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;
}
