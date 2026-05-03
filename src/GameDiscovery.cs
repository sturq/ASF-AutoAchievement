using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace ASF.AutoAchievement;

/// <summary>
/// Returns the bot's owned games (AppID → display name). Uses ASF's
/// ArchiHandler.GetOwnedGames wrapper around IPlayerService.GetOwnedGames,
/// which is what powers the public profile's "Games X" count. No Steam
/// Web API key required; rides on the bot's authenticated protocol session.
/// </summary>
internal static class GameDiscovery {
	internal static async Task<IReadOnlyDictionary<uint, string>?> GetOwnedGamesAsync(Bot bot) {
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
}
