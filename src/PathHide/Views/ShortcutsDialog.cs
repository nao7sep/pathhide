using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PathHide.I18n;

namespace PathHide.Views;

/// <summary>
/// Keyboard-shortcuts help. Renders the <see cref="ShortcutCatalog"/> it is handed — the same source
/// the live window accelerators are built from — grouped into sections laid out in two balanced
/// columns, so the modal can never show a label for a binding that does not exist. Opened from the
/// menu or via Cmd/Ctrl+/. Read-only: it owns no draft state, so the shell's close guard never prompts.
/// </summary>
public sealed class ShortcutsDialog : DialogBase
{
    public ShortcutsDialog(IReadOnlyList<ShortcutItem> shortcuts)
    {
        Width = 820;
        Localized.SetTitle(this, "shortcuts.title");

        var groups = ShortcutCatalog.GroupOrder
            .Select(group => (Group: group, Rows: shortcuts.Where(s => s.Group == group).ToList()))
            .Where(section => section.Rows.Count > 0)
            .ToList();
        var split = BalancedSplit(groups.Select(section => section.Rows.Count).ToList());

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 40 };
        for (var column = 0; column < 2; column++)
        {
            var stack = new StackPanel { Spacing = 24 };
            foreach (var (group, rows) in column == 0 ? groups.Take(split) : groups.Skip(split))
            {
                var section = new StackPanel();
                var header = new TextBlock
                {
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 10),
                }.Themed(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Localized.SetText(header, ShortcutCatalog.GroupHeaderKey(group));
                section.Children.Add(header);
                section.Children.Add(BuildRows(rows));
                stack.Children.Add(section);
            }

            Grid.SetColumn(stack, column);
            columns.Children.Add(stack);
        }

        SetContent(columns);
        var buttons = SetButtons(
        [
            new DialogButton("common.close", "close", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        SetInitialFocus(buttons["close"]);
    }

    /// <summary>
    /// Where the section list divides into two columns, keeping the sections in order: the split that
    /// leaves the taller column with the fewest rows, taking the earlier one on a tie.
    /// </summary>
    internal static int BalancedSplit(IReadOnlyList<int> rowCounts)
    {
        var total = rowCounts.Sum();
        var best = 0;
        var bestTaller = int.MaxValue;
        var left = 0;
        for (var split = 0; split <= rowCounts.Count; split++)
        {
            var taller = Math.Max(left, total - left);
            if (taller < bestTaller)
            {
                best = split;
                bestTaller = taller;
            }

            if (split < rowCounts.Count)
            {
                left += rowCounts[split];
            }
        }

        return best;
    }

    // A reference list carries no card, no zebra and no rule on every row: the section heading and
    // the space between rows do the separating, and the keycap is the one mark on the surface.
    private StackPanel BuildRows(IReadOnlyList<ShortcutItem> rows)
    {
        var stack = new StackPanel { Spacing = 12 };
        foreach (var row in rows)
            stack.Children.Add(BuildRow(row));
        return stack;
    }

    // Description on the left (wrapping), key on the right.
    private Grid BuildRow(ShortcutItem item)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 18,
        };

        var description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        }.Themed(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Localized.SetText(description, item.DescriptionKey);
        Grid.SetColumn(description, 0);
        grid.Children.Add(description);

        Control key = item.ShowAsKeycap ? Keycap(item.Label) : PlainAffordance(item.Label);
        Grid.SetColumn(key, 1);
        grid.Children.Add(key);

        return grid;
    }

    // A keycap: a small rounded border on the surface fill, with SemiBold text.
    private Border Keycap(string label) => new Border
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 3),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
        }.Themed(TextBlock.ForegroundProperty, "TextPrimaryBrush"),
    }
        .Themed(Border.BackgroundProperty, "SurfaceBrush")
        .Themed(Border.BorderBrushProperty, "ControlEdgeBrush");

    // A non-key affordance (drag and drop): plain right-aligned text, no keycap box. Words, not a key
    // legend, so its label is a catalogue key.
    private TextBlock PlainAffordance(string labelKey)
    {
        var text = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }.Themed(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Localized.SetText(text, labelKey);
        return text;
    }
}
