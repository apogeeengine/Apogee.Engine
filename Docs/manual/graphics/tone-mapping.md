# Exposure and tone mapping

The renderer lights the scene in physical units, exposes it like a camera, and then tone maps the exposed image to the screen. This page describes the three steps, the settings that control
them, and the units the lights are in. The code is in `Source/Apogee.Rendering` (`ExposureMath.cs`, `Gt7ToneMapping.cs` and the shaders `forward.slang`, `exposure.slang` and `tonemap.slang`).

```
lights (lux, candela)  →  forward pass: luminance in cd/m²  →  × exposure  →  HDR image (exposed values)
                                                                                    │
                                          luminance histogram → auto-exposure ──────┤  (next frame's exposure)
                                                                                    ▼
                                                          tone mapper (GT7 or PBR Neutral) → sRGB → screen
```

## Units: light is photometric

The forward pass computes **luminance in cd/m²** (nits) from lights in the units the glTF format uses (`KHR_lights_punctual`):

| What | Unit | In the code |
|---|---|---|
| The sun (`Light.Sun`, `RenderScene.SunColor`) | **lux** (illuminance on a surface facing the sun) | `SunColor` is color × lux; a surface facing the sun is lit with that many lux |
| Point and spot lights (`Light.Point`, `Intensity`) | **candela** (luminous intensity) | color × candela, divided by the squared distance: lux on a surface facing the light |
| Ambient (`AmbientSky`, `AmbientGround`) | **cd/m²**: the radiance of the environment | multiplied by the surface's albedo (not by π: a radiance is not an illuminance) |
| Emissive (`MaterialData.EmissiveFactor`, `KHR_materials_emissive_strength`) | **cd/m²** (nits) | added as it is |
| Unlit materials, the sky color, debug lines | **display-referred** colors | not light: the exposure does not change them |

A surface of albedo `a` lit by `E` lux straight on, seen from the front, has the luminance `E · (a / π)` (plus a little specular): 100 000 lux of sun on a white wall of 0.8 is about 25 000 cd/m². The
tests check this: a sun of 2 lux and a point light of 200 cd 10 m above the same ground give the same picture, and its luminance is the one the BRDF gives.

A point light's fall-off is the inverse square with a smooth window that reaches exactly zero at its radius (`Range`, or where it falls below 0.05 lux when it has none, at most 500 m). That radius is a
culling bound and does not depend on the exposure, so a very dark scene, exposed for starlight, can show the edge of a light whose range is left to the default; give such lights an explicit `Range`.

> Content authored before this page (suns of 3, lamps of 80 candela) is far too dim to be real, which is why the default exposure is automatic: whatever the scale of the lights, the image is exposed
> for it. When you author in real units (a sunny day is about 100 000 lux, a lit room 100 to 500 lux, moonlight 0.1 lux) the exposure also tells you what a camera would see.

## Exposure

A camera's exposure is one number, the **EV100** (exposure value at ISO 100). For an aperture f/*N*, a shutter time *t* in seconds and a sensitivity *S*:

```
EV100 = log2(N² / t) − log2(S / 100)
```

One stop of aperture, shutter or ISO is one EV. A sunny day with f/16 at 1/100 s and ISO 100 is EV100 14.6; a lit room is about 5.

The forward pass multiplies the luminance by `1 / (1.2 · 2^EV100)` (the saturation-based exposure of a camera at that EV100) before it writes the HDR image. The HDR image therefore holds
**exposed values**: 1.0 is the luminance that just saturates the sensor, a bright sun does not overflow the half-float image the way raw cd/m² would, and everything after the forward pass (tone mapping, the debug
draw) works on numbers around 0 to 1. A half float tops out at 65 504, and a very bright highlight at a low EV100 can still reach it (at EV100 −6 the exposure is 53), so the forward pass clamps its result to
65 504 instead of letting it become an infinity, which would turn into NaN in later passes; the tone mapper also takes an infinity as 65 504.

`RenderSettings.ExposureMode` chooses the EV100:

