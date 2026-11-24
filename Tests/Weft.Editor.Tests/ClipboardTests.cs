using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Editor.Editing;
using Weft.Reflection;
using Weft.Rendering;
using Xunit;

namespace Weft.Editor.Tests;

/// <summary>Copy, cut, paste and duplicate of entity trees through the clipboard's scene text. Plain classes and a memory clipboard: no window.</summary>
[Collection("editor")]
public sealed class ClipboardTests
{
    private static Entity DefineLink(World world) =>
        world.DefineComponent("Link", [("target", FieldKind.Entity), ("other", FieldKind.Entity)]);

    private static unsafe void SetLink(World world, Entity entity, Entity link, Entity target, Entity other)
    {
        ComponentInfo info = world.GetComponentInfo(link)!;
        byte[] data = new byte[info.Size];
        fixed (byte* bytes = data)
        {
            *(ulong*)(bytes + info.FindField("target")!.Offset) = target.Id;
            *(ulong*)(bytes + info.FindField("other")!.Offset) = other.Id;
            world.SetPointer(entity, link, info.Size, bytes);
        }
    }

    private static unsafe (Entity Target, Entity Other) GetLink(World world, Entity entity, Entity link)
    {
        ComponentInfo info = world.GetComponentInfo(link)!;
        byte* data = world.GetPointer(entity, link);
        return (new Entity(*(ulong*)(data + info.FindField("target")!.Offset)), new Entity(*(ulong*)(data + info.FindField("other")!.Offset)));
    }

    private static List<string?> Names(World world, IEnumerable<Entity> entities) => [.. entities.Select(world.GetName)];

    [Fact]
    public void CopyAndPasteMakesTheSameTreeUnderNewIdsAsOneStep()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity a = project.CreateEmpty();
        world.Set(a, new Transform(new Double3(1, 2, 3), Quaternion.Identity, new Vector3(2, 2, 2)));
        Entity child = project.CreateEmpty(a);
        world.Set(child, new Transform(new Double3(0, 5, 0), Quaternion.Identity, Vector3.One));
        project.Selection = a;
        int steps = project.History.Steps.Count;

        Assert.Equal(1, project.Copy(clipboard));
        Assert.StartsWith("<?xml", clipboard.Text, StringComparison.Ordinal);      // scene text, the XML a scene file holds
        Assert.Contains("<Scene", clipboard.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("uid=", clipboard.Text, StringComparison.Ordinal);    // a copy has no file identity
        Assert.Equal(steps, project.History.Steps.Count);                          // copying changes nothing

        IReadOnlyList<Entity> pasted = project.Paste(clipboard);
        Entity copy = Assert.Single(pasted);
        Assert.NotEqual(a, copy);
        Assert.Equal("Empty 2", world.GetName(copy));
        Assert.Equal(world.GetParent(a), world.GetParent(copy));                   // next to the original
        Assert.Equal(new Double3(1, 2, 3), world.Get<Transform>(copy).Position);
        Assert.Equal(new Vector3(2, 2, 2), world.Get<Transform>(copy).Scale);
        Entity copiedChild = Assert.Single(world.Children(copy));
        Assert.NotEqual(child, copiedChild);
        Assert.Equal("Empty", world.GetName(copiedChild));
        Assert.Equal(new Double3(0, 5, 0), world.Get<Transform>(copiedChild).Position);
        Assert.Equal(copy, project.Selection);                                      // what was pasted is selected
        Assert.Equal(1, project.Selected.Count);
        Assert.Equal(steps + 1, project.History.Steps.Count);                       // one step
        Assert.Equal("Paste Empty", project.History.UndoName);

        project.History.Undo();
        Assert.False(world.IsAlive(copy));
        Assert.False(world.IsAlive(copiedChild));
        Assert.True(world.IsAlive(a));
        project.History.Redo();
        Assert.True(world.IsAlive(copy));                                           // back under the same ids
        Assert.Equal(copiedChild, Assert.Single(world.Children(copy)));
        Assert.Equal("Empty 2", world.GetName(copy));
    }

    [Fact]
    public void PasteGoesNextToTheActiveEntityOrUnderItAsAChild()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity top = project.CreateEmpty();
        Entity inner = project.CreateEmpty(top);
        Entity other = project.CreateEmpty();
        world.SetName(other, "Other");
        project.Selection = other;
        project.Copy(clipboard);                                                    // the thing to paste: "Other"

        project.Selection = inner;
        Entity sibling = Assert.Single(project.Paste(clipboard));                   // next to "inner": a child of "top"
        Assert.Equal(top, world.GetParent(sibling));

