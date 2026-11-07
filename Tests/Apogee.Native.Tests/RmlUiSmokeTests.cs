using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Interop;
using Apogee.Native.RmlUi;
using Xunit;

namespace Apogee.Native.Tests;

/// <summary>RmlUi through our C API: layout, drawing (what the host is handed), the element tree and its handles, events, files and styles. RmlUi is process-wide, so these run one at a time.</summary>
[Collection("rmlui")]
public sealed unsafe class RmlUiSmokeTests : IDisposable
{
    /// <summary>What the host functions recorded, reached through the <c>user</c> pointer.</summary>
    private sealed class HostState
    {
        public readonly List<(ulong Handle, ArVertex[] Vertices, int[] Indices)> Compiled = [];
        public readonly List<(ulong Geometry, float X, float Y, ulong Texture)> Rendered = [];
        public readonly List<ulong> ReleasedGeometry = [];
        public readonly List<(string Path, int Width, int Height)> Textures = [];
        public readonly List<(int Width, int Height)> Generated = [];
        public readonly List<string> Log = [];
        public readonly List<(ulong Token, string Type, string Target, string? Param)> Events = [];
        public readonly Dictionary<string, byte[]> Files = [];
        public readonly List<string> FilesRead = [];
        public string Clipboard = "";
        public string? Cursor;
        public EventHandler? OnEvent;
        private ulong _next = 1;

        public ulong NextHandle() => _next++;
    }

    private delegate void EventHandler(ulong token, ArEvent* e);

