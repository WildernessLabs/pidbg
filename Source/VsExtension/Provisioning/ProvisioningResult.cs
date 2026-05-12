using System.Collections.Generic;
using Grpc.Core;

namespace PiDbg.Provisioning;

internal sealed class ProvisioningStep
{
    public string Name    { get; set; } = "";
    public bool   Skipped { get; set; }
    public bool   Success { get; set; }
    public string Message { get; set; } = "";
}

internal sealed class ProvisioningResult
{
    public bool                            Success { get; set; }
    public IReadOnlyList<ProvisioningStep> Steps   { get; set; } = new List<ProvisioningStep>();
    public string?                         Error   { get; set; }
    public Channel?                        Channel { get; set; }
}
