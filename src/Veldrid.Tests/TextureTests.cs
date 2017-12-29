using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Veldrid.Tests
{
    public abstract class TextureTestBase<T> : GraphicsDeviceTestBase<T> where T : GraphicsDeviceCreator
    {
        [Fact]
        public void Map_Succeeds()
        {
            Texture texture = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_G32_B32_A32_Float, TextureUsage.Staging));

            MappedResource map = GD.Map(texture, MapMode.ReadWrite, 0);
            GD.Unmap(texture, 0);
        }

        [Fact]
        public unsafe void Update_ThenMapRead_Succeeds_R32Float()
        {
            Texture texture = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_Float, TextureUsage.Staging));

            float[] data = Enumerable.Range(0, 1024 * 1024).Select(i => (float)i).ToArray();

            fixed (float* dataPtr = data)
            {
                GD.UpdateTexture(texture, (IntPtr)dataPtr, 1024 * 1024 * 4, 0, 0, 0, 1024, 1024, 1, 0, 0);
            }

            MappedResource map = GD.Map(texture, MapMode.Read, 0);
            float* mappedFloatPtr = (float*)map.Data;

            for (int y = 0; y < 1024; y++)
            {
                for (int x = 0; x < 1024; x++)
                {
                    int index = y * 1024 + x;
                    Assert.Equal(index, mappedFloatPtr[index]);
                }
            }
        }

        [Fact]
        public unsafe void Update_ThenMapRead_Succeeds_R16UNorm()
        {
            Texture texture = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

            ushort[] data = Enumerable.Range(0, 1024 * 1024).Select(i => (ushort)i).ToArray();

            fixed (ushort* dataPtr = data)
            {
                GD.UpdateTexture(texture, (IntPtr)dataPtr, 1024 * 1024 * sizeof(ushort), 0, 0, 0, 1024, 1024, 1, 0, 0);
            }

            MappedResource map = GD.Map(texture, MapMode.Read, 0);
            ushort* mappedFloatPtr = (ushort*)map.Data;

            for (int y = 0; y < 1024; y++)
            {
                for (int x = 0; x < 1024; x++)
                {
                    ushort index = (ushort)(y * 1024 + x);
                    Assert.Equal(index, mappedFloatPtr[index]);
                }
            }
        }

        [Fact]
        public unsafe void Update_ThenMapRead_SingleMip_Succeeds_R16UNorm()
        {
            Texture texture = RF.CreateTexture(
                TextureDescription.Texture2D(1024, 1024, 3, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

            ushort[] data = Enumerable.Range(0, 256 * 256).Select(i => (ushort)i).ToArray();

            fixed (ushort* dataPtr = data)
            {
                GD.UpdateTexture(texture, (IntPtr)dataPtr, 256 * 256 * sizeof(ushort), 0, 0, 0, 256, 256, 1, 2, 0);
            }

            MappedResource map = GD.Map(texture, MapMode.Read, 2);
            ushort* mappedFloatPtr = (ushort*)map.Data;

            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    uint mapIndex = (uint)(y * (map.RowPitch / sizeof(ushort)) + x);
                    ushort value = (ushort)(y * 256 + x);
                    Assert.Equal(value, mappedFloatPtr[mapIndex]);
                }
            }
        }

        [Fact]
        public unsafe void Update_ThenMapRead_Mip0_Succeeds_R16UNorm()
        {
            Texture texture = RF.CreateTexture(
                TextureDescription.Texture2D(256, 256, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

            ushort[] data = Enumerable.Range(0, 256 * 256).Select(i => (ushort)i).ToArray();

            fixed (ushort* dataPtr = data)
            {
                GD.UpdateTexture(texture, (IntPtr)dataPtr, 256 * 256 * sizeof(ushort), 0, 0, 0, 256, 256, 1, 0, 0);
            }

            MappedResource map = GD.Map(texture, MapMode.Read, 0);
            ushort* mappedFloatPtr = (ushort*)map.Data;

            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    ushort index = (ushort)(y * 256 + x);
                    Assert.Equal(index, mappedFloatPtr[index]);
                }
            }
        }

        [Fact]
        public unsafe void Update_ThenCopySingleMip_Succeeds_R16UNorm()
        {
            TextureDescription desc = TextureDescription.Texture2D(
                1024, 1024, 3, 1, PixelFormat.R16_UNorm, TextureUsage.Staging);
            Texture src = RF.CreateTexture(desc);
            Texture dst = RF.CreateTexture(desc);

            ushort[] data = Enumerable.Range(0, 256 * 256).Select(i => (ushort)i).ToArray();

            fixed (ushort* dataPtr = data)
            {
                GD.UpdateTexture(src, (IntPtr)dataPtr, 256 * 256 * sizeof(ushort), 0, 0, 0, 256, 256, 1, 2, 0);
            }

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.CopyTexture(src, dst, 2, 0);
            cl.End();
            GD.SubmitCommands(cl);
            GD.WaitForIdle();

            MappedResource map = GD.Map(dst, MapMode.Read, 2);
            ushort* mappedFloatPtr = (ushort*)map.Data;

            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    uint mapIndex = (uint)(y * (map.RowPitch / sizeof(ushort)) + x);
                    ushort value = (ushort)(y * 256 + x);
                    Assert.Equal(value, mappedFloatPtr[mapIndex]);
                }
            }
        }

        [Fact]
        public unsafe void Copy_BC3_Unorm()
        {
            Texture copySrc = RF.CreateTexture(TextureDescription.Texture2D(
                64, 64, 1, 1, PixelFormat.BC3_UNorm, TextureUsage.Staging));
            Texture copyDst = RF.CreateTexture(TextureDescription.Texture2D(
                64, 64, 1, 1, PixelFormat.BC3_UNorm, TextureUsage.Staging));

            uint totalDataSize = copySrc.Width * copySrc.Height;
            byte[] data = new byte[totalDataSize];

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }
            fixed (byte* dataPtr = data)
            {
                GD.UpdateTexture(copySrc, (IntPtr)dataPtr, totalDataSize, 0, 0, 0, copySrc.Width, copySrc.Height, 1, 0, 0);
            }

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.CopyTexture(
                copySrc, 0, 0, 0, 0, 0,
                copyDst, 0, 0, 0, 0, 0,
                copySrc.Width, copySrc.Height, 1, 1);
            cl.End();
            GD.SubmitCommands(cl);
            GD.WaitForIdle();
            MappedResourceView<byte> view = GD.Map<byte>(copyDst, MapMode.Read);
            for (int i = 0; i < data.Length; i++)
            {
                Assert.Equal(view[i], data[i]);
            }
        }

        [Fact]
        public unsafe void Update_ThenMapRead_3D()
        {
            Texture tex3D = RF.CreateTexture(TextureDescription.Texture3D(
                10, 10, 10, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

            RgbaByte[] data = new RgbaByte[tex3D.Width * tex3D.Height * tex3D.Depth];
            for (int z = 0; z < tex3D.Depth; z++)
                for (int y = 0; y < tex3D.Height; y++)
                    for (int x = 0; x < tex3D.Width; x++)
                    {
                        int index = (int)(z * tex3D.Width * tex3D.Height + y * tex3D.Height + x);
                        data[index] = new RgbaByte((byte)x, (byte)y, (byte)z, 1);
                    }

            fixed (RgbaByte* dataPtr = data)
            {
                GD.UpdateTexture(tex3D, (IntPtr)dataPtr, (uint)(data.Length * Unsafe.SizeOf<RgbaByte>()),
                    0, 0, 0,
                    tex3D.Width, tex3D.Height, tex3D.Depth,
                    0, 0);
            }

            MappedResourceView<RgbaByte> view = GD.Map<RgbaByte>(tex3D, MapMode.Read, 0);
            for (int z = 0; z < tex3D.Depth; z++)
                for (int y = 0; y < tex3D.Height; y++)
                    for (int x = 0; x < tex3D.Width; x++)
                    {
                        Assert.Equal(new RgbaByte((byte)x, (byte)y, (byte)z, 1), view[x, y, z]);
                    }

            GD.Unmap(tex3D);
        }

        [Fact]
        public unsafe void MapWrite_ThenMapRead_3D()
        {
            Texture tex3D = RF.CreateTexture(TextureDescription.Texture3D(
                10, 10, 10, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

            MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(tex3D, MapMode.Write);
            for (int z = 0; z < tex3D.Depth; z++)
                for (int y = 0; y < tex3D.Height; y++)
                    for (int x = 0; x < tex3D.Width; x++)
                    {
                        writeView[x, y, z] = new RgbaByte((byte)x, (byte)y, (byte)z, 1);
                    }
            GD.Unmap(tex3D);

            MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(tex3D, MapMode.Read, 0);
            for (int z = 0; z < tex3D.Depth; z++)
                for (int y = 0; y < tex3D.Height; y++)
                    for (int x = 0; x < tex3D.Width; x++)
                    {
                        Assert.Equal(new RgbaByte((byte)x, (byte)y, (byte)z, 1), readView[x, y, z]);
                    }
            GD.Unmap(tex3D);
        }

        [Fact]
        public unsafe void Update_ThenMapRead_1D()
        {
            Texture tex1D = RF.CreateTexture(
                TextureDescription.Texture1D(100, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));
            ushort[] data = Enumerable.Range(0, (int)tex1D.Width).Select(i => (ushort)(i * 2)).ToArray();
            fixed (ushort* dataPtr = &data[0])
            {
                GD.UpdateTexture(tex1D, (IntPtr)dataPtr, (uint)(data.Length * sizeof(ushort)), 0, 0, 0, tex1D.Width, 1, 1, 0, 0);
            }

            MappedResourceView<ushort> view = GD.Map<ushort>(tex1D, MapMode.Read);
            for (int i = 0; i < view.Count; i++)
            {
                Assert.Equal((ushort)(i * 2), view[i]);
            }
            GD.Unmap(tex1D);
        }

        [Fact]
        public unsafe void MapWrite_ThenMapRead_1D()
        {
            Texture tex1D = RF.CreateTexture(
                TextureDescription.Texture1D(100, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

            MappedResourceView<ushort> writeView = GD.Map<ushort>(tex1D, MapMode.Write);
            Assert.Equal(tex1D.Width, (uint)writeView.Count);
            for (int i = 0; i < writeView.Count; i++)
            {
                writeView[i] = (ushort)(i * 2);
            }
            GD.Unmap(tex1D);

            MappedResourceView<ushort> view = GD.Map<ushort>(tex1D, MapMode.Read);
            for (int i = 0; i < view.Count; i++)
            {
                Assert.Equal((ushort)(i * 2), view[i]);
            }
            GD.Unmap(tex1D);
        }

        [Fact]
        public unsafe void Copy_1DTo2D()
        {
            Texture tex1D = RF.CreateTexture(
                TextureDescription.Texture1D(100, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));
            Texture tex2D = RF.CreateTexture(
                TextureDescription.Texture2D(100, 10, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

            MappedResourceView<ushort> writeView = GD.Map<ushort>(tex1D, MapMode.Write);
            Assert.Equal(tex1D.Width, (uint)writeView.Count);
            for (int i = 0; i < writeView.Count; i++)
            {
                writeView[i] = (ushort)(i * 2);
            }
            GD.Unmap(tex1D);

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.CopyTexture(
                tex1D, 0, 0, 0, 0, 0,
                tex2D, 0, 5, 0, 0, 0,
                tex1D.Width, 1, 1, 1);
            cl.End();
            GD.SubmitCommands(cl);
            GD.DisposeWhenIdle(cl);
            GD.WaitForIdle();

            MappedResourceView<ushort> readView = GD.Map<ushort>(tex2D, MapMode.Read);
            for (int i = 0; i < tex2D.Width; i++)
            {
                Assert.Equal((ushort)(i * 2), readView[i, 5]);
            }
            GD.Unmap(tex2D);
        }

        [Fact]
        public void Update_MultipleMips_1D()
        {
            Texture tex1D = RF.CreateTexture(TextureDescription.Texture1D(
                100, 5, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

            for (uint level = 0; level < tex1D.MipLevels; level++)
            {
                MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(tex1D, MapMode.Write, level);
                for (int i = 0; i < writeView.Count; i++)
                {
                    writeView[i] = new RgbaByte((byte)i, (byte)(i * 2), (byte)level, 1);
                }
                GD.Unmap(tex1D, level);
            }

            for (uint level = 0; level < tex1D.MipLevels; level++)
            {
                MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(tex1D, MapMode.Read, level);
                for (int i = 0; i < readView.Count; i++)
                {
                    Assert.Equal(new RgbaByte((byte)i, (byte)(i * 2), (byte)level, 1), readView[i]);
                }
                GD.Unmap(tex1D, level);
            }
        }

        [Fact]
        public void Copy_DifferentMip_1DTo2D()
        {
            Texture tex1D = RF.CreateTexture(
                TextureDescription.Texture1D(200, 2, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));
            Texture tex2D = RF.CreateTexture(
                TextureDescription.Texture2D(100, 10, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

            MappedResourceView<ushort> writeView = GD.Map<ushort>(tex1D, MapMode.Write, 1);
            Assert.Equal(tex2D.Width, (uint)writeView.Count);
            for (int i = 0; i < writeView.Count; i++)
            {
                writeView[i] = (ushort)(i * 2);
            }
            GD.Unmap(tex1D, 1);

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.CopyTexture(
                tex1D, 0, 0, 0, 1, 0,
                tex2D, 0, 5, 0, 0, 0,
                tex2D.Width, 1, 1, 1);
            cl.End();
            GD.SubmitCommands(cl);
            GD.DisposeWhenIdle(cl);
            GD.WaitForIdle();

            MappedResourceView<ushort> readView = GD.Map<ushort>(tex2D, MapMode.Read);
            for (int i = 0; i < tex2D.Width; i++)
            {
                Assert.Equal((ushort)(i * 2), readView[i, 5]);
            }
            GD.Unmap(tex2D);
        }

        [Fact]
        public void Copy_WitOffsets_2D()
        {
            Texture src = RF.CreateTexture(TextureDescription.Texture2D(
                100, 100, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

            Texture dst = RF.CreateTexture(TextureDescription.Texture2D(
                100, 100, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

            MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(src, MapMode.Write);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                {
                    writeView[x, y] = new RgbaByte((byte)x, (byte)y, 0, 1);
                }
            GD.Unmap(src);

            CommandList cl = RF.CreateCommandList();
            cl.Begin();
            cl.CopyTexture(
                src,
                50, 50, 0, 0, 0,
                dst, 10, 10, 0, 0, 0,
                50, 50, 1, 1);
            cl.End();
            GD.SubmitCommands(cl);
            GD.WaitForIdle();

            MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(dst, MapMode.Read);
            for (int y = 10; y < 60; y++)
                for (int x = 10; x < 60; x++)
                {
                    Assert.Equal(new RgbaByte((byte)(x + 40), (byte)(y + 40), 0, 1), readView[x, y]);
                }

            GD.Unmap(dst);
        }
    }

#if TEST_VULKAN
    public class VulkanTextureTests : TextureTestBase<VulkanDeviceCreator> { }
#endif
#if TEST_D3D11
    public class D3D11TextureTests : TextureTestBase<D3D11DeviceCreator> { }
#endif
    public class OpenGLTextureTests : TextureTestBase<OpenGLDeviceCreator> { }
}
