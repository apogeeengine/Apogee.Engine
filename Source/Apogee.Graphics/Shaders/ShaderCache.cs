using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Apogee.Graphics.Shaders;

/// <summary>
/// SPIR-V on disk, so a shader that did not change is not compiled again. An entry is named by a hash of what is compiled (source, options,
/// compiler version) and remembers the hash of every file the compile read; it is used only while all of them are unchanged, so editing an
/// imported module invalidates it too.
/// </summary>
internal sealed class ShaderCache(string directory)
{
    private const string Magic = "APGSC2";

    public string Directory { get; } = directory;

    public static string Key(string compilerVersion, string options, string moduleName, string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{compilerVersion}\nabi{ShaderAbi.Version}\n{options}\n{moduleName}\n{source}");
        return Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant();
    }

    public CompiledShader? TryLoad(string key, string moduleName)
    {
        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (reader.ReadString() != Magic)
            {
                return null;
            }

            int dependencyCount = reader.ReadInt32();
            var dependencies = new List<string>(dependencyCount);
            for (int i = 0; i < dependencyCount; i++)
            {
                string dependency = reader.ReadString();
                byte[] expected = reader.ReadBytes(32);
                if (!File.Exists(dependency) || !SHA256.HashData(File.ReadAllBytes(dependency)).AsSpan().SequenceEqual(expected))
                {
                    return null;
                }

                dependencies.Add(dependency);
            }

            byte[] bundle = reader.ReadBytes(reader.ReadInt32());
            CompiledShader shader = ShaderBundle.Read(bundle, "", dependencies, fromCache: true);
            return shader.ModuleName == moduleName ? shader : null;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or ShaderCompileException)
        {
            return null;
        }
    }

    public void Store(string key, CompiledShader shader)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = PathFor(key);
        string temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var writer = new BinaryWriter(File.Create(temporary), Encoding.UTF8))
            {
                writer.Write(Magic);

                // Only files that exist on disk are dependencies; a module compiled from a string has just a name.
                var files = new List<string>();
                foreach (string dependency in shader.Dependencies)
                {
                    if (File.Exists(dependency))
                    {
                        files.Add(dependency);
                    }
                }

                writer.Write(files.Count);
                foreach (string file in files)
                {
                    writer.Write(file);
                    writer.Write(SHA256.HashData(File.ReadAllBytes(file)));
                }

                byte[] bundle = ShaderBundle.Write(shader.ModuleName, [("", shader)]);
                writer.Write(bundle.Length);
                writer.Write(bundle);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // A cache that cannot be written (read-only folder, a race with another process) only costs a recompile.
            File.Delete(temporary);
        }
    }

    private string PathFor(string key) => Path.Combine(Directory, key + ".shader");
}
