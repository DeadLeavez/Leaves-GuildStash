using System.Text.Json.Serialization;

namespace Leaves_GuildStash_Server;

// Deserialized from config/config.jsonc via ModHelper.GetJsonDataFromFile (SPT's JsonUtil).
// That deserializer is case-sensitive exact-match with NO camelCase policy, so every property MUST carry an explicit [JsonPropertyName] or it silently falls back to the C# default (this bit us: the config was ignored entirely until these attributes were added).
public class GuildStashConfig
{
    // Master switch. Off = the HTTP API isn't wired up and the mod does nothing.
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // Extra console logging for guild-box actions (open/close/convert deltas, tag-drift warnings).
    [JsonPropertyName("debug")]
    public bool Debug { get; set; } = true;

    // The base containers a player can turn into a guild stash. Each entry becomes TWO custom items: a CLOSED resting version (the one you carry / convert to) and an OPEN version that only exists while the storage is open.
    [JsonPropertyName("boxes")]
    public List<GuildStashBoxDef> Boxes { get; set; } = new List<GuildStashBoxDef>();

    // Optional: stock every CLOSED guild box at a trader, cheap (they're useless without a remote).
    [JsonPropertyName("closedBoxSale")]
    public GuildStashSaleConfig ClosedBoxSale { get; set; } = new GuildStashSaleConfig();

    // Set `IsUnremovable` on the OPEN box template so the CLIENT won't offer to discard it at all.
    // The server-side guard (GuildStashDiscardPatch) already refuses the deletion, but it can only answer with a Warning, and a player who ESCs past that is left staring at a ghost box; this removes the option.
    //
    // KILL SWITCH: Close works by pushing a client REMOVE of the open box, and this flag is believed to be client-side only (player actions), leaving server-pushed changes alone.
    // If that's wrong, Close breaks and the core loop with it - set false and restart to back it out without a rebuild.
    [JsonPropertyName("openBoxUnremovable")]
    public bool OpenBoxUnremovable { get; set; } = true;

    // Seconds to wait after a player's websocket drops before closing their open boxes (the alt-F4 / crash case).
    // The wait isn't padding: the websocket also drops during normal play (measured ~1s after deploying into a raid, and on 5s menu reconnects), so closing on the drop itself would rip a box away from someone actively using it.
    // Must comfortably exceed a menu blip. 0 disables the hook.
    [JsonPropertyName("wsDisconnectGraceSeconds")]
    public int WsDisconnectGraceSeconds { get; set; } = 60;
}

// Where/how the closed guild boxes are sold. Defaults to Ref, loyalty 1, 1 GP coin.
public class GuildStashSaleConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // Trader id to stock them at. Default = Ref ("Arena").
    [JsonPropertyName("trader")]
    public string Trader { get; set; } = "6617beeaa9cfa777ca915b7c";

    // Currency item the price is paid in. Default = GP coin (Ref's currency).
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "5d235b4d86f7742e017bc88a";

    [JsonPropertyName("price")]
    public int Price { get; set; } = 1;

    [JsonPropertyName("loyaltyLevel")]
    public int LoyaltyLevel { get; set; } = 1;
}

// One base container that becomes a guild-stash pair (closed + open).
// The two generated item ids are keyed off Key via LeavesUtil's IdStore, so they stay stable across restarts (see the Key field).
public class GuildStashBoxDef
{
    // Stable lookup key for the id store. Also the base for the two derived id keys ("<key>_closed" / "<key>_open").
    //
    // FROZEN - the mod went live 2026-08-26 and real players own boxes minted from these keys.
    // Renaming one mints NEW item tpls: every owned box becomes an unknown item and its contents go with it.
    // It also breaks box-type matching, because each storage records its key as StoredBox.BoxKey.
    // Never rename, never remove a box anyone owns, never regenerate config/ids.jsonc. Adding a new key is fine.
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    // Tpl of the vanilla container to clone (e.g. the Item Case or the T H I C C item case).
    // Both the closed and open versions clone from this, so they share its grid layout / size.
    [JsonPropertyName("cloneFrom")]
    public string CloneFrom { get; set; } = "";

    // Display name of the container (the " (Closed)" / " (Open)" suffix is appended automatically).
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // Short display name (shown on the item grid).
    [JsonPropertyName("shortName")]
    public string ShortName { get; set; } = "";

    // When true, the generated boxes' grid whitelist is replaced with the topmost "Item" node - i.e. they hold EVERYTHING.
    // Leave false for a base whose native filter you want to keep (e.g. a future mag/weapon case that should only hold its own category).
    // Independent of the guild-case blacklist below, which is always applied.
    [JsonPropertyName("overrideFilter")]
    public bool OverrideFilter { get; set; } = false;

    // Grid background colour (EFT colour name, e.g. "red"/"green"/"blue"/"yellow"/"violet"/"grey") for the closed / open state.
    // Null falls back to the mod defaults (see GuildStashItems).
    [JsonPropertyName("closedColor")]
    public string? ClosedColor { get; set; }

    [JsonPropertyName("openColor")]
    public string? OpenColor { get; set; }

    // Outer footprint of the box (its size in the parent grid), in cells. Null = don't change the base container's size.
    // Setting a small size (e.g. 2x2) makes a compact box that opens into big storage.
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}
