using System.Collections;
using IL.Core.Student;
using Xunit;

namespace IL.Core.Tests;

public sealed class TranscriptViewportIndexTests
{
    [Fact]
    public void VariableHeightsAndOverlapsDoNotLoseVisibleRows()
    {
        var random = new Random(42);
        var tops = Enumerable.Range(0, 500).Select(i => (double)(i / 2 * 76)).ToArray();
        var heights = tops.Select(_ => (double)random.Next(50, 800)).ToArray();
        var maximum = heights.Max();
        foreach (var top in Enumerable.Range(0, 100).Select(i => i * 199d - 500))
        {
            var bottom = top + 1000;
            var (start, end) = TranscriptViewportIndex.FindCandidates(tops, top, bottom, maximum);
            var found = Enumerable.Range(start, end - start).Where(i => tops[i] + heights[i] >= top && tops[i] <= bottom);
            var expected = Enumerable.Range(0, tops.Length).Where(i => tops[i] + heights[i] >= top && tops[i] <= bottom);
            Assert.Equal(expected, found);
        }
    }

    [Fact]
    public void LargeTranscriptLookupDoesNotScanAllSentences()
    {
        var tops = new CountingPositions(100_000);
        var (start, end) = TranscriptViewportIndex.FindCandidates(tops, 4_000_000, 4_001_600, 80);
        Assert.Equal(49_999, start); Assert.Equal(50_021, end);
        Assert.InRange(tops.Reads, 1, 36);
    }

    [Fact]
    public void EmptyTranscriptHasNoCandidates() => Assert.Equal((0, 0), TranscriptViewportIndex.FindCandidates([], 0, 1000, 0));

    private sealed class CountingPositions(int count) : IReadOnlyList<double>
    {
        public int Reads;
        public int Count => count;
        public double this[int index] { get { Reads++; return index * 80d; } }
        public IEnumerator<double> GetEnumerator() { for (var i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
