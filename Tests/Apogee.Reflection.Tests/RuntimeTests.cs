using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Core.Collections;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Reflection.Tests;

public class ValueTests
{
    [Fact]
    public void ScalarsRoundTrip()
    {
        Assert.True(Value.Bool(true).ToBool());
        Assert.Equal(-42L, Value.Int(-42).ToInt64());
        Assert.Equal(1.25, Value.Float(1.25).ToDouble());
        Assert.Equal("hi", Value.String("hi").ToStringValue());
        Assert.Equal(new Vector3(1.5f, -2, 3e10f), Value.Vector(new Vector3(1.5f, -2, 3e10f)).ToVector3());
        Assert.Equal(new Entity(1UL << 63 | 5), Value.Entity(new Entity(1UL << 63 | 5)).ToEntity()); // a pair id has the top bit set
        Assert.True(default(Value).IsNil);
        Assert.True(Value.String(null).IsNil);
        Assert.True(Value.Object(null).IsNil);
    }

    [Fact]
    public void ANumberIsAnIntegerOnlyWithoutAFraction()
    {
        Assert.Equal(3, Value.Float(3.0).ToInt32());
        Assert.Equal(3.0, Value.Int(3).ToDouble());
        Assert.Contains("expected an integer, got 3.5", Assert.Throws<ValueException>(() => Value.Float(3.5).ToInt32()).Message);
    }

    [Theory]
    [InlineData(200, "ToSByte")]
    [InlineData(-1, "ToByte")]
    [InlineData(70000, "ToInt16")]
    [InlineData(-1, "ToUInt16")]
    [InlineData(3_000_000_000, "ToInt32")]
    [InlineData(-1, "ToUInt32")]
    [InlineData(-1, "ToUInt64")]
    public void IntegersAreRangeChecked(long value, string method)
    {
        Value number = Value.Int(value);
        Func<Value, object> convert = method switch
        {
            "ToSByte" => v => v.ToSByte(),
            "ToByte" => v => v.ToByte(),
            "ToInt16" => v => v.ToInt16(),
            "ToUInt16" => v => v.ToUInt16(),
            "ToInt32" => v => v.ToInt32(),
            "ToUInt32" => v => v.ToUInt32(),
            _ => v => v.ToUInt64(),
        };
        Assert.Contains("out of range", Assert.Throws<ValueException>(() => convert(number)).Message);
    }

    [Fact]
    public void TheWrongKindNamesBothKinds()
    {
        Assert.Equal("expected number, got string", Assert.Throws<ValueException>(() => Value.String("x").ToDouble()).Message);
        Assert.Equal("expected string, got nil", Assert.Throws<ValueException>(() => default(Value).ToStringValue()).Message);
        Assert.Equal("expected Quat, got vector", Assert.Throws<ValueException>(() => Value.Vector(Vector3.One).ToQuaternion()).Message);
        Assert.Equal("expected vector, got boolean", Assert.Throws<ValueException>(() => Value.Bool(true).ToVector3()).Message);
        Assert.Equal("expected table, got number", Assert.Throws<ValueException>(() => Value.Int(1).ToList()).Message);
    }

    [Fact]
    public void ListsConvertElementByElement()
    {
        List<int> numbers = [1, 2, 3];
        Value list = Value.ListOf(numbers, static i => Value.Int(i));
        Assert.Equal([1, 2, 3], list.ToListOf(static v => v.ToInt32()));
        Assert.True(Value.ListOf<int>(null, static i => Value.Int(i)).IsNil);
    }

    [Fact]
    public void ValuesCompareByKindAndContent()
    {
        Assert.Equal(Value.Int(1), Value.Int(1));
        Assert.NotEqual(Value.Int(1), Value.Float(1)); // different kinds
        Assert.Equal(Value.Vector(Vector3.One), Value.Vector(Vector3.One));
        Assert.Equal(Value.Quat(Quaternion.Identity), Value.Quat(Quaternion.Identity));
        Assert.NotEqual(Value.Quat(Quaternion.Identity), Value.Quat(new Quaternion(0, 0, 0, -1))); // w is part of the value
        Assert.Equal(new Quaternion(1, -2, 3, -4), Value.Quat(new Quaternion(1, -2, 3, -4)).ToQuaternion());
        Assert.NotEqual(Value.String("a"), Value.String("b"));
    }

    [Fact]
    public void AValueStaysSmall() => Assert.Equal(32, // a reference, 8 bytes of bits, 2 floats (z and w of a quaternion) and the kind: no room for a double3, which is why positions cross as vectors
         System.Runtime.CompilerServices.Unsafe.SizeOf<Value>());
}

public sealed class Probe : EngineObject
{
    public override ClassInfo Class { get; } = new("Probe", null, typeof(Probe), [], [], []);

    public int Destroyed { get; private set; }

    protected override void OnDestroyed() => Destroyed++;
}

public class EngineObjectTests
{
    [Fact]
    public void AnObjectIsFoundByItsHandleUntilItIsDestroyed()
    {
        var probe = new Probe();
        SlotHandle handle = probe.Handle;
        Assert.False(handle.IsNull);
        Assert.True(EngineObject.TryGet(handle, out EngineObject? found));
        Assert.Same(probe, found);

        probe.Destroy();
        Assert.True(probe.IsDestroyed);
        Assert.False(EngineObject.TryGet(handle, out _));
        Assert.Equal(1, probe.Destroyed);

        probe.Destroy(); // twice is harmless
        Assert.Equal(1, probe.Destroyed);
    }

