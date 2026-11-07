using System;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Native.Cgltf;
using Xunit;
using CgltfApi = Apogee.Native.Cgltf.Cgltf;

namespace Apogee.Native.Tests;

public unsafe class CgltfSmokeTests
{
    [Fact]
    public void CgltfParsesALoadsItsBufferAndUnpacksPositions()
    {
        NativeRequirements.Require(typeof(CgltfApi).Assembly, "cgltf");

        // One triangle, with its positions in a base64 data URI.
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        string buffer = Convert.ToBase64String(MemoryMarshal.AsBytes<float>(positions));
        byte[] json = Encoding.UTF8.GetBytes($$$"""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"Triangle","mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}],
             "bufferViews":[{"buffer":0,"byteLength":36}],
             "buffers":[{"byteLength":36,"uri":"data:application/octet-stream;base64,{{{buffer}}}"}]}
            """);

        var options = new Options();
        Data* data;

        // cgltf keeps pointers into the memory it parsed (a .glb's binary chunk, the JSON text): it has to stay pinned until Free.
        fixed (byte* text = json)
        {
            Assert.Equal(Result.Success, CgltfApi.Parse(&options, text, (nuint)json.Length, &data));

            try
            {
                Assert.Equal(Result.Success, CgltfApi.LoadBuffers(&options, data, null));
                Assert.Equal(Result.Success, CgltfApi.Validate(data));

                Assert.Equal(1u, (uint)data->NodesCount);
                Assert.Equal("Triangle", Marshal.PtrToStringUTF8((IntPtr)data->Nodes[0].Name));

                Accessor* accessor = data->Meshes[0].Primitives[0].Attributes[0].Data;
                Span<float> unpacked = stackalloc float[9];
                fixed (float* destination = unpacked)
                {
                    Assert.Equal(9u, (uint)CgltfApi.AccessorUnpackFloats(accessor, destination, 9));
                }

                Assert.Equal(positions, unpacked.ToArray());
            }
            finally
            {
                CgltfApi.Free(data);
            }
        }
    }
}
