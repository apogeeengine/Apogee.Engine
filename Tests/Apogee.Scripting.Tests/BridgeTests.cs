using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;

namespace Apogee.Scripting.Tests;

public class BridgeTests
{
    private static (VmFixture Fixture, Animal Animal) WithAnimal()
    {
        var f = new VmFixture();
        var animal = new Animal { Name = "rex" };
        f.Vm.SetGlobal("pet", animal);
        return (f, animal);
    }

    // ---- Properties --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PropertiesAreReadAndWrittenWithTheirTypes()
    {
        var (f, animal) = WithAnimal();
        using (f)
        {
            Assert.Equal("rex", f.Run("return pet.name")[0].ToStringValue());
            Assert.Equal(4.0, f.Run("return pet.legs")[0].ToDouble());

            f.Run("pet.name = 'fido' pet.speed = 2.5 pet.legs = 3 pet.position = Vector3(1, 2, 3)");
            Assert.Equal("fido", animal.Name);
            Assert.Equal(2.5f, animal.Speed);
            Assert.Equal(3, animal.Legs);
            Assert.Equal(new Vector3(1, 2, 3), animal.Position);
            Assert.Equal(new Vector3(1, 2, 3), f.Run("return pet.position")[0].ToVector3());
        }
    }

    [Fact]
    public void AReadAfterAWriteInTheSameScriptSeesTheWrite()
    {
        // Luau resolves `global.field` once, when the function loads: the VM declares host globals mutable so that it does not for them.
        var (f, animal) = WithAnimal();
        using (f)
        {
            Assert.Equal(3.0, f.Run("pet.legs = 3 return pet.legs")[0].ToDouble());
            Assert.Equal("3 5", f.Run("local a = pet.legs pet.legs = 5 return a .. ' ' .. pet.legs")[0].ToStringValue());
            animal.Friend = new Animal { Name = "friend" };
            Assert.Equal("friend", f.Run("return pet.friend.name")[0].ToStringValue());
            animal.Friend.Name = "renamed";
            Assert.Equal("renamed", f.Run("local first = pet.friend.name pet.friend.name = 'renamed' return pet.friend.name")[0].ToStringValue());
        }
    }

    [Fact]
    public void EnumsAreStringsAndFlagsAreLists()
    {
        var (f, animal) = WithAnimal();
        using (f)
        {
            f.Run("pet.mood = 'very_happy' pet.traits = { 'fast', 'tough_skin' }");
            Assert.Equal(Mood.VeryHappy, animal.Mood);
            Assert.Equal(Traits.Fast | Traits.ToughSkin, animal.Traits);

            Assert.Equal("very_happy", f.Run("return pet.mood")[0].ToStringValue());
            Assert.Equal("fast,tough_skin", f.Run("return table.concat(pet.traits, ',')")[0].ToStringValue());
            f.Run("pet.traits = {}");
            Assert.Equal(Traits.None, animal.Traits);
            Assert.Equal(0.0, f.Run("return #pet.traits")[0].ToDouble());
            Assert.Equal("Angry/Strong", f.Run("return pet:describe('angry', { 'strong' })")[0].ToStringValue());
        }
    }

    [Fact]
    public void ListsAreTables()
    {
        var (f, animal) = WithAnimal();
        using (f)
        {
            f.Run("pet.tags = { 'a', 'b' }");
            Assert.Equal(["a", "b"], animal.Tags);
            Assert.Equal("a b", f.Run("return table.concat(pet.tags, ' ')")[0].ToStringValue());
            Assert.Equal(6.0, f.Run("return pet:sum({ 1, 2, 3 })")[0].ToDouble());
        }
    }

    [Fact]
    public void ObjectsAreTheSameObjectEveryTimeTheyAreSeen()
    {
        var (f, animal) = WithAnimal();
        using (f)
        {
            animal.Friend = animal;
            Assert.True(f.Run("return pet.friend == pet and pet.friend.friend == pet")[0].ToBool());
            Assert.True(f.Run("local t = { [pet] = 1 } return t[pet.friend] == 1")[0].ToBool());
            f.Run("pet.friend = nil");
            Assert.Null(animal.Friend);
        }
    }

