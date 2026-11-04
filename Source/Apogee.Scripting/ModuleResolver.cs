using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Apogee.Content.FileSystem;

namespace Apogee.Scripting;

/// <summary>Where scripts are read from. The VM only sees paths with <c>/</c> as the separator, plain ones or <c>res://</c>-style ones; <see cref="VfsScriptFiles"/> reads them from the content file system.</summary>
public interface IScriptFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string ReadAllText(string path);
}

/// <summary>Scripts from a folder of the real file system.</summary>
public sealed class PhysicalScriptFileSystem : IScriptFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
}

/// <summary>A module found by <see cref="ModuleResolver"/>.</summary>
/// <param name="Key">What identifies the module in the cache: its path, or <c>@engine/name</c>.</param>
/// <param name="ChunkName">The chunk name for Luau: <c>@path</c> for a file.</param>
internal sealed record ResolvedModule(string Key, string ChunkName, string? Path, string? EmbeddedSource);

/// <summary>
/// Require-by-string, as Luau's RFC describes it: <c>./x</c> and <c>../x</c> are relative to the requiring file, <c>@alias/x</c> uses
/// the aliases of the nearest <c>.luaurc</c>, and <c>@self/x</c> is relative to an <c>init.luau</c>'s own folder. A module is <c>x.luau</c>
/// or <c>x/init.luau</c>; having both is an error. Paths must stay inside the VM's roots (folders, or a prefix such as <c>res://</c>; <c>..</c> never climbs out of
/// a scheme). <c>@engine/name</c> modules are embedded.
/// </summary>
internal sealed class ModuleResolver(IScriptFileSystem files)
{
    private readonly List<string> _roots = [];
    private readonly Dictionary<string, string> _embedded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>?> _configs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public void AddRoot(string directory) => _roots.Add(Normalize(directory));

    /// <summary>Makes <c>@name/x</c> mean <c>path/x</c> in every script, without a <c>.luaurc</c> (a package's id for its folder). A <c>.luaurc</c> alias of the same name wins.</summary>
    public void AddAlias(string name, string path) => _aliases[name.ToLowerInvariant()] = Normalize(path);

    /// <summary>Adds a module that is part of the engine; <paramref name="name"/> is what follows <c>@engine/</c>.</summary>
    public void AddEmbedded(string name, string source) => _embedded["@engine/" + name] = source;

    public ResolvedModule Resolve(string requesterChunk, string request)
    {
        if (request.StartsWith("@engine/", StringComparison.Ordinal))
        {
            return _embedded.TryGetValue(request, out string? source)
                ? new ResolvedModule(request, "=" + request[1..], null, source)
                : throw new ScriptException(ScriptErrorKind.Runtime, $"module not found: {request}");
        }

        string target;
        if (request.StartsWith("./", StringComparison.Ordinal) || request.StartsWith("../", StringComparison.Ordinal))
        {
            target = Normalize(Combine(RequesterDirectory(requesterChunk, request), request));
        }
        else if (request.StartsWith('@'))
        {
            target = Normalize(ResolveAlias(requesterChunk, request));
        }
        else
        {
            throw new ScriptException(ScriptErrorKind.Runtime, $"require path '{request}' must start with ./, ../ or @alias/");
        }

        if (!_roots.Exists(root => VfsPath.IsUnder(target, root)))
        {
            throw new ScriptException(ScriptErrorKind.Runtime, $"module '{request}' is outside the folders scripts may load from");
        }

        string asFile = target + ".luau";
        string asInit = target + "/init.luau";
        bool file = files.FileExists(asFile);
        bool init = files.DirectoryExists(target) && files.FileExists(asInit);
        if (file && init)
        {
            throw new ScriptException(ScriptErrorKind.Runtime, $"module '{request}' is ambiguous: both {asFile} and {asInit} exist");
        }

        if (!file && !init)
        {
            throw new ScriptException(ScriptErrorKind.Runtime, $"module not found: {request} (looked for {asFile} and {asInit})");
        }

        string path = file ? asFile : asInit;
        return new ResolvedModule(path, "@" + path, path, null);
    }

    private static string RequesterDirectory(string chunk, string request)
    {
        if (!chunk.StartsWith('@'))
        {
            throw new ScriptException(ScriptErrorKind.Runtime, $"cannot resolve '{request}': the requiring chunk '{chunk}' is not a file");
        }

        return DirectoryOf(chunk[1..]);
    }

    private string ResolveAlias(string requesterChunk, string request)
    {
        int slash = request.IndexOf('/', StringComparison.Ordinal);
        string alias = (slash < 0 ? request[1..] : request[1..slash]).ToLowerInvariant();
        string rest = slash < 0 ? "" : request[(slash + 1)..];

        if (alias == "self")
        {
            string requester = requesterChunk.StartsWith('@') ? requesterChunk[1..] : throw new ScriptException(ScriptErrorKind.Runtime, $"cannot resolve '{request}': the requiring chunk is not a file");
            if (!requester.EndsWith("/init.luau", StringComparison.Ordinal))
            {
                throw new ScriptException(ScriptErrorKind.Runtime, $"'@self' can only be used from an init.luau, not from {requester}");
            }

            return Combine(DirectoryOf(requester), rest);
        }

        string start = RequesterDirectory(requesterChunk, request);
        for (string? directory = start; directory is not null; directory = ParentOf(directory))
        {
            Dictionary<string, string>? aliases = AliasesIn(directory);
            if (aliases is not null && aliases.TryGetValue(alias, out string? value))
            {
                return Combine(Combine(directory, value), rest);
            }
        }

        if (_aliases.TryGetValue(alias, out string? global))
        {
            return Combine(global, rest);
        }

        throw new ScriptException(ScriptErrorKind.Runtime, $"alias @{alias} is not defined in any .luaurc above {start}");
    }

    /// <summary>The <c>aliases</c> of the <c>.luaurc</c> in the folder (names lower-cased), or null without one.</summary>
    private Dictionary<string, string>? AliasesIn(string directory)
    {
        if (_configs.TryGetValue(directory, out Dictionary<string, string>? known))
        {
            return known;
        }

        Dictionary<string, string>? aliases = null;
        string path = Combine(directory, ".luaurc");
        if (files.FileExists(path))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(files.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("aliases", out JsonElement element) && element.ValueKind == JsonValueKind.Object)
                {
                    aliases = [];
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            aliases[property.Name.ToLowerInvariant()] = property.Value.GetString()!;
                        }
                    }
                }
            }
            catch (JsonException e)
            {
                throw new ScriptException(ScriptErrorKind.Runtime, $"{path} is not valid: {e.Message}");
            }
        }

        _configs[directory] = aliases;
        return aliases;
    }

    // ---- Paths ---------------------------------------------------------------------------------------------------------------------

    private static string DirectoryOf(string path) => VfsPath.DirectoryOf(path);

    private static string? ParentOf(string directory) => VfsPath.ParentOf(directory);

    private static string Combine(string directory, string relative) => VfsPath.Combine(directory, relative);

    /// <summary>Forward slashes, no <c>.</c> or <c>..</c> segments, no trailing slash.</summary>
    internal static string Normalize(string path) => VfsPath.Normalize(path);
}
