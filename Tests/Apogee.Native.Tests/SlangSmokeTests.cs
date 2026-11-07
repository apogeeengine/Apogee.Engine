using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Native.Slang;
using Xunit;
using SlangApi = Apogee.Native.Slang.Slang;

namespace Apogee.Native.Tests;

public unsafe class SlangSmokeTests
{
    private const string ComputeShader = """
        RWStructuredBuffer<float> output;

        [shader("compute")]
        [numthreads(64, 1, 1)]
        void doubleIt(uint3 id : SV_DispatchThreadID)
        {
            output[id.x] = id.x * 2.0;
        }
        """;

    private delegate void ResultCheck(Result* result);

    private static byte[] Z(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private static string? Text(byte* text) => Marshal.PtrToStringUTF8((IntPtr)text);

    /// <summary>Compiles <paramref name="source"/> (or, when null, the module named <paramref name="module"/> from the search paths) and hands the result to <paramref name="check"/>.</summary>
    private static void Compile(string module, string? source, string[] searchPaths, ResultCheck check)
    {
        NativeRequirements.Require(typeof(SlangApi).Assembly, "slang_c");

        var paths = new byte[searchPaths.Length][];
        var pins = new GCHandle[searchPaths.Length];
        var pointers = new byte*[searchPaths.Length];
        for (int i = 0; i < searchPaths.Length; i++)
        {
            paths[i] = Z(searchPaths[i]);
            pins[i] = GCHandle.Alloc(paths[i], GCHandleType.Pinned);
            pointers[i] = (byte*)pins[i].AddrOfPinnedObject();
        }

        try
        {
            Session* session;
            fixed (byte** searchPathData = pointers)
            {
                var desc = new SessionDesc { SearchPaths = searchPathData, SearchPathCount = searchPaths.Length, Optimization = 1 };
                Assert.True(SlangApi.SessionCreate(&desc, &session) >= 0);
            }

            try
            {
                byte[] moduleName = Z(module);
                byte[] path = Z(module + ".slang");
                byte[] sourceText = source is null ? [] : Z(source);
                Result* result;
                fixed (byte* moduleNameData = moduleName)
                fixed (byte* pathData = path)
                fixed (byte* sourceData = sourceText)
                {
                    _ = SlangApi.Compile(session, moduleNameData, pathData, source is null ? null : sourceData, null, 0, &result);
                }

                try
                {
                    check(result);
                }
                finally
                {
                    SlangApi.ResultDestroy(result);
                }
            }
            finally
            {
                SlangApi.SessionDestroy(session);
            }
        }
        finally
        {
            foreach (GCHandle pin in pins)
            {
                pin.Free();
            }
        }
    }

    [Fact]
    public void SlangCompilesAComputeShaderToSpirv()
    {
        Compile("smoke", ComputeShader, [], result =>
        {
            Assert.True(SlangApi.ResultStatus(result) >= 0, Text(SlangApi.ResultDiagnostics(result)));
            Assert.Equal(1, SlangApi.ResultEntryPointCount(result));
            Assert.Equal("doubleIt", Text(SlangApi.ResultEntryPointName(result, 0)));

            nuint size;
            byte* spirv = (byte*)SlangApi.ResultCode(result, 0, &size);
            Assert.True(size >= 20 && size % 4 == 0);
            Assert.Equal(0x07230203u, *(uint*)spirv);

            // The reflection says what to bind: the buffer's name and the thread group size.
            string json = Text(SlangApi.ResultReflectionJson(result))!;
            Assert.Contains("\"output\"", json);
            Assert.Contains("doubleIt", json);
        });
    }

    [Fact]
    public void SlangReportsACompileError()
    {
        Compile("bad", "[shader(\"compute\")] [numthreads(1,1,1)] void main( { }", [], result =>
        {
            Assert.True(SlangApi.ResultStatus(result) < 0);
            Assert.Equal(0, SlangApi.ResultEntryPointCount(result));

            string diagnostics = Text(SlangApi.ResultDiagnostics(result))!;
            Assert.Contains("error", diagnostics);
            Assert.Contains("bad.slang", diagnostics);
        });
    }

    [Fact]
    public void SlangFindsImportsOnTheSearchPathAndReportsThemAsDependencies()
    {
        string directory = Directory.CreateTempSubdirectory("apogee-slang").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "scale.slang"), "public float scale(float x) { return x * 3.0; }");
            File.WriteAllText(Path.Combine(directory, "main.slang"), """
                import scale;
                RWStructuredBuffer<float> output;
                [shader("compute")] [numthreads(1, 1, 1)]
                void run(uint3 id : SV_DispatchThreadID) { output[id.x] = scale(1.0); }
                """);

            // Loaded by name from the search path, not from a string.
            Compile("main", null, [directory], result =>
            {
                Assert.True(SlangApi.ResultStatus(result) >= 0, Text(SlangApi.ResultDiagnostics(result)));

                var dependencies = new System.Collections.Generic.List<string>();
                for (int i = 0; i < SlangApi.ResultDependencyCount(result); i++)
                {
                    dependencies.Add(Text(SlangApi.ResultDependency(result, i))!);
                }

                Assert.Contains(dependencies, d => d.EndsWith("main.slang", StringComparison.Ordinal));
                Assert.Contains(dependencies, d => d.EndsWith("scale.slang", StringComparison.Ordinal));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
