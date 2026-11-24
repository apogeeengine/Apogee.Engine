using System;
using Weft.Host.Editing;
using Weft.Reflection;
using Xunit;

namespace Weft.Host.Tests;

/// <summary>An object with a number, a text and a setter that refuses some values.</summary>
[WeftClass]
public partial class CommandProbe : EngineObject
{
    private int _small;

    [Property]
    public int Count { get; set; }

    [Property]
    public string Label { get; set; } = "";

    [Property]
    public int Small
    {
        get => _small;
        set => _small = value > 9 ? throw new ValueException("too big") : value;
    }

    [Property]
    public int Fixed { get; } = 1;
}

/// <summary>The command that sets a registered property of an engine object: apply, undo, merge, and what happens to the object it targets.</summary>
public sealed class SetPropertyCommandTests
{
    private static CommandProbe Probe()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(CommandProbe).Module.ModuleHandle);
        return new CommandProbe();
    }

    private static PropertyDef Property(CommandProbe probe, string name) => probe.Class.FindProperty(name)!;

    [Fact]
    public void ApplyAndUndoCallTheSetterAndTheStepIsNamedAfterThePropertyAndTheClass()
    {
        CommandProbe probe = Probe();
        var history = new EditHistory();
        history.Execute(new SetPropertyCommand(probe, Property(probe, "count"), Value.Int(0), Value.Int(5)));
        Assert.Equal(5, probe.Count);
        Assert.Equal("Set CommandProbe.count", history.UndoName);

        history.Undo();
        Assert.Equal(0, probe.Count);
        history.Redo();
        Assert.Equal(5, probe.Count);
        Assert.Equal("Named", new SetPropertyCommand(probe, Property(probe, "count"), Value.Int(0), Value.Int(1), "Named").Name);
    }

    [Fact]
    public void FollowingSetsOfTheSamePropertyMergeUntilTheGestureIsSealed()
    {
        CommandProbe probe = Probe();
        var history = new EditHistory();
        PropertyDef count = Property(probe, "count");
        history.Execute(new SetPropertyCommand(probe, count, Value.Int(0), Value.Int(1)));
        history.Execute(new SetPropertyCommand(probe, count, Value.Int(1), Value.Int(2)));
        history.Execute(new SetPropertyCommand(probe, count, Value.Int(2), Value.Int(3)));
        Assert.Single(history.Steps);
        history.Undo();
        Assert.Equal(0, probe.Count);                                // the value before the first of them

        history.Redo();
        Assert.Equal(3, probe.Count);                                // and the last one after
        history.Seal();
        history.Execute(new SetPropertyCommand(probe, count, Value.Int(3), Value.Int(4)));
        Assert.Equal(2, history.Steps.Count);

        // Another property, or another object, is a step of its own.
        history.Execute(new SetPropertyCommand(probe, Property(probe, "label"), Value.String(""), Value.String("x")));
        CommandProbe other = Probe();
        history.Execute(new SetPropertyCommand(other, Property(other, "label"), Value.String(""), Value.String("y")));
        Assert.Equal(4, history.Steps.Count);
    }

    [Fact]
    public void ASetterThatThrowsRecordsNothing()
    {
        CommandProbe probe = Probe();
        var history = new EditHistory();
        history.Execute(new SetPropertyCommand(probe, Property(probe, "small"), Value.Int(0), Value.Int(4)));
        history.Seal();
        ValueException refused = Assert.Throws<ValueException>(() => history.Execute(new SetPropertyCommand(probe, Property(probe, "small"), Value.Int(4), Value.Int(40))));
        Assert.Equal("too big", refused.Message);
        Assert.Equal(4, probe.Small);
        Assert.Single(history.Steps);
    }

    [Fact]
    public void AnObjectThatIsGoneIsLeftAloneByUndoAndRedo()
    {
        CommandProbe probe = Probe();
        var history = new EditHistory();
        history.Execute(new SetPropertyCommand(probe, Property(probe, "count"), Value.Int(0), Value.Int(5)));
        probe.Destroy();
        Assert.True(history.Undo());
        Assert.True(history.Redo());
        Assert.Equal(5, probe.Count);
    }

    [Fact]
    public void AReadOnlyPropertyCannotBeTheTarget()
    {
        CommandProbe probe = Probe();
        Assert.Throws<ArgumentException>(() => new SetPropertyCommand(probe, Property(probe, "fixed"), Value.Int(1), Value.Int(2)));
        Assert.Throws<ArgumentNullException>(() => new SetPropertyCommand(null!, Property(probe, "count"), Value.Int(1), Value.Int(2)));
    }
}
