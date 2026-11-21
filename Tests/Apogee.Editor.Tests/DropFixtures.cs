using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Editor.Assets;

namespace Apogee.Editor.Tests;

/// <summary>A temporary folder that stands in for the operating system's side of a drop (where the files come from), removed when disposed. Never anywhere but the temp folder.</summary>
internal sealed class DropSource : IDisposable
{
    public DropSource() => Root = Path.Combine(Path.GetTempPath(), "apogee-drop-" + Guid.NewGuid().ToString("N"));

    public string Root { get; }

    /// <summary>Writes a file under the folder (made if needed) and returns its full path.</summary>
    public string Write(string relative, byte[] bytes)
    {
        string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string Write(string relative, string text) => Write(relative, new UTF8Encoding(false).GetBytes(text));

    public string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The project's files as a list of virtual paths, for plans that need no disk on the project's side.</summary>
internal sealed class FakeTargets(IEnumerable<string>? files = null, IEnumerable<string>? folders = null) : IDropTargets
{
    public HashSet<string> Files { get; } = [.. files ?? []];

    public HashSet<string> Folders { get; } = [.. folders ?? []];

    public string ReadOnlyPrefix { get; set; } = "mod://";

    /// <summary>Paths that are links (they count as existing, and are never replaced).</summary>
    public HashSet<string> Links { get; } = [];

    public bool Exists(string virtualPath) => Files.Contains(virtualPath) || Folders.Contains(virtualPath) || Links.Contains(virtualPath);

    public bool IsLink(string virtualPath) => Links.Contains(virtualPath);

    public bool IsFolder(string virtualPath) => Folders.Contains(virtualPath);

    public bool CanWrite(string virtualPath) => virtualPath.StartsWith("res://", StringComparison.Ordinal) && !virtualPath.StartsWith(ReadOnlyPrefix, StringComparison.Ordinal);
}

/// <summary>A project that has been imported once has an import index; only such a project has files imported by a drop (see <c>ImportJob</c>).</summary>
internal static class ImportedProject
{
    public static void Mark(EditorProject project) =>
        _ = new Apogee.Content.Import.AssetImporter(project.Session.Files!, Apogee.Host.ScriptSession.Importers).Run();
}

/// <summary>Small valid files the importers take, built in code: a test needs its fixtures in the test project, and CI has no large files.</summary>
internal static class TestAssets
{
    /// <summary>A 2 by 2 PNG (smaller than a block, so importing it needs no texture compressor).</summary>
    public static byte[] Png(byte shade = 0) => Apogee.Content.Png.Encode(2, 2, [shade, 0, 0, 255, 0, shade, 0, 255, 0, 0, shade, 255, 255, 255, 255, 255]);

    private static byte[] TriangleBytes()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        byte[] bytes = new byte[positions.Length * sizeof(float)];
        Buffer.BlockCopy(positions, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private const string Document = """
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}],
         "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36}],
         "buffers":[{"byteLength":36{0}}]}
        """;

    /// <summary>A model with its buffer in a file beside it (<paramref name="bufferUri"/>); the buffer's bytes are <see cref="TriangleBuffer"/>.</summary>
    public static string GltfWithBuffer(string bufferUri) => Document.Replace("{0}", $",\"uri\":\"{bufferUri}\"", StringComparison.Ordinal);

    /// <summary>The bytes of the buffer <see cref="GltfWithBuffer"/> names.</summary>
    public static byte[] TriangleBuffer() => TriangleBytes();

    /// <summary>A model in one file (the binary form of glTF, with its buffer inside).</summary>
    public static byte[] Glb()
    {
        byte[] json = Encoding.UTF8.GetBytes(Document.Replace("{0}", "", StringComparison.Ordinal));
        byte[] paddedJson = Pad(json, (byte)' ');
        byte[] bin = Pad(TriangleBytes(), 0);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + paddedJson.Length + 8 + bin.Length));
        writer.Write((uint)paddedJson.Length);
        writer.Write(0x4E4F534Au);
        writer.Write(paddedJson);
        writer.Write((uint)bin.Length);
        writer.Write(0x004E4942u);
        writer.Write(bin);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Pad(byte[] bytes, byte with) => [.. bytes, .. Enumerable.Repeat(with, (4 - bytes.Length % 4) % 4)];
}
