using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// A seeded fuzz of what saving a scene with prefab instances promises: from two corners (each with two lamps, one of them with an override of its own and an entity added to it), a lamp, a player and a
/// switch, a few random edits (odd numbers, references, components, names, deletions, new entities, copies, instances, reused entity ids, snapshots, an entity moved by code) are made; the scene is
/// saved, loaded into another world, and that world is the same as the first and saves the same text. A second variant breaks the prefab in between, and the scene it saves still gives everything back
/// when the prefab is mended.
/// </summary>
public sealed class PrefabFuzzTests : PrefabTestBase
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 600).Select(seed => new object[] { seed });

    private static readonly float[] Floats = [0f, -0f, 1f, 1.5f, -2.5f, float.NaN, 1e-45f, 3.4e38f, 1e7f, 0.1f, float.NegativeInfinity];
    private static readonly double[] Doubles = [0, -0.0, 1e7, 1e-300, 123456.789, -3, 0.5, 1e12];
    private int _names;

    private string Base() => $"""
        <Scene version="2">
          <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          <Entity id="2" name="CornerA" prefab="{Corner}">
            <Transform Position="10 0 4"/>
            <Override entity="2/2"><Health Current="1"/><Flicker lamp="1"/></Override>
            <Override entity="2/3"><Entity id="3" name="Sign"><Transform Position="0 0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/><Marks Target="2"/></Entity></Override>
            <Entity id="4" name="Trash can"><Transform Position="5 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          </Entity>
          <Entity id="5" name="CornerB" prefab="{Corner}"><Override entity="4" name="Old bench"><Frozen append="true"/></Override></Entity>
          <Entity id="6" name="Lamp" prefab="{Lamp}"><Override entity="2"><Marks Level="7" Target="1"/></Override></Entity>
          <Entity id="7" name="Switch"><Marks Target="2/2"/></Entity>
        </Scene>
        """;

    // ---- The edits

    private string NewName() => $"n{_names++}";

    private static T Pick<T>(Random random, IReadOnlyList<T> from) => from[random.Next(from.Count)];

    private static Quaternion OddRotation(Random random) => random.Next(3) switch
    {
        0 => new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
        1 => new Quaternion(0, 0, 0, 0),
        _ => new Quaternion(Pick(random, Floats), 0, 0, 1),
    };

    private void Edit(World world, Random random, ref WorldSnapshot? held)
    {
        List<Entity> all = world.UserEntities();
        if (all.Count == 0)
        {
            return;
        }

        Entity target = Pick(random, all);
        Entity script = world.FindComponent("Flicker");
        switch (random.Next(15))
        {
            case 0:
                world.Set(target, new Transform(new Double3(Pick(random, Doubles), Pick(random, Doubles), Pick(random, Doubles)), OddRotation(random), new Vector3(Pick(random, Floats), 1, 1)));
                break;
            case 1:
                world.Set(target, new Health { Current = random.Next(-5, 100), Max = random.Next(100), Regen = Pick(random, Floats) });
                break;
            case 2:
                world.Set(target, new Marks { Alive = random.Next(2) == 0, Level = (byte)random.Next(256), Mass = Pick(random, Doubles), Target = world.Has<Marks>(target) ? world.Get<Marks>(target).Target : default });
                break;
            case 3:
                // An entity field: another live entity, or none.
                if (world.Has<Marks>(target))
                {
                    Marks marks = world.Get<Marks>(target);
                    marks.Target = random.Next(4) == 0 ? default : Pick(random, all);
                    world.Set(target, marks);
                }

                break;
            case 4:
                world.Add<Frozen>(target);
                break;
            case 5:
                world.Remove<Frozen>(target);
                break;
            case 6:
                world.Remove<Health>(target);
                break;
            case 7:
                world.Remove<Marks>(target);
                break;
            case 8:
                if (!script.IsNull)
                {
                    if (random.Next(2) == 0)
                    {
                        world.Add(target, script);
                    }
                    else
                    {
                        world.Remove(target, script);
                    }
                }

                break;
            case 9:
                world.SetName(target, NewName());
                break;
            case 10:
                if (all.Count > 3)
                {
                    world.Destroy(target);
                }

                break;
            case 11:
                Entity made = world.CreateEntity(NewName(), random.Next(3) == 0 ? default : target);
                world.Set(made, Transform.At(new Double3(Pick(random, Doubles), 0, 0)));
                if (random.Next(2) == 0)
                {
                    world.Set(made, new Marks { Target = Pick(random, all) });
                }

                break;
            case 12:
                world.Clone(target);
                break;
            case 13:
                world.Instantiate(random.Next(2) == 0 ? LampPath : CornerPath, random.Next(3) == 0 ? default : target);
                break;
            default:
                switch (random.Next(4))
                {
                    case 0:
                        held = world.Snapshot();
                        break;
                    case 1:
                        if (held is not null)
                        {
                            world.Restore(held);
                        }

                        break;
                    case 2:
                        // Entity ids that were freed come back with another generation: ids stop being in creation order.
                        world.Destroy(world.CreateEntity(NewName()));
                        break;
                    default:
                        // A member moved by code (the editor does not allow it).
                        Entity under = Pick(random, all);
                        if (world.IsPrefabMember(target) && !world.Subtree(target).Contains(under) && under != world.GetParent(target) && world.Children(under).All(c => world.GetName(c) != world.GetName(target)))
                        {
                            world.SetParent(target, under);
                        }

                        break;
                }

                break;
        }
    }

    private void Edits(World world, Random random, int count)
    {
        WorldSnapshot? held = null;
        for (int i = 0; i < count; i++)
        {
            Edit(world, random, ref held);
        }
    }

    // ---- What a world is

    private static string PathOf(World world, Entity entity)
    {
        var names = new Stack<string>();
        for (Entity current = entity; !current.IsNull; current = world.GetParent(current))
        {
            names.Push(world.GetName(current) ?? "?");
        }

        return string.Join('/', names);
    }

    // Every entity by its place in the hierarchy (siblings by name: their order is not something a scene keeps), with its components and every field as text; an entity field is the path of what it names.
    private static unsafe string Dump(World world)
    {
        var text = new StringBuilder();
        void Write(Entity entity, int depth)
        {
            string indent = new(' ', depth * 2);
            text.Append(indent).Append(world.GetName(entity) ?? "-").Append(world.IsPrefabInstance(entity) ? " [instance]" : "").Append('\n');
            foreach ((Entity id, ComponentInfo info) in world.ComponentsOf(entity).OrderBy(c => c.Info.Name, StringComparer.Ordinal))
            {
                if (world.IsSceneSkipped(info.Name))
                {
                    continue;
                }

                text.Append(indent).Append("  ").Append(info.Name);
                if (!info.IsTag)
                {
                    byte* data = world.GetPointer(entity, id);
                    foreach (FieldDef field in info.Fields)
                    {
                        string value = field.Kind switch
                        {
                            FieldKind.Entity => *(ulong*)(data + field.Offset) is var target && target != 0 && world.IsAlive(new Entity(target)) ? PathOf(world, new Entity(target)) : "-",
                            FieldKind.AssetRef => (*(uint*)(data + field.Offset)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            _ => FieldText.Format(field, data + field.Offset),
                        };
                        text.Append(' ').Append(field.Name).Append('=').Append(value);
                    }
                }

                text.Append('\n');
            }

            foreach (Entity child in world.Children(entity).OrderBy(c => world.GetName(c), StringComparer.Ordinal))
            {
                Write(child, depth + 1);
            }
        }

        foreach (Entity root in world.Roots().OrderBy(e => world.GetName(e), StringComparer.Ordinal))
        {
            Write(root, 0);
        }

        return text.ToString();
    }

    // How many edits a seed makes: from one to a few dozen, and the last seeds many more (a sequence that is long enough shows what a short one does not).
    private static int EditCount(int seed) => 1 + (seed % 24) + (seed / 50 * 20);

    // Two entities with one parent never have one name, and each is found by its name where it is (a scene cannot say two of one name, and Flecs loses one of them).
    private static void AssertNamesAreUnique(World world)
    {
        foreach (Entity entity in world.UserEntities())
        {
            if (world.GetName(entity) is { } name)
            {
                Entity parent = world.GetParent(entity);
                Assert.Equal(1, (parent.IsNull ? world.Roots() : world.Children(parent)).Count(c => world.GetName(c) == name));
                Assert.Equal(name, world.UniqueName(name, parent, entity));
            }
        }
    }

    // ---- The two promises

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ASceneEditedAtRandomSavesLoadsAndSavesTheSame(int seed)
    {
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        string saved = World.SaveScene();

        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Same(Dump(World), Dump(loaded));
        Same(saved, loaded.SaveScene());
    }

    private static void Same(string expected, string actual) => Assert.Equal(expected, actual);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ASceneSavedWhileItsPrefabIsBrokenGivesEverythingBackWhenItIsMended(int seed)
    {
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        string saved = World.SaveScene();
        string mended = Read("prefabs/lamp.aprefab");

        // The lamp loses its Base and the Bulb its Health, and the scene is loaded, edited a little (in the part that is not the lamp, in both worlds), and saved again against that.
        Write("prefabs/lamp.aprefab", mended
            .Replace("<Entity id=\"3\" name=\"Base\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>", "", StringComparison.Ordinal)
            .Replace("<Health Current=\"5\" Max=\"9\" Regen=\"0.5\"/>", "", StringComparison.Ordinal));
        using World broken = OtherWorld();
        broken.LoadScene(saved);
        EditThePartThatIsNotALamp(World, seed);
        EditThePartThatIsNotALamp(broken, seed);
        string whileBroken = broken.SaveScene();

        // The same text from the same world is the same text again.
        using World again = OtherWorld();
        again.LoadScene(whileBroken);
        Same(whileBroken, again.SaveScene());

        Write("prefabs/lamp.aprefab", mended);
        using World back = OtherWorld();
        back.LoadScene(whileBroken);
        Same(Dump(World), Dump(back));
    }

    private static void EditThePartThatIsNotALamp(World world, int seed)
    {
        Entity player = world.Lookup("Player");
        if (!player.IsNull)
        {
            world.Set(player, Transform.At(new Double3(seed, -0.0, 1e7)));
        }

        Entity switchEntity = world.Lookup("Switch");
        if (!switchEntity.IsNull)
        {
            world.Add<Frozen>(switchEntity);
        }
    }
}
