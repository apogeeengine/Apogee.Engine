using System.Collections.Generic;
using System.Linq;
using Apogee.Graphics;

namespace Apogee.Rendering.Graph;

/// <summary>Compiles declared passes into a <see cref="GraphPlan"/>. Pure: it needs no device, so it is tested on its own.</summary>
internal static class Planner
{
    public static GraphPlan Plan(IReadOnlyList<ResourceNode> resources, IReadOnlyList<PassNode> passes)
    {
        var plan = new GraphPlan();

        // 1. Culling, backwards from the outputs: a pass is needed if it writes something needed, and then so is what it reads.
        //    A pass that overwrites a resource without reading it makes the earlier writers of it unneeded.
        var needed = new bool[resources.Count];
        foreach (ResourceNode resource in resources.Where(r => r.IsOutput))
        {
            needed[resource.Index] = true;
        }

        var live = new bool[passes.Count];
        for (int i = passes.Count - 1; i >= 0; i--)
        {
            PassNode pass = passes[i];
            IEnumerable<ResourceUse> uses = pass.ImageUses.Concat(pass.BufferUses);
            if (!pass.SideEffects && !uses.Any(u => u.Writes && needed[u.Resource]))
            {
                continue;
            }

            live[i] = true;
            foreach (ResourceUse use in uses.Where(u => u.Writes && !u.Reads))
            {
                needed[use.Resource] = false;
            }

            foreach (ResourceUse use in uses.Where(u => u.Reads))
            {
                needed[use.Resource] = true;
            }
        }

        // 2. Order, lifetimes and validation: every read needs something written before it.
        var written = new bool[resources.Count];
        foreach (ResourceNode resource in resources.Where(r => r.Imported))
        {
            written[resource.Index] = resource.Kind == ResourceKind.Image ? resource.ImageInitial != ImageState.Undefined : resource.BufferInitial != BufferState.Undefined;
        }

        foreach (ResourceNode resource in resources)
        {
            resource.FirstUse = int.MaxValue;
            resource.LastUse = -1;
            resource.Physical = -1;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            if (!live[i])
            {
                continue;
            }

            PassNode pass = passes[i];
            int order = plan.LivePasses.Count;
            plan.LivePasses.Add(pass);
            foreach (ResourceUse use in pass.ImageUses.Concat(pass.BufferUses))
            {
                ResourceNode resource = resources[use.Resource];
                if (use.Reads && !written[use.Resource])
                {
                    throw new RenderGraphException($"Pass '{pass.Name}' reads '{resource.Name}', which no earlier pass writes and which was not imported with contents.");
                }

                resource.FirstUse = System.Math.Min(resource.FirstUse, order);
                resource.LastUse = System.Math.Max(resource.LastUse, order);
            }

            foreach (ResourceUse use in pass.ImageUses.Concat(pass.BufferUses).Where(u => u.Writes))
            {
                written[use.Resource] = true;
            }

            ValidateAttachments(resources, pass);
        }

        // 3. Share physical images and buffers between transients whose lifetimes do not overlap and whose descriptions are equal.
        var imageSlots = new List<int>();   // last use of each physical image
        foreach (ResourceNode resource in resources.Where(r => !r.Imported && r.Kind == ResourceKind.Image && r.LastUse >= 0).OrderBy(r => r.FirstUse))
        {
            plan.TransientImageCount++;
            int slot = -1;
            for (int s = 0; s < imageSlots.Count; s++)
            {
                if (plan.PhysicalImages[s].Equals(resource.ImageDescription) && imageSlots[s] < resource.FirstUse)
                {
                    slot = s;
                    break;
                }
            }

            if (slot < 0)
            {
                slot = plan.PhysicalImages.Count;
                plan.PhysicalImages.Add(resource.ImageDescription);
                imageSlots.Add(0);
            }

            imageSlots[slot] = resource.LastUse;
            resource.Physical = slot;
        }

        var bufferSlots = new List<int>();
        foreach (ResourceNode resource in resources.Where(r => !r.Imported && r.Kind == ResourceKind.Buffer && r.LastUse >= 0).OrderBy(r => r.FirstUse))
        {
            int slot = -1;
            for (int s = 0; s < bufferSlots.Count; s++)
            {
                if (plan.PhysicalBuffers[s] == (resource.BufferSize, resource.BufferUsage) && bufferSlots[s] < resource.FirstUse)
                {
                    slot = s;
                    break;
                }
            }

            if (slot < 0)
            {
                slot = plan.PhysicalBuffers.Count;
                plan.PhysicalBuffers.Add((resource.BufferSize, resource.BufferUsage));
                bufferSlots.Add(0);
            }

            bufferSlots[slot] = resource.LastUse;
            resource.Physical = slot;
        }

        // 4. Barriers: in front of each pass, whatever its resources need to go from their last state to the one the pass wants.
        //    Reading after reading in the same state needs none; anything after a write needs one (data and execution dependency).
        var imageState = new ImageState[resources.Count];
        var bufferState = new BufferState[resources.Count];
        foreach (ResourceNode resource in resources.Where(r => r.Imported))
        {
            imageState[resource.Index] = resource.ImageInitial;
            bufferState[resource.Index] = resource.BufferInitial;
        }

        foreach (PassNode pass in plan.LivePasses)
        {
            var images = new List<PlannedImageBarrier>();
            var buffers = new List<PlannedBufferBarrier>();
            foreach (ResourceUse use in pass.ImageUses)
            {
                var wanted = (ImageState)use.State;
                ImageState current = imageState[use.Resource];
                if (current != wanted || ResourceStates.Writes(current))
                {
                    images.Add(new PlannedImageBarrier(use.Resource, current, wanted));
                }

                imageState[use.Resource] = wanted;
            }

            foreach (ResourceUse use in pass.BufferUses)
            {
                var wanted = (BufferState)use.State;
                BufferState current = bufferState[use.Resource];
                if (current != wanted || ResourceStates.Writes(current))
                {
                    buffers.Add(new PlannedBufferBarrier(use.Resource, current, wanted));
                }

                bufferState[use.Resource] = wanted;
            }

            plan.Barriers[pass.Index] = (images, buffers);
        }

        // 5. Imported resources leave in the state the caller asked for (the window's image in Present).
        foreach (ResourceNode resource in resources.Where(r => r.Imported && r.LastUse >= 0))
        {
            if (resource.Kind == ResourceKind.Image && resource.ImageFinal != ImageState.Undefined && imageState[resource.Index] != resource.ImageFinal)
            {
                plan.Epilogue.Images.Add(new PlannedImageBarrier(resource.Index, imageState[resource.Index], resource.ImageFinal));
            }
            else if (resource.Kind == ResourceKind.Buffer && resource.BufferFinal != BufferState.Undefined && bufferState[resource.Index] != resource.BufferFinal)
            {
                plan.Epilogue.Buffers.Add(new PlannedBufferBarrier(resource.Index, bufferState[resource.Index], resource.BufferFinal));
            }
        }

        plan.Passes = [.. passes.Select(p => Describe(resources, p, live[p.Index], plan))];
        return plan;
    }

