using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace PathHide.Controls;

/// <summary>Projects the native current item onto its row without changing grid navigation.</summary>
public sealed class PathDataGrid : DataGrid
{
    protected override Type StyleKeyOverride => typeof(DataGrid);

    public PathDataGrid()
    {
        CurrentCellChanged += (_, _) =>
        {
            foreach (var row in this.GetVisualDescendants().OfType<DataGridRow>())
                MarkCurrentRow(row);
        };
        LoadingRow += (_, e) => MarkCurrentRow(e.Row);
    }

    // CurrentItem is native, protected state. Selection can contain several different rows, and
    // the current item need not be selected. Do not infer it from selection.
    private void MarkCurrentRow(DataGridRow row) =>
        row.Classes.Set("keyboardTarget", CurrentItem is not null && ReferenceEquals(row.DataContext, CurrentItem));
}
