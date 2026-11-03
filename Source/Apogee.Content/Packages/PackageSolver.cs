using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Content.FileSystem;

namespace Apogee.Content.Packages;

/// <summary>Where a package comes from. The base game's packages load first and cannot be switched off.</summary>
public enum PackageKind
{
    /// <summary>A package of the game itself (<c>res://packages/&lt;id&gt;</c>).</summary>
    Base,

    /// <summary>A mod folder of a development build (<c>res://dev_mods/&lt;id&gt;</c>).</summary>
    Dev,

    /// <summary>A mod the player installed (<c>user://mods/&lt;id&gt;</c> or <c>&lt;id&gt;.zip</c>).</summary>
    User,
}

/// <summary>A package found, before it is decided whether it loads.</summary>
/// <param name="Location">Where it was found, for messages: a folder's path, a zip's.</param>
/// <param name="OpenSource">Makes the source that holds its files (only called for a package that loads).</param>
public sealed record PackageCandidate(PackageManifest Manifest, PackageKind Kind, string Location, Func<IFileSource> OpenSource);

/// <summary>A package and whether it loads. <see cref="Reason"/> says why not.</summary>
public sealed class SolvedPackage
{
    internal SolvedPackage(PackageCandidate candidate) => Candidate = candidate;

    public PackageCandidate Candidate { get; }

    public PackageManifest Manifest => Candidate.Manifest;

    public string Id => Manifest.Id;

    public PackageKind Kind => Candidate.Kind;

    public string Location => Candidate.Location;

    public bool Enabled { get; internal set; } = true;

    /// <summary>Why the package does not load, or null.</summary>
    public string? Reason { get; internal set; }

    /// <summary>The source of the package's files, once it is mounted (only for one that loads).</summary>
    public IFileSource? Source { get; internal set; }
}

/// <param name="ApiVersion">The newest package API this engine has.</param>
/// <param name="GameVersion">The version of the game, checked against each package's <c>gameVersion</c> (null: not checked).</param>
/// <param name="UserDisabled">Ids of mods the player switched off. Base packages ignore it.</param>
public sealed record SolveOptions(int ApiVersion = PackageManifest.EngineApiVersion, PackageVersion? GameVersion = null, IReadOnlySet<string>? UserDisabled = null);

public sealed record SolveResult(IReadOnlyList<SolvedPackage> Packages, IReadOnlyList<SolvedPackage> LoadOrder, IReadOnlyList<string> Problems);

/// <summary>
/// Decides which packages load and in what order. There is one version of each id: the first candidate with an id wins and later ones are ignored with a problem. A package is
/// disabled, with a reason, when it needs a newer API or another game version, the player switched it off, a dependency is missing, disabled, or out of its version range, it is
/// in a dependency cycle, or it is a base package that needs a mod; disabling cascades to what depends on it. What loads is ordered so that dependencies come first, base packages
/// before mods and otherwise by id, so that the order is the same on every machine.
/// </summary>
public static class PackageSolver
{
    public static SolveResult Solve(IReadOnlyList<PackageCandidate> candidates, SolveOptions? options = null)
    {
        options ??= new SolveOptions();
        var problems = new List<string>();
        var packages = new List<SolvedPackage>();
        var byId = new Dictionary<string, SolvedPackage>(StringComparer.Ordinal);
        foreach (PackageCandidate candidate in candidates)
        {
            if (byId.TryGetValue(candidate.Manifest.Id, out SolvedPackage? first))
            {
                problems.Add($"'{candidate.Manifest.Id}' in {candidate.Location} is ignored: {first.Location} has the same id.");
                continue;
            }

            var solved = new SolvedPackage(candidate);
            packages.Add(solved);
            byId[solved.Id] = solved;
        }

        foreach (SolvedPackage package in packages)
        {
            PackageManifest manifest = package.Manifest;
            if (manifest.ApiVersion > options.ApiVersion)
            {
                Disable(package, $"needs package API version {manifest.ApiVersion}, but this engine has {options.ApiVersion}");
            }
            else if (options.GameVersion is { } game && !manifest.GameVersion.Matches(game))
            {
                Disable(package, $"needs game version {manifest.GameVersion}, but this is {game}");
            }
            else if (package.Kind != PackageKind.Base && options.UserDisabled?.Contains(package.Id) == true)
            {
                Disable(package, "is switched off");
            }
            else if (package.Kind == PackageKind.Base && manifest.Dependencies.FirstOrDefault(d => byId.TryGetValue(d.Id, out SolvedPackage? other) && other.Kind != PackageKind.Base) is { } mod)
            {
                Disable(package, $"is a base package but needs the mod '{mod.Id}'");
            }
        }

        foreach (List<SolvedPackage> cycle in FindCycles(packages, byId))
        {
            string members = string.Join(", ", cycle.Select(p => p.Id).Order(StringComparer.Ordinal));
            foreach (SolvedPackage package in cycle)
            {
                Disable(package, $"is part of a dependency cycle ({members})");
            }
        }

        // What depends on something that does not load does not load either, however many steps away.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (SolvedPackage package in packages.Where(p => p.Enabled))
            {
                foreach (PackageDependency dependency in package.Manifest.Dependencies)
                {
                    string? reason = null;
                    if (!byId.TryGetValue(dependency.Id, out SolvedPackage? needed))
                    {
                        reason = $"needs '{dependency.Id}', which is not installed";
                    }
                    else if (!needed.Enabled)
                    {
                        reason = $"needs '{dependency.Id}', which does not load";
                    }
                    else if (!dependency.Version.Matches(needed.Manifest.Version))
                    {
                        reason = $"needs '{dependency.Id}' {dependency.Version}, but {needed.Manifest.Version} is installed";
                    }

                    if (reason is not null)
                    {
                        Disable(package, reason);
                        changed = true;
                        break;
                    }
                }
            }
        }

