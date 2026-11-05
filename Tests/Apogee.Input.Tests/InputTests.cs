using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Native.Sdl;
using Apogee.Native.Tests;
using Apogee.Platform;
using Apogee.Reflection;
using Xunit;
using SDL = Apogee.Native.Sdl.SDL;

namespace Apogee.Input.Tests;

public class BindingTests
{
    [Theory]
    [InlineData("W", "W", Modifiers.None, Trigger.Press)]
    [InlineData("Shift+Tab", "Tab", Modifiers.Shift, Trigger.Press)]
    [InlineData("hold ctrl+alt+V", "V", Modifiers.Ctrl | Modifiers.Alt, Trigger.Hold)]
    [InlineData("Double Shift", "Shift", Modifiers.None, Trigger.DoubleTap)]
    [InlineData("mouse wheel up", "Mouse Wheel Up", Modifiers.None, Trigger.Press)]
    [InlineData("Ctrl+Mouse Left", "Mouse Left", Modifiers.Ctrl, Trigger.Press)]
    [InlineData("Joy LX-", "Joy LX-", Modifiers.None, Trigger.Press)]
    [InlineData("Hold joy a", "Joy A", Modifiers.None, Trigger.Hold)]
    [InlineData("  bracketleft ", "BracketLeft", Modifiers.None, Trigger.Press)]
    public void ReadsTheTextForm(string text, string input, Modifiers modifiers, Trigger trigger)
    {
        Binding binding = Binding.Parse(text);
        Assert.Equal(new Binding(input, modifiers, trigger), binding);
        Assert.Equal(binding, Binding.Parse(binding.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hold Alt+")]
    [InlineData("Mouse Thumb")]
    [InlineData("Joy Z")]
    [InlineData("Shift+Joy A")]
    [InlineData("Banana")]
    public void RefusesWrongOnes(string text)
    {
        Assert.False(Binding.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
        Assert.Throws<ValueException>(() => Binding.Parse(text));
    }

    [Fact]
    public void WritesModifiersInOneOrder()
    {
        Assert.Equal("Hold Ctrl+Shift+Alt+L", Binding.Parse("hold alt+shift+ctrl+l").ToString());
    }

    [Fact]
    public void EveryKeyNameRoundTripsAndHasAKey()
    {
        var seen = new HashSet<Scancode>();
        foreach (string name in KeyNames.All)
        {
            Assert.True(KeyNames.TryParse(name, out Scancode key, out string? written), name);
            Assert.Equal(name, written);
            Assert.Equal(name, KeyNames.NameOf(key));
            Assert.True(seen.Add(key), $"{name} shares a key");
            Assert.Equal(name, Binding.Parse(name).Input);
        }

        // The ranges are built from consecutive scancodes: spot-check the ends.
        Assert.Equal("Z", KeyNames.NameOf(Scancode.Z));
        Assert.Equal("1", KeyNames.NameOf(Scancode._1));
        Assert.Equal("0", KeyNames.NameOf(Scancode._0));
        Assert.Equal("F12", KeyNames.NameOf(Scancode.F12));
        Assert.Equal("F24", KeyNames.NameOf(Scancode.F24));
        Assert.Equal("Kp9", KeyNames.NameOf(Scancode.Kp9));
        Assert.Equal("Ctrl", KeyNames.NameOf(Scancode.Rctrl));
        Assert.Equal(Scancode.Lshift, Binding.Parse("Shift").Input == "Shift" && KeyNames.TryParse("Shift", out Scancode left, out _) ? left : default);
    }

    [Fact]
    public void KnowsWhichInputsAreAxes()
    {
        Assert.True(Binding.IsAxis("Joy LX-"));
        Assert.True(Binding.IsAxis("Mouse Y+"));
        Assert.True(Binding.IsAxis("Joy RT"));
        Assert.False(Binding.IsAxis("Joy A"));
        Assert.False(Binding.IsAxis("Mouse Left"));
        Assert.False(Binding.IsAxis("Minus")); // "-" ends a stick direction only on a gamepad or the mouse
    }
}

public class ActionMapTests
{
    internal static InputActionMap Catalog()
    {
        var map = new InputActionMap();
        map.AddSituation(["world", "on_foot"]);
        map.AddSituation(["world", "vehicle", "mouse_tool"]);
        map.AddSituation(["menu"]);
        string[] moving = ["on_foot", "vehicle"];
        string[] driving = ["vehicle"];
        map.AddAction("move_forward", moving, ["W", "Joy LY-"], true);
        map.AddAction("move_left", moving, ["A", "Joy LX-"], true);
        map.AddAction("brake", driving, ["Space", "Joy LT"], true);
        map.AddAction("lower", driving, ["V", "Joy A"]);
        map.AddAction("lights", driving, ["L"]);
        map.AddAction("tip_side", driving, ["Shift+U"]);
        map.AddAction("tip_ground", driving, ["Ctrl+U"]);
        map.AddAction("unload", driving, ["U", "Joy Up"]);
        map.AddAction("camera_left", ["world"], ["Q"]);
        map.AddAction("tab_left", ["menu"], ["Q"]);
        return map;
    }

    [Fact]
    public void DefaultsClashNowhere() => Assert.Empty(Catalog().AllConflicts());

    [Fact]
    public void ClashesPerContext()
    {
        InputActionMap map = Catalog();
        // Q turns the camera walking and driving, so lowering on it would clash; the menu's Q never meets them.
        Assert.Equal(["camera_left"], map.Conflicts("lower", Binding.Parse("Q")));
        Assert.Empty(map.Conflicts("tab_left", Binding.Parse("Tab")));
        // Other modifiers or triggers are told apart; an analog action takes its input whatever they are.
        Assert.Empty(map.Conflicts("lower", Binding.Parse("Ctrl+V")));
        Assert.Empty(map.Conflicts("lower", Binding.Parse("Hold B")));
        Assert.Equal(["move_left"], map.Conflicts("lights", Binding.Parse("Ctrl+A")));

        map.SetBindings("lights", ["Q"]);
        BindingConflict conflict = Assert.Single(map.AllConflicts());
        Assert.Equal(("lights", "camera_left", "Q"), (conflict.Action, conflict.Other, conflict.Binding.ToString()));
        Assert.Equal(["lights\tcamera_left\tQ"], map.GetAllConflicts());
    }

    [Fact]
    public void WithoutSituationsContextsClashOnlyWhenTheyAreShared()
    {
        var map = new InputActionMap();
        map.AddAction("a", ["x"], ["E"]);
        map.AddAction("b", ["y"], ["E"]);
        map.AddAction("c", ["x", "z"], ["E"]);
        Assert.Equal([("a", "c")], map.AllConflicts().Select(c => (c.Action, c.Other)));
    }

    [Fact]
    public void SaveWritesWhatDiffersAndLoadReadsItBack()
    {
        InputActionMap map = Catalog();
        map.SetBindings("lights", ["Hold L", "Joy RB"]);
        map.SetBindings("lower", []);
        IReadOnlyDictionary<string, string[]> saved = map.Save();
        Assert.Equal(["Hold L", "Joy RB"], saved["lights"]);
        Assert.Empty(saved["lower"]);
        Assert.False(saved.ContainsKey("unload"));

        InputActionMap other = Catalog();
        string[] errors = other.Load(new Dictionary<string, string[]> { ["lights"] = ["Hold L", "Joy RB"], ["nope"] = ["X"], ["lower"] = ["Joy Q"] });
        Assert.Equal(["Hold L", "Joy RB"], other.GetBindings("lights"));
        Assert.Equal(2, errors.Length);
    }

    [Fact]
    public void SaveTextRoundTrips()
    {
        InputActionMap map = Catalog();
        map.SetBindings("lights", ["Hold L", "Joy RB"]);
        map.SetBindings("lower", []);
        string text = map.SaveText();
        Assert.Equal("lower = \nlights = Hold L | Joy RB\n", text);

        InputActionMap other = Catalog();
        Assert.Empty(other.LoadText("# a comment\n" + text));
        Assert.Equal(map.GetBindings("lights"), other.GetBindings("lights"));
        Assert.Empty(other.GetBindings("lower"));
        Assert.Equal(2, other.LoadText("nonsense\nghost = A").Length);
    }

    [Fact]
    public void ResetGoesBackToTheDefaults()
    {
        InputActionMap map = Catalog();
        map.SetBindings("lights", ["K"]);
        map.Reset("lights");
        Assert.Equal(["L"], map.GetBindings("lights"));
        map.SetBindings("lights", ["K"]);
        map.ResetAll();
        Assert.Equal(["L"], map.GetBindings("lights"));
    }

    [Fact]
    public void ReportsMistakesToTheScript()
    {
        InputActionMap map = Catalog();
        Assert.Throws<ValueException>(() => map.AddAction("lights", ["x"], []));
        Assert.Throws<ValueException>(() => map.AddAction("new", ["x"], ["Banana"]));
        Assert.Throws<ValueException>(() => map.SetBindings("ghost", []));
        Assert.Equal("", InputActionMap.BindingError("W"));
        Assert.Contains("unknown key", InputActionMap.BindingError("Banana"));
        Assert.Equal("Hold Shift+Tab", InputActionMap.NormalizeBinding(" hold SHIFT+tab"));
    }
}

public class RouterTests
{
    private static InputActionRouter Router(string[] contexts, InputActionMap? map = null) => new() { Map = map ?? ActionMapTests.Catalog(), Contexts = contexts };

    private static readonly string[] s_driving = ["world", "vehicle", "mouse_tool"];
    private static readonly string[] s_onFoot = ["world", "on_foot"];
    private static readonly string[] s_menu = ["menu"];

    [Fact]
    public void TheMostSpecificModifiersWin()
    {
        InputActionRouter r = Router(s_driving);
        r.Press("U", Modifiers.Shift, 0);
        Assert.Equal(["tip_side"], r.Poll(0));
    }

    [Fact]
    public void TiesFireEveryMatch()
    {
        InputActionRouter r = Router(s_driving);
        r.Press("U", Modifiers.Ctrl | Modifiers.Shift, 0);
        Assert.Equal(["tip_side", "tip_ground"], r.Poll(0));
        r.Release("U", 0.1);
        r.Press("U", Modifiers.None, 1);
        Assert.Equal(["unload"], r.Poll(1));
    }

    [Fact]
    public void KeysDoWhatTheContextNeeds()
    {
        InputActionRouter r = Router(s_onFoot);
        r.Press("Q", Modifiers.None, 0);
        r.Release("Q", 0.1);
        r.Contexts = s_menu;
        r.Press("Q", Modifiers.None, 1);
        r.Press("V", Modifiers.None, 1);
        r.Press("W", Modifiers.None, 1);
        Assert.Equal(["camera_left", "tab_left"], r.Poll(1));
        Assert.Equal(0, r.GetStrength("move_forward"));
        r.Contexts = s_onFoot;
        Assert.Equal(1, r.GetStrength("move_forward"));
    }

    [Fact]
    public void AQuickReleaseFiresThePressWhenTheKeyAlsoHasAHold()
    {
        InputActionMap map = ActionMapTests.Catalog();
        map.AddAction("helper", ["vehicle"], ["Hold L"]);
        InputActionRouter r = Router(s_driving, map);

        r.Press("L", Modifiers.None, 0);
        Assert.Empty(r.Poll(0.2));
        r.Release("L", 0.25);
        Assert.Equal(["lights"], r.Poll(0.25));

        r.Press("L", Modifiers.None, 1);
        Assert.Empty(r.Poll(1.3));
        Assert.Equal(["helper"], r.Poll(1.41));
        r.Release("L", 2);
        Assert.Empty(r.Poll(2));
    }

    [Fact]
    public void ADoubleTapReplacesTwoPresses()
    {
        InputActionMap map = ActionMapTests.Catalog();
        map.AddAction("steering", ["vehicle"], ["Double L"]);
        InputActionRouter r = Router(s_driving, map);

        r.Press("L", Modifiers.None, 0);
        r.Release("L", 0.1);
        r.Press("L", Modifiers.None, 0.3);
        r.Release("L", 0.35);
        Assert.Equal(["steering"], r.Poll(1));

        // A single tap fires once the second did not come.
        r.Press("L", Modifiers.None, 2);
        r.Release("L", 2.1);
        Assert.Empty(r.Poll(2.3));
        Assert.Equal(["lights"], r.Poll(2.41));
    }

    [Fact]
    public void AnalogActionsReadKeysAndSticksPastTheDeadZone()
    {
        InputActionRouter r = Router(s_driving);
        r.Press("Space", Modifiers.Ctrl, 0);
        Assert.True(r.IsHeld("brake"));
        Assert.Empty(r.Poll(0)); // analog actions do not fire

        r.Axis("Joy LY", -0.6f, 0);
        Assert.Equal(0.5f, r.GetStrength("move_forward"), 1e-3f);
        r.Axis("Joy LY", 0.15f, 0);
        Assert.Equal(0, r.GetStrength("move_forward"));
        r.Axis("Joy LT", 1, 0);
        r.Release("Space", 0);
        Assert.True(r.IsHeld("brake"));
    }

    [Fact]
    public void TheMouseMovesInPixelsPerFrame()
    {
        InputActionMap map = ActionMapTests.Catalog();
        map.AddAction("look_right", ["world"], ["Mouse X+"], analog: true);
        InputActionRouter r = Router(s_driving, map);
        r.Axis("Mouse X", 12, 0);
        r.Axis("Mouse X", -2, 0);
        r.Axis("Mouse X", 3, 0);
        Assert.Equal(15, r.GetStrength("look_right"));
        r.EndFrame();
        Assert.Equal(0, r.GetStrength("look_right"));
    }

    [Fact]
    public void AStickPastHalfWayIsAButtonWithHysteresis()
    {
        InputActionMap map = ActionMapTests.Catalog();
        map.AddAction("next", ["vehicle"], ["Joy RX+"]);
        InputActionRouter r = Router(s_driving, map);
        r.Axis("Joy RX", 0.3f, 0);
        Assert.Empty(r.Poll(0));
        r.Axis("Joy RX", 0.7f, 0);
        r.Axis("Joy RX", 0.45f, 0.1); // still down: released below 0.4 only
        r.Axis("Joy RX", 0.9f, 0.2);
        Assert.Equal(["next"], r.Poll(0.2));
        r.Axis("Joy RX", 0, 0.3);
        r.Axis("Joy RX", 0.8f, 0.4);
        Assert.Equal(["next"], r.Poll(0.4));
    }

    [Fact]
    public void ContextChangesAndLostFocusDropWhatWasPending()
    {
        InputActionMap map = ActionMapTests.Catalog();
        map.AddAction("helper", ["vehicle"], ["Hold H"]);
        InputActionRouter r = Router(s_driving, map);
        r.Press("H", Modifiers.None, 0);
        r.Contexts = s_menu;
        Assert.Empty(r.Poll(1));
        r.Contexts = s_driving;
        r.ReleaseAll();
        r.Press("H", Modifiers.None, 2);
        r.ReleaseAll();
        Assert.Empty(r.Poll(3));
        Assert.Empty(r.GetDown());
    }

    [Fact]
    public void UpdateRaisesTheSignalAndListeningCapturesTheNextInput()
    {
        InputActionRouter r = Router(s_driving);
        var fired = new List<string>();
        var captured = new List<string>();
        r.ActionFired.Connect(fired.Add);
        r.Captured.Connect(captured.Add);

        r.Press("L", Modifiers.None, 0);
        r.Release("L", 0.1);
        r.Update(0.1);
        Assert.Equal(["lights"], fired);

        r.Listen();
        r.Press("Shift", Modifiers.None, 1); // a modifier alone does not count
        r.Press("F", Modifiers.Shift, 1);
        r.Update(1);
        Assert.Equal(["Shift+F"], captured);
        Assert.Equal(["lights"], fired); // the captured press was not routed

        r.Listen();
        r.Axis("Joy LY", -1, 2);
        r.Update(2);
        Assert.Equal(["Shift+F", "Joy LY-"], captured);
        Assert.True(r.IsGamepadLast);
    }

    [Fact]
    public void LabelsNameTheButtonsOfTheDeviceUsedLast()
    {
        InputActionMap map = ActionMapTests.Catalog();
        InputActionRouter r = Router(s_driving, map);
        Assert.Equal("–", r.GetLabel("ghost"));
        r.Press("Joy A", Modifiers.None, 0);
        Assert.Equal("Pad A", r.GetLabel("lower"));
        r.Press("V", Modifiers.None, 0);
        Assert.False(r.IsGamepadLast);
        Assert.Equal("V", r.GetLabel("lower")); // no video system here: the key's own name
        Assert.Equal("Hold Ctrl+Left mouse", InputActionRouter.LabelBinding("Hold Ctrl+Mouse Left"));
        Assert.Equal("Wheel up", InputActionRouter.LabelBinding("Mouse Wheel Up"));
        Assert.Equal("Mouse back", InputActionRouter.LabelBinding("Mouse Back"));
        Assert.Equal("Shift+Tab", InputActionRouter.LabelBinding("Shift+Tab"));
    }

    [Fact]
    public void TheClassIsDescribedToScripts()
    {
        ClassInfo info = TypeRegistry.GetClass("InputActionRouter")!;
        Assert.NotNull(info.FindMethod("press"));
        Assert.NotNull(info.FindSignal("action_fired"));
        Assert.NotNull(info.FindProperty("contexts"));
        Assert.NotNull(TypeRegistry.GetClass("InputActionMap")!.FindMethod("add_action"));
    }
}

/// <summary>SDL is process-wide state, so these run one at a time, on SDL's "dummy" video driver.</summary>
public unsafe class DeviceTests : IDisposable
{
    private readonly InputActionRouter _router;
    private readonly InputDevices _devices;

    public DeviceTests()
    {
        NativeRequirements.Require(typeof(SDL).Assembly, "SDL3");
        PlatformHost.Initialize("dummy");
        InputActionMap map = ActionMapTests.Catalog();
        map.AddAction("run", ["on_foot", "vehicle"], ["Shift"], analog: true);
        map.AddAction("shout", ["on_foot", "vehicle"], ["Shift+H"]);
        map.AddAction("hat", ["on_foot", "vehicle"], ["Joy Up"]);
        map.AddAction("zoom", ["on_foot", "vehicle"], ["Mouse Wheel Up"]);
        _router = new InputActionRouter { Map = map, Contexts = ["on_foot", "vehicle"] };
        _devices = new InputDevices(_router);
    }

    public void Dispose()
    {
        _devices.Dispose();
        PlatformHost.Shutdown();
        GC.SuppressFinalize(this);
    }

    private static void Push(Event e) => Assert.True(SDL.PushEvent(&e));

    private static void Key(Scancode key, bool down, bool repeat = false)
    {
        Event e = default;
        e.Type = (uint)(down ? EventType.KeyDown : EventType.KeyUp);
        e.Key.Scancode = key;
        e.Key.Down = down;
        e.Key.Repeat = repeat;
        Push(e);
    }

    [Fact]
    public void KeysGoByTheirPositionAndCarryTheModifiersHeld()
    {
        Key(Scancode.Lshift, true);
        Key(Scancode.H, true);
        PlatformHost.PumpEvents();
        Assert.Equal(["shout"], _router.Poll(0));
        Assert.True(_router.IsHeld("run"));
        Assert.Equal(["H", "Shift"], _router.GetDown());

        // Left and right Shift are one input: it is up when both are.
        Key(Scancode.Rshift, true);
        Key(Scancode.Lshift, false);
        PlatformHost.PumpEvents();
        Assert.True(_router.IsHeld("run"));
        Key(Scancode.Rshift, false);
        PlatformHost.PumpEvents();
        Assert.False(_router.IsHeld("run"));
    }

    [Fact]
    public void KeyRepeatIsIgnoredAndUnnamedKeysToo()
    {
        Key(Scancode.H, true);
        Key(Scancode.H, true, repeat: true);
        Key(Scancode.MediaPlay, true);
        PlatformHost.PumpEvents();
        Assert.Equal(["H"], _router.GetDown());
    }

    [Fact]
    public void ACapturedKeyboardKeepsNewPressesOutButLetsReleasesIn()
    {
        bool typing = false;
        _devices.KeyboardCaptured = () => typing;
        Key(Scancode.W, true);
        PlatformHost.PumpEvents();
        typing = true;
        Key(Scancode.A, true);
        Key(Scancode.W, false);
        PlatformHost.PumpEvents();
        Assert.Empty(_router.GetDown());
    }

    [Fact]
    public void MouseButtonsMotionAndTheWheel()
    {
        Event e = default;
        e.Type = (uint)EventType.MouseButtonDown;
        e.Button.Button = 3;
        e.Button.Down = true;
        Push(e);
        e = default;
        e.Type = (uint)EventType.MouseMotion;
        e.Motion.Xrel = 4;
        Push(e);
        e = default;
        e.Type = (uint)EventType.MouseWheel;
        e.Wheel.Y = 1;
        Push(e);
        PlatformHost.PumpEvents();

        Assert.Equal(["Mouse Right"], _router.GetDown());
        Assert.Equal(["zoom"], _router.Poll(0));
    }

    [Fact]
    public void GamepadButtonsAndAxes()
    {
        Event e = default;
        e.Type = (uint)EventType.GamepadButtonDown;
        e.Gbutton.Button = (byte)GamepadButton.DpadUp;
        e.Gbutton.Down = true;
        Push(e);
        e = default;
        e.Type = (uint)EventType.GamepadAxisMotion;
        e.Gaxis.Axis = (byte)GamepadAxis.Lefty;
        e.Gaxis.Value = short.MinValue;
        Push(e);
        PlatformHost.PumpEvents();

        Assert.Equal(["unload", "hat"], _router.Poll(0)); // the catalog binds Joy Up to unload too
        Assert.Equal(1, _router.GetStrength("move_forward"));
        Assert.True(_router.IsGamepadLast);
    }

    [Fact]
    public void LosingFocusLetsGoOfEverything()
    {
        Key(Scancode.W, true);
        PlatformHost.PumpEvents();
        Assert.NotEmpty(_router.GetDown());

        Event e = default;
        e.Type = (uint)EventType.WindowFocusLost;
        Push(e);
        PlatformHost.PumpEvents();
        Assert.Empty(_router.GetDown());
        Key(Scancode.W, false); // the release of a key we already let go of changes nothing
        PlatformHost.PumpEvents();
        Assert.Empty(_router.GetDown());
    }

    [Fact]
    public void KeyLabelsFollowTheKeyboardLayout()
    {
        // On the dummy driver's keymap (US) the key in W's place types a "w".
        Assert.Equal("W", InputActionRouter.LabelBinding("W"));
        Assert.Equal("Shift+Tab", InputActionRouter.LabelBinding("Shift+Tab"));
        Assert.Equal("Space", InputActionRouter.LabelBinding("Space"));
        Assert.Equal("Ctrl", InputActionRouter.LabelBinding("Ctrl"));
    }
}
