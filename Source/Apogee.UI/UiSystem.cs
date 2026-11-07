using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.RmlUi;
using Apogee.Reflection;
using Sdl = Apogee.Native.Sdl;

namespace Apogee.UI;

/// <summary>
/// The game's user interface: RmlUi with one context (the screen), its documents, fonts and input, behind the script global <c>ui</c>. Documents are written in RML and RCSS (HTML and CSS as RmlUi
/// knows them) with scripts of their own in Luau, or built from Luau; both go through <see cref="UiElement"/>s. RmlUi is process-wide, so there is one system at a time.
/// </summary>
/// <remarks>
/// Call <see cref="SetSize"/> when the window changes size, <see cref="Update"/> once a frame (it lays out and runs animations and delivers nothing by itself: events are delivered when input is
/// given), and let the <see cref="UiRenderer"/> draw (it is the render overlay: it asks for the picture when the frame's graph is built). Input comes from <see cref="UiInput"/> or from the
/// <c>Mouse</c>, <c>Key</c> and <c>Text</c> calls; each says whether the UI used it, so the game can leave it alone. Without a renderer (headless runs, tests) everything but drawing works.
/// What a document asks to read (its stylesheets, images and fonts) is read through the virtual file system, as <c>res://</c>, <c>mod://</c> and <c>user://</c> paths.
/// RmlUi has no stylesheet of its own: <c>ui_base.rcss</c> is put under every document (the document's own rules win), with the default font, Inter.
/// </remarks>
[ApogeeClass]
public sealed unsafe partial class UiSystem : EngineObject, IDisposable
{
    private static UiSystem? s_active;

    private readonly VirtualFileSystem _files;
    private readonly ContentLoader _content;
    private readonly Dictionary<ulong, Listener> _listeners = [];
    private readonly List<string> _fonts = [];
    private ArContext* _context;
    private UiRenderer? _renderer;
    private double _time;
    private int _width;
    private int _height;
    private float _density = 1f;
    private string _clipboard = "";
    private ulong _fakeHandles = 1;
    private int _framesSincePrune;
    private bool _debugger;
    private bool _disposed;

    private readonly List<(long Id, ScriptFunction Handler)> _updates = [];
    private long _nextUpdate;

    private sealed class Listener(ScriptFunction handler, string type)
    {
        public ScriptFunction Handler { get; } = handler;

        public string Type { get; } = type;
    }

    /// <summary>Starts RmlUi with <paramref name="width"/> x <paramref name="height"/> pixels of screen. Throws when there is a system already (RmlUi has one state per process) or the library is missing.</summary>
    public UiSystem(VirtualFileSystem files, ContentLoader? content = null, int width = 1280, int height = 720)
    {
        if (s_active is not null)
        {
            throw new InvalidOperationException("There is a UI system already: RmlUi has one state per process.");
        }

        _files = files;
        _content = content ?? new ContentLoader(files);
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);

