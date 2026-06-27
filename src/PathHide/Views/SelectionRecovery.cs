using System;
using System.Collections.Generic;

namespace PathHide.Views;

/// <summary>
/// Pure selection-recovery math for the path grid. When a removal clears the selection, the window
/// re-selects the row that slid into the lowest previously-selected slot so the keyboard never
/// dead-ends. Lifted out of the window so the off-by-one-prone clamp is unit-tested without driving a
/// real grid; the window keeps the DataGrid reads (which rows are selected, where they sit) and the
/// focus move.
/// </summary>
public static class SelectionRecovery
{
    /// <summary>
    /// The anchor to recover toward: the lowest of the given row indices (those that were selected),
    /// or 0 when none are valid. Negative indices — a selected item not found in the list — are ignored.
    /// </summary>
    public static int Anchor(IEnumerable<int> selectedRowIndices)
    {
        var lowest = int.MaxValue;
        foreach (var index in selectedRowIndices)
        {
            if (index >= 0 && index < lowest)
                lowest = index;
        }
        return lowest == int.MaxValue ? 0 : lowest;
    }

    /// <summary>
    /// The row index to select after the removal, given the recovery <paramref name="anchor"/> and the
    /// number of rows that remain. Returns -1 when nothing remains (no row to recover to); otherwise the
    /// anchor clamped to the last remaining row, so deleting the final rows lands on the new last row.
    /// </summary>
    public static int TargetIndex(int anchor, int remainingCount) =>
        remainingCount <= 0 ? -1 : Math.Clamp(anchor, 0, remainingCount - 1);
}
