using System;

namespace PiDbg.Provisioning;

public sealed class ProvisioningException : Exception
{
    public ProvisioningException(string message) : base(message) { }
    public ProvisioningException(string message, Exception inner) : base(message, inner) { }
}
