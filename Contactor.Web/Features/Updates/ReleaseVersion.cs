using System.Globalization;

namespace Contactor.Web.Features.Updates;

/// <summary>
/// A release's version, <c>YYYY.M.D</c> plus <c>.N</c> for a later release the same day (see <see cref="AppBuild"/>),
/// compared part by part: 2026.11.20 is newer than 2026.11.4, and 2026.11.4.1 newer than 2026.11.4.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly int[] _parts;

    private ReleaseVersion(string text, int[] parts)
    {
        Text = text;
        _parts = parts;
    }

    public string Text { get; }

    /// <summary>Reads a release's version; a tag's leading "v" (<c>v2026.11.20</c>) is allowed.</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(text) || text.Length > 32)
        {
            return false;
        }

        var plain = text.StartsWith('v') ? text[1..] : text;
        var pieces = plain.Split('.');
        if (pieces.Length is < 3 or > 4)
        {
            return false;
        }

        var parts = new int[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (pieces[i].Length is 0 or > 5 || !int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(plain, parts);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < Math.Max(_parts.Length, other._parts.Length); i++)
        {
            var mine = i < _parts.Length ? _parts[i] : 0;
            var theirs = i < other._parts.Length ? other._parts[i] : 0;
            if (mine != theirs)
            {
                return mine.CompareTo(theirs);
            }
        }

        return 0;
    }

    public bool Equals(ReleaseVersion? other)
    {
        return other is not null && CompareTo(other) == 0;
    }

    public override bool Equals(object? obj)
    {
        return obj is ReleaseVersion other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var part in _parts.Reverse().SkipWhile(p => p == 0))
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Text;
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right) > 0;
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right) < 0;
    }

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right) >= 0;
    }

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right) <= 0;
    }

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right)
    {
        return !(left == right);
    }
}