        var host = new ArHost
        {
            Size = (uint)sizeof(ArHost),
            CompileGeometry = &HostCompileGeometry,
            RenderGeometry = &HostRenderGeometry,
            ReleaseGeometry = &HostReleaseGeometry,
            LoadTexture = &HostLoadTexture,
            GenerateTexture = &HostGenerateTexture,
            ReleaseTexture = &HostReleaseTexture,
            EnableScissor = &HostEnableScissor,
            SetScissor = &HostSetScissor,
            SetTransform = &HostSetTransform,
            GetElapsedTime = &HostElapsedTime,
            Log = &HostLog,
            SetMouseCursor = &HostSetMouseCursor,
            SetClipboardText = &HostSetClipboard,
            GetClipboardText = &HostGetClipboard,
            ReadFile = &HostReadFile,
            OnEvent = &HostOnEvent,
            LoadInlineScript = &HostLoadInlineScript,
            LoadExternalScript = &HostLoadExternalScript,
            InstanceInlineHandler = &HostInstanceInlineHandler,
            CompileInlineHandler = &HostCompileInlineHandler,
            DocumentClosed = &HostDocumentClosed,
            DataGet = &HostDataGet,
            DataSet = &HostDataSet,
            DataSize = &HostDataSize,
            DataChild = &HostDataChild,
            DataEvent = &HostDataEvent,
            EnableClipMask = &HostEnableClipMask,
            RenderToClipMask = &HostRenderToClipMask,
            PushLayer = &HostPushLayer,
            CompositeLayers = &HostCompositeLayers,
            PopLayer = &HostPopLayer,
            SaveLayerAsTexture = &HostSaveLayerAsTexture,
            SaveLayerAsMaskImage = &HostSaveLayerAsMaskImage,
            CompileFilter = &HostCompileFilter,
            ReleaseFilter = &HostReleaseFilter,
            CompileShader = &HostCompileShader,
            RenderShader = &HostRenderShader,
            ReleaseShader = &HostReleaseShader,
        };

        s_active = this;
        if (!Rml.Initialize(&host))
        {
            s_active = null;
            throw new InvalidOperationException("RmlUi did not start.");
        }

        fixed (byte* name = Utf8.ToZeroTerminated("main"))
        {
            _context = Rml.ContextCreate(name, _width, _height);
        }

        LoadDefaultFonts();

