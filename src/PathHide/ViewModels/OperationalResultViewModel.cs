using Avalonia.Automation;

namespace PathHide.ViewModels;

public enum OperationalResultOwner
{
    PathStore,
    Visibility,
    Scan,
}

public sealed record OperationalResultViewModel(
    OperationalResultOwner Owner,
    string Message,
    bool IsError)
{
    public string SeverityLabel => IsError ? "Error" : "Warning";

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

    public string SeverityLabel => Severity.ToString();

    public string AccessibleName => $"{SeverityLabel}: {Message}";

    public AutomationLiveSetting LiveSetting => IsError
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;
}
