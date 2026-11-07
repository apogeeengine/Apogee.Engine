using System;
using System.Numerics;
using Apogee.Content;
using Apogee.Core.Graphics;
using Apogee.Editor.Editing;
using Apogee.Graphics;
using Apogee.Host;
using Apogee.Input;
using Apogee.Rendering;
using Apogee.Rendering.Shaders;
using Apogee.UI;

namespace Apogee.Editor;

/// <summary>
/// Draws one session's world into an image that the viewport panel shows as a texture: the scene seen through the editor's camera (or, while playing, the game's main camera), its debug lines, and while
/// playing the game's UI. There is one image per frame in flight, so the frame being recorded never overwrites one the GPU is still sampling. It belongs to a session and is made again when the session is replaced.
/// </summary>
public sealed class ViewportRenderer : IDisposable
{
    private sealed class Slot
    {
        public GpuImage? Image;
        public uint Index;
        public uint Width;
        public uint Height;
    }

    /// <summary>Frees an image and its bindless index once the frame that last sampled it is done.</summary>
    private sealed class Retired(GraphicsDevice device, GpuImage image, uint index) : IDisposable
    {
        public void Dispose()
        {
            device.Bindless.RemoveSampledImage(index);
            image.Dispose();
        }
    }

    private readonly GraphicsDevice _device;
    private readonly ScriptSession _session;
    private readonly ShaderLibrary _shaders;
    private readonly RenderWorld _renderWorld;
    private readonly Camera _camera = new();
    private readonly Slot[] _slots;
    private readonly InputDevices? _devices;
    private readonly UiInput? _uiInput;
    private readonly Func<bool>? _viewportHasKeyboard;
    private UiRenderer? _ui;
    private uint _renderedWidth;
    private uint _renderedHeight;

    public ViewportRenderer(GraphicsDevice device, ShaderLibrary shaders, ScriptSession session, int framesInFlight, bool playing, Func<bool> viewportHasKeyboard, Func<bool> viewportHasMouse, uint windowId = 0)
    {
        _device = device;
        _shaders = shaders;
        _session = session;
        Renderer = new SceneRenderer(device, shaders);
        session.Models.Attach(Renderer, new TextureCaps(device.SupportsBc, device.SupportsAstc));
        _renderWorld = new RenderWorld(session.World, session.Models);
        _slots = new Slot[Math.Max(framesInFlight, 1)];
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot();
        }

        if (playing)
        {
            // The game's input actions work while the viewport has the keyboard and the pointer; the editor's own widgets keep them otherwise.
            _devices = new InputDevices(session.Input) { KeyboardCaptured = () => !viewportHasKeyboard(), MouseCaptured = () => !viewportHasMouse() };

            // The game's UI gets the keys and the typed text while the viewport has the keyboard; the pointer is fed by the panel, in the panel's coordinates.
            _viewportHasKeyboard = viewportHasKeyboard;
            _uiInput = new UiInput(session.Ui, windowId) { PointerEnabled = false, KeyboardEnabled = viewportHasKeyboard };
        }
    }

    public SceneRenderer Renderer { get; }

    /// <summary>Once a frame while playing, after the events were pumped: starts SDL's text input when a text field of the game's UI has the focus and the viewport has the keyboard, and stops it otherwise.</summary>
    public void UpdateInput(Apogee.Platform.Window window)
    {
        if (_uiInput is not null && _viewportHasKeyboard!())
        {
            _uiInput.Update(window, applyCursor: false);
        }
    }

    /// <summary>The camera of the last frame (the game's or the editor's), for the gizmo's matrices and picking.</summary>
    public Camera Camera => _camera;

    /// <summary>Whether the last frame was seen through a camera of the scene.</summary>
    public bool UsedGameCamera { get; private set; }

    public RenderWorld RenderWorld => _renderWorld;

    /// <summary>The size of the image the last frame drew, in pixels.</summary>
    public (uint Width, uint Height) Size => (_renderedWidth, _renderedHeight);

    /// <summary>
    /// The bindless index of this frame's image at the size asked for (made, or made again when the size changed). The image is not drawn until <see cref="Render"/>; call both in the same frame.
    /// </summary>
    public uint Acquire(Frame frame, uint width, uint height)
    {
        width = Math.Max(width, 16);
        height = Math.Max(height, 16);
        Slot slot = _slots[frame.SlotIndex % _slots.Length];
        if (slot.Image is null || slot.Width != width || slot.Height != height)
        {
            if (slot.Image is not null)
            {
                frame.Defer(new Retired(_device, slot.Image, slot.Index));
            }

            slot.Image = _device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, width, height, ImageUsage.ColorAttachment | ImageUsage.Sampled), "Editor viewport");
            slot.Index = _device.Bindless.AddSampledImage(slot.Image);
            slot.Width = width;
            slot.Height = height;
        }

        _renderedWidth = width;
        _renderedHeight = height;
        return slot.Index;
    }

    /// <summary>
    /// Records the scene into this frame's image (after <see cref="Acquire"/>). While playing, the world's main camera is used when it has one and <paramref name="preferGameCamera"/> is set.
    /// </summary>
    public void Render(Frame frame, EditorCamera editorCamera, bool playing, bool preferGameCamera, float density)
    {
        Slot slot = _slots[frame.SlotIndex % _slots.Length];
        UsedGameCamera = playing && preferGameCamera && _renderWorld.TryGetMainCamera(_camera);
        if (!UsedGameCamera)
        {
            editorCamera.ApplyTo(_camera);
        }

        IRenderOverlay? overlay = null;
        if (playing)
        {
            _session.Ui.SetSize((int)slot.Width, (int)slot.Height, density);
            if (_ui is null && _session.Ui.DocumentsLoaded > 0)
            {
                _ui = new UiRenderer(_device, _session.Ui, _shaders);
            }

            overlay = _ui;
        }

        Renderer.Render(frame, _renderWorld, _camera, RenderTarget.Texture(slot.Image!, slot.Index), new RenderSettings { DebugDraw = _session.DebugDraw }, overlay);
    }

    public void Dispose()
    {
        _device.WaitIdle();
        _devices?.Dispose();
        _uiInput?.Dispose();
        _ui?.Dispose();
        _session.Models.Detach();
        _renderWorld.Dispose();
        Renderer.Dispose();
        foreach (Slot slot in _slots)
        {
            if (slot.Image is not null)
            {
                _device.Bindless.RemoveSampledImage(slot.Index);
                slot.Image.Dispose();
            }
        }
    }
}
