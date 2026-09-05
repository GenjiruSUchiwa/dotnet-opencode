namespace OpenCode.Server.Endpoints;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCode.Core.Session;
using OpenCode.Schema;

public sealed class SessionQueryParameterException(string message, string? field = null) : ArgumentException(message)
{
    public string? Field { get; } = field;
}

public sealed class SessionCursorException(string message = "Invalid cursor") : Exception(message);

public sealed record SessionPageCursors(
    [property: JsonPropertyName("previous"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Previous = null,
    [property: JsonPropertyName("next"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Next = null);

/// <summary>Wire cursors from protocol/groups/session.ts and server/handlers/message.ts.</summary>
internal static class SessionQueryParameters
{
    internal static SessionListQuery Sessions(IQueryCollection query)
    {
        CheckKeys(query, ["limit", "order", "directory", "project", "subpath", "workspace", "search", "parentID", "cursor"]);
        var limit = Limit(query, 9_007_199_254_740_991);
        // Query fields still undergo protocol decoding even when a cursor supplies the effective filters.
        var input = new SessionListQuery(limit, Order(Value(query, "order")), Value(query, "directory"),
            Value(query, "project"), Value(query, "subpath"), Workspace(Value(query, "workspace")),
            Value(query, "search"), query.ContainsKey("parentID"), Parent(Value(query, "parentID")));
        if (Value(query, "cursor") is not { } cursor) return input;
        try
        {
            using var document = Decode(cursor, strict: true);
            var root = document.RootElement;
            var directory = OptionalBranch(root, "directory");
            var project = directory is null && (!root.TryGetProperty("subpath", out var subpath) || subpath.ValueKind == JsonValueKind.String)
                ? OptionalBranch(root, "project") : null;
            var anchor = root.GetProperty("anchor");
            var time = anchor.GetProperty("time").GetDouble();
            if (!double.IsFinite(time)) throw new JsonException("Anchor time must be finite.");
            return new SessionListQuery(limit, Order(Text(root, "order")), directory, project,
                project is null ? null : Text(root, "subpath"), Workspace(Text(root, "workspace")), Text(root, "search"),
                root.TryGetProperty("parentID", out _), Parent(Text(root, "parentID")),
                new SessionListAnchor(SessionId.FromExisting(Text(anchor, "id", true)!), time, Direction(Text(anchor, "direction", true))));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SessionCursorException();
        }
    }

    internal static (int Limit, SessionQueryOrder Order, SessionMessageAnchor? Anchor) Messages(IQueryCollection query)
    {
        CheckKeys(query, ["limit", "order", "cursor"]);
        var limit = checked((int)Limit(query, 200));
        var order = Order(Value(query, "order")) ?? SessionQueryOrder.Descending;
        var cursor = Value(query, "cursor");
        if (string.IsNullOrEmpty(cursor)) return (limit, order, null);
        if (query.ContainsKey("order")) throw new SessionCursorException("Cursor cannot be combined with order");
        try
        {
            using var document = Decode(cursor, strict: false);
            var root = document.RootElement;
            var id = Text(root, "id", true)!;
            if (!id.StartsWith("msg_", StringComparison.Ordinal)) throw new JsonException("Invalid message ID.");
            return (limit, Order(Text(root, "order", true))!.Value,
                new SessionMessageAnchor(MessageId.FromExisting(id), Direction(Text(root, "direction", true))));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SessionCursorException();
        }
    }

    internal static string SessionCursor(SessionListQuery input, SessionInfo session, SessionPageDirection direction)
    {
        var data = new JsonObject();
        if (input.Workspace is not null) data["workspace"] = input.Workspace;
        if (input.Order is not null) data["order"] = OrderText(input.Order.Value);
        if (input.Search is not null) data["search"] = input.Search;
        if (input.FilterParent) data["parentID"] = input.ParentId?.Value ?? "null";
        // The upstream cursor union selects the directory branch first, then project, then all.
        if (input.Directory is not null) data["directory"] = input.Directory;
        else if (input.Project is not null)
        {
            data["project"] = input.Project;
            if (input.Subpath is not null) data["subpath"] = input.Subpath;
        }
        data["anchor"] = new JsonObject
        {
            ["id"] = session.Id.Value,
            ["time"] = session.Time.Updated.ToUnixTimeMilliseconds(),
            ["direction"] = DirectionText(direction)
        };
        return Encode(data.ToJsonString());
    }

    internal static string MessageCursor(SessionMessage message, SessionQueryOrder order, SessionPageDirection direction) =>
        Encode(JsonSerializer.Serialize(new { id = message.Id.Value, order = OrderText(order), direction = DirectionText(direction) }));

    private static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonDocument Decode(string cursor, bool strict)
    {
        // Session cursors use Effect's strict URL alphabet. Message cursors use
        // Node Buffer's permissive base64url decoding, including standard base64.
        var encoded = strict ? cursor.Replace("\r", "").Replace("\n", "")
            : Regex.Replace(cursor.Split('=')[0], "[^A-Za-z0-9+/_-]", "", RegexOptions.NonBacktracking);
        if (strict && (encoded.Length % 4 == 1 || !Regex.IsMatch(encoded, "\\A[-_A-Za-z0-9]*={0,2}\\z", RegexOptions.NonBacktracking)))
            throw new FormatException("Invalid base64url cursor.");
        if (!strict && encoded.Length % 4 == 1) encoded = encoded[..^1];
        encoded = encoded.Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))));
    }

    private static string? Text(JsonElement root, string name, bool required = false)
    {
        if (!root.TryGetProperty(name, out var value) && !required) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException($"Expected string {name}.");
    }

    private static string? OptionalBranch(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static SessionQueryOrder? Order(string? value) => value switch
    {
        null => null,
        "asc" => SessionQueryOrder.Ascending,
        "desc" => SessionQueryOrder.Descending,
        _ => throw new SessionQueryParameterException("Order must be asc or desc.", "order")
    };

    private static SessionPageDirection Direction(string? value) => value switch
    {
        "previous" => SessionPageDirection.Previous,
        "next" => SessionPageDirection.Next,
        _ => throw new JsonException("Invalid cursor direction.")
    };

    private static string OrderText(SessionQueryOrder order) => order == SessionQueryOrder.Ascending ? "asc" : "desc";
    private static string DirectionText(SessionPageDirection direction) => direction == SessionPageDirection.Previous ? "previous" : "next";

    private static SessionId? Parent(string? value) => value is null or "null" ? null : SessionId.FromExisting(value);

    private static string? Workspace(string? value) => value is null || value.StartsWith("wrk", StringComparison.Ordinal)
        ? value : throw new SessionQueryParameterException("Workspace ID must start with wrk.", "workspace");

    private static string? Value(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var value)) return null;
        if (value.Count != 1) throw new SessionQueryParameterException("Query parameters must have a single value.", name);
        return value[0]!;
    }

    private static long Limit(IQueryCollection query, long maximum)
    {
        if (Value(query, "limit") is not { } value) return 50;
        var invalid = new SessionQueryParameterException($"Limit must be a positive integer no greater than {maximum}.", "limit");
        value = value.Trim();
        // Effect NumberFromString uses Number(), including unsigned radix prefixes.
        var radix = Regex.IsMatch(value, "\\A0[xX][0-9a-fA-F]+\\z", RegexOptions.NonBacktracking) ? 16
            : Regex.IsMatch(value, "\\A0[bB][01]+\\z", RegexOptions.NonBacktracking) ? 2
            : Regex.IsMatch(value, "\\A0[oO][0-7]+\\z", RegexOptions.NonBacktracking) ? 8 : 0;
        double number;
        if (radix != 0)
        {
            try { number = Convert.ToUInt64(value[2..], radix); }
            catch (Exception error) when (error is FormatException or OverflowException) { throw invalid; }
        }
        else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw invalid;
        if (!double.IsFinite(number) || number < 1 || number > maximum || number != Math.Truncate(number)) throw invalid;
        return (long)number;
    }

    private static void CheckKeys(IQueryCollection query, string[] allowed)
    {
        if (query.Keys.FirstOrDefault(key => key != "auth_token" && !allowed.Contains(key, StringComparer.Ordinal)) is { } key)
            throw new SessionQueryParameterException("Unknown query parameter.", key);
    }
}
