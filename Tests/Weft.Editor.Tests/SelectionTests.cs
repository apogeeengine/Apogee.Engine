using System;
using System.Linq;
using Weft.Ecs;
using Weft.Editor.Editing;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Editor.Tests;

/// <summary>The selection model over a headless world: no window, no ImGui.</summary>
public sealed class SelectionTests : IDisposable
{
    private readonly World _world;
    private readonly Selection _selection;

    public SelectionTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
        _selection = new Selection(() => _world);
    }

    public void Dispose() => _world.Dispose();

    private Entity Make(string name, Entity parent = default) => _world.CreateEntity(name, parent);

    [Fact]
    public void ToggleAddAndRangeFollowTheVisibleOrder()
    {
        Entity a = Make("a");
        Entity b = Make("b");
        Entity c = Make("c");
        Entity d = Make("d");
        Entity e = Make("e");
        Entity[] visible = [a, b, c, d, e];

        _selection.Set(b);
        _selection.Toggle(d);                                   // Ctrl-click adds
        Assert.Equal([b, d], _selection.Items);
        _selection.Toggle(b);                                   // and takes away
        Assert.Equal([d], _selection.Items);
        _selection.Add(a);
        _selection.Add(a);                                      // already in: once
        Assert.Equal([d, a], _selection.Items);
        Assert.True(_selection.Contains(a));
        Assert.False(_selection.Contains(c));

        _selection.SetRange(visible, from: b, to: d);           // Shift-click replaces by the range
        Assert.Equal([b, c, d], _selection.Items);
        _selection.SetRange(visible, from: d, to: a);           // upwards, from the anchor
        Assert.Equal([a, b, c, d], _selection.Items);
        Assert.Equal(a, _selection.Active);

        // A collapsed branch is not in the visible order: the range skips it.
        Entity hidden = Make("hidden", c);
        _selection.SetRange([a, b, c, d, e], from: b, to: e);
        Assert.Equal([b, c, d, e], _selection.Items);
        Assert.False(_selection.Contains(hidden));

        _selection.SetRange(visible, from: hidden, to: e);      // an anchor that is not visible: just the clicked one
        Assert.Equal([e], _selection.Items);
        _selection.SetRange(visible, from: a, to: hidden);      // a target that is not visible: nothing changes
        Assert.Equal([e], _selection.Items);

        _selection.Clear();
        Assert.True(_selection.IsEmpty);
        Assert.Null(_selection.Active);
    }

    [Fact]
    public void TopLevelDropsDescendantsOfSelectedEntities()
    {
        Entity root = Make("root");
        Entity child = Make("child", root);
        Entity grandchild = Make("grandchild", child);
        Entity other = Make("other");
        Entity otherChild = Make("otherChild", other);
        Entity lone = Make("lone");

        _selection.Set([grandchild, root, otherChild, lone, child]);
        Assert.Equal([root, otherChild, lone], _selection.TopLevel());       // grandchild and child ride with root; otherChild's parent is not selected
        Assert.Equal(5, _selection.Count);                                    // the selection itself keeps them all

        _selection.Set([child, grandchild]);
        Assert.Equal([child], _selection.TopLevel());
        Assert.Equal([root], Selection.TopLevelOf(_world, [root, child, root]));    // a list that is not the selection, with a duplicate
    }

    [Fact]
    public void DeadEntitiesFallOut()
    {
        Entity a = Make("a");
        Entity b = Make("b");
        Entity c = Make("c");
        _selection.Set([a, b, c], active: b);
        Assert.Equal(b, _selection.Active);

        _world.Destroy(b);
        Assert.Equal([a, c], _selection.Items);
        Assert.False(_selection.Contains(b));
        Assert.Equal(c, _selection.Active);                      // the active one died: the last still selected takes over
        Assert.Equal([a, c], _selection.TopLevel());

        _world.Destroy(a);
        _world.Destroy(c);
        Assert.Empty(_selection.Items);
        Assert.Null(_selection.Active);
        Assert.True(_selection.IsEmpty);
    }

    [Fact]
    public void TheActiveEntityIsTheLastClicked()
    {
        Entity a = Make("a");
        Entity b = Make("b");
        Entity c = Make("c");

        _selection.Set(a);
        Assert.Equal(a, _selection.Active);
        _selection.Add(b);
        Assert.Equal(b, _selection.Active);
        _selection.Add(c);
        Assert.Equal(c, _selection.Active);
        _selection.Toggle(a);                                     // Ctrl-click on a selected one takes it out; the active one stays
        Assert.Equal(c, _selection.Active);
        _selection.Toggle(a);                                     // on one that is not selected it adds it and makes it active
        Assert.Equal(a, _selection.Active);
        _selection.Toggle(a);                                     // and deselecting the active one hands it to the last selected
        Assert.Equal(c, _selection.Active);
        _selection.SetActive(b);
        Assert.Equal(b, _selection.Active);
        _selection.SetActive(a);                                  // not selected: no change
        Assert.Equal(b, _selection.Active);
        _selection.Remove(b);
        Assert.Equal(c, _selection.Active);

        _selection.Set([a, b, c], active: a);
        Assert.Equal(a, _selection.Active);
        _selection.Set([a, b, c]);                                // by default the last
        Assert.Equal(c, _selection.Active);
    }

    [Fact]
    public void ItRaisesChangedOnlyWhenSomethingChanged()
    {
        Entity a = Make("a");
        Entity b = Make("b");
        int changes = 0;
        _selection.Changed += () => changes++;

        _selection.Set(a);
        _selection.Set(a);                                        // the same again: nothing
        _selection.Clear();
        _selection.Clear();
        _selection.Remove(b);                                     // not in it
        _selection.Add(a);
        _selection.Add(a);
        Assert.Equal(3, changes);
        Assert.Equal(3, _selection.Version);
    }

    [Fact]
    public void MultiSelectRequestsAreAppliedAgainstTheVisibleList()
    {
        Entity a = Make("a");
        Entity b = Make("b");
        Entity c = Make("c");
        Entity d = Make("d");
        Entity[] visible = [a, b, c, d];

        // A plain click on c: everything off, then c on.
        _selection.Set([a, b]);
        SelectionRequests.Apply(_selection, visible, [SelectionRequest.SetAll(false), SelectionRequest.SetRange(true, 2, 2, 1)]);
        Assert.Equal([c], _selection.Items);

        // Shift-click on a from the anchor c: a range upwards, with the clicked end (the first) active.
        SelectionRequests.Apply(_selection, visible, [SelectionRequest.SetRange(true, 0, 2, -1)]);
        Assert.Equal([c, a, b], _selection.Items);
        Assert.Equal(a, _selection.Active);

        // Ctrl-click on b: just b off.
        SelectionRequests.Apply(_selection, visible, [SelectionRequest.SetRange(false, 1, 1, 1)]);
        Assert.Equal([c, a], _selection.Items);

        // Ctrl+A selects all, keeping the active one; Escape clears.
        SelectionRequests.Apply(_selection, visible, [SelectionRequest.SetAll(true)]);
        Assert.Equal(4, _selection.Count);
        Assert.Equal(a, _selection.Active);
        SelectionRequests.Apply(_selection, visible, [SelectionRequest.SetAll(false)]);
        Assert.True(_selection.IsEmpty);

        // Indexes that are not in the list (it changed since ImGui asked) are ignored.
        SelectionRequests.Apply(_selection, visible, [SelectionRequest.SetRange(true, 2, 9, 1), SelectionRequest.SetRange(true, -1, 1, 1)]);
        Assert.True(_selection.IsEmpty);
    }
}
