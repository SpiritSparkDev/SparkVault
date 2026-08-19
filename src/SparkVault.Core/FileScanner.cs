namespace SparkVault.Core;

public static class FileScanner
{
    public static IReadOnlyList<BackupFile> Scan(string sourcePath, IEnumerable<string> excludePatterns)
    {
        var patterns = excludePatterns.ToList();
        var result = new List<BackupFile>();

        foreach (var fullPath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, fullPath);
            if (ExclusionMatcher.IsExcluded(relativePath, patterns))
                continue;

            result.Add(new BackupFile(fullPath, relativePath, new FileInfo(fullPath).Length));
        }

        return result;
    }
}
