using PathHide.Models;

namespace PathHide.Services;

public sealed record PathInspection(
    ActualState ActualState,
    ItemKind ItemKind);
