namespace Apogee.Native.Zstd;

public static partial class Zstd
{
    /// <summary>What <see cref="GetFrameContentSize"/> returns when the frame does not say how big its content is (<c>ZSTD_CONTENTSIZE_UNKNOWN</c>, <c>0ULL - 1</c>).</summary>
    public const ulong ContentSizeUnknown = ulong.MaxValue;

    /// <summary>What <see cref="GetFrameContentSize"/> returns for something that is not a zstd frame (<c>ZSTD_CONTENTSIZE_ERROR</c>, <c>0ULL - 2</c>).</summary>
    public const ulong ContentSizeError = ulong.MaxValue - 1;
}
