using System;
using System.Collections.Generic;
using Apogee.Core.Logging;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;

namespace Apogee.UI.Tests;

/// <summary>A UI system with a Luau VM behind it, as the runtime has them: the VM sees <c>ui</c>, and documents run their scripts in it.</summary>
internal sealed class ScriptedRig : IDisposable
{
    private sealed class Sink(List<string> errors) : ILogSink
    {
        public void Write(in LogEntry entry)
        {
            if (entry.Level >= LogLevel.Warning)
            {
                errors.Add($"{entry.Category}: {entry.Message}");
            }
        }
    }

    private readonly Sink _sink;

    public ScriptedRig(int width = 200, int height = 100, Func<ScriptOwner, string, bool, bool>? canAccessFile = null)
    {
        Rig = new UiRig(width, height);
        Vm = new LuauVm(new LuauVmOptions
        {
            Files = new VfsScriptFiles(Rig.Files),
            FileSystem = Rig.Files,
            CanAccessFile = canAccessFile,
            Output = (_, text, _) => Output.Add(text),
        });
        Vm.AddRoot("res://");
        Rig.Ui.Adopt();
        Rig.Ui.ScriptEnvironments = Vm;
        Vm.SetGlobal("ui", Rig.Ui);
        _sink = new Sink(Problems);
        Log.AddSink(_sink);
    }

    public UiRig Rig { get; }

    public LuauVm Vm { get; }

    public UiSystem Ui => Rig.Ui;

    /// <summary>What scripts printed.</summary>
    public List<string> Output { get; } = [];

    /// <summary>Warnings and errors the engine logged while the rig lived.</summary>
    public List<string> Problems { get; } = [];

    public IReadOnlyList<Value> Run(string source) => Vm.Execute(Vm.BaseOwner, "=test", source);

    public void Frame() => Ui.Update(0.016f);

    /// <summary>Moves the pointer to the middle of the element and clicks.</summary>
    public void Click(UiElement element)
    {
        (float x, float y, float width, float height) = element.Rect();
        Ui.MouseMove((int)(x + (width / 2)), (int)(y + (height / 2)));
        Ui.MouseButton(0, true);
        Ui.MouseButton(0, false);
    }

    public void Dispose()
    {
        Log.RemoveSink(_sink);
        Vm.Dispose();
        Rig.Dispose();
    }
}

[Collection("gpu")]
public sealed class UiScriptTests
{
    [Fact]
    public void AScriptInTheHeadDefinesFunctionsThatTheDocumentsAttributesCall()
    {
        using var rig = new ScriptedRig();
        UiElement document = rig.Run("""
            local doc = ui:load_document_text([[
            <rml><head>
              <script>
                clicks = 0
                function bump(element, event)
                  clicks = clicks + 1
                  print("bump", element.id, event.type, document.tag)
                end
              </script>
            </head><body>
              <div id="button" style="width: 100px; height: 40px" onclick="bump(element, event)"></div>
            </body></rml>]])
            doc:show()
            return doc
            """)[0].ToObject<UiElement>();
        rig.Frame();

        rig.Click(document.ById("button")!);
        rig.Click(document.ById("button")!);

        Assert.Equal(["bump\tbutton\tclick\tbody", "bump\tbutton\tclick\tbody"], rig.Output);
        Assert.Empty(rig.Problems);
        Assert.Equal(1, rig.Ui.ScriptedDocuments);
    }

    [Fact]
    public void AnAttributeAnotherScriptWritesRunsAsThatScriptAndNotAsTheDocumentsOwner()
    {
        using var rig = new ScriptedRig();
        UiElement document = rig.Run("""
            local d = ui:load_document_text([[<rml><head><script>function secret() print("base ran") end</script></head>
              <body><div id="host" style="width: 100px; height: 100px"></div></body></rml>]])
            d:show()
            return d
            """)[0].ToObject<UiElement>();
        rig.Vm.SetGlobal("shared_doc", document);
        rig.Frame();

        // A mod puts an onclick into the base game's document. The handler is the mod's own code: the base document's functions are not in its globals.
        ScriptOwner mod = rig.Vm.CreateOwner("some_mod", isMod: true);
        rig.Vm.Execute(mod, "=mod", """shared_doc:by_id("host").inner_rml = [[<div id="x" style="width: 50px; height: 50px" onclick="print('mod ran'); secret()"></div>]]""");
        rig.Frame();
        rig.Click(document.ById("x")!);

        Assert.Equal(["mod ran"], rig.Output);
        Assert.Contains(rig.Problems, p => p.Contains("attempt to call a nil value", StringComparison.Ordinal)); // secret() is nil for the mod
    }

