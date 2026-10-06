using System.Text.Json;

namespace MasryVoice.Api.Features.Tools;

public record ToolParameter(
    string Type,
    string Description,
    bool Required = true,
    string[]? EnumValues = null
);

public class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, ToolParameter> Parameters { get; set; } = new();

    public object ToOpenAiFunctionSchema()
    {
        var properties = new Dictionary<string, object>();
        var requiredList = new List<string>();

        foreach (var (paramName, param) in Parameters)
        {
            var propDict = new Dictionary<string, object>
            {
                ["type"] = param.Type,
                ["description"] = param.Description
            };

            if (param.EnumValues is { Length: > 0 })
            {
                propDict["enum"] = param.EnumValues;
            }

            properties[paramName] = propDict;

            if (param.Required)
            {
                requiredList.Add(paramName);
            }
        }

        return new
        {
            type = "function",
            function = new
            {
                name = Name,
                description = Description,
                parameters = new
                {
                    type = "object",
                    properties,
                    required = requiredList
                }
            }
        };
    }
}

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(JsonElement arguments, Guid conversationId, CancellationToken ct);
}

public record ToolResult(
    bool Success,
    string Message,
    object? Data = null,
    string? ErrorCode = null
);
