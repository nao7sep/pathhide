using PathHide.I18n;

namespace PathHide.ViewModels;

/// <summary>
/// A request for a destructive-action confirmation, raised by the view model and fulfilled
/// by the view's danger-styled confirm dialog. <see cref="ConfirmLabelKey"/> names the specific
/// action shown on the danger button (for example Remove), never a generic Yes or OK.
/// </summary>
public sealed record ConfirmRequest(Message Title, Message Message, string ConfirmLabelKey);
