using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PiDbg.UI;

// Tools → Options → Pi Debugger → Connection settings page.
// Implemented in Phase 7 (P7.4).
[Guid("C3D4E5F6-A7B8-9012-CDEF-012345678901")]
internal sealed class PiDbgOptionPage : DialogPage
{
    [Category("Connection")]
    [DisplayName("Pi Host")]
    [Description("Hostname or IP address of the Raspberry Pi.")]
    public string Host { get; set; } = "";

    [Category("Connection")]
    [DisplayName("SSH Port")]
    [Description("SSH port on the Raspberry Pi (default: 22).")]
    public int SshPort { get; set; } = 22;

    [Category("Connection")]
    [DisplayName("Username")]
    [Description("SSH username for the Raspberry Pi.")]
    public string Username { get; set; } = "";

    [Category("Connection")]
    [DisplayName("Private Key Path")]
    [Description("Path to the SSH private key file.")]
    public string PrivateKeyPath { get; set; } = "";

    [Category("Application")]
    [DisplayName("App Name")]
    [Description("Target application name on the Pi.")]
    public string AppName { get; set; } = "";
}
