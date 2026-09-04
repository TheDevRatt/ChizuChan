namespace ChizuChan.Commands;

public static class YouTubeDownloadUrlParser
{
    public const int MaximumUrlLength = 2048;

    private static readonly HashSet<string> LongFormHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
    };

    public static bool TryParse(string? input, out string canonicalVideoId)
    {
        canonicalVideoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaximumUrlLength ||
            input.Any(char.IsControl))
            return false;

        var value = input.Trim();
        if (value.Contains('\\') || value.Contains('#'))
            return false;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || HasEmptyExplicitPort(value) ||
            !uri.IsDefaultPort || uri.Port != 443)
            return false;

        var rawPath = GetRawPath(value);
        if (rawPath is null || rawPath.Contains('%'))
            return false;
        var rawQuery = GetRawQuery(value);
        if (!HasValidPercentEscapes(rawQuery))
            return false;

        if (string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
            return TryParsePathVideo(rawPath, rawQuery, prefix: "/", out canonicalVideoId);

        if (!LongFormHosts.Contains(uri.Host))
            return false;

        if (string.Equals(rawPath, "/watch", StringComparison.Ordinal))
            return TryParseWatchQuery(rawQuery, out canonicalVideoId);

        if (TryParsePathVideo(rawPath, rawQuery, "/shorts/", out canonicalVideoId))
            return true;

        return TryParsePathVideo(rawPath, rawQuery, "/embed/", out canonicalVideoId);
    }

    private static bool HasEmptyExplicitPort(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? value[authorityStart..] : value[authorityStart..authorityEnd];
        return authority.EndsWith(':');
    }

    private static string? GetRawPath(string value)
    {
        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
            return null;

        var authorityStart = schemeSeparator + 3;
        var pathStart = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (pathStart < 0 || value[pathStart] != '/')
            return string.Empty;

        var pathEnd = value.IndexOfAny(['?', '#'], pathStart);
        return pathEnd < 0 ? value[pathStart..] : value[pathStart..pathEnd];
    }

    private static string GetRawQuery(string value)
    {
        var queryStart = value.IndexOf('?');
        return queryStart < 0 ? string.Empty : value[queryStart..];
    }

    private static bool HasValidPercentEscapes(string query)
    {
        for (var index = 0; index < query.Length; index++)
        {
            if (query[index] != '%')
                continue;
            if (index + 2 >= query.Length ||
                !Uri.IsHexDigit(query[index + 1]) || !Uri.IsHexDigit(query[index + 2]))
                return false;
            index += 2;
        }

        return true;
    }

    private static bool TryParseWatchQuery(string query, out string canonicalVideoId)
    {
        canonicalVideoId = string.Empty;
        if (!TryReadQuery(query, out var videoValues) || videoValues.Count != 1)
            return false;

        var candidate = videoValues[0];
        if (!IsVideoId(candidate))
            return false;

        canonicalVideoId = candidate;
        return true;
    }

    private static bool TryParsePathVideo(
        string rawPath,
        string query,
        string prefix,
        out string canonicalVideoId)
    {
        canonicalVideoId = string.Empty;
        if (!rawPath.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var candidate = rawPath[prefix.Length..];
        if (!IsVideoId(candidate) ||
            !TryReadQuery(query, out var videoValues) || videoValues.Count != 0)
            return false;

        canonicalVideoId = candidate;
        return true;
    }

    private static bool TryReadQuery(string query, out List<string> videoValues)
    {
        videoValues = [];
        if (query.Length == 0)
            return true;
        if (query[0] != '?')
            return false;

        foreach (var field in query.AsSpan(1).ToString().Split('&'))
        {
            if (field.Length == 0)
                return false;

            var separator = field.IndexOf('=');
            var key = separator < 0 ? field : field[..separator];
            if (key.Length == 0 || key.Contains('%') || key.Contains(';'))
                return false;

            if (!string.Equals(key, "v", StringComparison.Ordinal))
                continue;

            videoValues.Add(separator < 0 ? string.Empty : field[(separator + 1)..]);
        }

        return true;
    }

    private static bool IsVideoId(string value)
    {
        if (value.Length != 11)
            return false;

        foreach (var character in value)
        {
            if ((character is >= 'a' and <= 'z') ||
                (character is >= 'A' and <= 'Z') ||
                (character is >= '0' and <= '9') || character is '_' or '-')
                continue;
            return false;
        }

        return true;
    }
}
