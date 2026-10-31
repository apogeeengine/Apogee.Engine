using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Apogee.Build;

/// <summary>
/// <c>./build.sh sdk steam &lt;zip&gt;</c>: unpacks the parts of the Steamworks SDK that the engine uses.
/// Valve allows redistributing the SDK's <c>redistributable_bin</c> folder (and nothing else), so Steam's libraries are committed
/// and its headers (needed only to regenerate the bindings) are git-ignored.
/// </summary>
public static class SdkCommand
{
    public static int Run(string repositoryRoot, string[] args)
    {
        if (args.Length != 2 || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: ./build.sh sdk steam <steamworks_sdk_…zip>");
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "steam" => Steam(repositoryRoot, args[1]),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{args[0]}: {e.Message}");
            return 1;
        }
    }

    private static int Unknown(string name)
    {
        Console.Error.WriteLine($"Unknown SDK '{name}'. Known: steam");
        return 1;
    }

    // Steamworks SDK zip: sdk/public/steam/*.h + steam_api.json (headers, git-ignored), sdk/redistributable_bin/* (committed).
    private static int Steam(string root, string archive)
    {
        string target = Path.Combine(root, "ThirdParty", "steamworks");
        var libraries = new Dictionary<string, string>
        {
            ["sdk/redistributable_bin/linux64/libsteam_api.so"] = Path.Combine("lib", "linux-x64", "libsteam_api.so"),
            ["sdk/redistributable_bin/win64/steam_api64.dll"] = Path.Combine("lib", "win-x64", "steam_api64.dll"),
            ["sdk/redistributable_bin/osx/libsteam_api.dylib"] = Path.Combine("lib", "osx-universal", "libsteam_api.dylib"),
        };

        int headers = 0;
        var libraryFiles = new List<string>();
        using var zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName;
            if (libraries.TryGetValue(name, out string? destination))
            {
                using Stream stream = entry.Open();
                Write(target, destination, stream);
                libraryFiles.Add(destination);
            }
            else if (name.StartsWith("sdk/public/steam/", StringComparison.Ordinal) && entry.Name.Length > 0
                && !name.StartsWith("sdk/public/steam/lib/", StringComparison.Ordinal)
                && (name.EndsWith(".h", StringComparison.Ordinal) || name.EndsWith("steam_api.json", StringComparison.Ordinal)))
            {
                using Stream stream = entry.Open();
                Write(target, Path.Combine("include", "steam", entry.Name), stream);
                headers++;
            }
        }

        if (libraryFiles.Count != libraries.Count || headers == 0)
        {
            throw new InvalidDataException("Not a Steamworks SDK archive (sdk/redistributable_bin and sdk/public/steam expected).");
        }

        string sums = string.Join('\n', libraryFiles.Order(StringComparer.Ordinal)
            .Select(f => $"{Sha256(Path.Combine(target, f))}  {f.Replace('\\', '/')}")) + "\n";
        File.WriteAllText(Path.Combine(target, "lib", "SHA256SUMS"), sums);
        Console.WriteLine($"steam: {libraryFiles.Count} redistributable libraries into {Path.GetRelativePath(root, target)}/lib (committed), "
            + $"{headers} headers into include/ (git-ignored: only the bindings generator reads them)");
        return 0;
    }

    private static void Write(string target, string relative, Stream contents)
    {
        string path = Path.Combine(target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        contents.CopyTo(output);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
