using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Native.RmlUi;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;

namespace Apogee.UI.Tests;

/// <summary>A script function that runs a C# delegate with the event it is given.</summary>
internal sealed class Handler(Action<UiEvent> action) : ScriptFunction
{
    public int Calls { get; private set; }

    public override int Call(ReadOnlySpan<Value> arguments, Span<Value> results)
    {
        Calls++;
        action(arguments[0].ToObject<UiEvent>());
        return 0;
    }
}

/// <summary>A virtual file system over a temporary folder as <c>res://</c>, and a UI system on it. RmlUi is process-wide, so every test that makes one runs alone, in the same collection as the GPU tests.</summary>
internal sealed class UiRig : IDisposable
{
    public UiRig(int width = 200, int height = 100)
    {
        NativeRequirements.Require(typeof(Rml).Assembly, "rmlui");
        Folder = Directory.CreateTempSubdirectory("apogee-ui").FullName;
        Files = new VirtualFileSystem();
        Files.Mount("res://", new DirectorySource(Folder));
        Ui = new UiSystem(Files, width: width, height: height);
    }

    public string Folder { get; }

    public VirtualFileSystem Files { get; }

    public UiSystem Ui { get; }

    public void Write(string name, byte[] bytes)
    {
        string path = Path.Combine(Folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    /// <summary>Loads RML text, shows it and lays it out.</summary>
    public UiElement Show(string rml)
    {
        UiElement document = Ui.LoadDocumentText(rml) ?? throw new InvalidOperationException("the document did not load");
        document.Show();
        Ui.Update(0.016f);
        return document;
    }

    public void Dispose()
    {
        Ui.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a failure.
        }
    }
}

[Collection("gpu")]
public sealed class UiSystemTests
{
    [Fact]
    public void ThereIsOneSystemAtATimeAndTheNextOneStartsCleanly()
    {
        using var rig = new UiRig();
        Assert.Throws<InvalidOperationException>(() => new UiSystem(rig.Files));
        rig.Ui.Dispose();
        using var second = new UiSystem(rig.Files, width: 64, height: 48);
        Assert.Equal(64, second.Width);
        Assert.Equal(48, second.Height);
    }

    [Fact]
    public void EveryDocumentHasTheBaseStyleUnderItsOwnRules()
    {
        using var rig = new UiRig(300, 200);
        UiElement document = rig.Show("""
            <rml><head><style>#own { margin: 0; height: 10px; } .row { height: 5px; background-color: #123456; }</style></head>
            <body>
              <div id="a" style="height: 20px"></div>
              <p id="own"></p>
              <p id="base" style="height: 10px"></p>
              <h1 id="title">Hi</h1>
              <span id="s">x</span>
            </body></rml>
            """);

        // A bare div has the width of the body: it is a block, which RmlUi does not make it without a stylesheet. (border-box and margin: 0 on the body are the base's too.)
        Assert.Equal(300f, document.ById("a")!.Rect().Width);
        Assert.Equal(20f, document.ById("a")!.Rect().Height);

        // The base gives a paragraph a margin of 1em (16px); the document's own rule for #own says 0, and wins.
        float ownTop = document.ById("own")!.Rect().Y;
        float baseTop = document.ById("base")!.Rect().Y;
        Assert.Equal(20f, ownTop);
        Assert.Equal(20f + 10f + 16f, baseTop); // after #own (10px, no margin), then the base's margin of 1em

        // Headings are bold and bigger, and inline elements stay inline (they take no width of their own).
        Assert.True(document.ById("title")!.Rect().Height > 20f);
        Assert.True(document.ById("s")!.Rect().Width < 30f);
    }

