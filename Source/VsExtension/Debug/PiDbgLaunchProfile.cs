namespace PiDbg.Debug;

internal static class PiDbgLaunchProfile
{
    public const string CommandName = "PiDbg";

    public static string BuildProfileName(string host) => $"Raspberry Pi ({host})";
}
