using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// What the names of a world should be after random edits, worked out the plain way: a list of what exists and its names, searched by scanning (the world looks names up in an index of Flecs').
/// Every step does an edit to the world and to the model; <see cref="Check"/> compares them.
/// </summary>
internal sealed class NameModel(World world, Random random)
{
    private sealed class Node
    {
        public Entity Id;
        public string? Name;
        public Entity Parent;

        public Node Copy() => new() { Id = Id, Name = Name, Parent = Parent };
    }

    // Names an editor makes, ones that look alike, one that is empty, one Flecs reads as an id, one with a dot, the engine's own entity of the module ("Empty") and two of the top level.
    private static readonly string[] Pool = ["a", "A", "a 2", "a 10", "b", "Lamp", "Lamp 2", "", "#7", "x.y", "Empty", "a (copy)", "Health", "Transform"];

    // What Flecs keeps at the top level in the world of the tests: a name a user's top-level entity cannot take.
    private static readonly HashSet<string> TopLevelOfTheEngine = ["Health", "Transform"];

    private Dictionary<Entity, Node> _nodes = [];
    private WorldSnapshot? _held;
    private Dictionary<Entity, Node> _heldNodes = [];

    private static Dictionary<Entity, Node> Copy(Dictionary<Entity, Node> nodes) => nodes.ToDictionary(p => p.Key, p => p.Value.Copy());

    private bool Taken(string name, Entity parent, Entity except) =>
        _nodes.Values.Any(n => n.Id != except && n.Parent == parent && n.Name == name) || (parent.IsNull && TopLevelOfTheEngine.Contains(name));

    // What a name asked for becomes among the children of `parent`, apart from `except`: the name, or "name 2", "name 3"… the first free; null for an empty one.
    private string? Unique(string name, Entity parent, Entity except)
    {
        if (name.Length == 0)
        {
            return null;
        }

        string chosen = name;
        for (int n = 2; Taken(chosen, parent, except); n++)
        {
            chosen = $"{name} {n}";
        }

        return chosen;
    }

    private List<Node> Subtree(Node root)
    {
        var all = new List<Node> { root };
        for (int i = 0; i < all.Count; i++)
        {
            all.AddRange(_nodes.Values.Where(n => n.Parent == all[i].Id).OrderBy(n => n.Id.Id));
        }

        return all;
    }

    private Node Pick() => _nodes.Values.OrderBy(n => n.Id.Id).ElementAt(random.Next(_nodes.Count));

    private string Name() => Pool[random.Next(Pool.Length)];

    public void Step()
    {
        switch (_nodes.Count == 0 ? 0 : random.Next(12))
        {
            case 0:
            case 1:
                Create();
                break;
            case 2:
            case 3:
                Rename();
                break;
            case 4:
            case 5:
                Move();
                break;
            case 6:
                Delete();
                break;
            case 7:
                Copy();
                break;
            case 8:
                _held = world.Snapshot();
                _heldNodes = Copy(_nodes);
                break;
            case 9:
                if (_held is not null)
                {
                    world.Restore(_held);
                    _nodes = Copy(_heldNodes);
                }

                break;
            case 10:
                Load();
                break;
            default:
                DeleteAndBringBack();
                break;
        }
    }

    private void Create()
    {
        Entity entity = world.CreateEntity();
        Entity parent = _nodes.Count > 0 && random.Next(3) > 0 ? Pick().Id : default;
        if (!parent.IsNull)
        {
            world.SetParent(entity, parent);
        }

        string name = Name();
        world.SetName(entity, name);
        _nodes[entity] = new Node { Id = entity, Parent = parent, Name = Unique(name, parent, entity) };
    }

    private void Rename()
    {
        Node node = Pick();
        string name = Name();
        world.SetName(node.Id, name);
        node.Name = Unique(name, node.Parent, node.Id);
    }

    private void Move()
    {
        Node node = Pick();
        Entity parent = random.Next(4) == 0 ? default : Pick().Id;
        if (Subtree(node).Any(n => n.Id == parent))
        {
            return; // under itself
        }

        world.SetParent(node.Id, parent);
        if (node.Parent != parent)
        {
            node.Parent = parent;
            if (node.Name is { } name)
            {
                node.Name = Unique(name, parent, node.Id); // a name taken there is given up and a free one taken
            }
        }
    }

    private void Delete()
    {
        Node node = Pick();
        if (_nodes.Count < 3)
        {
            return;
        }

        foreach (Node gone in Subtree(node))
        {
            _nodes.Remove(gone.Id);
        }

        world.Destroy(node.Id);
    }

    private void Copy()
    {
        Node source = Pick();
        Entity parent = random.Next(3) == 0 ? Pick().Id : source.Parent;
        if (Subtree(source).Any(n => n.Id == parent))
        {
            return; // into itself: a copy of a branch that grows
        }

        if (Subtree(source).Any(n => n.Name is null))
        {
            return; // the copies of unnamed siblings cannot be told apart to say which is the copy of which
        }

        CopyInto(source, parent, world.Clone(source.Id, parent));
    }

    // The name of a copy: "name (copy)", "name (copy 2)"… the first free among the new parent's children as they are then.
    private string CopyName(Node source, Entity parent)
    {
        string original = source.Name!;
        string name = original + " (copy)";
        for (int n = 2; Taken(name, parent, default); n++)
        {
            name = $"{original} (copy {n})";
        }

        return name;
    }

