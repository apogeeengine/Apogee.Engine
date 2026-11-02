namespace Apogee.Reflection;

/// <summary>An ECS entity or component id. Its meaning is the world's; the registry only carries it.</summary>
public readonly record struct Entity(ulong Id)
{
    public bool IsNull => Id == 0;

    public override string ToString() => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
