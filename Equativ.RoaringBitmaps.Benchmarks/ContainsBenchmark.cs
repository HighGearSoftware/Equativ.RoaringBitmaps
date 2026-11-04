using BenchmarkDotNet.Attributes;
using Equativ.RoaringBitmaps.Datasets;

namespace Equativ.RoaringBitmaps.Benchmarks;

[MemoryDiagnoser(false)]
public class ContainsBenchmark
{
    private RoaringBitmap[] _bitmaps;
    private int[] _probes;

    [Params(
        Paths.Census1881,
        Paths.Census1881Srt,
        Paths.CensusIncome,
        Paths.CensusIncomeSrt,
        Paths.Dimension003,
        Paths.Dimension008,
        Paths.Dimension033,
        Paths.UsCensus2000,
        Paths.WeatherSept85,
        Paths.WeatherSept85Srt,
        Paths.WikileaksNoQuotes,
        Paths.WikileaksNoQuotesSrt)]
    public string FileName { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        using var provider = new ZipRealDataProvider(FileName);
        var values = provider.EnumerateValues().ToArray();
        _bitmaps = values.Select(RoaringBitmap.Create).ToArray();

        // Half of the probes are present in the bitmaps, the other half are random values within the same range
        var random = new Random(42);
        var all = values.SelectMany(v => v).ToArray();
        var max = all.Max();
        _probes = Enumerable.Range(0, 1000)
            .Select(i => i % 2 == 0 ? all[random.Next(all.Length)] : random.Next(0, max))
            .ToArray();
    }

    [Benchmark]
    public int Contains()
    {
        var count = 0;
        foreach (var bitmap in _bitmaps)
        {
            foreach (var probe in _probes)
            {
                if (bitmap.Contains(probe))
                {
                    count++;
                }
            }
        }
        return count;
    }
}
