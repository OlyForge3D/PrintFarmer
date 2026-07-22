using System.Text.Json;

namespace Farm.Backend.Plugin.Moonraker;

internal static class MoonrakerOnlineStatusClassifier
{
    public static bool? ResolveKlippyReady(JsonElement statusObj)
    {
        if (TryGetWebhooksState(statusObj, out string? webhooksState))
        {
            return string.Equals(webhooksState, "ready", StringComparison.OrdinalIgnoreCase);
        }

        return HasPrinterObjectStatus(statusObj) ? true : null;
    }

    private static bool TryGetWebhooksState(JsonElement statusObj, out string? webhooksState)
    {
        webhooksState = null;
        if (!statusObj.TryGetProperty("webhooks", out JsonElement webhooks) ||
            webhooks.ValueKind != JsonValueKind.Object ||
            !webhooks.TryGetProperty("state", out JsonElement state) ||
            state.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        webhooksState = state.GetString();
        return !string.IsNullOrWhiteSpace(webhooksState);
    }

    private static bool HasPrinterObjectStatus(JsonElement statusObj)
    {
        if (statusObj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in statusObj.EnumerateObject())
        {
            if (!string.Equals(property.Name, "webhooks", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
