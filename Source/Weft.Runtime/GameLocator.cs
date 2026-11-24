using System.IO;
using Weft.Content.Packages;

namespace Weft.Runtime;

/// <summary>Finds the game that a shipped runtime belongs to: the folder with its <c>game.xml</c> and <c>data/*.pack</c>.</summary>
internal static class GameLocator
{
    /// <summary>
    /// The game's root: <paramref name="programDirectory"/> itself (Windows and Linux, where the game is the executable's folder), else its sibling <c>../Resources</c> (the program is in
    /// <c>Name.app/Contents/MacOS</c> and the game's files in <c>Name.app/Contents/Resources</c>), whichever holds a <c>game.xml</c>. Both are tried on every system, so that a bundle can be
    /// checked anywhere. Null when there is no game.
    /// </summary>
    /// <param name="programDirectory">The folder of the executable (<c>AppContext.BaseDirectory</c>).</param>
    public static string? Find(string programDirectory)
    {
        string directory = Path.GetFullPath(programDirectory);
        foreach (string candidate in new[] { directory, Path.GetFullPath(Path.Combine(directory, "..", "Resources")) })
        {
            if (File.Exists(Path.Combine(candidate, GameManifest.FileName)))
            {
                return candidate.Replace('\\', '/').TrimEnd('/');
            }
        }

        return null;
    }
}
