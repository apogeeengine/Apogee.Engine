using System;
using System.Collections.Generic;
using System.IO;

namespace Apogee.Export.Windows;

/// <summary>
/// Changes the resources and the subsystem of a Windows executable, in our own code (no <c>rc.exe</c>, no <c>UpdateResource</c>), on any operating system. The executable's other resources (its
/// manifest above all) are kept as they are.
/// <para>
/// Where the new resource section goes: when the old <c>.rsrc</c> is the last section of the file and holds nothing but resources, it is rewritten where it is. Otherwise (a linker puts
/// <c>.reloc</c> last) a new <c>.rsrc</c> section is added after the last one, and the old one, still in the file, is renamed <c>.rsrc0</c> and left as dead data: nothing points to it any more, and
/// no section moves, so nothing the program or the pack key slot holds changes place. The section table gets one more header, which needs 40 free bytes after it.
/// </para>
/// </summary>
internal static class PeResourceWriter
{
    /// <summary><c>IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ</c>: what a linker gives a resource section.</summary>
    private const uint ResourceSectionFlags = 0x4000_0040;

    /// <summary>Rewrites the executable <paramref name="path"/>; see <see cref="Rewrite"/>.</summary>
    /// <exception cref="ExportException">The file could not be read or written, or is not an executable that can be changed.</exception>
    public static void Write(string path, IReadOnlyDictionary<ResourceKey, byte[]> replace, IReadOnlyCollection<int> removeTypes, bool guiSubsystem, ICollection<string> warnings)
    {
        byte[] original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ExportException($"{path} could not be read: {e.Message}", e);
        }

