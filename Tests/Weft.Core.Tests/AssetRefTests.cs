using System;
using System.Collections.Generic;
using Weft.Core.Assets;
using Xunit;

namespace Weft.Core.Tests;

public class AssetRefTests
{
    private const string Sample = "uid://4k2m9x7q1w3ze";

    [Fact]
    public void AUidRoundTripsThroughItsText()
    {
        var values = new List<ulong> { 1, ulong.MaxValue };
        var random = new Random(1234);
        for (int i = 0; i < 1000; i++)
        {
            ulong value = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
            if (value != 0)
            {
                values.Add(value);
            }
        }

        foreach (ulong value in values)
        {
            var uid = new AssetUid(value);
            Assert.True(AssetUid.TryParse(uid.ToString(), out AssetUid back));
            Assert.Equal(uid, back);
            Assert.Equal(uid, AssetUid.Parse(uid.ToString()));
        }
    }

    [Fact]
    public void UidTextIsThirteenCrockfordCharactersAfterTheScheme()
    {
        Assert.Equal("uid://", AssetUid.Scheme);
        Assert.Equal("uid://0000000000001", new AssetUid(1).ToString());
        Assert.Equal("uid://fzzzzzzzzzzzz", new AssetUid(ulong.MaxValue).ToString());

        for (int i = 0; i < 1000; i++)
        {
            string text = AssetUid.New().ToString();
            Assert.StartsWith("uid://", text);
            Assert.Equal(6 + 13, text.Length);
            Assert.Matches("^uid://[0-9a-hjkmnp-tv-z]{13}$", text);
        }
    }

    [Fact]
    public void UidParsingIgnoresCaseAndRefusesBadText()
    {
        Assert.True(AssetUid.TryParse(Sample, out AssetUid lower));
        Assert.True(AssetUid.TryParse("UID://4K2M9X7Q1W3ZE", out AssetUid shouted));
        Assert.Equal(lower, shouted);
        Assert.True(AssetUid.TryParse("uid://4K2m9X7q1W3zE", out AssetUid mixed));
        Assert.Equal(lower, mixed);

        foreach (string bad in new[]
        {
            "uid://4k2m9x7q1w3zi", "uid://4k2m9x7q1w3zl", "uid://4k2m9x7q1w3zo", "uid://4k2m9x7q1w3zu",
            "uid://4k2m9x7q1w3ze".ToUpperInvariant().Replace("3Z", "3I"),
            "uid://4k2m9x7q1w3z", "uid://4k2m9x7q1w3zeq", "4k2m9x7q1w3ze", "uid:/4k2m9x7q1w3ze", "uid://0000000000000",
            "uid://gzzzzzzzzzzzz", "uid://4k2m9x7q1w3z-", "uid:// 4k2m9x7q1w3ze", "", "uid://",
        })
        {
            Assert.False(AssetUid.TryParse(bad, out AssetUid uid), bad);
            Assert.True(uid.IsNone);
            Assert.Throws<FormatException>(() => AssetUid.Parse(bad));
        }
    }

    [Fact]
    public void NewUidsAreNonZeroAndDistinct()
    {
        var seen = new HashSet<ulong>(100_000);
        for (int i = 0; i < 100_000; i++)
        {
            AssetUid uid = AssetUid.New();
            Assert.False(uid.IsNone);
            Assert.True(seen.Add(uid.Value), "a UID was minted twice");
        }

        Assert.True(AssetUid.None.IsNone);
        Assert.Equal(default, AssetUid.None);
    }

    [Fact]
    public void AnAssetRefHasThreeTextForms()
    {
        AssetUid uid = AssetUid.Parse(Sample);

        AssetRef path = AssetRef.FromPath("res://models/car.glb");
        Assert.Equal("res://models/car.glb", path.ToString());
        Assert.True(path.HasPath);
        Assert.True(path.Uid.IsNone);
        Assert.False(path.IsEmpty);

        var both = new AssetRef("res://models/car.glb", uid);
        Assert.Equal("res://models/car.glb|" + Sample, both.ToString());

        AssetRef only = AssetRef.FromUid(uid);
        Assert.Equal(Sample, only.ToString());
        Assert.False(only.HasPath);
        Assert.False(only.IsEmpty);

        foreach (AssetRef reference in new[] { path, both, only })
        {
            Assert.True(AssetRef.TryParse(reference.ToString(), out AssetRef back));
            Assert.Equal(reference, back);
            Assert.Equal(reference, AssetRef.Parse(reference.ToString()));
        }

        Assert.Equal(uid, AssetRef.Parse("mod://pack/a.png|UID://4K2M9X7Q1W3ZE").Uid);
        Assert.Equal("mod://pack/a.png", AssetRef.Parse("mod://pack/a.png|" + Sample).Path);

        Assert.True(AssetRef.Parse("").IsEmpty);
        Assert.True(default(AssetRef).IsEmpty);
        Assert.Equal("", default(AssetRef).ToString());
        Assert.Throws<ArgumentException>(() => AssetRef.FromPath(""));
        Assert.Throws<ArgumentException>(() => AssetRef.FromPath("a|b"));
        Assert.Throws<ArgumentException>(() => AssetRef.FromPath(Sample));
    }

    [Fact]
    public void AnAssetRefRefusesAnEmptyPathWithASeparator()
    {
        foreach (string bad in new[]
        {
            "|" + Sample, "a|", "a|uid://", "a|nonsense", "a|" + Sample + "|" + Sample, "uid://nonsense",
            Sample + "|" + Sample, "a|0000000000000",
        })
        {
            Assert.False(AssetRef.TryParse(bad, out AssetRef value), bad);
            Assert.True(value.IsEmpty);
            Assert.Throws<FormatException>(() => AssetRef.Parse(bad));
        }
    }
}
