// Auto-exposure: a histogram of the scene's log luminance, built from the HDR image, and the exposure it leads to, adapted over time. Both stay on the GPU: the exposure goes in a
// persistent buffer (FrameData::exposureState) that the next frame's forward pass reads, so nothing is read back. Apogee.Rendering/ExposureMath.cs is the twin on the CPU.
//
// The HDR image holds exposed values (luminance * the exposure of the frame), so a pixel's luminance in cd/m2 is its value divided by the exposure that frame was drawn with.
#include "common.slang"

struct ExposurePush
{
    FrameData* frame;
    uint* histogram;          // 256 counters: bin 0 the pixels too dark to measure, 1 to 255 along the log2 luminance range
    uint texture;             // the HDR image
    uint depth;               // the depth image: pixels where nothing was drawn (reverse-Z depth 0) are the sky, a display-referred color and not light, and are not measured
    uint width;
    uint height;
    uint reset;               // 1: the exposure starts at the target (the first frame of auto-exposure), instead of moving towards it
    float minLog2Luminance;
    float log2Range;          // the range's size in stops (max - min)
    float lowPercentile;      // the darkest and brightest pixels left out of the average
    float highPercentile;
    float minEv100;
    float maxEv100;
    float speedBright;        // per second, when the scene got brighter
    float speedDark;          // per second, when it got darker
    float deltaTime;
    float unused0;
    float unused1;
};
[[vk::push_constant]] ConstantBuffer<ExposurePush> push PUSH_SLOT;

static const uint HISTOGRAM_BINS = 256;
static const float SATURATION_FACTOR = 1.2;       // ExposureMath.SaturationFactor
static const float METER_CALIBRATION = 12.5;      // ExposureMath.MeterCalibration

// The bin of a luminance: 0 for black, NaN or below the range, else 1 to 255 along the range (ExposureMath.HistogramBin).
uint histogramBin(float luminance)
{
    if (!(luminance > 0.0))
        return 0;

    float log2Luminance = log2(min(luminance, 3.0e38));
    if (log2Luminance < push.minLog2Luminance)
        return 0;

    float t = (log2Luminance - push.minLog2Luminance) / push.log2Range;
    return uint(clamp(t * float(HISTOGRAM_BINS - 2) + 1.0, 1.0, float(HISTOGRAM_BINS - 1)));
}

// The log2 luminance at the center of a bin (ExposureMath.BinLog2Luminance).
float binLog2Luminance(uint bin)
{
    return push.minLog2Luminance + (float(bin) - 0.5) / float(HISTOGRAM_BINS - 2) * push.log2Range;
}

groupshared uint gBins[HISTOGRAM_BINS];

// One thread per pixel, 16 x 16 per group: the group counts its pixels in groupshared memory, then adds its counts to the global histogram.
[shader("compute")] [numthreads(16, 16, 1)]
void buildHistogram(uint3 id : SV_DispatchThreadID, uint index : SV_GroupIndex)
{
    gBins[index] = 0;
    GroupMemoryBarrierWithGroupSync();

    if (id.x < push.width && id.y < push.height && gTextures[push.depth].Load(int3(int2(id.xy), 0)).x > 0.0)
    {
        float3 color = gTextures[push.texture].Load(int3(int2(id.xy), 0)).rgb;
        float luminance = dot(color, float3(0.2126, 0.7152, 0.0722)) / sceneExposure(push.frame);
        uint bin = histogramBin(luminance);
        InterlockedAdd(gBins[bin], 1);
    }

    GroupMemoryBarrierWithGroupSync();

    uint count = gBins[index];
    if (count != 0)
        InterlockedAdd(push.histogram[index], count);
}

groupshared uint gCounts[HISTOGRAM_BINS];

// One group of 256 threads: thread 0 averages the histogram (the part between the percentiles) and moves the exposure towards what it calls for; every thread clears its counter for the next frame.
[shader("compute")] [numthreads(256, 1, 1)]
void resolveExposure(uint index : SV_GroupIndex)
{
    gCounts[index] = push.histogram[index];
    push.histogram[index] = 0;
    GroupMemoryBarrierWithGroupSync();

    if (index != 0)
        return;

    // ExposureMath.AverageLog2Luminance: the window [from, to) of the measured pixels, in order, and the average of their bins' centers.
    float total = 0.0;
    for (uint i = 1; i < HISTOGRAM_BINS; i++)
        total += float(gCounts[i]);

    float averageLog2 = push.minLog2Luminance;
    if (total > 0.0)
    {
        float from = total * push.lowPercentile;
        float to = total * push.highPercentile;
        float sum = 0.0;
        float weight = 0.0;
        float seen = 0.0;
        for (uint i = 1; i < HISTOGRAM_BINS; i++)
        {
            float start = seen;
            seen += float(gCounts[i]);
            float inside = max(0.0, min(seen, to) - max(start, from));
            sum += inside * binLog2Luminance(i);
            weight += inside;
        }

        // A window that holds nothing falls back to the plain average of everything measured.
        if (weight <= 0.0)
        {
            for (uint i = 1; i < HISTOGRAM_BINS; i++)
            {
                sum += float(gCounts[i]) * binLog2Luminance(i);
                weight += float(gCounts[i]);
            }
        }

        averageLog2 = sum / weight;
    }

    // ExposureMath.TargetEv100 and Adapt.
    float target = clamp(log2(exp2(averageLog2) * 100.0 / METER_CALIBRATION), push.minEv100, push.maxEv100);
    float4 state = push.frame->exposureState[0];
    float ev = target;
    if (push.reset == 0)
    {
        float speed = target > state.y ? push.speedBright : push.speedDark;
        ev = state.y + (target - state.y) * (1.0 - exp(-push.deltaTime * speed));
    }

    push.frame->exposureState[0] = float4(1.0 / (SATURATION_FACTOR * exp2(ev)), ev, exp2(averageLog2), 0.0);
}
