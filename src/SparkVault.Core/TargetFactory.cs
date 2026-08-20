namespace SparkVault.Core;

public static class TargetFactory
{
    public static IBackupTarget Create(BackupTarget config) => config.Type switch
    {
        TargetType.Local => new LocalTarget(config.DestinationPath ?? throw new InvalidOperationException("Local target requires DestinationPath.")),
        TargetType.Ftp => new FtpTarget(config),
        TargetType.Sftp => new SftpTarget(config),
        _ => throw new NotSupportedException($"Unbekannter Zieltyp: {config.Type}"),
    };
}
