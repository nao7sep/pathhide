using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Models;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The shell's side of the modal-dialog conventions, measured on the real dialogs over a real owner:
/// the bands, the bound, and the body's clearance from its scroll bar.
/// </summary>
public sealed class DialogBoundsTests : IDisposable
{
    private readonly List<Window> _open = [];

    // Shorter than Settings, so the bound is doing the work. The position matters: an owner at the top
    // of the screen hides a centring mistake, because the placement a too-tall dialog would get is
    // clamped to the screen edge and lands on the right answer by accident.
    private Window ShortOwner()
    {
        var owner = new Window { Width = 900, Height = 420, Position = new PixelPoint(120, 120) };
        _open.Add(owner);
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }

    private SettingsDialog OpenSettings(Window owner)
    {
        var dialog = new SettingsDialog("Inter", ThemePreference.System, false, true, (_, _, _) => null);
        _open.Add(dialog);
        _ = dialog.ShowBoundedAsync(owner);
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    private static ScrollViewer Body(Window dialog) =>
        dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "DialogScroll");

    private static Rect At(Visual visual, Visual relativeTo)
    {
        var topLeft = visual.TranslatePoint(new Point(0, 0), relativeTo)!.Value;
        return new Rect(topLeft.X, topLeft.Y, visual.Bounds.Width, visual.Bounds.Height);
    }

    [AvaloniaFact]
    public void A_dialog_opens_at_a_share_of_the_window_that_owns_it()
    {
        var owner = ShortOwner();
        var dialog = OpenSettings(owner);

        Assert.Equal(owner.ClientSize.Height * WindowMetrics.DialogHeightFraction, dialog.Bounds.Height, 0);
    }

    // A bound applied after the window opens rather than before it would leave the dialog above its
    // owner's centre by half the difference.
    [AvaloniaFact]
    public void A_bounded_dialog_is_still_centred_on_the_window_that_owns_it()
    {
        var owner = ShortOwner();
        var dialog = OpenSettings(owner);

        var dialogCentre = dialog.Position.Y + (dialog.Bounds.Height / 2);
        var ownerCentre = owner.Position.Y + (owner.Bounds.Height / 2);
        Assert.True(Math.Abs(dialogCentre - ownerCentre) <= 1,
            $"the dialog's centre is {dialogCentre:F0} and its owner's is {ownerCentre:F0}");
    }

    // A margin on the region rather than on its content leaves the bar short of the band's corners:
    // a gap above it where it should start, and another below.
    [AvaloniaFact]
    public void The_scroll_region_fills_its_band_on_every_side()
    {
        var owner = ShortOwner();
        var dialog = OpenSettings(owner);

        var body = At(Body(dialog), dialog);
        var separatorTop = At(dialog.FindControl<Border>("FooterSeparator")!, dialog).Y;

        Assert.Equal(0, body.X, 0);
        Assert.Equal(0, body.Y, 0);
        Assert.Equal(dialog.Bounds.Width, body.Right, 0);
        Assert.Equal(separatorTop, body.Bottom, 0);
    }

    // The original fault: the bar drew in the same band as the right-hand end of a control. It may
    // take part of the content's inset, but it may not reach the content.
    [AvaloniaFact]
    public void The_scroll_bar_takes_the_content_inset_and_never_the_content()
    {
        var owner = ShortOwner();
        var dialog = OpenSettings(owner);
        var body = Body(dialog);

        Assert.True(body.Extent.Height > body.Viewport.Height, "the body must overflow");
        var bar = body.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Orientation.Vertical && b.Bounds.Width > 0);
        var barLeft = At(bar, dialog).X;
        Assert.Equal(dialog.Bounds.Width, At(bar, dialog).Right, 0);

        var covered = new List<string>();
        foreach (var control in body.GetVisualDescendants().OfType<Control>())
        {
            if (control is not (Button or ComboBox or CheckBox or TextBox or ListBox or RadioButton)) continue;
            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0) continue;
            if (control.FindAncestorOfType<ScrollBar>() is not null) continue;
            var right = At(control, dialog).Right;
            if (right > barLeft)
                covered.Add($"{control.GetType().Name} reaches {right:F0} past the bar at {barLeft:F0}");
        }

        Assert.True(covered.Count == 0, string.Join("; ", covered));
    }

    // The point of bounding the height is that the footer survives it, behind the line that opens it.
    [AvaloniaFact]
    public void The_footer_band_opens_with_a_line_and_stays_on_screen()
    {
        var owner = ShortOwner();
        var dialog = OpenSettings(owner);

        var separator = At(dialog.FindControl<Border>("FooterSeparator")!, dialog);
        var footer = At(dialog.FindControl<StackPanel>("ButtonPanel")!, dialog);

        Assert.True(separator.Height > 0, "the footer opens with no line");
        Assert.Equal(0, separator.X, 0);
        Assert.Equal(dialog.Bounds.Width, separator.Right, 0);
        Assert.True(footer.Bottom <= dialog.Bounds.Height + 0.5, "the footer left the window");
        Assert.Equal(footer.Y - separator.Bottom, dialog.Bounds.Height - footer.Bottom, 0);
    }

    // Nothing here grows with the user's own data, so every dialog stays the size it opens at.
    [AvaloniaFact]
    public void Every_dialog_is_fixed_because_none_of_them_grows()
    {
        var owner = ShortOwner();

        foreach (var dialog in new Window[]
        {
            new SettingsDialog("Inter", ThemePreference.System, false, true, (_, _, _) => null),
            new AboutDialog(_ => true),
            new ShortcutsDialog(ShortcutCatalog.Build(owner)),
        })
        {
            _open.Add(dialog);
            Assert.False(dialog.CanResize, $"{dialog.GetType().Name} is resizable");
        }
    }

    // The startup-failure notice is the application's only window: it has no owner to be bounded by,
    // and no way to be resized back if it opens taller than the display. Its constructor is private,
    // so this factory is the only way the lifetime can build one and the bound cannot be bypassed.
    [AvaloniaFact]
    public void The_startup_failure_notice_is_bounded_by_the_screen()
    {
        var notice = NoticeDialog.CreateStartupFailure("PathHide could not start", "Something went wrong.");
        _open.Add(notice);

        var screen = notice.Screens.Primary!;
        Assert.Equal(
            WindowMetrics.DialogMaxHeight(0, screen.WorkingArea.Height, screen.Scaling),
            notice.MaxHeight);
        Assert.True(double.IsFinite(notice.MaxHeight), "the notice is bounded by nothing");
    }

    public void Dispose()
    {
        for (var i = _open.Count - 1; i >= 0; i--)
            _open[i].Close();
        _open.Clear();
        Dispatcher.UIThread.RunJobs();
    }
}