    [Fact]
    public void ADocumentSaysItHasLoadedAndItsBodyIsThereByThen()
    {
        using var rig = new ScriptedRig();
        rig.Run("""
            ui:load_document_text([[<rml><head><script>
                print("head", document:by_id("later") == nil)
                document:listen("load", function() print("loaded", document:by_id("later") ~= nil) end)
            </script></head><body><div id="later"></div></body></rml>]])
            """);

        Assert.Equal(["head\ttrue", "loaded\ttrue"], rig.Output);
    }

    [Fact]
    public void ADocumentsFunctionsAreItsOwnAndAnotherDocumentDoesNotSeeThem()
    {
        using var rig = new ScriptedRig();
        IReadOnlyList<Value> documents = rig.Run("""
            local a = ui:load_document_text("<rml><head><script>function who() return 'a' end</script></head><body><div id='x' style='width: 50px; height: 50px' onclick=\"print(who())\"></div></body></rml>")
            local b = ui:load_document_text("<rml><head><script>function who() return 'b' end</script></head><body><div id='x' style='width: 50px; height: 50px' onclick=\"print(who())\"></div></body></rml>")
            a:hide()
            b:show()
            return a, b
            """);
        UiElement b = documents[1].ToObject<UiElement>();
        rig.Frame();

        rig.Click(b.ById("x")!);
        Assert.Equal(["b"], rig.Output);
    }

    [Fact]
    public void AnExternalScriptIsReadFromTheFileSystemAndRequiresRelativeToItself()
    {
        using var rig = new ScriptedRig();
        rig.Rig.Write("ui/helper.luau", "return { greet = function(name) return 'hello ' .. name end }");
        rig.Rig.Write("ui/menu.luau", "local helper = require('./helper')\nfunction greet() print(helper.greet('menu')) end\nprint('loaded')");
        rig.Rig.Write("ui/menu.rml", "<rml><head><script src=\"menu.luau\"/></head><body><div id=\"b\" style=\"width: 50px; height: 50px\" onclick=\"greet()\"></div></body></rml>");

        UiElement document = rig.Run("local d = ui:load_document('res://ui/menu.rml'); d:show(); return d")[0].ToObject<UiElement>();
        rig.Frame();
        rig.Click(document.ById("b")!);

        Assert.Equal(["loaded", "hello menu"], rig.Output);
        Assert.Empty(rig.Problems);
    }

