using System.IO;
using Apogee.Content.FileSystem;

namespace Apogee.Scripting;

/// <summary>Scripts from the content file system: <c>res://main.luau</c>, <c>mod://my_mod/ui.luau</c>. Give it to <see cref="LuauVmOptions.Files"/> and add the prefixes scripts may load from with <see cref="LuauVm.AddRoot"/>.</summary>
public sealed class VfsScriptFiles(VirtualFileSystem files) : IScriptFileSystem
{
    public bool FileExists(string path) => files.FileExists(path);

    public bool DirectoryExists(string path) => files.DirectoryExists(path);

    public string ReadAllText(string path)
    {
        try
        {
            return files.ReadAllText(path);
        }
        catch (IOException e)
        {
            // A script error, not a crash: the file went away, or the path is not one.
            throw new ScriptException(ScriptErrorKind.Runtime, $"cannot read {path}: {e.Message}");
        }
    }
}
