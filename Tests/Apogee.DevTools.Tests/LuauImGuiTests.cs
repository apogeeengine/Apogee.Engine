using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.DevTools;
using Apogee.Native.CImGui;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;

namespace Apogee.DevTools.Tests;

/// <summary>The <c>imgui</c> and <c>devtools</c> tables of Luau, run against real ImGui frames (no window, no GPU).</summary>
[Collection("imgui")]
public sealed unsafe class LuauImGuiTests : IDisposable
{
    private readonly ImGuiHost _host;
    private readonly LuauVm _vm;
    private readonly List<string> _printed = [];

    public LuauImGuiTests()
    {
        NativeRequirements.Require(typeof(ImGui).Assembly, "cimgui");
        NativeRequirements.Require(typeof(Apogee.Native.Luau.Lua).Assembly, "luau");
        ScriptGui.Clear();

        // The library registers from its assembly's module initializer, which runs when the assembly is first used.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(ImGuiLibrary).Module.ModuleHandle);
        _host = new ImGuiHost();
        _vm = new LuauVm(new LuauVmOptions { Output = (_, text, _) => _printed.Add(text) });
    }

    public void Dispose()
    {
        _vm.Dispose();
        _host.Dispose();
        ScriptGui.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs the script in one ImGui frame and returns what it returned.</summary>
    private IReadOnlyList<Value> InFrame(string script, float width = 800, float height = 600)
    {
        _host.BeginFrame(width, height, 1f, 1f / 60f);
        try
        {
            return _vm.Execute(_vm.BaseOwner, "=gui", script);
        }
        finally
        {
            _host.EndFrame();
        }
    }

    private static int VertexCount() => ImGui.GetDrawData()->TotalVtxCount;

    /// <summary>A new window is hidden for its first frame (ImGui sizes it first): this runs the script twice and returns the second frame's results.</summary>
    private IReadOnlyList<Value> InTwoFrames(string script)
    {
        InFrame(script);
        return InFrame(script);
    }

    [Fact]
    public void AScriptDrawsAWindowWithWidgetsThatReturnWhatTheyWereGiven()
    {
        IReadOnlyList<Value> results = InTwoFrames(@"
            local visible = imgui.begin_window('Test')
            imgui.text('hello')
            local clicked = imgui.button('Press')
            local changed, on = imgui.checkbox('On', true)
            local changed2, speed = imgui.slider_float('Speed', 0.5, 0, 1)
            local changed3, count = imgui.slider_int('Count', 3, 0, 10)
            local changed4, name = imgui.input_text('Name', 'rex')
            local changed5, index = imgui.combo('Pick', 2, { 'a', 'b', 'c' })
            local changed6, position = imgui.drag_float3('Position', Vector3(1, 2, 3))
            local changed7, color = imgui.color_edit('Tint', { 1, 0.5, 0.25, 1 })
            imgui.end_window()
            return visible, clicked, changed, on, speed, count, name, index, position, #color");

        Assert.True(results[0].ToBool());
        Assert.False(results[1].ToBool()); // nobody clicked
        Assert.False(results[2].ToBool());
        Assert.True(results[3].ToBool());
        Assert.Equal(0.5, results[4].ToDouble());
        Assert.Equal(3, results[5].ToInt32());
        Assert.Equal("rex", results[6].ToStringValue());
        Assert.Equal(2, results[7].ToInt32()); // an index counts from 1, as in Luau
        Assert.Equal(new System.Numerics.Vector3(1, 2, 3), results[8].ToVector3());
        Assert.Equal(4, results[9].ToInt32());
        Assert.True(VertexCount() > 0); // and it was drawn
    }

    [Fact]
    public void ABeginHasItsEndAndTheFrameDrawsSomething()
    {
        InTwoFrames("imgui.begin_window('Empty') imgui.end_window()");
        int empty = VertexCount();

        InTwoFrames("imgui.begin_window('Full') for i = 1, 20 do imgui.text('line ' .. i) end imgui.end_window()");

        Assert.True(VertexCount() > empty);
    }

    [Fact]
    public void WithNoFrameOpenEveryCallDoesNothingAndReturnsWhatItWasGiven()
    {
        // No BeginFrame: the host is idle. Tool code stays in a game that has no ImGui frame.
        IReadOnlyList<Value> results = _vm.Execute(_vm.BaseOwner, "=idle", @"
            local visible, open = imgui.begin_window('Ghost', true)
            local clicked = imgui.button('x')
            local changed, on = imgui.checkbox('c', true)
            local changed2, value = imgui.slider_float('s', 0.25, 0, 1)
            imgui.text('nothing') imgui.end_window()
            return visible, open, clicked, changed, on, value, devtools.frame_open()");

        Assert.False(results[0].ToBool()); // not visible: nothing is open
        Assert.True(results[1].ToBool()); // the open flag it was given comes back
        Assert.False(results[2].ToBool());
        Assert.False(results[3].ToBool());
        Assert.True(results[4].ToBool());
        Assert.Equal(0.25, results[5].ToDouble());
        Assert.False(results[6].ToBool());
    }

    [Fact]
    public void AGuiFunctionRunsInsideTheFrame()
    {
        _vm.Execute(_vm.BaseOwner, "=register", @"
            gui_id = devtools.on_gui(function()
                imgui.begin_window('From a callback')
                imgui.text('open: ' .. tostring(devtools.frame_open()))
                imgui.end_window()
            end)");
        Assert.Equal(1, ScriptGui.CallbackCount);

        for (int frame = 0; frame < 2; frame++)
        {
            _host.BeginFrame(800, 600, 1f, 1f / 60f);
            ScriptGui.RunGui();
            _host.EndFrame();
        }

        Assert.True(VertexCount() > 0);
    }

    [Fact]
    public void AGuiFunctionThatFailsIsLoggedAndRemoved()
    {
        _vm.Execute(_vm.BaseOwner, "=register", "devtools.on_gui(function() error('gui broke') end) devtools.on_gui(function() print('still here') end)");
        Assert.Equal(2, ScriptGui.CallbackCount);

        for (int i = 0; i < 3; i++)
        {
            _host.BeginFrame(800, 600, 1f, 1f / 60f);
            ScriptGui.RunGui();
            _host.EndFrame();
        }

        Assert.Equal(1, ScriptGui.CallbackCount);
        Assert.Equal(["still here", "still here", "still here"], _printed);
    }

    [Fact]
    public void ACallbackCanBeRemoved()
    {
        _vm.Execute(_vm.BaseOwner, "=register", "local id = devtools.on_gui(function() end) devtools.remove_gui(id)");

        Assert.Equal(0, ScriptGui.CallbackCount);
    }

    [Fact]
    public void AScriptThatForgetsAnEndOrFailsInTheMiddleDoesNotBreakTheFrames()
    {
        InFrame("imgui.begin_window('Never ended') imgui.begin_table('t', 2) imgui.text('x')");
        InFrame("imgui.begin_window('Fine') imgui.text('x') imgui.end_window()");
        Assert.Throws<ScriptException>(() => InFrame("imgui.begin_window('Fails') imgui.begin_child('c') error('midway')"));
        InTwoFrames("imgui.begin_window('Fine again') imgui.text('x') imgui.end_window()");

        Assert.True(VertexCount() > 0);
    }

    [Fact]
    public void FlagsAreListsOfNamesAndAMistakeNamesTheChoices()
    {
        IReadOnlyList<Value> results = InFrame(@"
            imgui.set_next_window_pos(10, 10, 'always')
            imgui.set_next_window_size(200, 100, 'always')
            local visible = imgui.begin_window('Flags', nil, { 'no_title_bar', 'no_resize' })
            imgui.end_window()
            return visible");
        Assert.True(results[0].ToBool());

        _host.BeginFrame(800, 600, 1f, 1f / 60f);
        try
        {
            ScriptException error = Assert.Throws<ScriptException>(() => _vm.Execute(_vm.BaseOwner, "=bad", "imgui.begin_window('x', nil, { 'no_titlebar' })"));
            Assert.Contains("'no_titlebar' is not a ImGuiWindowFlags; use one of: none, no_title_bar", error.Message);
        }
        finally
        {
            _host.EndFrame();
        }
    }

    [Fact]
    public void AnImGuiButtonIsClickedByTheMouse()
    {
        ImGuiIO* io = _host.IO;
        const string Script = @"
            imgui.set_next_window_pos(0, 0, 'always')
            imgui.set_next_window_size(300, 200, 'always')
            imgui.begin_window('Clicks', nil, { 'no_title_bar', 'no_move', 'no_resize' })
            local clicked = imgui.button('Press me')
            imgui.end_window()
            return clicked";

        // The window exists after a frame or two; then move onto the button, press, release: ImGui reports the click on the release.
        ImGui.ImGuiIOAddMousePosEvent(io, 30, 20);
        InTwoFrames(Script);
        Assert.False(InFrame(Script)[0].ToBool());
        ImGui.ImGuiIOAddMouseButtonEvent(io, 0, true);
        Assert.False(InFrame(Script)[0].ToBool());
        ImGui.ImGuiIOAddMouseButtonEvent(io, 0, false);
        Assert.True(InFrame(Script)[0].ToBool());
    }

    [Fact]
    public void WatchedValuesAreKeptByName()
    {
        _vm.Execute(_vm.BaseOwner, "=watch", "devtools.watch('fps', 60) devtools.watch('mode', 'fast') devtools.watch('fps', 59.5)");

        Assert.Equal(new Dictionary<string, string> { ["fps"] = "59.5", ["mode"] = "fast" }, ScriptGui.Watches.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void TheDefinitionsDeclareTheLibrariesWithTheirEnums()
    {
        string text = DefinitionWriter.Write().ReplaceLineEndings("\n");

        Assert.Contains("declare imgui: {\n", text);
        Assert.Contains("    text: (text: string) -> (),\n", text);
        Assert.Contains("    checkbox: (label: string, value: boolean) -> (boolean, boolean),\n", text);
        Assert.Contains("    combo: (label: string, index: number, items: { string }) -> (boolean, number),\n", text);
        Assert.Contains("    begin_window: (title: string, open: any, flags: ({ ImGuiWindowFlags } | {})?) -> (boolean, boolean),\n", text);
        Assert.Contains("type ImGuiWindowFlags = \"none\" | \"no_title_bar\"", text);
        Assert.Contains("declare devtools: {\n", text);
        Assert.Contains("    on_gui: (callback: (...any) -> ...any) -> number,\n", text);
    }
}
