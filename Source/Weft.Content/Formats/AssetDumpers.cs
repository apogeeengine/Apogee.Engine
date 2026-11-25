using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Weft.Content.Assets;
using Weft.Content.Xml;

namespace Weft.Content.Formats;

/// <summary>Writes the <c>META</c> of a file of one format as text, <c>name = value</c> on a line each (the container's own part of the dump is written before it).</summary>
/// <param name="asset">The file; <see cref="BinaryAsset.Meta"/> and the blocks are read from it.</param>
/// <param name="output">Where the lines go.</param>
public delegate void AssetDumper(BinaryAsset asset, TextWriter output);

/// <summary>
/// What <c>Weft.Runtime --dump &lt;file&gt;</c> writes: a binary artifact as text. A file on the common container (<see cref="BinaryAsset"/>) is dumped as its header, its UID, its import hash and its block table
/// (offsets, sizes, CRCs and whether each CRC holds), then its <c>META</c> by the dumper registered for its magic with <see cref="Register"/>; cooked XML is dumped as XML text. A format that lands on the
/// container registers its dumper here, so that every artifact has a dump. Safe to use from several threads.
/// </summary>
public static class AssetDumpers
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<uint, AssetDumper> s_dumpers = [];

    /// <summary>Registers the dumper of the files whose magic is <paramref name="magic"/> (<see cref="BinaryAsset.Fourcc"/>).</summary>
    /// <exception cref="InvalidOperationException">The magic has a dumper already.</exception>
    public static void Register(uint magic, AssetDumper dumper)
    {
        ArgumentNullException.ThrowIfNull(dumper);
        lock (s_lock)
        {
            if (!s_dumpers.TryAdd(magic, dumper))
            {
                throw new InvalidOperationException($"'{BinaryAsset.FourccText(magic)}' has a dumper already.");
            }
        }
    }

    /// <summary>Whether there is a dumper for <paramref name="magic"/>.</summary>
    public static bool IsRegistered(uint magic)
    {
        lock (s_lock)
        {
            return s_dumpers.ContainsKey(magic);
        }
    }

    /// <summary>Writes the text of <paramref name="file"/> to <paramref name="output"/>.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="name">What it is called in the text and in messages.</param>
    /// <exception cref="ContentException">It is neither cooked XML nor a container, or it is damaged so that it cannot be listed.</exception>
    public static void Dump(ReadOnlyMemory<byte> file, string name, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(output);
        if (XmlBinary.IsBinary(file.Span))
        {
            DumpCookedXml(XmlBinary.Read(file.Span, name), name, output);
            return;
        }

        BinaryAsset asset = BinaryAsset.Read(file, name);
        DumpContainer(asset, output);
        AssetDumper? dumper;
        lock (s_lock)
        {
            s_dumpers.TryGetValue(asset.Magic, out dumper);
        }

        output.WriteLine();
        if (dumper is null)
        {
            output.WriteLine($"META: {asset.Meta.Length} bytes; no dumper for '{asset.MagicName}'");
            return;
        }

        output.WriteLine("META:");
        try
        {
            dumper(asset, output);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A dumper reads a META that is only known to be the bytes the writer checksummed: it may be nonsense to the format.
            output.WriteLine($"the dumper of '{asset.MagicName}' failed: {e.GetType().Name}: {Clean(e.Message)}");
        }
    }

    /// <summary>The most characters of XML a cooked file is dumped as (a cooked file shares its strings, so a small file can name one text a million times).</summary>
    public const int MaxXmlChars = 16 * 1024 * 1024;

    /// <summary>The largest block with a codec that <c>--dump</c> decodes to check its CRC; a larger one is listed as unchecked.</summary>
    public const long MaxCheckedCodecBlockBytes = 64L * 1024 * 1024;

    private static void DumpCookedXml(XmlNode root, string name, TextWriter output)
    {
        long budget = MaxXmlChars;
        WriteXml(root, 0, output, ref budget, name);
    }

    private static void WriteXml(XmlNode node, int depth, TextWriter output, ref long budget, string name)
    {
        Spend(ref budget, (depth * 2) + node.Name.Length + 8, name);
        output.Write(new string(' ', depth * 2));
        output.Write('<');
        output.Write(Clean(node.Name));
        foreach (XmlProperty attribute in node.Attributes)
        {
            Spend(ref budget, attribute.Name.Length + attribute.Value.Length + 4, name);
            output.Write(' ');
            output.Write(Clean(attribute.Name));
            output.Write("=\"");
            output.Write(Escape(attribute.Value));
            output.Write('"');
        }

        if (node.Children.Count == 0 && node.Text.Length == 0)
        {
            output.WriteLine("/>");
            return;
        }

        output.Write('>');
        if (node.Text.Length > 0)
        {
            Spend(ref budget, node.Text.Length, name);
            output.Write(Escape(node.Text));
        }

        if (node.Children.Count == 0)
        {
            output.WriteLine($"</{Clean(node.Name)}>");
            return;
        }

        output.WriteLine();
        foreach (XmlNode child in node.Children)
        {
            WriteXml(child, depth + 1, output, ref budget, name);
        }

        Spend(ref budget, (depth * 2) + node.Name.Length + 4, name);
        output.WriteLine(new string(' ', depth * 2) + "</" + Clean(node.Name) + ">");
    }

    private static void Spend(ref long budget, long chars, string name)
    {
        budget -= chars;
        if (budget < 0)
        {
            throw new ContentException($"'{name}' would be dumped as more than {MaxXmlChars} characters of XML; it is cut here.");
        }
    }

    /// <summary>The text with &amp;, &lt;, &gt; and quotes as entities and every control character as a numeric one, so that a file cannot write escape sequences to the terminal.</summary>
    private static string Escape(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    text.Append("&amp;");
                    break;
                case '<':
                    text.Append("&lt;");
                    break;
                case '>':
                    text.Append("&gt;");
                    break;
                case '"':
                    text.Append("&quot;");
                    break;
                case '\n':
                    text.Append('\n');
                    break;
                default:
                    text.Append(char.IsControl(c) ? "&#x" + ((int)c).ToString("X", CultureInfo.InvariantCulture) + ";" : c.ToString());
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>A name or message with every control character replaced by a question mark.</summary>
    private static string Clean(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            text.Append(char.IsControl(c) ? '?' : c);
        }

        return text.ToString();
    }

    private static void DumpContainer(BinaryAsset asset, TextWriter output)
    {
        Span<byte> magic = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(magic, asset.Magic);
        AssetFormat? format = AssetFormats.FindMagic(magic);
        output.WriteLine($"file:           {asset.Name} ({asset.FileLength} bytes)");
        output.WriteLine($"format:         {asset.MagicName}{(format is null ? "" : " (" + format.Label + ")")}, version {asset.FormatVersion}, container version {BinaryAsset.ContainerVersion}");
        output.WriteLine($"flags:          0x{asset.Flags:x8}");
        output.WriteLine($"uid:            {(asset.Uid.IsNone ? "none" : asset.Uid.ToString())}");
        output.WriteLine($"import hash:    {asset.ImportHashHex()}");
        output.WriteLine($"blocks:         {asset.Blocks.Count}");
        output.WriteLine("  #  kind  codec          align  offset      stored      raw         crc32c    stride  check");
        for (int i = 0; i < asset.Blocks.Count; i++)
        {
            BinaryBlock block = asset.Blocks[i];
            string check;
            if (block.Codec != BlockCodec.None && block.RawSize > MaxCheckedCodecBlockBytes)
            {
                check = $"not checked (more than {MaxCheckedCodecBlockBytes / (1024 * 1024)} MiB to decode)";
            }
            else
            {
                try
                {
                    asset.ReadBlock(i);
                    check = "ok";
                }
                catch (ContentException e)
                {
                    check = "BAD: " + Clean(e.Message);
                }
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i,-2} {block.KindName}  {block.Codec,-13}  {1 << block.AlignmentLog2,-5}  {block.Offset,-10}  {block.StoredSize,-10}  {block.RawSize,-10}  {block.Crc:x8}  {block.Stride,-6}  {check}"));
        }
    }
}
