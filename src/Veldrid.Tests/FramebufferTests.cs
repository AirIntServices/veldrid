﻿using Xunit;

namespace Veldrid.Tests
{
    public abstract class FramebufferTests<T> : GraphicsDeviceTestBase<T> where T : GraphicsDeviceCreator
    {
        [Fact]
        public void NoDepthTarget_ClearAllColors_Succeeds()
        {
            Texture colorTarget = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_G32_B32_A32_Float, TextureUsage.RenderTarget));
            Framebuffer fb = RF.CreateFramebuffer(new FramebufferDescription(null, colorTarget));

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, RgbaFloat.Red);
            cl.End();
            GD.SubmitCommands(cl);
            GD.WaitForIdle();

            Texture staging = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_G32_B32_A32_Float, TextureUsage.Staging));

            cl.Begin();
            cl.CopyTexture(
                colorTarget, 0, 0, 0, 0, 0,
                staging, 0, 0, 0, 0, 0,
                1024, 1024, 1, 1);
            cl.End();
            GD.SubmitCommands(cl);
            GD.WaitForIdle();

            MappedResourceView<RgbaFloat> view = GD.Map<RgbaFloat>(staging, MapMode.Read);
            for (int i = 0; i < view.Count; i++)
            {
                Assert.Equal(RgbaFloat.Red, view[i]);
            }
            GD.Unmap(staging);
        }

        [Fact]
        public void NoDepthTarget_ClearDepth_Fails()
        {
            Texture colorTarget = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_G32_B32_A32_Float, TextureUsage.RenderTarget));
            Framebuffer fb = RF.CreateFramebuffer(new FramebufferDescription(null, colorTarget));

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(fb);
            Assert.Throws<VeldridException>(() => cl.ClearDepthStencil(1f));
        }

        [Fact]
        public void NoColorTarget_ClearColor_Fails()
        {
            Texture depthTarget = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R16_UNorm, TextureUsage.DepthStencil));
            Framebuffer fb = RF.CreateFramebuffer(new FramebufferDescription(depthTarget));

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(fb);
            Assert.Throws<VeldridException>(() => cl.ClearColorTarget(0, RgbaFloat.Red));
        }

        [Fact]
        public void ClearColorTarget_OutOfRange_Fails()
        {
            TextureDescription desc = TextureDescription.Texture2D(
                1024, 1024, 1, 1, PixelFormat.R32_G32_B32_A32_Float, TextureUsage.RenderTarget);
            Texture colorTarget0 = RF.CreateTexture(desc);
            Texture colorTarget1 = RF.CreateTexture(desc);
            Framebuffer fb = RF.CreateFramebuffer(new FramebufferDescription(null, colorTarget0, colorTarget1));

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, RgbaFloat.Red);
            cl.ClearColorTarget(1, RgbaFloat.Red);
            Assert.Throws<VeldridException>(() => cl.ClearColorTarget(2, RgbaFloat.Red));
            Assert.Throws<VeldridException>(() => cl.ClearColorTarget(3, RgbaFloat.Red));
        }
    }

    public abstract class SwapchainFramebufferTests<T> : GraphicsDeviceTestBase<T> where T : GraphicsDeviceCreator
    {
        [Fact]
        public void ClearSwapchainFramebuffer_Succeeds()
        {
            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(GD.SwapchainFramebuffer);
            cl.ClearColorTarget(0, RgbaFloat.Red);
            cl.ClearDepthStencil(1f);
            cl.End();
        }
    }

    public class OpenGLFramebufferTests : FramebufferTests<OpenGLDeviceCreator> { }
    public class OpenGLSwapchainFramebufferTests : SwapchainFramebufferTests<OpenGLDeviceCreator> { }
#if TEST_VULKAN
    public class VulkanFramebufferTests : FramebufferTests<VulkanDeviceCreator> { }
    public class VulkanSwapchainFramebufferTests : SwapchainFramebufferTests<VulkanDeviceCreatorWithMainSwapchain> { }
#endif
#if TEST_D3D11
    public class D3D11FramebufferTests : FramebufferTests<D3D11DeviceCreator> { }
    public class D3D11SwapchainFramebufferTests : SwapchainFramebufferTests<D3D11DeviceCreatorWithMainSwapchain> { }
#endif
#if TEST_METAL
    public class MetalFramebufferTests : FramebufferTests<MetalDeviceCreator> { }
    public class MetalSwapchainFramebufferTests : SwapchainFramebufferTests<MetalDeviceCreatorWithMainSwapchain> { }
#endif
}