        project.Selection = inner;
        Entity under = Assert.Single(project.Paste(clipboard, inner));              // as a child of "inner"
        Assert.Equal(inner, world.GetParent(under));
        Assert.Equal("Other", world.GetName(under));                                // nothing of that name below "inner"

        project.Selection = inner;
        Entity atRoot = Assert.Single(project.Paste(clipboard, default(Entity)));   // the root, whatever is selected
        Assert.True(world.GetParent(atRoot).IsNull);
        Assert.Equal("Other 2", world.GetName(atRoot));                             // the original "Other" is a root too

        project.Selected.Clear();
        Entity noSelection = Assert.Single(project.Paste(clipboard));               // nothing selected: the root
        Assert.True(world.GetParent(noSelection).IsNull);
        Assert.Equal("Other 3", world.GetName(noSelection));
    }

    [Fact]
    public void PastedNamesAreUniqueAmongTheirNewSiblings()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity a = project.CreateEmpty();
        Entity b = project.CreateEmpty();
        Entity childOfA = project.CreateEmpty(a);
        Entity childOfB = project.CreateEmpty(b);
        world.SetName(a, "A");
        world.SetName(b, "B");
        project.Selected.Set([childOfA, childOfB]);                                 // two entities that are both called "Empty"
        project.Copy(clipboard);

        IReadOnlyList<Entity> pasted = project.Paste(clipboard, default(Entity));   // both at the root, in one document
        Assert.Equal(["Empty", "Empty 2"], Names(world, pasted));
        Assert.Equal("Paste 2 entities", project.History.UndoName);

        project.Paste(clipboard, default(Entity));
        Assert.Equal(["A", "B", "Empty", "Empty 2", "Empty 3", "Empty 4"], Names(world, world.Roots()));
    }

    [Fact]
    public void ANameIsOnlyChangedWhenASiblingHasIt()
    {
        // The engine has an entity called "Empty" too (in Flecs' core module): it is nobody's sibling, so a pasted "Empty" keeps its name when no sibling has it.
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity source = project.CreateEmpty();
        Entity child = project.CreateEmpty(source);
        project.Selected.Set([child]);
        project.Copy(clipboard);

        Entity first = Assert.Single(project.Paste(clipboard, default(Entity)));
        Assert.Equal("Empty 2", world.GetName(first));                                   // the root "Empty" is a sibling

        world.SetName(source, "Source");
        Entity second = Assert.Single(project.Paste(clipboard, default(Entity)));
        Assert.Equal("Empty", world.GetName(second));                                    // no sibling has it now
        Entity third = Assert.Single(project.Paste(clipboard, default(Entity)));
        Assert.Equal("Empty 3", world.GetName(third));
        Assert.Equal(["Source", "Empty 2", "Empty", "Empty 3"], Names(world, world.Roots()));

        project.History.Undo();
        project.History.Undo();
        Assert.Equal(["Source", "Empty 2"], Names(world, world.Roots()));
        Assert.Equal("Empty", world.GetName(Assert.Single(project.Paste(clipboard, default(Entity)))));
    }

    [Fact]
    public void ReferencesInsideTheCopyFollowItAndOutsideOnesAreDropped()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity link = DefineLink(world);
        Entity a = project.CreateEmpty();
        Entity b = project.CreateEmpty(a);
        Entity outside = project.CreateEmpty();
        SetLink(world, a, link, target: b, other: outside);                         // inside the copy, and outside it
        SetLink(world, b, link, target: a, other: default);

        project.Selection = a;
        project.Copy(clipboard);
        Entity copy = Assert.Single(project.Paste(clipboard));
        Entity copiedB = Assert.Single(world.Children(copy));

        (Entity target, Entity other) = GetLink(world, copy, link);
        Assert.Equal(copiedB, target);                                              // the copy refers to the copy, not the original
        Assert.True(other.IsNull);                                                  // what pointed outside points at nothing
        (target, other) = GetLink(world, copiedB, link);
        Assert.Equal(copy, target);
        Assert.True(other.IsNull);
        Assert.Equal(b, GetLink(world, a, link).Target);                            // the original is untouched
        Assert.Equal(outside, GetLink(world, a, link).Other);

        // Several top-level entities are one document: a reference from one to another of them is kept.
        Entity c = project.CreateEmpty();
        SetLink(world, c, link, target: outside, other: a);
        project.Selected.Set([outside, c]);
        project.Copy(clipboard);
        IReadOnlyList<Entity> two = project.Paste(clipboard, default(Entity));
        Assert.Equal(2, two.Count);
        (target, other) = GetLink(world, two[1], link);
        Assert.Equal(two[0], target);
        Assert.True(other.IsNull);                                                  // "a" was not copied
    }

    [Fact]
    public void ModelsTravelAsAPathAndAreFoundAgainOnPaste()
    {
        using var rig = new ProjectRig();
        rig.Write("game/models/box.glb", "not a real model");                       // only the path matters here
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity box = project.CreateModel("res://models/box.glb", new Double3(4, 0, 0));
        project.Selection = box;
        project.Copy(clipboard);
        Assert.Contains("Model=\"res://models/box.glb", clipboard.Text, StringComparison.Ordinal);   // a path (and its UID once imported), not a number

        Entity copy = Assert.Single(project.Paste(clipboard));
        Assert.Equal("res://models/box.glb", project.Session.Models.PathOf(world.Get<ModelRenderer>(copy).Model));

        // Text written by hand, or copied from another project, works the same.
        clipboard.Text = "<Scene version=\"1\"><Entity id=\"1\" name=\"Hand\"><Transform Position=\"1 2 3\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/><ModelRenderer Model=\"res://models/box.glb\"/></Entity></Scene>";
        Entity hand = Assert.Single(project.Paste(clipboard));
        Assert.Equal("Hand", world.GetName(hand));
        Assert.Equal(new Double3(1, 2, 3), world.Get<Transform>(hand).Position);
        Assert.Equal("res://models/box.glb", project.Session.Models.PathOf(world.Get<ModelRenderer>(hand).Model));
    }

    [Fact]
    public void CutThenUndoBringsTheEntitiesBack()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity a = project.CreateEmpty();
        Entity child = project.CreateEmpty(a);
        Entity b = project.CreateEmpty();
        project.Selected.Set([a, child]);                                           // the child is carried by its parent, once
        int steps = project.History.Steps.Count;

        Assert.Equal(1, project.Cut(clipboard));
        Assert.False(world.IsAlive(a));
        Assert.False(world.IsAlive(child));
        Assert.True(world.IsAlive(b));
        Assert.True(project.Selected.IsEmpty);
        Assert.Equal(steps + 1, project.History.Steps.Count);                       // one step
        Assert.NotNull(clipboard.Text);

        project.History.Undo();
        Assert.True(world.IsAlive(a));                                              // the same ids
        Assert.Equal(a, world.GetParent(child));
        Assert.Equal(steps, project.History.Steps.Count);

        project.History.Redo();
        Assert.False(world.IsAlive(a));
        Entity pasted = Assert.Single(project.Paste(clipboard));                   // and what was cut can be pasted
        Assert.Equal("Empty", world.GetName(pasted));
        Assert.Single(world.Children(pasted));
    }

    [Fact]
    public void ATextThatIsNotASceneChangesNothing()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        project.CreateEmpty();
        int steps = project.History.Steps.Count;
        int roots = world.Roots().Count;

        string[] texts =
        [
            "", "just some words", "<Foo><Entity id=\"1\"/></Foo>", "<Scenery/>", "<Scene><Entity id=\"1\"", "<Scene version=\"99\"><Entity id=\"1\" name=\"Future\"/></Scene>",
            "<!DOCTYPE Scene [<!ENTITY x \"y\">]><Scene><Entity id=\"1\" name=\"&x;\"/></Scene>", "<Scene version=\"1\"/>", "<Scene version=\"1\"><Entity id=\"1\" prefab=\"res://p.aprefab\"/></Scene>",
            "<Scene>" + new string('x', ClipboardScene.MaxLength) + "</Scene>",
        ];
        foreach (string text in texts)
        {
            clipboard.Text = text;
            Assert.Empty(project.Paste(clipboard));
        }

        clipboard.Text = null;
        Assert.Empty(project.Paste(clipboard));
        Assert.Equal(roots, world.Roots().Count);
        Assert.Equal(steps, project.History.Steps.Count);
        Assert.True(world.Lookup("Future").IsNull);
        Assert.Equal(1, project.Selected.Count);                                    // the selection was not touched either

        // A clipboard that is too long to hold, or has nothing to copy, is left alone by Copy.
        project.Selected.Clear();
        clipboard.Text = "kept";
        Assert.Equal(0, project.Copy(clipboard));
        Assert.Equal("kept", clipboard.Text);
        Assert.Equal(0, project.Cut(clipboard));
        Assert.Equal("kept", clipboard.Text);
    }

    [Fact]
    public void TheClipboardTextIsChecked()
    {
        Assert.True(ClipboardScene.LooksLikeScene("<Scene version=\"1\"/>"));
        Assert.True(ClipboardScene.LooksLikeScene("<Scene>"));
        Assert.True(ClipboardScene.LooksLikeScene("\uFEFF  <?xml version=\"1.0\"?>\n<!-- a copy -->\n<Scene version=\"1\"><Entity id=\"1\"/></Scene>"));
        Assert.False(ClipboardScene.LooksLikeScene(null));
        Assert.False(ClipboardScene.LooksLikeScene(""));
        Assert.False(ClipboardScene.LooksLikeScene("Scene"));
        Assert.False(ClipboardScene.LooksLikeScene("<Scenery/>"));
        Assert.False(ClipboardScene.LooksLikeScene("<?xml version=\"1.0\"?><Material/>"));
        Assert.False(ClipboardScene.LooksLikeScene("<?xml never closed"));
        Assert.False(ClipboardScene.LooksLikeScene("<Scene/>" + new string(' ', ClipboardScene.MaxLength)));

        Assert.True(ClipboardScene.TryRead("<Scene version=\"1\"><Entity id=\"1\"/></Scene>", out var scene, out string problem));
        Assert.Equal("Scene", scene.Name);
        Assert.Equal("", problem);
        Assert.False(ClipboardScene.TryRead(null, out _, out problem));
        Assert.Contains("no text", problem, StringComparison.Ordinal);
        Assert.False(ClipboardScene.TryRead("<Scene><Entity", out _, out problem));
        Assert.Contains("not a scene", problem, StringComparison.Ordinal);
        Assert.False(ClipboardScene.TryRead("<Scene/>" + new string(' ', ClipboardScene.MaxLength), out _, out problem));
        Assert.Contains("longer", problem, StringComparison.Ordinal);

        var memory = new MemoryClipboard();
        Assert.Null(memory.GetText());
        memory.SetText("x");
        Assert.Equal("x", memory.GetText());
    }

    [Fact]
    public void DuplicatingSeveralIsOneStep()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        Entity a = project.CreateEmpty();
        Entity child = project.CreateEmpty(a);
        Entity b = project.CreateEmpty();
        world.SetName(a, "A");
        world.SetName(b, "B");
        project.Selected.Set([a, child, b]);
        int steps = project.History.Steps.Count;

        IReadOnlyList<Entity> copies = project.Duplicate(project.Selected.TopLevel());
        Assert.Equal(2, copies.Count);                                               // the child goes with its parent, once
        Assert.Equal(["A (copy)", "B (copy)"], Names(world, copies));
        Assert.Single(world.Children(copies[0]));
        Assert.Empty(world.Children(copies[1]));
        Assert.Equal(copies, project.Selected.Items);                                // the copies are selected
        Assert.Equal(steps + 1, project.History.Steps.Count);
        Assert.Equal("Duplicate 2 entities", project.History.UndoName);

        project.History.Undo();
        Assert.All(copies, copy => Assert.False(world.IsAlive(copy)));
        Assert.True(world.IsAlive(a) && world.IsAlive(child) && world.IsAlive(b));   // the originals are untouched
        project.History.Redo();
        Assert.All(copies, copy => Assert.True(world.IsAlive(copy)));

        Entity one = project.Duplicate(b);                                           // a single one keeps its name in the step
        Assert.True(world.IsAlive(one));
        Assert.StartsWith("Duplicate B", project.History.UndoName, StringComparison.Ordinal);
        Assert.Empty(project.Duplicate(new List<Entity>()));
    }

    [Fact]
    public void CopyWorksWhilePlayingButCutPasteAndDuplicateDoNothing()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        World world = rig.World;
        var clipboard = new MemoryClipboard();
        Entity hero = project.CreateEmpty();
        project.Rename(hero, "Hero");
        Assert.True(project.Play(), project.PlayError);
        world = rig.World;                                                           // the run has a world of its own
        Entity playing = world.Lookup("Hero");
        project.Selection = playing;
        int roots = world.Roots().Count;

        Assert.Equal(1, project.Copy(clipboard));                                    // it copies the running state
        Assert.Contains("Hero", clipboard.Text, StringComparison.Ordinal);
        Assert.Equal(0, project.Cut(clipboard));
        Assert.Empty(project.Paste(clipboard));
        Assert.True(project.Duplicate(playing).IsNull);
        Assert.Empty(project.Duplicate([playing]));
        Assert.Equal(roots, world.Roots().Count);
        Assert.True(world.IsAlive(playing));

        project.Stop();
        Assert.Single(project.World.Roots());
        Assert.Single(project.Paste(clipboard));                                     // editing again: it pastes
    }
}
