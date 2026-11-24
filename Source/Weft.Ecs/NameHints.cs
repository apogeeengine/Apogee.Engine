using System.Collections.Generic;
using Weft.Reflection;

namespace Weft.Ecs;

/// <summary>
/// Where <see cref="World.UniqueName(string, Entity, Entity)"/> left off for a name under a parent, for a load that names many entities one after the other and frees no name: the next one starts at the
/// suffix after the last that was given, which are all taken. Without it the n-th entity of one name tries n suffixes.
/// </summary>
internal sealed class NameHints
{
    private readonly Dictionary<(ulong Parent, string Name), int> _next = [];

    /// <summary>The first suffix that can be free for <paramref name="name"/> under <paramref name="parent"/> (2 when none was given yet).</summary>
    public int Next(Entity parent, string name) => _next.GetValueOrDefault((parent.Id, name), 2);

    /// <summary>Notes that <paramref name="suffix"/> was given, so every one up to it is taken.</summary>
    public void Used(Entity parent, string name, int suffix) => _next[(parent.Id, name)] = suffix + 1;
}
