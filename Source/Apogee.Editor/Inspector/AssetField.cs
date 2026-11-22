namespace Apogee.Editor.Inspector;

/// <summary>What an asset field shows, worked out by the drawer so that the widget only draws it.</summary>
/// <param name="Text">What is on the button: the file's name, <c>None</c>, <c>---</c> when the selected entities differ, or for a broken reference the text it was written as.</param>
/// <param name="Detail">The tooltip: the file's full path and its <c>uid://</c>, or what is wrong with a broken reference; null for none.</param>
/// <param name="Broken">The reference names something that is not there (or nothing that can be saved): drawn in red, with its text kept.</param>
/// <param name="Mixed">The selected entities have different values.</param>
/// <param name="CanShow">There is a file to show in the asset browser.</param>
/// <param name="Problem">A message about the last pick (it could not be used), drawn in red under the field; null for none.</param>
public readonly record struct AssetFieldView(string Text, string? Detail, bool Broken, bool Mixed, bool CanShow, string? Problem = null);

/// <summary>What the person did to an asset field this frame.</summary>
public enum AssetFieldAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>A file was chosen in the picker popup: <see cref="AssetFieldResult.Path"/> is it.</summary>
    Pick,

    /// <summary>A file was dropped on the field (from the asset browser): <see cref="AssetFieldResult.Path"/> is it.</summary>
    Drop,

    /// <summary>The <i>Clear</i> button.</summary>
    Clear,

    /// <summary><i>Show in assets</i>.</summary>
    Show,
}

/// <summary>An action on an asset field, with the virtual path it names for <see cref="AssetFieldAction.Pick"/> and <see cref="AssetFieldAction.Drop"/>.</summary>
public readonly record struct AssetFieldResult(AssetFieldAction Action, string? Path = null);
