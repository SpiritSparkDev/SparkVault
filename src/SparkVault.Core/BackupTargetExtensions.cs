namespace SparkVault.Core;

public static class BackupTargetExtensions
{
    // No `_ =>` default arm on purpose: with every TargetType member covered, adding a fourth
    // one later trips CS8509 here instead of silently degrading to Type.ToString().
    // CS8524 is the separate "an out-of-range cast like (TargetType)3 isn't covered" complaint —
    // that one can only come from a corrupt DB row, and silencing it is what keeps CS8509 usable
    // as the real signal. Suppressed here only, never repo-wide.
#pragma warning disable CS8524
    public static string Describe(this BackupTarget target) => target.Type switch
    {
        TargetType.Local => $"Lokal: {target.DestinationPath}",
        TargetType.Ftp => $"FTP: {target.Host}",
        TargetType.Sftp => $"SFTP: {target.Host}",
    };
#pragma warning restore CS8524
}