| Mode | EV100 | Settings |
|---|---|---|
| `Fixed` | the value you set | `Ev100` |
| `Camera` | from the physical camera | `Aperture`, `ShutterSeconds`, `Iso` |
| `Auto` (default) | measured from the scene every frame, adapted over time | `Ev100` (first frame), `MinEv100`, `MaxEv100`, `AdaptationSpeedBright`, `AdaptationSpeedDark`, `AutoLowPercentile`, `AutoHighPercentile` |

`RenderSettings.Exposure` is an **exposure compensation** as a factor (2 is one stop brighter) on top of any mode.

```csharp
var settings = new RenderSettings
{
    ExposureMode = ExposureMode.Camera,
    Aperture = 8f, ShutterSeconds = 1f / 125f, Iso = 100f,   // EV100 ≈ 13
    Exposure = 1.5f,                                          // and a little brighter
};
```

### Auto-exposure

Two compute passes run after the forward pass, and nothing is read back to the CPU:

1. **Luminance histogram** (`buildHistogram`). Every pixel where something lit was drawn adds one to a bin of a 256-bin histogram of its luminance's log2. Bin 0 counts what is not measured (black, below the range,
   NaN or infinite), bins 1 to 254 divide the range 2⁻¹⁰ to 2¹⁸ cd/m² (0.001 to 262 000, 28 stops, about a ninth of a stop per bin) evenly, and bin 255 holds what is at or above the top. A pixel's luminance is its
   value divided by the exposure the frame was drawn with. What is not light is left out: pixels where nothing was drawn (the sky color), found by their depth, and opaque unlit pixels, which the forward pass writes
   with alpha 0 (an unlit color divided by the exposure would read as a luminance that moves with the exposure, and push it further every frame). A blended unlit surface mixes with what is behind it and is measured
   with it. The group counts in shared memory and adds its counts to the global histogram.
