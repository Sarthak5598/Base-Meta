using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataLens.Core;

/// <summary>One JSON convention everywhere: camelCase names, enums as camelCase strings.</summary>
public static class DataLensJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
