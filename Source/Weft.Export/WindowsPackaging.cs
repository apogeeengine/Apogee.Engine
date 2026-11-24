using System;
using System.Collections.Generic;
using Weft.Export.Windows;

namespace Weft.Export;

/// <summary>
/// Windows: the executable gets the game's icon (at several sizes) and a version resource made from <c>game.xml</c> (name, organization, version, description, copyright), and becomes a program with
/// windows instead of a console program, unless <see cref="ExportOptions.Console"/> asks to keep the console. This is done by our own PE writer (<see cref="PeResourceWriter"/>), on any host.
/// It runs after the pack key is written into the executable, and the writer moves no section, so every copy of the key stays where it was; the checksum it writes comes last, over the final file.
/// </summary>
internal sealed class WindowsPackaging : IPlatformPackaging
{
    public void Apply(PlatformPackagingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string executable = context.Layout.ExecutablePath;

        var resources = new Dictionary<ResourceKey, byte[]>
        {
            [new ResourceKey(ResourceType.Version, 1, VersionResource.Language)] = VersionResource.Build(context.Manifest),
        };

        // The runtime's own version information is replaced, and so is its icon when the game has one (a game without an icon keeps the runtime's).
        List<int> replacedTypes = [ResourceType.Version];
        if (context.IconFile is { } file)
        {
            IconImages icon = IconImages.Load(file);
            IconResource.Add(resources, icon, VersionResource.Language);
            context.Warnings.AddRange(icon.Warnings);
            replacedTypes.Add(ResourceType.Icon);
            replacedTypes.Add(ResourceType.GroupIcon);
            context.Log($"Wrote the icon ({string.Join(", ", IconResource.Sizes)})");
        }

        PeResourceWriter.Write(executable, resources, replacedTypes, guiSubsystem: !context.Options.Console, context.Warnings);
        context.Log($"Wrote the version information {context.Manifest.Version}" + (context.Options.Console ? "; kept the console" : " and set the windowed subsystem"));
    }
}
