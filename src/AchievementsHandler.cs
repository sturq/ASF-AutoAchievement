using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Internal;

namespace ASF.AutoAchievement;

/// <summary>
/// Custom SteamKit2 handler that talks to Steam's user-stats service:
///   - ClientGetUserStats     - fetch schema + current stat values
///   - ClientStoreUserStats2  - push back updated stat values
///
/// Pending requests are matched to responses by JobID via TaskCompletionSource,
/// so we don't need to register a callback with ASF's callback manager.
/// </summary>
public sealed class AchievementsHandler : ClientMsgHandler {
	private readonly ConcurrentDictionary<JobID, TaskCompletionSource<GetUserStatsResult>> _pendingGets = new();
	private readonly ConcurrentDictionary<JobID, TaskCompletionSource<StoreUserStatsResult>> _pendingStores = new();

	public override void HandleMsg(IPacketMsg packetMsg) {
		ArgumentNullException.ThrowIfNull(packetMsg);

		switch (packetMsg.MsgType) {
			case EMsg.ClientGetUserStatsResponse:
				HandleGetResponse(packetMsg);
				break;
			case EMsg.ClientStoreUserStatsResponse:
				HandleStoreResponse(packetMsg);
				break;
		}
	}

	public async Task<GetUserStatsResult?> GetUserStatsAsync(uint appID, TimeSpan timeout, CancellationToken ct = default) {
		if (Client is null || !Client.IsConnected || Client.SteamID is null) {
			return null;
		}

		ClientMsgProtobuf<CMsgClientGetUserStats> request = new(EMsg.ClientGetUserStats) {
			SourceJobID = Client.GetNextJobID()
		};
		request.Body.steam_id_for_user = Client.SteamID.ConvertToUInt64();
		request.Body.game_id = appID;
		request.Body.crc_stats = 0;
		request.Body.schema_local_version = -1;

		TaskCompletionSource<GetUserStatsResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingGets[request.SourceJobID] = tcs;

		try {
			Client.Send(request);
		} catch {
			_pendingGets.TryRemove(request.SourceJobID, out _);
			throw;
		}

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
		linked.CancelAfter(timeout);
		using (linked.Token.Register(static state => {
			((TaskCompletionSource<GetUserStatsResult>) state!).TrySetCanceled();
		}, tcs)) {
			try {
				return await tcs.Task.ConfigureAwait(false);
			} catch (OperationCanceledException) {
				_pendingGets.TryRemove(request.SourceJobID, out _);
				return null;
			}
		}
	}

	public async Task<StoreUserStatsResult?> StoreUserStatsAsync(uint appID, uint crcStats, IReadOnlyList<(uint statId, uint statValue)> stats, TimeSpan timeout, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(stats);

		if (Client is null || !Client.IsConnected || Client.SteamID is null) {
			return null;
		}

		if (stats.Count == 0) {
			return new StoreUserStatsResult(EResult.OK);
		}

		ClientMsgProtobuf<CMsgClientStoreUserStats2> request = new(EMsg.ClientStoreUserStats2) {
			SourceJobID = Client.GetNextJobID()
		};
		ulong steamId = Client.SteamID.ConvertToUInt64();
		request.Body.settor_steam_id = steamId;
		request.Body.settee_steam_id = steamId;
		request.Body.game_id = appID;
		request.Body.explicit_reset = false;
		request.Body.crc_stats = crcStats;

		foreach ((uint statId, uint statValue) in stats) {
			request.Body.stats.Add(new CMsgClientStoreUserStats2.Stats {
				stat_id = statId,
				stat_value = statValue
			});
		}

		TaskCompletionSource<StoreUserStatsResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingStores[request.SourceJobID] = tcs;

		try {
			Client.Send(request);
		} catch {
			_pendingStores.TryRemove(request.SourceJobID, out _);
			throw;
		}

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
		linked.CancelAfter(timeout);
		using (linked.Token.Register(static state => {
			((TaskCompletionSource<StoreUserStatsResult>) state!).TrySetCanceled();
		}, tcs)) {
			try {
				return await tcs.Task.ConfigureAwait(false);
			} catch (OperationCanceledException) {
				_pendingStores.TryRemove(request.SourceJobID, out _);
				return null;
			}
		}
	}

