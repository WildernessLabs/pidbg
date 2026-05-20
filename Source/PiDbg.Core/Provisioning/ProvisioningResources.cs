using System.Reflection;

namespace PiDbg.Provisioning;

public static class ProvisioningResources
{
    private static Assembly _binaryAssembly = typeof(ProvisioningResources).Assembly;

    // Register the entry-point assembly that contains large binary embedded resources
    // (meadow-daemon binary, vsdbg tarball). Text resources live in PiDbg.Core itself.
    public static void Register(Assembly assembly) => _binaryAssembly = assembly;

    public static Assembly Get() => _binaryAssembly;
}
