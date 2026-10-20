using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;
using static PathHide.Tests.ViewModels.RecordFormatTests;

namespace PathHide.Tests.Views;

public sealed class RecordsLayoutTests
{
    [Fact]
    public void A_drag_saves_a_whole_pixel_width_inside_the_lists_bounds()
    {
        Assert.Equal(320, RecordsLayout.Intent(100));
        Assert.Equal(451, RecordsLayout.Intent(450.6));
        Assert.Equal(640, RecordsLayout.Intent(900));
    }

    [Fact]
    public void The_list_narrows_to_leave_the_detail_its_minimum_and_never_below_its_own()
    {
        // 1148 inside the margins leaves room for the whole intent.
        Assert.Equal(500, RecordsLayout.DisplayedListWidth(500, 1148));
        // At 800 the detail pane needs 436 with the splitter, so the list gets the rest.
        Assert.Equal(364, RecordsLayout.DisplayedListWidth(500, 800));
        Assert.Equal(320, RecordsLayout.DisplayedListWidth(500, 500));
    }
}

public sealed class RecordsWindowTests : WindowTest
{
    private const string ThisLaunch = "2026-10-04T08:00:00.000Z";

    private readonly FakeRecordsReader _reader = new();
    private readonly FakeDelays _delays = new();
    private readonly FakeJsonStore<AppState> _stateStore = new();

