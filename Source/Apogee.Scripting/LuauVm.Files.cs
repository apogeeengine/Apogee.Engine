using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Native.Luau;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    /// <summary>Registers the <c>fs</c> library: the virtual file system for scripts, behind <see cref="LuauVmOptions.CanAccessFile"/>.</summary>
    private static void RegisterFileNatives()
    {
        ScriptNatives.RegisterLibrary("fs", "exists", &FsExistsBody, "(path: string) -> boolean", "Whether a file or folder is there (false for a path this script may not see).");
        ScriptNatives.RegisterLibrary("fs", "read_text", &FsReadTextBody, "(path: string) -> (string?, string?)", "The file as UTF-8 text, or nil and a message.");
        ScriptNatives.RegisterLibrary("fs", "read_bytes", &FsReadBytesBody, "(path: string) -> (string?, string?)", "The file's bytes as a string, or nil and a message.");
        ScriptNatives.RegisterLibrary("fs", "write_text", &FsWriteBody, "(path: string, text: string) -> (boolean?, string?)", "Writes a file (making its folders), or returns nil and a message. A mod can write under user://moddata/<its id>/ only.");
        ScriptNatives.RegisterLibrary("fs", "write_bytes", &FsWriteBody, "(path: string, bytes: string) -> (boolean?, string?)", "Writes the bytes of a string to a file, like write_text.");
        ScriptNatives.RegisterLibrary("fs", "list", &FsListBody, "(directory: string, pattern: string?, recursive: boolean?) -> ({ string }?, string?)", "The files in a folder (full paths, sorted) whose names match a pattern with * and ?, and inside its folders with recursive.");
        ScriptNatives.RegisterLibrary("fs", "list_dirs", &FsListDirsBody, "(directory: string) -> ({ string }?, string?)", "The folders directly in a folder (full paths, sorted).");
        ScriptNatives.RegisterLibrary("fs", "make_dir", &FsMakeDirBody, "(path: string) -> (boolean?, string?)", "Makes a folder (and the ones above it).");
        ScriptNatives.RegisterLibrary("fs", "remove", &FsRemoveBody, "(path: string) -> (boolean?, string?)", "Deletes a file, or a folder with what is in it. Nothing there is fine.");
    }

    /// <summary>
    /// Checks a path a script gave against the rules (valid, and allowed for the owner of the script that asked) and gives the file system. On failure it says why in terms of the virtual
    /// path only: the paths of the real disk never reach a script.
    /// </summary>
    private bool TryAccess(State* L, int argument, bool write, out VirtualFileSystem files, out string path, out string error)
    {
        string given = StringArgument(L, argument, "the path");
        files = _options.FileSystem!;
        path = given;
        error = "";
        if (_options.FileSystem is null)
        {
            error = "there is no file system";
            return false;
        }

        if (!VfsPath.TryParse(given, out path, out string? problem))
        {
            error = $"{given}: not a valid path ({problem})";
            return false;
        }

        if (_options.CanAccessFile is { } can && !can(OwnerOf(L), path, write))
        {
            error = $"{path}: access denied";
            return false;
        }

        return true;
    }

    private static int Failure(State* L, string message)
    {
        Lua.Pushnil(L);
        Stack.PushString(L, message);
        return 2;
    }

    private static int Done(State* L)
    {
        Lua.Pushboolean(L, 1);
        return 1;
    }

    private static string Describe(string path, Exception e, string doing) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => $"{path}: not found",
        UnauthorizedAccessException => $"{path}: read-only or not allowed",
        _ => $"{path}: could not be {doing}",
    };

    private static int FsExistsBody(State* L)
    {
        LuauVm vm = VmOf(L);
        Lua.Pushboolean(L, vm.TryAccess(L, 1, write: false, out VirtualFileSystem files, out string path, out _) && files.Exists(path) ? 1 : 0);
        return 1;
    }

    private static int FsReadTextBody(State* L)
    {
        if (!VmOf(L).TryAccess(L, 1, write: false, out VirtualFileSystem files, out string path, out string error))
        {
            return Failure(L, error);
        }

        try
        {
            Stack.PushString(L, files.ReadAllText(path));
            return 1;
        }
        catch (IOException e)
        {
            return Failure(L, Describe(path, e, "read"));
        }
        catch (UnauthorizedAccessException e)
        {
            return Failure(L, Describe(path, e, "read"));
        }
    }

    private static int FsReadBytesBody(State* L)
    {
        if (!VmOf(L).TryAccess(L, 1, write: false, out VirtualFileSystem files, out string path, out string error))
        {
            return Failure(L, error);
        }

        try
        {
            byte[] bytes = files.ReadAllBytes(path);
            fixed (byte* data = bytes)
            {
                Lua.Pushlstring(L, data, (nuint)bytes.Length);
            }

            return 1;
        }
        catch (IOException e)
        {
            return Failure(L, Describe(path, e, "read"));
        }
        catch (UnauthorizedAccessException e)
        {
            return Failure(L, Describe(path, e, "read"));
        }
    }

    private static int FsWriteBody(State* L)
    {
        if (!Stack.Is(L, 2, LuaType.Tstring))
        {
            throw new Apogee.Reflection.ValueException($"the data must be a string, got {Stack.TypeName(L, 2)}");
        }

        if (!VmOf(L).TryAccess(L, 1, write: true, out VirtualFileSystem files, out string path, out string error))
        {
            return Failure(L, error);
        }

        nuint length;
        byte* data = Lua.Tolstring(L, 2, &length);
        try
        {
            files.WriteAllBytes(path, new ReadOnlySpan<byte>(data, (int)length));
            return Done(L);
        }
        catch (IOException e)
        {
            return Failure(L, Describe(path, e, "written"));
        }
        catch (UnauthorizedAccessException e)
        {
            return Failure(L, Describe(path, e, "written"));
        }
    }

    private static int FsListBody(State* L)
    {
        string pattern = Stack.Is(L, 2, LuaType.Tstring) ? Stack.ReadString(L, 2) : "*";
        bool recursive = Lua.Toboolean(L, 3) != 0;
        if (!VmOf(L).TryAccess(L, 1, write: false, out VirtualFileSystem files, out string path, out string error))
        {
            return Failure(L, error);
        }

        return PushPaths(L, files.EnumerateFiles(path, pattern, recursive));
    }

    private static int FsListDirsBody(State* L)
    {
        if (!VmOf(L).TryAccess(L, 1, write: false, out VirtualFileSystem files, out string path, out string error))
        {
            return Failure(L, error);
        }

        return PushPaths(L, files.EnumerateDirectories(path));
    }

    private static int PushPaths(State* L, List<string> paths)
    {
        Stack.Reserve(L, 4);
        Lua.Createtable(L, paths.Count, 0);
        for (int i = 0; i < paths.Count; i++)
        {
            Stack.PushString(L, paths[i]);
            Lua.Rawseti(L, -2, i + 1);
        }

        return 1;
    }

    private static int FsMakeDirBody(State* L)
    {
        if (!VmOf(L).TryAccess(L, 1, write: true, out VirtualFileSystem files, out string path, out string error))
        {
            return Failure(L, error);
        }

        try
        {
            files.CreateDirectory(path);
            return Done(L);
        }
        catch (IOException e)
        {
            return Failure(L, Describe(path, e, "made"));
        }
        catch (UnauthorizedAccessException e)
        {
            return Failure(L, Describe(path, e, "made"));
        }
    }

    private static int FsRemoveBody(State* L)
    {
        if (!VmOf(L).TryAccess(L, 1, write: true, out VirtualFileSystem files, out string path, out string error))
        {
            return Failure(L, error);
        }

        try
        {
            files.Delete(path);
            return Done(L);
        }
        catch (IOException e)
        {
            return Failure(L, Describe(path, e, "removed"));
        }
        catch (UnauthorizedAccessException e)
        {
            return Failure(L, Describe(path, e, "removed"));
        }
    }
}
