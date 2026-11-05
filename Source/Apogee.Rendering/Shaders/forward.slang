// The forward pass: glTF metallic-roughness PBR (Cook-Torrance GGX), lit by the sun and by the lights of the pixel's cluster.
#include "common.slang"

[[vk::push_constant]] ConstantBuffer<DrawPush> push PUSH_SLOT;

struct ForwardInput
{
    float4 position : SV_Position;
    float3 worldPosition : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    float2 uv : TEXCOORD3;
    nointerpolation uint material : TEXCOORD4;
};

[shader("vertex")]
ForwardInput forwardVs(uint vertexId : SV_VertexID)
{
    FrameData* frame = push.frame;
    Instance instance = frame->instances[push.instance];
    Vertex v = push.vertices[instance.vertexOffset + vertexId];

    float4 world = mul(float4(v.px, v.py, v.pz, 1.0), instance.world);
    ForwardInput output;
    output.position = clipPosition(mul(world, frame->viewProj));
    output.worldPosition = world.xyz;
    output.normal = mul(float4(v.nx, v.ny, v.nz, 0.0), instance.normalMatrix).xyz;
    output.tangent = float4(mul(float4(v.tx, v.ty, v.tz, 0.0), instance.world).xyz, v.tw);
    output.uv = float2(v.u, v.v);
    output.material = instance.material;
    return output;
}

float4 sampleTexture(Material material, uint slot, uint texture, uint sampler, float2 uv)
{
    float2 transformed = transformUv(material.transforms[slot * 2], material.transforms[slot * 2 + 1], uv);
    return gTextures[texture].Sample(gSamplers[sampler], transformed);
}

