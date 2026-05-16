using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace PiDbg.UI;

public sealed partial class ConnectDialog : Window
{
    public string  Host       { get; private set; } = "";
    public int     Port       { get; private set; } = 22;
    public string  User       { get; private set; } = "pi";
    public string  RootFolder { get; private set; } = "~/meadow";
    public string? Password   { get; private set; }
    public string? KeyFile    { get; private set; }
    public string  AppName    { get; private set; } = "";
    public bool    Remember   { get; private set; } = true;

    public ConnectDialog() => InitializeComponent();

    public void SetDefaults(string host, string user = "pi", int port = 22,
                            string rootFolder = "~/meadow", string? keyFile = null,
                            string appName = "")
    {
        HostBox.Text       = host;
        UserBox.Text       = user;
        PortBox.Text       = port.ToString(CultureInfo.InvariantCulture);
        RootFolderBox.Text = rootFolder;
        KeyFileBox.Text    = keyFile ?? "";
        AppNameBox.Text    = appName;
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select SSH Private Key",
            Filter = "Private key files (*.pem;*.key;id_rsa)|*.pem;*.key;id_rsa|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true)
            KeyFileBox.Text = dlg.FileName;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var hostText = HostBox.Text.Trim();
        if (string.IsNullOrEmpty(hostText))
        {
            MessageBox.Show(this, "Host is required.", "Configure Pi Debugger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            HostBox.Focus();
            return;
        }

        Host       = hostText;
        Port       = int.TryParse(PortBox.Text.Trim(), out var p) && p > 0 && p < 65536 ? p : 22;
        User       = string.IsNullOrWhiteSpace(UserBox.Text) ? "pi" : UserBox.Text.Trim();
        RootFolder = string.IsNullOrWhiteSpace(RootFolderBox.Text) ? "~/meadow" : RootFolderBox.Text.Trim();
        Password   = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        KeyFile    = string.IsNullOrWhiteSpace(KeyFileBox.Text) ? null : KeyFileBox.Text.Trim();
        AppName    = string.IsNullOrWhiteSpace(AppNameBox.Text) ? "" : AppNameBox.Text.Trim();
        Remember   = RememberCheck.IsChecked == true;

        DialogResult = true;
    }
}
