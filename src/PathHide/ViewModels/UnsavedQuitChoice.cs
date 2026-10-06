namespace PathHide.ViewModels;

/// <summary>
/// What the user chose when a quit stopped because a save of theirs did not land
/// (unsaved-edits-conventions, Quitting). Dismissing the question keeps the app open.
/// </summary>
public enum UnsavedQuitChoice
{
    KeepOpen,
    Retry,
    QuitAnyway,
}
