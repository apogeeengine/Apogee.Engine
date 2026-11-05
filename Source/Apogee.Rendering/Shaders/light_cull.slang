// For every cluster, lists the lights whose sphere of influence touches it. One thread per cluster; a cluster keeps at most
// MAX_LIGHTS_PER_CLUSTER lights (the rest are dropped), so there are no atomics and nothing to clear.
#include "common.slang"

struct CullPush { FrameData* frame; };
[[vk::push_constant]] ConstantBuffer<CullPush> push PUSH_SLOT;

[shader("compute")] [numthreads(64, 1, 1)]
void cullLights(uint3 id : SV_DispatchThreadID)
{
    uint cluster = id.x;
    if (cluster >= CLUSTERS_X * CLUSTERS_Y * CLUSTERS_Z)
        return;

    FrameData* frame = push.frame;
    float3 boxMin = frame->clusterBounds[cluster * 2].xyz;
    float3 boxMax = frame->clusterBounds[cluster * 2 + 1].xyz;

    uint count = 0;
    uint lightCount = frame->counts.x;
    uint* list = frame->clusterLights + cluster * CLUSTER_STRIDE;
    for (uint i = 0; i < lightCount && count < MAX_LIGHTS_PER_CLUSTER; i++)
    {
        Light light = frame->lights[i];
        float3 center = mul(float4(light.positionRadius.xyz, 1.0), frame->view).xyz;
        float radius = light.positionRadius.w;

        // Squared distance from the sphere's center to the box.
        float3 nearest = clamp(center, boxMin, boxMax);
        float3 delta = center - nearest;
        if (dot(delta, delta) <= radius * radius)
        {
            list[1 + count] = i;
            count++;
        }
    }

    list[0] = count;
}
