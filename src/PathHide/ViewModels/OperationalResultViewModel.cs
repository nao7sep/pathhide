using Avalonia.Automation;

namespace PathHide.ViewModels;

public enum OperationalResultOwner
{
    PathStore,
    Visibility,
    Settings,
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