    // Notes the copy of one entity, and then (in the order of their ids, which is the order they are copied in) the copies of its children, which are found by their names: ids that were freed come back, so
    // the copies are not in the order of the originals.
    private void CopyInto(Node source, Entity parent, Entity copy)
    {
        _nodes[copy] = new Node { Id = copy, Parent = parent, Name = CopyName(source, parent) };
        List<Entity> made = world.Children(copy);
        foreach (Node child in world.Children(source.Id).Select(c => _nodes[c]).ToList())
        {
            string expected = CopyName(child, copy);
            Entity found = made.FirstOrDefault(c => !_nodes.ContainsKey(c) && world.GetName(c) == expected);
            Assert.False(found.IsNull, $"the copy of '{child.Name}' is not called '{expected}'");
            CopyInto(child, copy, found);
        }
    }

    // A scene of a few entities, some inside others, loaded into the world or under an entity: each takes the first free name among its siblings, in the order of the document.
    private void Load()
    {
        Entity parent = random.Next(3) == 0 ? Pick().Id : default;
        int count = random.Next(1, 7);
        var up = new List<int>();
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            up.Add(i == 0 || random.Next(3) == 0 ? -1 : random.Next(i));
            names.Add(Name());
        }

        var text = new StringBuilder("<Scene version=\"2\">");
        var asked = new List<string>();
        void Write(int index)
        {
            asked.Add(names[index]);
            text.Append(CultureInfo.InvariantCulture, $"<Entity id=\"{index + 1}\"");
            if (names[index].Length > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" name=\"{names[index]}\"");
            }

            text.Append('>');
            for (int child = 0; child < count; child++)
            {
                if (up[child] == index)
                {
                    Write(child);
                }
            }

            text.Append("</Entity>");
        }

        for (int i = 0; i < count; i++)
        {
            if (up[i] < 0)
            {
                Write(i);
            }
        }

        text.Append("</Scene>");

        List<Entity> loaded = world.LoadScene(text.ToString(), parent);
        Assert.Equal(count, loaded.Count);
        var made = new List<Node>();
        foreach (Entity entity in loaded)
        {
            var node = new Node { Id = entity, Parent = world.GetParent(entity) };
            _nodes[entity] = node;
            made.Add(node);
        }

        for (int i = 0; i < made.Count; i++)
        {
            made[i].Name = Unique(asked[i], made[i].Parent, made[i].Id);
        }
    }

    // What an undo of a deletion does: the branch comes back under its ids, each name the first free one among its siblings (another entity has taken the branch's name in the meantime).
    private void DeleteAndBringBack()
    {
        Node node = Pick();
        List<Node> branch = Subtree(node);
        if (_nodes.Count - branch.Count < 1)
        {
            return;
        }

        WorldSnapshot snapshot = world.SnapshotTree(node.Id);
        List<Node> saved = [.. branch.Select(n => n.Copy())];
        foreach (Node gone in branch)
        {
            _nodes.Remove(gone.Id);
        }

        world.Destroy(node.Id);
        Node? thief = _nodes.Values.OrderBy(n => n.Id.Id).FirstOrDefault(n => n.Parent == node.Parent);
        if (thief is not null && saved[0].Name is { } stolen)
        {
            world.SetName(thief.Id, stolen);
            thief.Name = Unique(stolen, thief.Parent, thief.Id);
        }

        world.RestoreEntities(snapshot);
        foreach (Node back in saved)
        {
            _nodes[back.Id] = new Node { Id = back.Id, Parent = back.Parent };
        }

        foreach (Node back in saved)
        {
            _nodes[back.Id].Name = back.Name is { } wanted ? Unique(wanted, back.Parent, back.Id) : null;
        }
    }

    /// <summary>Whether the world is what the model says after step <paramref name="step"/>: the same entities with the same names and parents, no two siblings of one name, and a top-level name found by a lookup.</summary>
    public void Check(int step)
    {
        List<Entity> user = world.UserEntities();
        Assert.True(_nodes.Count == user.Count, $"step {step}: {_nodes.Count} entities in the model, {user.Count} in the world");
        foreach (Node node in _nodes.Values)
        {
            Assert.True(world.IsAlive(node.Id), $"step {step}: {node.Id} is not alive");
            Assert.True(node.Name == world.GetName(node.Id), $"step {step}: {node.Id} is '{world.GetName(node.Id)}', the model says '{node.Name}'");
            Assert.True(node.Parent == world.GetParent(node.Id), $"step {step}: {node.Id} has another parent");
            if (node.Parent.IsNull && node.Name is { } name && !name.StartsWith('#') && !name.Contains('.', StringComparison.Ordinal))
            {
                Assert.True(world.Lookup(name) == node.Id, $"step {step}: a lookup of '{name}' does not find the top-level entity of that name");
            }
        }

        foreach (IGrouping<(Entity Parent, string? Name), Node> group in _nodes.Values.Where(n => n.Name is not null).GroupBy(n => (n.Parent, n.Name)))
        {
            Assert.True(group.Count() == 1, $"step {step}: two siblings are called '{group.Key.Name}'");
        }
    }
}