    private static void ValidateAttachments(IReadOnlyList<ResourceNode> resources, PassNode pass)
    {
        if (pass.Colors.Count == 0 && pass.Depth is null)
        {
            return;
        }

        if (pass.Type != PassType.Graphics)
        {
            throw new RenderGraphException($"Pass '{pass.Name}' has attachments but is not a graphics pass.");
        }

        var sizes = pass.Colors.Select(c => resources[c.Resource]).Concat(pass.Depth is { } d ? [resources[d.Resource]] : [])
            .Select(r => (r.Name, Width: AttachmentSize(r).Width, Height: AttachmentSize(r).Height))
            .ToList();
        if (sizes.Any(s => s.Width != sizes[0].Width || s.Height != sizes[0].Height))
        {
            throw new RenderGraphException($"Pass '{pass.Name}' renders to attachments of different sizes: {string.Join(", ", sizes.Select(s => $"{s.Name} {s.Width}x{s.Height}"))}.");
        }
    }

    internal static (uint Width, uint Height) AttachmentSize(ResourceNode resource) =>
        resource.ImportedBackbuffer is { } back ? (back.Width, back.Height)
        : resource.ImportedImage is { } image ? (image.Width, image.Height)
        : (resource.ImageDescription.Width, resource.ImageDescription.Height);

    private static PassPlan Describe(IReadOnlyList<ResourceNode> resources, PassNode pass, bool live, GraphPlan plan)
    {
        string[] reads = [.. pass.ImageUses.Concat(pass.BufferUses).Where(u => u.Reads).Select(u => resources[u.Resource].Name)];
        string[] writes = [.. pass.ImageUses.Concat(pass.BufferUses).Where(u => u.Writes).Select(u => resources[u.Resource].Name)];
        int barriers = live && plan.Barriers.TryGetValue(pass.Index, out var b) ? b.Images.Count + b.Buffers.Count : 0;
        return new PassPlan(pass.Name, pass.Type, !live, reads, writes, barriers);
    }
}
