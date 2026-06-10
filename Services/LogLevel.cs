namespace PathHide.Services;

/// <summary>
/// The four — and only four — logging levels defined by the project's logging
/// conventions. <see cref="Debug"/> is developer-only and never reaches an
/// end-user disk; the other three are always written.
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}
