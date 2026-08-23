namespace SparkVault.Core;

public static class FileScanner
{
    public static IReadOnlyList<BackupFile> Scan(string sourcePath, IEnumerable<string> excludePatterns)
    {
        var patterns = excludePatterns.ToList();
        var result = new List<BackupFile>();

        // IgnoreInaccessible defaults to true here (unlike the SearchOption overload), so an
        // ACL-denied subfolder is skipped instead of aborting the whole scan.
        // AttributesToSkip = 0 keeps today's behaviour of including hidden/system files.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };

        foreach (var fullPath in Directory.EnumerateFiles(sourcePath, "*", options))
        {
            var relativePath = Path.GetRelativePath(sourcePath, fullPath);
            if (ExclusionMatcher.IsExcluded(relativePath, patterns))
                continue;

            var info = new FileInfo(fullPath);
            result.Add(new BackupFile(fullPath, relativePath, info.Length, info.LastWriteTimeUtc));
        }

        return result;
    }
}
