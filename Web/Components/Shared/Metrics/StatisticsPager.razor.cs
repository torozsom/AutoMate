using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared.Metrics;

/// <summary>
///     Presents bounded numeric-table paging and supported row-count selection.
/// </summary>
public partial class StatisticsPager
{
    /// <summary>Distinct accessible pagination label.</summary>
    [Parameter]
    public string Label { get; set; } = "Statistics pages";

    /// <summary>Total filtered rows.</summary>
    [Parameter]
    public int Total { get; set; }

    /// <summary>Current one-based page.</summary>
    [Parameter]
    public int Page { get; set; } = 1;

    /// <summary>Current approved page size.</summary>
    [Parameter]
    public int Size { get; set; } = 25;

    /// <summary>Requested page, independent of chart aggregation.</summary>
    [Parameter]
    public EventCallback<int> Change { get; set; }

    /// <summary>Page size callback; parents reset to page one.</summary>
    [Parameter]
    public EventCallback<int> SizeChanged { get; set; }

    /// <summary>Empty tables still have a stable page one.</summary>
    private int Pages => Math.Max(1, (Total + Size - 1) / Size);

    /// <summary>
    ///     Only the finite supported row counts are accepted.
    /// </summary>
    /// <param name="args">Browser event data for this interaction.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task ResizeAsync(ChangeEventArgs args)
    {
        return SizeChanged.InvokeAsync(int.TryParse(args.Value?.ToString(), out var size) && size is 10 or 25 or 50
            ? size
            : 25);
    }
}
