using System.Text.Json;

namespace Flotilla;

static class Json
{
    public static bool TryGet(this JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    public static string Text(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public static long Number(this JsonElement element, string name)
    {
        if (!element.TryGet(name, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
            JsonValueKind.True => 1,
            _ => 0,
        };
    }
}
