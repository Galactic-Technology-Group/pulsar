using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nms.Shared;

/// <summary>
/// Serializer options every side of the wire must share. The API emits enums as strings (over HTTP and SignalR);
/// a client without this converter cannot deserialize <c>"vlanMode": "Routed"</c> and silently drops the payload.
/// </summary>
public static class NmsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };
}