    [Fact]
    public void TheDefaultFontIsInterAndTextHasASize()
    {
        using var rig = new UiRig(400, 200);
        UiElement document = rig.Show("<rml><body><div id=\"t\" style=\"font-size: 20px\">Hello, world</div><div id=\"empty\"></div></body></rml>");

        (float x, float y, float width, float height) = document.ById("t")!.Rect();
        Assert.True(height >= 20f && height <= 40f, $"one line of 20px text is {height} tall");
        Assert.Equal(0f, document.ById("empty")!.Rect().Height);

        // The same string in bold is wider: the bold face is loaded, and not the regular one stretched.
        UiElement bold = rig.Show("<rml><body><span id=\"r\" style=\"font-size: 20px\">Hello, world</span><br/><span id=\"b\" style=\"font-size: 20px; font-weight: bold\">Hello, world</span></body></rml>");
        Assert.True(bold.ById("b")!.Rect().Width > bold.ById("r")!.Rect().Width);
    }

    [Fact]
    public void ElementsAreMadeAndMovedFromTheScriptSurface()
    {
        using var rig = new UiRig();
        UiElement document = rig.Show("<rml><body><div id=\"list\" style=\"width: 100px\"></div></body></rml>");
        UiElement list = document.ById("list")!;

        UiElement row = list.CreateElement("div")!;
        row.ClassName = "row";
        row.Id = "r1";
        row.SetProperty("height", "10px");
        UiElement label = row.CreateText("one")!;
        Assert.True(row.Append(label));
        Assert.True(list.Append(row));
        Assert.False(list.Append(row)); // it is in a tree: it has no owner to give it

        UiElement second = list.CreateElement("div")!;
        second.Id = "r0";
        second.SetProperty("height", "10px");
        Assert.True(list.InsertBefore(second, row));
        rig.Ui.Update(0.016f);

        Assert.Equal(2, list.ChildCount);
        Assert.Equal("r0", list.Child(0)!.Id);
        Assert.Equal("r1", list.Child(1)!.Id);
        Assert.Equal("row", list.Child(1)!.ClassName);
        Assert.Equal(10f, list.ById("r1")!.Rect().Y - list.ById("r0")!.Rect().Y); // the order is the layout's
        Assert.Equal("one", label.Text);
        label.Text = "uno";
        Assert.Equal("uno", row.Child(0)!.Text);
        Assert.Equal("div", row.Tag);
        Assert.Equal(list.Id, row.Parent!.Id);
        Assert.True(list.Contains(label));
        Assert.Equal("list", row.Closest("#list")!.Id);
        Assert.Equal(document.Id, list.Document!.Id);

        // Taking a child out gives it back to the script; letting go of it then destroys it.
        Assert.True(list.Remove(row));
        Assert.Equal(1, list.ChildCount);
        Assert.Null(row.Parent);
        Assert.True(list.InsertBefore(row, null));
        Assert.Equal(2, list.ChildCount);

        // Destroying an element kills every object that names it, and what is inside it.
        second.DestroyElement();
        Assert.False(second.IsAlive);
        Assert.Equal(1, list.ChildCount);
        list.DestroyElement();
        Assert.False(row.IsAlive);
        Assert.False(label.IsAlive);
        Assert.Equal("", row.Tag);
        Assert.Null(row.Parent);
    }

    [Fact]
    public void AttributesStylesAndPseudoClassesAreThereForScripts()
    {
        using var rig = new UiRig();
        UiElement document = rig.Show("<rml><body><div id=\"d\" data-n=\"5\"></div><input id=\"i\" type=\"text\"/></body></rml>");
        UiElement d = document.ById("d")!;

        Assert.Equal("5", d.GetAttribute("data-n"));
        Assert.Null(d.GetAttribute("nope"));
        d.SetAttribute("data-n", "six");
        Assert.True(d.HasAttribute("data-n"));
        d.RemoveAttribute("data-n");
        Assert.False(d.HasAttribute("data-n"));

        Assert.True(d.SetProperty("width", "77px"));
        Assert.False(d.SetProperty("width", "wide"));
        d.SetProperty("height", "9px");
        rig.Ui.Update(0.016f);
        Assert.Equal((0f, 0f, 77f, 9f), d.Rect());
        d.RemoveProperty("width");
        rig.Ui.Update(0.016f);
        Assert.Equal(200f, d.Rect().Width);

        Assert.False(d.HasPseudoClass("checked"));
        d.SetPseudoClass("checked", true);
        Assert.True(d.HasPseudoClass("checked"));

        d.InnerRml = "<b id=\"bold\">hi</b>";
        Assert.NotNull(d.ById("bold"));
        Assert.Contains("hi", d.InnerRml);

        UiElement input = document.ById("i")!;
        Assert.True(input.Focus());
        Assert.True(rig.Ui.WantsKeyboard); // a text field has the focus
        input.Blur();
        Assert.False(rig.Ui.WantsKeyboard);
    }

