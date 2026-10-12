using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PathHide.I18n;
using PathHide.Storage;

namespace PathHide.ViewModels;

/// <summary>
/// A record's level as the records window shows it: the catalogue's word for a level it knows, and the
/// stored word for one it does not.
/// </summary>
internal static class RecordLevels
{
    internal static string Text(string level) => level switch
    {
        "error" => Localizer.T("records.levelError"),
        "warn" => Localizer.T("records.levelWarn"),
        "info" => Localizer.T("records.levelInfo"),
        "debug" => Localizer.T("records.levelDebug"),
        _ => level,
    };
}

/// <summary>
/// One row of the records list. It holds the stored record and renders its words when shown, so its
/// owner's <see cref="Retranslate"/> brings it into a new language.
/// </summary>
public sealed class RecordRowViewModel(RecordSummary record) : ObservableObject
{
    public RecordSummary Record { get; } = record;

    public long Id => Record.Id;

    public string Message => Record.Message;

    public string TimeText => RecordFormat.ListTime(Record.Time, Localizer.Current.Culture, TimeZoneInfo.Local);

    public string LevelText => RecordLevels.Text(Record.Level);

    public bool IsError => Record.Level == "error";

    public bool IsWarning => Record.Level == "warn";

    internal void Retranslate() => OnPropertyChanged(string.Empty);
}

/// <summary>A choice in one of the records window's filters.</summary>
public abstract class RecordFilterOption : ObservableObject
{
    public abstract string Label { get; }

    internal void Retranslate() => OnPropertyChanged(nameof(Label));
}

/// <summary>A level choice: one level, every warning and error, or all of them (null).</summary>
public sealed class RecordLevelOption(RecordLevelFilter? level) : RecordFilterOption
{
    public RecordLevelFilter? Level { get; } = level;

    public override string Label => Level switch
    {
        null => Localizer.T("records.allLevels"),
        RecordLevelFilter.WarningsAndErrors => Localizer.T("records.levelWarningsAndErrors"),
        RecordLevelFilter.Error => Localizer.T("records.levelError"),
        RecordLevelFilter.Warn => Localizer.T("records.levelWarn"),
        RecordLevelFilter.Info => Localizer.T("records.levelInfo"),
        RecordLevelFilter.Debug => Localizer.T("records.levelDebug"),
        _ => Level.ToString()!,
    };
}

/// <summary>A launch choice: one launch, named by its session, or all of them (null).</summary>
public sealed class RecordLaunchOption(string? session, string currentSession) : RecordFilterOption
{
    public string? Session { get; } = session;

    public override string Label => Session is null
        ? Localizer.T("records.allLaunches")
        : RecordsViewModel.LaunchLabel(Session, currentSession);
}

/// <summary>The selected record, whole, in the words the detail pane shows it in.</summary>
public sealed class RecordDetailViewModel(RecordDetail record, string currentSession) : ObservableObject
{
    public RecordDetail Record { get; } = record;

    public string Message => Record.Message;

    public string LevelText => RecordLevels.Text(Record.Level);

    public bool IsError => Record.Level == "error";

    public bool IsWarning => Record.Level == "warn";

    public string TimeText => RecordFormat.DetailTime(Record.Time, Localizer.Current.Culture, TimeZoneInfo.Local);

    public string LaunchText => RecordsViewModel.LaunchLabel(Record.Session, currentSession);

    public string? FieldsText { get; } = record.Fields is null ? null : RecordFormat.PrettyJson(record.Fields);

    public bool HasFields => FieldsText is not null;

    public string? ErrorText { get; } = record.Error is null ? null : RecordFormat.PrettyJson(record.Error);

    public bool HasError => ErrorText is not null;

    internal void Retranslate() => OnPropertyChanged(string.Empty);
}
