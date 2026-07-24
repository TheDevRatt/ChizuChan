using System.Text;
using System.Text.RegularExpressions;

namespace ChizuChan.Services;

public static partial class YouTubeMusicPathPolicy
{
    private const int MaximumSegmentLength = 80;
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string SanitizeSegment(string? value, string fallback)
    {
        var safeFallback = SanitizeCore(fallback);
        if (string.IsNullOrEmpty(safeFallback))
            safeFallback = "Unknown";
        var result = SanitizeCore(value);
        if (string.IsNullOrEmpty(result))
            result = safeFallback;

        var deviceStem = result.Split('.', 2)[0];
        if (WindowsDeviceNames.Contains(deviceStem))
            result = "_" + result;
        return result;
    }

    private static string SanitizeCore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(Math.Min(normalized.Length, MaximumSegmentLength));
        foreach (var character in normalized)
        {
            if (char.IsControl(character) || character is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                builder.Append(' ');
                continue;
            }
            builder.Append(character);
        }

        var result = WhitespacePattern().Replace(builder.ToString(), " ").Trim(' ', '.');
        if (result is "." or ".." || string.IsNullOrWhiteSpace(result))
            return "";
        if (result.Length > MaximumSegmentLength)
            result = result[..MaximumSegmentLength].TrimEnd(' ', '.');
        return result;
    }

    public static string BuildDestinationPath(
        string libraryRootPath,
        string artist,
        string album,
        string title,
        string canonicalVideoId)
    {
        if (string.IsNullOrWhiteSpace(libraryRootPath))
            throw new ArgumentException("The library root is required.", nameof(libraryRootPath));
        if (!YouTubeIdPattern().IsMatch(canonicalVideoId))
            throw new ArgumentException("The video ID is invalid.", nameof(canonicalVideoId));

        if (IsUncPath(libraryRootPath) && !OperatingSystem.IsWindows())
            return BuildUncDestination(libraryRootPath, artist, album, title, canonicalVideoId);
        if (!Path.IsPathFullyQualified(libraryRootPath))
            throw new ArgumentException("The library root must be absolute.", nameof(libraryRootPath));

        var root = Path.GetFullPath(libraryRootPath);
        var destination = Path.GetFullPath(Path.Combine(
            root,
            SanitizeSegment(artist, "Unknown Artist"),
            SanitizeSegment(album, "Single"),
            $"01 - {SanitizeSegment(title, "Untitled")} [{canonicalVideoId}].m4a"));
        EnsureWithinRoot(root, destination);
        return destination;
    }

    public static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        if (IsUncPath(rootPath) && !OperatingSystem.IsWindows())
        {
            var uncRoot = NormalizeUnc(rootPath).TrimEnd('\\');
            var uncCandidate = NormalizeUnc(candidatePath);
            return uncCandidate.Equals(uncRoot, StringComparison.OrdinalIgnoreCase) ||
                   uncCandidate.StartsWith(uncRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }

        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(root, comparison) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static void EnsureWithinRoot(string root, string destination)
    {
        if (!IsWithinRoot(root, destination))
            throw new InvalidOperationException("The destination escaped the configured library root.");
    }

    private static string BuildUncDestination(
        string root,
        string artist,
        string album,
        string title,
        string id)
    {
        if (root.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part is "." or ".."))
        {
            throw new ArgumentException("A UNC root cannot contain traversal segments.", nameof(root));
        }
        root = NormalizeUnc(root).TrimEnd('\\');
        if (root.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            throw new ArgumentException("A UNC root must include server and share.", nameof(root));
        var destination = string.Join('\\',
            root,
            SanitizeSegment(artist, "Unknown Artist"),
            SanitizeSegment(album, "Single"),
            $"01 - {SanitizeSegment(title, "Untitled")} [{id}].m4a");
        if (!IsWithinRoot(root, destination))
            throw new InvalidOperationException("The destination escaped the configured library root.");
        return destination;
    }

    private static bool IsUncPath(string value) => value.StartsWith("\\\\", StringComparison.Ordinal);

    private static string NormalizeUnc(string value)
    {
        var parts = value.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 2) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return "\\\\" + string.Join('\\', stack);
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTubeIdPattern();
}
