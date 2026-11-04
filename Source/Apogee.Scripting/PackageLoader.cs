using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Packages;
using Apogee.Core.Logging;

namespace Apogee.Scripting;

public sealed record PackageLoaderOptions
{
    /// <summary>The memory each mod's scripts may use, in bytes (a soft limit: going over it is an error in that mod's code). 0: none.</summary>
    public long ModMemoryLimit { get; init; } = 256L * 1024 * 1024;

    /// <summary>The time one call of a mod's code may take, in milliseconds. 0: the VM's default.</summary>
    public int ModTimeBudgetMilliseconds { get; init; } = 2000;
}

/// <summary>What happened to a package's scripts.</summary>
/// <param name="Ran">True when every script ran (a package with none counts).</param>
/// <param name="Error">What stopped it, or null.</param>
public sealed record PackageRunResult(SolvedPackage Package, bool Ran, string? Error);

/// <summary>
/// Runs the packages of a <see cref="PackageSet"/> in a VM. Each package's scripts run in the order its manifest lists them, packages in load order; a package's base-game scripts run as the
/// VM's base owner, a mod's as an owner of its own (its own memory category and limits), with:
/// <list type="bullet">
/// <item><c>@&lt;id&gt;/…</c> as a <c>require</c> alias for each package, so that a mod can use a dependency's modules;</item>
/// <item>confinement: a mod may <c>require</c> files of its own package and of the packages it depends on (and <c>res://</c>), nothing else;</item>
/// <item>the <c>fs</c> library with the same rules: a mod reads <c>res://</c>, its own package, its dependencies', and <c>user://moddata/&lt;id&gt;/</c>, and writes only <c>user://moddata/&lt;id&gt;/</c>;</item>
/// <item>modules that run as the owner of the package they are in, whoever required them.</item>
/// </list>
/// A script that fails stops its package (the error is logged and in the result), and what depends on it is not run; it never stops the game. Content the package brought stays loaded.
/// </summary>
public sealed class PackageLoader
{
    private readonly VirtualFileSystem _files;
    private readonly PackageSet _packages;
    private readonly PackageLoaderOptions _options;
    private readonly Dictionary<string, HashSet<string>> _reach = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptOwner> _owners = new(StringComparer.Ordinal);

    public PackageLoader(VirtualFileSystem files, PackageSet packages, PackageLoaderOptions? options = null)
    {
        _files = files;
        _packages = packages;
        _options = options ?? new PackageLoaderOptions();

        // What each package may reach: itself and, however far, what it depends on. Dependencies come first in the load order, so one pass does it.
        foreach (SolvedPackage package in packages.LoadOrder)
        {
            var reach = new HashSet<string>(StringComparer.Ordinal) { package.Id };
            foreach (PackageDependency dependency in package.Manifest.Dependencies)
            {
                if (_reach.TryGetValue(dependency.Id, out HashSet<string>? inner))
                {
                    reach.UnionWith(inner);
                }
            }

            _reach[package.Id] = reach;
        }
    }

    /// <summary>Gives <see cref="LuauVmOptions.OwnerOfModule"/>: a module in a package runs as that package's owner.</summary>
    public ScriptOwner? OwnerOfModule(string path) => PackageIdOf(path) is { } id && _owners.TryGetValue(id, out ScriptOwner? owner) ? owner : null;

    /// <summary>Gives <see cref="LuauVmOptions.CanLoadModule"/>.</summary>
    public bool CanLoadModule(ScriptOwner requester, string path)
    {
        if (!requester.IsMod)
        {
            return true;
        }

        return PackageIdOf(path) is { } id
            ? _reach.TryGetValue(requester.Id, out HashSet<string>? reach) && reach.Contains(id)
            : path.StartsWith("res://", StringComparison.Ordinal);
    }

    /// <summary>Gives <see cref="LuauVmOptions.CanAccessFile"/>.</summary>
    public bool CanAccessFile(ScriptOwner owner, string path, bool write)
    {
        if (!owner.IsMod)
        {
            return true;
        }

        string data = $"user://moddata/{owner.Id}";
        if (VfsPath.IsUnder(path, data))
        {
            return true;
        }

        if (write)
        {
            return false;
        }

        if (VfsPath.IsUnder(path, "res://"))
        {
            return true;
        }

        return PackageIdOf(path) is { } id && _reach.TryGetValue(owner.Id, out HashSet<string>? reach) && reach.Contains(id);
    }

    /// <summary>The package a <c>mod://&lt;id&gt;/…</c> path is in.</summary>
    private static string? PackageIdOf(string path)
    {
        if (!path.StartsWith("mod://", StringComparison.Ordinal))
        {
            return null;
        }

        int slash = path.IndexOf('/', "mod://".Length);
        return slash < 0 ? path["mod://".Length..] : path["mod://".Length..slash];
    }

    /// <summary>Makes the owners, aliases and roots, and runs every package's scripts in <paramref name="vm"/>, which was made with this loader's hooks.</summary>
    public IReadOnlyList<PackageRunResult> Load(LuauVm vm)
    {
        foreach (SolvedPackage package in _packages.LoadOrder)
        {
            string root = "mod://" + package.Id;
            vm.AddRoot(root);
            vm.AddAlias(package.Id, root);
            _owners[package.Id] = package.Kind == PackageKind.Base
                ? vm.BaseOwner
                : vm.CreateOwner(package.Id, isMod: true, _options.ModMemoryLimit, _options.ModTimeBudgetMilliseconds);
        }

        var results = new List<PackageRunResult>();
        vm.SetPackages(_packages, results); // scripts that run now can already ask which packages there are (the list fills as they run)
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (SolvedPackage package in _packages.LoadOrder)
        {
            string? error = package.Manifest.Dependencies.FirstOrDefault(d => failed.Contains(d.Id)) is { } broken
                ? $"was not run: '{broken.Id}', which it needs, failed"
                : RunScripts(vm, package);
            if (error is not null)
            {
                failed.Add(package.Id);
                Log.Error("Packages", $"{package.Id} {error}");
            }

            results.Add(new PackageRunResult(package, error is null, error));
        }

        return results;
    }

    private string? RunScripts(LuauVm vm, SolvedPackage package)
    {
        ScriptOwner owner = _owners[package.Id];
        foreach (string script in package.Manifest.Scripts)
        {
            string path = PackageSet.Address(package, script);
            try
            {
                vm.Execute(owner, "@" + path, _files.ReadAllText(path));
            }
            catch (ScriptException e)
            {
                return $"failed in {script}: {e.Message}";
            }
            catch (IOException e)
            {
                return $"could not read {script}: {e.Message}";
            }
        }

        return null;
    }
}