    [Fact]
    public void MistakesAreErrorsThatNameWhatWasWrong()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Contains("'wings' is not a member of Animal", f.Fails("return pet.wings").Message);
            Assert.Contains("Animal.id cannot be set", f.Fails("pet.id = 3").Message);
            Assert.Contains("bad value for Animal.name: expected string, got number", f.Fails("pet.name = 5").Message);
            Assert.Contains("bad value for Animal.mood: 'furious' is not a Mood; use one of: calm, angry, very_happy", f.Fails("pet.mood = 'furious'").Message);
            Assert.Contains("expected an integer, got 1.5", f.Fails("pet.legs = 1.5").Message);
            Assert.Contains("expected an object, got number", f.Fails("local get = pet.speak; return get(5)").Message);
        }
    }

    // ---- Methods ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void MethodsTakeDefaultsAndReturnSeveralValues()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal("rex: ...", f.Run("return pet:speak()")[0].ToStringValue());
            Assert.Equal("rex: woof", f.Run("return pet:speak('woof')")[0].ToStringValue());

            IReadOnlyList<Value> pair = f.Run("return pet:pair()");
            Assert.Equal(1, pair[0].ToInt32());
            Assert.Equal("one", pair[1].ToStringValue());

            Assert.Equal(new Vector3(0, 2, 0), f.Run("return pet:move(Vector3(0, 2, 0))")[0].ToVector3());
        }
    }

    [Fact]
    public void AMethodCanBeKeptAsAFunctionValue()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal("rex: hi", f.Run("local speak = pet.speak return speak(pet, 'hi')")[0].ToStringValue());
        }
    }

    [Fact]
    public void BadArgumentsNameTheMethodAndTheParameter()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Contains("bad argument #1 (word) to 'speak': expected string, got number", f.Fails("return pet:speak(5)").Message);
            Assert.Contains("speak expects 0 to 1 arguments, got 2", f.Fails("return pet:speak('a', 'b')").Message);
            Assert.Contains("move expects 1 arguments, got 0", f.Fails("return pet:move()").Message);
            Assert.Contains("bad argument #1 (by) to 'move': expected vector, got number", f.Fails("return pet:move(5)").Message);
            Assert.Contains("'fly' is not a member of Animal", f.Fails("pet:fly()").Message);
            Assert.Contains("is not a method", f.Fails("pet:name()").Message);
        }
    }

    [Fact]
    public void StaticMethodsAndCreatableClassesAreGlobals()
    {
        using var f = new VmFixture();

        Assert.Equal("made", f.Run("return Animal.make('made').name")[0].ToStringValue());
        Assert.Equal("animal", f.Run("return Animal.new().name")[0].ToStringValue());
        Assert.Equal(7.0, f.Run("return Animal.new().id")[0].ToDouble());
        Assert.Contains("attempt to index nil", f.Fails("return Dog.new()").Message); // Dog is not creatable and has no static methods: no global
    }

    [Fact]
    public void LibrariesAreGlobalTables()
    {
        using var f = new VmFixture();
        ZooLibrary.Residents.Clear();
        ZooLibrary.Residents.Add(new Animal { Name = "zed" });

        Assert.Equal(1.0, f.Run("return zoo.count()")[0].ToDouble());
        Assert.Equal("zed", f.Run("return zoo.find('zed').name")[0].ToStringValue());
        Assert.True(f.Run("return zoo.find('nobody')")[0].IsNil);
        Assert.Contains("readonly", f.Fails("zoo.count = nil").Message);
    }

    [Fact]
    public void ScriptFunctionsCanBePassedToTheEngineAndCalledBack()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal(
                "5 five",
                f.Run("local seen pet:call(function(n, s) seen = n .. ' ' .. s end) return seen")[0].ToStringValue());
            Assert.Equal(20.0, f.Run("return pet:apply(function(x) return x * 10 end)")[0].ToDouble());
            Assert.Contains("kaboom", f.Fails("pet:apply(function() error('kaboom') end)").Message);
        }
    }

    // ---- Lifetimes ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADestroyedObjectCannotBeUsedFromAScript()
    {
        var (f, animal) = WithAnimal();
        using (f)
        {
            Assert.Equal("rex", f.Run("return pet.name")[0].ToStringValue());
            animal.Destroy();

            Assert.Contains("attempt to use a destroyed object", f.Fails("return pet.name").Message);
            Assert.Contains("attempt to use a destroyed object", f.Fails("pet:speak()").Message);
            Assert.Equal("destroyed object", f.Run("return tostring(pet)")[0].ToStringValue());
        }
    }

    [Fact]
    public void AnObjectAScriptCreatedIsDestroyedWhenTheScriptLetsGoOfItUnlessTheEngineAdopted()
    {
        using var f = new VmFixture();
        int before = Animal.DestroyedCount;

        f.Run("for i = 1, 10 do Animal.new() end");
        f.Vm.CollectGarbage();
        f.Vm.ProcessPending();
        Assert.Equal(10, Animal.DestroyedCount - before);

        before = Animal.DestroyedCount;
        f.Run("for i = 1, 5 do Animal.new():keep() end"); // adopted by the engine
        f.Vm.CollectGarbage();
        f.Vm.ProcessPending();
        Assert.Equal(0, Animal.DestroyedCount - before);
    }

    [Fact]
    public void AnObjectThatTheEngineOwnsIsNeverDestroyedByTheScriptsGarbage()
    {
        var (f, animal) = WithAnimal();
        using (f)
        {
            f.Run("local copy = pet");
            f.Vm.CollectGarbage();
            f.Vm.ProcessPending();
            Assert.False(animal.IsDestroyed);
        }
    }

    [Fact]
    public void ObjectsHaveAReadableName()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal("Animal", f.Run("return tostring(pet)")[0].ToStringValue());
            Assert.Equal("userdata", f.Run("return type(pet)")[0].ToStringValue());
        }
    }

    // ---- Classes and mods -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASubclassHasTheMembersOfItsBase()
    {
        using var f = new VmFixture();
        f.Vm.SetGlobal("dog", new Dog { Name = "dingo" });

        Assert.Equal("dingo: grr", f.Run("return dog:speak('grr')")[0].ToStringValue());
        Assert.True(f.Run("return dog.barks")[0].ToBool());
        Assert.Equal(4.0, f.Run("return dog.legs")[0].ToDouble());
    }

    [Fact]
    public void ClassesBlockedForModsWorkForTheBaseGameOnly()
    {
        using var f = new VmFixture();
        f.Vm.SetGlobal("secret", new Secret());
        ScriptOwner mod = f.Vm.CreateOwner("evilmod", isMod: true);

        Assert.Equal("hidden", f.Run("return secret:reveal()")[0].ToStringValue());
        Assert.StartsWith("mod \"evilmod\" can't use Secret: it is the base game's", f.Fails("return secret:reveal()", mod).Message);
        Assert.Contains("can't use Secret", f.Fails("return secret.x", mod).Message);
    }

    [Fact]
    public void ClassesNotForScriptsAreNilThere()
    {
        using var f = new VmFixture();
        f.Vm.SetGlobal("holder", new Holder { Hidden = new Internal(), Secret = null });

        Assert.True(f.Run("return holder.hidden")[0].IsNil);
        Assert.True(f.Run("return Internal")[0].IsNil);
        Assert.True(f.Run("return holder.secret")[0].IsNil);
    }

    // ---- Signals --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASignalCallsTheScriptsHandlerWithItsArguments()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal(
                "7:rex",
                f.Run("local got pet.hurt:connect(function(amount, who) got = amount .. ':' .. who end) pet:wound(7) return got")[0].ToStringValue());
        }
    }

    [Fact]
    public void AConnectionCanBeDisconnected()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal(
                "1 false",
                f.Run(
                    "local calls = 0 local c = pet.died:connect(function() calls += 1 end) pet:kill() local was = calls c:disconnect() pet:kill() return calls .. ' ' .. tostring(c.connected)")[0].ToStringValue());
        }
    }

    [Fact]
    public void AFailingHandlerIsLoggedAndTheEmitterNeverSeesIt()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal(
                "after",
                f.Run("pet.died:connect(function() error('handler broke') end) pet:kill() return 'after'")[0].ToStringValue());
        }
    }

    [Fact]
    public void ConnectionsToADestroyedObjectAreReleased()
    {
        var (f, animal) = WithAnimal();
        using (f)
        {
            f.Run("held = pet.died:connect(function() end) return held.connected");
            animal.Destroy();

            f.Vm.ProcessPending();

            Assert.Equal(0, f.Vm.ConnectionCount);
        }
    }

    [Fact]
    public void SeveralHandlersRunInOrder()
    {
        var (f, _) = WithAnimal();
        using (f)
        {
            Assert.Equal("ab", f.Run("local s = '' pet.died:connect(function() s ..= 'a' end) pet.died:connect(function() s ..= 'b' end) pet:kill() return s")[0].ToStringValue());
            Assert.Contains("connect expects a function", f.Fails("pet.died:connect(5)").Message);
            Assert.Contains("a signal has connect(fn)", f.Fails("pet.died:fire()").Message);
        }
    }

    // ---- Vectors ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void VectorsHaveMethods()
    {
        using var f = new VmFixture();

        Assert.Equal(5.0, f.Run("local v = Vector3(3, 4, 0) return v:length()")[0].ToDouble());
        Assert.Equal(32.0, f.Run("local a, b = Vector3(1, 2, 3), Vector3(4, 5, 6) return a:dot(b)")[0].ToDouble());
        Assert.Equal(new Vector3(0, 0, 1), f.Run("local a, b = Vector3(1, 0, 0), Vector3(0, 1, 0) return a:cross(b)")[0].ToVector3());
        Assert.Equal(new Vector3(0.6f, 0.8f, 0), f.Run("local v = Vector3(3, 4, 0) return v:normalized()")[0].ToVector3());
        Assert.Equal(new Vector3(5, 7, 9), f.Run("local a, b = Vector3(1, 2, 3), Vector3(4, 5, 6) return a + b")[0].ToVector3()); // arithmetic is native
    }
}
