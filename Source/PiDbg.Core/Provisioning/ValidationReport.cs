using System.Collections.Generic;
using System.Linq;

namespace PiDbg.Provisioning;

internal sealed class ValidationItem
{
    public string Check   { get; set; } = "";
    public bool   Passed  { get; set; }
    public bool   IsFatal { get; set; }
    public string Message { get; set; } = "";
}

internal sealed class ValidationReport
{
    public IReadOnlyList<ValidationItem> Items { get; set; } = new List<ValidationItem>();

    public bool AllFatalsPassed => Items.All(i => !i.IsFatal || i.Passed);

    public IReadOnlyList<ValidationItem> Failures =>
        Items.Where(i => !i.Passed && i.IsFatal).ToList();

    public IReadOnlyList<ValidationItem> Warnings =>
        Items.Where(i => !i.Passed && !i.IsFatal).ToList();
}
