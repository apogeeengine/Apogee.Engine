// Builds the view-space bounding box of every cluster of the 16 x 9 x 24 grid. Run when the projection changes.
#include "common.slang"

struct ClusterPush { FrameData* frame; };
[[vk::push_constant]] ConstantBuffer<ClusterPush> push PUSH_SLOT;

[shader("compute")] [numthreads(64, 1, 1)]
void buildClusters(uint3 id : SV_DispatchThreadID)
{
    uint cluster = id.x;
    if (cluster >= CLUSTERS_X * CLUSTERS_Y * CLUSTERS_Z)
        return;

    FrameData* frame = push.frame;
    uint x = cluster % CLUSTERS_X;
    uint y = (cluster / CLUSTERS_X) % CLUSTERS_Y;
    uint z = cluster / (CLUSTERS_X * CLUSTERS_Y);

    // The cluster's rectangle in normalized device coordinates (y grows downwards in Vulkan) and its depth range.
    float2 ndcMin = float2(float(x) / float(CLUSTERS_X), float(y) / float(CLUSTERS_Y)) * 2.0 - 1.0;
    float2 ndcMax = float2(float(x + 1) / float(CLUSTERS_X), float(y + 1) / float(CLUSTERS_Y)) * 2.0 - 1.0;
    float zNear = sliceDepth(frame, z);
    float zFar = sliceDepth(frame, z + 1);

    float3 boxMin = float3(1e30, 1e30, 1e30);
    float3 boxMax = float3(-1e30, -1e30, -1e30);
    for (uint corner = 0; corner < 8; corner++)
    {
        float2 ndc = float2((corner & 1) != 0 ? ndcMax.x : ndcMin.x, (corner & 2) != 0 ? ndcMax.y : ndcMin.y);
        float depth = (corner & 4) != 0 ? zFar : zNear;
        // clip.xy = proj scale * view.xy and clip.w = depth, so view.xy = ndc * depth / scale.
        float3 p = float3(ndc.x * depth / frame->projScale.x, ndc.y * depth / frame->projScale.y, -depth);
        boxMin = min(boxMin, p);
        boxMax = max(boxMax, p);
    }

    frame->clusterBounds[cluster * 2] = float4(boxMin, 0.0);
    frame->clusterBounds[cluster * 2 + 1] = float4(boxMax, 0.0);
}
