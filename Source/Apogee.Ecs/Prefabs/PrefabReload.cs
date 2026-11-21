namespace Apogee.Ecs;

/// <summary>How a <see cref="PrefabLibrary.Reload(string)"/> ended.</summary>
public enum PrefabReloadStatus
{
    /// <summary>The file was read again and differs from what the library had: the library holds the new document and has raised <see cref="PrefabLibrary.Changed"/>.</summary>
    Reloaded,

    /// <summary>The file says what the library already has (a save that was reported twice, a touched file): nothing happened.</summary>
    Unchanged,

    /// <summary>The file cannot be used (it is gone, cannot be read, is not a prefab, or holds an instance of itself): the library keeps the document it had, and <see cref="PrefabReload.Error"/> says why.</summary>
    Failed,
}

/// <summary>What <see cref="PrefabLibrary.Reload(string)"/> did for one file.</summary>
/// <param name="Handle">The handle of the prefab the path named (0 when it named none).</param>
/// <param name="Path">The file that was looked at, or null when the reference names no file.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Error">Why <see cref="PrefabReloadStatus.Failed"/>, for a message; null otherwise.</param>
public readonly record struct PrefabReload(uint Handle, string? Path, PrefabReloadStatus Status, string? Error = null);
