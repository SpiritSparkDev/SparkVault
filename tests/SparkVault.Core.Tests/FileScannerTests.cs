using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class FileScannerTests
{
    [Fact]
    public void ScansFilesAndAppliesExclusions()
    {
        var root = Directory.CreateTempSubdirectory("sparkvault-scan-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "keep.txt"), "hello");
            File.WriteAllText(Path.Combine(root.FullName, "skip.tmp"), "temp");
            var subDir = Directory.CreateDirectory(Path.Combine(root.FullName, "sub"));
            File.WriteAllText(Path.Combine(subDir.FullName, "nested.txt"), "world");

            var result = FileScanner.Scan(root.FullName, new[] { "*.tmp" });

            Assert.Equal(2, result.Count);
            Assert.Contains(result, f => f.RelativePath == "keep.txt" && f.Size == 5);
            Assert.Contains(result, f => f.RelativePath == Path.Combine("sub", "nested.txt"));
            Assert.DoesNotContain(result, f => f.RelativePath == "skip.tmp");
            var keepFile = result.Single(f => f.RelativePath == "keep.txt");
            Assert.True(DateTime.UtcNow - keepFile.LastWriteTimeUtc < TimeSpan.FromMinutes(1));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
