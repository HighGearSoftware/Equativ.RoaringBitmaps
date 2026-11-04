using Xunit;

namespace Equativ.RoaringBitmaps.Tests;

public class RoaringBitmapContainsTests
{
    private static IEnumerable<int> Range(int start, int count) => Enumerable.Range(start, count);

    /// <summary>
    /// Checks every value of the set, its neighbours (which cross container and key boundaries) and the extremes
    /// </summary>
    private static void AssertContainsMatches(ISet<int> expected, RoaringBitmap bitmap)
    {
        var probes = new HashSet<int> { 0, -1, 1, int.MinValue, int.MaxValue, 1 << 16, (1 << 16) - 1 };
        foreach (var value in expected)
        {
            probes.Add(value);
            probes.Add(unchecked(value - 1));
            probes.Add(unchecked(value + 1));
        }
        foreach (var probe in probes)
        {
            Assert.True(expected.Contains(probe) == bitmap.Contains(probe), $"Contains({probe}) should be {expected.Contains(probe)}");
        }
    }

    public static TheoryData<IEnumerable<int>> Values => new()
    {
        Enumerable.Empty<int>(),
        new[] { 0 },
        new[] { int.MaxValue },
        new[] { int.MinValue, -1, -65536, -65537 },
        Range(1000, 200),
        Range(0, Container.MaxSize),
        Range(0, Container.MaxSize + 1),
        Range(1000, 10000),
        Range(0, 1 << 16),
        Range(0, 100).Concat(Range(1 << 16, 10000)).Concat(Range(5 << 16, 3)),
        Range(-5000, 10000),
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Contains_MatchesHashSet(IEnumerable<int> values)
    {
        var expected = new HashSet<int>(values);
        AssertContainsMatches(expected, RoaringBitmap.Create(expected));
    }

    [Fact]
    public void Contains_Random()
    {
        var random = new Random(42);
        var expected = new HashSet<int>();
        for (var i = 0; i < 50000; i++)
        {
            expected.Add(random.Next(0, 1 << 20));
        }
        var bitmap = RoaringBitmap.Create(expected);
        AssertContainsMatches(expected, bitmap);
        for (var i = 0; i < 10000; i++)
        {
            var probe = random.Next();
            Assert.Equal(expected.Contains(probe), bitmap.Contains(probe));
        }
    }

    [Fact]
    public void Contains_AfterInPlaceOperations_IgnoresSpareCapacity()
    {
        // An in-place intersection leaves stale values past the cardinality of the array container
        // and stale keys past the size of the bitmap
        var x = RoaringBitmap.Create(Range(0, 3000).Concat(Range(1 << 16, 10)).Concat(Range(2 << 16, 10000)));
        x.AndInPlace(RoaringBitmap.Create(Range(0, 1000)));
        AssertContainsMatches(new HashSet<int>(Range(0, 1000)), x);
        Assert.False(x.Contains(2000));
        Assert.False(x.Contains(1 << 16));
        Assert.False(x.Contains(2 << 16));

        // An in-place xor shifts values down within the array
        var y = RoaringBitmap.Create(Range(0, 3000));
        y.XorInPlace(RoaringBitmap.Create(Range(0, 1000)));
        AssertContainsMatches(new HashSet<int>(Range(1000, 2000)), y);
    }

    [Fact]
    public void Contains_AfterNot()
    {
        // The negation spans the whole 32-bit range, too large to enumerate: probe around the original values
        var original = new HashSet<int>(Range(0, 100).Concat(Range(1 << 16, 10000)));
        var bitmap = ~RoaringBitmap.Create(original);
        foreach (var value in original)
        {
            Assert.False(bitmap.Contains(value));
        }
        Assert.True(bitmap.Contains(100));
        Assert.True(bitmap.Contains((1 << 16) - 1));
        Assert.True(bitmap.Contains((1 << 16) + 10000));
        Assert.True(bitmap.Contains(int.MaxValue));
        Assert.True(bitmap.Contains(-1));
    }

    [Theory]
    [InlineData("bitmapwithoutruns.bin")]
    [InlineData("bitmapwithruns.bin")]
    public void Contains_AfterDeserialize(string name)
    {
        using var stream = new FileStream(name, FileMode.Open, FileAccess.Read);
        var bitmap = RoaringBitmap.Deserialize(stream);
        AssertContainsMatches(new HashSet<int>(bitmap.ToArray()), bitmap);
        Assert.True(bitmap.Contains(3 * 100000));
        Assert.False(bitmap.Contains(3 * 100000 + 1));
    }
}
