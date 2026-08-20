using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class TargetFactoryTests
{
    [Fact]
    public void Create_Local_ReturnsLocalTarget()
    {
        var config = new BackupTarget { Type = TargetType.Local, DestinationPath = "C:\\a" };

        var target = TargetFactory.Create(config);

        Assert.IsType<LocalTarget>(target);
    }

    [Fact]
    public void Create_Ftp_ReturnsFtpTarget()
    {
        var config = new BackupTarget { Type = TargetType.Ftp, Host = "ftp.example.com", Username = "u", RemotePath = "/x" };

        var target = TargetFactory.Create(config);

        Assert.IsType<FtpTarget>(target);
    }

    [Fact]
    public void Create_Sftp_ReturnsSftpTarget()
    {
        var config = new BackupTarget
        {
            Type = TargetType.Sftp,
            Host = "sftp.example.com",
            Username = "u",
            RemotePath = "/x",
            EncryptedPassword = CredentialProtector.Protect("password")
        };

        var target = TargetFactory.Create(config);

        Assert.IsType<SftpTarget>(target);
    }

    [Fact]
    public void Create_Local_WithoutDestinationPath_Throws()
    {
        var config = new BackupTarget { Type = TargetType.Local, DestinationPath = null };

        Assert.Throws<InvalidOperationException>(() => TargetFactory.Create(config));
    }
}