float distributionGgx(float NoH, float a)
{
    float a2 = a * a;
    float d = NoH * NoH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

float visibilitySmith(float NoV, float NoL, float a)
{
    float a2 = a * a;
    float ggxV = NoL * sqrt(NoV * NoV * (1.0 - a2) + a2);
    float ggxL = NoV * sqrt(NoL * NoL * (1.0 - a2) + a2);
    return 0.5 / max(ggxV + ggxL, 1e-5);
}

float3 fresnelSchlick(float3 f0, float VoH)
{
    return f0 + (1.0 - f0) * pow(1.0 - VoH, 5.0);
}

// The light arriving as `radiance` (already color * illuminance, along the surface normal) from direction L, reflected towards V.
float3 evaluateLight(float3 L, float3 radiance, float3 N, float3 V, float3 diffuseColor, float3 f0, float roughness)
{
    float NoL = saturate(dot(N, L));
    if (NoL <= 0.0)
        return float3(0.0, 0.0, 0.0);

    float3 H = normalize(L + V);
    float NoV = max(dot(N, V), 1e-4);
    float NoH = saturate(dot(N, H));
    float VoH = saturate(dot(V, H));
    float a = max(roughness * roughness, 0.002);

    float3 F = fresnelSchlick(f0, VoH);
    float3 specular = F * (distributionGgx(NoH, a) * visibilitySmith(NoV, NoL, a));
    float3 diffuse = (1.0 - F) * diffuseColor / PI;
    return (diffuse + specular) * radiance * NoL;
}

// How many lights a cluster has: nothing is dark, then blue, cyan, green, yellow and red from one light to eight or more.
float3 heatmap(uint count)
{
    if (count == 0)
        return float3(0.02, 0.02, 0.02);

    float t = saturate(float(count - 1) / 7.0);
    return float3(saturate(1.5 - abs(4.0 * t - 3.0)), saturate(1.5 - abs(4.0 * t - 2.0)), saturate(1.5 - abs(4.0 * t - 1.0)));
}

[shader("fragment")]
float4 forwardFs(ForwardInput input, bool frontFacing : SV_IsFrontFace) : SV_Target
{
    FrameData* frame = push.frame;
    Material material = push.materials[input.material];
    uint flags = material.textures1.y;

    float4 baseColor = material.baseColor * sampleTexture(material, 0, material.textures0.x, material.samplers0.x, input.uv);
    if ((flags & FLAG_ALPHA_MASK) != 0 && baseColor.a < material.shading.w)
        discard;

    float alpha = (flags & FLAG_ALPHA_BLEND) != 0 ? baseColor.a : 1.0;
    if ((flags & FLAG_UNLIT) != 0)
        return float4(baseColor.rgb, alpha);

    float4 metallicRoughness = sampleTexture(material, 1, material.textures0.y, material.samplers0.y, input.uv);
    float metallic = saturate(material.emissiveMetallic.a * metallicRoughness.b);
    float roughness = clamp(material.shading.x * metallicRoughness.g, 0.045, 1.0);
    float occlusion = 1.0 + material.shading.z * (sampleTexture(material, 3, material.textures0.w, material.samplers0.w, input.uv).r - 1.0);
    float3 emissive = material.emissiveMetallic.rgb * sampleTexture(material, 4, material.textures1.x, material.samplers1.x, input.uv).rgb;

    float3 N = normalize(input.normal);
    if (!frontFacing)
        N = -N;
    if ((flags & FLAG_NORMAL_MAP) != 0)
    {
        float3 tangentNormal = sampleTexture(material, 2, material.textures0.z, material.samplers0.z, input.uv).xyz * 2.0 - 1.0;
        tangentNormal.xy *= material.shading.y;
        float3 T = normalize(input.tangent.xyz - N * dot(N, input.tangent.xyz));
        float3 B = cross(N, T) * input.tangent.w;
        N = normalize(T * tangentNormal.x + B * tangentNormal.y + N * tangentNormal.z);
    }

    float3 V = normalize(frame->cameraPosition.xyz - input.worldPosition);
    float3 diffuseColor = baseColor.rgb * (1.0 - metallic);
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), baseColor.rgb, metallic);

    float3 color = evaluateLight(frame->sunDirection.xyz, frame->sunColor.rgb, N, V, diffuseColor, f0, roughness);

    // The lights of this pixel's cluster.
    float viewDepth = -mul(float4(input.worldPosition, 1.0), frame->view).z;
    uint cx = min(uint(input.position.x * frame->screen.z * float(CLUSTERS_X)), CLUSTERS_X - 1);
    uint cy = min(uint(input.position.y * frame->screen.w * float(CLUSTERS_Y)), CLUSTERS_Y - 1);
    uint cluster = (clusterSlice(frame, viewDepth) * CLUSTERS_Y + cy) * CLUSTERS_X + cx;
    uint* list = frame->clusterLights + cluster * CLUSTER_STRIDE;
    uint lightCount = list[0];

    for (uint i = 0; i < lightCount; i++)
    {
        Light light = frame->lights[list[1 + i]];
        float3 toLight = light.positionRadius.xyz - input.worldPosition;
        float distance2 = max(dot(toLight, toLight), 1e-4);
        float3 L = toLight * rsqrt(distance2);

        // Inverse-square falloff that reaches exactly zero at the light's radius.
        float radius2 = light.positionRadius.w * light.positionRadius.w;
        float window = saturate(1.0 - (distance2 / radius2) * (distance2 / radius2));
        float attenuation = window * window / distance2;
        if (light.colorType.a > 0.5)
        {
            float cone = saturate(dot(light.directionCone.xyz, -L) * light.spot.x + light.spot.y);
            attenuation *= cone * cone;
        }

        color += evaluateLight(L, light.colorType.rgb * attenuation, N, V, diffuseColor, f0, roughness);
    }

    // A flat two-colour hemisphere for the light that comes from everywhere.
    float3 ambient = lerp(frame->ambientGround.rgb, frame->ambientSky.rgb, N.y * 0.5 + 0.5);
    color += ambient * (diffuseColor + f0 * 0.5) * occlusion + emissive;

    if (frame->counts.y == 1)
        color = lerp(color * 0.25, heatmap(lightCount), 0.85);

    return float4(color, alpha);
}
