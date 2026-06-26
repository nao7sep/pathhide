namespace PathHide.ViewModels;

/// <summary>
/// A request for a destructive-action confirmation, raised by the view model and fulfilled
/// by the view's danger-styled confirm dialog. <see cref="ConfirmLabel"/> is the specific
/// action label shown on the danger button (for example <c>Remove</c>), never a generic
/// <c>Yes</c>/<c>OK</c>.
/// </summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmLabel);
