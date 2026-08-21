using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SparkVault.Core;

namespace SparkVault.App;

public partial class TargetEditorWindow : Window
{
    private readonly int? _existingId;
    private readonly string? _existingEncryptedPassword;
    private readonly string? _existingEncryptedKeyPassphrase;

    public BackupTarget? Result { get; private set; }

    public TargetEditorWindow(BackupTarget? existing)
    {
        InitializeComponent();

        if (existing is not null)
        {
            _existingId = existing.Id;
            _existingEncryptedPassword = existing.EncryptedPassword;
            _existingEncryptedKeyPassphrase = existing.EncryptedKeyPassphrase;
            LoadTarget(existing);
        }
        else
        {
            TypeCombo.SelectedIndex = 0;
            EncryptionModeCombo.SelectedIndex = 0;
        }
    }

    private void LoadTarget(BackupTarget target)
    {
        TypeCombo.SelectedIndex = target.Type switch
        {
            TargetType.Local => 0,
            TargetType.Ftp => 1,
            TargetType.Sftp => 2,
            _ => 0,
        };
        DestinationPathBox.Text = target.DestinationPath ?? "";
        HostBox.Text = target.Host ?? "";
        PortBox.Text = target.Port?.ToString() ?? "";
        UsernameBox.Text = target.Username ?? "";
        RemotePathBox.Text = target.RemotePath ?? "";
        EncryptionModeCombo.SelectedIndex = target.EncryptionMode switch
        {
            FtpEncryption.Explicit => 1,
            FtpEncryption.Implicit => 2,
            _ => 0,
        };
        PrivateKeyPathBox.Text = target.PrivateKeyPath ?? "";
        // PasswordBox/KeyPassphraseBox stay blank on load by design — an empty field on save
        // means "keep the existing encrypted credential" (see BuildTargetFromForm), so we
        // never need to (and never could, without the DPAPI user context) show the plaintext.
    }

    private void TypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var isLocal = TypeCombo.SelectedIndex == 0;
        var isFtp = TypeCombo.SelectedIndex == 1;
        var isSftp = TypeCombo.SelectedIndex == 2;

        LocalPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        RemotePanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
        FtpOnlyPanel.Visibility = isFtp ? Visibility.Visible : Visibility.Collapsed;
        SftpOnlyPanel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        TestConnectionButton.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DestinationPathBox.Text = dialog.SelectedPath;
    }

    private void BrowseKeyFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Privaten Schlüssel wählen" };
        if (dialog.ShowDialog(this) == true)
            PrivateKeyPathBox.Text = dialog.FileName;
    }

    private BackupTarget? BuildTargetFromForm()
    {
        var type = TypeCombo.SelectedIndex switch
        {
            1 => TargetType.Ftp,
            2 => TargetType.Sftp,
            _ => TargetType.Local,
        };

        var target = new BackupTarget { Id = _existingId ?? 0, Type = type };

        if (type == TargetType.Local)
        {
            if (string.IsNullOrWhiteSpace(DestinationPathBox.Text))
            {
                MessageBox.Show(this, "Bitte einen Zielpfad angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            target.DestinationPath = DestinationPathBox.Text.Trim();
            return target;
        }

        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            MessageBox.Show(this, "Bitte einen Host angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            MessageBox.Show(this, "Bitte einen Benutzernamen angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (string.IsNullOrWhiteSpace(RemotePathBox.Text))
        {
            MessageBox.Show(this, "Bitte einen Remote-Pfad angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(PortBox.Text))
        {
            if (!int.TryParse(PortBox.Text, out var parsedPort) || parsedPort <= 0)
            {
                MessageBox.Show(this, "Bitte einen gültigen Port angeben (oder leer lassen).", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            port = parsedPort;
        }

        target.Host = HostBox.Text.Trim();
        target.Port = port;
        target.Username = UsernameBox.Text.Trim();
        target.RemotePath = RemotePathBox.Text.Trim();

        target.EncryptedPassword = PasswordBox.Password.Length > 0
            ? CredentialProtector.Protect(PasswordBox.Password)
            : _existingEncryptedPassword;

        if (type == TargetType.Ftp)
        {
            target.EncryptionMode = EncryptionModeCombo.SelectedIndex switch
            {
                1 => FtpEncryption.Explicit,
                2 => FtpEncryption.Implicit,
                _ => FtpEncryption.None,
            };
        }
        else
        {
            target.PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPathBox.Text) ? null : PrivateKeyPathBox.Text.Trim();
            target.EncryptedKeyPassphrase = KeyPassphraseBox.Password.Length > 0
                ? CredentialProtector.Protect(KeyPassphraseBox.Password)
                : _existingEncryptedKeyPassphrase;

            if (string.IsNullOrEmpty(target.EncryptedPassword) && string.IsNullOrEmpty(target.PrivateKeyPath))
            {
                MessageBox.Show(this, "Bitte Passwort und/oder privaten Schlüssel angeben.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        return target;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var target = BuildTargetFromForm();
        if (target is null) return;

        TestConnectionButton.IsEnabled = false;
        try
        {
            await using var probe = TargetFactory.Create(target);
            var ok = await probe.TestConnectionAsync(CancellationToken.None);
            MessageBox.Show(this, ok ? "Verbindung erfolgreich." : "Verbindung fehlgeschlagen.", "SparkVault",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Verbindung fehlgeschlagen: {ex.Message}", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var target = BuildTargetFromForm();
        if (target is null) return;

        Result = target;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
