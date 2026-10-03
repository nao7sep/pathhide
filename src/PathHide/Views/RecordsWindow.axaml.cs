using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using PathHide.I18n;
using PathHide.Services;
using PathHide.ViewModels;

namespace PathHide.Views;

/// <summary>
/// The records window: <c>records.sqlite3</c>, newest first, beside the selected record whole. A durable
/// secondary window with its own placement (window conventions, Placement), opened from the main
/// window's menu, which keeps it to one. It has no owner and never refuses to close, so it neither
/// keeps the app from quitting nor stands in front of the main window when the app is activated.
/// </summary>
public partial class RecordsWindow : Window
{
    private readonly RecordsViewModel _records;
    private readonly MainWindowViewModel _state;
    private readonly WindowPlacement _placement;
    private ScrollViewer? _listScroll;

    private ColumnDefinition ListColumn => Shell.ColumnDefinitions[0];
    private ColumnDefinition DetailColumn => Shell.ColumnDefinitions[2];

    // The list width the user last dragged to: window conventions, Content-based minimum size.
    private double _listIntent;

    /// <summary>For the XAML previewer only.</summary>
    public RecordsWindow()
    {
        InitializeComponent();
        _records = null!;
        _state = null!;
        _placement = null!;
    }

    /// <param name="records">What the window shows.</param>
    /// <param name="state">The owner of the app's window state, where this window's placement and list width are kept.</param>
    public RecordsWindow(RecordsViewModel records, MainWindowViewModel state)
    {
        InitializeComponent();
        _records = records;
        _state = state;
        DataContext = records;

        if (OperatingSystem.IsWindows())
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://PathHide/Assets/icon-win.png"));
            Icon = new WindowIcon(iconStream);
        }

        // The app's inactive-window treatments (the quieter focus ring) key on this class, as in
        // DialogBase and the main window.
        Activated += (_, _) => Classes.Set("windowInactive", false);
        Deactivated += (_, _) => Classes.Set("windowInactive", true);

        // The designed size and the minimum, then the saved placement over them, and the list at its
        // saved width: all before the first frame.
        Width = RecordsLayout.DefaultWidth;
        Height = RecordsLayout.DefaultHeight;
        MinWidth = RecordsLayout.MinWidth;
        ApplyMinimumHeight();
        _placement = new WindowPlacement(this, "records");
        _placement.Restore(state.RecordsWindowPositionX, state.RecordsWindowPositionY,
            state.RecordsWindowWidth, state.RecordsWindowHeight, state.RecordsWindowMaximized);
        _listIntent = RecordsLayout.Intent(state.RecordsListWidth ?? RecordsLayout.ListDefault);
        ApplyListWidth();

        Shell.SizeChanged += (_, _) => ApplyListWidth();
        // The filters' height follows the UI font, which Settings can change while this is open.
        FiltersBand.SizeChanged += (_, e) => MinHeight = RecordsLayout.MinHeightFor(e.NewSize.Height);
        Splitter.DragCompleted += (_, _) => CommitListWidth();
        Splitter.KeyUp += OnSplitterKeyUp;

        RecordList.TemplateApplied += OnListTemplateApplied;
        records.PageApplied += OnPageApplied;

        // Signals and language changes only while open, so a closed window is never written to.
        Opened += (_, _) =>
        {
            Log.RecordStored += OnRecordStored;
            Localizer.Changed += records.Retranslate;
            records.Start();
        };
        Closed += (_, _) =>
        {
            Log.RecordStored -= OnRecordStored;
            Localizer.Changed -= records.Retranslate;
            records.PageApplied -= OnPageApplied;
            records.Close();
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_placement.ForClose() is { } placement)
        {
            try
            {
                _state.SaveRecordsWindowPlacement(
                    placement.X, placement.Y, placement.Width, placement.Height, placement.Maximized);
            }
            catch (Exception ex)
            {
                // Disposable placement must never block closing or affect user data.
                Log.Warn("window geometry save failed", ex, new { window = "records" });
            }
        }

        base.OnClosing(e);
    }

    // The logger's writer thread calls this; the window answers on the UI thread.
    private void OnRecordStored() => Dispatcher.UIThread.Post(_records.OnRecordStored);

    // Before the first layout, from the filter band's own measure.
    private void ApplyMinimumHeight()
    {
        FiltersBand.Measure(Size.Infinity);
        MinHeight = RecordsLayout.MinHeightFor(FiltersBand.DesiredSize.Height);
    }

    // The shown width is re-derived from the intent on every resize, and the detail column takes the
    // rest again, so a narrow window narrows the list without changing what is saved.
    private void ApplyListWidth()
    {
        var available = Shell.Bounds.Width > 0 ? Shell.Bounds.Width : Width - RecordsLayout.Padding * 2;
        ListColumn.Width = new GridLength(RecordsLayout.DisplayedListWidth(_listIntent, available));
        DetailColumn.Width = GridLength.Star;
    }

    // Only a finished drag, or a keyboard step, saves; a resize never does.
    private void CommitListWidth()
    {
        _listIntent = RecordsLayout.Intent(ListColumn.ActualWidth);
        ApplyListWidth();
        _ = SaveListWidthAsync(_listIntent);
    }

    private async Task SaveListWidthAsync(double width)
    {
        try
        {
            await _state.SaveRecordsListWidthAsync(width);
        }
        catch (Exception ex)
        {
            // The width is disposable state; the store logged the failure's detail.
            Log.Warn("records list width save failed", ex);
        }
    }

    private void OnSplitterKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
            CommitListWidth();
    }

    private void OnListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_listScroll is not null)
            _listScroll.ScrollChanged -= OnListScrollChanged;
        _listScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_listScroll is not null)
            _listScroll.ScrollChanged += OnListScrollChanged;
    }

    private void OnListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_listScroll is { } scroll)
            _records.OnListScrolled(RecordsScroll.AtTop(scroll.Offset.Y), RecordsScroll.NearEnd(scroll.Offset.Y, scroll.Viewport.Height, scroll.Extent.Height));
    }

    // A page that leaves the list short of its end reads the next, judged once the page is laid out.
    private void OnPageApplied() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_listScroll is { } scroll)
                _records.CheckFill(RecordsScroll.NearEnd(scroll.Offset.Y, scroll.Viewport.Height, scroll.Extent.Height));
        }, DispatcherPriority.Loaded);
}

/// <summary>Where the records list is scrolled to, as the window's paging and live updates read it.</summary>
internal static class RecordsScroll
{
    internal static bool AtTop(double offset) => offset < 1;

    /// <summary>Within about one screen of the end of what is loaded.</summary>
    internal static bool NearEnd(double offset, double viewport, double extent) =>
        extent - offset - viewport <= viewport;
}
