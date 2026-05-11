namespace PiDbg.Infrastructure;

public enum OutputPane { PiDbg, Provisioning }

public interface IOutputWindowService
{
    void Write(OutputPane pane, string message);
    void WriteLine(OutputPane pane, string message);
    void WriteError(OutputPane pane, string message);
    void WriteWarning(OutputPane pane, string message);
    void Clear(OutputPane pane);
    void Activate(OutputPane pane);
}
