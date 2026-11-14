namespace Apogee.Export;

/// <summary>The packaging for each platform.</summary>
internal static class PlatformPackaging
{
    public static IPlatformPackaging For(ExportTarget target) => target.Os switch
    {
        "win" => new WindowsPackaging(),
        "osx" => new MacPackaging(),
        _ => new LinuxPackaging(),
    };
}
