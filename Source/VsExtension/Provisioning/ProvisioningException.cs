using System;

namespace PiDbg.Provisioning;

internal sealed class ProvisioningException : Exception
{
    public ProvisioningException(string message) : base(message) { }
    public ProvisioningException(string message, Exception inner) : base(message, inner) { }
}
