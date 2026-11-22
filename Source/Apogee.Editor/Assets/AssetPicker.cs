using System;
using System.Collections.Generic;
using Apogee.Host.Editing;

namespace Apogee.Editor.Assets;

/// <summary>
/// What the picker popup of an asset field shows: the files of one asset type from the <see cref="AssetCatalog"/> that match a filter text. The popup reads <see cref="Results"/> and draws only the rows that
/// are on the screen, so fifty thousand files cost the same as a hundred; the matching is done again only when the filter or the catalog's listing changed, not every frame. There is one for each asset type
/// (<see cref="AssetCatalog.PickerFor"/>): a person has one popup open at a time. No ImGui here, so tests drive it.
/// </summary>
public sealed class AssetPicker
{
    private readonly AssetCatalog _catalog;
    private string _filter = "";
    private List<CatalogEntry> _results = [];
    private int _seenVersion = -1;
    private string? _seenFilter;

    internal AssetPicker(AssetCatalog catalog, string type)
    {
        _catalog = catalog;
        Type = type;
    }

    /// <summary>The asset type the picker lists (<c>model</c>).</summary>
    public string Type { get; }

    /// <summary>What a person calls it (<c>Model</c>), for the popup's title.</summary>
    public string Label => AssetKinds.LabelOf(Type);

    /// <summary>The text the file names and paths are matched against (see <see cref="AssetCatalog.Search"/>); empty lists everything.</summary>
    public string Filter
    {
        get => _filter;
        set => _filter = value ?? "";
    }

    /// <summary>The files that match, best first (name matches, then path matches). The same list is returned until the filter or the catalog's listing changes.</summary>
    public IReadOnlyList<CatalogEntry> Results
    {
        get
        {
            _ = _catalog.Entries(Type);   // cheap: it only starts a new listing when one is due
            int version = _catalog.Version;
            if (version != _seenVersion || !string.Equals(_filter, _seenFilter, StringComparison.Ordinal))
            {
                _results = _catalog.Search(Type, _filter);
                _seenVersion = version;
                _seenFilter = _filter;
            }

            return _results;
        }
    }

    /// <summary>The first listing is not done yet: the popup says so instead of showing an empty list.</summary>
    public bool Loading => _catalog.IsLoading;

    /// <summary>The listing stopped at a limit: some files are not in the list.</summary>
    public bool Truncated => _catalog.Truncated;

    /// <summary>Called when the popup opens: the filter starts empty, and a new listing is asked for if the last is old.</summary>
    public void Open()
    {
        _filter = "";
        _seenFilter = null;
        _seenVersion = -1;
        _ = _catalog.Entries(Type);
    }
}
