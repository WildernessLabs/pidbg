using System;
using System.Collections.Generic;

namespace PiDbg.Provisioning;

internal static class PlatformValidator
{
    public static ValidationReport Validate(DetectionResult result)
    {
        var items = new List<ValidationItem>();

        items.Add(Check("Architecture",
            result.Host.Arch == "aarch64",
            fatal: true,
            $"ARM64 ({result.Host.Arch})",
            $"Unsupported architecture '{result.Host.Arch}'. Only ARM64 (aarch64) is supported."));

        bool osOk = result.Host.OsId is "raspbian" or "debian" or "raspios" or "ubuntu";
        items.Add(Check("Operating System",
            osOk,
            fatal: true,
            result.Host.OsPretty,
            $"Unsupported OS '{result.Host.OsPretty}'. " +
            "Supported: Raspberry Pi OS 64-bit (Bookworm), Debian 12."));

        bool verOk = result.Host.OsId == "ubuntu"
            ? int.TryParse(result.Host.OsVersion.Split('.')[0], out var uv) && uv >= 22
            : int.TryParse(result.Host.OsVersion, out var dv) && dv >= 12;
        items.Add(Check("OS Version",
            verOk,
            fatal: true,
            $"Version {result.Host.OsVersion} (OK)",
            $"OS version {result.Host.OsVersion} is too old. Minimum: Debian 12 (Bookworm)."));

        items.Add(Check("systemd User Session",
            result.Runtime.SystemdUserAvailable,
            fatal: true,
            "systemd --user available",
            "systemd user session not available. Ensure pam_systemd.so is configured."));

        items.Add(Check("Directory /opt/meadow",
            result.Filesystem.OptMeadowExists,
            fatal: true,
            "/opt/meadow exists",
            "/opt/meadow does not exist. Run setup-meadow.sh on the device first:\n" +
            "  curl -sSL .../setup-meadow.sh | sudo bash"));

        items.Add(Check("/opt/meadow Writable",
            result.Filesystem.OptMeadowWritable,
            fatal: true,
            "/opt/meadow is writable",
            "/opt/meadow is not writable. Run setup-meadow.sh to fix ownership:\n" +
            "  sudo chown -R $USER /opt/meadow"));

        items.Add(Check("Disk Space",
            result.Filesystem.FreeBytesOpt >= 200L * 1024 * 1024,
            fatal: true,
            $"{result.Filesystem.FreeBytesOpt / 1024 / 1024} MB free (OK)",
            $"Insufficient disk space: " +
            $"{result.Filesystem.FreeBytesOpt / 1024 / 1024} MB available, 200 MB required."));

        items.Add(Check("Linger Enabled",
            result.Host.Linger,
            fatal: false,
            "Linger enabled (service persists after logout)",
            "Linger not enabled. The daemon will stop on SSH disconnect. " +
            "Run: loginctl enable-linger $USER"));

        items.Add(Check("curl Available",
            result.Runtime.CurlAvailable,
            fatal: false,
            "curl available (online vsdbg install supported)",
            "curl not found. vsdbg will be installed from the bundled offline tarball."));

        return new ValidationReport { Items = items };
    }

    private static ValidationItem Check(
        string name, bool condition, bool fatal, string passed, string failed)
        => new ValidationItem
        {
            Check   = name,
            Passed  = condition,
            IsFatal = fatal,
            Message = condition ? passed : failed,
        };
}
