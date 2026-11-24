using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Weft.Editor.Editing;
using Weft.Editor.Inspector;
using Weft.Host.Editing;
using Weft.Reflection;
using Xunit;

namespace Weft.Editor.Tests;

/// <summary>An object inspector over a fake UI, and what the fake saw in the last frame.</summary>
internal sealed class ObjectRig
{
    public ObjectRig(bool withHistory = true)
    {
        // The generated registration of the test classes runs with the assembly's module initializer: make sure it has.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(InspectProbe).Module.ModuleHandle);
        History = withHistory ? new EditHistory() : null;
        Table = DrawerTable.CreateDefault();
        Inspector = new ObjectInspector(History, Table, entity => entity.Id == 0x100000005 ? "Hero" : null);
    }

    public EditHistory? History { get; }

    public DrawerTable Table { get; }

    public ObjectInspector Inspector { get; }

    public FakeInspectorUi Ui { get; } = new();

    public List<DrawnWidget> Seen { get; private set; } = [];

    public List<string> Texts { get; private set; } = [];

    public List<string> Errors { get; private set; } = [];

    public List<string> Headings { get; private set; } = [];

    /// <summary>One frame with whatever the fake was told to do; what was drawn is kept in <see cref="Seen"/> and the script is forgotten.</summary>
    public bool Frame(EngineObject target, bool locked = false)
    {
        bool written = Inspector.Draw(Ui, target, locked);
        Seen = [.. Ui.Drawn];
        Texts = [.. Ui.Texts];
        Errors = [.. Ui.Errors];
        Headings = [.. Ui.Headings];
        Ui.NextFrame();
        return written;
    }

    public DrawnWidget Widget(string kind, string label) => Seen.Single(w => w.Kind == kind && w.Label == label);

    public bool Has(string kind, string label) => Seen.Any(w => w.Kind == kind && w.Label == label);

    public int Count(string kind) => Seen.Count(w => w.Kind == kind);
}

/// <summary>The object inspector: what it draws for a registered class, and what its edits do. No ImGui: a fake answers as a person would.</summary>
public sealed class ObjectInspectorTests
{
    private static readonly double[] DragFrames = [4.0, 6.0];

    [Fact]
    public void EveryPropertyKindReadsAndWritesThroughValue()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();

        // What each property is drawn with.
        rig.Frame(probe);
        Assert.Equal([0, 10, 2], [rig.Widget("drag", "count").Min, rig.Widget("drag", "count").Max, rig.Widget("drag", "count").Speed]);
        Assert.Equal("%.0f", rig.Widget("drag", "count").Format);
        Assert.Equal([0, 1], [rig.Widget("drag", "level").Min, rig.Widget("drag", "level").Max]);
        Assert.Equal("probe", rig.Widget("text", "name").Text);
        Assert.Equal("line", rig.Widget("text", "name").Format);
        Assert.True(rig.Has("toggle", "flag"));
        Assert.True(rig.Has("drag", "position"));
        Assert.True(rig.Has("color", "tint"));
        Assert.Equal("%.1f deg", rig.Widget("drag", "turn").Format);
        Assert.Equal(["idle", "walk", "run"], rig.Widget("combo", "gait").Items);
        Assert.Equal("idle", rig.Widget("combo", "gait").Text);
        Assert.Equal("parts: head", rig.Widget("node", "parts").Text);
        Assert.Equal("None", rig.Widget("entity", "target").Text);

        // Each is changed the way a person would, and lands in the property through its setter.
        rig.Ui.ToggleTo["flag"] = true;
        rig.Ui.DragTo["count"] = [8];
        rig.Ui.DragTo["level"] = [0.25];
        rig.Ui.TextTo["name"] = "renamed";
        rig.Ui.DragTo["position"] = [1, 2, 3];
        rig.Ui.ColorTo["tint"] = [2f, 0.5f, 0.25f];
        rig.Ui.DragTo["turn"] = [0, 90, 0];
        rig.Ui.PickIndex["gait"] = 2;
        rig.Ui.ToggleTo["arm"] = true;
        rig.Ui.EntityTo["target"] = 0x100000005;
        Assert.True(rig.Frame(probe));

