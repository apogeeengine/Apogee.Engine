using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Weft.Content.Xml;
using Weft.Core.Logging;
using Weft.Ecs;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Ecs.Tests;

/// <summary>
/// The prefab expander on XML alone (no world): nesting, overrides by address, scopes, stale overrides kept aside, cycles, the depth limit and the entity budget, with hostile documents.
/// </summary>
public sealed class PrefabExpanderTests : IDisposable
{
    private sealed class Sink : ILogSink
    {
        public List<string> Warnings { get; } = [];

        public void Write(in LogEntry entry)
        {
            if (entry.Level == LogLevel.Warning)
            {
                lock (Warnings)
                {
                    Warnings.Add(entry.Message);
                }
            }
        }
    }

    private const string Lamp = "res://prefabs/lamp.aprefab";
    private const string Corner = "res://prefabs/corner.aprefab";

    private static readonly string[] UnusableNames = ["dups", "two", "notprefab", "newer", "variant"];

    private readonly Sink _sink = new();
    private readonly Dictionary<string, XmlNode> _docs = new(StringComparer.Ordinal);

    public PrefabExpanderTests()
    {
        Log.AddSink(_sink);
        Add(Lamp, """
            <Prefab version="1" nextId="4">
              <Components>
                <Component name="Flicker"><Field name="speed" kind="Float32"/><Field name="lamp" kind="Entity"/></Component>
              </Components>
              <Entity id="1" name="Lamp">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Entity id="2" name="Bulb">
                  <Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                  <Light Color="1 0.9 0.7" Intensity="80" Range="12"/>
                  <Flicker speed="2" lamp="1"/>
                </Entity>
                <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
            </Prefab>
            """);
        Add(Corner, """
            <Prefab version="1" nextId="5">
              <Entity id="1" name="Corner">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Entity id="2" name="Lamp" prefab="res://prefabs/lamp.aprefab">
                  <Transform Position="2 0 0"/>
                  <Override entity="2"><Light Intensity="120"/></Override>
                </Entity>
                <Entity id="3" name="Lamp 2" prefab="res://prefabs/lamp.aprefab">
                  <Transform Position="-2 0 0"/>
                  <Override entity="3" remove="true"/>
                </Entity>
                <Entity id="4" name="Bench"><Transform Position="0 0 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
            </Prefab>
            """);
    }