	private void HandleGetResponse(IPacketMsg packetMsg) {
		ClientMsgProtobuf<CMsgClientGetUserStatsResponse> resp = new(packetMsg);
		if (!_pendingGets.TryRemove(resp.TargetJobID, out TaskCompletionSource<GetUserStatsResult>? tcs)) {
			return;
		}

		EResult eresult = (EResult) resp.Body.eresult;
		List<(uint statId, uint statValue)> stats = new(resp.Body.stats?.Count ?? 0);
		if (resp.Body.stats is not null) {
			foreach (CMsgClientGetUserStatsResponse.Stats s in resp.Body.stats) {
				stats.Add((s.stat_id, s.stat_value));
			}
		}

		byte[]? schema = resp.Body.schema;
		uint crcStats = resp.Body.crc_stats;

		tcs.TrySetResult(new GetUserStatsResult(eresult, crcStats, stats, schema));
	}

	private void HandleStoreResponse(IPacketMsg packetMsg) {
		ClientMsgProtobuf<CMsgClientStoreUserStatsResponse> resp = new(packetMsg);
		if (!_pendingStores.TryRemove(resp.TargetJobID, out TaskCompletionSource<StoreUserStatsResult>? tcs)) {
			return;
		}

		tcs.TrySetResult(new StoreUserStatsResult((EResult) resp.Body.eresult));
	}
}

public sealed record GetUserStatsResult(EResult Result, uint CrcStats, IReadOnlyList<(uint statId, uint statValue)> Stats, byte[]? SchemaBytes);

public sealed record StoreUserStatsResult(EResult Result);

/// <summary>
/// Decodes a single achievement bit from a binary-VDF schema (the bytes
/// in CMsgClientGetUserStatsResponse.schema).
/// </summary>
public sealed record AchievementBit(uint StatID, int BitIndex, string ApiName, int Permission, string DisplayName);

internal static class SchemaParser {
	/// <summary>
	/// Parses the binary KeyValue schema returned by Steam and yields one
	/// AchievementBit per achievement it advertises. Stats with type=4 are
	/// the achievement bitmap stats (each bit = one achievement).
	/// </summary>
	internal static List<AchievementBit> ParseAchievements(byte[]? schemaBytes) {
		List<AchievementBit> result = [];
		if (schemaBytes is null || schemaBytes.Length == 0) {
			return result;
		}

		KeyValue root = new();
		using (MemoryStream ms = new(schemaBytes, writable: false)) {
			try {
				if (!root.TryReadAsBinary(ms)) {
					return result;
				}
			} catch {
				return result;
			}
		}

		KeyValue stats = root["stats"];
		if (stats == KeyValue.Invalid) {
			return result;
		}

		foreach (KeyValue stat in stats.Children) {
			if (!uint.TryParse(stat.Name, out uint statId)) {
				continue;
			}

			// Achievement-bitmap stats are identified by having a "bits" subkey
			// (each bit corresponds to one achievement). The type field is
			// usually "4" but we don't rely on that - presence of "bits" is
			// definitional.
			KeyValue bits = stat["bits"];
			if (bits == KeyValue.Invalid || bits.Children.Count == 0) {
				continue;
			}

			foreach (KeyValue bit in bits.Children) {
				if (!int.TryParse(bit.Name, out int bitIndex) || bitIndex < 0 || bitIndex > 31) {
					continue;
				}

				string apiName = bit["name"].AsString() ?? "";
				int permission = bit["permission"].AsInteger(0);

				// Localized display name lives under display.name. Some schemas
				// store the string directly, others nest it under language keys
				// like "english". Try the direct form, then fall back to english,
				// then to the API name.
				string displayName = bit["display"]["name"].AsString()
					?? bit["display"]["name"]["english"].AsString()
					?? apiName;

				result.Add(new AchievementBit(statId, bitIndex, apiName, permission, displayName));
			}
		}

		return result;
	}
}