    [Fact]
    public void ListenersGetEventsWithTheirParametersAndCanStopThemAndGoWhenTheirElementDoes()
    {
        using var rig = new UiRig();
        UiElement document = rig.Show("<rml><body><div id=\"outer\" style=\"width: 100px; height: 60px\"><div id=\"inner\" style=\"width: 20px; height: 20px\"></div></div></body></rml>");
        UiElement outer = document.ById("outer")!;
        UiElement inner = document.ById("inner")!;

        var seen = new List<string>();
        var onOuter = new Handler(e => seen.Add($"outer:{e.Type}:{e.Target!.Id}:{e.Current!.Id}:{e.Phase}:{e.GetNumber("mouse_x")}"));
        long token = outer.Listen("click", onOuter);
        Assert.NotEqual(0L, token);

        Assert.False(rig.Ui.MouseMove(10, 10) && false);
        rig.Ui.MouseButton(0, true);
        rig.Ui.MouseButton(0, false);
        Assert.Equal(["outer:click:inner:outer:4:10"], seen); // it bubbled up from the inner div, with the pointer's position
        Assert.True(rig.Ui.WantsMouse);

        // A capture listener goes first and can stop the event from reaching the rest.
        seen.Clear();
        outer.Listen("click", new Handler(e => { seen.Add("capture"); e.StopPropagation(); }), capture: true);
        rig.Ui.MouseButton(0, true);
        rig.Ui.MouseButton(0, false);
        Assert.Equal(["capture"], seen);

        // A handler that throws is logged and does not unwind through RmlUi.
        UiElement extra = document.CreateElement("div")!;
        document.Append(extra);
        Assert.NotEqual(0L, extra.Listen("mouseover", new Handler(_ => throw new ValueException("a script bug"))));

        // The handler of a destroyed element's listener is forgotten when the system next prunes.
        Assert.Equal(3, rig.Ui.ListenerCount);
        extra.DestroyElement();
        Assert.Equal(2, rig.Ui.ListenerCount);

        Assert.True(rig.Ui.Unlisten(token));
        Assert.False(rig.Ui.Unlisten(token));
        Assert.Equal(1, rig.Ui.ListenerCount);
        Assert.True(inner.IsAlive);
    }

    [Fact]
    public void TypingGoesToTheFocusedFieldAndAChangeEventCarriesTheValue()
    {
        using var rig = new UiRig(300, 100);
        UiElement document = rig.Show("<rml><body><input id=\"f\" type=\"text\" style=\"width: 200px; height: 24px\"/></body></rml>");
        UiElement field = document.ById("f")!;
        var values = new List<string?>();
        field.Listen("change", new Handler(e => values.Add(e.GetString("value"))));

        Assert.True(field.Focus());
        rig.Ui.Text("héllo");
        rig.Ui.Key(ArKey.Return, true);
        rig.Ui.Key(ArKey.Return, false);
        rig.Ui.Update(0.016f);

        Assert.Contains("héllo", values);
        Assert.Equal("héllo", field.GetAttribute("value"));
    }

