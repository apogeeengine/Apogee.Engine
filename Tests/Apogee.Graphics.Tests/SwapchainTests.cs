using System;
using System.Numerics;
using Apogee.Native.Tests;
using Apogee.Platform;
using Xunit;
using SDL = Apogee.Native.Sdl.SDL;

namespace Apogee.Graphics.Tests;

/// <summary>Opens a real window, so it only runs with <c>APOGEE_WINDOW_TESTS=1</c> on a machine with a display.</summary>
public sealed unsafe class SwapchainTests
{
    [Fact]
    public void FramesAreRenderedToAWindowThroughResizes()
    {
        if (Environment.GetEnvironmentVariable("APOGEE_WINDOW_TESTS") != "1")
        {
            Assert.Skip("Set APOGEE_WINDOW_TESTS=1 to run the tests that open a window.");
        }

        NativeRequirements.Require(typeof(SDL).Assembly, "SDL3");
        PlatformHost.Initialize();
        try
        {
            using var window = new Window("Apogee swapchain test", 320, 200, WindowOptions.Resizable | GraphicsDevice.RequiredWindowOptions());
            Assert.True(GraphicsDevice.TryCreate(new GraphicsDeviceOptions { Window = window }, out GraphicsDevice? created, out string? error), error);
            using GraphicsDevice device = created!;
            using (Swapchain swapchain = device.CreateSwapchain())
            using (FrameLoop loop = device.CreateFrameLoop(swapchain))
            {
                int rendered = 0;
                void Run(int frames)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        PlatformHost.PumpEvents();
                        Frame? frame = loop.BeginFrame();
                        if (frame is null)
                        {
                            continue;
                        }

                        Backbuffer back = frame.Backbuffer!.Value;
                        var barrier = new ImageBarrier(back.Image, ImageState.Undefined, ImageState.ColorAttachment);
                        frame.Recorder.Barrier(barrier);
                        frame.Recorder.BeginRendering(back.Width, back.Height, [new ColorTarget(back.View, Clear: true, ClearColor: new Vector4(0.1f, 0.2f, 0.4f, 1f))]);
                        frame.Recorder.EndRendering();
                        frame.Recorder.Barrier(barrier with { Before = ImageState.ColorAttachment, After = ImageState.Present });
                        loop.EndFrame(frame);
                        rendered++;
                    }
                }

                Run(20);
                Assert.True(rendered > 0);
                Assert.Equal(window.PixelSize, new PixelSize((int)swapchain.Width, (int)swapchain.Height));

                SDL.SetWindowSize(window.NativeHandle, 400, 300);
                Run(30);
                Assert.Equal(window.PixelSize, new PixelSize((int)swapchain.Width, (int)swapchain.Height));
                Assert.True(rendered > 20);

                loop.WaitIdle();
            }

            Assert.Empty(device.TakeValidationErrors());
        }
        finally
        {
            PlatformHost.Shutdown();
        }
    }
}
