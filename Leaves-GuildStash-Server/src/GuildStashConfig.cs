using System.Text.Json.Serialization;

namespace Leaves_GuildStash_Server;

// Deserialized by SPT's JsonUtil: case-sensitive exact-match, no camelCase policy. Every property needs an explicit [JsonPropertyName] or it silently uses the C# default.
public class GuildStashConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // extra console logging for guild-box actions
    [JsonPropertyName("debug")]
    public bool Debug { get; set; } = true;

    [JsonPropertyName("boxes")]
    public List<GuildStashBoxDef> Boxes { get; set; } = new List<GuildStashBoxDef>();

    [JsonPropertyName("closedBoxSale")]
    public GuildStashSaleConfig ClosedBoxSale { get; set; } = new GuildStashSaleConfig();

    // Seconds to wait after a player's websocket drops before closing their open boxes (alt-F4 or crash).
    // The delay is load-bearing: the websocket ALSO drops during normal play. Recommended to not go below default 60. But can be increased on very unsteady connections. 
    // 0 Disables rather than no grace time.
    [JsonPropertyName("wsDisconnectGraceSeconds")]
    public int WsDisconnectGraceSeconds { get; set; } = 60;
}

public class GuildStashSaleConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("trader")]
    public string Trader { get; set; } = "6617beeaa9cfa777ca915b7c"; //Default - Ref

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "5d235b4d86f7742e017bc88a"; //Default - GP Coins

    [JsonPropertyName("price")]
    public int Price { get; set; } = 1;

    [JsonPropertyName("loyaltyLevel")]
    public int LoyaltyLevel { get; set; } = 1;
}

// One base container that becomes a closed+open guild-stash pair.
public class GuildStashBoxDef
{
    // Stable lookup key for the id store; also the base for "<key>_closed" / "<key>_open".
    //
    // Rename one and every owned box turns into an unknown item, contents included, and box-type matching breaks (storages record this as BoxKey).
    // Never rename, never remove an owned box, never regenerate config/ids.jsonc. New keys are fine.
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("cloneFrom")]
    public string CloneFrom { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("shortName")]
    public string ShortName { get; set; } = "";

    // true = replace the grid whitelist with the topmost "Item" node (holds everything). false keeps the base container's native filter.
    [JsonPropertyName("overrideFilter")]
    public bool OverrideFilter { get; set; } = false;

    // EFT colour name ("red"/"green"/"blue"/"yellow"/"violet"/"grey"); null = mod default
    [JsonPropertyName("closedColor")]
    public string? ClosedColor { get; set; }

    [JsonPropertyName("openColor")]
    public string? OpenColor { get; set; }

    // outer size in the parent grid, in cells. null = keep the base container's size.
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}