    [Fact]
    public void DocumentsAndWhatTheyLinkAreReadThroughTheVirtualFileSystem()
    {
        using var rig = new UiRig(200, 100);
        rig.Write("ui/menu.rml", "<rml><head><link type=\"text/rcss\" href=\"menu.rcss\"/></head><body><div id=\"b\"></div></body></rml>");
        rig.Write("ui/menu.rcss", "#b { height: 31px; background-color: #ff0000; }");

        UiElement? document = rig.Ui.LoadDocument("res://ui/menu.rml");
        Assert.NotNull(document);
        document.Show();
        rig.Ui.Update(0.016f);
        Assert.Equal(31f, document.ById("b")!.Rect().Height); // the stylesheet was found next to the document, through a res:// path

        Assert.Null(rig.Ui.LoadDocument("res://ui/missing.rml"));
        Assert.False(rig.Ui.LoadFont("res://nothing.ttf", "None"));
        rig.Write("ui/notafont.ttf", "this is not a font");
        Assert.False(rig.Ui.LoadFont("res://ui/notafont.ttf", "Junk"));
    }

    [Fact]
    public void ResizingChangesWhatPercentagesMeanAndDensityChangesDp()
    {
        using var rig = new UiRig(200, 100);
        UiElement document = rig.Show("<rml><body><div id=\"p\" style=\"width: 50%; height: 10px\"></div><div id=\"dp\" style=\"width: 10dp; height: 10px\"></div></body></rml>");
        Assert.Equal(100f, document.ById("p")!.Rect().Width);
        Assert.Equal(10f, document.ById("dp")!.Rect().Width);

        rig.Ui.SetSize(400, 300, density: 2f);
        rig.Ui.Update(0.016f);
        Assert.Equal(400, rig.Ui.Width);
        Assert.Equal(2f, rig.Ui.Density);
        Assert.Equal(200f, document.ById("p")!.Rect().Width);
        Assert.Equal(20f, document.ById("dp")!.Rect().Width);
    }

    [Fact]
    public void AnimationsRunOnTheTimeTheUpdatesAddAndAHugeStepAdvancesThemByAtMostATenthOfASecond()
    {
        using var rig = new UiRig(200, 100);
        // RmlUi's tween names are linear-in, quadratic-out...; a transition starts counting at the first update after the change.
        UiElement document = rig.Show("<rml><head><style>#a { width: 0px; height: 10px; transition: width 1s linear-in; } #a.on { width: 100px; }</style></head><body><div id=\"a\"></div></body></rml>");
        UiElement a = document.ById("a")!;
        Assert.Equal(0f, a.Rect().Width);

        a.ClassName = "on";
        rig.Ui.Update(0.05f);
        Assert.Equal(0f, a.Rect().Width);
        for (int i = 0; i < 10; i++)
        {
            rig.Ui.Update(0.05f);
        }

        Assert.InRange(a.Rect().Width, 45f, 55f); // half a second in
        for (int i = 0; i < 12; i++)
        {
            rig.Ui.Update(0.05f);
        }

        Assert.Equal(100f, a.Rect().Width);
        Assert.Equal(0.016 + 0.05 * 23, rig.Ui.Time, 6);

        // One long frame is not allowed to skip an animation: it advances it by a tenth of a second.
        a.ClassName = "";
        rig.Ui.Update(0.05f);
        rig.Ui.Update(5f);
        Assert.InRange(a.Rect().Width, 85f, 95f);
    }

    [Fact]
    public void ClosingADocumentKillsItsElementsAndTheDebuggerCanBeShown()
    {
        using var rig = new UiRig();
        UiElement document = rig.Show("<rml><body><div id=\"x\"></div></body></rml>");
        UiElement x = document.ById("x")!;

        rig.Ui.Debugger = true;
        Assert.True(rig.Ui.Debugger);
        rig.Ui.Update(0.016f);
        rig.Ui.Debugger = false;

        document.Close();
        rig.Ui.Update(0.016f);
        Assert.False(x.IsAlive);
        Assert.False(document.IsAlive);
        Assert.Empty(document.Id);
    }

