using System;
using System.IO;
using System.Text;

namespace Apogee.Export;

/// <summary>
/// macOS: the export is a bundle, <c>Name.app</c>. This step writes its <c>Contents/Info.plist</c> and <c>PkgInfo</c>, the icon as <c>Contents/Resources/game.icns</c>, marks the executable as executable,
/// and last makes the executable's ad-hoc code signature match the bytes it has now that the pack key is in it (see <see cref="MachOSignature"/>, which also says what is not done: no Developer ID
/// signature, no notarization). The program and its libraries are already in <c>Contents/MacOS</c>, and <c>game.xml</c> and <c>data/</c> in <c>Contents/Resources</c>, put there by the export.
/// </summary>
internal sealed class MacPackaging : IPlatformPackaging
{
    public void Apply(PlatformPackagingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ExportLayout layout = context.Layout;
        if (layout.Bundle is not { } bundle)
        {
            throw new ExportException("The macOS packaging needs the layout of a bundle.");
        }

        ExecutableBit.Set(context, "macOS");

        string contents = Path.Combine(bundle, "Contents");
        Directory.CreateDirectory(layout.GameRoot);

        bool hasIcon = false;
        if (context.IconFile is { } file)
        {
            IconImages icon = IconImages.Load(file);
            File.WriteAllBytes(Path.Combine(layout.GameRoot, InfoPlist.IconFileName), IcnsWriter.Build(icon, context.Warnings));
            context.Warnings.AddRange(icon.Warnings);
            context.Log($"Wrote {InfoPlist.IconFileName}");
            hasIcon = true;
        }

        uint minimum = MachOBuildVersion.Required(MinimumVersionFiles(layout), MachOFile.CpuTypeOf(layout.Target.Arch));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist.Build(context.Manifest, hasIcon, MachOBuildVersion.Format(minimum)), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(contents, "PkgInfo"), InfoPlist.PackageInfo, new UTF8Encoding(false));
        context.Log($"Wrote Info.plist (macOS {MachOBuildVersion.Format(minimum)} or later)");

        // Last, and nothing may change the executable's bytes after it: the pack key is already in.
        if (MachOSignature.Rehash(layout.ExecutablePath))
        {
            context.Log("Made the executable's ad-hoc code signature match the pack key");
        }
        else if (layout.Target.Arch == "arm64")
        {
            context.Warnings.Add($"{Path.GetFileName(layout.ExecutablePath)} has no code signature, and a Mac with Apple silicon does not run a program without one: sign it (codesign --force --sign - <path to the executable>) on a Mac.");
        }
    }

    /// <summary>The Mach-O files whose minimum system version the bundle's is made of: the executable, and every library beside it.</summary>
    private static string[] MinimumVersionFiles(ExportLayout layout) =>
        [layout.ExecutablePath, .. Directory.EnumerateFiles(layout.ExecutableDirectory, "*.dylib", SearchOption.TopDirectoryOnly)];
}
