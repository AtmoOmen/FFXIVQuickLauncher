using System;
using System.Globalization;

namespace XIVLauncher.Common;

public class SeVersion : IComparable, IComparable<SeVersion>, IEquatable<SeVersion>
{
    public uint Year     { get; set; }
    public uint Month    { get; set; }
    public uint Day      { get; set; }
    public uint Revision { get; set; }
    public uint Part     { get; set; }

    public static SeVersion Parse(string input)
    {
        var parts = input.Split('.');
        return new SeVersion
        {
            Year     = uint.Parse(parts[0], CultureInfo.InvariantCulture),
            Month    = uint.Parse(parts[1], CultureInfo.InvariantCulture),
            Day      = uint.Parse(parts[2], CultureInfo.InvariantCulture),
            Revision = uint.Parse(parts[3], CultureInfo.InvariantCulture),
            Part     = uint.Parse(parts[4], CultureInfo.InvariantCulture)
        };
    }

    public static bool operator ==(SeVersion x, SeVersion y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (ReferenceEquals(x, null)) return false;
        return x.Equals(y);
    }

    public static bool operator !=(SeVersion x, SeVersion y) => !(x == y);

    public static bool operator <(SeVersion x, SeVersion y) => ReferenceEquals(x, null) ? !ReferenceEquals(y, null) : x.CompareTo(y) < 0;

    public static bool operator >(SeVersion x, SeVersion y) => !ReferenceEquals(x, null) && x.CompareTo(y) > 0;

    public static bool operator <=(SeVersion x, SeVersion y) => ReferenceEquals(x, null) || x.CompareTo(y) <= 0;

    public static bool operator >=(SeVersion x, SeVersion y) => ReferenceEquals(x, null) ? ReferenceEquals(y, null) : x.CompareTo(y) >= 0;

    public override bool Equals(object obj) => Equals(obj as SeVersion);

    public bool Equals(SeVersion other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Year == other.Year && Month == other.Month && Day == other.Day && Revision == other.Revision && Part == other.Part;
    }

    public override int GetHashCode() =>
        HashCode.Combine(Year, Month, Day, Revision, Part);

    public int CompareTo(SeVersion other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (ReferenceEquals(null, other)) return 1;

        var comparison = Year.CompareTo(other.Year);
        if (comparison != 0) return comparison;
        comparison = Month.CompareTo(other.Month);
        if (comparison != 0) return comparison;
        comparison = Day.CompareTo(other.Day);
        if (comparison != 0) return comparison;
        comparison = Revision.CompareTo(other.Revision);
        if (comparison != 0) return comparison;
        return Part.CompareTo(other.Part);
    }

    public int CompareTo(object obj)
    {
        if (obj == null) return 1;
        if (obj is SeVersion other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(SeVersion)}");
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0:0000}.{1:00}.{2:00}.{3:0000}.{4:0000}", Year, Month, Day, Revision, Part);
}
