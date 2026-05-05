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
	public uint ScanIntervalHours { get; set; } = 12;

	// Wait after login before kicking off the first scan.
	public uint InitialDelaySeconds { get; set; } = 60;

	// Pause between games inside a single scan to avoid spamming Steam.
	public uint PerGameDelayMilliseconds { get; set; } = 750;

	// Try to set achievements whose schema lists permission > 0 (server-only).
	// Steam normally rejects these — off by default to keep the log quiet.
	// When false, protected bits are skipped client-side and never sent.
	public bool AttemptProtectedAchievements { get; set; } = false;

	// Discovery mode. true = IPlayerService.GetOwnedGames (~570 entries, the
	// "Games X" count on the public profile). false = store dynamicstore
	// (~thousands, includes free games never played, demos, DLC, etc. —
	// catches achievements on titles that aren't in the profile games list).
	public bool OnlyProfileGames { get; set; } = true;

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
				case "ScanIntervalHours":
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint scan) && scan > 0) { config.ScanIntervalHours = scan; }
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
				case "OnlyProfileGames":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.OnlyProfileGames = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.OnlyProfileGames = false; }
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
