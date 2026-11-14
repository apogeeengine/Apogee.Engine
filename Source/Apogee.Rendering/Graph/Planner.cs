using System;
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
            foreach (ResourceUse use in uses.Where(u => u.Writes && !u.Reads && Covers(resources[u.Resource], u)))
            {
                needed[use.Resource] = false;
            }

            foreach (ResourceUse use in uses.Where(u => u.Reads))
            {
                needed[use.Resource] = true;
            }
        }

        // 2. Order, lifetimes and validation: every read needs something written before it.
        // Whether each part has been written: one flag per buffer, one per mip level and layer of an image.
        var written = new bool[resources.Count][];
        foreach (ResourceNode resource in resources)
        {
            written[resource.Index] = new bool[resource.Kind == ResourceKind.Image ? resource.MipCount * resource.LayerCount : 1];
            if (resource.Imported && (resource.Kind == ResourceKind.Image ? resource.ImageInitial != ImageState.Undefined : resource.BufferInitial != BufferState.Undefined))
            {
                Array.Fill(written[resource.Index], true);
            }
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
                if (use.Reads && !AllWritten(resource, written[use.Resource], use))
                {
                    throw new RenderGraphException($"Pass '{pass.Name}' reads '{resource.Name}', which no earlier pass writes and which was not imported with contents.");
                }

                resource.FirstUse = System.Math.Min(resource.FirstUse, order);
                resource.LastUse = System.Math.Max(resource.LastUse, order);
            }

            foreach (ResourceUse use in pass.ImageUses.Concat(pass.BufferUses).Where(u => u.Writes))
            {
                MarkWritten(resources[use.Resource], written[use.Resource], use);
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
        var imageState = new ImageState[resources.Count][];
        var bufferState = new BufferState[resources.Count];
        foreach (ResourceNode resource in resources)
        {
            if (resource.Kind == ResourceKind.Image)
            {
                imageState[resource.Index] = new ImageState[resource.MipCount * resource.LayerCount];
                if (resource.Imported)
                {
                    Array.Fill(imageState[resource.Index], resource.ImageInitial);
                }
            }
            else if (resource.Imported)
            {
                bufferState[resource.Index] = resource.BufferInitial;
            }
        }

        foreach (PassNode pass in plan.LivePasses)
        {
            var images = new List<PlannedImageBarrier>();
            var buffers = new List<PlannedBufferBarrier>();
            foreach (ResourceUse use in pass.ImageUses)
            {
                AddImageBarriers(images, resources[use.Resource], imageState[use.Resource], use.Range, (ImageState)use.State, afterWrites: true);
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
            if (resource.Kind == ResourceKind.Image && resource.ImageFinal != ImageState.Undefined)
            {
                AddImageBarriers(plan.Epilogue.Images, resource, imageState[resource.Index], new SubresourceRange(0, resource.MipCount, 0, resource.LayerCount), resource.ImageFinal, afterWrites: false);
            }
            else if (resource.Kind == ResourceKind.Buffer && resource.BufferFinal != BufferState.Undefined && bufferState[resource.Index] != resource.BufferFinal)
            {
                plan.Epilogue.Buffers.Add(new PlannedBufferBarrier(resource.Index, bufferState[resource.Index], resource.BufferFinal));
            }
        }

        plan.EpilogueImageBarriers = [.. plan.Epilogue.Images.Select(b => Describe(resources, b))];
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

        var sizes = pass.Colors.Select(c => (resources[c.Resource], c.Range)).Concat(pass.Depth is { } d ? [(resources[d.Resource], d.Range)] : [])
            .Select(a => (a.Item1.Name, Width: AttachmentSize(a.Item1, a.Range.BaseMip).Width, Height: AttachmentSize(a.Item1, a.Range.BaseMip).Height))
            .ToList();
        if (sizes.Any(s => s.Width != sizes[0].Width || s.Height != sizes[0].Height))
        {
            throw new RenderGraphException($"Pass '{pass.Name}' renders to attachments of different sizes: {string.Join(", ", sizes.Select(s => $"{s.Name} {s.Width}x{s.Height}"))}.");
        }
    }

    /// <summary>The size of a mip level of an image resource.</summary>
    internal static (uint Width, uint Height) AttachmentSize(ResourceNode resource, uint mip = 0)
    {
        (uint width, uint height) = resource.ImportedBackbuffer is { } back ? (back.Width, back.Height)
            : resource.ImportedImage is { } image ? (image.Width, image.Height)
            : (resource.ImageDescription.Width, resource.ImageDescription.Height);
        return (System.Math.Max(1, width >> (int)mip), System.Math.Max(1, height >> (int)mip));
    }

    /// <summary>Whether a use covers the whole of its resource (buffers are always used whole).</summary>
    private static bool Covers(ResourceNode resource, ResourceUse use) =>
        resource.Kind == ResourceKind.Buffer || (use.Range.BaseMip == 0 && use.Range.MipCount == resource.MipCount && use.Range.BaseLayer == 0 && use.Range.LayerCount == resource.LayerCount);

    private static bool AllWritten(ResourceNode resource, bool[] written, ResourceUse use)
    {
        if (resource.Kind == ResourceKind.Buffer)
        {
            return written[0];
        }

        for (uint mip = use.Range.BaseMip; mip < use.Range.BaseMip + use.Range.MipCount; mip++)
        {
            for (uint layer = use.Range.BaseLayer; layer < use.Range.BaseLayer + use.Range.LayerCount; layer++)
            {
                if (!written[(mip * resource.LayerCount) + layer])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void MarkWritten(ResourceNode resource, bool[] written, ResourceUse use)
    {
        if (resource.Kind == ResourceKind.Buffer)
        {
            written[0] = true;
            return;
        }

        for (uint mip = use.Range.BaseMip; mip < use.Range.BaseMip + use.Range.MipCount; mip++)
        {
            for (uint layer = use.Range.BaseLayer; layer < use.Range.BaseLayer + use.Range.LayerCount; layer++)
            {
                written[(mip * resource.LayerCount) + layer] = true;
            }
        }
    }

    /// <summary>
    /// Moves the parts of an image a use covers to the wanted state, adding a barrier for every rectangle of mips and layers that is in another state (or, with <paramref name="afterWrites"/>, was written: writing
    /// after writing needs one in the same state). Parts in the same state are grouped per mip level and the levels with the same layers merged, so a whole image in one state is one barrier.
    /// </summary>
    private static void AddImageBarriers(List<PlannedImageBarrier> barriers, ResourceNode resource, ImageState[] states, SubresourceRange range, ImageState wanted, bool afterWrites)
    {
        uint layers = resource.LayerCount;
        int first = barriers.Count;
        for (uint mip = range.BaseMip; mip < range.BaseMip + range.MipCount; mip++)
        {
            uint layer = range.BaseLayer;
            uint end = range.BaseLayer + range.LayerCount;
            while (layer < end)
            {
                ImageState current = states[(mip * layers) + layer];
                uint start = layer;
                while (layer < end && states[(mip * layers) + layer] == current)
                {
                    layer++;
                }

                bool needed = current != wanted || (afterWrites && ResourceStates.Writes(current));
                if (!needed)
                {
                    continue;
                }

                // The same layers in the same state as the level before: that barrier grows by a level.
                int match = -1;
                for (int i = first; i < barriers.Count; i++)
                {
                    PlannedImageBarrier b = barriers[i];
                    if (b.Before == current && b.BaseLayer == start && b.LayerCount == layer - start && b.BaseMip + b.MipCount == mip)
                    {
                        match = i;
                        break;
                    }
                }

                if (match >= 0)
                {
                    barriers[match] = barriers[match] with { MipCount = barriers[match].MipCount + 1 };
                }
                else
                {
                    barriers.Add(new PlannedImageBarrier(resource.Index, current, wanted, mip, 1, start, layer - start));
                }
            }
        }

        for (uint mip = range.BaseMip; mip < range.BaseMip + range.MipCount; mip++)
        {
            Array.Fill(states, wanted, (int)((mip * layers) + range.BaseLayer), (int)range.LayerCount);
        }
    }

    private static ImageBarrierPlan Describe(IReadOnlyList<ResourceNode> resources, PlannedImageBarrier b) =>
        new(resources[b.Resource].Name, b.Before, b.After, b.BaseMip, b.MipCount, b.BaseLayer, b.LayerCount);

    private static PassPlan Describe(IReadOnlyList<ResourceNode> resources, PassNode pass, bool live, GraphPlan plan)
    {
        string[] reads = [.. pass.ImageUses.Concat(pass.BufferUses).Where(u => u.Reads).Select(u => resources[u.Resource].Name)];
        string[] writes = [.. pass.ImageUses.Concat(pass.BufferUses).Where(u => u.Writes).Select(u => resources[u.Resource].Name)];
        int barriers = live && plan.Barriers.TryGetValue(pass.Index, out var b) ? b.Images.Count + b.Buffers.Count : 0;
        ImageBarrierPlan[] imageBarriers = live && plan.Barriers.TryGetValue(pass.Index, out var planned) ? [.. planned.Images.Select(b => Describe(resources, b))] : [];
        return new PassPlan(pass.Name, pass.Type, !live, reads, writes, barriers, imageBarriers);
    }
}
