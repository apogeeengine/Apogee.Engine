using System;
using Apogee.Content;
using Apogee.DevTools;
using Apogee.Graphics;
using Apogee.Rendering;
using Apogee.Rendering.Shaders;

namespace Apogee.Host;

/// <summary>
/// The session's world drawn through its main camera (the entity with <see cref="MainCamera"/>), with the ImGui frame over it. Nothing is made until a world has such a camera: a script
/// that only has panels never compiles the scene shaders, and keeps drawing its panels over the plain background.
/// </summary>
public sealed class WorldView : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly ShaderLibrary _shaders;
    private readonly ScriptSession _session;
    private readonly IRenderOverlay _overlay;
    private readonly RenderWorld _world;
    private readonly Camera _camera = new();
    private SceneRenderer? _renderer;

    public WorldView(GraphicsDevice device, ShaderLibrary shaders, ScriptSession session, IRenderOverlay overlay)
    {
        _device = device;
        _shaders = shaders;
        _session = session;
        _overlay = overlay;
        _world = new RenderWorld(session.World, session.Models);
    }

    /// <summary>The renderer of the scene, once there has been a frame that needed it.</summary>
    public SceneRenderer? Renderer => _renderer;

    /// <summary>
    /// Records the frame into <paramref name="frame"/>, ending with the image in <paramref name="target"/>, and returns true; or returns false, recording nothing, when the world has no main camera.
    /// </summary>
    public bool TryDraw(Frame frame, in RenderTarget target)
    {
        if (!_world.TryGetMainCamera(_camera))
        {
            return false;
        }

        if (_renderer is null)
        {
            _renderer = new SceneRenderer(_device, _shaders);
            _session.Models.Attach(_renderer, new TextureCaps(_device.SupportsBc, _device.SupportsAstc));
        }

        if (_session.Physics.DebugDraw)
        {
            _session.Physics.DrawDebug(_session.DebugDraw, camera: _camera.Position);
        }

        _renderer.Render(frame, _world, _camera, target, new RenderSettings { DebugDraw = _session.DebugDraw }, _overlay);
        return true;
    }

    public void Dispose()
    {
        _device.WaitIdle();
        _session.Models.Detach(); // the models go before the device does
        _world.Dispose();
        _renderer?.Dispose();
    }
}