2. **Resolve exposure** (`resolveExposure`). One thread averages the histogram without its darkest 10 % and brightest 10 % of pixels (a few lamps should not darken the image, a dark corner should not brighten it),
   gets the EV100 `log2(L · 100 / 12.5)` that exposes that luminance as a mid-gray (12.5 is the reflected-light meter's calibration constant), clamps it to `MinEv100` and `MaxEv100`, and moves the exposure towards it:
   `ev += (target − ev) · (1 − e^(−speed · dt))`, with `AdaptationSpeedBright` (default 3 per second) when the scene got brighter and `AdaptationSpeedDark` (1) when it got darker, as an eye does. It writes the
   exposure into a small persistent buffer and clears the histogram for the next frame.

The forward pass of the **next** frame reads the exposure from that buffer, on the GPU, so no frame waits for the previous one. The consequences:

- The first frame of auto-exposure, and the first after another mode, is drawn with `Ev100`; the histogram then sets the exposure to the target at once (no crawl from a stale value), and the second frame is right.
- The frame time for the adaptation is measured between calls to `Render` (clamped to a quarter of a second); `RenderSettings.DeltaTime` sets it, which the tests use to be repeatable.
- There is no way yet to snap the adaptation on a camera cut or a scene load while staying in `Auto` (it restarts only after another mode); after a cut the exposure takes a second or two to settle. Not done.
- The state belongs to the `SceneRenderer`: two views drawn by one renderer share an exposure. Give each its own renderer when they should adapt separately.

`ExposureMath` is the CPU twin of all of this (EV100, the scale, a bin, the percentile average, the adaptation), and the tests run the shader passes against it.

## Tone mapping

`RenderSettings.Tonemapper` chooses what turns the exposed image into the display's range. The result is encoded with the sRGB transfer function in the shader (the swapchain is UNORM).

### `Tonemapper.Gt7` (default)

Gran Turismo 7's operator, from Polyphony Digital's sample implementation (`gt7_tone_mapping.cpp`, MIT licence; the notice is in `Legal/THIRD_PARTY_NOTICES.md`), ported to `tonemap.slang` and, as its twin,
to `Gt7ToneMapping.cs`. It works like this:

1. The color goes to **linear Rec.2020** (the renderer's working space is Rec.709, the sRGB primaries) and is scaled to the operator's frame-buffer units, where **1.0 is 100 cd/m²**.
2. Each channel goes through a **curve**: a power-law toe, a linear section (the identity from 0.538 up to 0.444 of the peak) and an exponential shoulder that levels off just above the display's peak.
3. The result is also taken to **ICtCp** (a perceptual color space with the lightness I apart from the chroma Ct and Cp). The curved color's lightness is combined with the *input's* chroma, which is faded
   out as the lightness nears the peak. Per-channel curves alone shift hues (saturated bright colors skew towards yellow or cyan); keeping the chroma fixes the hue, and fading it makes the brightest colors go to white.
4. The two results are blended (60 % of the chroma-preserving one), cut to the peak, and scaled so that the peak is the display's white.

**SDR mode** is the one the renderer uses: the display's white is the operator's **paper white of 250 cd/m²** (2.5 in frame-buffer units), which is where the curve's parameters were tuned. The renderer maps the
exposed value 1.0 (the luminance that saturates the sensor) to that paper white, so a mid-gray at 18 % is shown as a mid-tone and the sensor's saturation just under white. The output is converted back to Rec.709
(colors outside the sRGB gamut are clipped) before the sRGB encoding.

Two small differences from the original, both for the GPU: negative and NaN channels are taken as 0 and an infinity as 65 504 before the operator (it assumes valid input, and an infinity makes NaN in the ICtCp
conversion), and the constants it sets up once for the SDR peak (the shoulder's `kA`, `kB`, `kC` and the lightness of the peak) are literals in the shader, which a test compares with the C# twin's. The C# twin
clamps an HDR peak to the original's 250 to 10 000 cd/m². HDR output (the operator's HDR mode on an HDR10 swapchain) is not implemented. The twin is also tested against the original's own test inputs
(SDR and HDR peaks of 1000, 4000 and 10 000), with the outputs computed from the original's source.

### `Tonemapper.PbrNeutral`

The Khronos PBR Neutral tone mapper, as before. It leaves colors below a compression point alone, so a base color texture shows as it was authored, which suits product views and flat-lit stylized scenes. Its input is the
exposed value as it is.

## Reading the settings together

| I want | Use |
|---|---|
| A scene lit in real units that adapts like an eye | `ExposureMode.Auto` (the default), with `MinEv100`/`MaxEv100` to keep it in a plausible range |
| A scene that stays the same brightness whatever the camera sees | `ExposureMode.Fixed` and an `Ev100` (about 15 for sun, 12 overcast, 6 to 8 indoors) |
| Photographic control | `ExposureMode.Camera` with aperture, shutter and ISO |
| The brightness of the previous renderer for unit-scale content | `Tonemapper.PbrNeutral`, `ExposureMode.Fixed`, and `Ev100 = -log2(1.2)` (an exposure of exactly 1) |

The dev overlay's exposure slider is the compensation (`RenderSettings.Exposure`).

## Tests

- `Gt7ToneMappingTests`: the curve (toe, linear section, continuity, monotonic, the shoulder's asymptote), PQ against its known values, ICtCp round trips, the gamut matrices against the Rec.709 and Rec.2020
  primaries they are derived from, infinities, the shader's literal constants, and the original's outputs for its test inputs.
- `ToneMappingGpuTests`: an image of gradients (gray and colors, 0.002 to 30, the last column infinite) goes through `tonemap.slang` and every pixel is compared with the CPU twin, for GT7 and for Neutral.
- `ExposureMathTests`: EV100, the scale, the bins, the percentile average and the adaptation.
- `ExposureTests`: the histogram pass against the CPU's bins on an image whose size is not a multiple of the group; the exposure pass against `ExposureMath` (first frame, brighter, darker, clamped, cleared); an unlit
  quad covering most of the view not moving the exposure; an emitter of
  known luminance at several EV100s; the physical camera against the same EV100 set by hand; a lux and a candela giving the same picture with the luminance the BRDF predicts; and auto-exposure following a scene
  four stops darker with the adaptation's rate and restarting after another mode.
