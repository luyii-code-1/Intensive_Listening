using System.Globalization;
using System.Text.RegularExpressions;

namespace IL.Core.Updates;

public sealed record ManagedVersion(Version Base, int Channel, int Day, int Sequence) : IComparable<ManagedVersion>
{
    public static bool TryParse(string? value, out ManagedVersion version)
    {
        version = null!;
        if (value is null || value.Length > 100) return false;
        var match = Regex.Match(value, @"^v?(\d+\.\d+\.\d+(?:\.\d+)?)(?:(?:-(insider|beta)\.(\d{8})(?:\.(\d+))?)|(?: (Insider Preview|Beta) (\d{4})(?: (\d+))?))?\z");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var number)) return false;
        var channel = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[5].Value;
        var insider = channel is "insider" or "Insider Preview";
        var date = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[6].Value;
        var sequence = match.Groups[4].Success ? match.Groups[4].Value : match.Groups[7].Value;
        if (date.Length == 4) date = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Year.ToString(CultureInfo.InvariantCulture) + date;
        if (channel.Length > 0 && (!DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            (insider ? !int.TryParse(sequence, out var n) || n < 1 : sequence.Length > 0))) return false;
        var day = date.Length == 0 ? 0 : int.Parse(date, CultureInfo.InvariantCulture);
        version = new(new(number.Major, number.Minor, Math.Max(0, number.Build), Math.Max(0, number.Revision)), channel.Length == 0 ? 2 : insider ? 0 : 1, day,
            insider ? int.Parse(sequence, CultureInfo.InvariantCulture) : 0);
        return true;
    }
    public int CompareTo(ManagedVersion? other)
    {
        if (other is null) return 1;
        var result = Base.CompareTo(other.Base);
        if (result == 0) result = Channel.CompareTo(other.Channel);
        if (result == 0) result = Day.CompareTo(other.Day);
        return result == 0 ? Sequence.CompareTo(other.Sequence) : result;
    }
}
