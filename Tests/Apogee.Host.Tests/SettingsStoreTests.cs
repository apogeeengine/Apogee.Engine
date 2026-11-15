using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Audio;
using Apogee.Content.FileSystem;
using Apogee.Input;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;
using Lua = Apogee.Native.Luau.Lua;

namespace Apogee.Host.Tests;

/// <summary>A clock that moves when the test says so.</summary>
internal sealed class FakeClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan time) => _ticks += (long)time.TotalMilliseconds;
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("apogee-settings-");

    public SettingsStoreTests()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private string FilePath => Path.Combine(_dir.FullName, "settings.xml");

    private VirtualFileSystem Files(bool writable = true)
    {
        var files = new VirtualFileSystem();
        files.Mount("user://", new DirectorySource(_dir.FullName, writable));
        return files;
    }

    private SettingsStore Open(bool writable = true) => SettingsStore.Open(Files(writable));

    private static SettingsStore WithGameKeys(SettingsStore store)
    {
        store.Define(SettingDef.OfBool("game.hints", true));
        store.Define(SettingDef.OfInt("game.fov", 90, min: 60, max: 120));
        store.Define(SettingDef.OfNumber("game.sensitivity", 1.5, min: 0.1, max: 5));
        store.Define(SettingDef.OfString("game.name", "Driver"));
        store.Define(SettingDef.OfChoice("game.quality", "high", ["low", "high"]));
        return store;
    }

    [Fact]
    public void DefaultsWhenThereIsNoFile()
    {
        SettingsStore store = WithGameKeys(Open());

        Assert.True(store.GetBool("game.hints"));
        Assert.Equal(90, store.GetInt("game.fov"));
        Assert.Equal(1.5, store.GetNumber("game.sensitivity"));
        Assert.Equal("Driver", store.GetString("game.name"));
        Assert.Equal("high", store.GetString("game.quality"));
        Assert.False(store.IsDirty);

        Assert.True(store.Save());
        Assert.False(File.Exists(FilePath)); // nothing differs, so there is nothing to write
    }

    [Fact]
    public void SetValuesRoundTripThroughTheFileAndOnlyNonDefaultsAreWritten()
    {
        SettingsStore store = WithGameKeys(Open());
        store.Set("game.hints", Value.Bool(false));
        store.Set("game.fov", Value.Float(100)); // a whole float is fine for an int
        store.Set("game.sensitivity", Value.Float(0.25));
        store.Set("game.name", Value.String("  Ana & <Bo>  "));
        store.Set("game.quality", Value.String("low"));
        store.Set("game.sensitivity", Value.Float(1.5)); // back to the default: not written
        Assert.True(store.Save());

        string xml = File.ReadAllText(FilePath);
        Assert.Equal(
            "<settings version=\"1\">\n"
            + "  <value key=\"game.fov\">100</value>\n"
            + "  <value key=\"game.hints\">false</value>\n"
            + "  <value key=\"game.name\">Ana &amp; &lt;Bo&gt;</value>\n"
            + "  <value key=\"game.quality\">low</value>\n"
            + "</settings>\n",
            xml);
        Assert.False(File.Exists(FilePath + ".tmp"));

        SettingsStore again = WithGameKeys(Open());
        Assert.False(again.GetBool("game.hints"));
        Assert.Equal(100, again.GetInt("game.fov"));
        Assert.Equal(1.5, again.GetNumber("game.sensitivity"));
        Assert.Equal("Ana & <Bo>", again.GetString("game.name"));
        Assert.Equal("low", again.GetString("game.quality"));
    }

    [Fact]
    public void ADefaultThatChangesInAGameUpdateReachesPlayersWhoNeverTouchedIt()
    {
        SettingsStore store = Open();
        store.Define(SettingDef.OfInt("game.fov", 90));
        store.Define(SettingDef.OfBool("game.hints", true));
        store.Set("game.hints", Value.Bool(false));
        Assert.True(store.Save());

        SettingsStore updated = Open();
        updated.Define(SettingDef.OfInt("game.fov", 100));
        updated.Define(SettingDef.OfBool("game.hints", true));
        Assert.Equal(100, updated.GetInt("game.fov"));
        Assert.False(updated.GetBool("game.hints"));
    }

    [Fact]
    public void UnknownKeysSurviveASave()
    {
        File.WriteAllText(FilePath, "<settings version=\"1\">\n  <value key=\"mod.sleepy.nap\">true</value>\n  <value key=\"game.fov\">110</value>\n</settings>\n");
        SettingsStore store = WithGameKeys(Open());
        Assert.Equal(110, store.GetInt("game.fov"));
        Assert.DoesNotContain("mod.sleepy.nap", store.Keys());

        store.Set("game.hints", Value.Bool(false));
        Assert.True(store.Save());

        string xml = File.ReadAllText(FilePath);
        Assert.Contains("<value key=\"mod.sleepy.nap\">true</value>", xml, StringComparison.Ordinal);
        Assert.Contains("<value key=\"game.fov\">110</value>", xml, StringComparison.Ordinal);
        Assert.Contains("<value key=\"game.hints\">false</value>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueReadBeforeItsKeyIsDefinedIsAppliedWhenDefined()
    {
        File.WriteAllText(FilePath, "<settings version=\"1\">\n  <value key=\"mod.late.volume\">0.25</value>\n  <value key=\"mod.late.mode\">turbo</value>\n  <value key=\"mod.late.count\">many</value>\n  <value key=\"mod.late.big\">500</value>\n</settings>\n");
        SettingsStore store = Open();
        var seen = new List<string>();
        store.KeyDefined += seen.Add;

        store.Define(SettingDef.OfNumber("mod.late.volume", 1, min: 0, max: 1));
        store.Define(SettingDef.OfChoice("mod.late.mode", "slow", ["slow", "fast"])); // 'turbo' is not a choice: the default, and the file is not touched until a save
        store.Define(SettingDef.OfInt("mod.late.count", 3)); // not a number: the default
        store.Define(SettingDef.OfInt("mod.late.big", 3, max: 100)); // out of range: clamped

        Assert.Equal(0.25, store.GetNumber("mod.late.volume"));
        Assert.Equal("slow", store.GetString("mod.late.mode"));
        Assert.Equal(3, store.GetInt("mod.late.count"));
        Assert.Equal(100, store.GetInt("mod.late.big"));
        Assert.Equal(["mod.late.volume", "mod.late.mode", "mod.late.count", "mod.late.big"], seen);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void ACorruptFileIsSetAsideAndDefaultsAreUsed()
    {
        File.WriteAllText(FilePath, "<settings version=\"1\"><value key=\"game.fov\">100</value>");
        SettingsStore store = WithGameKeys(Open());

        Assert.Equal(90, store.GetInt("game.fov"));
        Assert.Equal("<settings version=\"1\"><value key=\"game.fov\">100</value>", File.ReadAllText(Path.Combine(_dir.FullName, "settings.bad.xml")));

        store.Set("game.fov", Value.Int(70));
        Assert.True(store.Save());
        Assert.Contains("70", File.ReadAllText(FilePath), StringComparison.Ordinal);

        File.WriteAllText(FilePath, "<options/>"); // well formed, but not ours
        Assert.Equal(90, WithGameKeys(Open()).GetInt("game.fov"));
    }

    [Fact]
    public void WrongTypesAreErrorsAndRangesClamp()
    {
        SettingsStore store = WithGameKeys(Open());

        Assert.Throws<ValueException>(() => store.Set("game.hints", Value.Int(1)));
        Assert.Throws<ValueException>(() => store.Set("game.fov", Value.Float(90.5)));
        Assert.Throws<ValueException>(() => store.Set("game.fov", Value.String("90")));
        Assert.Throws<ValueException>(() => store.Set("game.name", Value.Bool(true)));
        Assert.Throws<ValueException>(() => store.Set("game.quality", Value.String("ultra")));
        Assert.Throws<ValueException>(() => store.Set("game.sensitivity", Value.Float(double.NaN)));
        Assert.Throws<ValueException>(() => store.Set("game.nothing", Value.Int(1)));
        Assert.Throws<ValueException>(() => store.Get("game.nothing"));
        Assert.False(store.IsDirty);

        store.Set("game.fov", Value.Int(500));
        Assert.Equal(120, store.GetInt("game.fov"));
        store.Set("game.sensitivity", Value.Float(-3));
        Assert.Equal(0.1, store.GetNumber("game.sensitivity"));

        Assert.Throws<ValueException>(() => store.Define(SettingDef.OfBool("Game.Loud", false)));
        Assert.Throws<ValueException>(() => store.Define(SettingDef.OfBool("single", false)));
        Assert.Throws<ValueException>(() => store.Define(SettingDef.OfInt("game.range", 5, min: 10, max: 1)));
        Assert.Throws<ValueException>(() => store.Define(SettingDef.OfInt("game.range", 500, min: 1, max: 100)));
        Assert.Throws<ValueException>(() => store.Define(SettingDef.OfChoice("game.empty", "a", [])));
        Assert.Throws<ValueException>(() => store.Define(SettingDef.OfChoice("game.other", "c", ["a", "b"])));
        Assert.Throws<ValueException>(() => store.Define(SettingDef.OfBool("audio.bus.music", true))); // a bus volume is a number
    }

    [Fact]
    public void ChangedFiresOnceOnlyOnARealChange()
    {
        SettingsStore store = WithGameKeys(Open());
        var changes = new List<string>();
        var fovAtTheTime = new List<int>();
        _ = store.Changed.Connect(key =>
        {
            changes.Add(key);
            fovAtTheTime.Add(store.GetInt("game.fov")); // the store has the new value when the signal fires
        });

        store.Set("game.fov", Value.Int(90)); // same as now
        Assert.Empty(changes);
        Assert.False(store.IsDirty);

        store.Set("game.fov", Value.Int(100));
        store.Set("game.fov", Value.Float(100));
        store.Set("game.fov", Value.Int(100));
        Assert.Equal(["game.fov"], changes);
        Assert.True(store.IsDirty);

        store.Reset("game.fov");
        store.Reset("game.fov");
        Assert.Equal(["game.fov", "game.fov"], changes);
        Assert.Equal([100, 90], fovAtTheTime);

        store.Set("game.hints", Value.Bool(false));
        store.Set("game.name", Value.String("Ana"));
        changes.Clear();
        store.ResetAll("game.");
        Assert.Equal(["game.hints", "game.name"], changes);
    }

    [Fact]
    public void RestartKeysSetNeedsRestart()
    {
        SettingsStore store = Open();
        EngineSettings.Register(store, new EngineSettingDefaults());
        Assert.False(store.NeedsRestart);

        store.Set(EngineSettings.WindowFullscreen, Value.Bool(true));
        Assert.False(store.NeedsRestart);

        store.Set(EngineSettings.GraphicsVSync, Value.Bool(false));
        Assert.True(store.NeedsRestart);
        store.Set(EngineSettings.GraphicsVSync, Value.Bool(true)); // back to what this run started with
        Assert.False(store.NeedsRestart);

        store.Set(EngineSettings.GraphicsVSync, Value.Bool(false));
        Assert.True(store.Save());
        SettingsStore next = Open();
        EngineSettings.Register(next, new EngineSettingDefaults());
        Assert.False(next.GetBool(EngineSettings.GraphicsVSync));
        Assert.False(next.NeedsRestart); // the next start is the restart
    }

    [Fact]
    public void SavingIsDebouncedAndFlushedOnDispose()
    {
        var clock = new FakeClock();
        SettingsStore store = WithGameKeys(Open());
        store.Clock = clock;
        TimeSpan second = TimeSpan.FromSeconds(1);

        store.SaveIfDirty(second);
        Assert.False(File.Exists(FilePath)); // nothing to save

        store.Set("game.fov", Value.Int(100));
        store.SaveIfDirty(second); // the first change is written at once
        Assert.Contains("100", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.False(store.IsDirty);

        for (int i = 101; i <= 110; i++)
        {
            store.Set("game.fov", Value.Int(i)); // a slider being dragged
            clock.Advance(TimeSpan.FromMilliseconds(50));
            store.SaveIfDirty(second);
        }

        Assert.Contains("100", File.ReadAllText(FilePath), StringComparison.Ordinal); // still the first write
        Assert.True(store.IsDirty);

        clock.Advance(TimeSpan.FromMilliseconds(600));
        store.SaveIfDirty(second);
        Assert.Contains("110", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.False(store.IsDirty);

        store.Set("game.fov", Value.Int(65));
        store.Flush(); // the end of the session: no waiting
        Assert.Contains("65", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteIsAtomic()
    {
        SettingsStore store = WithGameKeys(Open());
        store.Set("game.fov", Value.Int(100));
        Assert.True(store.Save());
        string before = File.ReadAllText(FilePath);
        Assert.Equal(["settings.xml"], Directory.GetFiles(_dir.FullName).Select(Path.GetFileName)); // the temporary file is gone

        // A read-only mount: the write throws, the old file is whole and the change stays to be saved.
        SettingsStore readOnly = WithGameKeys(Open(writable: false));
        readOnly.Set("game.fov", Value.Int(70));
        Assert.False(readOnly.Save());
        Assert.True(readOnly.IsDirty);
        Assert.Equal(before, File.ReadAllText(FilePath));
        Assert.Equal(["settings.xml"], Directory.GetFiles(_dir.FullName).Select(Path.GetFileName));
    }

    [Fact]
    public void ANewStoreIsInMemoryAndNeverWritten()
    {
        var store = new SettingsStore();
        store.Define(SettingDef.OfBool("game.hints", true));
        store.Set("game.hints", Value.Bool(false));
        Assert.True(store.Save());
        Assert.False(store.IsDirty);
        Assert.Equal([], Directory.GetFiles(_dir.FullName));
    }

    /// <summary>The table a script passes to <c>settings:define</c>.</summary>
    private static ScriptRecord Spec(string type, Value defaultValue) => new ScriptRecord().With("type", Value.String(type)).With("default", defaultValue);

    private static ScriptOwner NewOwner(LuauVm vm, string id, bool mod) => vm.CreateOwner(id, mod);

    [Fact]
    public void AModCanOnlyChangeItsOwnKeys()
    {
        using var vm = new LuauVm();
        ScriptOwner mod = NewOwner(vm, "coolmod", mod: true);
        ScriptOwner other = NewOwner(vm, "other", mod: true);
        SettingsStore store = WithGameKeys(Open());
        EngineSettings.Register(store, new EngineSettingDefaults());

        using (ScriptCaller.Enter(other))
        {
            store.Define("mod.other.flag", Spec("bool", Value.Bool(true)));
            store.Define("audio.bus.other", Spec("number", Value.Float(1)));
        }

        using (ScriptCaller.Enter(mod))
        {
            // It may read everything.
            Assert.Equal(90, store.GetInt("game.fov"));
            Assert.True(store.GetBool("mod.other.flag"));

            // It may define and set its own namespace, and its own audio buses.
            store.Define("mod.coolmod.hints", Spec("bool", Value.Bool(true)));
            store.Define("audio.bus.cool", Spec("number", Value.Float(1)));
            Assert.Equal("coolmod", store.Definition("mod.coolmod.hints")!.Owner);
            _ = Assert.Throws<ValueException>(() => store.Define("game.cheat", Spec("bool", Value.Bool(true))));
            _ = Assert.Throws<ValueException>(() => store.Define("mod.other.more", Spec("bool", Value.Bool(true))));
            _ = Assert.Throws<ValueException>(() => store.Define("mod.coolmodx.more", Spec("bool", Value.Bool(true))));
            _ = Assert.Throws<ValueException>(() => store.Define("window.width", Spec("int", Value.Int(1))));
            store.Set("mod.coolmod.hints", Value.Bool(false));
            store.Set("audio.bus.cool", Value.Float(0.5));
            store.Reset("mod.coolmod.hints");
            store.ResetAll("mod.coolmod.");

            // Nothing else.
            ValueException error = Assert.Throws<ValueException>(() => store.Set("game.fov", Value.Int(100)));
            Assert.Contains("a mod can only change its own settings (mod.coolmod.", error.Message, StringComparison.Ordinal);
            _ = Assert.Throws<ValueException>(() => store.Set("window.fullscreen", Value.Bool(true)));
            _ = Assert.Throws<ValueException>(() => store.Set("mod.other.flag", Value.Bool(false)));
            _ = Assert.Throws<ValueException>(() => store.Set("audio.bus.other", Value.Float(0)));
            _ = Assert.Throws<ValueException>(() => store.Reset("game.fov"));
            _ = Assert.Throws<ValueException>(() => store.ResetAll());
            _ = Assert.Throws<ValueException>(() => store.ResetAll("game."));
            Assert.Equal(90, store.GetInt("game.fov"));
        }

        // The base game and the engine change any of them.
        using (ScriptCaller.Enter(vm.BaseOwner))
        {
            store.Set("game.fov", Value.Int(100));
            store.Set("mod.other.flag", Value.Bool(false));
        }

        store.Set("window.fullscreen", Value.Bool(true));
        Assert.Equal(100, store.GetInt("game.fov"));
    }

    [Fact]
    public void AScriptCannotRedefineAnEngineKeyOrAnotherScriptsOrChangeItsType()
    {
        using var vm = new LuauVm();
        SettingsStore store = Open();
        EngineSettings.Register(store, new EngineSettingDefaults());

        using (ScriptCaller.Enter(vm.BaseOwner))
        {
            store.Define("game.hints", Spec("bool", Value.Bool(true)));
            Assert.Equal("base", store.Definition("game.hints")!.Owner);
            ValueException engine = Assert.Throws<ValueException>(() => store.Define("window.width", Spec("int", Value.Int(800))));
            Assert.Contains("engine", engine.Message, StringComparison.Ordinal);
            ValueException type = Assert.Throws<ValueException>(() => store.Define("game.hints", Spec("int", Value.Int(1))));
            Assert.Contains("defined as bool", type.Message, StringComparison.Ordinal);
            _ = Assert.Throws<ValueException>(() => store.Define("game.more", new ScriptRecord().With("type", Value.String("bool"))));    // no default
            _ = Assert.Throws<ValueException>(() => store.Define("game.more", Spec("float", Value.Float(1))));                              // no such type
            _ = Assert.Throws<ValueException>(() => store.Define("game.more", Spec("bool", Value.Bool(true)).With("colour", Value.Int(1)))); // a typo is an error
            ValueException owner = Assert.Throws<ValueException>(() => store.Define(SettingDef.OfBool("game.hints", false) with { Owner = "othermod" }));
            Assert.Contains("already defined by base", owner.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RedefiningOnReloadKeepsTheValue()
    {
        SettingsStore store = Open();
        store.Define(SettingDef.OfInt("game.fov", 90, min: 60, max: 120, help: "old"));
        store.Set("game.fov", Value.Int(110));
        var changes = new List<string>();
        _ = store.Changed.Connect(changes.Add);

        store.Define(SettingDef.OfInt("game.fov", 95, min: 60, max: 130, help: "new")); // a hot reload: the spec is replaced, the player's value is not
        Assert.Equal(110, store.GetInt("game.fov"));
        Assert.Equal("new", store.Definition("game.fov")!.Help);
        Assert.Equal(95, store.Definition("game.fov")!.Default.ToInt32());
        Assert.Empty(changes);

        store.Define(SettingDef.OfInt("game.fov", 90, min: 60, max: 100)); // a narrower range pulls it in, and says so
        Assert.Equal(100, store.GetInt("game.fov"));
        Assert.Equal(["game.fov"], changes);
    }

    [Fact]
    public void InfoDescribesAKey()
    {
        SettingsStore store = WithGameKeys(Open());
        var info = (ScriptRecord)store.Info("game.fov")!;
        Dictionary<string, Value> fields = info.Fields.ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("int", fields["type"].ToStringValue());
        Assert.Equal(90, fields["default"].ToInt32());
        Assert.Equal(60, fields["min"].ToDouble());
        Assert.Equal(120, fields["max"].ToDouble());
        Assert.False(fields["restart"].ToBool());
        Assert.False(fields.ContainsKey("choices"));

        var quality = (ScriptRecord)store.Info("game.quality")!;
        Assert.Equal(["low", "high"], quality.Fields.First(f => f.Key == "choices").Value.ToList().Select(v => v.ToStringValue()));
        Assert.Null(store.Info("game.nothing"));
        Assert.Equal(["game.fov", "game.hints", "game.name", "game.quality", "game.sensitivity"], store.Keys("game."));
    }

    [Fact]
    public void AudioVolumeAndBusKeysDriveTheAudioSystem()
    {
        using var world = new Apogee.Ecs.World();
        using var audio = new AudioSystem(world, new AudioOptions { Output = AudioOutput.None });
        File.WriteAllText(FilePath, "<settings version=\"1\">\n  <value key=\"audio.bus.music\">0.2</value>\n  <value key=\"audio.volume\">0.5</value>\n</settings>\n");
        SettingsStore store = Open();
        EngineSettings.Register(store, new EngineSettingDefaults());
        EngineSettings.ConnectAudio(store, audio);

        Assert.Equal(0.5f, audio.Volume);

        // A bus is set when its key is defined, with the saved value.
        store.Define(SettingDef.OfNumber("audio.bus.music", 1, min: 0, max: 1));
        Assert.Equal(0.2f, audio.GetBus("music").Volume);
        store.Define(SettingDef.OfNumber("audio.bus.ui", 0.8, min: 0, max: 1));
        Assert.Equal(0.8f, audio.GetBus("ui").Volume);

        store.Set("audio.volume", Value.Float(0.75));
        store.Set("audio.bus.music", Value.Float(0.9));
        Assert.Equal(0.75f, audio.Volume);
        Assert.Equal(0.9f, audio.GetBus("music").Volume);

        store.Set("audio.volume", Value.Float(5)); // clamped by the key's range
        Assert.Equal(1f, audio.Volume);
        store.ResetAll("audio.");
        Assert.Equal(1f, audio.GetBus("music").Volume);
        Assert.Equal(0.8f, audio.GetBus("ui").Volume);
    }

    [Fact]
    public void BindingsAreSavedAndAppliedWhenTheActionIsAdded()
    {
        SettingsStore store = Open();
        var map = new InputActionMap();
        store.Bindings = map;
        map.AddAction("lights", ["vehicle"], ["L"]);
        map.AddAction("horn", ["vehicle"], ["H"]);
        Assert.False(store.IsDirty);

        map.SetBindings("lights", ["Hold L", "Joy RB"]);
        Assert.True(store.IsDirty);
        Assert.True(store.Save());
        Assert.Equal(
            "<settings version=\"1\">\n  <bindings>\n    <action id=\"lights\">Hold L | Joy RB</action>\n  </bindings>\n</settings>\n",
            File.ReadAllText(FilePath));

        // The next run: the mod that adds "lights" loads after the settings.
        SettingsStore next = Open();
        var other = new InputActionMap();
        other.AddAction("horn", ["vehicle"], ["H"]);
        next.Bindings = other;
        Assert.False(other.HasAction("lights"));
        Assert.True(next.Save() && next.IsDirty == false);
        Assert.Contains("lights", File.ReadAllText(FilePath), StringComparison.Ordinal); // kept while its action does not exist

        other.AddAction("lights", ["vehicle"], ["L"]);
        Assert.Equal(["Hold L", "Joy RB"], other.GetBindings("lights"));

        other.Reset("lights");
        Assert.True(next.IsDirty);
        Assert.True(next.Save());
        Assert.Equal("<settings version=\"1\"/>\n", File.ReadAllText(FilePath)); // back to the defaults: nothing differs
    }

    [Fact]
    public void WindowSizeIsStoredOnlyOnceItSettlesAndNotWhileFullscreen()
    {
        SettingsStore store = Open();
        EngineSettings.Register(store, new EngineSettingDefaults(1280, 720));
        using var tracker = new WindowSettings(store);

        for (int i = 0; i < WindowSettings.SettledFrames + 5; i++)
        {
            tracker.Observe(1280, 720);
        }

        Assert.False(store.IsDirty);

        for (int i = 0; i < 10; i++)
        {
            tracker.Observe(1000 + i, 700); // being dragged
        }

        Assert.Equal(1280, store.GetInt("window.width"));
        for (int i = 0; i < WindowSettings.SettledFrames + 2; i++)
        {
            tracker.Observe(1009, 700);
        }

        Assert.Equal(1009, store.GetInt("window.width"));
        Assert.Equal(700, store.GetInt("window.height"));

        store.Set("window.fullscreen", Value.Bool(true)); // the desktop's size is not the window's
        for (int i = 0; i < WindowSettings.SettledFrames + 2; i++)
        {
            tracker.Observe(2560, 1440);
        }

        Assert.Equal(1009, store.GetInt("window.width"));

        using var fixedSize = new WindowSettings(store, trackSize: false);
        store.Set("window.fullscreen", Value.Bool(false));
        for (int i = 0; i < WindowSettings.SettledFrames + 2; i++)
        {
            fixedSize.Observe(640, 480);
        }

        Assert.Equal(1009, store.GetInt("window.width"));
    }

    [Fact]
    public void TheGamesWindowIsTheDefaultOfTheWindowKeys()
    {
        SettingsStore store = Open();
        EngineSettings.Register(store, new EngineSettingDefaults(1920, 1080, Fullscreen: true, VSync: false));
        Assert.Equal(1920, store.GetInt("window.width"));
        Assert.Equal(1080, store.GetInt("window.height"));
        Assert.True(store.GetBool("window.fullscreen"));
        Assert.False(store.GetBool("graphics.vsync"));
        Assert.True(store.Save());
        Assert.False(File.Exists(FilePath)); // the game's window is a default, not a choice of the player

        store.Set("window.width", Value.Int(100)); // below the minimum
        Assert.Equal(320, store.GetInt("window.width"));
    }
}
