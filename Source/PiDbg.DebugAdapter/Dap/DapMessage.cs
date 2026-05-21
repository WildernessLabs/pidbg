using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiDbg.DebugAdapter.Dap;

// Represents a framed DAP message (parsed header + raw JSON body).
internal sealed class DapMessage
{
    public int     Seq        { get; }
    public int     RequestSeq { get; }  // non-zero only for type=="response"
    public string  Type       { get; }  // "request" | "response" | "event"
    public string? Command    { get; }  // set when Type == "request" or "response"
    public string? Event      { get; }  // set when Type == "event"
    public JsonNode? Body     { get; }

    private readonly string _raw;

    private DapMessage(int seq, int requestSeq, string type, string? command, string? @event, JsonNode? body, string raw)
    {
        Seq        = seq;
        RequestSeq = requestSeq;
        Type       = type;
        Command    = command;
        Event      = @event;
        Body       = body;
        _raw       = raw;
    }

    public static DapMessage? TryParse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return null;

            var seq        = node["seq"]?.GetValue<int>() ?? 0;
            var requestSeq = node["request_seq"]?.GetValue<int>() ?? 0;
            var type       = node["type"]?.GetValue<string>() ?? "";
            var command    = node["command"]?.GetValue<string>();
            var @event     = node["event"]?.GetValue<string>();
            // DAP uses "arguments" for requests, "body" for responses and events.
            var body       = type == "request" ? node["arguments"] : node["body"];

            return new DapMessage(seq, requestSeq, type, command, @event, body, json);
        }
        catch
        {
            return null;
        }
    }

    // Build a request (used when the adapter acts as a DAP client toward vsdbg).
    public static string BuildRequest(int seq, string command, object? arguments = null)
    {
        var node = new JsonObject
        {
            ["seq"]     = seq,
            ["type"]    = "request",
            ["command"] = command,
        };
        if (arguments != null)
            node["arguments"] = JsonSerializer.SerializeToNode(arguments);
        return node.ToJsonString();
    }

    // Build a response to a request.
    public static string BuildResponse(int requestSeq, int seq, string command, bool success, object? body = null)
    {
        var node = new JsonObject
        {
            ["seq"]         = seq,
            ["type"]        = "response",
            ["request_seq"] = requestSeq,
            ["success"]     = success,
            ["command"]     = command,
        };
        if (body != null)
            node["body"] = JsonSerializer.SerializeToNode(body);
        return node.ToJsonString();
    }

    // Build an event.
    public static string BuildEvent(int seq, string eventName, object? body = null)
    {
        var node = new JsonObject
        {
            ["seq"]   = seq,
            ["type"]  = "event",
            ["event"] = eventName,
        };
        if (body != null)
            node["body"] = JsonSerializer.SerializeToNode(body);
        return node.ToJsonString();
    }

    public string ToJson() => _raw;
}