        Assert.True(probe.Flag);
        Assert.Equal(8, probe.Count);
        Assert.Equal(0.25f, probe.Level);
        Assert.Equal("renamed", probe.Name);
        Assert.Equal(new Vector3(1, 2, 3), probe.Position);
        Assert.Equal(new Vector3(2f, 0.5f, 0.25f), probe.Tint);          // above one stays
        Assert.True(Quaternion.Dot(Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0), probe.Turn) > 0.9999f);
        Assert.Equal(ProbeGait.Run, probe.Gait);
        Assert.Equal(ProbeParts.Head | ProbeParts.Arm, probe.Parts);
        Assert.Equal(new Entity(0x100000005), probe.Target);

        // The next frame shows what was written, and the entity by name.
        rig.Frame(probe);
        Assert.Equal("Hero", rig.Widget("entity", "target").Text);
        Assert.Equal("run", rig.Widget("combo", "gait").Text);
        Assert.Equal("renamed", rig.Widget("text", "name").Text);

        // Each property was one step, named after it; undoing them all puts everything back.
        Assert.Equal(10, rig.History!.Steps.Count);
        Assert.Contains("Set InspectProbe.count", rig.History.Steps);
        while (rig.History.Undo())
        {
        }

        Assert.False(probe.Flag);
        Assert.Equal(3, probe.Count);
        Assert.Equal("probe", probe.Name);
        Assert.Equal(ProbeGait.Idle, probe.Gait);
        Assert.Equal(ProbeParts.Head, probe.Parts);
        Assert.Equal(new Entity(0), probe.Target);
    }

    [Fact]
    public void ReadOnlyPropertiesAreNotWritten()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        rig.Ui.DragTo["id"] = [99];
        rig.Ui.DragTo["locked"] = [99];
        rig.Frame(probe);

        // Shown, greyed out, and the script's try to change them is ignored (no setter; a read-only hint).
        Assert.True(rig.Widget("drag", "id").Disabled);
        Assert.True(rig.Widget("drag", "locked").Disabled);
        Assert.False(rig.Widget("drag", "count").Disabled);
        Assert.Equal(7, probe.Id);
        Assert.Equal(2f, probe.Locked);
        Assert.False(rig.History!.CanUndo);

        // A hidden property is not shown at all.
        Assert.False(rig.Has("drag", "secret"));

        // A whole inspector can be read-only too (the window of a game that is not to be touched).
        rig.Ui.DragTo["count"] = [9];
        Assert.False(rig.Frame(probe, locked: true));
        Assert.Equal(3, probe.Count);
        Assert.False(rig.History.CanUndo);
    }

    [Fact]
    public void CyclesStop()
    {
        var rig = new ObjectRig();
        var a = new InspectProbeChild { Label = "a" };
        var b = new InspectProbeChild { Label = "b" };
        a.Next = b;
        b.Next = a;
        rig.Frame(a);

        // a shows b, b shows a again as a name only.
        Assert.Equal(1, rig.Count("node"));
        Assert.Contains(rig.Texts, t => t.Contains("shown above", StringComparison.Ordinal));

        // An object that holds itself opens nothing.
        var alone = new InspectProbeChild();
        alone.Next = alone;
        rig.Frame(alone);
        Assert.Equal(0, rig.Count("node"));
        Assert.Contains(rig.Texts, t => t.StartsWith("next: InspectProbeChild (shown above", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedObjectsStopAtTheDepthLimit()
    {
        var rig = new ObjectRig();
        var chain = new InspectProbeChild[10];
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] = new InspectProbeChild { Label = "n" + i };
        }

        for (int i = 0; i < chain.Length - 1; i++)
        {
            chain[i].Next = chain[i + 1];
        }

        var probe = new InspectProbe { Child = chain[0] };
        rig.Frame(probe);
        Assert.Equal(ObjectInspector.MaxDepth, rig.Seen.Count(w => w.Kind == "node" && (w.Label == "child" || w.Label.StartsWith("child/", StringComparison.Ordinal))));
        Assert.Contains(rig.Texts, t => t.Contains("not opened", StringComparison.Ordinal));

        // Every level that is open writes to its own object; the one that is not open is untouched.
        rig.Ui.DragTo["amount"] = [5];
        rig.Frame(probe);
        Assert.Equal([5, 5, 5, 5, 0], chain.Take(5).Select(c => c.Amount));
    }

    [Fact]
    public void PropertiesOfBaseClassesComeFirstAndCategoriesAreGrouped()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        rig.Frame(probe);

        List<string> order = [.. rig.Seen.Select(w => w.Label)];
        Assert.True(order.IndexOf("shared") < order.IndexOf("flag"));           // the base class's first
        Assert.Equal(["Identity"], rig.Headings);
        Assert.True(order.IndexOf("title") > order.IndexOf("ticking"));         // categorised ones follow the rest, together
        Assert.Equal(order.IndexOf("title") + 1, order.IndexOf("title2"));
        Assert.Equal("base title", rig.Widget("text", "title").Text);
    }

    [Fact]
    public void TextIsOneLineUntilItIsMarkedOrLongOrHasLineBreaks()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        rig.Frame(probe);
        Assert.Equal("line", rig.Widget("text", "notes").Format);

        rig.Table.RegisterMultiline("InspectProbe", "notes");
        rig.Frame(probe);
        Assert.Equal("multiline", rig.Widget("text", "notes").Format);
        Assert.Equal("line", rig.Widget("text", "name").Format);

        // A base class's registration reaches its subclasses.
        rig.Table.RegisterMultiline("InspectProbeBase", "title");
        rig.Frame(probe);
        Assert.Equal("multiline", rig.Widget("text", "title").Format);

        // Text that already has several lines, or is long, gets the box; it stays a box when it is cut short while it is typed in.
        probe.Name = "one\ntwo";
        rig.Frame(probe);
        Assert.Equal("multiline", rig.Widget("text", "name").Format);
        probe.Name = "short";
        rig.Frame(probe);
        Assert.Equal("multiline", rig.Widget("text", "name").Format);
        probe.Title2 = new string('x', 300);
        rig.Frame(probe);
        Assert.Equal("multiline", rig.Widget("text", "title2").Format);
    }

    [Fact]
    public void ATextTooLongToEditIsShownCutAndNotEdited()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe { Notes = new string('z', ObjectInspector.MaxEditableText + 1) };
        rig.Ui.TextTo["notes"] = "changed";
        rig.Frame(probe);
        Assert.False(rig.Has("text", "notes"));
        Assert.Contains(rig.Texts, t => t.StartsWith("notes: (", StringComparison.Ordinal) && t.Contains("too long to edit", StringComparison.Ordinal) && t.Length < 200);
        Assert.Equal(ObjectInspector.MaxEditableText + 1, probe.Notes.Length);
    }

    [Fact]
    public void EnumsAreCombosAndAValueThatIsNoMemberShowsItsNumber()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe { Gait = (ProbeGait)9, Parts = ProbeParts.Head | ProbeParts.Leg };
        rig.Frame(probe);
        DrawnWidget combo = rig.Widget("combo", "gait");
        Assert.Equal("9", combo.Text);
        Assert.Equal(-1, combo.Selected);
        Assert.Equal("parts: head|leg", rig.Widget("node", "parts").Text);

        // Picking the member it already is does nothing; a flag checkbox turned off clears its bit only.
        probe.Gait = ProbeGait.Walk;
        rig.Ui.PickIndex["gait"] = 1;
        rig.Ui.ToggleTo["head"] = false;
        rig.Frame(probe);
        Assert.Equal(ProbeGait.Walk, probe.Gait);
        Assert.Equal(ProbeParts.Leg, probe.Parts);
        Assert.Equal(["Set InspectProbe.parts"], rig.History!.Steps);
    }

    [Fact]
    public void AListIsEditedByAddingRemovingMovingAndTyping()
    {
        var rig = new ObjectRig();
        var holder = new InspectProbeHolder { Words = ["a", "b", "c"] };
        rig.Frame(holder);
        Assert.Equal("words [3]", rig.Widget("node", "words").Text);
        Assert.Equal(["a", "b", "c"], Enumerable.Range(0, 3).Select(i => rig.Widget("text", $"[{i}]").Text));
        Assert.True(rig.Widget("button", "words/0/Up").Disabled);
        Assert.True(rig.Widget("button", "words/2/Down").Disabled);

        rig.Ui.Pressed.Add("words/1/Remove");
        rig.Frame(holder);
        Assert.Equal(["a", "c"], holder.Words);

        rig.Ui.Pressed.Add("words/1/Up");
        rig.Frame(holder);
        Assert.Equal(["c", "a"], holder.Words);

        rig.Ui.Pressed.Add("words/Add");
        rig.Frame(holder);
        Assert.Equal(["c", "a", ""], holder.Words);

        rig.Ui.TextTo["[2]"] = "z";
        rig.Frame(holder);
        Assert.Equal(["c", "a", "z"], holder.Words);

        // Four gestures, four steps; each undoes alone.
        Assert.Equal(4, rig.History!.Steps.Count);
        rig.History.Undo();
        Assert.Equal(["c", "a", ""], holder.Words);
        rig.History.Undo();
        rig.History.Undo();
        rig.History.Undo();
        Assert.Equal(["a", "b", "c"], holder.Words);
    }

    [Fact]
    public void AddingToAListOfEnumsOrNumbersUsesTheTypesZero()
    {
        var rig = new ObjectRig(withHistory: false);
        var holder = new InspectProbeHolder();
        rig.Ui.Pressed.Add("gaits/Add");
        rig.Frame(holder);
        Assert.Equal([ProbeGait.Idle], holder.Gaits);

        var probe = new InspectProbe();
        rig.Ui.Pressed.Add("numbers/Add");
        rig.Frame(probe);
        rig.Ui.Pressed.Add("numbers/Add");
        rig.Frame(probe);
        Assert.Equal([0, 0], probe.Numbers);

        // A number in the list is edited like a number.
        rig.Ui.DragTo["[1]"] = [7];
        rig.Frame(probe);
        Assert.Equal([0, 7], probe.Numbers);
    }

    [Fact]
    public void AHugeListIsDrawnAPageAtATime()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe { Numbers = [.. Enumerable.Range(0, 1000)] };
        rig.Frame(probe);
        Assert.Equal("numbers [1000]", rig.Widget("node", "numbers").Text);
        Assert.Equal(ObjectInspector.PageSize, rig.Seen.Count(w => w.Kind == "drag" && w.Label.StartsWith('[')));
        Assert.True(rig.Has("drag", "[199]"));
        Assert.False(rig.Has("drag", "[200]"));
        Assert.Contains(rig.Texts, t => t.Contains("1 to 200 of 1000", StringComparison.Ordinal));
        Assert.True(rig.Widget("button", "numbers/Previous page").Disabled);

        rig.Ui.Pressed.Add("numbers/Next page");
        rig.Frame(probe);
        rig.Frame(probe);
        Assert.True(rig.Has("drag", "[200]"));
        Assert.False(rig.Has("drag", "[199]"));

        // Editing an item of the second page changes that item only.
        rig.Ui.DragTo["[250]"] = [-5];
        rig.Frame(probe);
        Assert.Equal(-5, probe.Numbers[250]);
        Assert.Equal(1000, probe.Numbers.Count);

        // The last page is clamped when the list shrinks under it.
        probe.Numbers = [1, 2, 3];
        rig.Frame(probe);
        Assert.True(rig.Has("drag", "[0]"));
        Assert.False(rig.Has("button", "numbers/Next page"));
    }

    [Fact]
    public void AListTooBigToEditIsOnlyShown()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe { Numbers = [.. Enumerable.Range(0, ObjectInspector.MaxEditableItems + 1)] };
        rig.Ui.Pressed.Add("numbers/Add");
        rig.Ui.DragTo["[0]"] = [9];
        rig.Frame(probe);
        Assert.False(rig.Has("button", "numbers/Add"));
        Assert.False(rig.Has("button", "numbers/0/Remove"));
        Assert.Contains(rig.Texts, t => t.Contains("too many to edit", StringComparison.Ordinal));
        Assert.Equal(0, probe.Numbers[0]);
        Assert.Equal(ObjectInspector.MaxEditableItems + 1, probe.Numbers.Count);
    }

    [Fact]
    public void AListOfObjectsOpensItsItemsAndAnItemThatWasDestroyedIsSaid()
    {
        var rig = new ObjectRig();
        var first = new InspectProbeChild { Label = "first" };
        var second = new InspectProbeChild { Label = "second" };
        var holder = new InspectProbeHolder { Items = [first, second] };
        rig.Frame(holder);
        Assert.Equal("[0]: InspectProbeChild", rig.Widget("node", "items/0").Text);
        Assert.Equal("[1]: InspectProbeChild", rig.Widget("node", "items/1").Text);
        Assert.True(rig.Has("text", "label"));
        Assert.False(rig.Has("button", "items/Add"));         // objects cannot be made here
        Assert.True(rig.Has("button", "items/1/Remove"));

        // An item's own property is written to that item.
        rig.Ui.TextTo["label"] = "renamed";
        rig.Frame(holder);
        Assert.Equal("renamed", first.Label);
        Assert.Equal("renamed", second.Label);

        second.Destroy();
        rig.Frame(holder);
        Assert.Contains(rig.Texts, t => t == "[1]: (destroyed)");

        // Removing the dead item from the list is still possible.
        rig.Ui.Pressed.Add("items/1/Remove");
        rig.Frame(holder);
        Assert.Equal([first], holder.Items);
    }

    [Fact]
    public void AGetterThatThrowsIsShownAndTheRestIsStillDrawn()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        rig.Frame(probe);
        Assert.Contains(rig.Texts, t => t == "broken: (cannot be read: the getter blew up)");
        Assert.True(rig.Has("toggle", "flag"));
        Assert.True(rig.Has("drag", "ticking"));
    }

    [Fact]
    public void ASetterThatRefusesALeavesTheValueAndSaysWhy()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        rig.Ui.DragTo["limited"] = [9];
        Assert.False(rig.Frame(probe));
        Assert.Equal(0, probe.Limited);
        Assert.False(rig.History!.CanUndo);                        // nothing was recorded
        Assert.Equal(["too big: at most 5"], rig.Errors);
        Assert.Equal("too big: at most 5", rig.Inspector.Errors["limited"]);

        // The message stays under the property until it is written successfully.
        rig.Frame(probe);
        Assert.Equal(["too big: at most 5"], rig.Errors);
        rig.Ui.DragTo["limited"] = [4];
        Assert.True(rig.Frame(probe));
        Assert.Equal(4, probe.Limited);
        Assert.Empty(rig.Errors);
        Assert.Empty(rig.Inspector.Errors);
        Assert.Equal(["Set InspectProbe.limited"], rig.History.Steps);

        // A refused value in the middle of a drag keeps the part of the drag that was accepted as one step.
        rig.Ui.DragTo["limited"] = [3];
        rig.Ui.HeldOn.Add("limited");
        rig.Frame(probe);
        rig.Ui.DragTo["limited"] = [8];
        rig.Ui.HeldOn.Add("limited");
        rig.Frame(probe);
        rig.Ui.DragTo["limited"] = [2];
        rig.Ui.ReleasedOn.Add("limited");
        rig.Frame(probe);
        Assert.Equal(2, probe.Limited);
        Assert.Equal(2, rig.History.Steps.Count);
        rig.History.Undo();
        Assert.Equal(4, probe.Limited);
    }

    [Fact]
    public void WithoutAHistoryEditsAreWrittenStraightToTheSetter()
    {
        var rig = new ObjectRig(withHistory: false);
        var probe = new InspectProbe();
        rig.Ui.DragTo["count"] = [6];
        rig.Ui.DragTo["limited"] = [9];
        Assert.True(rig.Frame(probe));
        Assert.Equal(6, probe.Count);
        Assert.Equal(0, probe.Limited);
        Assert.Equal(["too big: at most 5"], rig.Errors);
        Assert.Null(rig.Inspector.History);
    }

    [Fact]
    public void AnObjectDestroyedWhileInspectedIsSaidAndItsHistoryDoesNotThrow()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        rig.Ui.DragTo["count"] = [6];
        rig.Frame(probe);
        Assert.Equal(6, probe.Count);

        probe.Destroy();
        rig.Frame(probe);
        Assert.Equal(["(destroyed)"], rig.Texts);
        Assert.Empty(rig.Seen);

        // Undo and redo of a step on an object that is gone have nothing to do.
        Assert.True(rig.History!.Undo());
        Assert.True(rig.History.Redo());

        // Writing through the inspector to it is refused with a message and no step.
        PropertyDef count = probe.Class.FindProperty("count")!;
        Assert.False(rig.Inspector.SetProperty(probe, count, Value.Int(6), Value.Int(1), "count"));
        Assert.Equal("The object was destroyed.", rig.Inspector.Errors["count"]);
    }

    [Fact]
    public void APropertyThatChangesByItselfEveryFrameIsNotAnEdit()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        var shown = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            Assert.False(rig.Frame(probe));
            shown.Add(rig.Widget("drag", "ticking").Numbers[0]);
        }

        Assert.Equal(4, shown.Distinct().Count());                  // it is read every frame
        Assert.False(rig.History!.CanUndo);                        // and nothing was written because of it

        // An edit of something else in between is still a clean step.
        rig.Ui.DragTo["count"] = [6];
        Assert.True(rig.Frame(probe));
        Assert.Equal(["Set InspectProbe.count"], rig.History.Steps);
    }

    [Fact]
    public void TheFramesOfOneDragAreOneStep()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        foreach (double value in DragFrames)
        {
            rig.Ui.DragTo["count"] = [value];
            rig.Ui.HeldOn.Add("count");
            rig.Frame(probe);
        }

        rig.Ui.DragTo["count"] = [8];
        rig.Ui.ReleasedOn.Add("count");
        rig.Frame(probe);
        Assert.Equal(8, probe.Count);
        Assert.Single(rig.History!.Steps);

        // The next drag is a step of its own.
        rig.Ui.DragTo["count"] = [2];
        rig.Ui.ReleasedOn.Add("count");
        rig.Frame(probe);
        Assert.Equal(2, rig.History.Steps.Count);

        rig.History.Undo();
        Assert.Equal(8, probe.Count);
        rig.History.Undo();
        Assert.Equal(3, probe.Count);                               // what it was before the first drag
    }

    [Fact]
    public void MissingValuesAreSaidAndANullableNumberCanBeGivenOne()
    {
        var rig = new ObjectRig();
        var probe = new InspectProbe();
        rig.Frame(probe);
        Assert.Contains(rig.Texts, t => t == "maybe: none");
        Assert.Contains(rig.Texts, t => t == "missing: none");
        Assert.True(rig.Has("button", "maybe/Set"));
        Assert.False(rig.Has("button", "missing/Set"));       // an object cannot be made here

        rig.Ui.Pressed.Add("maybe/Set");
        rig.Frame(probe);
        Assert.Equal(0, probe.Maybe);
        rig.Frame(probe);
        Assert.True(rig.Has("drag", "maybe"));
    }

    [Fact]
    public void ADrawerRegisteredForAClassReplacesTheDefaultAndCanFallBackToIt()
    {
        var rig = new ObjectRig();
        rig.Table.RegisterClass("InspectProbeBase", (in ObjectEdit edit) =>
        {
            edit.Ui.Text($"custom {edit.Class.Name} at '{edit.Path}'");
            return edit.DrawProperties();
        });
        var child = new InspectProbeChild();
        var probe = new InspectProbe { Child = child };
        rig.Ui.DragTo["count"] = [6];
        Assert.True(rig.Frame(probe));

        // The drawer of the base class is used for a subclass, and DrawProperties keeps the rest.
        Assert.Contains("custom InspectProbe at ''", rig.Texts);
        Assert.Equal(6, probe.Count);
        Assert.True(rig.Has("toggle", "flag"));

        // A drawer of the class itself wins over a base class's, and a drawer that draws nothing of the properties hides them.
        rig.Table.RegisterClass("InspectProbeChild", (in ObjectEdit edit) =>
        {
            edit.Ui.Text($"child drawer depth {edit.Depth} at '{edit.Path}'");
            return false;
        });
        rig.Frame(probe);
        Assert.Contains("child drawer depth 1 at 'child'", rig.Texts);
        Assert.False(rig.Has("text", "label"));

        // A drawer that throws is reported where it stands, and the rest of the inspector goes on.
        rig.Table.RegisterClass("InspectProbeChild", (in ObjectEdit edit) => throw new InvalidOperationException("drawer failed"));
        rig.Frame(probe);
        Assert.Contains(rig.Errors, e => e.Contains("drawer failed", StringComparison.Ordinal));
        Assert.True(rig.Has("toggle", "flag"));
    }

    [Fact]
    public void TheSharedTableTakesRegistrationsThroughTheStaticHook()
    {
        // The editor's own table: a registration is found for the class and its subclasses and for no other. (It stays registered: nothing but this test draws a holder with the shared table.)
        var holder = new InspectProbeHolder();
        Assert.Null(InspectorDrawers.Shared.FindClass(holder.Class));
        InspectorDrawers.RegisterClass("InspectProbeHolder", (in ObjectEdit edit) => edit.DrawProperties());
        Assert.NotNull(InspectorDrawers.Shared.FindClass(holder.Class));
        Assert.Null(InspectorDrawers.Shared.FindClass(new InspectProbeChild().Class));
        InspectorDrawers.RegisterMultiline("InspectProbeHolder", "words");
        Assert.True(InspectorDrawers.Shared.IsMultiline(holder.Class, "words"));
        Assert.False(InspectorDrawers.Shared.IsMultiline(holder.Class, "items"));
    }
}
