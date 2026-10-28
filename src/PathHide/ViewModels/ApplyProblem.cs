namespace PathHide.ViewModels;

internal enum ApplyProblem
{
    Unchanged,
    Missing,
    Error,
    // The elevated helper did not exit within its timeout.
    Unresponsive,
    Cancelled,
    ElevationBusy,
    ElevationDeclined,
}

internal static class ApplyProblemPresentation
{
    internal static bool IsFailure(this ApplyProblem problem) =>
        problem is ApplyProblem.Error or ApplyProblem.Unresponsive;

    internal static string MessageKey(this ApplyProblem problem) => problem switch
    {
        ApplyProblem.Unchanged => "apply.unchanged",
        ApplyProblem.Missing => "apply.missing",
        ApplyProblem.Error => "apply.errors",
        ApplyProblem.Unresponsive => "apply.unresponsive",
        ApplyProblem.Cancelled => "apply.cancelled",
        ApplyProblem.ElevationBusy => "apply.elevationBusy",
        ApplyProblem.ElevationDeclined => "apply.elevationDeclined",
        _ => throw new System.Diagnostics.UnreachableException(),
    };
}