        byte[] result = Rewrite(original, replace, removeTypes, guiSubsystem, warnings);
        try
        {
            // Through a second file, so that a failure leaves the executable as it was.
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, result);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ExportException($"{path} could not be written: {e.Message}", e);
        }
    }

    /// <summary>
    /// The executable <paramref name="file"/> (which is not changed) with <paramref name="replace"/> set in its resources, all resources of the types in <paramref name="removeTypes"/> removed first,
    /// the signature stripped, the subsystem made GUI if <paramref name="guiSubsystem"/> (a console program becomes a program with windows; one that has none stays), and the checksum right.
    /// </summary>
    /// <param name="file">The bytes of a PE32 or PE32+ executable.</param>
    /// <param name="replace">The resources to write.</param>
    /// <param name="removeTypes">The resource types whose existing resources are dropped, so that no old icon or version is left beside the new one.</param>
    /// <param name="guiSubsystem">Whether to turn a console program into a windowed one.</param>
    /// <param name="warnings">Told if something the person exporting should know happens: a signature was removed.</param>
    /// <exception cref="ExportException">The file is not an executable we can change, or is damaged.</exception>
    public static byte[] Rewrite(byte[] file, IReadOnlyDictionary<ResourceKey, byte[]> replace, IReadOnlyCollection<int> removeTypes, bool guiSubsystem, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(replace);
        ArgumentNullException.ThrowIfNull(removeTypes);
        ArgumentNullException.ThrowIfNull(warnings);

        PeImage image = PeImage.Parse((byte[])file.Clone());
        ResourceTree tree = ResourceTree.Read(image);
        foreach (int type in removeTypes)
        {
            tree.RemoveType(type);
        }

        foreach ((ResourceKey key, byte[] data) in replace)
        {
            tree.Set(key, data);
        }

        if (image.RemoveOverlay())
        {
            warnings.Add("The signature of the executable was removed, because the changes to the file invalidate it: sign the exported game after the export (signtool).");
        }

        PlaceResources(image, tree);
        if (guiSubsystem && image.Subsystem == PeImage.SubsystemConsole)
        {
            image.SetSubsystem(PeImage.SubsystemGui);
        }

        image.UpdateCheckSum();
        return image.Bytes;
    }

    /// <summary>Writes the tree into the file as the resource section (in place or as a new last section) and fixes the headers that describe it.</summary>
    private static void PlaceResources(PeImage image, ResourceTree tree)
    {
        (uint rva, uint directorySize) = image.GetDirectory(PeImage.ResourceDirectory);
        int existing = rva == 0 ? -1 : image.SectionIndexOf(rva);
        if (existing >= 0 && CanRewriteInPlace(image, existing, rva, directorySize))
        {
            RewriteInPlace(image, existing, tree);
        }
        else
        {
            AppendSection(image, existing, tree);
        }
    }

    /// <summary>
    /// Whether the resources are all of the last section, which is also the last in the file: the resource directory starts the section and covers it (so nothing else lives there), and nothing
    /// follows it, so it can grow or shrink without moving anything.
    /// </summary>
    private static bool CanRewriteInPlace(PeImage image, int index, uint rva, uint directorySize)
    {
        PeSection section = image.Sections[index];
        uint size = section.VirtualSize != 0 ? section.VirtualSize : section.SizeOfRawData;
        return index == image.Sections.Count - 1
            && section.SizeOfRawData != 0
            && rva == section.VirtualAddress
            && directorySize >= size
            && section.RawEnd == image.SectionsEnd;
    }

    private static void RewriteInPlace(PeImage image, int index, ResourceTree tree)
    {
        PeSection old = image.Sections[index];
        byte[] data = tree.Serialize(old.VirtualAddress);
        uint rawSize = CheckedUInt(PeImage.AlignUp(data.Length, image.FileAlignment), "The resources are too big.");

        // Cut the file back to the start of the section and grow it by the new raw size: the old contents are gone and the rest is zero padding.
        image.Resize(checked((int)old.PointerToRawData));
        image.Resize(CheckedInt(old.PointerToRawData + (long)rawSize, "The executable would be bigger than 2 GiB."));
        data.CopyTo(image.Bytes.AsSpan((int)old.PointerToRawData));

        image.SetSection(index, old with { VirtualSize = (uint)data.Length, SizeOfRawData = rawSize });
        AdjustInitializedData(image, rawSize - (long)old.SizeOfRawData);
        image.SetDirectory(PeImage.ResourceDirectory, old.VirtualAddress, (uint)data.Length);
        image.SetSizeOfImage(CheckedUInt(PeImage.AlignUp((long)old.VirtualAddress + data.Length, image.SectionAlignment), "The image would be bigger than 4 GiB."));
    }

    private static void AppendSection(PeImage image, int existing, ResourceTree tree)
    {
        long virtualEnd = image.SizeOfImage;
        foreach (PeSection section in image.Sections)
        {
            virtualEnd = Math.Max(virtualEnd, section.VirtualEnd);
        }

        uint virtualAddress = CheckedUInt(PeImage.AlignUp(virtualEnd, image.SectionAlignment), "The image would be bigger than 4 GiB.");
        long pointer = PeImage.AlignUp(image.SectionsEnd, image.FileAlignment);
        byte[] data = tree.Serialize(virtualAddress);
        uint rawSize = CheckedUInt(PeImage.AlignUp(data.Length, image.FileAlignment), "The resources are too big.");

        // The new header first: it fails, before anything is written, when the section table has no room.
        image.SetSection(image.Sections.Count, new PeSection(".rsrc", (uint)data.Length, virtualAddress, rawSize, CheckedUInt(pointer, "The executable would be bigger than 4 GiB."), ResourceSectionFlags));
        image.Resize(CheckedInt(pointer + rawSize, "The executable would be bigger than 2 GiB."));
        data.CopyTo(image.Bytes.AsSpan((int)pointer));

        // Two sections called .rsrc would confuse tools that look for the resources by name; the old one is no longer the resource section.
        if (existing >= 0 && image.Sections[existing].Name == ".rsrc")
        {
            image.SetSection(existing, image.Sections[existing] with { Name = ".rsrc0" });
        }

        AdjustInitializedData(image, rawSize);
        image.SetDirectory(PeImage.ResourceDirectory, virtualAddress, (uint)data.Length);
        image.SetSizeOfImage(CheckedUInt(PeImage.AlignUp((long)virtualAddress + data.Length, image.SectionAlignment), "The image would be bigger than 4 GiB."));
    }

    /// <summary>Adds <paramref name="delta"/> to <c>SizeOfInitializedData</c>, the total raw size of the sections with data.</summary>
    private static void AdjustInitializedData(PeImage image, long delta) => image.SetSizeOfInitializedData((uint)Math.Clamp(image.SizeOfInitializedData + delta, 0, uint.MaxValue));

    private static uint CheckedUInt(long value, string message) => value is >= 0 and <= uint.MaxValue ? (uint)value : throw new ExportException(message);

    private static int CheckedInt(long value, string message) => value is >= 0 and <= int.MaxValue ? (int)value : throw new ExportException(message);
}
