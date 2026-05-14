using System.Collections.Generic;
using System.Text.Json;

namespace ASF.AutoAchievement;

/// <summary>
/// Per-bot configuration for the AutoAchievement plugin.
/// Lives inside a bot's JSON config file under the "AutoAchievement" key.
/// All properties are optional. Manual parsing (no JsonSerializer.Deserialize)
/// to survive ASF's aggressive assembly trimming.
/// </summary>
public sealed class PluginConfig {
	public const string ConfigKey = "AutoAchievement";

	public bool Enabled { get; set; } = true;

	// How often to re-scan the entire library for new achievements.
	// Always full library — IPlayerService.GetOwnedGames doesn't include
	// free games never played, so we go through the dynamicstore endpoint
	// (every AppID the account has access to).
	public uint ScanIntervalDays { get; set; } = 7;

	// Between full scans, periodically check for newly-added games (free
	// game claims, gifts, etc.) and unlock achievements for them once.
	// Each dynamic check filters to AppIDs that have no _lastScannedAt
	// entry — i.e. never scanned. New games are picked up within this
	// interval; existing games still get rescanned by the full scan every
	// ScanIntervalDays for any new achievements the dev added. Set to 0
	// to disable dynamic checks entirely (full scan only).
	public uint DynamicCheckIntervalHours { get; set; } = 24;

	// Wait after login before kicking off the first scan.
	public uint InitialDelaySeconds { get; set; } = 60;

	// Pause between games inside a single scan to avoid spamming Steam.
	public uint PerGameDelayMilliseconds { get; set; } = 750;

	// Try to set achievements whose schema lists permission > 0 (server-only).
	// Steam normally rejects these — off by default to keep the log quiet.
	// When false, protected bits are skipped client-side and never sent.
	public bool AttemptProtectedAchievements { get; set; } = false;

	// When true (default), defer per-game scan iterations while ASF's built-in
	// card farmer is actively farming a game (CardsFarmer.NowFarming). The
	// scan picks up exactly where it left off as soon as farming finishes.
	// Set false to scan unconditionally — Play(appID) calls during the scan
	// will knock the card-farming game out of the play slot mid-drop.
	public bool AllowCardFarming { get; set; } = true;

	// AppIDs / names that are never touched.
	public HashSet<uint> Blacklist { get; set; } = [];

	internal static PluginConfig FromAdditionalProperties(IReadOnlyDictionary<string, JsonElement>? additional) {
		// No AutoAchievement block in the bot config → opt-out.
		if (additional is null || !additional.TryGetValue(ConfigKey, out JsonElement element)) {
			return new PluginConfig { Enabled = false };
		}

		PluginConfig config = new();

		if (element.ValueKind != JsonValueKind.Object) {
			return config;
		}

		foreach (JsonProperty prop in element.EnumerateObject()) {
			switch (prop.Name) {
				case "Enabled":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.Enabled = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.Enabled = false; }
					break;
				case "ScanIntervalDays":
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint days) && days > 0) { config.ScanIntervalDays = days; }
					break;
				case "DynamicCheckIntervalHours":
					// 0 = disabled (no dynamic checks, full scan only).
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint dh)) { config.DynamicCheckIntervalHours = dh; }
					break;
				case "InitialDelaySeconds":
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint delay)) { config.InitialDelaySeconds = delay; }
					break;
				case "PerGameDelayMilliseconds":
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint pgms)) { config.PerGameDelayMilliseconds = pgms; }
					break;
				case "AttemptProtectedAchievements":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.AttemptProtectedAchievements = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.AttemptProtectedAchievements = false; }
					break;
				case "AllowCardFarming":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.AllowCardFarming = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.AllowCardFarming = false; }
					break;
				case "Blacklist":
					config.Blacklist = ParseUintArray(prop.Value);
					break;
			}
		}

		return config;
	}

	private static HashSet<uint> ParseUintArray(JsonElement element) {
		HashSet<uint> result = [];
		if (element.ValueKind == JsonValueKind.Array) {
			foreach (JsonElement el in element.EnumerateArray()) {
				if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint val) && val > 0) {
					result.Add(val);
				}
			}
		}
		return result;
	}
}
