using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Editor.Editing;
using Apogee.Editor.Panels;
using Apogee.Graphics;
using Apogee.Rendering.Graph;
using Apogee.Rendering.Shaders;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>One editor frame on a GPU: the scene is drawn into the viewport image with the helpers, and the render graph of that frame lays out as nodes. Skips with no GPU.</summary>
[Collection("editor")]
public sealed class ViewportTests
{
    [Fact]
    public void TheViewportDrawsASceneIntoATextureAndTheRenderGraphLaysOutAsNodes()
    {
        if (!GraphicsDevice.TryCreate(new GraphicsDeviceOptions { ApplicationName = "Apogee editor tests" }, out GraphicsDevice? device, out string? error))
        {
            if (Environment.GetEnvironmentVariable("APOGEE_REQUIRE_VULKAN") == "1")
            {
                Assert.Fail($"A GPU is required here: {error}");
            }

            Assert.Skip(error ?? "no GPU");
        }

        using (device)
        {
            device!.TakeValidationErrors();
            using var rig = new ProjectRig();
            EditorProject project = rig.Project;
            project.CreatePointLight(default, new Double3(0, 3, 0));
            project.CreateCamera();
            project.World.Progress(0f);

            ShaderLibrary shaders = ShaderLibrary.Standard(Path.Combine(Path.GetTempPath(), "apogee-test-shader-cache"));
            using FrameLoop loop = device.CreateFrameLoop();
            var viewport = new ViewportRenderer(device, shaders, project.Session, loop.FramesInFlight, playing: false, () => false, () => false);
            try
            {
                var camera = new EditorCamera();
                for (int i = 0; i < 3; i++)
                {
                    Frame frame = loop.BeginFrame()!;
                    uint index = viewport.Acquire(frame, 320, 200);
                    Assert.NotEqual(0u, index);
                    EditorHelpers.Draw(project.World, project.Session.Models, project.Session.DebugDraw, project.Selection, camera.Target);
                    Assert.False(project.Session.DebugDraw.IsEmpty);
                    viewport.Render(frame, camera, playing: false, preferGameCamera: true, density: 1f);
                    loop.EndFrame(frame);
                    project.Session.EndFrame();
                }

                loop.WaitIdle();
                Assert.Equal((320u, 200u), viewport.Size);

                // The graph of that frame as nodes: the depth prepass feeds the forward pass, and the tone map comes last.
                (var nodes, var links) = RenderGraphLayout.Build(viewport.Renderer.LastPlan!);
                Assert.Contains(nodes, n => n.Name == "Forward");
                GraphNode depth = nodes.First(n => n.Name == "Depth prepass");
                GraphNode forward = nodes.First(n => n.Name == "Forward");
                GraphNode tonemap = nodes.First(n => n.Name == "Tonemap");
                Assert.True(forward.Column > depth.Column);
                Assert.True(tonemap.Column > forward.Column);
                Assert.Contains(links, l => depth.Outputs.Any(p => p.Id == l.FromPin) && forward.Inputs.Any(p => p.Id == l.ToPin));
                Assert.Equal(nodes.SelectMany(n => n.Inputs.Concat(n.Outputs)).Count(), nodes.SelectMany(n => n.Inputs.Concat(n.Outputs)).Select(p => p.Id).Distinct().Count());   // pin ids are unique
            }
            finally
            {
                viewport.Dispose();
            }

            Assert.Empty(device.TakeValidationErrors());
        }
    }
}
