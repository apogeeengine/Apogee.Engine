using System;
using Apogee.Reflection;

namespace Apogee.Host.Editing;

/// <summary>
/// Sets one registered property of an <see cref="EngineObject"/> to a value and puts the old one back on undo, through the same setter scripts use (no reflection). The frames of one drag are one step:
/// a following command that sets the same property of the same object folds its value in (see <see cref="TryMerge"/>). An object that has been destroyed meanwhile is left alone by undo and redo (there
/// is nothing to put back), so a history that outlives its objects does not throw. A setter that throws on <see cref="Apply"/> leaves the exception to the caller: <see cref="EditHistory.Execute"/>
/// records nothing in that case.
/// </summary>
public sealed class SetPropertyCommand : IEditCommand
{
    private readonly string? _name;
    private Value _after;

    /// <summary>Sets <paramref name="property"/> of <paramref name="target"/> to <paramref name="after"/>; <paramref name="before"/> is what undo puts back and <paramref name="name"/> what the Edit menu says (<c>Set Class.property</c> by default).</summary>
    public SetPropertyCommand(EngineObject target, PropertyDef property, Value before, Value after, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (property.Setter is null)
        {
            throw new ArgumentException("the property has no setter", nameof(property));
        }

        Target = target;
        Property = property;
        Before = before;
        _after = after;
        _name = name;
    }

    /// <summary>The object that is edited.</summary>
    public EngineObject Target { get; }

    /// <summary>The property that is set.</summary>
    public PropertyDef Property { get; }

    /// <summary>The value undo puts back.</summary>
    public Value Before { get; }

    /// <summary>The value this command sets (the last one, after merges).</summary>
    public Value After => _after;

    /// <inheritdoc />
    public string Name => _name ?? $"Set {Target.Class.Name}.{Property.ScriptName}";

    /// <inheritdoc />
    public void Apply()
    {
        if (!Target.IsDestroyed)
        {
            Property.Setter!(Target, in _after);
        }
    }

    /// <inheritdoc />
    public void Undo()
    {
        if (!Target.IsDestroyed)
        {
            Value back = Before;
            Property.Setter!(Target, in back);
        }
    }

    /// <inheritdoc />
    public bool TryMerge(IEditCommand following)
    {
        if (following is not SetPropertyCommand other || !ReferenceEquals(other.Target, Target) || !ReferenceEquals(other.Property, Property))
        {
            return false;
        }

        _after = other._after;
        return true;
    }
}