        return new SolveResult(packages, Order(packages, byId), problems);
    }

    private static void Disable(SolvedPackage package, string reason)
    {
        if (package.Enabled)
        {
            package.Enabled = false;
            package.Reason = reason;
        }
    }

    /// <summary>Groups of packages that depend on each other in a ring (Tarjan's strongly connected components; a package that needs itself is rejected by the manifest).</summary>
    private static List<List<SolvedPackage>> FindCycles(List<SolvedPackage> packages, Dictionary<string, SolvedPackage> byId)
    {
        var cycles = new List<List<SolvedPackage>>();
        var index = new Dictionary<SolvedPackage, int>();
        var low = new Dictionary<SolvedPackage, int>();
        var stack = new Stack<SolvedPackage>();
        var onStack = new HashSet<SolvedPackage>();
        int counter = 0;

        void Visit(SolvedPackage node)
        {
            index[node] = low[node] = counter++;
            stack.Push(node);
            onStack.Add(node);
            foreach (PackageDependency dependency in node.Manifest.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Id, out SolvedPackage? next))
                {
                    continue;
                }

                if (!index.TryGetValue(next, out int nextIndex))
                {
                    Visit(next);
                    low[node] = Math.Min(low[node], low[next]);
                }
                else if (onStack.Contains(next))
                {
                    low[node] = Math.Min(low[node], nextIndex);
                }
            }

            if (low[node] == index[node])
            {
                var component = new List<SolvedPackage>();
                SolvedPackage member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (member != node);

                if (component.Count > 1)
                {
                    cycles.Add(component);
                }
            }
        }

        foreach (SolvedPackage package in packages)
        {
            if (!index.ContainsKey(package))
            {
                Visit(package);
            }
        }

        return cycles;
    }

    /// <summary>Dependencies first; among the ones that can go next, base packages and then the lowest id.</summary>
    private static List<SolvedPackage> Order(List<SolvedPackage> packages, Dictionary<string, SolvedPackage> byId)
    {
        List<SolvedPackage> enabled = [.. packages.Where(p => p.Enabled)];
        var waiting = enabled.ToDictionary(p => p, p => p.Manifest.Dependencies.Count(d => byId.TryGetValue(d.Id, out SolvedPackage? n) && n.Enabled));
        var ready = new SortedSet<SolvedPackage>(Comparer<SolvedPackage>.Create((a, b) =>
        {
            int kind = (a.Kind == PackageKind.Base ? 0 : 1).CompareTo(b.Kind == PackageKind.Base ? 0 : 1);
            return kind != 0 ? kind : string.CompareOrdinal(a.Id, b.Id);
        }));

        foreach (SolvedPackage package in enabled.Where(p => waiting[p] == 0))
        {
            ready.Add(package);
        }

        var order = new List<SolvedPackage>();
        while (ready.Count > 0)
        {
            SolvedPackage next = ready.Min!;
            ready.Remove(next);
            order.Add(next);
            foreach (SolvedPackage dependent in enabled.Where(p => p.Manifest.Dependencies.Any(d => d.Id == next.Id)))
            {
                if (--waiting[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        return order;
    }
}
