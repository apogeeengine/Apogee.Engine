using System;
using System.Numerics;
using Apogee.Content;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Rendering;

/// <summary>
/// An ECS <see cref="World"/> as something to draw: every entity with a <see cref="ModelRenderer"/> and a <see cref="WorldTransform"/> shows its model there, every <see cref="Light"/> lights
/// it, and the entity with <see cref="MainCamera"/> is the eye (<see cref="TryGetMainCamera"/>). Nothing is copied: the world is read when a frame is rendered, so what the systems
/// moved this frame is what is drawn.
/// </summary>
public sealed class RenderWorld : IRenderSource, IDisposable
{
    private readonly World _world;
    private readonly ModelLibrary _models;
    private readonly Query<ModelRenderer, WorldTransform> _renderers;
    private readonly Query<Light, WorldTransform> _lights;
    private readonly Query<CameraComponent, WorldTransform> _cameras;
    private readonly EachAction<ModelRenderer, WorldTransform> _gatherModel;
    private readonly EachAction<Light, WorldTransform> _gatherLight;
    private SceneGather? _into;
    private Double3 _camera;
    private bool _sunFound;

    public RenderWorld(World world, ModelLibrary models)
    {
        _world = world;
        _models = models;
        _renderers = world.Query<ModelRenderer, WorldTransform>();
        _lights = world.Query<Light, WorldTransform>();
        _cameras = world.Query<CameraComponent, WorldTransform>();
        _gatherModel = GatherModel;
        _gatherLight = GatherLight;
    }

    public World World => _world;

    /// <summary>The direction toward the sun and its color times illuminance when no directional <see cref="Light"/> is in the world. Zero color: no sun.</summary>
    public Vector3 DefaultSunDirection { get; set; } = Vector3.Normalize(new Vector3(0.4f, 1f, 0.3f));

    public Vector3 DefaultSunColor { get; set; }

    public Vector3 AmbientSky { get; set; } = new(0.12f, 0.15f, 0.2f);

    public Vector3 AmbientGround { get; set; } = new(0.05f, 0.045f, 0.04f);

    /// <summary>Copies the main camera's placement and lens into <paramref name="camera"/>. False, and the camera untouched, when no entity is a <see cref="MainCamera"/>.</summary>
    public bool TryGetMainCamera(Camera camera)
    {
        bool found = false;
        _cameras.Each((Entity entity, ref CameraComponent lens, ref WorldTransform placement) =>
        {
            if (found || !_world.Has<MainCamera>(entity))
            {
                return;
            }

            found = true;
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, placement.Rotation);
            camera.Position = placement.Position;
            camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
            camera.Yaw = MathF.Atan2(-forward.X, -forward.Z);
            camera.VerticalFov = lens.FovDegrees * MathF.PI / 180f;
            camera.Near = lens.Near;
            camera.ClusterFar = lens.ClusterFar;
        });
        return found;
    }

    void IRenderSource.Gather(SceneGather into, Double3 camera)
    {
        _into = into;
        _camera = camera;
        _sunFound = false;
        into.SunDirection = DefaultSunDirection;
        into.SunColor = DefaultSunColor;
        into.AmbientSky = AmbientSky;
        into.AmbientGround = AmbientGround;
        _renderers.Each(_gatherModel);
        _lights.Each(_gatherLight);
        _into = null;
    }

    private void GatherModel(Entity entity, ref ModelRenderer renderer, ref WorldTransform placement)
    {
        GpuModel? model = _models.Find(renderer.Model);
        if (model is null)
        {
            return;
        }

        SceneGather into = _into!;
        into.Placements.Add(new Placement(model, placement.Position, placement.Rotation, placement.Scale));

        // The lights the model came with move with it.
        if (model.Lights.Count > 0)
        {
            Vector3 fromCamera = (Vector3)(placement.Position - _camera);
            Matrix4x4 orientation = Matrix4x4.CreateScale(placement.Scale) * Matrix4x4.CreateFromQuaternion(placement.Rotation);
            foreach (LightData light in model.Lights)
            {
                into.Lights.Add(SceneRenderer.ToGpu(
                    light.Kind,
                    fromCamera + Vector3.TransformNormal(light.Position, orientation),
                    Vector3.Transform(light.Direction, placement.Rotation),
                    light.Color,
                    light.Intensity,
                    light.Range,
                    light.InnerConeAngle,
                    light.OuterConeAngle));
            }
        }
    }

    private void GatherLight(Entity entity, ref Light light, ref WorldTransform placement)
    {
        SceneGather into = _into!;
        Vector3 direction = Vector3.Transform(-Vector3.UnitZ, placement.Rotation);
        var kind = (LightKind)light.Kind;
        if (kind == LightKind.Directional)
        {
            if (!_sunFound)
            {
                _sunFound = true;
                into.SunDirection = -direction;
                into.SunColor = light.Color * light.Intensity;
            }

            return;
        }

        into.Lights.Add(SceneRenderer.ToGpu(kind, (Vector3)(placement.Position - _camera), direction, light.Color, light.Intensity, light.Range, light.InnerCone, light.OuterCone));
    }

    /// <summary>Releases the queries. The world and the models are not owned.</summary>
    public void Dispose()
    {
        _renderers.Dispose();
        _lights.Dispose();
        _cameras.Dispose();
    }
}
