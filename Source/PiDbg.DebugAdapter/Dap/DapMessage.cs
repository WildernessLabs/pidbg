using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiDbg.DebugAdapter.Dap;

// Represents a framed DAP message (parsed header + raw JSON body).
internal sealed class DapMessage
{
    public int     Seq     { get; }
    public string  Type    { get; }   // "request" | "response" | "event"
    public string? Command { get; }   // set when Type == "request"
    public string? Event   { get; }   // set when Type == "event"
    public JsonNode? Body  { get; }

    private readonly string _raw;

    private DapMessage(int seq, string type, string? command, string? @event, JsonNode? body, string raw)
    {
        Seq     = seq;
        Type    = type;
        Command = command;
        Event   = @event;
        Body    = body;
        _raw    = raw;
    }

    public static DapMessage? TryParse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return null;

            var seq     = node["seq"]?.GetValue<int>() ?? 0;
            var type    = node["type"]?.GetValue<string>() ?? "";
            var command = node["command"]?.GetValue<string>();
            var @event  = node["event"]?.GetValue<string>();
            // Requests carry payload in "arguments"; responses/events use "body".
            var body    = type == "request" ? node["arguments"] : node["body"];

            return new DapMessage(seq, type, command, @event, body, json);
        }
        catch
        {
            return null;
        }
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
