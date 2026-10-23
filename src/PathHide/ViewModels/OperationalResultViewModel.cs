using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using PathHide.I18n;

namespace PathHide.ViewModels;

public enum OperationalResultOwner
{
    PathStore,
    Visibility,
    Scan,
    Window,
}

/// <summary>
/// A dismissible problem report under the path list. It holds its <see cref="Message"/>, not the
/// words, so a language change re-renders it where it stands (<see cref="Retranslate"/>).
/// </summary>
public sealed partial class OperationalResultViewModel(
    OperationalResultOwner owner,
    Message message,
    bool isError) : ObservableObject
{
    public OperationalResultOwner Owner { get; } = owner;

    private Message _message = message;
    public Message Message => _message;

    private bool _isError = isError;
    public bool IsError => _isError;

    // Visibility reports outlive unrelated commands. Only their affected paths can resolve them.
    internal Dictionary<string, ApplyProblem> UnresolvedPaths { get; } = new(StringComparer.Ordinal);

    internal void UpdateVisibilitySummary()
    {
        _message = VisibilitySummary(UnresolvedPaths);
        _isError = UnresolvedPaths.Values.Any(problem => problem.IsFailure());
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(LiveSetting));
    }

    internal static Message VisibilitySummary(IReadOnlyDictionary<string, ApplyProblem> problems, int applied = 0)
    {
        var parts = new List<Message>();
        if (applied > 0)
            parts.Add(Message.Of("apply.applied", ("count", applied)));
        foreach (var problem in Enum.GetValues<ApplyProblem>())
        {
            var count = problems.Values.Count(value => value == problem);
            if (count > 0)
                parts.Add(Message.Of(problem.MessageKey(), ("count", count)));
        }
        return Message.Join("apply.join", parts);
    }

    public string Text => Localizer.Of(Message);

    public AutomationLiveSetting LiveSetting => IsError
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;

    /// <summary>Called by the window's view model when the language changes.</summary>
    internal void Retranslate() => OnPropertyChanged(nameof(Text));
}

public enum PathAddResultSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>The result of adding paths, shown in the path list's own strip.</summary>
public sealed partial class PathAddResultViewModel(
    Message message,
    PathAddResultSeverity severity) : ObservableObject
{
    public Message Message { get; } = message;

    public PathAddResultSeverity Severity { get; } = severity;

    public bool IsWarning => Severity == PathAddResultSeverity.Warning;

    public bool IsError => Severity == PathAddResultSeverity.Error;

    public string Text => Localizer.Of(Message);

    public AutomationLiveSetting LiveSetting => IsError
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;

    /// <summary>Called by the window's view model when the language changes.</summary>
    internal void Retranslate() => OnPropertyChanged(nameof(Text));
}
