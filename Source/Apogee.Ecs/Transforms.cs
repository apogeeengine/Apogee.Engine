using System;
using Apogee.Core.Numerics;
using Apogee.Native.Flecs;
using Apogee.Reflection;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs;

public sealed unsafe partial class World
{
    /// <summary>
    /// The world placement of <paramref name="entity"/> computed now, from its <see cref="Transform"/> and those of its parents. <see cref="WorldTransform"/> is only up to date after the
    /// last <see cref="Progress"/>; this is for code that changes transforms and needs the result before the next one (physics placing a body it was just given). An entity with no
    /// <see cref="Transform"/> counts as the identity.
    /// </summary>
    public Trs ComputeWorldTransform(Entity entity)
    {
        // Top down, exactly as the transform system composes: the world placement of the parent, then this local one in it. A parent with no Transform counts as the identity.
        Trs local = TryGet(entity, out Transform transform) ? transform.ToTrs() : Trs.Identity;
        Entity parent = GetParent(entity);
        return parent.Id == 0 ? local : local.InWorldOf(ComputeWorldTransform(parent));
    }

    /// <summary>
    /// Registers <see cref="Transform"/> and <see cref="WorldTransform"/> and keeps the second equal to the first composed with the parents'
    /// (<c>ChildOf</c>), top down, in the <see cref="Phase.PostUpdate"/> of every <see cref="Progress"/>. A root entity's world transform is
    /// its own. Adding a <see cref="Transform"/> also adds its <see cref="WorldTransform"/> (the <c>With</c> trait), so the system never
    /// misses one. Every world has it.
    /// </summary>
    private void InstallTransforms()
    {
        Entity local = Component<Transform>();
        Entity world = Component<WorldTransform>();
        Flecs.AddId(Native, local.Id, Flecs.MakePair(Flecs.With, world.Id));

        _ = AddSystem(
            "TransformSystem",
            [new QueryTerm(local), new QueryTerm(world), new QueryTerm(world, QueryOperator.Optional, QuerySource.ParentCascade)],
            Phase.PostUpdate,
            static (in TableView view) =>
            {
                Span<Transform> locals = view.Field<Transform>(0);
                Span<WorldTransform> worlds = view.Field<WorldTransform>(1);
                WorldTransform* parent = (WorldTransform*)view.FieldPointer(2, sizeof(WorldTransform));
                if (parent is null)
                {
                    for (int i = 0; i < locals.Length; i++)
                    {
                        worlds[i] = WorldTransform.From(locals[i].ToTrs());
                    }

                    return;
                }

                Trs parentTrs = parent->ToTrs();
                for (int i = 0; i < locals.Length; i++)
                {
                    worlds[i] = WorldTransform.From(locals[i].ToTrs().InWorldOf(parentTrs));
                }
            });
    }
}