    private MainWindowViewModel Owner(AppState? state = null)
    {
        var settings = new FakeSettingsStore();
        return new MainWindowViewModel(
            new BoundedVisibility(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, _stateStore, state ?? new AppState(), []);
    }

    private (RecordsWindow Window, RecordsViewModel Records) ShowRecords(MainWindowViewModel owner)
    {
        var records = new RecordsViewModel(_reader, ThisLaunch, _delays.Delay);
        var window = Show(new RecordsWindow(records, owner));
        return (window, records);
    }

    private static ColumnDefinition ListColumn(RecordsWindow window) =>
        window.FindControl<Grid>("Shell")!.ColumnDefinitions[0];

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_window_minimum_is_its_two_pane_minimums_the_splitter_and_the_margins()
    {
        var (window, _) = ShowRecords(Owner());
        var shell = window.FindControl<Grid>("Shell")!;
        var columns = shell.ColumnDefinitions;

        Assert.Equal(
            shell.Margin.Left + shell.Margin.Right + columns[0].MinWidth + columns[1].Width.Value + columns[2].MinWidth,
            window.MinWidth);
    }

    [AvaloniaFact]
    public void The_menu_opens_one_records_window_and_opening_it_again_brings_that_one_forward()
    {
        var main = Show(new MainWindow { DataContext = Owner() });
        main.RecordsReader = () => _reader;

        var first = Track(main.OpenRecords());
        Dispatcher.UIThread.RunJobs();
        first.WindowState = WindowState.Minimized;
        var again = main.OpenRecords();

        Assert.Same(first, again);
        Assert.Equal(WindowState.Normal, first.WindowState);
        Assert.True(first.IsVisible);

        first.Close();
        Dispatcher.UIThread.RunJobs();
        var reopened = Track(main.OpenRecords());
        Assert.NotSame(first, reopened);
    }

    [AvaloniaFact]
    public void The_list_opens_at_its_saved_width_and_a_finished_drag_saves_it_once_clamped()
    {
        var owner = Owner(new AppState { RecordsListWidth = 500, WindowPositionX = -1200 });
        var (window, _) = ShowRecords(owner);
        Settle(window);
        Assert.Equal(500, ListColumn(window).Width.Value);

        ListColumn(window).Width = new GridLength(700);
        Settle(window);
        window.FindControl<GridSplitter>("Splitter")!.RaiseEvent(
            new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        WaitFor(() => _stateStore.SaveCount == 1);

        Assert.Equal(640, _stateStore.LastSaved!.RecordsListWidth);
        // A save of the records window's state keeps the main window's.
        Assert.Equal(-1200, _stateStore.LastSaved.WindowPositionX);
        Assert.Equal(640, owner.RecordsListWidth);
    }

    private static void Press(GridSplitter splitter, Key key)
    {
        splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
    }

    [AvaloniaFact]
    public void The_keyboard_steps_the_list_by_16_and_Home_and_End_go_to_its_bounds_each_saved_once()
    {
        var (window, _) = ShowRecords(Owner(new AppState { RecordsListWidth = 500 }));
        Settle(window);
        var splitter = window.FindControl<GridSplitter>("Splitter")!;

        Press(splitter, Avalonia.Input.Key.Right);
        WaitFor(() => _stateStore.SaveCount == 1);
        Assert.Equal(516, _stateStore.LastSaved!.RecordsListWidth);

        Press(splitter, Avalonia.Input.Key.Home);
        WaitFor(() => _stateStore.SaveCount == 2);
        Assert.Equal(RecordsLayout.ListMin, _stateStore.LastSaved!.RecordsListWidth);

        Press(splitter, Avalonia.Input.Key.End);
        WaitFor(() => _stateStore.SaveCount == 3);
        Assert.Equal(RecordsLayout.ListMax, _stateStore.LastSaved!.RecordsListWidth);
        Settle(window);
        Assert.Equal(RecordsLayout.ListMax, ListColumn(window).Width.Value);
    }

    [AvaloniaFact]
    public void A_key_that_moves_nothing_saves_nothing()
    {
        var (window, _) = ShowRecords(Owner(new AppState { RecordsListWidth = RecordsLayout.ListMax }));
        Settle(window);
        var splitter = window.FindControl<GridSplitter>("Splitter")!;

        Press(splitter, Avalonia.Input.Key.End);
        Press(splitter, Avalonia.Input.Key.Right);
        Settle(window);

        Assert.Equal(0, _stateStore.SaveCount);
    }

    [AvaloniaFact]
    public void A_narrower_window_narrows_the_list_and_saves_nothing_and_the_width_returns_with_the_room()
    {
        var (window, _) = ShowRecords(Owner(new AppState { RecordsListWidth = 500 }));
        Settle(window);

        window.Width = RecordsLayout.MinWidth;
        Settle(window);
        Assert.Equal(RecordsLayout.ListMin, ListColumn(window).Width.Value);

        window.Width = RecordsLayout.DefaultWidth;
        Settle(window);
        Assert.Equal(500, ListColumn(window).Width.Value);
        Assert.Equal(0, _stateStore.SaveCount);
    }

    [AvaloniaFact]
    public void Closing_saves_its_own_placement_beside_the_main_windows()
    {
        var (window, _) = ShowRecords(Owner(new AppState { WindowPositionX = -1200, WindowWidth = 1100 }));
        window.Width = 1000;
        window.Height = 640;
        Settle(window);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        var saved = _stateStore.LastSaved!;
        Assert.Equal(1000, saved.RecordsWindowWidth);
        Assert.Equal(640, saved.RecordsWindowHeight);
        Assert.NotNull(saved.RecordsWindowPositionX);
        Assert.False(saved.RecordsWindowMaximized);
        Assert.Equal(-1200, saved.WindowPositionX);
        Assert.Equal(1100, saved.WindowWidth);
    }

    [AvaloniaFact]
    public void A_saved_rectangle_no_screen_shows_leaves_the_designed_size()
    {
        var (window, _) = ShowRecords(Owner(new AppState
        {
            RecordsWindowPositionX = -100_000, RecordsWindowPositionY = -100_000,
            RecordsWindowWidth = 900, RecordsWindowHeight = 500,
        }));

        Assert.Equal(RecordsLayout.DefaultWidth, window.Width);
        Assert.Equal(RecordsLayout.DefaultHeight, window.Height);
        Assert.True(window.MinWidth >= RecordsLayout.MinWidth);
    }

    [AvaloniaFact]
    public void A_stored_record_signals_the_open_window_and_a_closed_one_stops_listening()
    {
        Log.Start(new NullSink(), Path.Combine(Path.GetTempPath(), "pathhide-records-window-tests", Guid.NewGuid().ToString("N")));
        try
        {
            var (window, records) = ShowRecords(Owner());
            _reader.Unanswered.Single().Answer(false);
            Dispatcher.UIThread.RunJobs();

            Log.Info("a record");
            WaitFor(() => _delays.Running(RecordsViewModel.LiveInterval) == 1);

            _delays.Elapse(RecordsViewModel.LiveInterval);
            Dispatcher.UIThread.RunJobs();
            _reader.Unanswered.Single().Answer(false, Record(1));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(records.Rows);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            Log.Info("after closing");
            Thread.Sleep(100);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, _delays.Running(RecordsViewModel.LiveInterval));
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [AvaloniaFact]
    public void The_records_window_follows_a_language_change()
    {
        var (window, records) = ShowRecords(Owner());
        _reader.Unanswered.Single().Answer(false, Record(1, level: "warn"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Records", window.Title);

        using (Localizer.Speaking("ja"))
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("ログ", window.Title);
            Assert.Equal("警告", records.Rows[0].LevelText);
            Assert.Equal("すべてのレベル", records.LevelOptions[0].Label);
        }
    }

    [AvaloniaFact]
    public void A_closed_records_window_is_left_alone_by_a_language_change()
    {
        var (window, records) = ShowRecords(Owner());
        _reader.Unanswered.Single().Answer(false, Record(1));
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        var notified = 0;
        records.PropertyChanged += (_, _) => notified++;

        using (Localizer.Speaking("ja"))
            Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, notified);
    }

    [AvaloniaFact]
    public void Moving_onto_the_last_row_scrolls_to_the_end_and_reads_the_next_page()
    {
        var (window, records) = ShowRecords(Owner());
        _reader.Unanswered.Single().Answer(true, Enumerable.Range(0, RecordsReader.PageSize).Select(i => Record(500 - i)).ToArray());
        Settle(window);
        // A full page overflows the list, so nothing more is read while it is at the top.
        Assert.Empty(_reader.Unanswered);

        var list = window.FindControl<ListBox>("RecordList")!;
        list.Focus();
        list.SelectedIndex = 0;
        Settle(window);
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.End });
        Settle(window);

        Assert.Same(records.Rows[^1], records.SelectedRow);
        Assert.Equal(new RecordCursor(Record(401).Time, 401), Assert.Single(_reader.Unanswered).Query.After);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("The condition did not hold within five seconds.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    [AvaloniaFact]
    public void Hover_keeps_the_records_keyboard_target_and_click_selects_its_own_record()
    {
        var (window, records) = ShowRecords(Owner());
        _reader.Unanswered.Single().Answer(false, Record(3), Record(2), Record(1));
        Settle(window);
        var list = window.FindControl<ListBox>("RecordList")!;
        list.SelectedIndex = 1;
        list.ContainerFromIndex(1)!.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        Settle(window);
        Assert.Same(records.Rows[2], records.SelectedRow);
        var earlier = list.ContainerFromIndex(0)!;
        var point = earlier.TranslatePoint(new Point(earlier.Bounds.Width / 2, earlier.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        Assert.True(earlier.IsPointerOver);
        Assert.Same(records.Rows[2], records.SelectedRow);
        var target = list.ContainerFromIndex(2)!;
        Assert.True(target.IsFocused);
        Assert.NotNull(target.FocusAdorner);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.None, null);
        Settle(window);
        Assert.Same(records.Rows[1], records.SelectedRow);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Same(records.Rows[0], records.SelectedRow);
    }

    // Newer records join above the keyboard target while the user reads; the next arrow moves from the
    // record they were on, not from its old position (composite-control conventions).
    [AvaloniaFact]
    public void Records_arriving_above_the_keyboard_target_leave_the_next_arrow_on_that_record()
    {
        var (window, records) = ShowRecords(Owner());
        _reader.Unanswered.Single().Answer(false, Record(3), Record(2), Record(1));
        Settle(window);
        var list = window.FindControl<ListBox>("RecordList")!;
        list.SelectedIndex = 1;
        list.ContainerFromIndex(1)!.Focus(NavigationMethod.Tab);
        var target = records.SelectedRow!;

        records.OnRecordStored();
        _delays.Elapse(RecordsViewModel.LiveInterval);
        Settle(window);
        _reader.Unanswered.Single().Answer(false, Record(5), Record(4), Record(3), Record(2), Record(1));
        Settle(window);
        Assert.Equal(5, records.Rows.Count);
        Assert.Same(target, records.SelectedRow);
        Assert.True(list.ContainerFromItem(target)!.IsFocused);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        Settle(window);
        Assert.Equal(1, records.SelectedRow!.Id);
    }

    [AvaloniaTheory]
    [MemberData(nameof(PathHide.Tests.I18n.LabelFitTests.Tags), MemberType = typeof(PathHide.Tests.I18n.LabelFitTests))]
    public async Task A_filter_trimmed_when_closed_shows_every_label_whole_once_opened_by_keyboard(string tag)
    {
        // The closed filters may trim their choice with one drawn marker; opening one from the keyboard
        // (or by a tap) is the direct path to the complete labels (text-cleanup conventions, rule 4).
        using var speaking = Localizer.Speaking(tag);
        _reader.Sessions = [ThisLaunch, "2026-10-03T21:45:10.000Z"];
        var (window, records) = ShowRecords(Owner());
        window.Width = RecordsLayout.MinWidth;
        Settle(window);
        await Task.Yield();
        Settle(window);
        Assert.Equal(3, records.LaunchOptions.Count);

        foreach (var combo in window.GetLogicalDescendants().OfType<ComboBox>())
        {
            combo.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
            window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
            Settle(window);
            Assert.True(combo.IsDropDownOpen);

            var labels = combo.GetLogicalDescendants().OfType<ComboBoxItem>()
                .SelectMany(item => item.GetVisualDescendants().OfType<TextBlock>())
                .Where(text => !string.IsNullOrEmpty(text.Text)).ToList();
            Assert.Equal(combo.ItemCount, labels.Count);
            Assert.All(labels, text => Assert.DoesNotContain(text.TextLayout.TextLines, line => line.HasCollapsed));
            // The list grows with its labels only up to the app's popup cap (composite-control conventions).
            var list = Assert.IsType<Border>(combo.GetVisualDescendants().OfType<Popup>().Single().Child);
            Assert.Equal("PopupBorder", list.Name);
            Assert.True(list.Bounds.Width <= (double)Application.Current!.FindResource("AppPopupMaxWidth")!);

            combo.IsDropDownOpen = false;
            Settle(window);
        }
    }

    [AvaloniaFact]
    public void A_long_message_wraps_whole_inside_the_details_scrolling_area_without_growing_the_pane()
    {
        // The message is text to be read: never shortened, and never a header that grows with it
        // (interface-styling conventions, Labels).
        var message = string.Join(' ', Enumerable.Repeat("apply: failed to set attributes on a path", 400));
        _reader.Details[1] = new RecordDetail(1, ThisLaunch, Record(1).Time, "error", message, null, null);
        var (window, records) = ShowRecords(Owner());
        _reader.Unanswered.Single().Answer(false, Record(1, session: ThisLaunch, level: "error"));
        Settle(window);
        records.SelectedRow = records.Rows.Single();
        Settle(window);
        Assert.True(records.HasDetail);

        var body = window.FindControl<ScrollViewer>("DetailBody")!;
        var shown = window.FindControl<SelectableTextBlock>("DetailMessage")!;
        Assert.Same(body, shown.FindAncestorOfType<ScrollViewer>());
        Assert.Equal(message, shown.Text);
        Assert.DoesNotContain(shown.TextLayout.TextLines, line => line.HasCollapsed);
        Assert.True(body.Extent.Height > body.Viewport.Height, "The long message should scroll, not size the pane.");
        Assert.True(body.Bounds.Height <= window.Bounds.Height);
    }

    private sealed class NullSink : ILogSink
    {
        public void Write(LogEntry entry)
        {
        }

        public void Dispose()
        {
        }
    }
}
