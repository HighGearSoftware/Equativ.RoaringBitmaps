using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Equativ.RoaringBitmaps;

/// <summary>
/// A compressed bitmap of 32-bit integers.
/// The operators (|, &amp;, ^, ~) and <see cref="AndNot"/> leave their operands untouched and return a new bitmap.
/// The *InPlace methods modify this bitmap instead, which avoids most of the allocations. Bitmaps may share
/// storage behind the scenes (copy on write), so modifying one bitmap in place never affects another one.
/// A bitmap can be read from several threads at once, but an in-place operation must not run concurrently
/// with any other access to the same bitmap. Enumeration is lazy, and modifying a bitmap in place while it is
/// being enumerated makes the enumerator throw InvalidOperationException, like List&lt;T&gt;.
/// </summary>
public class RoaringBitmap : IEnumerable<int>, IEquatable<RoaringBitmap>
{
    private readonly RoaringArray _highLowContainer;

    private RoaringBitmap(RoaringArray input)
    {
        _highLowContainer = input;
    }

    public long Cardinality => _highLowContainer.Cardinality;

    /// <summary>
    /// Checks whether a value is in the bitmap, without enumerating it
    /// </summary>
    /// <param name="value">Integer</param>
    /// <returns>True if the bitmap contains the value</returns>
    public bool Contains(int value)
    {
        return _highLowContainer.Contains(value);
    }

    public IEnumerator<int> GetEnumerator()
    {
        return _highLowContainer.AsEnumerable().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
    
    /// <summary>
    /// Convert the bitmap to an array of integers
    /// </summary>
    /// <returns>Array of integers</returns>
    public List<int> ToArray()
    {
        var list = new List<int>((int)Cardinality);
        foreach (var item in _highLowContainer.AsEnumerable())
        {
            list.Add(item);
        }
        return list;
    }
    
    public bool Equals(RoaringBitmap? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (ReferenceEquals(null, other))
        {
            return false;
        }
        return _highLowContainer.Equals(other._highLowContainer);
    }
    
    public override bool Equals(object? obj)
    {
        var ra = obj as RoaringBitmap;
        return ra != null && Equals(ra);
    }

    public override int GetHashCode()
    {
        return (13 ^ _highLowContainer.GetHashCode()) << 3;
    }

    /// <summary>
    /// Creates a new RoaringBitmap from an existing list of integers
    /// </summary>
    /// <param name="values">List of integers</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap Create(params int[] values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        return Create((ReadOnlySpan<int>) values);
    }

    /// <summary>
    /// Optimizes a RoaringBitmap to prepare e.g. for Serialization/Deserialization
    /// </summary>
    /// <returns>RoaringBitmap</returns>
    public RoaringBitmap Optimize()
    {
        return new RoaringBitmap(RoaringArray.Optimize(_highLowContainer));
    }

    /// <summary>
    /// Creates a copy of this bitmap that can be modified in place independently of the original.
    /// This is cheap: the storage is shared until one side modifies it.
    /// </summary>
    /// <returns>RoaringBitmap</returns>
    public RoaringBitmap Clone()
    {
        return new RoaringBitmap(_highLowContainer.Clone());
    }