        // RmlUi has no stylesheet of its own: ours goes under the rules of every document (the document's own win).
        fixed (byte* css = Utf8.ToZeroTerminated(BaseStyle))
        {
            Rml.SetBaseStyle(css);
        }
    }

    /// <summary>The width of the screen in pixels.</summary>
    [Property]
    public int Width => _width;

    /// <summary>The height of the screen in pixels.</summary>
    [Property]
    public int Height => _height;

    /// <summary>How many pixels a density-independent pixel (<c>dp</c> in RCSS) is: 1 on an ordinary screen, 2 on a high-density one, or what the player's UI scale says.</summary>
    [Property]
    public float Density
    {
        get => _density;
        set
        {
            _density = Math.Max(value, 0.25f);
            Rml.ContextSetDensity(Context, _density);
        }
    }

    /// <summary>RmlUi's inspector (the element tree and styles of every document, and a log) as a window over the UI.</summary>
    [Property]
    public bool Debugger
    {
        get => _debugger;
        set
        {
            if (value && !_debugger && !Rml.DebuggerInitialize(Context))
            {
                return;
            }

            _debugger = value;
            Rml.DebuggerSetVisible(value);
        }
    }

    /// <summary>True while the pointer is over an element of a document: the game should not also react to the mouse.</summary>
    [Property]
    public bool WantsMouse => !_disposed && Rml.ContextIsMouseOverUi(Context);

    /// <summary>True while a text field has the focus: typed text and keys are the UI's, not the game's.</summary>
    [Property]
    public bool WantsKeyboard
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            ulong focused = Rml.ContextFocusedElement(Context);
            try
            {
                string tag = ElementText(focused, static (e, b, c) => Rml.ElementTag(e, b, c));
                return tag is "input" or "textarea" or "select";
            }
            finally
            {
                Rml.ElementRelease(focused);
            }
        }
    }

    /// <summary>The mouse cursor the UI wants (<c>pointer</c>, <c>text</c>, <c>default</c>...), as RmlUi last said.</summary>
    public string Cursor { get; private set; } = "default";

    /// <summary>How many documents have been loaded since the system started: a host makes the renderer only when this is more than zero.</summary>
    public int DocumentsLoaded { get; private set; }

    /// <summary>The renderer that draws the UI, once there is one.</summary>
    public UiRenderer? Renderer => _renderer;

    internal ArContext* Context => !_disposed ? _context : throw new ObjectDisposedException(nameof(UiSystem));

    /// <summary>The seconds the UI has run: the sum of the <see cref="Update"/> steps, what its animations and transitions go by.</summary>
    public double Time => _time;

    // ---- Frame ----

    /// <summary>Tells the UI how big the screen is, in pixels, and how many pixels a density-independent one is (see <see cref="Density"/>).</summary>
    public void SetSize(int width, int height, float density = 0)
    {
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        Rml.ContextSetSize(Context, _width, _height);
        if (density > 0)
        {
            Density = density;
        }
    }

    /// <summary>Lays out what changed and runs animations, <paramref name="deltaSeconds"/> after the last call. Returns whether anything changed.</summary>
    [Method]
    public bool Update(float deltaSeconds)
    {
        _time += Math.Max(deltaSeconds, 0f);
        RunUpdateHandlers(deltaSeconds);
        bool changed = Rml.ContextUpdate(Context);
        if (++_framesSincePrune >= 120)
        {
            _framesSincePrune = 0;
            PruneListeners();
        }

        return changed;
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with the frame's time step at the start of every <see cref="Update"/>, before layout: where a script puts what changed in the game (a speed, a health value) into the UI, so that it is
    /// in the picture of this frame. Returns a number for <see cref="RemoveUpdate"/>. A handler that fails is logged and removed.
    /// </summary>
    [Method]
    public long OnUpdate(ScriptFunction handler)
    {
        long id = ++_nextUpdate;
        _updates.Add((id, handler));
        ScriptScope.OnReload(() => RemoveUpdate(id)); // a module that is reloaded adds its handlers again
        return id;
    }

    [Method]
    public void RemoveUpdate(long id) => _updates.RemoveAll(u => u.Id == id);

    private void RunUpdateHandlers(float deltaSeconds)
    {
        if (_updates.Count == 0)
        {
            return;
        }

        foreach ((long id, ScriptFunction handler) in _updates.ToArray())
        {
            try
            {
                handler.Call([Value.Float(deltaSeconds)], []);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                Log.Error("UI", $"an update handler failed and is removed: {failure.Message}");
                RemoveUpdate(id);
            }
        }
    }

    /// <summary>Called by the renderer when the frame is built: RmlUi hands the host the geometry through the host functions.</summary>
    internal void RenderContext()
    {
        if (!_disposed)
        {
            Rml.ContextRender(_context);
        }
    }

    internal void Attach(UiRenderer renderer) => _renderer = renderer;

    internal void Detach(UiRenderer renderer)
    {
        if (ReferenceEquals(_renderer, renderer))
        {
            _renderer = null;
        }
    }

    // ---- Documents ----

    /// <summary>Loads the RML document at <paramref name="path"/> (<c>res://ui/menu.rml</c>). It is not shown until <see cref="UiElement.Show"/>. Null when it cannot be read or parsed (the log says why).</summary>
    [Method]
    public UiElement? LoadDocument(string path)
    {
        ulong document;
        fixed (byte* p = Utf8.ToZeroTerminated(path))
        {
            document = Rml.ContextLoadDocument(Context, p);
        }

        return Track(document);
    }

    /// <summary>Loads a document from RML text; <paramref name="sourceUrl"/> is what its relative paths (stylesheets, images) are relative to.</summary>
    [Method]
    public UiElement? LoadDocumentText(string rml, string sourceUrl = "")
    {
        ulong document;
        fixed (byte* text = Utf8.ToZeroTerminated(rml))
        fixed (byte* url = Utf8.ToZeroTerminated(sourceUrl.Length > 0 ? sourceUrl : "[document from memory]"))
        {
            document = Rml.ContextLoadDocumentText(Context, text, url);
        }

        return Track(document);
    }

    /// <summary>An empty document, with a <c>body</c> to put elements in, for a UI built in code.</summary>
    [Method]
    public UiElement? CreateDocument() => LoadDocumentText("<rml><head><title>document</title></head><body></body></rml>");

    private UiElement? Track(ulong document)
    {
        if (document == 0)
        {
            return null;
        }

        DocumentsLoaded++;
        var element = new UiElement(this, document);
        ScriptScope.OnReload(() => CloseDocument(element)); // a module that is reloaded loads its documents again
        return element;
    }

    internal void CloseDocument(UiElement document)
    {
        if (!_disposed && !document.IsDestroyed && Rml.ElementIsAlive(document.Native))
        {
            Rml.ContextUnloadDocument(_context, document.Native);
        }
    }

    /// <summary>Loads a font from the file at <paramref name="path"/> for the family <paramref name="family"/> (documents say <c>font-family: Family</c>). False when it is not a font.</summary>
    [Method]
    public bool LoadFont(string path, string family, bool bold = false, bool italic = false, bool fallback = false)
    {
        byte[] data;
        try
        {
            data = _files.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            Log.Warn("UI", $"font {path} cannot be read: {e.Message}");
            return false;
        }

        return LoadFont(data, family, bold, italic, fallback);
    }

    /// <summary>Loads a font from memory (RmlUi keeps its own copy).</summary>
    public bool LoadFont(ReadOnlySpan<byte> data, string family, bool bold = false, bool italic = false, bool fallback = false)
    {
        fixed (byte* d = data)
        fixed (byte* f = Utf8.ToZeroTerminated(family))
        {
            return Rml.LoadFont(d, (nuint)data.Length, f, italic, bold, fallback);
        }
    }

    private static readonly string[] s_defaultFonts = ["Inter-Regular", "Inter-Bold", "Inter-Italic"];

    private static string? s_baseStyle;

    /// <summary>The engine's base stylesheet (<c>ui_base.rcss</c>), which every document is given under its own rules.</summary>
    public static string BaseStyle => s_baseStyle ??= ReadResource("Apogee.UI.ui_base.rcss") is { } bytes ? Encoding.UTF8.GetString(bytes) : "";

    private static byte[]? ReadResource(string name)
    {
        using Stream? stream = typeof(UiSystem).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void LoadDefaultFonts()
    {
        foreach (string name in s_defaultFonts)
        {
            byte[]? data = ReadResource($"Apogee.UI.Fonts.{name}.ttf");
            if (data is null)
            {
                Log.Warn("UI", $"the default font {name} is missing from the assembly");
                continue;
            }

            if (!LoadFont(data, "Inter", bold: name.EndsWith("Bold", StringComparison.Ordinal), italic: name.EndsWith("Italic", StringComparison.Ordinal)))
            {
                Log.Warn("UI", $"the default font {name} did not load");
            }
        }
    }

    // ---- Input ----

    /// <summary>The pointer moved to (<paramref name="x"/>, <paramref name="y"/>) pixels. False when the UI used it.</summary>
    public bool MouseMove(int x, int y, int modifiers = 0) => Rml.ContextMouseMove(Context, x, y, modifiers);

    /// <summary>A button went down or up: 0 left, 1 right, 2 middle. False when the UI used it.</summary>
    public bool MouseButton(int button, bool down, int modifiers = 0) => Rml.ContextMouseButton(Context, button, down, modifiers);

    public bool MouseWheel(float x, float y, int modifiers = 0) => Rml.ContextMouseWheel(Context, x, y, modifiers);

    public bool MouseLeave() => Rml.ContextMouseLeave(Context);

    /// <summary>A key went down or up (<see cref="ArKey"/>).</summary>
    public bool Key(ArKey key, bool down, int modifiers = 0) => Rml.ContextKey(Context, (int)key, down, modifiers);

    /// <summary>Typed text, as UTF-8 characters.</summary>
    public bool Text(string text)
    {
        fixed (byte* p = Utf8.ToZeroTerminated(text))
        {
            return Rml.ContextText(Context, p);
        }
    }

    // ---- Events ----

    internal ulong Listen(UiElement element, string type, bool capture, ScriptFunction handler)
    {
        ulong token;
        fixed (byte* t = Utf8.ToZeroTerminated(type))
        {
            token = Rml.ElementAddListener(element.Native, t, capture);
        }

        if (token != 0)
        {
            _listeners[token] = new Listener(handler, type);
        }

        return token;
    }

    /// <summary>Takes back a listener (the number <see cref="UiElement.Listen"/> returned). False when it was gone already.</summary>
    [Method]
    public bool Unlisten(long token)
    {
        _listeners.Remove((ulong)token);
        return Rml.ElementRemoveListener((ulong)token);
    }

    /// <summary>Forgets the handlers of listeners that went with their elements.</summary>
    private void PruneListeners()
    {
        List<ulong>? gone = null;
        foreach (ulong token in _listeners.Keys)
        {
            if (!Rml.ListenerExists(token))
            {
                (gone ??= []).Add(token);
            }
        }

        if (gone is not null)
        {
            foreach (ulong token in gone)
            {
                _listeners.Remove(token);
            }
        }

        foreach (ulong token in new List<ulong>(_inlineCallers.Keys))
        {
            if (!Rml.ListenerExists(token))
            {
                _inlineCallers.Remove(token);
            }
        }
    }

    /// <summary>The number of listeners scripts have on elements that still exist (after the dead ones are pruned).</summary>
    public int ListenerCount
    {
        get
        {
            PruneListeners();
            return _listeners.Count;
        }
    }

    private void Dispatch(ulong token, ArEvent* native)
    {
        if (!_listeners.TryGetValue(token, out Listener? listener))
        {
            return;
        }

        var wrapper = new UiEvent(this, native);
        try
        {
            listener.Handler.Call([Value.Object(wrapper)], []);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Nothing may unwind through RmlUi's frames: a handler that fails is logged and the event goes on.
            Log.Error("UI", $"a handler of '{listener.Type}' events failed: {failure.Message}");
        }
        finally
        {
            wrapper.Invalidate();
            wrapper.Destroy();
        }
    }

    // ---- Text results ----

    internal delegate int Getter(ulong element, byte* buffer, int capacity);

    /// <summary>A text result of the C API: asks how long it is, then reads it. Null when there is none (a dead element, a missing attribute).</summary>
    internal static string? ElementTextOrNull(ulong element, Getter getter)
    {
        int length = getter(element, null, 0);
        if (length < 0)
        {
            return null;
        }

        if (length == 0)
        {
            return "";
        }

        byte[] buffer = new byte[length + 1];
        fixed (byte* p = buffer)
        {
            getter(element, p, buffer.Length);
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    internal static string ElementText(ulong element, Getter getter) => ElementTextOrNull(element, getter) ?? "";

    // ---- Host functions: RmlUi calls them, and they forward to the system ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostCompileGeometry(void* user, ArVertex* vertices, int vertexCount, int* indices, int indexCount)
    {
        UiSystem? ui = s_active;
        if (ui is null)
        {
            return 0;
        }

        try
        {
            return ui._renderer is { } renderer
                ? renderer.CompileGeometry(new ReadOnlySpan<ArVertex>(vertices, vertexCount), new ReadOnlySpan<int>(indices, indexCount))
                : ui._fakeHandles++; // no GPU: layout and events work, there is nothing to draw
        }
        catch (Exception e)
        {
            Log.Error("UI", $"compiling geometry failed: {e.Message}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostRenderGeometry(void* user, ulong geometry, float x, float y, ulong texture) => s_active?._renderer?.RenderGeometry(geometry, x, y, texture);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostReleaseGeometry(void* user, ulong geometry) => s_active?._renderer?.ReleaseGeometry(geometry);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostLoadTexture(void* user, byte* source, int* width, int* height)
    {
        UiSystem? ui = s_active;
        if (ui is null)
        {
            return 0;
        }

        string path = Utf8.FromPointer(source) ?? "";
        try
        {
            TextureData image = ui._content.LoadTexture(path, srgb: false, new TextureCaps(false, false), sampler: null, mipmaps: false);
            *width = image.Width;
            *height = image.Height;
            return ui._renderer is { } renderer ? renderer.CreateTexture(image.Mips[0], image.Width, image.Height, premultiplied: false) : ui._fakeHandles++;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warn("UI", $"image {path} does not load: {e.Message}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong HostGenerateTexture(void* user, byte* rgba, int width, int height)
    {
        UiSystem? ui = s_active;
        if (ui is null)
        {
            return 0;
        }

        try
        {
            return ui._renderer is { } renderer ? renderer.CreateTexture(new ReadOnlySpan<byte>(rgba, width * height * 4), width, height, premultiplied: true) : ui._fakeHandles++;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("UI", $"creating a texture failed: {e.Message}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostReleaseTexture(void* user, ulong texture) => s_active?._renderer?.ReleaseTexture(texture);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostEnableScissor(void* user, int enable) => s_active?._renderer?.EnableScissor(enable != 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostSetScissor(void* user, int x, int y, int width, int height) => s_active?._renderer?.SetScissor(x, y, width, height);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostSetTransform(void* user, float* matrix) => s_active?._renderer?.SetTransform(matrix);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double HostElapsedTime(void* user) => s_active?._time ?? 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostLog(void* user, int type, byte* message)
    {
        string text = Utf8.FromPointer(message) ?? "";
        switch ((ArLogType)type)
        {
            case ArLogType.Error or ArLogType.Assert:
                Log.Error("RmlUi", text);
                break;
            case ArLogType.Warning:
                Log.Warn("RmlUi", text);
                break;
            case ArLogType.Always or ArLogType.Info:
                Log.Info("RmlUi", text);
                break;
            default:
                Log.Debug("RmlUi", text);
                break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostSetMouseCursor(void* user, byte* name)
    {
        if (s_active is { } ui)
        {
            ui.Cursor = Utf8.FromPointer(name) is { Length: > 0 } cursor ? cursor : "default";
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostSetClipboard(void* user, byte* text)
    {
        if (s_active is not { } ui)
        {
            return;
        }

        ui._clipboard = Utf8.FromPointer(text) ?? "";
        if (PlatformHostReady())
        {
            Sdl.SDL.SetClipboardText(text);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostGetClipboard(void* user, void* sink)
    {
        if (s_active is not { } ui)
        {
            return;
        }

        string text = ui._clipboard;
        if (PlatformHostReady())
        {
            byte* native = Sdl.SDL.GetClipboardText();
            if (native != null)
            {
                text = Utf8.FromPointer(native) ?? "";
                Sdl.SDL.Free(native);
            }
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
        {
            Rml.SinkWrite(sink, p, (nuint)bytes.Length);
        }
    }

    private static bool PlatformHostReady() => Apogee.Platform.PlatformHost.IsInitialized;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HostReadFile(void* user, byte* path, void* sink)
    {
        UiSystem? ui = s_active;
        string name = Utf8.FromPointer(path) ?? "";
        if (ui is null)
        {
            return 0;
        }

        try
        {
            byte[] bytes = ui._files.ReadAllBytes(name);
            fixed (byte* p = bytes)
            {
                Rml.SinkWrite(sink, p, (nuint)bytes.Length);
            }

            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("UI", $"{name} cannot be read: {e.Message}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostOnEvent(void* user, ulong token, ArEvent* native) => s_active?.Dispatch(token, native);

    // ---- End ----

    public void Dispose() => Destroy();

    protected override void OnDestroyed()
    {
        if (_disposed)
        {
            return;
        }

        CloseModels();
        _disposed = true;
        _listeners.Clear();
        _updates.Clear();
        Rml.Shutdown();
        DisposeScripts();
        _context = null;
        if (ReferenceEquals(s_active, this))
        {
            s_active = null;
        }
    }
}
