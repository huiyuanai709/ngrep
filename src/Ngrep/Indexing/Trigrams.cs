namespace Ngrep.Indexing;

internal static class Trigrams
{
    public const int Length = 3;

    public static HashSet<string> Extract(string text)
    {
        var folded = text.ToLowerInvariant();
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (folded.Length < Length)
        {
            return set;
        }

        for (var i = 0; i <= folded.Length - Length; i++)
        {
            set.Add(folded.Substring(i, Length));
        }

        return set;
    }

    public static List<string> ForQuery(string pattern)
    {
        var folded = pattern.ToLowerInvariant();
        var list = new List<string>(Math.Max(0, folded.Length - 2));
        for (var i = 0; i <= folded.Length - Length; i++)
        {
            list.Add(folded.Substring(i, Length));
        }

        return list;
    }
}
