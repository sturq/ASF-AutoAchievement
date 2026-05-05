using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;

namespace ASF.AutoAchievement;

/// <summary>
/// Returns the bot's owned games. Two strategies, mirroring AutoIdle:
///   - GetProfileGamesAsync   = IPlayerService.GetOwnedGames, with names.
///                              Matches the public profile "Games X" count
///                              (~hundreds). Excludes free games never played
///                              and other apps Steam considers "in library
///                              but not owned in the played sense".
///   - GetAllOwnedAppIDsAsync = store dynamicstore/userdata, AppIDs only.
///                              Returns every app the account has access to
///                              (~thousands, includes demos, DLC, soundtracks,
///                              and unplayed free games).
/// </summary>
internal static class GameDiscovery {
	private const string StoreHost = "https://store.steampowered.com";

	internal static async Task<IReadOnlyDictionary<uint, string>?> GetProfileGamesAsync(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (bot.SteamID == 0) {
			return null;
		}

		Dictionary<uint, string>? response;
		try {
			response = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);
		} catch (Exception ex) {
			bot.ArchiLogger.LogGenericException(ex);
			return null;
		}

		if (response is null) {
			bot.ArchiLogger.LogGenericWarning("AutoAchievement: GetOwnedGames returned null (Steam refused, profile private, or query timed out).");
			return null;
		}

		bot.ArchiLogger.LogGenericInfo($"AutoAchievement: profile owned-games returned {response.Count} entries.");
		return response;
	}

	internal static async Task<IReadOnlyCollection<uint>> GetAllOwnedAppIDsAsync(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (bot.SteamID == 0) {
			return [];
		}

		Uri uri = new($"{StoreHost}/dynamicstore/userdata/?id_required=0");

		ObjectResponse<JsonElement>? response;
		try {
			response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(uri).ConfigureAwait(false);
		} catch (Exception ex) {
			bot.ArchiLogger.LogGenericException(ex);
			return [];
		}

		if (response is null || response.Content.ValueKind != JsonValueKind.Object) {
			bot.ArchiLogger.LogGenericWarning("AutoAchievement: failed to fetch dynamicstore userdata.");
			return [];
		}

		if (!TryGetProp(response.Content, "rgOwnedApps", out JsonElement ownedApps)
			|| ownedApps.ValueKind != JsonValueKind.Array) {
			bot.ArchiLogger.LogGenericWarning("AutoAchievement: rgOwnedApps missing from dynamicstore response.");
			return [];
		}

		HashSet<uint> appIDs = new(ownedApps.GetArrayLength());
		foreach (JsonElement el in ownedApps.EnumerateArray()) {
			if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint appID) && appID > 0) {
				appIDs.Add(appID);
			}
		}

		bot.ArchiLogger.LogGenericInfo($"AutoAchievement: dynamicstore returned {appIDs.Count} entries (including DLC / demos / unplayed free games).");
		return appIDs;
	}

	// ASF is published with aggressive trimming that strips
	// JsonElement.TryGetProperty(string, ...). Plain enumeration survives.
	private static bool TryGetProp(in JsonElement element, string name, out JsonElement value) {
		if (element.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty prop in element.EnumerateObject()) {
				if (prop.Name == name) {
					value = prop.Value;
					return true;
				}
			}
		}
		value = default;
		return false;
	}
}