    public void Dispose()
    {
        Log.RemoveSink(_sink);
        GC.SuppressFinalize(this);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------------------

    private void Add(string path, string xml) => _docs[path] = XmlNode.Parse(xml, path);

    private ResolvedPrefab Resolve(string reference) =>
        _docs.TryGetValue(reference, out XmlNode? document) ? new ResolvedPrefab(reference, document) : new ResolvedPrefab(reference, null, "there is no such file");

    private PrefabExpander Expander(ExpansionBudget? budget = null, Func<string, string, bool>? isEntityField = null) =>
        new(Resolve, isEntityField ?? ((_, _) => false), budget ?? new ExpansionBudget());

    private Expansion Expand(string instance, string address = "1", ExpansionBudget? budget = null, Func<string, string, bool>? isEntityField = null) =>
        Expander(budget, isEntityField).Expand(XmlNode.Parse(instance, "scene"), address);

    private static string Instance(string prefab, string body = "", string address = "1") => $"<Entity id=\"{address.Split('/')[^1]}\" prefab=\"{prefab}\">{body}</Entity>";

    private static string? Attr(ExpandedEntity? entity, string component, string attribute) => entity?.Component(component)?.Attribute(attribute);

    private static ExpandedEntity At(Expansion expansion, string address) =>
        expansion.Index.TryGetValue(address, out ExpandedEntity? entity) ? entity : throw new Xunit.Sdk.XunitException($"no entity at {address}: {string.Join(", ", expansion.Index.Keys.Order())}");

    private static string Dump(ExpandedEntity entity)
    {
        var text = new StringBuilder();
        foreach (ExpandedEntity each in entity.Subtree())
        {
            text.Append(new string(' ', each.Depth * 2))
                .Append(each.Name ?? "-").Append(" @").Append(each.Address)
                .Append(" member=").Append(each.MemberId).Append('/').Append(each.MemberPrefab)
                .Append(" instance=").Append(each.InstancePrefab).Append('/').Append(each.InstanceRootId)
                .Append(each.IsMissing ? " MISSING" : "")
                .Append(" [").AppendJoin(' ', each.Components.Select(c => c.ToXml())).Append(']')
                .Append(" unapplied=[").AppendJoin(' ', each.Unapplied.Select(u => u.ToXml())).Append("]\n");
        }

        return text.ToString();
    }

    private bool Warned(string text)
    {
        lock (_sink.Warnings)
        {
            return _sink.Warnings.Any(w => w.Contains(text, StringComparison.Ordinal));
        }
    }

    // ---- Addresses -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1", true)]
    [InlineData("4/2", true)]
    [InlineData("4/3/2", true)]
    [InlineData("2147483647", true)]
    [InlineData("1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17", true)]
    [InlineData("1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17/18", false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("02", false)]
    [InlineData("-1", false)]
    [InlineData("+1", false)]
    [InlineData("1/", false)]
    [InlineData("/1", false)]
    [InlineData("1//2", false)]
    [InlineData("1 /2", false)]
    [InlineData("2147483648", false)]
    [InlineData("a", false)]
    [InlineData("~1", false)]
    public void AnAddressIsIdsSeparatedBySlashes(string text, bool valid) => Assert.Equal(valid, EntityAddress.IsValid(text));

    [Fact]
    public void AddressesCompareSegmentByNumberWithTheOuterOneFirst()
    {
        string[] sorted = ["", "1", "2", "2/2", "2/9", "2/10", "10", "10/1"];
        string[] shuffled = [.. sorted.Reverse()];
        Assert.Equal(sorted, shuffled.Order(Comparer<string>.Create(EntityAddress.Compare)));
        Assert.Equal("3/12", EntityAddress.Format([3, 12]));
        Assert.Equal("5", EntityAddress.Combine("", 5));
        Assert.Equal("5/6", EntityAddress.Combine("5", 6));
    }

    // ---- Expansion -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFlatPrefabExpandsWithAddressesUnderTheInstance()
    {
        var budget = new ExpansionBudget();
        Expansion expansion = Expand(Instance(Lamp, address: "7"), "7", budget);

        ExpandedEntity root = expansion.Root;
        Assert.Equal("7", root.Address);
        Assert.Equal(Lamp, root.InstancePrefab);
        Assert.Equal(1, root.InstanceRootId);
        Assert.Null(root.MemberId); // the instance is an entity of a scene, not of a prefab file
        Assert.Equal("Lamp", root.Name);

        Assert.Equal(["7/2", "7/3"], root.Children.Select(c => c.Address));
        Assert.Equal(["Bulb", "Base"], root.Children.Select(c => c.Name));
        Assert.All(root.Children, c => Assert.Equal(Lamp, c.MemberPrefab));
        Assert.Equal([2, 3], root.Children.Select(c => c.MemberId!.Value));
        Assert.All(root.Children, c => Assert.Same(root, c.Parent));
        Assert.Equal("0 1.8 0", Attr(At(expansion, "7/2"), "Transform", "Position"));
        Assert.Same(root, At(expansion, "7/1")); // the root's id in its file is another name for it
        Assert.Equal(3, budget.Used);
        Assert.Single(expansion.Schemas);
        Assert.Equal(root.Subtree().Count(), root.Subtree().Select(e => e.Address).Distinct().Count());
    }

    [Fact]
    public void ARootPatchChangesOnlyTheNamedFields()
    {
        Expansion expansion = Expand(Instance(Lamp, "<Transform Position=\"5 0 0\"/>"));

        XmlNode transform = expansion.Root.Component("Transform")!;
        Assert.Equal("5 0 0", transform.Attribute("Position"));
        Assert.Equal("0 0 0 1", transform.Attribute("Rotation"));
        Assert.Equal("1 1 1", transform.Attribute("Scale"));
        Assert.Equal("0 0 0", _docs[Lamp].Child("Entity")!.Child("Transform")!.Attribute("Position")); // the cached document is not touched
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOverrideFindsAMemberByAddressWhereverItMoved(bool moved)
    {
        // The prefab author puts the latch under the lid in one version and under the root in the other: the override says "entity 3" and finds it either way.
        string latch = "<Entity id=\"3\" name=\"Latch\"><Transform Position=\"0 0 0\"/></Entity>";
        Add("res://prefabs/crate.aprefab", $"""
            <Prefab version="1" nextId="4"><Entity id="1" name="Crate">
              {(moved ? "" : latch)}
              <Entity id="2" name="Lid"><Transform Position="0 1 0"/>{(moved ? latch : "")}</Entity>
            </Entity></Prefab>
            """);

        Expansion expansion = Expand(Instance("res://prefabs/crate.aprefab", "<Override entity=\"3\"><Transform Position=\"1 2 3\"/></Override>", "9"), "9");

        ExpandedEntity found = At(expansion, "9/3");
        Assert.Equal("1 2 3", Attr(found, "Transform", "Position"));
        Assert.Equal(moved ? "9/2" : "9", found.Parent!.Address);
        Assert.Empty(expansion.Root.Unapplied);
    }

    [Fact]
    public void AMemberCanBeRenamed()
    {
        Expansion expansion = Expand(Instance(Lamp, "<Override entity=\"2\" name=\"Glow\"/>"));

        Assert.Equal("Glow", At(expansion, "1/2").Name);
        Assert.Equal("Base", At(expansion, "1/3").Name);
        Assert.Empty(expansion.Root.Unapplied);
    }

    [Fact]
    public void AComponentCanBeAddedAndRemoved()
    {
        Expansion expansion = Expand(Instance(
            Lamp,
            """
            <Override entity="3"><Health Max="5" Current="5" append="true"/></Override>
            <Override entity="2"><Light remove="true"/></Override>
            <Health Max="9" Current="9" append="true"/>
            """));

        ExpandedEntity bulb = At(expansion, "1/2");
        Assert.Null(bulb.Component("Light"));
        Assert.NotNull(bulb.Component("Flicker"));
        Assert.Equal("5", Attr(At(expansion, "1/3"), "Health", "Max"));
        Assert.Equal("9", Attr(expansion.Root, "Health", "Max"));
        Assert.Empty(expansion.Root.Unapplied);
    }

    [Fact]
    public void AnAddedComponentWinsOverOneThePrefabGainedLater()
    {
        string instance = Instance(Lamp, "<Override entity=\"3\"><Health Max=\"50\" append=\"true\"/></Override>");
        Assert.Equal("50", Attr(At(Expand(instance), "1/3"), "Health", "Max"));

        // The prefab gains a Health of its own on the same entity (with a field the instance never wrote): the instance's component replaces it whole.
        Add(Lamp, """
            <Prefab version="1" nextId="4"><Entity id="1" name="Lamp"><Transform Position="0 0 0"/>
              <Entity id="3" name="Base"><Health Max="1" Current="1" Regen="3"/></Entity>
            </Entity></Prefab>
            """);
        ExpandedEntity latest = At(Expand(instance), "1/3");
        Assert.Equal("50", Attr(latest, "Health", "Max"));
        Assert.Null(Attr(latest, "Health", "Regen"));
        Assert.Single(latest.Components, c => c.Name == "Health");
    }

    [Fact]
    public void RemovingAnEntityRemovesItsBranch()
    {
        Add("res://prefabs/tree.aprefab", """
            <Prefab version="1" nextId="5"><Entity id="1" name="Tree">
              <Entity id="2" name="Trunk"><Entity id="3" name="Branch"><Entity id="4" name="Leaf"/></Entity></Entity>
            </Entity></Prefab>
            """);

        Expansion expansion = Expand(Instance("res://prefabs/tree.aprefab", "<Override entity=\"2\" remove=\"true\"/><Override entity=\"3\" name=\"late\"/>"));

        Assert.Equal(["Tree"], expansion.Root.Subtree().Select(e => e.Name));
        Assert.True(At(expansion, "1/4").IsRemoved);
        Assert.Null(At(expansion, "1/2").Parent);
        // An address inside a removed branch finds nothing: the later override is kept, not applied to a ghost.
        Assert.Single(expansion.Root.Unapplied);
        Assert.Equal("late", expansion.Root.Unapplied[0].Attribute("name"));
    }

    [Fact]
    public void AnEntityCanBeAddedUnderAMember()
    {
        Expansion expansion = Expand(Instance(
            Lamp,
            """
            <Override entity="3"><Entity id="9" name="Sign"><Transform Position="0 0.5 0"/><Flicker lamp="1"/></Entity></Override>
            <Entity id="10" name="Trash can"/>
            """));

        ExpandedEntity sign = At(expansion, "9"); // added entities are numbered in the scope of the document that writes them
        Assert.Equal("Base", sign.Parent!.Name);
        Assert.Same(sign, At(expansion, "1/3").Added.Single());
        Assert.Null(sign.MemberId);
        Assert.Equal("1", Attr(sign, "Flicker", "lamp")); // a scene's own address, untouched (the schema is the lamp's)
        Assert.Equal(["Trash can"], expansion.Root.Added.Select(e => e.Name));
        Assert.Equal(expansion.Root.Subtree().Count(), expansion.Root.Subtree().Select(e => e.Address).Distinct().Count());
    }

    [Fact]
    public void NestedOverridesApplyInnerFirst()
    {
        // Inside the corner the lamp's bulb has Intensity 120 and the lamp is at x=2.
        Expansion plain = Expand(Instance(Corner, address: "5"), "5");
        Assert.Equal("120", Attr(At(plain, "5/2/2"), "Light", "Intensity"));
        Assert.Equal("2 0 0", Attr(At(plain, "5/2"), "Transform", "Position"));
        Assert.Equal("80", Attr(At(plain, "5/3/2"), "Light", "Intensity")); // the second lamp has no override of its bulb
        Assert.Equal("12", Attr(At(plain, "5/2/2"), "Light", "Range"));
        Assert.True(At(plain, "5/3/3").IsRemoved); // its own override removed the base

        // An outer override on the same field wins; on another field, the inner value stays.
        Expansion outer = Expand(Instance(Corner, "<Override entity=\"2/2\"><Light Range=\"30\"/></Override>", "5"), "5");
        Assert.Equal("120", Attr(At(outer, "5/2/2"), "Light", "Intensity"));
        Assert.Equal("30", Attr(At(outer, "5/2/2"), "Light", "Range"));
        Expansion both = Expand(Instance(Corner, "<Override entity=\"2/2\"><Light Intensity=\"40\"/></Override>", "5"), "5");
        Assert.Equal("40", Attr(At(both, "5/2/2"), "Light", "Intensity"));
        Assert.Empty(both.Root.Unapplied);
    }

    [Fact]
    public void AnOuterOverrideReachesIntoANestedInstance()
    {
        Expansion expansion = Expand(Instance(
            Corner,
            """
            <Override entity="2/2"><Light Intensity="40"/></Override>
            <Override entity="2" name="Standing lamp"/>
            <Override entity="2/3"><Transform Position="0 0.25 0"/></Override>
            <Override entity="3" remove="true"/>
            """,
            "5"), "5");

        Assert.Equal("40", Attr(At(expansion, "5/2/2"), "Light", "Intensity"));
        Assert.Equal("Standing lamp", At(expansion, "5/2").Name);
        Assert.Equal("0 0.25 0", Attr(At(expansion, "5/2/3"), "Transform", "Position"));
        Assert.True(At(expansion, "5/3").IsRemoved);
        Assert.True(At(expansion, "5/3/2").IsRemoved); // the nested lamp went with it
        Assert.Empty(expansion.Root.Unapplied);
        Assert.Equal(["Standing lamp", "Bench"], expansion.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void PrefabEntityFieldsAreRescopedAndOverrideFieldsAreNot()
    {
        Add("res://prefabs/wired.aprefab", """
            <Prefab version="1" nextId="6">
              <Components><Component name="Link"><Field name="to" kind="Entity"/><Field name="far" kind="Entity"/><Field name="n" kind="Int32"/></Component></Components>
              <Entity id="1" name="Wired">
                <Link to="1" far="99" n="1"/>
                <Entity id="2" name="Lamp" prefab="res://prefabs/lamp.aprefab">
                  <Override entity="2"><Flicker lamp="4"/></Override>
                </Entity>
                <Entity id="4" name="Switch"><Link to="2" far="2/3" n="2"/></Entity>
              </Entity>
            </Prefab>
            """);

        Expansion expansion = Expand(Instance("res://prefabs/wired.aprefab", "<Override entity=\"4\"><Link to=\"1\"/></Override><Override entity=\"2/2\"><Link to=\"1\" append=\"true\"/></Override>", "5"), "5");

        // Inside the prefab: "1" is the instance itself, "2/3" an entity inside the nested lamp; "99" is an id nothing has (it resolves to nothing, as in a scene). Integers stay integers.
        Assert.Equal("5", Attr(expansion.Root, "Link", "to"));
        Assert.Equal("5/99", Attr(expansion.Root, "Link", "far"));
        Assert.False(expansion.Index.ContainsKey("5/99"));
        Assert.Equal("1", Attr(expansion.Root, "Link", "n"));
        ExpandedEntity sw = At(expansion, "5/4");
        Assert.Equal("1", Attr(sw, "Link", "to")); // written by an override in the scene: the scene's scope, where "1" is the scene's own entity 1 (the prefab said "2")
        Assert.Equal("5/2/3", Attr(sw, "Link", "far"));
        Assert.Equal("2", Attr(sw, "Link", "n"));

        // The override inside the prefab (lamp="4": the prefab's scope) lands under the instance; the lamp on its own points at its own root.
        Assert.Equal("5/4", Attr(At(expansion, "5/2/2"), "Flicker", "lamp"));
        Assert.Equal("5/2", Attr(At(Expand(Instance(Lamp, address: "5/2"), "5/2"), "5/2/2"), "Flicker", "lamp"));
        Assert.Equal("1", Attr(At(expansion, "5/2/2"), "Link", "to")); // appended by the scene's override: scene scope
    }

    [Fact]
    public void AnEntityFieldPointingOutsideThePrefabNamesNothing()
    {
        Add("res://prefabs/stray.aprefab", """
            <Prefab version="1" nextId="3">
              <Entity id="1" name="Stray"><Marks Target="77" Other="abc" Back="../2" Up="0"/><Entity id="2" name="Kid"><Marks Target="1" Other="" Back="1/2"/></Entity></Entity>
            </Prefab>
            """);

        Expansion expansion = Expand(Instance("res://prefabs/stray.aprefab", address: "3"), "3", isEntityField: (c, a) => c == "Marks" && a is "Target" or "Other" or "Back" or "Up");

        // Rescoped under the instance (never to an address outside it), and left alone when it is no address at all; nothing ever resolves to an entity of another instance.
        ExpandedEntity root = expansion.Root;
        Assert.Equal("3/77", Attr(root, "Marks", "Target"));
        Assert.False(expansion.Index.ContainsKey("3/77"));
        Assert.Equal("", Attr(root, "Marks", "Other")); // text that is no address means no entity, and nothing a file writes can spell a made-up address
        Assert.Equal("", Attr(root, "Marks", "Back"));
        Assert.Equal("", Attr(root, "Marks", "Up"));
        Assert.Equal("3", Attr(At(expansion, "3/2"), "Marks", "Target"));
        Assert.Equal("", Attr(At(expansion, "3/2"), "Marks", "Other"));
        Assert.Equal("3/1/2", Attr(At(expansion, "3/2"), "Marks", "Back")); // an address through the root's own id, as written
    }

    [Fact]
    public void TheRootAliasIsAccepted()
    {
        Expansion expansion = Expand(Instance(
            Corner,
            """
            <Override entity="1"><Transform Position="1 1 1"/></Override>
            <Override entity="2/1"><Transform Position="4 4 4"/></Override>
            """,
            "5"), "5");

        Assert.Equal("1 1 1", Attr(expansion.Root, "Transform", "Position"));
        Assert.Equal("4 4 4", Attr(At(expansion, "5/2"), "Transform", "Position")); // 2/1: the root of the nested lamp
        Assert.Same(At(expansion, "5/2"), At(expansion, "5/2/1"));
        Assert.Empty(expansion.Root.Unapplied);
    }

    [Fact]
    public void AnOverrideOfAMissingEntityIsUnapplied()
    {
        string stale = "<Override entity=\"99\" name=\"Gone\"><Light Intensity=\"1\"/></Override>";
        Expansion expansion = Expand(Instance(Lamp, stale + "<Override entity=\"2\"><Light Intensity=\"7\"/></Override>"));

        Assert.Equal("7", Attr(At(expansion, "1/2"), "Light", "Intensity")); // the rest still applies
        XmlNode kept = Assert.Single(expansion.Root.Unapplied);
        Assert.Equal(XmlNode.Parse(stale).ToXml(), kept.ToXml()); // as written
        Assert.True(Warned("the instance has no entity 99"));
        Assert.DoesNotContain(expansion.Root.Subtree(), e => e.Name == "Gone");
    }

    [Fact]
    public void AFieldPatchOfAComponentThePrefabRemovedIsUnapplied()
    {
        // The prefab's Base has no Health (it lost it): the instance's patch of Health.Max cannot make a half component, so it stays aside whole.
        Expansion expansion = Expand(Instance(
            Lamp,
            "<Override entity=\"3\" name=\"Foot\"><Health Max=\"3\"/><Transform Position=\"1 0 0\"/></Override><Health Max=\"4\"/>"));

        ExpandedEntity baseEntity = At(expansion, "1/3");
        Assert.Null(baseEntity.Component("Health"));
        Assert.Equal("Foot", baseEntity.Name); // what could apply did
        Assert.Equal("1 0 0", Attr(baseEntity, "Transform", "Position"));
        Assert.Null(expansion.Root.Component("Health"));
        Assert.Equal(2, expansion.Root.Unapplied.Count);
        XmlNode left = expansion.Root.Unapplied[0]; // only the part that failed, under the same address
        Assert.Equal("Override", left.Name);
        Assert.Equal("3", left.Attribute("entity"));
        Assert.Null(left.Attribute("name"));
        Assert.Equal("Health", Assert.Single(left.Children).Name);
        Assert.Equal("Health", expansion.Root.Unapplied[1].Name);
        Assert.Equal("4", expansion.Root.Unapplied[1].Attribute("Max"));
        Assert.True(Warned("the entity has no Health to patch"));
    }

    [Fact]
    public void RemovingAComponentTheEntityLacksIsUnappliedToo()
    {
        Expansion expansion = Expand(Instance(Lamp, "<Override entity=\"3\"><Light remove=\"true\"/></Override>"));

        Assert.Single(expansion.Root.Unapplied);
        Assert.True(Warned("the entity has no Light to remove"));
    }

    [Fact]
    public void AnOverrideCannotRemoveTheRootOfItsOwnInstance()
    {
        Expansion expansion = Expand(Instance(Lamp, "<Override entity=\"1\" remove=\"true\"/><Override remove=\"true\"/>"));

        Assert.Equal(2, expansion.Root.Unapplied.Count);
        Assert.Equal(3, expansion.Root.Subtree().Count());
        Assert.True(Warned("cannot be removed by its own override"));
    }

    // ---- Cycles, depth, budgets, missing prefabs ---------------------------------------------------------------------------------------

    private void AddChain(string name, params string[] contains)
    {
        string nested = string.Concat(contains.Select((p, i) => $"<Entity id=\"{i + 2}\" name=\"in {p}\" prefab=\"res://prefabs/{p}.aprefab\"/>"));
        Add($"res://prefabs/{name}.aprefab", $"<Prefab version=\"1\" nextId=\"{contains.Length + 2}\"><Entity id=\"1\" name=\"{name}\">{nested}</Entity></Prefab>");
    }

    [Fact]
    public void APrefabThatContainsItselfIsLeftEmpty()
    {
        AddChain("selfish", "selfish");

        Expansion direct = Expand(Instance("res://prefabs/selfish.aprefab"));

        // The first level is expanded; the instance of itself inside it is the root alone, with the reason.
        ExpandedEntity inner = Assert.Single(direct.Root.Children);
        Assert.True(inner.IsMissing);
        Assert.Contains("contains itself", inner.MissingReason, StringComparison.Ordinal);
        Assert.Contains("selfish.aprefab → res://prefabs/selfish.aprefab", inner.MissingReason, StringComparison.Ordinal);
        Assert.Empty(inner.Children);
        Assert.Equal(2, direct.Root.Subtree().Count());

        // Through another: a -> b -> a.
        AddChain("a", "b");
        AddChain("b", "a");
        Expansion through = Expand(Instance("res://prefabs/a.aprefab"));
        ExpandedEntity b = through.Root.Children.Single();
        ExpandedEntity again = b.Children.Single();
        Assert.False(b.IsMissing);
        Assert.True(again.IsMissing);
        Assert.Contains("a.aprefab contains itself (res://prefabs/a.aprefab → res://prefabs/b.aprefab → res://prefabs/a.aprefab)", again.MissingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleThroughThreePrefabsIsFoundAndTheFilesAreStillUsableOnTheirOwn()
    {
        AddChain("ca", "cb");
        AddChain("cb", "cc");
        AddChain("cc", "ca");

        Expansion expansion = Expand(Instance("res://prefabs/ca.aprefab"));

        ExpandedEntity[] all = [.. expansion.Root.Subtree()];
        Assert.Equal(4, all.Length);
        Assert.Contains("ca.aprefab → res://prefabs/cb.aprefab → res://prefabs/cc.aprefab → res://prefabs/ca.aprefab", all[3].MissingReason, StringComparison.Ordinal);
        Assert.Equal([false, false, false, true], all.Select(e => e.IsMissing));

        // A prefab that is not in the cycle expands in full next to them: the stack is per chain, not per expansion.
        AddChain("pair", "lamp", "lamp");
        Assert.Equal(1 + 2 * 3, Expand(Instance("res://prefabs/pair.aprefab")).Root.Subtree().Count());
    }

    [Fact]
    public void NestingDeeperThanTheLimitIsLeftEmpty()
    {
        // p1 holds p2 holds ... p17: the first sixteen levels expand, the seventeenth is the root alone.
        for (int i = 1; i <= 17; i++)
        {
            if (i < 17)
            {
                AddChain($"p{i}", $"p{i + 1}");
            }
            else
            {
                Add("res://prefabs/p17.aprefab", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"p17\"><Transform Position=\"0 0 0\"/></Entity></Prefab>");
            }
        }

        Expansion expansion = Expand(Instance("res://prefabs/p1.aprefab"));

        ExpandedEntity[] chain = [.. expansion.Root.Subtree()];
        Assert.Equal(17, chain.Length);
        Assert.Equal(16, chain.Count(e => !e.IsMissing));
        Assert.True(chain[16].IsMissing);
        Assert.Contains("nested more than 16 deep", chain[16].MissingReason, StringComparison.Ordinal);
        Assert.Null(chain[16].Component("Transform"));
        Assert.All(chain, e => Assert.True(EntityAddress.IsValid(e.Address), e.Address)); // seventeen segments at most
        Assert.Equal(17, chain[16].Address.Split('/').Length);

        // Sixteen deep is fine: end the chain at the sixteenth.
        Add("res://prefabs/p16.aprefab", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"p16\"/></Prefab>");
        ExpandedEntity[] sixteen = [.. Expand(Instance("res://prefabs/p1.aprefab")).Root.Subtree()];
        Assert.Equal(16, sixteen.Length);
        Assert.DoesNotContain(sixteen, e => e.IsMissing);
    }

    private void AddBomb(int levels, int fan)
    {
        for (int level = 0; level < levels; level++)
        {
            string nested = level == levels - 1
                ? ""
                : string.Concat(Enumerable.Range(2, fan).Select(i => $"<Entity id=\"{i}\" prefab=\"res://prefabs/bomb{level + 1}.aprefab\"/>"));
            Add($"res://prefabs/bomb{level}.aprefab", $"<Prefab version=\"1\" nextId=\"{fan + 2}\"><Entity id=\"1\" name=\"bomb{level}\">{nested}</Entity></Prefab>");
        }
    }

    [Fact]
    public void MoreEntitiesThanTheBudgetFailsBeforeAnythingIsMade()
    {
        AddBomb(5, 10); // 1 + 10 + 100 + 1000 + 10000 = 11111 entities, from five small files

        var small = new ExpansionBudget(500);
        FormatException e = Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/bomb0.aprefab"), budget: small));
        Assert.Contains("more than 500 entities", e.Message, StringComparison.Ordinal);
        Assert.Equal(500, small.Used); // counted as each entity was about to be made: never one over

        var exact = new ExpansionBudget(11111);
        Assert.Equal(11111, Expand(Instance("res://prefabs/bomb0.aprefab"), budget: exact).Root.Subtree().Count());
        Assert.Equal(11111, exact.Used);
        Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/bomb0.aprefab"), budget: new ExpansionBudget(11110)));
    }

    [Fact]
    public void ABombSixteenLevelsDeepStopsAtTheDefaultBudget()
    {
        AddBomb(16, 10); // 10^15 entities if it were expanded

        var budget = new ExpansionBudget();
        Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/bomb0.aprefab"), budget: budget));
        Assert.Equal(ExpansionBudget.DefaultLimit, budget.Used);
    }

    [Fact]
    public void OneBudgetServesEveryExpansionOfALoad()
    {
        var budget = new ExpansionBudget(7, used: 2); // the scene's own two entities are already in it
        PrefabExpander expander = Expander(budget);
        expander.Expand(XmlNode.Parse(Instance(Lamp, address: "1")), "1"); // 3 entities: 5 of 7
        Assert.Equal(5, budget.Used);
        Assert.Throws<FormatException>(() => expander.Expand(XmlNode.Parse(Instance(Lamp, address: "2")), "2")); // 3 more do not fit
        Assert.Equal(7, budget.Used);
    }

    [Fact]
    public void AMissingPrefabKeepsAllItsChildrenUnapplied()
    {
        var budget = new ExpansionBudget();
        Expansion expansion = Expand(
            Instance("res://prefabs/ghost.aprefab", "<Transform Position=\"1 0 0\"/><Override entity=\"2\"><Light Intensity=\"1\"/></Override><Entity id=\"9\" name=\"Kept\"/>", "4"),
            "4",
            budget);

        ExpandedEntity root = expansion.Root;
        Assert.True(root.IsMissing);
        Assert.Equal("res://prefabs/ghost.aprefab", root.InstancePrefab);
        Assert.Equal(0, root.InstanceRootId);
        Assert.Empty(root.Children);
        Assert.Empty(root.Components);
        Assert.Equal(["Transform", "Override", "Entity"], root.Unapplied.Select(u => u.Name));
        Assert.Equal(1, budget.Used);
        Assert.False(expansion.Index.ContainsKey("9")); // the entity it holds was not made
        Assert.True(Warned("ghost.aprefab, which cannot be used: there is no such file"));
    }

    [Fact]
    public void APrefabThatIsNotUsableLeavesTheInstanceEmptyWithTheReason()
    {
        Add("res://prefabs/dups.aprefab", "<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\"><Entity id=\"2\"/><Entity id=\"2\"/></Entity></Prefab>");
        Add("res://prefabs/two.aprefab", "<Prefab version=\"1\"><Entity id=\"1\"/><Entity id=\"2\"/></Prefab>");
        Add("res://prefabs/notprefab.aprefab", "<Scene version=\"1\"><Entity id=\"1\"/></Scene>");
        Add("res://prefabs/newer.aprefab", "<Prefab version=\"2\"><Entity id=\"1\"/></Prefab>");
        Add("res://prefabs/variant.aprefab", $"<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" prefab=\"{Lamp}\"/></Prefab>");

        string[] reasons = [.. UnusableNames.Select(n =>
        {
            ExpandedEntity root = Expand(Instance($"res://prefabs/{n}.aprefab", "<Transform Position=\"1 1 1\"/>")).Root;
            Assert.True(root.IsMissing, n);
            Assert.Single(root.Unapplied);
            return root.MissingReason!;
        })];

        Assert.Contains("used twice", reasons[0], StringComparison.Ordinal);
        Assert.Contains("2 root entities", reasons[1], StringComparison.Ordinal);
        Assert.Contains("not a prefab", reasons[2], StringComparison.Ordinal);
        Assert.Contains("reads up to", reasons[3], StringComparison.Ordinal);
        Assert.Contains("instance of another prefab", reasons[4], StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdAddedByAnOverrideCannotRepeatAnIdOfTheFile()
    {
        // The ids of entities an <Override> adds come from the file's numbering too: a repeat is an error, and nextId counts them.
        static string Holder(int signId) => $"""
            <Prefab version="1" nextId="3">
              <Entity id="1" name="Holder"><Entity id="2" name="Lamp" prefab="{Lamp}"><Override entity="3"><Entity id="{signId}" name="Sign"/></Override></Entity></Entity>
            </Prefab>
            """;
        Add("res://prefabs/holder.aprefab", Holder(2));
        ExpandedEntity broken = Expand(Instance("res://prefabs/holder.aprefab")).Root;
        Assert.True(broken.IsMissing);
        Assert.Contains("used twice", broken.MissingReason, StringComparison.Ordinal);

        XmlNode fine = XmlNode.Parse(Holder(8));
        PrefabDocument.Check(fine, "holder", warnNextId: false);
        Assert.Equal(9L, PrefabDocument.NextIdOf(fine));
    }

    [Fact]
    public void ABranchDeeperThanTheTreeLimitIsRefused()
    {
        string Tall(string name, string end)
        {
            var text = new StringBuilder();
            text.Append("<Prefab version=\"1\" nextId=\"200\"><Entity id=\"1\" name=\"").Append(name).Append("\">");
            for (int i = 2; i < 100; i++)
            {
                text.Append("<Entity id=\"").Append(i).Append("\">");
            }

            text.Append(end);
            for (int i = 2; i < 100; i++)
            {
                text.Append("</Entity>");
            }

            return text.Append("</Entity></Prefab>").ToString();
        }

        Add("res://prefabs/tall1.aprefab", Tall("tall1", "<Entity id=\"150\" prefab=\"res://prefabs/tall2.aprefab\"/>"));
        Add("res://prefabs/tall2.aprefab", Tall("tall2", ""));
        FormatException e = Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/tall1.aprefab")));
        Assert.Contains("nested more than 128 deep", e.Message, StringComparison.Ordinal);

        Assert.Equal(99, Expand(Instance("res://prefabs/tall2.aprefab")).Root.Subtree().Count());
    }

    // ---- Hostile text in overrides ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BadAddressesAndUnknownAttributesAreWarnedAndKept()
    {
        string[] bad =
        [
            "<Override entity=\"x/y\"><Light Intensity=\"1\"/></Override>",
            "<Override entity=\"0\" name=\"zero\"/>",
            "<Override entity=\"1//2\" name=\"gap\"/>",
            "<Override entity=\"02\" name=\"leading zero\"/>",
            "<Override name=\"no entity\"><Light Intensity=\"9\"/></Override>",
            "<Override entity=\"\" name=\"empty\"/>",
            "<Override entity=\"-2\" name=\"minus\"/>",
            "<Override entity=\"1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17/18\" name=\"long\"/>",
            "<Override entity=\"2\" order=\"first\"><Light Intensity=\"9\"/></Override>",
            "<Override entity=\"2\" patch=\"true\"><Light Intensity=\"9\"/></Override>",
        ];

        Expansion expansion = Expand(Instance(Lamp, string.Concat(bad)));

        Assert.Equal(bad.Select(b => XmlNode.Parse(b).ToXml()), expansion.Root.Unapplied.Select(u => u.ToXml()));
        Assert.Equal("80", Attr(At(expansion, "1/2"), "Light", "Intensity")); // none of them was applied, not even the valid parts of the one with an unknown attribute
        Assert.True(Warned("is not an entity address"));
        Assert.True(Warned("an <Override> has an attribute 'order' this engine does not know"));
    }

    [Fact]
    public void SiblingNamesAreMadeUniqueAsTheLoaderDoes()
    {
        Add("res://prefabs/parts.aprefab", """
            <Prefab version="1" nextId="6"><Entity id="1" name="Parts">
              <Entity id="2" name="Part"/><Entity id="3" name="Part"/><Entity id="4" name="Part 2"/><Entity id="5"/>
            </Entity></Prefab>
            """);

        Expansion expansion = Expand(Instance(
            "res://prefabs/parts.aprefab",
            "<Override entity=\"5\" name=\"Part\"/><Entity id=\"20\" name=\"Part\"/><Entity id=\"21\" name=\"Other\"/><Entity id=\"22\" name=\"Other\"/>"));

        string[] names = [.. expansion.Root.Children.Concat(expansion.Root.Added).Select(c => c.Name!)];
        Assert.Equal(["Part", "Part 2", "Part 2 2", "Part 3", "Part 4", "Other", "Other 2"], names);

        // The scene loader gives the same names to the same siblings in the same order.
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        using var world = new World();
        world.LoadScene("<Scene version=\"1\"><Entity id=\"1\" name=\"Parts\">"
            + "<Entity id=\"2\" name=\"Part\"/><Entity id=\"3\" name=\"Part\"/><Entity id=\"4\" name=\"Part 2\"/><Entity id=\"5\" name=\"Part\"/>"
            + "<Entity id=\"20\" name=\"Part\"/><Entity id=\"21\" name=\"Other\"/><Entity id=\"22\" name=\"Other\"/></Entity></Scene>");
        Entity parent = world.Roots().Single();
        Assert.Equal(names, world.Children(parent).Select(c => world.GetName(c)!));
    }

    [Fact]
    public void ReservedAttributesAreNotKept()
    {
        Add("res://prefabs/reserved.aprefab", """
            <Prefab version="1" nextId="3"><Entity id="1" name="Reserved">
              <Health Max="1" append="true" patch="true" remove="false"/><PrefabInstance Prefab="1"/><PrefabMember Id="4"/>
              <Entity id="2" name="Kid"><Transform Position="1 1 1" append="true"/></Entity>
            </Entity></Prefab>
            """);

        Expansion expansion = Expand(Instance("res://prefabs/reserved.aprefab", "<Health Max=\"2\" patch=\"true\"/><Override entity=\"2\"><Transform Position=\"2 2 2\" remove=\"false\"/><Light Intensity=\"1\" append=\"true\"/></Override>"));

        Assert.Equal(["Health"], expansion.Root.Components.Select(c => c.Name)); // the engine's own components are never read from a file
        foreach (ExpandedEntity entity in expansion.Root.Subtree())
        {
            Assert.All(entity.Components, c => Assert.DoesNotContain(c.Attributes, a => a.Name is "patch" or "remove" or "append"));
        }

        Assert.Equal("2", Attr(expansion.Root, "Health", "Max"));
        Assert.Equal("2 2 2", Attr(At(expansion, "1/2"), "Transform", "Position"));
        Assert.True(Warned("PrefabInstance is made by the engine"));
        Assert.Empty(expansion.Root.Unapplied);
    }

    [Fact]
    public void AnEntityWithoutAnIdOrWithARepeatedOneCannotBeReferredTo()
    {
        Expansion expansion = Expand(Instance(Lamp, "<Entity name=\"No id\"/><Entity id=\"x\" name=\"Bad id\"/><Entity id=\"30\" name=\"One\"/><Entity id=\"30\" name=\"Again\"/><Entity id=\"1\" name=\"Root again\"/>"));

        ExpandedEntity[] added = [.. expansion.Root.Added];
        Assert.Equal(5, added.Length);
        Assert.StartsWith("~", added[0].Address, StringComparison.Ordinal);
        Assert.StartsWith("~", added[1].Address, StringComparison.Ordinal);
        Assert.Equal("30", added[2].Address);
        Assert.StartsWith("~", added[3].Address, StringComparison.Ordinal); // the first has it
        Assert.Same(added[2], At(expansion, "30"));
        Assert.StartsWith("~", added[4].Address, StringComparison.Ordinal); // "1" is the instance's own address here
        Assert.Same(expansion.Root, At(expansion, "1"));
        Assert.Equal(expansion.Root.Subtree().Count(), expansion.Root.Subtree().Select(e => e.Address).Distinct().Count());
        Assert.False(EntityAddress.IsValid(added[0].Address)); // no document can write it
    }

    [Fact]
    public void AnInstanceWithoutAnIdOrWithARepeatedOneKeepsItsMembersApartFromEveryoneElses()
    {
        // Added instances of the lamp: one with no id, then two with the same one. Their members must not land on the single-segment addresses of the scene.
        Expansion expansion = Expand(Instance(Lamp, $"<Entity prefab=\"{Lamp}\" name=\"Spare\"/><Entity id=\"7\" prefab=\"{Lamp}\" name=\"Seven\"/><Entity id=\"7\" prefab=\"{Lamp}\" name=\"Seven again\"/>"));

        ExpandedEntity spare = expansion.Root.Added[0];
        Assert.StartsWith("~", spare.Address, StringComparison.Ordinal);
        ExpandedEntity bulb = spare.Children[0];
        Assert.Equal($"{spare.Address}/2", bulb.Address);
        Assert.Equal(spare.Address, Attr(bulb, "Flicker", "lamp")); // its own root, not the scene's entity 1

        ExpandedEntity seven = expansion.Root.Added[1];
        ExpandedEntity again = expansion.Root.Added[2];
        Assert.Equal("7", seven.Address);
        Assert.StartsWith("~", again.Address, StringComparison.Ordinal);
        Assert.Equal("7/2", seven.Children[0].Address);
        Assert.Equal($"{again.Address}/2", again.Children[0].Address);
        Assert.Equal(expansion.Root.Subtree().Count(), expansion.Root.Subtree().Select(e => e.Address).Distinct().Count());
        Assert.True(Warned("the entity address 7 is used twice"));
    }

    // ---- Bounds on work, components and the log ---------------------------------------------------------------------------------------

    private int Count(string text)
    {
        lock (_sink.Warnings)
        {
            return _sink.Warnings.Count(w => w.Contains(text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheWorkOfOverridesIsBoundedAcrossNestedInstances()
    {
        // L is one entity. M is a root with an instance of L and thousands of overrides of it. N holds many M. Few entities, but an override applies once for every instance of its file.
        Add("res://prefabs/l.aprefab", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"L\"><Transform Position=\"0 0 0\"/></Entity></Prefab>");
        string overrides = string.Concat(Enumerable.Repeat("<Override entity=\"1\"><Transform Position=\"1 1 1\"/></Override>", 2000));
        Add("res://prefabs/m.aprefab", $"<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\" name=\"M\"><Entity id=\"2\" name=\"in L\" prefab=\"res://prefabs/l.aprefab\">{overrides}</Entity></Entity></Prefab>");
        string many = string.Concat(Enumerable.Range(2, 200).Select(i => $"<Entity id=\"{i}\" prefab=\"res://prefabs/m.aprefab\"/>"));
        Add("res://prefabs/n.aprefab", $"<Prefab version=\"1\" nextId=\"300\"><Entity id=\"1\" name=\"N\">{many}</Entity></Prefab>");

        var budget = new ExpansionBudget(5000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        FormatException e = Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/n.aprefab"), budget: budget));

        Assert.Contains("steps of work", e.Message, StringComparison.Ordinal);
        Assert.True(budget.Used < budget.Limit, "the entities were not the problem"); // 401 would fit
        Assert.True(budget.WorkUsed <= budget.WorkLimit);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());

        // The same, as the file would be with a budget to match its size, expands.
        Assert.Equal(401, Expand(Instance("res://prefabs/n.aprefab"), budget: new ExpansionBudget(2_000_000)).Root.Subtree().Count(c => !c.IsMissing));
    }

    [Fact]
    public void StaleOverridesInManyInstancesAreBoundedToo()
    {
        Add("res://prefabs/m.aprefab", $"<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\" name=\"M\"><Entity id=\"2\" name=\"in lamp\" prefab=\"{Lamp}\">{string.Concat(Enumerable.Range(100, 2000).Select(i => $"<Override entity=\"{i}\"/>"))}</Entity></Entity></Prefab>");
        string many = string.Concat(Enumerable.Range(2, 200).Select(i => $"<Entity id=\"{i}\" prefab=\"res://prefabs/m.aprefab\"/>"));
        Add("res://prefabs/n.aprefab", $"<Prefab version=\"1\" nextId=\"300\"><Entity id=\"1\" name=\"N\">{many}</Entity></Prefab>");

        Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/n.aprefab"), budget: new ExpansionBudget(5000)));
        Assert.True(Count("the instance has no entity") <= PrefabExpander.MaxWarnings + 1); // and the log is not flooded on the way
    }

    [Fact]
    public void AnEntityWithVeryManyComponentsIsExpandedInLinearTime()
    {
        string components = string.Concat(Enumerable.Range(0, 50_000).Select(i => $"<C{i}/>"));
        Add("res://prefabs/wide.aprefab", $"<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Wide\">{components}</Entity></Prefab>");
        string removals = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"<C{i} remove=\"true\"/>"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Expansion expansion = Expand(Instance("res://prefabs/wide.aprefab", removals + "<Extra append=\"true\"/>"));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        ExpandedEntity root = expansion.Root;
        Assert.Equal(30_001, root.ComponentCount);
        Assert.Null(root.Component("C5"));
        Assert.Equal("C20000", root.Components.First().Name); // order kept
        Assert.Equal("Extra", root.Components.Last().Name);
        Assert.NotNull(root.Component("C49999"));
        Assert.Empty(root.Unapplied);

        // Many instances of it hit the budget instead of building 50,000 references each.
        Add("res://prefabs/crowd.aprefab", $"<Prefab version=\"1\" nextId=\"9\"><Entity id=\"1\" name=\"Crowd\">{string.Concat(Enumerable.Range(2, 8).Select(i => $"<Entity id=\"{i}\" prefab=\"res://prefabs/wide.aprefab\"/>"))}</Entity></Prefab>");
        var budget = new ExpansionBudget(1000);
        Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/crowd.aprefab"), budget: budget));
        Assert.True(budget.WorkUsed <= budget.WorkLimit);
    }

    [Fact]
    public void ACopiedComponentSharesItsChildrenWithTheDocument()
    {
        const string path = "res://prefabs/blob.aprefab";
        Add(path, "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Blob\"><Marks Target=\"1\" patch=\"x\"><Extra a=\"1\"><Deep b=\"2\"/></Extra></Marks></Entity></Prefab>");
        string before = _docs[path].ToXml();
        XmlNode original = _docs[path].Child("Entity")!.Child("Marks")!;
        Func<string, string, bool> isEntity = (c, a) => c == "Marks" && a == "Target";

        XmlNode copy = Expand(Instance(path), isEntityField: isEntity).Root.Component("Marks")!;
        Assert.NotSame(original, copy); // an attribute changed: a copy of the element...
        Assert.DoesNotContain(copy.Attributes, a => a.Name == "patch");
        Assert.Same(original.Children[0], copy.Children[0]); // ...that shares what is below it

        // Merged into, the document's nodes are still as they were.
        XmlNode merged = Expand(Instance(path, "<Marks Target=\"1\"/>"), isEntityField: isEntity).Root.Component("Marks")!;
        Assert.Equal("Deep", merged.Child("Extra")!.Children[0].Name);
        Assert.Equal(before, _docs[path].ToXml());
    }

    [Fact]
    public void TheSameStaleOverrideInManyInstancesIsLoggedOnce()
    {
        Add("res://prefabs/stale.aprefab", $"<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\" name=\"Stale\"><Entity id=\"2\" name=\"in lamp\" prefab=\"{Lamp}\"><Override entity=\"777\" name=\"x\"/></Entity></Entity></Prefab>");
        Add("res://prefabs/crowd.aprefab", $"<Prefab version=\"1\" nextId=\"2000\"><Entity id=\"1\" name=\"Crowd\">{string.Concat(Enumerable.Range(2, 1000).Select(i => $"<Entity id=\"{i}\" prefab=\"res://prefabs/stale.aprefab\"/>"))}</Entity></Prefab>");
        int before = Count("the instance has no entity 777");

        Expansion expansion = Expand(Instance("res://prefabs/crowd.aprefab"));

        Assert.Equal(1 + 1000 * 4, expansion.Root.Subtree().Count());
        Assert.True(Count("the instance has no entity 777") - before <= 1);
        Assert.Empty(expansion.Root.Unapplied); // the prefab file's own leftovers are not the scene's
    }

    [Fact]
    public void WarningsAreCappedWithOneLineSayingSo()
    {
        string stale = string.Concat(Enumerable.Range(100, 500).Select(i => $"<Override entity=\"{i}\"/>")); // 500 different elements, none of them applicable
        int before = Count("the instance has no entity");
        PrefabExpander expander = Expander();

        Expansion expansion = expander.Expand(XmlNode.Parse(Instance(Lamp, stale), "scene"), "1");

        Assert.Equal(500, expansion.Root.Unapplied.Count); // all kept, though only some are said
        Assert.Equal(300, expander.SuppressedWarnings);
        Assert.Equal(PrefabExpander.MaxWarnings, Count("the instance has no entity") - before);
        Assert.True(Warned("more than 200 warnings about prefabs"));
    }

    [Fact]
    public void NoFieldTextCanSpellAMadeUpAddress()
    {
        Add("res://prefabs/tilde.aprefab", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Tilde\"><Marks Target=\"~1\"/></Entity></Prefab>");
        Func<string, string, bool> isEntity = (c, a) => c == "Marks" && a == "Target";

        Expansion plain = Expand(Instance("res://prefabs/tilde.aprefab", "<Entity name=\"No id\"/>"), isEntityField: isEntity);
        Assert.Equal("", Attr(plain.Root, "Marks", "Target"));
        Assert.StartsWith("~", plain.Root.Added.Single().Address, StringComparison.Ordinal);

        // Written in the scene's own scope, the same.
        Expansion scene = Expand(Instance("res://prefabs/tilde.aprefab", "<Marks Target=\"~1\"/><Override entity=\"1\"><Marks Target=\"~2\"/></Override>"), isEntityField: isEntity);
        Assert.Equal("", Attr(scene.Root, "Marks", "Target"));
        Assert.All(scene.Root.Subtree().SelectMany(e => e.Components).SelectMany(c => c.Attributes), a => Assert.False(a.Value.StartsWith('~')));
    }

    [Fact]
    public void WhatAPrefabFilesOwnInstancesCouldNotApplyIsNotTheScenes()
    {
        Add("res://prefabs/leaky.aprefab", $"""
            <Prefab version="1" nextId="5"><Entity id="1" name="Leaky">
              <Entity id="2" name="in lamp" prefab="{Lamp}"><Override entity="99"/><Health Max="1"/></Entity>
              <Entity id="3" name="ghost" prefab="res://prefabs/ghost.aprefab"><Transform Position="1 1 1"/></Entity>
            </Entity></Prefab>
            """);

        Expansion expansion = Expand(Instance("res://prefabs/leaky.aprefab", "<Override entity=\"98\"/>"));

        Assert.Equal("98", Assert.Single(expansion.Root.Unapplied).Attribute("entity")); // the scene's own
        ExpandedEntity[] below = [.. expansion.Root.Subtree().Skip(1)];
        Assert.All(below, e => Assert.Empty(e.Unapplied));
        Assert.Contains(below, e => e.IsMissing);
        Assert.Equal(["res://prefabs/leaky.aprefab", Lamp, "res://prefabs/ghost.aprefab"], expansion.Prefabs); // the missing one is a dependency too
    }

    // ---- Where an instance is, and what depends on what ---------------------------------------------------------------------------------

    [Fact]
    public void AnInstanceInsideAPrefabFileIsExpandedInThatFilesScope()
    {
        XmlNode element = XmlNode.Parse($"<Entity id=\"2\" name=\"Lamp\" prefab=\"{Lamp}\"><Override entity=\"2\"><Flicker lamp=\"3\"/></Override></Entity>", Corner);
        var scope = new ExpandOptions { ScopeKey = Corner, ScopeBase = "5", ScopeRootId = 1 };

        Expansion expansion = Expander().Expand(element, "5/2", options: scope);

        Assert.Equal(2, expansion.Root.MemberId); // an entity of the corner, as well as the root of a lamp
        Assert.Equal(Corner, expansion.Root.MemberPrefab);
        Assert.Equal("5/3", Attr(At(expansion, "5/2/2"), "Flicker", "lamp")); // written in the corner's numbering, so under wherever the corner is
        Assert.Equal([Lamp], expansion.Prefabs);

        XmlNode rootOfCorner = XmlNode.Parse($"<Entity id=\"2\" name=\"Lamp\" prefab=\"{Lamp}\"><Override entity=\"2\"><Flicker lamp=\"1\"/></Override></Entity>", Corner);
        Assert.Equal("5", Attr(At(Expander().Expand(rootOfCorner, "5/2", options: scope), "5/2/2"), "Flicker", "lamp"));

        Assert.Throws<ArgumentException>(() => Expander().Expand(element, "5/2", options: new ExpandOptions { ScopeKey = Corner }));
        Assert.Throws<ArgumentException>(() => Expander().Expand(element, "5/2", options: new ExpandOptions { StartDepth = 129 }));
    }

    [Fact]
    public void TheDepthLimitCountsFromTheTopOfTheWholeExpansion()
    {
        Add("res://prefabs/tree.aprefab", "<Prefab version=\"1\" nextId=\"5\"><Entity id=\"1\" name=\"Tree\"><Entity id=\"2\"><Entity id=\"3\"><Entity id=\"4\"/></Entity></Entity></Entity></Prefab>");
        XmlNode instance = XmlNode.Parse(Instance("res://prefabs/tree.aprefab"));

        Assert.Equal(4, Expander().Expand(instance, "1", options: new ExpandOptions { StartDepth = 125 }).Root.Subtree().Count()); // the deepest is the 128th
        Assert.Throws<FormatException>(() => Expander().Expand(instance, "1", options: new ExpandOptions { StartDepth = 126 }));
    }

    [Fact]
    public void ExpansionListsEveryPrefabItUsedInTheOrderMet()
    {
        AddChain("outer", "inner", "inner", "outer");

        Expansion expansion = Expand(Instance("res://prefabs/outer.aprefab"));

        // The one it contains itself through shows up once, and a missing file would too.
        Assert.Equal(["res://prefabs/outer.aprefab", "res://prefabs/inner.aprefab"], expansion.Prefabs);
    }

    [Fact]
    public void TwoSpellingsOfOneFileAreOnePrefab()
    {
        const string path = "res://prefabs/spelled.aprefab";
        XmlNode loop = XmlNode.Parse("<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\" name=\"S\"><Entity id=\"2\" name=\"again\" prefab=\"uid://a\"/></Entity></Prefab>", path);
        XmlNode broken = XmlNode.Parse("<Prefab version=\"1\"><Entity id=\"1\"/><Entity id=\"2\"/></Prefab>", "res://prefabs/broken.aprefab");
        ResolvedPrefab Resolver(string reference) => reference switch
        {
            "uid://a" or "res://prefabs/spelled.aprefab|uid://a" => new ResolvedPrefab(path, loop),
            "uid://b" or "res://prefabs/broken.aprefab|uid://b" => new ResolvedPrefab("res://prefabs/broken.aprefab", broken),
            _ => new ResolvedPrefab(reference, null, "no such file"),
        };
        var expander = new PrefabExpander(Resolver, (_, _) => false, new ExpansionBudget());

        // The loop is found whichever way the file is spelled on the way in and on the way round.
        Expansion cycle = expander.Expand(XmlNode.Parse("<Entity id=\"1\" prefab=\"res://prefabs/spelled.aprefab|uid://a\"/>"), "1");
        ExpandedEntity again = cycle.Root.Children.Single();
        Assert.True(again.IsMissing);
        Assert.Contains("contains itself", again.MissingReason, StringComparison.Ordinal);
        Assert.Equal([path], cycle.Prefabs);

        // A file that is there but cannot be used has the one key too, so a refresh finds both instances.
        Expansion twice = expander.Expand(XmlNode.Parse("<Entity id=\"1\" prefab=\"uid://b\"><Entity id=\"2\" prefab=\"res://prefabs/broken.aprefab|uid://b\"/></Entity>"), "1");
        Assert.True(twice.Root.IsMissing);
        Assert.Equal("res://prefabs/broken.aprefab", twice.Root.InstancePrefab);
        Assert.Equal(["res://prefabs/broken.aprefab"], twice.Prefabs);
    }

    // ---- Work that is quadratic in a list ---------------------------------------------------------------------------------------------

    [Fact]
    public void ManySiblingsWithOneNameAreCountedAsWork()
    {
        string Parts(int count) => $"<Prefab version=\"1\" nextId=\"{count + 5}\"><Entity id=\"1\" name=\"Parts\">{string.Concat(Enumerable.Range(2, count).Select(i => $"<Entity id=\"{i}\" name=\"x\"/>"))}</Entity></Prefab>";

        Add("res://prefabs/parts.aprefab", Parts(300));
        ExpandedEntity[] fine = [.. Expand(Instance("res://prefabs/parts.aprefab")).Root.Children];
        Assert.Equal("x 300", fine[^1].Name);

        Add("res://prefabs/parts.aprefab", Parts(20_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/parts.aprefab"), budget: new ExpansionBudget(30_000))); // 2*10^8 names tried, if it were allowed
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public void ManyRemovalsFromOneParentAreCountedAsWork()
    {
        Add("res://prefabs/flat.aprefab", $"<Prefab version=\"1\" nextId=\"20010\"><Entity id=\"1\" name=\"Flat\">{string.Concat(Enumerable.Range(2, 20_000).Select(i => $"<Entity id=\"{i}\"/>"))}</Entity></Prefab>");
        string removals = string.Concat(Enumerable.Range(2, 20_000).Select(i => $"<Override entity=\"{i}\" remove=\"true\"/>"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<FormatException>(() => Expand(Instance("res://prefabs/flat.aprefab", removals), budget: new ExpansionBudget(30_000)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    // ---- Smaller rules ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnEntityAddedUnderAMemberThatTheSameElementRemovesIsKeptAside()
    {
        Expansion expansion = Expand(Instance(
            Lamp,
            "<Override entity=\"3\"><Entity id=\"9\" name=\"Sign\"/></Override><Entity id=\"10\" name=\"Mug\"/><Override entity=\"3\" remove=\"true\"/>"));

        Assert.DoesNotContain(expansion.Root.Subtree(), e => e.Name == "Sign");
        Assert.Contains(expansion.Root.Subtree(), e => e.Name == "Mug"); // under the root, which stays
        XmlNode held = Assert.Single(expansion.Root.Unapplied);
        Assert.Equal("Override", held.Name);
        Assert.Equal("3", held.Attribute("entity"));
        Assert.Equal("Sign", held.Children.Single().Attribute("name"));
        Assert.True(Warned("is removed by another override"));
    }

    [Fact]
    public void AnAddressOfTooManySegmentsIsRefusedAndOneAtTheLimitLeavesTheInstanceEmpty()
    {
        string seventeen = string.Join('/', Enumerable.Range(1, 17));
        Assert.Throws<ArgumentException>(() => Expander().Expand(XmlNode.Parse(Instance(Lamp)), seventeen + "/18"));

        // Seventeen segments is a valid address, but the entities below it would need eighteen.
        ExpandedEntity root = Expander().Expand(XmlNode.Parse(Instance(Lamp)), seventeen).Root;
        Assert.True(root.IsMissing);
        Assert.Contains("more than 17 segments", root.MissingReason, StringComparison.Ordinal);
    }

    // ---- Misc ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ExpansionIsDeterministic()
    {
        string instance = Instance(
            Corner,
            """
            <Override entity="2/2"><Light Intensity="40"/></Override><Override entity="99"/><Entity name="x"/><Entity name="x"/>
            <Override entity="4"><Entity id="9" name="Mug"/><Entity id="10" name="Mug"/></Override>
            """,
            "5");

        string first = Dump(Expand(instance, "5").Root);
        string second = Dump(Expand(instance, "5").Root);
        Assert.Equal(first, second);

        Assert.Contains("Mug 2", first, StringComparison.Ordinal);
        Assert.Contains("~", first, StringComparison.Ordinal); // the entities with no id are numbered the same way each time
    }

    [Fact]
    public void ExpandingDoesNotChangeTheDocuments()
    {
        string before = string.Concat(_docs.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Value.ToXml()));
        string instance = Instance(Corner, "<Override entity=\"2/2\"><Light Intensity=\"40\"/><Flicker lamp=\"3\"/></Override><Override entity=\"4\"><Transform remove=\"true\"/></Override>", "5");

        Expansion first = Expand(instance, "5");
        Expansion second = Expand(instance, "5");

        Assert.Equal(before, string.Concat(_docs.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Value.ToXml())));
        Assert.Equal(Dump(first.Root), Dump(second.Root));
    }

    [Fact]
    public void ExpandRefusesWhatIsNotAnInstanceOrAnAddress()
    {
        PrefabExpander expander = Expander();
        Assert.Throws<ArgumentException>(() => expander.Expand(XmlNode.Parse("<Entity id=\"1\"/>"), "1"));
        Assert.Throws<ArgumentException>(() => expander.Expand(XmlNode.Parse("<Light prefab=\"x\"/>"), "1"));
        Assert.Throws<ArgumentException>(() => expander.Expand(XmlNode.Parse(Instance(Lamp)), "0"));
        Assert.Throws<ArgumentException>(() => expander.Expand(XmlNode.Parse(Instance(Lamp)), ""));
    }

    [Fact]
    public void AResolverThatThrowsLeavesTheInstanceMissing()
    {
        var expander = new PrefabExpander(
            reference => throw new System.IO.FileNotFoundException($"cannot read {reference}"),
            (_, _) => false,
            new ExpansionBudget());

        ExpandedEntity root = expander.Expand(XmlNode.Parse(Instance("res://prefabs/broken.aprefab", "<Transform/>")), "1").Root;

        Assert.True(root.IsMissing);
        Assert.Contains("cannot read res://prefabs/broken.aprefab", root.MissingReason, StringComparison.Ordinal);
        Assert.Single(root.Unapplied);
    }

    [Fact]
    public void ANestedInstanceRootIsAMemberOfTheOuterFileAndTheRootOfItsOwn()
    {
        Expansion expansion = Expand(Instance(Corner, address: "5"), "5");

        ExpandedEntity lamp = At(expansion, "5/2");
        Assert.Equal(Lamp, lamp.InstancePrefab);
        Assert.Equal(2, lamp.MemberId); // its id in the corner
        Assert.Equal(Corner, lamp.MemberPrefab);
        ExpandedEntity bulb = At(expansion, "5/2/2");
        Assert.Equal(Lamp, bulb.MemberPrefab); // the bulb belongs to the lamp's file
        Assert.Equal(2, bulb.MemberId);
        Assert.Equal("Lamp 2", At(expansion, "5/3").Name); // the instance element's name, not the prefab's own root name
        Assert.Equal(["Lamp", "Lamp 2", "Bench"], expansion.Root.Children.Select(c => c.Name));
        Assert.Single(expansion.Schemas); // the lamp's <Components>, once, however many lamps
    }
}