    [Fact]
    public void ABrokenScriptOrHandlerIsLoggedAndDoesNotStopTheDocument()
    {
        using var rig = new ScriptedRig();
        UiElement document = rig.Run("""
            local d = ui:load_document_text([[<rml><head><script>this is not luau</script><script>print("second ran")</script></head>
              <body><div id="b" style="width: 50px; height: 50px" onclick="error('boom')"></div><div id="c" style="width: 50px; height: 50px" onclick="((("></div></body></rml>]])
            d:show()
            return d
            """)[0].ToObject<UiElement>();
        rig.Frame();
        rig.Click(document.ById("b")!);
        rig.Click(document.ById("c")!);

        Assert.Equal(["second ran"], rig.Output);
        Assert.Contains(rig.Problems, p => p.Contains("script", StringComparison.Ordinal));
        Assert.Contains(rig.Problems, p => p.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(rig.Problems, p => p.Contains("does not compile", StringComparison.Ordinal));
    }

    [Fact]
    public void AScriptFileTheOwnerMayNotReadIsRefused()
    {
        using var rig = new ScriptedRig(canAccessFile: (_, path, _) => !path.Contains("secret", StringComparison.Ordinal));
        rig.Rig.Write("ui/secret.luau", "print('should not run')");
        rig.Rig.Write("ui/open.luau", "print('ran')");
        rig.Run("""
            ui:load_document_text("<rml><head><script src='res://ui/secret.luau'/><script src='res://ui/open.luau'/></head><body></body></rml>")
            """);

        Assert.Equal(["ran"], rig.Output);
        Assert.Contains(rig.Problems, p => p.Contains("secret.luau", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosingADocumentEndsItsScripts()
    {
        using var rig = new ScriptedRig();
        UiElement document = rig.Run("return ui:load_document_text('<rml><head><script>x = 1</script></head><body></body></rml>')")[0].ToObject<UiElement>();
        Assert.Equal(1, rig.Ui.ScriptedDocuments);
        document.Close();
        rig.Frame(); // RmlUi destroys an unloaded document at its next update
        Assert.Equal(0, rig.Ui.ScriptedDocuments);
    }

    // ---- Data models ----

    /// <summary>A model "stats" over a table, a document that shows it, and a function the test calls to change the table the way a script would.</summary>
    private sealed class StatsRig : IDisposable
    {
        public StatsRig()
        {
            Rig = new ScriptedRig();
            IReadOnlyList<Value> result = Rig.Run("""
                local stats
                stats = {
                    score = 5, name = "Ada", alive = true, player = { hp = 10 },
                    items = { { name = "sword", qty = 1 }, { name = "bow", qty = 2 } },
                    bought = {},
                    buy = function(event, amount, what) table.insert(stats.bought, `{amount} {what} {event.type}`) end,
                }
                local model = ui:open_data_model("stats", stats)
                local doc = ui:load_document_text([[
                <rml><body data-model="stats" style="width: 200px">
                  <div id="score">{{score}}</div>
                  <div id="hp">{{player.hp}}</div>
                  <div id="shown" data-if="alive" style="height: 10px">alive</div>
                  <div id="list"><div class="item" data-for="item : items">{{item.name}} x{{item.qty}}</div></div>
                  <div id="buy" style="width: 50px; height: 20px" data-event-click="buy(3, 'sword')"></div>
                  <input id="name" type="text" data-value="name"/>
                </body></rml>]])
                doc:show()
                local actions = {
                    hp = function(v) stats.player.hp = v end,
                    add_item = function(name, qty) table.insert(stats.items, { name = name, qty = qty }) end,
                    alive = function(v) stats.alive = v end,
                    name = function() return stats.name end,
                    bought = function() return table.concat(stats.bought, ";") end,
                    score = function() return stats.score end,
                }
                return doc, model, function(name, ...) return actions[name](...) end
                """);
            Document = result[0].ToObject<UiElement>();
            Model = result[1].ToObject<UiDataModel>();
            Act = result[2].ToFunction();
            Rig.Frame();
        }

        public ScriptedRig Rig { get; }

        public UiElement Document { get; }

        public UiDataModel Model { get; }

        public ScriptFunction Act { get; }

        public Value Do(string action, params Value[] arguments)
        {
            Span<Value> result = new Value[1];
            Act.Call([Value.String(action), .. arguments], result);
            return result[0];
        }

        public string Html(string id) => Document.ById(id)!.InnerRml;

        /// <summary>The rows of a data-for: its visible children (RmlUi keeps the template element, hidden).</summary>
        public List<string> Rows(string id)
        {
            UiElement parent = Document.ById(id)!;
            var rows = new List<string>();
            for (int i = 0; i < parent.ChildCount; i++)
            {
                UiElement child = parent.Child(i)!;
                if (child.IsVisible)
                {
                    rows.Add(child.InnerRml);
                }
            }

            return rows;
        }

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public void ADataModelShowsTheTablesValuesAndFollowsChangesOnceTheyAreMarkedDirty()
    {
        using var stats = new StatsRig();
        Assert.Equal("5", stats.Html("score"));
        Assert.Equal("10", stats.Html("hp"));

        // The table is read when RmlUi needs a value: a change in a struct shows after Dirty on the whole.
        stats.Do("hp", Value.Float(25));
        stats.Rig.Frame();
        Assert.Empty(stats.Rig.Problems);
        Assert.Equal("10", stats.Html("hp")); // not yet: nobody said it changed
        stats.Model.Dirty("player");
        stats.Rig.Frame();
        Assert.Equal("25", stats.Html("hp"));

        // Set writes a top-level field and marks it dirty in one go.
        Assert.True(stats.Model.Set("score", Value.Float(9)));
        stats.Rig.Frame();
        Assert.Equal("9", stats.Html("score"));
        Assert.Equal(9.0, stats.Do("score").ToDouble());
        Assert.Empty(stats.Rig.Problems);
    }

    [Fact]
    public void DataIfAndDataForFollowTheTable()
    {
        using var stats = new StatsRig();
        Assert.True(stats.Document.ById("shown")!.IsVisible);
        Assert.Equal(["sword x1", "bow x2"], stats.Rows("list"));

        stats.Do("alive", Value.Bool(false));
        stats.Model.Dirty("alive");
        stats.Do("add_item", Value.String("axe"), Value.Float(3));
        stats.Model.Dirty("items");
        stats.Rig.Frame();

        Assert.False(stats.Document.ById("shown")!.IsVisible);
        Assert.Equal(["sword x1", "bow x2", "axe x3"], stats.Rows("list"));
    }

    [Fact]
    public void ADataEventCallsTheTablesFunctionWithTheEventAndTheExpressionsArguments()
    {
        using var stats = new StatsRig();
        stats.Rig.Click(stats.Document.ById("buy")!);
        Assert.Equal("3 sword click", stats.Do("bought").ToStringValue());
        Assert.Empty(stats.Rig.Problems);
    }

    [Fact]
    public void DataValueWritesWhatThePlayerTypesBackIntoTheTable()
    {
        using var stats = new StatsRig();
        UiElement input = stats.Document.ById("name")!;
        Assert.Equal("Ada", input.GetAttribute("value"));

        input.Focus();
        stats.Rig.Ui.Text("Bob");
        stats.Rig.Ui.Key(Apogee.Native.RmlUi.ArKey.Return, true);
        stats.Rig.Ui.Key(Apogee.Native.RmlUi.ArKey.Return, false);
        input.Blur(); // a text field says it changed when it loses the focus
        stats.Rig.Frame();

        Assert.Equal("BobAda", stats.Do("name").ToStringValue()); // typed at the start: the caret was there
    }

    [Fact]
    public void AModelClosesAndTheNameCanBeUsedAgainButNotWhileItIsOpen()
    {
        using var stats = new StatsRig();
        Assert.True(stats.Rig.Run("return ui:open_data_model('stats', { x = 1 })")[0].IsNil);
        Assert.Contains(stats.Rig.Problems, p => p.Contains("'stats' already", StringComparison.Ordinal));

        stats.Document.Close();
        stats.Rig.Frame();
        stats.Model.Close();
        Assert.False(stats.Model.IsOpen);

        UiDataModel reopened = stats.Rig.Run("return ui:open_data_model('stats', { x = 1 })")[0].ToObject<UiDataModel>();
        Assert.True(reopened.IsOpen);
    }
}

/// <summary>An editor that shows the game in a panel feeds the pointer itself and gates the keyboard: UiInput can be told to ignore the pointer and to take keys and text only when asked.</summary>
[Collection("gpu")]
public sealed unsafe class UiInputGateTests
{
    [Fact]
    public void TextAndKeysReachTheUiOnlyWhileTheGateIsOpenAndThePointerCanBeLeftToTheHost()
    {
        Apogee.Native.Tests.NativeRequirements.Require(typeof(Apogee.Native.Sdl.SDL).Assembly, "SDL3");
        Apogee.Platform.PlatformHost.Initialize("dummy");
        try
        {
            using var rig = new UiRig(200, 100);
            UiElement document = rig.Show("<rml><body><input id=\"field\" type=\"text\" style=\"width: 100px; height: 20px\"/><div id=\"hit\" style=\"width: 50px; height: 50px; margin-top: 30px\"/></body></rml>");
            var clicks = new Handler(_ => { });
            document.ById("hit")!.Listen("click", clicks);
            UiElement field = document.ById("field")!;
            field.Focus();

            bool gate = false;
            using var input = new UiInput(rig.Ui, 0) { PointerEnabled = false, KeyboardEnabled = () => gate };

            void Push(Apogee.Native.Sdl.EventType type, string? text = null, float x = 0, float y = 0, byte button = 0)
            {
                Apogee.Native.Sdl.Event e = default;
                e.Type = (uint)type;
                nint memory = text is null ? 0 : System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(text);
                if (type == Apogee.Native.Sdl.EventType.TextInput)
                {
                    e.Text.Text = (byte*)memory;
                }
                else if (type is Apogee.Native.Sdl.EventType.MouseButtonDown or Apogee.Native.Sdl.EventType.MouseButtonUp)
                {
                    e.Button.X = x;
                    e.Button.Y = y;
                    e.Button.Button = button;
                }

                Assert.True(Apogee.Native.Sdl.SDL.PushEvent(&e));
                Apogee.Platform.PlatformHost.PumpEvents();
                if (memory != 0)
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(memory);
                }
            }

            Push(Apogee.Native.Sdl.EventType.TextInput, "ab");
            Assert.Equal("", field.GetAttribute("value") ?? "");            // the gate is shut: nothing typed

            gate = true;
            Push(Apogee.Native.Sdl.EventType.TextInput, "cd");
            rig.Ui.Update(0.016f);
            Assert.Equal("cd", field.GetAttribute("value"));

            // The pointer is the host's: a click event from SDL does nothing while PointerEnabled is false.
            Push(Apogee.Native.Sdl.EventType.MouseButtonDown, x: 10, y: 70, button: 1);
            Push(Apogee.Native.Sdl.EventType.MouseButtonUp, x: 10, y: 70, button: 1);
            Assert.Equal(0, clicks.Calls);
            input.PointerEnabled = true;
            Push(Apogee.Native.Sdl.EventType.MouseButtonDown, x: 10, y: 70, button: 1);
            Push(Apogee.Native.Sdl.EventType.MouseButtonUp, x: 10, y: 70, button: 1);
            Assert.Equal(1, clicks.Calls);
        }
        finally
        {
            Apogee.Platform.PlatformHost.Shutdown();
        }
    }
}