    private static HostState State(void* user) => (HostState)GCHandle.FromIntPtr((IntPtr)user).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CompileGeometry(void* user, ArVertex* vertices, int vertexCount, int* indices, int indexCount)
    {
        HostState state = State(user);
        ulong handle = state.NextHandle();
        state.Compiled.Add((handle, new ReadOnlySpan<ArVertex>(vertices, vertexCount).ToArray(), new ReadOnlySpan<int>(indices, indexCount).ToArray()));
        return handle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RenderGeometry(void* user, ulong geometry, float x, float y, ulong texture) => State(user).Rendered.Add((geometry, x, y, texture));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseGeometry(void* user, ulong geometry) => State(user).ReleasedGeometry.Add(geometry);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong LoadTexture(void* user, byte* source, int* width, int* height)
    {
        HostState state = State(user);
        string path = Marshal.PtrToStringUTF8((IntPtr)source)!;
        *width = 32;
        *height = 16;
        state.Textures.Add((path, 32, 16));
        return state.NextHandle();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong GenerateTexture(void* user, byte* rgba, int width, int height)
    {
        HostState state = State(user);
        state.Generated.Add((width, height));
        return state.NextHandle();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseTexture(void* user, ulong texture)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Log(void* user, int type, byte* message) => State(user).Log.Add($"{(ArLogType)type}: {Marshal.PtrToStringUTF8((IntPtr)message)}");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetMouseCursor(void* user, byte* name) => State(user).Cursor = Marshal.PtrToStringUTF8((IntPtr)name);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetClipboardText(void* user, byte* text) => State(user).Clipboard = Marshal.PtrToStringUTF8((IntPtr)text)!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GetClipboardText(void* user, void* sink)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(State(user).Clipboard);
        fixed (byte* p = bytes)
        {
            Rml.SinkWrite(sink, p, (nuint)bytes.Length);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadFile(void* user, byte* path, void* sink)
    {
        HostState state = State(user);
        string name = Marshal.PtrToStringUTF8((IntPtr)path)!;
        state.FilesRead.Add(name);
        if (!state.Files.TryGetValue(name, out byte[]? data))
        {
            return 0;
        }

        fixed (byte* p = data)
        {
            Rml.SinkWrite(sink, p, (nuint)data.Length);
        }

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(void* user, ulong token, ArEvent* e)
    {
        HostState state = State(user);
        state.OnEvent?.Invoke(token, e);
    }

    private readonly HostState _state = new();
    private readonly GCHandle _handle;

    public RmlUiSmokeTests()
    {
        NativeRequirements.Require(typeof(Rml).Assembly, "rmlui");
        _handle = GCHandle.Alloc(_state);
        var host = new ArHost
        {
            Size = (uint)sizeof(ArHost),
            User = (void*)GCHandle.ToIntPtr(_handle),
            CompileGeometry = &CompileGeometry,
            RenderGeometry = &RenderGeometry,
            ReleaseGeometry = &ReleaseGeometry,
            LoadTexture = &LoadTexture,
            GenerateTexture = &GenerateTexture,
            ReleaseTexture = &ReleaseTexture,
            Log = &Log,
            SetMouseCursor = &SetMouseCursor,
            SetClipboardText = &SetClipboardText,
            GetClipboardText = &GetClipboardText,
            ReadFile = &ReadFile,
            OnEvent = &OnEvent,
        };
        Assert.True(Rml.Initialize(&host));
    }

    public void Dispose()
    {
        Rml.Shutdown();
        _handle.Free();
        GC.SuppressFinalize(this);
    }

    // ---- Helpers over the C strings ----

    private static ArContext* Context(int width = 400, int height = 300)
    {
        fixed (byte* name = Utf8.ToZeroTerminated("main"))
        {
            return Rml.ContextCreate(name, width, height);
        }
    }

    private static ulong Load(ArContext* context, string rml)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(rml))
        fixed (byte* url = Utf8.ToZeroTerminated("test.rml"))
        {
            return Rml.ContextLoadDocumentText(context, text, url);
        }
    }

    private static ulong ById(ulong root, string id)
    {
        fixed (byte* p = Utf8.ToZeroTerminated(id))
        {
            return Rml.ElementById(root, p);
        }
    }

    private static string Read(Func<nint, int, int> getter)
    {
        byte[] buffer = new byte[256];
        fixed (byte* p = buffer)
        {
            int length = getter((nint)p, buffer.Length);
            return length < 0 ? "<none>" : Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length - 1));
        }
    }

    private static string Attribute(ulong element, string name)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            byte* name2 = n;
            return Read((p, c) => Rml.ElementGetAttribute(element, name2, (byte*)p, c));
        }
    }

    private static string Id(ulong element) => Read((p, c) => Rml.ElementGetId(element, (byte*)p, c));

    private static string Tag(ulong element) => Read((p, c) => Rml.ElementTag(element, (byte*)p, c));

    private static string Classes(ulong element) => Read((p, c) => Rml.ElementGetClassNames(element, (byte*)p, c));

    private static bool SetProperty(ulong element, string name, string value)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        fixed (byte* v = Utf8.ToZeroTerminated(value))
        {
            return Rml.ElementSetProperty(element, n, v);
        }
    }

    private static ArRect Rect(ulong element)
    {
        ArRect rect;
        Assert.True(Rml.ElementGetRect(element, &rect));
        return rect;
    }

    private static byte[] Font() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "ProggyClean.ttf"));

    private static bool LoadFont(string family)
    {
        byte[] data = Font();
        fixed (byte* d = data)
        fixed (byte* f = Utf8.ToZeroTerminated(family))
        {
            return Rml.LoadFont(d, (nuint)data.Length, f, false, false, false);
        }
    }

    private const string Page = """
        <rml>
        <head>
          <style>
            /* RmlUi has no built-in stylesheet: without this a div is inline and takes no width or height. */
            body, div, p { display: block; }
            body { width: 400dp; height: 300dp; }
            #box { display: block; width: 100px; height: 50px; margin: 10px; background-color: #ff0000; }
            #box.wide { width: 250px; }
          </style>
        </head>
        <body>
          <div id="box">
            <div id="inner" class="a b" style="width: 20px; height: 10px; background-color: #00ff00;"></div>
          </div>
          <div id="second" data-x="7"></div>
        </body>
        </rml>
        """;

    // ---- Tests ----

    [Fact]
    public void ItStartsAndLaysADocumentOutInPixels()
    {
        Assert.False(Rml.Initialize(null)); // a second start is refused
        Assert.StartsWith("6.3", Marshal.PtrToStringUTF8((IntPtr)Rml.Version()));
        ArContext* context = Context();
        Assert.True(context != null);
        ulong document = Load(context, Page);
        Assert.NotEqual(0ul, document);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);

        ulong box = ById(document, "box");
        ArRect rect = Rect(box);
        Assert.Equal(100f, rect.Width);
        Assert.Equal(50f, rect.Height);
        Assert.Equal(10f, rect.X); // its margin
        Assert.Equal("div", Tag(box));
        Assert.Equal("box", Id(box));
        Assert.Equal("a b", Classes(ById(document, "inner")));
        Assert.Equal(20f, Rect(ById(document, "inner")).Width);
        Assert.True(Rml.ElementIsVisible(box));
        Assert.Equal(0ul, ById(document, "nothing"));
        Assert.Equal(2, Rml.ElementChildCount(document)); // the document is the body: it holds the two divs
        Assert.Equal(document, Rml.ElementParent(box) == 0 ? 0 : document);
    }

    [Fact]
    public void ChangingTheTreeChangesTheLayoutAfterAnUpdate()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        ulong box = ById(document, "box");

        Assert.True(SetProperty(box, "width", "180px"));
        Assert.False(SetProperty(box, "width", "not a length at all"));
        Rml.ContextUpdate(context);
        Assert.Equal(180f, Rect(box).Width);

        fixed (byte* classes = Utf8.ToZeroTerminated("wide"))
        {
            Assert.True(Rml.ElementRemoveProperty(box, (byte*)Utf8Of("width")));
            Assert.True(Rml.ElementSetClassNames(box, classes));
        }

        Rml.ContextUpdate(context);
        Assert.Equal(250f, Rect(box).Width); // the class's rule, once the inline value is gone
        Assert.Equal("wide", Classes(box));
    }

    private static readonly Dictionary<string, GCHandle> s_pins = [];

    /// <summary>A zero-terminated copy of the text that stays put (for a few calls in a test that need a pointer in an expression).</summary>
    private static IntPtr Utf8Of(string text)
    {
        if (!s_pins.TryGetValue(text, out GCHandle pin))
        {
            pin = GCHandle.Alloc(Utf8.ToZeroTerminated(text), GCHandleType.Pinned);
            s_pins[text] = pin;
        }

        return pin.AddrOfPinnedObject();
    }

    [Fact]
    public void ElementsAreMadeOwnedByTheirHandleUntilATreeTakesThemAndTheTreeIsEditable()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        Rml.DocumentShow(document);
        ulong box = ById(document, "box");

        ulong made = Rml.DocumentCreateElement(document, (byte*)Utf8Of("span"));
        ulong text = Rml.DocumentCreateText(document, (byte*)Utf8Of("hello"));
        Assert.NotEqual(0ul, made);
        Assert.Equal(-1, Rml.ElementChildCount(0));

        // A made element is the handle's until it is put in a tree; then the tree's.
        Assert.True(Rml.ElementAppendChild(made, text));
        Assert.True(Rml.ElementAppendChild(box, made));
        Assert.False(Rml.ElementAppendChild(box, made)); // no longer owned by its handle
        Assert.Equal(2, Rml.ElementChildCount(box));
        Assert.Equal("span", Tag(Rml.ElementChild(box, 1)));
        Assert.True(Rml.TextSet(text, (byte*)Utf8Of("changed")));
        Assert.Equal("changed", Read((p, c) => Rml.TextGet(text, (byte*)p, c)));
        Assert.Equal("<none>", Read((p, c) => Rml.TextGet(box, (byte*)p, c))); // an element is not a text node
        Assert.True(Rml.ElementContains(box, made));
        Assert.False(Rml.ElementContains(made, box));

        // Insert before an existing child, then take it out again and put it back somewhere else.
        ulong first = Rml.DocumentCreateElement(document, (byte*)Utf8Of("p"));
        Assert.True(Rml.ElementInsertBefore(box, first, Rml.ElementChild(box, 0)));
        Assert.Equal("p", Tag(Rml.ElementChild(box, 0)));
        Assert.Equal(3, Rml.ElementChildCount(box));
        Assert.True(Rml.ElementRemoveChild(box, first));
        Assert.Equal(2, Rml.ElementChildCount(box));
        Assert.False(Rml.ElementRemoveChild(box, first)); // it is not a child any more
        ulong second = ById(document, "second");
        Assert.True(Rml.ElementInsertBefore(second, first, 0));
        Assert.Equal(1, Rml.ElementChildCount(second));
        Assert.True(Rml.ElementIsAlive(Rml.ElementParent(first)));
        Assert.True(Rml.ElementContains(second, first));

        // Destroying takes an element out of its tree, with what is in it.
        Rml.ElementDestroy(first);
        Assert.False(Rml.ElementIsAlive(first));
        Assert.Equal(0, Rml.ElementChildCount(second));
        Rml.ElementDestroy(made);
        Assert.Equal(1, Rml.ElementChildCount(box));
        Assert.False(Rml.ElementIsAlive(text)); // it went with its parent
        Rml.ElementRelease(box);
        Assert.False(Rml.ElementIsAlive(box));
    }

    [Fact]
    public void AHandleToADestroyedElementIsRefusedAndAReusedSlotDoesNotAnswerToTheOldHandle()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        ulong stale = Rml.DocumentCreateElement(document, (byte*)Utf8Of("div"));
        Rml.ElementDestroy(stale); // owned and never in a tree: destroyed
        Assert.False(Rml.ElementIsAlive(stale));
        Assert.False(Rml.ElementSetId(stale, (byte*)Utf8Of("x")));
        Assert.Equal("<none>", Tag(stale));

        Rml.ElementRelease(stale);
        ulong fresh = Rml.DocumentCreateElement(document, (byte*)Utf8Of("div")); // takes the freed slot
        Assert.NotEqual(stale, fresh);
        Assert.False(Rml.ElementIsAlive(stale));
        Assert.True(Rml.ElementIsAlive(fresh));
        Assert.False(Rml.ElementAppendChild(fresh, stale));

        // Unloading a document kills what is in it.
        ulong inner = ById(document, "inner");
        Rml.ContextUnloadDocument(context, document);
        Rml.ContextUpdate(context);
        Assert.False(Rml.ElementIsAlive(inner));
        Assert.False(Rml.ElementIsAlive(document));
    }

    [Fact]
    public void AttributesIdsClassesInnerRmlAndPseudoClassesRoundTrip()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        ulong second = ById(document, "second");

        Assert.Equal("7", Attribute(second, "data-x"));
        Assert.True(Rml.ElementHasAttribute(second, (byte*)Utf8Of("data-x")));
        Assert.True(Rml.ElementSetAttribute(second, (byte*)Utf8Of("data-y"), (byte*)Utf8Of("héllo")));
        Assert.Equal("héllo", Attribute(second, "data-y"));
        Assert.True(Rml.ElementRemoveAttribute(second, (byte*)Utf8Of("data-y")));
        Assert.False(Rml.ElementHasAttribute(second, (byte*)Utf8Of("data-y")));
        Assert.Equal("<none>", Attribute(second, "data-y"));

        Assert.True(Rml.ElementSetId(second, (byte*)Utf8Of("renamed")));
        Assert.NotEqual(0ul, ById(document, "renamed"));
        Assert.Equal(0ul, ById(document, "second"));

        Assert.True(Rml.ElementSetInnerRml(second, (byte*)Utf8Of("<b id=\"bold\">hi</b> there")));
        Assert.NotEqual(0ul, ById(document, "bold"));
        Assert.Contains("hi", Read((p, c) => Rml.ElementGetInnerRml(second, (byte*)p, c)));

        Assert.False(Rml.ElementIsPseudoClassSet(second, (byte*)Utf8Of("checked")));
        Assert.True(Rml.ElementSetPseudoClass(second, (byte*)Utf8Of("checked"), true));
        Assert.True(Rml.ElementIsPseudoClassSet(second, (byte*)Utf8Of("checked")));

        // Closest looks up from the element itself.
        ulong inner = ById(document, "inner");
        ulong box = Rml.ElementClosest(inner, (byte*)Utf8Of("#box"));
        Assert.Equal("box", Id(box));
        Assert.Equal(0ul, Rml.ElementClosest(inner, (byte*)Utf8Of("#second")));
    }

    [Fact]
    public void ARenderedDocumentHandsTheHostItsGeometryWithTheColorsAndPositionsOfTheLayout()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        Rml.ContextRender(context);

        Assert.NotEmpty(_state.Rendered);
        // The red box: a quad at its place (the margin) with the vertex color red, alpha multiplied in.
        (ulong Handle, ArVertex[] Vertices, int[] Indices) red = _state.Compiled.Find(g => g.Vertices.Length >= 4 && g.Vertices[0].R == 255 && g.Vertices[0].G == 0 && g.Vertices[0].B == 0);
        Assert.NotEqual(0ul, red.Handle);
        Assert.Equal(6, red.Indices.Length);
        float width = 0, height = 0;
        foreach (ArVertex v in red.Vertices)
        {
            width = Math.Max(width, v.X);
            height = Math.Max(height, v.Y);
        }

        Assert.Equal(100f, width);
        Assert.Equal(50f, height);
        (ulong Geometry, float X, float Y, ulong Texture) call = _state.Rendered.Find(r => r.Geometry == red.Handle);
        Assert.Equal(10f, call.X);
        Assert.Equal(10f, call.Y);
        Assert.Equal(0ul, call.Texture); // colored, not textured
        Assert.Contains(_state.Compiled, g => g.Vertices.Length >= 4 && g.Vertices[0].G == 255 && g.Vertices[0].R == 0); // the green box
    }

    [Fact]
    public void ATextRunNeedsAFontAndMakesATextureForItsGlyphs()
    {
        ArContext* context = Context();
        const string Rml2 = "<rml><head><style>body { font-family: Proggy; font-size: 16px; color: white; }</style></head><body><p id=\"t\">Hello</p></body></rml>";

        ulong before = Load(context, Rml2);
        Rml.DocumentShow(before);
        Rml.ContextUpdate(context);
        Assert.Equal(0f, Rect(ById(before, "t")).Height); // no font: no text to lay out, so the paragraph has no height

        Assert.True(LoadFont("Proggy"));
        fixed (byte* garbage = new byte[64])
        {
            Assert.False(Rml.LoadFont(garbage, 64, (byte*)Utf8Of("Junk"), false, false, false)); // not a font
        }

        ulong after = Load(context, Rml2);
        Rml.DocumentShow(after);
        Rml.ContextUpdate(context);
        Rml.ContextRender(context);
        ArRect text = Rect(ById(after, "t"));
        Assert.True(text.Height > 0, "the paragraph has the height of its line");
        Assert.NotEmpty(_state.Generated); // the glyph atlas
        Assert.Contains(_state.Rendered, r => r.Texture != 0);
    }

    [Fact]
    public void ClickingFiresTheListenerWithTheTargetAndTheEventsParameters()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        ulong box = ById(document, "box");
        ulong inner = ById(document, "inner");

        ulong bubble = Rml.ElementAddListener(box, (byte*)Utf8Of("click"), false);
        Assert.NotEqual(0ul, bubble);
        var seen = new List<(ulong Token, string Type, string Target, string Current, int Phase, double X)>();
        _state.OnEvent = (token, e) =>
        {
            seen.Add((token, Read((p, c) => Rml.EventType(e, (byte*)p, c)), Id(Rml.EventTarget(e)), Id(Rml.EventCurrent(e)), Rml.EventPhase(e), Rml.EventGetNumber(e, (byte*)Utf8Of("mouse_x"), -1)));
            Assert.True(Rml.EventHasParameter(e, (byte*)Utf8Of("mouse_x")));
            Assert.False(Rml.EventHasParameter(e, (byte*)Utf8Of("no_such")));
        };

        // The pointer over the inner div (at 10,10 to 30,20 in the document): a click there reaches the box's listener by bubbling.
        Rml.ContextMouseMove(context, 15, 15, 0);
        Rml.ContextMouseButton(context, 0, true, 0);
        Rml.ContextMouseButton(context, 0, false, 0);

        (ulong Token, string Type, string Target, string Current, int Phase, double X) click = Assert.Single(seen);
        Assert.Equal(bubble, click.Token);
        Assert.Equal("click", click.Type);
        Assert.Equal("inner", click.Target);
        Assert.Equal("box", click.Current);
        Assert.Equal((int)ArEventPhase.Bubble, click.Phase);
        Assert.Equal(15.0, click.X);

        Assert.True(Rml.ContextIsMouseOverUi(context));
        ulong under = Rml.ContextElementAt(context, 15, 15);
        Assert.Equal("inner", Id(under));
        Assert.True(Rml.ElementContains(box, inner));

        // After removing the listener there is no event; removing it again says no.
        seen.Clear();
        Assert.True(Rml.ElementRemoveListener(bubble));
        Assert.False(Rml.ElementRemoveListener(bubble));
        Rml.ContextMouseButton(context, 0, true, 0);
        Rml.ContextMouseButton(context, 0, false, 0);
        Assert.Empty(seen);
    }

    [Fact]
    public void ACapturingListenerGoesFirstCanStopTheEventAndDiesWithItsElement()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        ulong box = ById(document, "box");
        ulong inner = ById(document, "inner");
        ulong capture = Rml.ElementAddListener(box, (byte*)Utf8Of("click"), true);
        ulong target = Rml.ElementAddListener(inner, (byte*)Utf8Of("click"), false);
        var order = new List<ulong>();
        _state.OnEvent = (token, e) =>
        {
            order.Add(token);
            if (token == capture)
            {
                Rml.EventStopPropagation(e);
            }
        };

        Rml.ContextMouseMove(context, 15, 15, 0);
        Rml.ContextMouseButton(context, 0, true, 0);
        Rml.ContextMouseButton(context, 0, false, 0);
        Assert.Equal([capture], order); // the capture listener ran and stopped it before the target

        // A listener on an element that is destroyed is gone with it.
        Rml.ElementDestroy(inner);
        Assert.False(Rml.ElementRemoveListener(target));
        Assert.True(Rml.ElementRemoveListener(capture));
    }

    [Fact]
    public void AStyleSheetCanBeAddedToALoadedDocument()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        ulong second = ById(document, "second");
        Assert.Equal(0f, Rect(second).Height);

        Assert.True(Rml.DocumentAddStyle(document, (byte*)Utf8Of("#second { height: 33px; } .x { width: 5px }"), false));
        Rml.ContextUpdate(context);
        Assert.Equal(33f, Rect(second).Height);
        Assert.Equal(50f, Rect(ById(document, "box")).Height); // what the document had stays

        // Another sheet on top of the first keeps both.
        Assert.True(Rml.DocumentAddStyle(document, (byte*)Utf8Of("#second { width: 66px; }"), false));
        Rml.ContextUpdate(context);
        Assert.Equal(66f, Rect(second).Width);
        Assert.Equal(33f, Rect(second).Height);
    }

    [Fact]
    public void AStyleSheetAddedUnderneathLosesToTheDocumentsOwnRules()
    {
        ArContext* context = Context();
        ulong document = Load(context, Page);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        ulong second = ById(document, "second");

        // Underneath: the document's `#box { width: 100px }` beats a base rule of the same specificity, and a base rule where the document has none applies.
        Assert.True(Rml.DocumentAddStyle(document, (byte*)Utf8Of("#box { width: 999px; } #second { height: 12px; }"), true));
        Rml.ContextUpdate(context);
        Assert.Equal(100f, Rect(ById(document, "box")).Width);
        Assert.Equal(12f, Rect(second).Height);

        // The element's owner document, from any element, and for a document itself.
        ulong inner = ById(document, "inner");
        Assert.Equal("body", Tag(Rml.ElementDocument(inner)));
        Assert.True(Rml.ElementIsAlive(Rml.ElementDocument(document)));
        ulong made = Rml.DocumentCreateElement(Rml.ElementDocument(inner), (byte*)Utf8Of("div"));
        Assert.NotEqual(0ul, made);
    }

    [Fact]
    public void DocumentsAndTheirStylesheetsAreReadThroughTheHostsFiles()
    {
        _state.Files["ui/menu.rml"] = Encoding.UTF8.GetBytes("<rml><head><link type=\"text/rcss\" href=\"menu.rcss\"/></head><body><div id=\"b\"></div></body></rml>");
        _state.Files["ui/menu.rcss"] = Encoding.UTF8.GetBytes("div { display: block; } #b { width: 77px; height: 11px; }");
        ArContext* context = Context();

        ulong document;
        fixed (byte* path = Utf8.ToZeroTerminated("ui/menu.rml"))
        {
            document = Rml.ContextLoadDocument(context, path);
        }

        Assert.NotEqual(0ul, document);
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        Assert.Contains("ui/menu.rml", _state.FilesRead);
        Assert.Contains("ui/menu.rcss", _state.FilesRead); // resolved against the document's own path
        Assert.Equal(77f, Rect(ById(document, "b")).Width);

        fixed (byte* missing = Utf8.ToZeroTerminated("ui/none.rml"))
        {
            Assert.Equal(0ul, Rml.ContextLoadDocument(context, missing));
        }
    }

    [Fact]
    public void ImagesAreLoadedByTheHostAndTheClipboardAndLogGoThroughIt()
    {
        ArContext* context = Context();
        ulong document = Load(context, "<rml><head><style>body{width:400dp;height:300dp;} img{display:block;}</style></head><body><img id=\"i\" src=\"pics/a.png\"/></body></rml>");
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        Rml.ContextRender(context);
        Assert.Contains(_state.Textures, t => t.Path.EndsWith("pics/a.png", StringComparison.Ordinal));
        ArRect image = Rect(ById(document, "i"));
        Assert.Equal(32f, image.Width); // the size the host gave
        Assert.Equal(16f, image.Height);

        Load(context, "<rml><body><div></rml>"); // not well formed: logged, not thrown
        Assert.Contains(_state.Log, l => l.StartsWith("Warning", StringComparison.Ordinal) || l.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public void TheKeyboardReachesAFocusedTextFieldAsTypedTextAndKeys()
    {
        ArContext* context = Context();
        ulong document = Load(context, "<rml><head><style>body{width:400dp;height:300dp;} input{display:block;width:200px;height:20px;}</style></head><body><input id=\"f\" type=\"text\"/></body></rml>");
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        ulong field = ById(document, "f");
        Assert.True(Rml.ElementFocus(field));
        Assert.Equal("f", Id(Rml.ContextFocusedElement(context)));

        var changes = new List<string>();
        Rml.ElementAddListener(field, (byte*)Utf8Of("change"), false);
        _state.OnEvent = (token, e) => changes.Add(Read((p, c) => Rml.EventGetString(e, (byte*)Utf8Of("value"), (byte*)p, c)));
        Rml.ContextText(context, (byte*)Utf8Of("hi"));
        Rml.ContextText(context, (byte*)Utf8Of(" é"));
        Rml.ContextKey(context, (int)ArKey.Return, true, 0);
        Rml.ContextKey(context, (int)ArKey.Return, false, 0);
        Rml.ContextUpdate(context);

        Assert.Contains("hi é", changes); // Return commits the text field
        Assert.Equal("hi é", Attribute(field, "value"));
        Rml.ElementBlur(field);
        Assert.NotEqual("f", Id(Rml.ContextFocusedElement(context)));
    }

    [Fact]
    public void TheDebuggerAttachesAndTogglesAndTheKeyEnumHasRmlUisValues()
    {
        ArContext* context = Context();
        Assert.True(Rml.DebuggerInitialize(context));
        Rml.DebuggerSetVisible(false);
        Rml.ContextUpdate(context); // visibility is a style: it shows after a layout
        Assert.False(Rml.DebuggerIsVisible());
        Rml.DebuggerSetVisible(true);
        Rml.ContextUpdate(context);
        Assert.True(Rml.DebuggerIsVisible());
        Rml.ContextRender(context);
        Assert.NotEmpty(_state.Rendered); // it draws its own window

        Assert.Equal(1u, (uint)ArKey.Space);
        Assert.Equal(12u, (uint)ArKey.A);
        Assert.Equal(2u, (uint)ArKey._0);
    }

    [Fact]
    public void ContextsCanBeResizedAndDestroyedAndShutdownClosesWhatIsLeft()
    {
        ArContext* context = Context(200, 100);
        ulong document = Load(context, "<rml><head><style>body{width:100%;height:100%;}</style></head><body></body></rml>");
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        Assert.Equal(200f, Rect(document).Width);
        Rml.ContextSetSize(context, 640, 480);
        Rml.ContextUpdate(context);
        Assert.Equal(640f, Rect(document).Width);
        Assert.Equal(480f, Rect(document).Height);

        Rml.ContextDestroy(context);
        Context(); // another one is fine; Dispose shuts down with it still open
    }

    [Fact]
    public void TheBaseStyleGoesUnderTheRulesOfEveryDocumentLoadedAfterIt()
    {
        ArContext* context = Context(300, 200);
        fixed (byte* css = Utf8.ToZeroTerminated("div { display: block; height: 20px; } .own { height: 5px; }"))
        {
            Assert.True(Rml.SetBaseStyle(css));
        }

        ulong document = Load(context, "<rml><head><style>.own { height: 7px; }</style></head><body><div id=\"a\"></div><div id=\"b\" class=\"own\"></div></body></rml>");
        Rml.DocumentShow(document);
        Rml.ContextUpdate(context);
        Assert.Equal(20f, Rect(ById(document, "a")).Height); // the base style made a block of it
        Assert.Equal(7f, Rect(ById(document, "b")).Height);  // the document's rule wins over the base's, which has the same specificity
    }

    [Fact]
    public void DataModelsAreCreatedOncePerNameAndRemoved()
    {
        ArContext* context = Context();
        fixed (byte* model = Utf8.ToZeroTerminated("stats"))
        fixed (byte* field = Utf8.ToZeroTerminated("score"))
        {
            Assert.True(Rml.DataModelCreate(context, model));
            Assert.False(Rml.DataModelCreate(context, model)); // the name is taken
            Assert.False(Rml.DataModelBind(context, model, field, 0, (int)ArDataKind.Scalar)); // a ref of 0 is none
            Assert.True(Rml.DataModelDirty(context, model, null)); // everything
            Assert.True(Rml.DataModelRemove(context, model));
            Assert.False(Rml.DataModelRemove(context, model));
            Assert.False(Rml.DataModelDirty(context, model, field)); // gone
        }
    }
}
