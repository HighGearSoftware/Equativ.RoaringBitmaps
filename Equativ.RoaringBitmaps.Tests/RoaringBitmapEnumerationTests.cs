using Xunit;

namespace Equativ.RoaringBitmaps.Tests;

public class RoaringBitmapEnumerationTests
{
    // An array container and a bitmap container, so that the modification lands both within and across containers
    private static RoaringBitmap CreateBitmap() =>
        RoaringBitmap.Create(Enumerable.Range(0, 100).Concat(Enumerable.Range(1 << 16, 10000)));

    public static TheoryData<string> InPlaceOperations => new()
    {
        nameof(RoaringBitmap.OrInPlace),
        nameof(RoaringBitmap.AndInPlace),
        nameof(RoaringBitmap.XorInPlace),
        nameof(RoaringBitmap.AndNotInPlace),
        nameof(RoaringBitmap.NotInPlace),
    };

    private static void Modify(RoaringBitmap bitmap, string operation)
    {
        var other = RoaringBitmap.Create(5, 1 << 20);
        switch (operation)
        {
            case nameof(RoaringBitmap.OrInPlace): bitmap.OrInPlace(other); break;
            case nameof(RoaringBitmap.AndInPlace): bitmap.AndInPlace(other); break;
            case nameof(RoaringBitmap.XorInPlace): bitmap.XorInPlace(other); break;
            case nameof(RoaringBitmap.AndNotInPlace): bitmap.AndNotInPlace(other); break;
            case nameof(RoaringBitmap.NotInPlace): bitmap.NotInPlace(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    [Theory]
    [MemberData(nameof(InPlaceOperations))]
    public void ModifyingDuringEnumeration_Throws(string operation)
    {
        var bitmap = CreateBitmap();

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in bitmap)
            {
                Modify(bitmap, operation);
            }
        });
    }

    [Theory]
    [MemberData(nameof(InPlaceOperations))]
    public void ModifyingAfterTheLastValue_ThrowsOnTheFinalMoveNext(string operation)
    {
        var bitmap = CreateBitmap();
        using var enumerator = bitmap.GetEnumerator();
        for (var i = 0; i < bitmap.Cardinality; i++)
        {
            Assert.True(enumerator.MoveNext());
        }

        Modify(bitmap, operation);

        Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
    }

    [Fact]
    public void ModifyingWithItselfDuringEnumeration_Throws()
    {
        var bitmap = CreateBitmap();

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in bitmap)
            {
                bitmap.XorInPlace(bitmap);
            }
        });
    }

    [Fact]
    public void ModifyingAClone_DoesNotAffectTheOriginalsEnumeration()
    {
        var bitmap = CreateBitmap();
        var clone = bitmap.Clone();
        var seen = new List<int>();

        foreach (var value in bitmap)
        {
            clone.OrInPlace(RoaringBitmap.Create(value + 1));
            seen.Add(value);
        }

        Assert.Equal(CreateBitmap().ToArray(), seen);
    }

    [Fact]
    public void EnumeratingAfterModification_SeesTheNewValues()
    {
        var bitmap = CreateBitmap();
        foreach (var _ in bitmap)
        {
        }

        bitmap.OrInPlace(RoaringBitmap.Create(1 << 20));

        Assert.Contains(1 << 20, bitmap);
        Assert.Equal(10101, bitmap.Count());
    }
}
