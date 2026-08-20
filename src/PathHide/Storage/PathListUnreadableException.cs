using System;

namespace PathHide.Storage;

/// <summary>
/// The path list was present but could not be read. It has been set aside with
/// its bytes intact; the app must not continue with an empty list.
/// </summary>
/// <remarks>
/// Its own type so the composition root can tell this halt from the other one —
/// a store that could not even be set aside — and word each honestly. Both stop
/// startup; they stop it for opposite reasons.
/// </remarks>
public sealed class PathListUnreadableException : Exception
{
    public PathListUnreadableException()
        : base("The path list could not be read.")
    {
    }
}
