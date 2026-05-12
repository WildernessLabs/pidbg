using System.Globalization;
using System.Windows;

namespace PiDbg.UI;

public sealed partial class ConnectDialog : Window
{
    public string  Host     { get; private set; } = "";
    public int     Port     { get; private set; } = 22;
    public string  User     { get; private set; } = "pi";
    public string? Password { get; private set; }
    public bool    Remember { get; private set; } = true;

    public ConnectDialog() => InitializeComponent();

    public void SetDefaults(string host, string user = "pi", int port = 22)
    {
        HostBox.Text = host;
        UserBox.Text = user;
        PortBox.Text = port.ToString(CultureInfo.InvariantCulture);
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var hostText = HostBox.Text.Trim();
        if (string.IsNullOrEmpty(hostText))
        {
            MessageBox.Show(this, "Host is required.", "Connect to Raspberry Pi",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            HostBox.Focus();
            return;
        }

        Host     = hostText;
        Port     = int.TryParse(PortBox.Text.Trim(), out var p) && p > 0 && p < 65536 ? p : 22;
        User     = string.IsNullOrWhiteSpace(UserBox.Text) ? "pi" : UserBox.Text.Trim();
        Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        Remember = RememberCheck.IsChecked == true;

        DialogResult = true;
    }
}
