using System.IO.Enumeration;

namespace SparkVault.Core;

public static class ExclusionMatcher
{
    public static bool IsExcluded(string relativePath, IEnumerable<string> patterns)
    {
        var normalizedPath = relativePath.Replace('\\', '/');

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            var normalizedPattern = pattern.Replace('\\', '/');
            if (FileSystemName.MatchesSimpleExpression(normalizedPattern, normalizedPath))
                return true;
        }

        return false;
    }
}
