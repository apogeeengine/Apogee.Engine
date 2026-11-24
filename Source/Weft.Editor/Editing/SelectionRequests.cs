using System;
using System.Collections.Generic;
using Weft.Reflection;

namespace Weft.Editor.Editing;

/// <summary>
/// One request of ImGui's multi-select, with the item indexes it names (the position in the list of visible nodes): select or deselect everything, or the items from <see cref="First"/> to
/// <see cref="Last"/> (inclusive). <see cref="Direction"/> is negative when <see cref="First"/> is the item that was clicked and <see cref="Last"/> the anchor the range grew from.
/// </summary>
public readonly record struct SelectionRequest(bool All, bool Selected, int First, int Last, int Direction)
{
    public static SelectionRequest SetAll(bool selected) => new(true, selected, 0, 0, 1);

    public static SelectionRequest SetRange(bool selected, int first, int last, int direction) => new(false, selected, first, last, direction);
}

/// <summary>Applies the requests ImGui's multi-select made (clicks, Shift ranges, Ctrl toggles, the box, Escape) to a <see cref="Selection"/>; the panel only translates ImGui's request list into these.</summary>
public static class SelectionRequests
{
    /// <summary>
    /// Applies <paramref name="requests"/> in order. <paramref name="visible"/> is the list of nodes the indexes refer to, in the order the hierarchy shows them. A request that names an index outside it is
    /// ignored (the list changed since ImGui made it). Selecting a range makes the clicked end the active entity.
    /// </summary>
    public static void Apply(Selection selection, IReadOnlyList<Entity> visible, IEnumerable<SelectionRequest> requests)
    {
        foreach (SelectionRequest request in requests)
        {
            if (request.All)
            {
                if (request.Selected)
                {
                    selection.Set(visible, selection.Active);
                }
                else
                {
                    selection.Clear();
                }

                continue;
            }

            int low = Math.Min(request.First, request.Last);
            int high = Math.Max(request.First, request.Last);
            if (low < 0 || high >= visible.Count)
            {
                continue;
            }

            for (int i = low; i <= high; i++)
            {
                if (request.Selected)
                {
                    selection.Add(visible[i]);
                }
                else
                {
                    selection.Remove(visible[i]);
                }
            }

            if (request.Selected)
            {
                selection.SetActive(visible[request.Direction < 0 ? request.First : request.Last]);
            }
        }
    }
}