    [Fact]
    public void ADestroyedObjectCannotBeUsedFromAScript()
    {
        var probe = new Probe();
        Value value = Value.Object(probe);
        Assert.Same(probe, value.ToObject<Probe>());

        probe.Destroy();
        Assert.Equal("attempt to use a destroyed object", Assert.Throws<ValueException>(() => value.ToObject<Probe>()).Message);
    }

    [Fact]
    public void AStaleHandleDoesNotReachTheObjectThatReusedItsSlot()
    {
        var first = new Probe();
        SlotHandle stale = first.Handle;
        first.Destroy();

        // The slot is reused as soon as another object is made; the old handle must still not resolve.
        var objects = Enumerable.Range(0, 64).Select(_ => new Probe()).ToList();
        Assert.False(EngineObject.TryGet(stale, out _));
        Assert.All(objects, o => Assert.True(EngineObject.TryGet(o.Handle, out _)));
        objects.ForEach(o => o.Destroy());
    }

    [Fact]
    public void ObjectsAreCheckedAgainstTheClassTheyAreUsedAs()
    {
        Value value = Value.Object(new Probe());
        Assert.Contains("expected Other, got Probe", Assert.Throws<ValueException>(() => value.ToObject<Other>()).Message);
    }

    private sealed class Other : EngineObject
    {
        public override ClassInfo Class => throw new NotSupportedException();
    }
}

public class SignalTests
{
    [Fact]
    public void EmittingCallsEveryHandlerInOrder()
    {
        var signal = new Signal<int, string>();
        var log = new List<string>();
        signal.Connect((n, s) => log.Add($"a{n}{s}"));
        signal.Connect((n, s) => log.Add($"b{n}{s}"));

        signal.Emit(1, "x");

        Assert.Equal(["a1x", "b1x"], log);
        Assert.Equal(2, signal.Count);
    }

    [Fact]
    public void AHandlerMayDisconnectItselfWhileItRuns()
    {
        var signal = new Signal();
        int calls = 0;
        Connection self = default;
        self = signal.Connect(() =>
        {
            calls++;
            self.Disconnect();
        });
        signal.Connect(() => calls += 10);

        signal.Emit();
        signal.Emit();

        Assert.Equal(21, calls); // 1 + 10, then 10
        Assert.False(self.IsConnected);
    }

    [Fact]
    public void AFailingHandlerDoesNotStopTheOthersOrReachTheEmitter()
    {
        var signal = new Signal<int>();
        int total = 0;
        signal.Connect(_ => throw new InvalidOperationException("boom"));
        signal.Connect(n => total += n);

        signal.Emit(5);

        Assert.Equal(5, total);
    }

    [Fact]
    public void DisconnectingTwiceDoesNothing()
    {
        var signal = new Signal();
        Connection connection = signal.Connect(() => { });
        connection.Disconnect();
        connection.Disconnect();
        default(Connection).Disconnect();
        Assert.Equal(0, signal.Count);
    }
}

public class EnumInfoTests
{
    private static readonly EnumInfo Flags = new("Parts", true, [new("none", 0), new("head", 1), new("arm", 2), new("leg", 4)]);

    [Fact]
    public void NamesAreLookedUpBothWays()
    {
        Assert.True(Flags.TryGetValue("arm", out long value));
        Assert.Equal(2, value);
        Assert.False(Flags.TryGetValue("tail", out _));
        Assert.Equal("leg", Flags.GetName(4));
        Assert.Equal("none", Flags.GetName(0));
    }

    [Fact]
    public void FlagsAreJoinedAndUnknownBitsAreNumbers()
    {
        Assert.Equal("head|arm", Flags.GetName(3));
        Assert.Equal("9", Flags.GetName(9)); // bit 8 is no member
    }
}

public class RegistryTests
{
    [Fact]
    public void EngineObjectIsTheRootClass()
    {
        ClassInfo root = TypeRegistry.GetClass("EngineObject");
        Assert.Null(root.BaseName);
        Assert.Contains(root, TypeRegistry.Classes);
    }

    [Fact]
    public void ANameCanBeRegisteredOnce()
    {
        TypeRegistry.Register(new ClassInfo("RegistryOnce", null, typeof(Probe), [], [], []));
        Assert.Throws<InvalidOperationException>(() => TypeRegistry.Register(new ClassInfo("RegistryOnce", null, typeof(Probe), [], [], [])));
        Assert.Null(TypeRegistry.FindClass("RegistryMissing"));
        Assert.Throws<KeyNotFoundException>(() => TypeRegistry.GetClass("RegistryMissing"));
    }

    [Fact]
    public void ListsAreSortedByName()
    {
        TypeRegistry.Register(new ClassInfo("RegistrySortB", null, typeof(Probe), [], [], []));
        TypeRegistry.Register(new ClassInfo("RegistrySortA", null, typeof(Probe), [], [], []));

        string[] names = [.. TypeRegistry.Classes.Select(c => c.Name)];
        Assert.True(names.SequenceEqual(names.OrderBy(n => n, StringComparer.Ordinal)));
    }

    [Fact]
    public void AnEnumFromTwoAssembliesIsRegisteredOnce()
    {
        var first = new EnumInfo("RegistryShared", false, [new("a", 0)]);
        TypeRegistry.RegisterIfMissing(first);
        TypeRegistry.RegisterIfMissing(new EnumInfo("RegistryShared", false, [new("b", 1)]));
        Assert.Same(first, TypeRegistry.GetEnum("RegistryShared"));
    }
}
