using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Content.Packages;
using Apogee.Core.Logging;

namespace Apogee.Scripting;

/// <summary>What one <see cref="HotReloader.Poll"/> did.</summary>
/// <param name="Modules">Modules that were reloaded.</param>
/// <param name="DefsReloaded">The definitions were read again.</param>
/// <param name="Assets">Assets (sources) that changed and were imported again when the project is imported.</param>
/// <param name="Shaders">Shader sources that changed.</param>
/// <param name="NeedsRestart">Changes that only take effect when the game starts again: a package's manifest, a script that runs once.</param>
/// <param name="Failures">What could not be reloaded and why; the old version stays.</param>
public sealed record ReloadReport(
    IReadOnlyList<string> Modules,
    bool DefsReloaded,
    IReadOnlyList<string> Assets,
    IReadOnlyList<string> Shaders,
    IReadOnlyList<string> NeedsRestart,
    IReadOnlyList<(string Path, string Message)> Failures)
{
    public bool Any => Modules.Count > 0 || DefsReloaded || Assets.Count > 0 || Shaders.Count > 0 || NeedsRestart.Count > 0 || Failures.Count > 0;
}

/// <summary>
/// Reloads what changed on disk while the game runs (a development run, the editor). <see cref="Poll"/>, called from the thread that owns the VM, takes the changes a
/// <see cref="VfsWatcher"/> has noticed and sorts them: a <c>.luau</c> file that a script has required is a module reload (<see cref="LuauVm.ReloadModule"/>: updated in place, the old one
/// stays if the new one fails); a definition or schema in a package's content or schema folders makes the definitions be read again (<see cref="LuauVm.Defs"/> is replaced and
/// <c>defs.on_change</c> listeners are called); an image or model is imported again (when the project has been imported: <c>imported/index.xml</c>) and <see cref="AssetsChanged"/> says
/// which; a <c>.slang</c> file raises <see cref="ShadersChanged"/>, for the renderer to rebuild what uses it (the engine's renderers do not yet). A change that cannot be applied to a
/// running game (a package's <c>mod.xml</c>, a script that only runs once) is listed in <see cref="ReloadReport.NeedsRestart"/>.
/// </summary>
public sealed class HotReloader : IDisposable
{
    private static readonly string[] s_assetExtensions = [".glb", ".gltf", ".bin", ".png", ".jpg", ".jpeg"];

    private readonly VirtualFileSystem _files;
    private readonly LuauVm _vm;
    private readonly PackageSet? _packages;
    private readonly VfsWatcher _watcher;
    private readonly IReadOnlyList<IAssetImporter>? _importers;

    /// <param name="importers">What to import again when an asset changes (default: <see cref="AssetImporter.Defaults"/>). The host adds importers of libraries Scripting does not reference, such as shaders.</param>
    public HotReloader(VirtualFileSystem files, LuauVm vm, PackageSet? packages = null, TimeSpan? debounce = null, IReadOnlyList<IAssetImporter>? importers = null)
    {
        _importers = importers;
        _files = files;
        _vm = vm;
        _packages = packages;
        _watcher = new VfsWatcher(files, debounce);
    }

    /// <summary>Raised with the paths of assets that changed (and were imported again, if the project is imported), for the host to load them again.</summary>
    public event Action<IReadOnlyList<string>>? AssetsChanged;

    /// <summary>Raised with the paths of shader sources that changed.</summary>
    public event Action<IReadOnlyList<string>>? ShadersChanged;

    /// <summary>Reloads what changed since the last call (and has been quiet for the debounce time).</summary>
    public ReloadReport Poll()
    {
        List<string> changes = [.. _watcher.TakeChanges().Where(p => !VfsPath.IsUnder(p, "res://" + AssetImporter.CacheFolder))];
        var modules = new List<string>();
        var assets = new List<string>();
        var shaders = new List<string>();
        var restart = new List<string>();
        var failures = new List<(string, string)>();
        bool defs = false;

        foreach (string path in changes)
        {
            string name = VfsPath.FileName(path);
            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".luau")
            {
                if (_vm.IsModuleLoaded(path))
                {
                    ModuleReload result = _vm.ReloadModule(path);
                    if (result.Success)
                    {
                        modules.Add(path);
                        Log.Info("HotReload", $"{path}: reloaded ({result.Message})");
                    }
                    else
                    {
                        failures.Add((path, result.Message));
                        Log.Error("HotReload", $"{path}: not reloaded, the old version stays: {result.Message}");
                    }
                }
                else if (_packages?.Scripts.Any(s => s.Path == path) == true)
                {
                    restart.Add(path);
                }
            }
            else if (name == "mod.xml")
            {
                restart.Add(path);
            }
            else if (name.EndsWith(".import.xml", StringComparison.OrdinalIgnoreCase) || s_assetExtensions.Contains(extension))
            {
                if (path.StartsWith("res://", StringComparison.Ordinal))
                {
                    assets.Add(path);
                }
            }
            else if (extension == ".slang")
            {
                shaders.Add(path);
            }
            else if ((extension is ".xml" or ".xsd") && _packages is not null && IsDefinitionFile(path))
            {
                defs = true;
            }
        }

        if (defs)
        {
            try
            {
                _vm.Defs = _packages!.LoadDefs();
                _vm.NotifyDefsChanged();
                Log.Info("HotReload", $"definitions reloaded ({_vm.Defs.All.Count} definitions, {_vm.Defs.Problems.Count} problems)");
            }
            catch (Exception e) when (e is ContentException or IOException)
            {
                failures.Add(("definitions", e.Message));
                Log.Error("HotReload", $"definitions not reloaded, the old ones stay: {e.Message}");
                defs = false;
            }
        }

        if (assets.Count > 0)
        {
            ImportChanged(assets, failures);
            AssetsChanged?.Invoke(assets);
        }

        if (shaders.Count > 0)
        {
            if (shaders.Any(p => p.StartsWith("res://", StringComparison.Ordinal)))
            {
                ImportChanged([], failures);
            }

            ShadersChanged?.Invoke(shaders);
        }

        foreach (string path in restart)
        {
            Log.Warn("HotReload", $"{path} changed: restart the game to use it.");
        }

        return new ReloadReport(modules, defs, assets, shaders, restart, failures);
    }

    private bool IsDefinitionFile(string path) =>
        _packages!.ContentFolders.Any(f => VfsPath.IsUnder(path, f.Path)) || _packages.SchemaFolders.Any(f => VfsPath.IsUnder(path, f));

    /// <summary>Imports again what changed, when the project has been imported at all (otherwise the host loads the sources).</summary>
    private void ImportChanged(List<string> assets, List<(string, string)> failures)
    {
        if (!_files.FileExists("res://" + AssetImporter.CacheFolder + "/index.xml"))
        {
            return;
        }

        try
        {
            ImportReport report = new AssetImporter(_files, _importers).Run();
            foreach ((string source, string message) in report.Failed)
            {
                failures.Add(("res://" + source, message));
                Log.Error("HotReload", $"{source}: not imported, the old artifact stays: {message}");
            }

            if (report.Imported.Count > 0)
            {
                Log.Info("HotReload", $"imported {string.Join(", ", report.Imported)}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failures.Add(("assets", $"cannot import: {e.Message}"));
        }
    }

    public void Dispose() => _watcher.Dispose();
}