    /// <summary>
    /// Creates a new RoaringBitmap from an existing list of integers, which is not mutated.
    /// </summary>
    /// <param name="values">List of integers</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap Create(IEnumerable<int> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (values is int[] array)
        {
            return Create((ReadOnlySpan<int>) array);
        }
        var chunk = ArrayPool<int>.Shared.Rent(CreateChunkSize);
        try
        {
            RoaringArray? result = null;
            var count = 0;
            foreach (var value in values)
            {
                chunk[count++] = value;
                if (count == CreateChunkSize)
                {
                    result = AddChunk(result, chunk, count);
                    count = 0;
                }
            }
            return new RoaringBitmap(AddChunk(result, chunk, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Creates a new RoaringBitmap from a span of integers, which is left untouched
    /// </summary>
    /// <param name="values">Span of integers</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap Create(ReadOnlySpan<int> values)
    {
        var chunkSize = Math.Min(values.Length, CreateChunkSize);
        var chunk = ArrayPool<int>.Shared.Rent(chunkSize);
        try
        {
            RoaringArray? result = null;
            for (var start = 0; start < values.Length; start += chunkSize)
            {
                var count = Math.Min(chunkSize, values.Length - start);
                values.Slice(start, count).CopyTo(chunk);
                result = AddChunk(result, chunk, count);
            }
            return new RoaringBitmap(result ?? FromChunk(chunk, 0));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(chunk);
        }
    }

    // Values are copied into pooled chunks of this many integers to be sorted. It is a power of two, so that the pool
    // hands out arrays of exactly this size: 64 KB, which stays below the large object heap threshold.
    private const int CreateChunkSize = 16384;

    private static RoaringArray AddChunk(RoaringArray? result, int[] chunk, int count)
    {
        var chunkArray = FromChunk(chunk, count);
        if (result is null)
        {
            return chunkArray;
        }
        result.OrInPlace(chunkArray);
        return result;
    }

    /// <summary>
    /// Creates a RoaringArray from the first count integers of chunk, which are reordered in the process
    /// </summary>
    private static RoaringArray FromChunk(int[] chunk, int count)
    {
        if (count == 0)
        {
            return new RoaringArray(0, new List<ushort>(), new List<Container>());
        }

        // Container keys are the high 16 bits of each value, and they must be in ascending order.
        // Negative values have the highest keys (their sign bit is set), so we sort the data as unsigned
        // integers to keep the keys in ascending order. After we flip the sign bit, a signed sort puts the
        // values in unsigned order, so we flip it, sort, and flip it back.
        for (var i = 0; i < count; i++)
        {
            chunk[i] ^= int.MinValue;
        }
        Array.Sort(chunk, 0, count);
        for (var i = 0; i < count; i++)
        {
            chunk[i] ^= int.MinValue;
        }

        // In-place deduplication (two pointers technique)
        var uniqueCount = 1;
        for (var i = 1; i < count; i++)
        {
            if (chunk[i] != chunk[uniqueCount - 1])
            {
                chunk[uniqueCount++] = chunk[i];
            }
        }

        var keys = new List<ushort>();
        var containers = new List<Container>();
        var index = 0;

        while (index < uniqueCount)
        {
            var hb = Utils.HighBits(chunk[index]);
            var start = index;
            index++;
            while (index < uniqueCount && Utils.HighBits(chunk[index]) == hb)
            {
                index++;
            }

            var containerCount = index - start;
            var lows = new ushort[containerCount];
            for (var j = 0; j < containerCount; j++)
            {
                lows[j] = Utils.LowBits(chunk[start + j]);
            }

            keys.Add(hb);
            containers.Add(containerCount > Container.MaxSize
                ? BitmapContainer.Create(lows)
                : ArrayContainer.Create(lows));
        }

        return new RoaringArray(keys.Count, keys, containers);
    }

    /// <summary>
    /// Bitwise Or operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator |(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(x._highLowContainer | y._highLowContainer);
    }

    /// <summary>
    /// Bitwise And operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator &(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(x._highLowContainer & y._highLowContainer);
    }

    /// <summary>
    /// Bitwise Not operation of a RoaringBitmap
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator ~(RoaringBitmap x)
    {
        return new RoaringBitmap(~x._highLowContainer);
    }

    /// <summary>
    /// Bitwise Xor operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator ^(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(x._highLowContainer ^ y._highLowContainer);
    }

    /// <summary>
    /// Bitwise AndNot operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap AndNot(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(RoaringArray.AndNot(x._highLowContainer, y._highLowContainer));
    }

    /// <summary>
    /// Bitwise Or with another RoaringBitmap, performed in place: this bitmap becomes this | other
    /// </summary>
    /// <param name="other">RoaringBitmap, left untouched</param>
    public void OrInPlace(RoaringBitmap other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        _highLowContainer.OrInPlace(other._highLowContainer);
    }

    /// <summary>
    /// Bitwise And with another RoaringBitmap, performed in place: this bitmap becomes this &amp; other
    /// </summary>
    /// <param name="other">RoaringBitmap, left untouched</param>
    public void AndInPlace(RoaringBitmap other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        _highLowContainer.AndInPlace(other._highLowContainer);
    }

    /// <summary>
    /// Bitwise Xor with another RoaringBitmap, performed in place: this bitmap becomes this ^ other
    /// </summary>
    /// <param name="other">RoaringBitmap, left untouched</param>
    public void XorInPlace(RoaringBitmap other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        _highLowContainer.XorInPlace(other._highLowContainer);
    }

    /// <summary>
    /// Bitwise AndNot with another RoaringBitmap, performed in place: this bitmap becomes AndNot(this, other)
    /// </summary>
    /// <param name="other">RoaringBitmap, left untouched</param>
    public void AndNotInPlace(RoaringBitmap other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        _highLowContainer.AndNotInPlace(other._highLowContainer);
    }

    /// <summary>
    /// Bitwise Not, performed in place: this bitmap becomes ~this
    /// </summary>
    public void NotInPlace()
    {
        _highLowContainer.NotInPlace();
    }

    /// <summary>
    /// Serializes a RoaringBitmap into a stream using the 'official' RoaringBitmap file format
    /// </summary>
    /// <param name="roaringBitmap">RoaringBitmap</param>
    /// <param name="stream">Stream</param>
    public static void Serialize(RoaringBitmap roaringBitmap, Stream stream)
    {
        RoaringArray.Serialize(roaringBitmap._highLowContainer, stream);
    }

    /// <summary>
    /// Deserializes a RoaringBitmap from astream using the 'official' RoaringBitmap file format
    /// </summary>
    /// <param name="stream">Stream</param>
    public static RoaringBitmap Deserialize(Stream stream)
    {
        var ra = RoaringArray.Deserialize(stream);
        return new RoaringBitmap(ra);
    }
}