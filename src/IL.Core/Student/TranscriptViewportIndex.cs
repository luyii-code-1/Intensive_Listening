namespace IL.Core.Student;

public static class TranscriptViewportIndex
{
    // Tall and overlapping rows are covered by the maximum measured row height.
    // Callers check exact bounds only within this sorted candidate range.
    public static (int Start, int End) FindCandidates(IReadOnlyList<double> tops, double top, double bottom, double maximumHeight)
    {
        int Lower(double value, bool inclusive)
        {
            var low = 0; var high = tops.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                var position = tops[middle];
                if (position < value || inclusive && position == value) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        return (Lower(top - maximumHeight, false), Lower(bottom, true));
    }
}