    [Fact]
    public void ADocumentCanBeBuiltFromNothingForTheFramework()
    {
        using var rig = new UiRig();
        UiElement document = rig.Ui.CreateDocument()!;
        document.Show();
        UiElement box = document.CreateElement("div")!;
        box.SetProperty("height", "14px");
        Assert.True(document.Append(box));
        rig.Ui.Update(0.016f);

        Assert.Equal(200f, box.Rect().Width); // blocks, thanks to the base stylesheet
        Assert.Equal(14f, box.Rect().Height);
    }

    [Fact]
    public void SdlScancodesMapToRmlUiKeysAndButtonsAndCursors()
    {
        Assert.Equal(ArKey.A, UiInput.MapKey((Apogee.Native.Sdl.Scancode)4));
        Assert.Equal(ArKey.Z, UiInput.MapKey((Apogee.Native.Sdl.Scancode)29));
        Assert.Equal(ArKey._1, UiInput.MapKey((Apogee.Native.Sdl.Scancode)30));
        Assert.Equal(ArKey._0, UiInput.MapKey((Apogee.Native.Sdl.Scancode)39));
        Assert.Equal(ArKey.F5, UiInput.MapKey((Apogee.Native.Sdl.Scancode)62));
        Assert.Equal(ArKey.Return, UiInput.MapKey(Apogee.Native.Sdl.Scancode.Return));
        Assert.Equal(ArKey.Back, UiInput.MapKey(Apogee.Native.Sdl.Scancode.Backspace));
        Assert.Equal(ArKey.Left, UiInput.MapKey(Apogee.Native.Sdl.Scancode.Left));
        Assert.Equal(ArKey.Numpad5, UiInput.MapKey((Apogee.Native.Sdl.Scancode)93));
        Assert.Equal(ArKey.Unknown, UiInput.MapKey((Apogee.Native.Sdl.Scancode)1000));

        Assert.Equal(0, UiInput.MapButton(1));
        Assert.Equal(1, UiInput.MapButton(3));
        Assert.Equal(2, UiInput.MapButton(2));
        Assert.Equal(-1, UiInput.MapButton(9));

        Assert.Equal(Apogee.Native.Sdl.SystemCursor.Pointer, UiInput.CursorFor("pointer"));
        Assert.Equal(Apogee.Native.Sdl.SystemCursor.Text, UiInput.CursorFor("text"));
        Assert.Equal(Apogee.Native.Sdl.SystemCursor.Default, UiInput.CursorFor("whatever"));
    }
}

[Collection("gpu")]
public sealed class PointerEventsTests
{
    private static int Clicks(string parentStyle, string childStyle)
    {
        using var rig = new UiRig(300, 200);
        UiElement document = rig.Show($"""
            <rml><body>
              <div id="overlay" style="position: absolute; left: 0; top: 0; width: 300px; height: 200px; {parentStyle}">
                <div id="column" style="position: absolute; left: 100px; top: 100px; width: 100px; height: 40px; {childStyle}">
                  <div id="button" style="width: 100px; height: 40px; background-color: #f00;"></div>
                </div>
              </div>
            </body></rml>
            """);
        int clicks = 0;
        document.ById("button")!.Listen("click", new Handler(_ => clicks++));
        rig.Ui.MouseMove(150, 120);
        rig.Ui.MouseButton(0, true);
        rig.Ui.MouseButton(0, false);
        return clicks;
    }

    [Fact]
    public void AChildThatSaysAutoReceivesTheMouseUnderAParentThatSaysNone()
    {
        Assert.Equal(1, Clicks("", ""));                                                  // nothing special: clickable
        Assert.Equal(0, Clicks("pointer-events: none;", ""));                             // the parent's none is inherited
        Assert.Equal(1, Clicks("pointer-events: none;", "pointer-events: auto;"));        // the child opts back in
    }
}
