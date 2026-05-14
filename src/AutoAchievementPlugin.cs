using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace ASF.AutoAchievement;

/// <summary>
/// Auto-achievement plugin. For every owned game (minus the blacklist), the
/// plugin briefly enters "playing" state, fetches the achievement schema via
/// ClientGetUserStats, and pushes back a ClientStoreUserStats2 with every
/// available bit set. Re-runs every ScanIntervalDays to pick up newly added
/// achievements after game updates.
/// </summary>
[Export(typeof(IPlugin))]
public sealed class AutoAchievementPlugin : IPlugin, IBotModules, IBotConnection, IBot, IBotCommand2, IBotSteamClient {
	private static readonly ConcurrentDictionary<string, BotRuntime> Runtimes = new();

	public string Name => "ASF-AutoAchievement";

	public Version Version => typeof(AutoAchievementPlugin).Assembly.GetName().Version
		?? new Version(1, 0, 0, 0);

	public Task OnLoaded() {
		ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo(
			$"{Name} v{Version} loaded — every bot's library will be scanned for new achievements. See !aahelp for commands."
		);
		return Task.CompletedTask;
	}

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		PluginConfig config = PluginConfig.FromAdditionalProperties(additionalConfigProperties);
		BotRuntime runtime = Runtimes.GetOrAdd(bot.BotName, _ => new BotRuntime(bot));
		runtime.UpdateConfig(config);

		return Task.CompletedTask;
	}

	public Task<IReadOnlyCollection<ClientMsgHandler>?> OnBotSteamHandlersInit(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		BotRuntime runtime = Runtimes.GetOrAdd(bot.BotName, _ => new BotRuntime(bot));
		AchievementsHandler handler = new();
		runtime.AttachHandler(handler);

		return Task.FromResult<IReadOnlyCollection<ClientMsgHandler>?>(new ClientMsgHandler[] { handler });
	}

	public Task OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager) {
		// We don't subscribe to any SteamKit2 callbacks — pending requests
		// are matched by JobID inside AchievementsHandler.HandleMsg via TCS.
		// Method exists only because IBotSteamClient requires it.
		return Task.CompletedTask;
	}

	public async Task OnBotLoggedOn(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Runtimes.TryGetValue(bot.BotName, out BotRuntime? runtime)) {
			await runtime.StopAsync().ConfigureAwait(false);
			runtime.Start();
		}
	}

	public async Task OnBotDisconnected(Bot bot, EResult reason) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Runtimes.TryGetValue(bot.BotName, out BotRuntime? runtime)) {
			await runtime.StopAsync().ConfigureAwait(false);
		}
	}

	public Task OnBotInit(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);
		return Task.CompletedTask;
	}

	public async Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Runtimes.TryRemove(bot.BotName, out BotRuntime? runtime)) {
			await runtime.DisposeAsync().ConfigureAwait(false);
		}
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(args);

		if (args.Length == 0) {
			return null;
		}

		string cmd = args[0].ToUpperInvariant();

		BotRuntime? runtime;
		string[] tail;
		if (args.Length > 1 && TryFindRuntime(args[1], out runtime)) {
			tail = args.Skip(2).ToArray();
		} else {
			Runtimes.TryGetValue(bot.BotName, out runtime);
			tail = args.Skip(1).ToArray();
		}

		if (runtime is null) {
			return null;
		}

		return cmd switch {
			"AASHOW" or "AASTATUS" => runtime.HandleShow(),
			"AANOW" or "AASCAN" => await runtime.HandleScanNow().ConfigureAwait(false),
			"AACHECK" or "AADYNAMIC" => await runtime.HandleDynamicCheckNow().ConfigureAwait(false),
			"AAGAME" or "AAONE" => await runtime.HandleScanGame(tail).ConfigureAwait(false),
			"AASTATS" or "AASTAT" => runtime.HandleStats(tail),
			"AABLACKLIST" or "AABL" or "AABLOCK" => await runtime.HandleAddBlacklist(tail).ConfigureAwait(false),
			"AABLACKLISTREMOVE" or "AABLRM" or "AAUNBLOCK" => runtime.HandleRemoveBlacklist(tail),
			"AAINTERVAL" or "AAINT" => runtime.HandleInterval(tail),
			"AADYNAMICINTERVAL" or "AADINT" or "AADINTERVAL" => runtime.HandleDynamicInterval(tail),
			"AAPROTECTED" or "AAPROT" => runtime.HandleProtected(tail),
			"AACARDS" => runtime.HandleAllowCardFarmingToggle(),
			"AACANCEL" or "AASTOP" => runtime.HandleCancel(),
			"AATOGGLE" => runtime.HandleToggle(),
			"AAHELP" => HelpText(),
			_ => null
		};
	}

	private static bool TryFindRuntime(string botName, out BotRuntime? runtime) {
		foreach (KeyValuePair<string, BotRuntime> kvp in Runtimes) {
			if (string.Equals(kvp.Key, botName, StringComparison.OrdinalIgnoreCase)) {
				runtime = kvp.Value;
				return true;
			}
		}
		runtime = null;
		return false;
	}

	private static string HelpText() => string.Join('\n', new[] {
		"AutoAchievement commands:",
		"  aashow [bot]                              — status, current scan progress, totals",
		"  aanow [bot]                               — run a full library scan immediately (alias: aascan)",
		"  aacheck [bot]                             — dynamic check now: scan only games never seen before (alias: aadynamic)",
		"  aacancel [bot]                            — cancel the in-flight scan, AutoIdle resumes (alias: aastop)",
		"  aagame [bot] <appid|name>                 — unlock achievements for a single game",
		"  aastats [bot] [N|all]                     — per-game stats (default: all, sorted by all-time desc)",
		"  aablacklist [bot] <appid|name>            — never touch this game",
		"  aablacklistremove [bot] <appid|name>      — remove from blacklist",
		"  aainterval [bot] <days>                   — change full-scan interval in days (0 to reset to default)",
		"  aadynamicinterval [bot] <hours|off|reset> — change dynamic-check interval in hours, 'off' to disable, 'reset' to clear override (alias: aadint)",
		"  aaprotected [bot] [on|off|reset]          — runtime override for AttemptProtectedAchievements (no arg = show)",
		"  aacards [bot]                             — toggle AllowCardFarming (yield play slot to ASF card farmer)",
		"  aatoggle [bot]                            — toggle the plugin on/off at runtime",
		"  aahelp                                    — this message"
	});
}

internal sealed class BotRuntime : IAsyncDisposable {
	private const string PersistKey = "ASF.AutoAchievement.State";

	private readonly Bot _bot;
	private readonly object _gate = new();
	private readonly Dictionary<uint, string> _nameCache = new();
	private readonly HashSet<uint> _persistentBlacklist = [];

	private AchievementsHandler? _handler;
	private PluginConfig _config = new();
	private CancellationTokenSource? _cts;
	private Task? _loop;
	private DateTime? _lastScanCompletedAt;
	private uint? _scanIntervalDaysOverride;
	private bool? _enabledOverride;
	private bool? _attemptProtectedOverride;
	private bool? _allowCardFarmingOverride;
	private DateTime? _lastScanCancelledAt;
	// Duration of the most recent scan, in seconds. Used by aashow to
	// report "last scan took Xh Ym" alongside "Xd ago".
	private long _lastScanDurationSeconds;
	// Number of AppIDs targeted by the most recent scan. Used to estimate
	// "next scan will take ~Yh Zm" from per-game delay × count without
	// having to do a fresh discovery on aashow.
	private int _lastScanTargetCount;
	// Whether the most recent scan was a full library pass or a dynamic
	// new-games-only check. Display in aashow + dictates resume mode.
	private bool _lastScanWasFullScan = true;
	private bool _resumeIsFullScan = true;
	// When the most recent dynamic check ran (success or cancel). Dynamic
	// check is overdue when (now - this) >= DynamicCheckIntervalHours.
	private DateTime? _lastDynamicCheckAt;
	// Per-mode metrics — every per-game scan attributes to one bucket
	// based on the scan it was part of.
	private long _gamesScannedFullSession;
	private long _gamesScannedFullAllTime;
	private long _gamesScannedDynamicSession;
	private long _gamesScannedDynamicAllTime;
	// Runtime override for DynamicCheckIntervalHours (parallel to
	// _scanIntervalDaysOverride).
	private uint? _dynamicCheckIntervalHoursOverride;
	private CancellationTokenSource? _scanCancelCts;
	private long _totalAchievementsUnlocked;
	private bool _persistentLoaded;

	// Per-game stats. _allTimeUnlocked is persisted; _sessionUnlocked is in-memory only.
	private readonly Dictionary<uint, long> _allTimeUnlocked = new();
	private readonly Dictionary<uint, long> _sessionUnlocked = new();
	// Last known schema totals per game (so we can show "X / Y unlocked" in stats).
	private readonly Dictionary<uint, int> _schemaTotal = new();
	private readonly Dictionary<uint, int> _schemaAlreadyUnlocked = new();
	private readonly Dictionary<uint, DateTime> _lastScannedAt = new();

	// Global counters.
	private long _scansCompletedAllTime;
	private int _scansCompletedSession;
	private long _scanSecondsAllTime;
	private long _totalUptimeBaselineSeconds;
	private readonly DateTime _sessionStartedAt = DateTime.UtcNow;

	// Currently-running scan state, exposed via aashow.
	private uint? _currentScanGameID;
	private int _currentScanIndex;
	private int _currentScanTotal;
	private DateTime? _currentScanStartedAt;

	// Resume point for an interrupted scan: the AppID that was about to be
	// scanned when the previous run got cancelled (disconnect, plugin stop,
	// ASF restart, etc.). Persisted in BotDatabase. Cleared when a scan
	// completes fully, so the *next* scan after a clean run starts fresh.
	private uint? _resumeFromAppID;

	internal BotRuntime(Bot bot) {
		_bot = bot ?? throw new ArgumentNullException(nameof(bot));
	}

	internal void AttachHandler(AchievementsHandler handler) {
		_handler = handler ?? throw new ArgumentNullException(nameof(handler));
	}

	internal void UpdateConfig(PluginConfig config) {
		ArgumentNullException.ThrowIfNull(config);

		EnsurePersistentLoaded();

		bool restart;
		lock (_gate) {
			restart = !ConfigEquivalent(_config, config) && _loop is { IsCompleted: false };
			_config = config;
		}

		_bot.ArchiLogger.LogGenericInfo(
			$"AutoAchievement config: Enabled={IsEnabled(config)}, ScanIntervalDays={EffectiveScanIntervalDays(config)}, "
			+ $"DynamicCheckIntervalHours={EffectiveDynamicCheckIntervalHours(config)}, "
			+ $"InitialDelaySeconds={config.InitialDelaySeconds}, PerGameDelayMs={config.PerGameDelayMilliseconds}, "
			+ $"AttemptProtected={EffectiveAttemptProtected(config)}, AllowCardFarming={EffectiveAllowCardFarming(config)}, "
			+ $"ConfigBlacklist={config.Blacklist.Count}, PersistentBlacklist={_persistentBlacklist.Count}"
		);

		if (restart) {
			_ = Task.Run(async () => {
				await StopAsync().ConfigureAwait(false);
				Start();
			});
		}
	}

	internal void Start() {
		// Everything that touches _loop / _cts must stay inside the lock —
		// otherwise concurrent Start() calls (UpdateConfig restart task +
		// OnBotLoggedOn happening near-simultaneously after a config reload)
		// can both pass the "is loop running?" check before either assigns
		// _loop, spawning duplicate Tasks that race and cancel each other.
		bool startedNew = false;
		bool isDisabled = false;
		lock (_gate) {
			if (_loop is { IsCompleted: false }) {
				return;
			}
			if (!(_enabledOverride ?? _config.Enabled)) {
				isDisabled = true;
			} else {
				_cts = new CancellationTokenSource();
				CancellationToken token = _cts.Token;
				_loop = Task.Run(() => ScanLoopAsync(token));
				startedNew = true;
			}
		}

		if (isDisabled) {
			_bot.ArchiLogger.LogGenericInfo("AutoAchievement: disabled in config for this bot.");
		} else if (startedNew) {
			_bot.ArchiLogger.LogGenericInfo("AutoAchievement: scan loop started.");
		}
	}

	internal async Task StopAsync() {
		Task? loop;
		CancellationTokenSource? cts;
		lock (_gate) {
			loop = _loop;
			cts = _cts;
		}

		if (cts is not null) {
			try { cts.Cancel(); } catch (ObjectDisposedException) { }
		}

		if (loop is not null) {
			try { await loop.ConfigureAwait(false); } catch { }
		}

		lock (_gate) {
			if (ReferenceEquals(_cts, cts)) { _cts = null; }
			if (ReferenceEquals(_loop, loop)) { _loop = null; }
		}
		cts?.Dispose();
	}

	public async ValueTask DisposeAsync() {
		await StopAsync().ConfigureAwait(false);
	}

	// ------------------------------------------------------------------
	// Scan loop
	// ------------------------------------------------------------------

	private async Task ScanLoopAsync(CancellationToken token) {
		try {
			PluginConfig cfg;
			lock (_gate) { cfg = _config; }

			if (cfg.InitialDelaySeconds > 0) {
				await Task.Delay(TimeSpan.FromSeconds(cfg.InitialDelaySeconds), token).ConfigureAwait(false);
			}

			while (!token.IsCancellationRequested) {
				lock (_gate) { cfg = _config; }

				// Decide what kind of scan (if any) to run this iteration.
				//   - Pending resume → finish the interrupted scan in its
				//     original mode (carried in _resumeIsFullScan).
				//   - Full scan overdue (interval since last completed/
				//     cancelled scan ≥ ScanIntervalDays) → run full scan.
				//   - Else, dynamic check overdue (since last dynamic check
				//     ≥ DynamicCheckIntervalHours) → run dynamic check.
				//   - Else, sleep until the soonest of either due time.
				DateTime? lastFullEnd = EffectiveLastScanEnded();
				DateTime? lastDynamic;
				bool resumePending;
				bool resumeFull;
				lock (_gate) {
					lastDynamic = _lastDynamicCheckAt;
					resumePending = _resumeFromAppID.HasValue;
					resumeFull = _resumeIsFullScan;
				}

				TimeSpan fullInterval = TimeSpan.FromDays(EffectiveScanIntervalDays(cfg));
				uint dynamicHours = EffectiveDynamicCheckIntervalHours(cfg);
				bool dynamicEnabled = dynamicHours > 0;
				TimeSpan dynamicInterval = dynamicEnabled
					? TimeSpan.FromHours(dynamicHours)
					: TimeSpan.MaxValue;

				DateTime now = DateTime.UtcNow;
				bool fullDue = !lastFullEnd.HasValue || (now - lastFullEnd.Value) >= fullInterval;
				bool dynamicDue = dynamicEnabled
					&& (!lastDynamic.HasValue || (now - lastDynamic.Value) >= dynamicInterval);

				bool runScan = false;
				bool fullScan = true;
				if (resumePending) {
					runScan = true;
					fullScan = resumeFull;
				} else if (fullDue) {
					runScan = true;
					fullScan = true;
				} else if (dynamicDue) {
					runScan = true;
					fullScan = false;
				}

				if (runScan) {
					// Per-scan cancellation token: !aacancel signals this
					// without killing the whole loop. Linked with the loop
					// token so either source ends the scan. Mutex against
					// !aanow/!aacheck: if a manual scan is already running,
					// skip this iteration — racing two scans would stomp
					// per-game Play(appID) calls.
					CancellationTokenSource scanCts = new();
					bool gotSlot;
					lock (_gate) {
						if (_scanCancelCts is not null) {
							gotSlot = false;
						} else {
							_scanCancelCts = scanCts;
							gotSlot = true;
						}
					}
					if (!gotSlot) {
						scanCts.Dispose();
						try {
							await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
						} catch (OperationCanceledException) {
							break;
						}
						continue;
					}
					using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, scanCts.Token);

					string mode = fullScan ? "full library scan" : "dynamic check (new games only)";
					_bot.ArchiLogger.LogGenericInfo($"AutoAchievement: starting {mode}.");

					DateTime scanStartedAt = DateTime.UtcNow;
					bool fullPass = false;
					bool userCancelled = false;
					try {
						ScanResult result = await ScanLibraryAsync(cfg, linkedCts.Token, fullScan).ConfigureAwait(false);
						long elapsedSecs = (long) (DateTime.UtcNow - scanStartedAt).TotalSeconds;
						fullPass = result.FullLibraryPass;
						userCancelled = scanCts.IsCancellationRequested && !token.IsCancellationRequested;
						lock (_gate) {
							_totalAchievementsUnlocked += result.AchievementsUnlocked;
							_scanSecondsAllTime += elapsedSecs;
							_lastScanDurationSeconds = elapsedSecs;
							_lastScanWasFullScan = fullScan;
							// Every scan attempt counts as a dynamic check —
							// dynamic mode scans new games, full mode scans
							// everything (which subsumes new games). So we
							// always reset the dynamic clock.
							_lastDynamicCheckAt = DateTime.UtcNow;
							if (fullPass && fullScan) {
								// Full library pass = the configured cooldown
								// gets reset.
								_lastScanCompletedAt = DateTime.UtcNow;
								_scansCompletedAllTime++;
								_scansCompletedSession++;
							} else if (userCancelled) {
								_lastScanCancelledAt = DateTime.UtcNow;
								_resumeFromAppID = null;
							}
							SavePersistentState();
						}
						if (userCancelled) {
							_bot.ArchiLogger.LogGenericInfo($"AutoAchievement: scan cancelled by user after {FormatDuration(TimeSpan.FromSeconds(elapsedSecs))} — AutoIdle resuming. Resume point cleared; next scan starts from index 0.");
						} else {
							LogScanSummary(result, TimeSpan.FromSeconds(elapsedSecs));
						}
					} catch (OperationCanceledException) {
						if (token.IsCancellationRequested) { break; }
						userCancelled = true;
						lock (_gate) {
							_lastScanCancelledAt = DateTime.UtcNow;
							_lastDynamicCheckAt = DateTime.UtcNow;
							_resumeFromAppID = null;
							SavePersistentState();
						}
						_bot.ArchiLogger.LogGenericInfo("AutoAchievement: scan cancelled by user — AutoIdle resuming. Resume point cleared; next scan starts from index 0.");
					} catch (Exception ex) {
						_bot.ArchiLogger.LogGenericException(ex);
					} finally {
						lock (_gate) {
							if (ReferenceEquals(_scanCancelCts, scanCts)) { _scanCancelCts = null; }
						}
						scanCts.Dispose();
					}
				}

				// Sleep until the soonest of either due-event. Re-read the
				// timestamps because the just-run scan moved at least one
				// of them. Floor at 30s so we don't busy-loop after errors.
				DateTime? recheckFullEnd = EffectiveLastScanEnded();
				DateTime? recheckDynamic;
				lock (_gate) { recheckDynamic = _lastDynamicCheckAt; }
				DateTime nextFullDue = (recheckFullEnd ?? DateTime.UtcNow).Add(fullInterval);
				DateTime soonestDue = nextFullDue;
				if (dynamicEnabled) {
					DateTime nextDynamicDue = (recheckDynamic ?? DateTime.UtcNow).Add(dynamicInterval);
					if (nextDynamicDue < soonestDue) { soonestDue = nextDynamicDue; }
				}
				TimeSpan untilNext = soonestDue - DateTime.UtcNow;
				if (untilNext < TimeSpan.FromSeconds(30)) { untilNext = TimeSpan.FromSeconds(30); }

				try {
					await Task.Delay(untilNext, token).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					break;
				}
			}
		} catch (OperationCanceledException) {
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericException(ex);
		} finally {
			try { _bot.Actions.Resume(); } catch { }
			_bot.ArchiLogger.LogGenericInfo("AutoAchievement: scan loop stopped.");
		}
	}

	private async Task<ScanResult> ScanLibraryAsync(PluginConfig cfg, CancellationToken token, bool fullScan) {
		ScanResult result = new();
		result.WasFullScan = fullScan;

		// Pull the profile-games list to populate the name cache for nice
		// log output. AppIDs only present in dynamicstore (DLC, demos,
		// unplayed F2P games) won't have a name and just log as "AppID N".
		IReadOnlyDictionary<uint, string>? profile = await GameDiscovery.GetProfileGamesAsync(_bot).ConfigureAwait(false);
		if (profile is not null) {
			lock (_gate) {
				foreach (KeyValuePair<uint, string> kvp in profile) {
					if (!string.IsNullOrEmpty(kvp.Value)) {
						_nameCache[kvp.Key] = kvp.Value;
					}
				}
			}
		}

		// Target set is always the full dynamicstore — every AppID the bot
		// has access to, which catches achievements on never-played free
		// games, demos that have stat schemas, etc. If dynamicstore fails
		// we fall back to profile games for this cycle rather than returning
		// empty (otherwise an empty scan would have to be classified as
		// either "completed" or "errored" and burn the wrong amount of time).
		HashSet<uint> targetSet;
		IReadOnlyCollection<uint> all = await GameDiscovery.GetAllOwnedAppIDsAsync(_bot).ConfigureAwait(false);
		if (all.Count > 0) {
			targetSet = [.. all];
		} else if (profile is not null && profile.Count > 0) {
			_bot.ArchiLogger.LogGenericWarning("AutoAchievement: dynamicstore returned empty, falling back to profile-only games for this cycle.");
			targetSet = [.. profile.Keys];
		} else {
			return result;
		}

		// Dynamic check (fullScan=false) only scans games never seen before
		// — every AppID NOT in _lastScannedAt. Full scan covers everything.
		if (!fullScan) {
			HashSet<uint> alreadyScanned;
			lock (_gate) { alreadyScanned = [.. _lastScannedAt.Keys]; }
			targetSet.ExceptWith(alreadyScanned);
		}

		HashSet<uint> blacklist = EffectiveBlacklist(cfg);
		// Sort targets by AppID so the order is stable across runs — required
		// for resume-from-position to work correctly across reconnects.
		List<uint> targets = targetSet.Where(id => !blacklist.Contains(id))
			.OrderBy(static id => id).ToList();

		// Critical "least downtime" optimization: if dynamic check found
		// nothing new, return immediately BEFORE signalling sibling pause.
		// AutoIdle keeps playing without interruption; AA just records the
		// check happened and moves on. Full scans don't take this path —
		// they always have games to scan (the whole library).
		if (!fullScan && targets.Count == 0) {
			_bot.ArchiLogger.LogGenericInfo("AutoAchievement: dynamic check — no new games to scan, all known games already covered. AutoIdle continues uninterrupted.");
			result.FullLibraryPass = true; // trivially complete
			return result;
		}

		// Resume from where the previous run was interrupted, if applicable.
		uint? resumeFrom;
		lock (_gate) { resumeFrom = _resumeFromAppID; }

		int startIdx = 0;
		if (resumeFrom.HasValue) {
			int found = targets.IndexOf(resumeFrom.Value);
			if (found >= 0) {
				startIdx = found;
				_bot.ArchiLogger.LogGenericInfo(
					$"AutoAchievement: resuming previous interrupted scan at {found + 1}/{targets.Count} ({FormatID(resumeFrom.Value)})."
				);
			} else {
				_bot.ArchiLogger.LogGenericInfo(
					$"AutoAchievement: previous interrupted scan was at AppID {resumeFrom.Value} but it's no longer in the library. Starting from the beginning."
				);
				lock (_gate) {
					_resumeFromAppID = null;
					SavePersistentState();
				}
			}
		}

		int remaining = targets.Count - startIdx;
		string mode = fullScan ? "full library scan" : "dynamic check (new games only)";
		_bot.ArchiLogger.LogGenericInfo(
			$"AutoAchievement: {mode} — scanning {remaining} game(s) (of {targets.Count} total, skipping {blacklist.Count} blacklisted). "
			+ $"Estimated time: {FormatDuration(TimeSpan.FromMilliseconds((long) remaining * (cfg.PerGameDelayMilliseconds + 1500L)))}."
		);

		lock (_gate) {
			_currentScanIndex = startIdx;
			_currentScanTotal = targets.Count;
			_currentScanStartedAt = DateTime.UtcNow;
			_currentScanGameID = null;
			_resumeIsFullScan = fullScan;
			_lastScanTargetCount = targets.Count;
		}

		// Signal sibling plugins (currently: ASF-AutoIdle) to release the
		// bot's "playing" slot for the duration of this scan. The command is
		// silently ignored if no plugin handles it, so this is safe even when
		// AutoIdle isn't installed.
		await SignalSiblingPauseAsync().ConfigureAwait(false);

		bool completedAll = true;
		try {
			for (int i = startIdx; i < targets.Count; i++) {
				if (token.IsCancellationRequested) { completedAll = false; break; }

				uint appID = targets[i];
				int displayIdx = i + 1;

				// Persist resume point BEFORE processing this game. If the bot
				// disconnects mid-scan, the next session resumes here.
				lock (_gate) {
					_currentScanIndex = displayIdx;
					_currentScanGameID = appID;
					_resumeFromAppID = appID;
					SavePersistentState();
				}

				// User has manually launched a game on this account. Hold the
				// scan at this game until they close it — Steam won't accept
				// our Play() / stat writes for any other app while they're
				// in-game.
				if (!_bot.IsPlayingPossible) {
					await WaitWhilePlayingBlockedAsync(displayIdx, targets.Count, appID, token).ConfigureAwait(false);
					if (token.IsCancellationRequested) { completedAll = false; break; }
				}

				// Coexist with ASF's card farmer in AllowCardFarming mode.
				// Calling Play(appID) for our scan would knock the card-
				// farming game out of the play slot mid-drop, so we wait
				// for the farmer to finish before processing this game.
				// Picks back up at the same AppID once farming completes.
				if (IsCardFarmingActive(cfg)) {
					await WaitWhileCardFarmingAsync(displayIdx, targets.Count, appID, token).ConfigureAwait(false);
					if (token.IsCancellationRequested) { completedAll = false; break; }
				}

				try {
					GameScanOutcome outcome = await ScanGameAsync(appID, cfg, token).ConfigureAwait(false);
					result.GamesProcessed++;
					result.AchievementsUnlocked += outcome.Unlocked;

					// Per-mode metrics — attribute each per-game scan to
					// whichever bucket initiated this run.
					lock (_gate) {
						if (fullScan) {
							_gamesScannedFullSession++;
							_gamesScannedFullAllTime++;
						} else {
							_gamesScannedDynamicSession++;
							_gamesScannedDynamicAllTime++;
						}
					}

					if (outcome.Unlocked > 0) {
						result.GamesWithUnlocks++;
						result.Unlocks.Add((appID, outcome.Unlocked, outcome.TotalInSchema));
					}
					if (outcome.NoAchievements) { result.GamesNoAchievements++; }
					if (outcome.AlreadyComplete) { result.GamesAlreadyComplete++; }
					if (outcome.Rejected) {
						result.GamesRejected++;
						result.Rejections.Add((appID, outcome.Detail ?? "rejected"));
					}
					if (outcome.HadError) {
						result.GamesWithErrors++;
						result.Errors.Add((appID, outcome.Detail ?? "error"));
					}
				} catch (OperationCanceledException) {
					completedAll = false;
					throw;
				} catch (Exception ex) {
					_bot.ArchiLogger.LogGenericException(ex);
					result.GamesWithErrors++;
					result.Errors.Add((appID, ex.Message));
				}

				if (cfg.PerGameDelayMilliseconds > 0) {
					try {
						await Task.Delay(TimeSpan.FromMilliseconds(cfg.PerGameDelayMilliseconds), token).ConfigureAwait(false);
					} catch (OperationCanceledException) {
						completedAll = false;
						break;
					}
				}
			}
		} finally {
			lock (_gate) {
				_currentScanIndex = 0;
				_currentScanTotal = 0;
				_currentScanStartedAt = null;
				_currentScanGameID = null;

				// Only clear the resume point if we made it all the way
				// through. Cancellations (disconnect, restart, manual stop)
				// preserve _resumeFromAppID so the next run picks up there.
				if (completedAll) {
					_resumeFromAppID = null;
					SavePersistentState();
				}
			}

			// Try AutoIdle first. If it acknowledges (returns a non-empty
			// response), AutoIdle's restart will re-assert its batch via
			// Play(batch) — calling Bot.Actions.Resume() ourselves first
			// would just churn state and risk Steam ignoring the rapid
			// pause→resume→play sequence. Only fall back to Resume() when
			// AutoIdle isn't installed.
			bool autoIdleAcknowledged = await SignalSiblingResumeAsync().ConfigureAwait(false);
			if (!autoIdleAcknowledged) {
				try { _bot.Actions.Resume(); } catch { }
			}
		}

		// A "completed cycle" = walking from wherever this run started to
		// the end of the library without break / cancel. Resume runs count
		// too: across the disconnect, the prior session covered 1..startIdx
		// and this session covered startIdx..end, so together they form
		// exactly one full library cycle. Only runs that were interrupted
		// mid-walk or returned early on empty discovery don't qualify.
		result.FullLibraryPass = completedAll && targets.Count > 0;
		return result;
	}

	private async Task<GameScanOutcome> ScanGameAsync(uint appID, PluginConfig cfg, CancellationToken token) {
		AchievementsHandler? handler = _handler;
		if (handler is null) {
			return new GameScanOutcome(0, true, NoAchievements: false, AlreadyComplete: false, Rejected: false, TotalInSchema: 0, Detail: "SteamKit2 handler not attached");
		}

		// Briefly enter "playing" state so Steam will accept stat writes.
		try {
			(bool ok, string msg) = await _bot.Actions.Play(new[] { appID }).ConfigureAwait(false);
			if (!ok) {
				return new GameScanOutcome(0, true, NoAchievements: false, AlreadyComplete: false, Rejected: false, TotalInSchema: 0, Detail: $"Play call failed: {msg}");
			}
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericException(ex);
			return new GameScanOutcome(0, true, NoAchievements: false, AlreadyComplete: false, Rejected: false, TotalInSchema: 0, Detail: $"Play threw: {ex.Message}");
		}

		// Tiny delay so Steam registers the play before we hit user stats.
		try { await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false); } catch (OperationCanceledException) {
			return new GameScanOutcome(0, false, NoAchievements: false, AlreadyComplete: false, Rejected: false, TotalInSchema: 0, Detail: "cancelled");
		}

		GetUserStatsResult? get = await handler.GetUserStatsAsync(appID, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
		if (get is null) {
			return new GameScanOutcome(0, true, NoAchievements: false, AlreadyComplete: false, Rejected: false, TotalInSchema: 0, Detail: "Steam didn't respond within 20s (timed out)");
		}

		if (get.Result != EResult.OK) {
			// Steam returns Fail / Invalid for apps that don't have a user-stats
			// schema configured (i.e. no Steam achievements). Treat as "no achievements".
			// Record the snapshot anyway so dynamic checks know we've seen this
			// AppID — otherwise every dynamic check re-scans the same ~thousands
			// of DLC / soundtrack / demo / Steamworks-Redistributable entries
			// from dynamicstore, because they never make it into _lastScannedAt
			// and the filter (ExceptWith _lastScannedAt.Keys) sees them as new
			// games forever.
			RecordSchemaSnapshot(appID, total: 0, alreadyUnlocked: 0);
			return new GameScanOutcome(0, false, NoAchievements: true, AlreadyComplete: false, Rejected: false, TotalInSchema: 0, Detail: "this game has no Steam achievements");
		}

		List<AchievementBit> bits = SchemaParser.ParseAchievements(get.SchemaBytes);
		if (bits.Count == 0) {
			RecordSchemaSnapshot(appID, total: 0, alreadyUnlocked: 0);
			return new GameScanOutcome(0, false, NoAchievements: true, AlreadyComplete: false, Rejected: false, TotalInSchema: 0, Detail: "this game has no Steam achievements");
		}

		// Build the current stat-id → value map.
		Dictionary<uint, uint> currentValues = new();
		foreach ((uint statId, uint statValue) in get.Stats) {
			currentValues[statId] = statValue;
		}

		// Walk every bit in the schema. Track:
		//   totalInSchema        — every achievement the game has
		//   totalAlreadyUnlocked — already-unlocked count across ALL bits
		//   protectedSkipped     — protected bits we won't even attempt
		//   candidateCount       — locked, non-protected (or all if attempt-protected) bits we'll write back
		Dictionary<uint, uint> targetMask = new();
		int totalInSchema = 0;
		int totalAlreadyUnlocked = 0;
		int protectedSkipped = 0;
		int protectedAlreadyUnlocked = 0;
		int candidateCount = 0;
		bool attemptProtected = EffectiveAttemptProtected(cfg);

		foreach (AchievementBit bit in bits) {
			totalInSchema++;
			uint mask = 1u << bit.BitIndex;
			currentValues.TryGetValue(bit.StatID, out uint current);
			bool alreadyOn = (current & mask) != 0;
			if (alreadyOn) {
				totalAlreadyUnlocked++;
			}

			if (bit.Permission != 0 && !attemptProtected) {
				protectedSkipped++;
				if (alreadyOn) { protectedAlreadyUnlocked++; }
				continue;
			}

			if (alreadyOn) {
				continue;
			}

			candidateCount++;
			targetMask.TryGetValue(bit.StatID, out uint accum);
			targetMask[bit.StatID] = accum | mask;
		}

		if (candidateCount == 0) {
			// Nothing to write — still record the schema snapshot so stats
			// reflect "X/Y unlocked" correctly even on a no-op pass.
			RecordSchemaSnapshot(appID, totalInSchema, totalAlreadyUnlocked);

			bool fullyComplete = totalInSchema == totalAlreadyUnlocked;
			string detail = fullyComplete
				? $"already 100% complete ({totalInSchema}/{totalInSchema})"
				: $"{totalAlreadyUnlocked}/{totalInSchema} unlocked, {protectedSkipped} protected skipped, 0 candidates left";
			return new GameScanOutcome(0, false, NoAchievements: false, AlreadyComplete: fullyComplete, Rejected: false, TotalInSchema: totalInSchema, Detail: detail);
		}

		// Build the new stat values: OR the new bits into the existing values.
		List<(uint statId, uint statValue)> updates = new(targetMask.Count);
		foreach (KeyValuePair<uint, uint> kvp in targetMask) {
			currentValues.TryGetValue(kvp.Key, out uint existing);
			updates.Add((kvp.Key, existing | kvp.Value));
		}

		StoreUserStatsResult? store = await handler.StoreUserStatsAsync(appID, get.CrcStats, updates, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
		if (store is null) {
			return new GameScanOutcome(0, true, NoAchievements: false, AlreadyComplete: false, Rejected: false, TotalInSchema: totalInSchema, Detail: "StoreUserStats timed out");
		}

		if (store.Result != EResult.OK) {
			RecordSchemaSnapshot(appID, totalInSchema, totalAlreadyUnlocked);
			return new GameScanOutcome(0, false, NoAchievements: false, AlreadyComplete: false, Rejected: true, TotalInSchema: totalInSchema, Detail: $"Steam rejected the write with {store.Result} — the {candidateCount} remaining locked achievement(s) are server-validated and can't be unlocked from the client.");
		}

		// Successful unlock — credit the per-game stats and bump the schema snapshot.
		int newTotalUnlocked = totalAlreadyUnlocked + candidateCount;
		lock (_gate) {
			_allTimeUnlocked.TryGetValue(appID, out long allCount);
			_allTimeUnlocked[appID] = allCount + candidateCount;
			_sessionUnlocked.TryGetValue(appID, out long sessCount);
			_sessionUnlocked[appID] = sessCount + candidateCount;
			_lastScannedAt[appID] = DateTime.UtcNow;
			_schemaTotal[appID] = totalInSchema;
			_schemaAlreadyUnlocked[appID] = newTotalUnlocked;
		}

		string protectedNote = protectedSkipped > 0 ? $", {protectedSkipped} protected skipped" : "";
		string detailMsg = $"unlocked {candidateCount}, now {newTotalUnlocked}/{totalInSchema}{protectedNote}";
		return new GameScanOutcome(candidateCount, false, NoAchievements: false, AlreadyComplete: newTotalUnlocked == totalInSchema, Rejected: false, TotalInSchema: totalInSchema, Detail: detailMsg);
	}

	// Holds the scan at the current game while ASF reports the bot can't play
	// games — i.e. the Steam account is currently in a game launched outside
	// ASF (the user opened a title in their Steam client). Logs the stop and
	// the resume so the user sees in the log exactly where the scan paused.
	private async Task WaitWhilePlayingBlockedAsync(int idx, int total, uint appID, CancellationToken token) {
		DateTime? blockedSince = null;
		while (!token.IsCancellationRequested && !_bot.IsPlayingPossible) {
			if (blockedSince is null) {
				blockedSince = DateTime.UtcNow;
				_bot.ArchiLogger.LogGenericInfo(
					$"AutoAchievement: stopped at {idx}/{total} ({FormatID(appID)}) — user is playing a game on this account. Will resume at this game when free."
				);
			}
			try {
				await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}
		}

		if (blockedSince.HasValue && !token.IsCancellationRequested) {
			TimeSpan blockedFor = DateTime.UtcNow - blockedSince.Value;
			_bot.ArchiLogger.LogGenericInfo(
				$"AutoAchievement: user closed their game, resuming scan at {idx}/{total} ({FormatID(appID)}) (paused {FormatDuration(blockedFor)})."
			);
		}
	}

	// Pauses the scan while ASF's card farmer is using the play slot for
	// its own farming. Polls every 15s — the farmer typically finishes a
	// single game in 5-30 minutes, so we'd otherwise idle here for a while.
	private async Task WaitWhileCardFarmingAsync(int idx, int total, uint appID, CancellationToken token) {
		DateTime? blockedSince = null;
		while (!token.IsCancellationRequested) {
			bool farming;
			try { farming = _bot.CardsFarmer.NowFarming; } catch { farming = false; }
			if (!farming) { break; }

			if (blockedSince is null) {
				blockedSince = DateTime.UtcNow;
				_bot.ArchiLogger.LogGenericInfo(
					$"AutoAchievement: stopped at {idx}/{total} ({FormatID(appID)}) — ASF card farmer is active. Will resume at this game when farming completes."
				);
			}
			try {
				await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}
		}

		if (blockedSince.HasValue && !token.IsCancellationRequested) {
			TimeSpan blockedFor = DateTime.UtcNow - blockedSince.Value;
			_bot.ArchiLogger.LogGenericInfo(
				$"AutoAchievement: card farmer finished, resuming scan at {idx}/{total} ({FormatID(appID)}) (paused {FormatDuration(blockedFor)})."
			);
		}
	}

	private void RecordSchemaSnapshot(uint appID, int total, int alreadyUnlocked) {
		lock (_gate) {
			_lastScannedAt[appID] = DateTime.UtcNow;
			_schemaTotal[appID] = total;
			_schemaAlreadyUnlocked[appID] = alreadyUnlocked;
		}
	}

	// Cross-plugin coordination via ASF's command bus. Currently only
	// ASF-AutoIdle implements `idlepause` / `idleresume`; if it isn't
	// installed the dispatch returns null and we simply move on. We swallow
	// any exception so a misbehaving sibling can never break our scan.
	private async Task SignalSiblingPauseAsync() {
		try {
			// Pass our plugin name as the source so AutoIdle can attribute the
			// pause time to "ASF-AutoAchievement" in its idleshow / idlestats output.
			await _bot.Commands.Response(EAccess.Owner, "idlepause " + _bot.BotName + " ASF-AutoAchievement", _bot.SteamID).ConfigureAwait(false);
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericDebug($"AutoAchievement: idlepause signal threw — {ex.Message}");
		}
	}

	private async Task<bool> SignalSiblingResumeAsync() {
		try {
			string? response = await _bot.Commands.Response(EAccess.Owner, "idleresume " + _bot.BotName, _bot.SteamID).ConfigureAwait(false);
			// Empty / null response = no plugin handled the command (AutoIdle not installed).
			return !string.IsNullOrEmpty(response);
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericDebug($"AutoAchievement: idleresume signal threw — {ex.Message}");
			return false;
		}
	}

	private void LogScanSummary(ScanResult result, TimeSpan elapsed) {
		List<string> lines = [];
		string header = result.FullLibraryPass
			? $"AutoAchievement: scan cycle complete in {FormatDuration(elapsed)} — waiting for next interval."
			: $"AutoAchievement: scan interrupted after {FormatDuration(elapsed)} (will resume from this point next time).";
		lines.Add(header);
		lines.Add($"  Games scanned: {result.GamesProcessed}");
		lines.Add($"    - With new achievements unlocked: {result.GamesWithUnlocks}");
		lines.Add($"    - Already 100% complete: {result.GamesAlreadyComplete}");
		lines.Add($"    - No Steam achievements: {result.GamesNoAchievements}");
		if (result.GamesRejected > 0) {
			lines.Add($"    - Rejected by Steam (server-validated): {result.GamesRejected}");
		}
		if (result.GamesWithErrors > 0) {
			lines.Add($"    - Errors / timeouts: {result.GamesWithErrors}");
		}
		lines.Add($"  Achievements unlocked this scan: {result.AchievementsUnlocked}");

		if (result.Unlocks.Count > 0) {
			List<(uint AppID, int Unlocked, int Total)> sorted = result.Unlocks
				.OrderByDescending(static u => u.Unlocked).ToList();

			lines.Add("  Per-game unlocks:");
			int max = Math.Min(sorted.Count, 30);
			for (int i = 0; i < max; i++) {
				(uint id, int unlocked, int total) = sorted[i];
				string ratio = total > 0 ? $"{unlocked} → {Math.Min(total, GetSchemaUnlocked(id))}/{total}" : $"{unlocked}";
				lines.Add($"    {FormatID(id)}: {ratio}");
			}
			if (sorted.Count > max) {
				lines.Add($"    ... and {sorted.Count - max} more (run !aastats for the full list)");
			}
		}

		if (result.Rejections.Count > 0 && result.Rejections.Count <= 10) {
			lines.Add("  Rejections:");
			foreach ((uint id, string reason) in result.Rejections) {
				lines.Add($"    {FormatID(id)}: {reason}");
			}
		} else if (result.Rejections.Count > 10) {
			lines.Add($"  Rejections: {result.Rejections.Count} games (run !aastats to inspect).");
		}

		_bot.ArchiLogger.LogGenericInfo(string.Join('\n', lines));
	}

	private int GetSchemaUnlocked(uint appID) {
		lock (_gate) {
			_schemaAlreadyUnlocked.TryGetValue(appID, out int n);
			return n;
		}
	}

	// ------------------------------------------------------------------
	// Commands
	// ------------------------------------------------------------------

	internal string HandleShow() {
		PluginConfig cfg;
		DateTime? last;
		long total;
		uint? intervalOverride;
		bool? enabledOverride;
		HashSet<uint> blacklist;
		uint? scanGame;
		int scanIdx;
		int scanTotal;
		DateTime? scanStarted;
		long scansAll;
		int scansSession;
		long scanSecsAll;
		int sessionUnlocked;
		DateTime? lastCancelled;
		long lastDurSecs;
		int lastTargetCount;
		bool lastWasFull;
		DateTime? lastDynamic;
		long gFullSess, gFullAll, gDynSess, gDynAll;
		uint? dynIntervalOverride;
		lock (_gate) {
			cfg = _config;
			last = _lastScanCompletedAt;
			lastCancelled = _lastScanCancelledAt;
			total = _totalAchievementsUnlocked;
			intervalOverride = _scanIntervalDaysOverride;
			enabledOverride = _enabledOverride;
			blacklist = EffectiveBlacklist(cfg);
			scanGame = _currentScanGameID;
			scanIdx = _currentScanIndex;
			scanTotal = _currentScanTotal;
			scanStarted = _currentScanStartedAt;
			scansAll = _scansCompletedAllTime;
			scansSession = _scansCompletedSession;
			scanSecsAll = _scanSecondsAllTime;
			lastDurSecs = _lastScanDurationSeconds;
			lastTargetCount = _lastScanTargetCount;
			lastWasFull = _lastScanWasFullScan;
			lastDynamic = _lastDynamicCheckAt;
			gFullSess = _gamesScannedFullSession;
			gFullAll = _gamesScannedFullAllTime;
			gDynSess = _gamesScannedDynamicSession;
			gDynAll = _gamesScannedDynamicAllTime;
			dynIntervalOverride = _dynamicCheckIntervalHoursOverride;
			sessionUnlocked = 0;
			foreach (long v in _sessionUnlocked.Values) { sessionUnlocked += (int) v; }
		}

		List<string> lines = [];
		lines.Add($"AutoAchievement status for {_bot.BotName}:");
		lines.Add($"  Enabled: {IsEnabled(cfg)}{(enabledOverride.HasValue ? " (runtime override)" : "")}");
		lines.Add($"  Full-scan interval: every {EffectiveScanIntervalDays(cfg)} day(s){(intervalOverride.HasValue ? " (runtime override)" : "")}");
		uint effDynHours = EffectiveDynamicCheckIntervalHours(cfg);
		string dynIntervalLine = effDynHours == 0
			? "  Dynamic-check interval: disabled (full scan only)"
			: $"  Dynamic-check interval: every {effDynHours} hour(s) — scans games never seen before, no pause on AutoIdle when nothing new";
		if (dynIntervalOverride.HasValue) { dynIntervalLine += " (runtime override)"; }
		lines.Add(dynIntervalLine);
		lines.Add($"  Initial delay: {cfg.InitialDelaySeconds}s, per-game delay: {cfg.PerGameDelayMilliseconds}ms");
		bool? protOverride;
		bool? acfOverride;
		lock (_gate) {
			protOverride = _attemptProtectedOverride;
			acfOverride = _allowCardFarmingOverride;
		}
		lines.Add($"  AttemptProtectedAchievements: {EffectiveAttemptProtected(cfg)}{(protOverride.HasValue ? " (runtime override)" : "")}");
		lines.Add($"  AllowCardFarming: {EffectiveAllowCardFarming(cfg)}{(acfOverride.HasValue ? " (runtime override)" : "")}");
		lines.Add($"  Achievements unlocked: {sessionUnlocked} (this session) / {total} (all-time)");
		lines.Add($"  Scans completed: {scansSession} (this session) / {scansAll} (all-time, total scan time {FormatDuration(TimeSpan.FromSeconds(scanSecsAll))})");

		if (scanStarted.HasValue && scanTotal > 0) {
			TimeSpan elapsed = DateTime.UtcNow - scanStarted.Value;
			string current = scanGame.HasValue ? FormatID(scanGame.Value) : "(idle between games)";
			lines.Add($"  Currently scanning: {scanIdx}/{scanTotal} — {current}, started {FormatDuration(elapsed)} ago");
		}

		// Show whichever scan-end event is most recent so "next in" matches
		// the actual cooldown the loop is honouring.
		DateTime? lastEnded = null;
		string lastLabel = "completed";
		if (last.HasValue && lastCancelled.HasValue) {
			if (lastCancelled.Value > last.Value) { lastEnded = lastCancelled; lastLabel = "cancelled"; }
			else { lastEnded = last; lastLabel = "completed"; }
		} else if (last.HasValue) {
			lastEnded = last; lastLabel = "completed";
		} else if (lastCancelled.HasValue) {
			lastEnded = lastCancelled; lastLabel = "cancelled";
		}
		if (lastEnded.HasValue) {
			TimeSpan ago = DateTime.UtcNow - lastEnded.Value;
			TimeSpan nextIn = TimeSpan.FromDays(EffectiveScanIntervalDays(cfg)) - ago;
			if (nextIn < TimeSpan.Zero) { nextIn = TimeSpan.Zero; }
			string durSuffix = lastDurSecs > 0
				? $", took {FormatDuration(TimeSpan.FromSeconds(lastDurSecs))}"
				: "";
			string typeSuffix = lastWasFull ? "full scan, " : "dynamic check, ";
			lines.Add($"  Last scan: {FormatDuration(ago)} ago ({typeSuffix}{lastLabel}{durSuffix}), next full scan in: {FormatCountdown(nextIn)}");
		} else {
			lines.Add("  Last scan: never (waiting for first run)");
		}

		// Dynamic check timing — independent of the full-scan cooldown.
		// Suppress when disabled (interval = 0); the "interval: disabled"
		// line above is enough info on its own.
		if (effDynHours > 0) {
			if (lastDynamic.HasValue) {
				TimeSpan dyAgo = DateTime.UtcNow - lastDynamic.Value;
				TimeSpan dyNext = TimeSpan.FromHours(effDynHours) - dyAgo;
				if (dyNext < TimeSpan.Zero) { dyNext = TimeSpan.Zero; }
				lines.Add($"  Last dynamic check: {FormatDuration(dyAgo)} ago, next dynamic check in: {FormatCountdown(dyNext)}");
			} else {
				lines.Add("  Last dynamic check: never (waiting for first run)");
			}
		}

		// Next-scan duration estimate. Uses the most recent target count
		// (persisted) × per-game delay + ~1.5s Steam roundtrip per game.
		// Falls back gracefully if we haven't run a scan yet.
		if (lastTargetCount > 0) {
			long estimatedMs = (long) lastTargetCount * (cfg.PerGameDelayMilliseconds + 1500L);
			lines.Add($"  Next full-scan estimated duration: ~{FormatDuration(TimeSpan.FromMilliseconds(estimatedMs))} ({lastTargetCount} games × {cfg.PerGameDelayMilliseconds + 1500L}ms each — actual depends on Steam response and per-game pauses)");
		}

		// Per-mode game-scan metrics.
		lines.Add($"  Games scanned (full scans):    {gFullSess} this session / {gFullAll} all-time");
		lines.Add($"  Games scanned (dynamic check): {gDynSess} this session / {gDynAll} all-time");

		// Plugin uptime.
		long totalUptimeSecs;
		lock (_gate) {
			totalUptimeSecs = _totalUptimeBaselineSeconds + (long) (DateTime.UtcNow - _sessionStartedAt).TotalSeconds;
		}
		lines.Add($"  Plugin uptime: {FormatDuration(DateTime.UtcNow - _sessionStartedAt)} (this session) / {FormatDuration(TimeSpan.FromSeconds(totalUptimeSecs))} (all sessions)");

		lines.Add($"  Blacklist: {blacklist.Count} game(s)");
		if (blacklist.Count > 0) {
			lines.Add($"    {FormatList(blacklist)}");
		}
		return string.Join('\n', lines);
	}

	internal string HandleStats(string[] args) {
		// aastats [N|all] — every game by all-time desc by default.
		int top = int.MaxValue;
		if (args.Length > 0) {
			string raw = args[0].Trim();
			if (raw.Equals("ALL", StringComparison.OrdinalIgnoreCase)) {
				top = int.MaxValue;
			} else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0) {
				top = n;
			} else {
				return "Usage: !aastats [N|all]   — every tracked game (default), or top N.";
			}
		}

		Dictionary<uint, long> allTime;
		Dictionary<uint, long> sessionTime;
		Dictionary<uint, int> schemaTotal;
		Dictionary<uint, int> schemaUnlocked;
		Dictionary<uint, DateTime> lastScannedAt;
		long total;
		long scansAll;
		int scansSession;
		long scanSecsAll;
		long totalUptimeSecs;
		lock (_gate) {
			allTime = new Dictionary<uint, long>(_allTimeUnlocked);
			sessionTime = new Dictionary<uint, long>(_sessionUnlocked);
			schemaTotal = new Dictionary<uint, int>(_schemaTotal);
			schemaUnlocked = new Dictionary<uint, int>(_schemaAlreadyUnlocked);
			lastScannedAt = new Dictionary<uint, DateTime>(_lastScannedAt);
			total = _totalAchievementsUnlocked;
			scansAll = _scansCompletedAllTime;
			scansSession = _scansCompletedSession;
			scanSecsAll = _scanSecondsAllTime;
			totalUptimeSecs = _totalUptimeBaselineSeconds + (long) (DateTime.UtcNow - _sessionStartedAt).TotalSeconds;
		}

		if (allTime.Count == 0 && sessionTime.Count == 0) {
			return $"AutoAchievement stats for {_bot.BotName}: no games tracked yet (waiting for first scan to record results).";
		}

		long sessionTotal = 0;
		foreach (long v in sessionTime.Values) { sessionTotal += v; }

		// Order by all-time desc (with tiebreak by session desc for games never scanned in past sessions).
		HashSet<uint> universe = new(allTime.Keys);
		foreach (uint k in sessionTime.Keys) { universe.Add(k); }
		List<uint> ordered = universe.OrderByDescending(static k => k).ToList();
		ordered = universe.Select(k => (k, allTime.GetValueOrDefault(k), sessionTime.GetValueOrDefault(k)))
			.OrderByDescending(static x => x.Item2)
			.ThenByDescending(static x => x.Item3)
			.Select(static x => x.k).ToList();

		int shown = Math.Min(top, ordered.Count);

		List<string> lines = [];
		lines.Add($"AutoAchievement stats for {_bot.BotName}:");
		lines.Add(shown == ordered.Count
			? $"  Tracked games: {ordered.Count} (all listed below)"
			: $"  Tracked games: {ordered.Count} (top {shown} below)");
		lines.Add($"  Plugin uptime (this session): {FormatDuration(DateTime.UtcNow - _sessionStartedAt)}");
		lines.Add($"  Plugin uptime (all sessions): {FormatDuration(TimeSpan.FromSeconds(totalUptimeSecs))}");
		lines.Add($"  Scans completed: {scansSession} (this session) / {scansAll} (all-time)");
		lines.Add($"  Total scan time (all-time): {FormatDuration(TimeSpan.FromSeconds(scanSecsAll))}");
		lines.Add($"  Achievements unlocked (this session, summed): {sessionTotal}");
		lines.Add($"  Achievements unlocked (all-time, summed): {total}");
		lines.Add($"  Session started: {_sessionStartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
		lines.Add("");

		for (int i = 0; i < shown; i++) {
			uint id = ordered[i];
			long allCount = allTime.GetValueOrDefault(id);
			long sessCount = sessionTime.GetValueOrDefault(id);
			schemaTotal.TryGetValue(id, out int schemaTot);
			schemaUnlocked.TryGetValue(id, out int schemaUnl);
			lastScannedAt.TryGetValue(id, out DateTime lastSeen);

			string idx = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3);
			lines.Add($"  {idx}. {FormatID(id)}");

			string completion = schemaTot > 0
				? (schemaUnl >= schemaTot ? $"{schemaUnl}/{schemaTot} (complete)" : $"{schemaUnl}/{schemaTot}")
				: "schema unknown";
			string lastSeenText = lastSeen == default ? "never" : FormatDuration(DateTime.UtcNow - lastSeen) + " ago";
			lines.Add($"        all-time {allCount}, session {sessCount}, completion {completion}, last scanned {lastSeenText}");
		}

		return string.Join('\n', lines);
	}

	internal Task<string?> HandleScanNow() => HandleManualScanAsync(fullScan: true);

	internal Task<string?> HandleDynamicCheckNow() => HandleManualScanAsync(fullScan: false);

	private async Task<string?> HandleManualScanAsync(bool fullScan) {
		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		if (!IsEnabled(cfg)) {
			return "AutoAchievement is disabled for this bot.";
		}

		// Register the CTS as _scanCancelCts so !aacancel can stop this run
		// the same way it stops the loop's scheduled scan. Reject if a scan
		// is already in flight (loop or another !aanow) — racing two
		// scans would have them both calling Play(appID) and stomping on
		// each other's stat writes.
		CancellationTokenSource cts = new(TimeSpan.FromHours(6));
		lock (_gate) {
			if (_scanCancelCts is not null) {
				cts.Dispose();
				return "AutoAchievement: a scan is already running. Use !aacancel to stop it first.";
			}
			_scanCancelCts = cts;
		}

		string manualMode = fullScan ? "full library scan" : "dynamic check (new games only)";
		_bot.ArchiLogger.LogGenericInfo($"AutoAchievement: starting {manualMode} (manual trigger).");

		DateTime startedAt = DateTime.UtcNow;
		try {
			ScanResult result = await ScanLibraryAsync(cfg, cts.Token, fullScan).ConfigureAwait(false);
			TimeSpan elapsed = DateTime.UtcNow - startedAt;
			bool userCancelled = cts.IsCancellationRequested && !result.FullLibraryPass;
			lock (_gate) {
				_totalAchievementsUnlocked += result.AchievementsUnlocked;
				_scanSecondsAllTime += (long) elapsed.TotalSeconds;
				_lastScanDurationSeconds = (long) elapsed.TotalSeconds;
				_lastScanWasFullScan = fullScan;
				_lastDynamicCheckAt = DateTime.UtcNow;
				if (result.FullLibraryPass && fullScan) {
					_lastScanCompletedAt = DateTime.UtcNow;
					_scansCompletedAllTime++;
					_scansCompletedSession++;
				} else if (userCancelled) {
					_lastScanCancelledAt = DateTime.UtcNow;
					_resumeFromAppID = null;
				}
				SavePersistentState();
			}
			if (userCancelled) {
				_bot.ArchiLogger.LogGenericInfo($"AutoAchievement: scan cancelled by user after {FormatDuration(elapsed)} — AutoIdle resuming. Resume point cleared; next scan starts from index 0.");
				return $"Scan cancelled in {FormatDuration(elapsed)}.";
			}
			LogScanSummary(result, elapsed);
			string passLabel = result.FullLibraryPass ? "Scan cycle complete" : "Scan interrupted";
			return $"{passLabel} in {FormatDuration(elapsed)}. {result.AchievementsUnlocked} achievement(s) unlocked across {result.GamesWithUnlocks} game(s); see ASF log for the per-game breakdown.";
		} catch (OperationCanceledException) {
			TimeSpan elapsed = DateTime.UtcNow - startedAt;
			lock (_gate) {
				_lastScanCancelledAt = DateTime.UtcNow;
				_resumeFromAppID = null;
				SavePersistentState();
			}
			_bot.ArchiLogger.LogGenericInfo($"AutoAchievement: scan cancelled by user after {FormatDuration(elapsed)} — AutoIdle resuming.");
			return $"Scan cancelled in {FormatDuration(elapsed)}.";
		} finally {
			lock (_gate) {
				if (ReferenceEquals(_scanCancelCts, cts)) { _scanCancelCts = null; }
			}
			cts.Dispose();
		}
	}

	internal async Task<string?> HandleScanGame(string[] args) {
		if (args.Length == 0) {
			return "Usage: !aagame <appid|name>";
		}
		string target = string.Join(' ', args).Trim().Trim('"');
		uint? appID = await ResolveAppIDAsync(target).ConfigureAwait(false);
		if (!appID.HasValue) {
			return $"Couldn't find a game matching '{target}' in this bot's library. Try the AppID number instead.";
		}

		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
		try {
			GameScanOutcome outcome = await ScanGameAsync(appID.Value, cfg, cts.Token).ConfigureAwait(false);
			lock (_gate) {
				_totalAchievementsUnlocked += outcome.Unlocked;
				SavePersistentState();
			}
			try { _bot.Actions.Resume(); } catch { }

			string detailSuffix = string.IsNullOrEmpty(outcome.Detail) ? "" : $" — {outcome.Detail}";
			return outcome.Unlocked > 0
				? $"{FormatID(appID.Value)}: unlocked {outcome.Unlocked} achievement(s){detailSuffix}."
				: outcome.HadError
					? $"{FormatID(appID.Value)}: no achievements unlocked{detailSuffix}."
					: $"{FormatID(appID.Value)}: nothing to do{detailSuffix}.";
		} catch (Exception ex) {
			return $"Error scanning {FormatID(appID.Value)}: {ex.Message}";
		}
	}

	internal async Task<string?> HandleAddBlacklist(string[] args) {
		if (args.Length == 0) {
			return "Usage: !aablacklist <appid|name>";
		}
		string target = string.Join(' ', args).Trim().Trim('"');
		uint? appID = await ResolveAppIDAsync(target).ConfigureAwait(false);
		if (!appID.HasValue) {
			return $"Couldn't find a game matching '{target}' in this bot's library. Try the AppID number instead.";
		}

		bool added;
		lock (_gate) {
			added = _persistentBlacklist.Add(appID.Value);
			if (added) { SavePersistentState(); }
		}

		string formatted = FormatID(appID.Value);
		return added
			? $"Added {formatted} to the blacklist. It won't be scanned anymore."
			: $"{formatted} is already in the blacklist.";
	}

	internal string HandleRemoveBlacklist(string[] args) {
		if (args.Length == 0) {
			return "Usage: !aablacklistremove <appid|name>";
		}
		string target = string.Join(' ', args).Trim().Trim('"');
		uint? appID = TryParseAppID(target) ?? FindByName(target);
		if (!appID.HasValue) {
			return $"Couldn't find '{target}'. Pass the AppID number to remove an entry whose name isn't cached.";
		}

		bool removed;
		lock (_gate) {
			removed = _persistentBlacklist.Remove(appID.Value);
			if (removed) { SavePersistentState(); }
		}

		string formatted = FormatID(appID.Value);
		return removed
			? $"Removed {formatted} from the blacklist."
			: $"{formatted} wasn't in the runtime blacklist (config-defined entries must be removed in JSON).";
	}

	internal string HandleInterval(string[] args) {
		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		if (args.Length == 0) {
			return $"Scan interval: {EffectiveScanIntervalDays(cfg)} day(s)\nUsage: !aainterval <days>   (0 to reset to default)";
		}

		string raw = args[0].Trim();
		if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint days)) {
			return $"'{raw}' is not a number. Pass a positive integer of days (or 0 to reset).";
		}

		if (days == 0) {
			lock (_gate) {
				_scanIntervalDaysOverride = null;
				SavePersistentState();
			}
			return $"Reset. Scan interval is now {cfg.ScanIntervalDays} day(s).";
		}

		lock (_gate) {
			_scanIntervalDaysOverride = days;
			SavePersistentState();
		}
		return $"Scan interval is now {days} day(s). The change applies on the next sleep cycle.";
	}

	// Change the dynamic-check interval at runtime. Two special argument
	// forms: no arg = show, "off"/"disable"/"0" = disable dynamic checks
	// entirely (full scan only). Any positive integer = hours between
	// dynamic checks.
	internal string HandleDynamicInterval(string[] args) {
		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		if (args.Length == 0) {
			uint cur = EffectiveDynamicCheckIntervalHours(cfg);
			string state = cur == 0 ? "disabled (full scan only)" : $"every {cur} hour(s)";
			return $"Dynamic-check interval: {state}\nUsage: !aadynamicinterval <hours>   (0 or 'off' to disable, 'reset' to clear runtime override)";
		}

		string raw = args[0].Trim();

		if (raw.Equals("RESET", StringComparison.OrdinalIgnoreCase)) {
			lock (_gate) {
				_dynamicCheckIntervalHoursOverride = null;
				SavePersistentState();
			}
			uint deflt = cfg.DynamicCheckIntervalHours;
			string state = deflt == 0 ? "disabled (full scan only)" : $"{deflt} hour(s)";
			return $"Reset. Dynamic-check interval is now {state} (from JSON config).";
		}

		uint hours;
		if (raw.Equals("OFF", StringComparison.OrdinalIgnoreCase) || raw.Equals("DISABLE", StringComparison.OrdinalIgnoreCase) || raw.Equals("DISABLED", StringComparison.OrdinalIgnoreCase)) {
			hours = 0;
		} else if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)) {
			return $"'{raw}' is not a valid value. Pass a non-negative integer of hours, 'off' to disable, or 'reset' to clear the override.";
		}

		lock (_gate) {
			_dynamicCheckIntervalHoursOverride = hours;
			SavePersistentState();
		}
		if (hours == 0) {
			return "Dynamic-check interval is now disabled (full scan only). The change applies on the next sleep cycle.";
		}
		return $"Dynamic-check interval is now every {hours} hour(s). The change applies on the next sleep cycle.";
	}

	// Cancel an in-flight scan and let AutoIdle take back the play slot. The
	// loop's main token stays alive; only the per-scan CTS gets cancelled, so
	// the loop keeps running and will respect the configured cooldown before
	// the next scan. If no scan is currently running, this is a no-op.
	internal string HandleCancel() {
		CancellationTokenSource? cts;
		lock (_gate) { cts = _scanCancelCts; }
		if (cts is null) {
			return "AutoAchievement: no scan currently running.";
		}
		try { cts.Cancel(); } catch (ObjectDisposedException) { }
		return "AutoAchievement: scan cancellation requested. AutoIdle will resume shortly.";
	}

	internal string HandleToggle() {
		PluginConfig cfg;
		bool current;
		bool newValue;
		lock (_gate) {
			cfg = _config;
			current = IsEnabled(cfg);
			newValue = !current;
			_enabledOverride = newValue;
			SavePersistentState();
		}

		_ = Task.Run(async () => {
			await StopAsync().ConfigureAwait(false);
			if (newValue) { Start(); }
		});
		return $"Enabled = {newValue} (runtime override). Scan loop {(newValue ? "starting" : "stopping")} now.";
	}

	internal string HandleAllowCardFarmingToggle() {
		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		bool current = EffectiveAllowCardFarming(cfg);
		bool newValue = !current;

		lock (_gate) {
			_allowCardFarmingOverride = newValue;
			SavePersistentState();
		}

		return newValue
			? "AllowCardFarming is now true (runtime override). Scans will pause when ASF's card farmer is active and resume when it finishes."
			: "AllowCardFarming is now false (runtime override). Scans will run unconditionally and may interrupt card farming.";
	}

	internal string HandleProtected(string[] args) {
		PluginConfig cfg;
		bool? current;
		lock (_gate) {
			cfg = _config;
			current = _attemptProtectedOverride;
		}

		bool effective = EffectiveAttemptProtected(cfg);
		string source = current.HasValue ? "runtime override" : "JSON config";

		if (args.Length == 0) {
			return $"AttemptProtectedAchievements: {effective} (source: {source}; JSON default: {cfg.AttemptProtectedAchievements})\n"
				+ "Usage: !aaprotected <on|off|reset>   — runtime override (reset to fall back to JSON config)";
		}

		string action = args[0].Trim().ToUpperInvariant();
		bool? newValue = action switch {
			"ON" or "TRUE" or "1" or "ENABLE" or "ENABLED" => true,
			"OFF" or "FALSE" or "0" or "DISABLE" or "DISABLED" => false,
			"RESET" or "CLEAR" or "DEFAULT" or "AUTO" => (bool?) null,
			_ => current  // sentinel: unknown action
		};

		if (action != "ON" && action != "TRUE" && action != "1" && action != "ENABLE" && action != "ENABLED"
			&& action != "OFF" && action != "FALSE" && action != "0" && action != "DISABLE" && action != "DISABLED"
			&& action != "RESET" && action != "CLEAR" && action != "DEFAULT" && action != "AUTO") {
			return $"Unknown value '{args[0]}'. Use on / off / reset.";
		}

		lock (_gate) {
			_attemptProtectedOverride = newValue;
			SavePersistentState();
		}

		bool nowEffective = EffectiveAttemptProtected(cfg);
		return newValue.HasValue
			? $"AttemptProtectedAchievements override = {newValue.Value} (was: {effective}). Applies on next scan."
			: $"AttemptProtectedAchievements override cleared. Falling back to JSON config: {nowEffective}.";
	}


	// ------------------------------------------------------------------
	// Helpers
	// ------------------------------------------------------------------

	private bool IsEnabled(PluginConfig cfg) {
		lock (_gate) {
			return _enabledOverride ?? cfg.Enabled;
		}
	}

	private bool EffectiveAttemptProtected(PluginConfig cfg) {
		lock (_gate) {
			return _attemptProtectedOverride ?? cfg.AttemptProtectedAchievements;
		}
	}

	private bool EffectiveAllowCardFarming(PluginConfig cfg) {
		lock (_gate) {
			return _allowCardFarmingOverride ?? cfg.AllowCardFarming;
		}
	}

	// Returns true iff (a) we're in coexist mode (AllowCardFarming=true)
	// AND (b) ASF's card farmer is actively farming a game right now.
	// In that state ScanLibraryAsync's per-game loop should defer before
	// calling Play(appID) — otherwise we'd kick the card-farming game
	// out of the play slot mid-drop.
	private bool IsCardFarmingActive(PluginConfig cfg) {
		if (!EffectiveAllowCardFarming(cfg)) { return false; }
		try {
			return _bot.CardsFarmer.NowFarming;
		} catch {
			return false;
		}
	}

	private uint EffectiveScanIntervalDays(PluginConfig cfg) {
		uint? overrideValue;
		lock (_gate) { overrideValue = _scanIntervalDaysOverride; }
		uint chosen = overrideValue ?? cfg.ScanIntervalDays;
		return Math.Max(1u, chosen);
	}

	private uint EffectiveDynamicCheckIntervalHours(PluginConfig cfg) {
		uint? overrideValue;
		lock (_gate) { overrideValue = _dynamicCheckIntervalHoursOverride; }
		// 0 is a valid value here — it disables dynamic checks entirely.
		// We don't floor at 1 like full-scan-days because there's no
		// reason to forbid "off".
		return overrideValue ?? cfg.DynamicCheckIntervalHours;
	}

	// Last time a scan ended for any reason — completion or user-cancel.
	// Cooldown gating is anchored on this so that cancelling a scan still
	// pushes the next one out by a full interval (otherwise the loop would
	// just retry within seconds and ignore the user's intent).
	private DateTime? EffectiveLastScanEnded() {
		lock (_gate) {
			DateTime? a = _lastScanCompletedAt;
			DateTime? b = _lastScanCancelledAt;
			if (a.HasValue && b.HasValue) { return a.Value > b.Value ? a : b; }
			return a ?? b;
		}
	}

	private HashSet<uint> EffectiveBlacklist(PluginConfig cfg) {
		HashSet<uint> result = [.. cfg.Blacklist];
		lock (_gate) {
			result.UnionWith(_persistentBlacklist);
		}
		return result;
	}

	private string FormatID(uint appID) {
		string name;
		lock (_gate) {
			_nameCache.TryGetValue(appID, out name!);
		}
		return string.IsNullOrEmpty(name) ? $"AppID {appID}" : $"{name} ({appID})";
	}

	private string FormatList(IEnumerable<uint> ids) =>
		string.Join(", ", ids.Select(FormatID));

	private static string FormatDuration(TimeSpan d) {
		if (d < TimeSpan.Zero) { d = TimeSpan.Zero; }
		if (d.TotalDays >= 1) { return ((int) d.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + d.Hours.ToString(CultureInfo.InvariantCulture) + "h"; }
		if (d.TotalHours >= 1) { return ((int) d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + d.Minutes.ToString(CultureInfo.InvariantCulture) + "m"; }
		if (d.TotalMinutes >= 1) { return ((int) d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " + d.Seconds.ToString(CultureInfo.InvariantCulture) + "s"; }
		return ((int) d.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
	}

	// Full-resolution countdown for "time until next scan" displays — always
	// "Xd Yh Zm Ws" so glancing at aashow gives an exact figure regardless
	// of magnitude. Used by aashow's "next in" and the loop's cooldown log
	// line, NOT by FormatDuration's other callers (uptime, scan elapsed,
	// etc.) where the auto-collapsing format is fine.
	private static string FormatCountdown(TimeSpan d) {
		if (d < TimeSpan.Zero) { d = TimeSpan.Zero; }
		int days = (int) d.TotalDays;
		int hours = d.Hours;
		int mins = d.Minutes;
		int secs = d.Seconds;
		return days.ToString(CultureInfo.InvariantCulture) + "d "
			+ hours.ToString(CultureInfo.InvariantCulture) + "h "
			+ mins.ToString(CultureInfo.InvariantCulture) + "m "
			+ secs.ToString(CultureInfo.InvariantCulture) + "s";
	}

	private async Task<uint?> ResolveAppIDAsync(string input) {
		uint? parsed = TryParseAppID(input);
		if (parsed.HasValue) { return parsed; }

		uint? cached = FindByName(input);
		if (cached.HasValue) { return cached; }

		IReadOnlyDictionary<uint, string>? owned = await GameDiscovery.GetProfileGamesAsync(_bot).ConfigureAwait(false);
		if (owned is not null) {
			lock (_gate) {
				foreach (KeyValuePair<uint, string> kvp in owned) {
					if (!string.IsNullOrEmpty(kvp.Value)) {
						_nameCache[kvp.Key] = kvp.Value;
					}
				}
			}
			return FindByName(input);
		}
		return null;
	}

	private static uint? TryParseAppID(string input) =>
		uint.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) && id > 0 ? id : null;

	private uint? FindByName(string input) {
		lock (_gate) {
			foreach (KeyValuePair<uint, string> kvp in _nameCache) {
				if (string.Equals(kvp.Value, input, StringComparison.OrdinalIgnoreCase)) {
					return kvp.Key;
				}
			}
			foreach (KeyValuePair<uint, string> kvp in _nameCache) {
				if (kvp.Value.Contains(input, StringComparison.OrdinalIgnoreCase)) {
					return kvp.Key;
				}
			}
		}
		return null;
	}


	// ------------------------------------------------------------------
	// Persistent state via BotDatabase
	// ------------------------------------------------------------------

	private void EnsurePersistentLoaded() {
		lock (_gate) {
			if (_persistentLoaded) { return; }
			_persistentLoaded = true;
		}

		try {
			JsonElement state = _bot.BotDatabase.LoadFromJsonStorage(PersistKey);
			if (state.ValueKind != JsonValueKind.Object) { return; }

			lock (_gate) {
				if (TryGetProp(state, "blacklist", out JsonElement bl) && bl.ValueKind == JsonValueKind.Array) {
					foreach (JsonElement el in bl.EnumerateArray()) {
						if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint id) && id > 0) {
							_persistentBlacklist.Add(id);
						}
					}
				}
				if (TryGetProp(state, "scanIntervalDaysOverride", out JsonElement intvl)
					&& intvl.ValueKind == JsonValueKind.Number
					&& intvl.TryGetUInt32(out uint d) && d > 0) {
					_scanIntervalDaysOverride = d;
				}
				if (TryGetProp(state, "enabledOverride", out JsonElement en)) {
					if (en.ValueKind == JsonValueKind.True) { _enabledOverride = true; } else if (en.ValueKind == JsonValueKind.False) { _enabledOverride = false; }
				}
				if (TryGetProp(state, "attemptProtectedOverride", out JsonElement prot)) {
					if (prot.ValueKind == JsonValueKind.True) { _attemptProtectedOverride = true; } else if (prot.ValueKind == JsonValueKind.False) { _attemptProtectedOverride = false; }
				}
				if (TryGetProp(state, "allowCardFarmingOverride", out JsonElement acf)) {
					if (acf.ValueKind == JsonValueKind.True) { _allowCardFarmingOverride = true; } else if (acf.ValueKind == JsonValueKind.False) { _allowCardFarmingOverride = false; }
				}
				if (TryGetProp(state, "resumeFromAppID", out JsonElement resEl)
					&& resEl.ValueKind == JsonValueKind.Number
					&& resEl.TryGetUInt32(out uint resId)
					&& resId > 0) {
					_resumeFromAppID = resId;
				}
				if (TryGetProp(state, "totalAchievementsUnlocked", out JsonElement tot)
					&& tot.ValueKind == JsonValueKind.Number
					&& tot.TryGetInt64(out long t) && t >= 0) {
					_totalAchievementsUnlocked = t;
				}
				if (TryGetProp(state, "lastScanCompletedAt", out JsonElement last) && last.ValueKind == JsonValueKind.String) {
					string? raw = last.GetString();
					if (!string.IsNullOrEmpty(raw)
						&& DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)) {
						_lastScanCompletedAt = parsed;
					}
				}
				if (TryGetProp(state, "lastScanCancelledAt", out JsonElement lastCanc) && lastCanc.ValueKind == JsonValueKind.String) {
					string? raw = lastCanc.GetString();
					if (!string.IsNullOrEmpty(raw)
						&& DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)) {
						_lastScanCancelledAt = parsed;
					}
				}
				if (TryGetProp(state, "scansCompletedAllTime", out JsonElement scAll)
					&& scAll.ValueKind == JsonValueKind.Number && scAll.TryGetInt64(out long sa) && sa >= 0) {
					_scansCompletedAllTime = sa;
				}
				if (TryGetProp(state, "scanSecondsAllTime", out JsonElement scSec)
					&& scSec.ValueKind == JsonValueKind.Number && scSec.TryGetInt64(out long ss) && ss >= 0) {
					_scanSecondsAllTime = ss;
				}
				if (TryGetProp(state, "lastScanDurationSeconds", out JsonElement lds)
					&& lds.ValueKind == JsonValueKind.Number && lds.TryGetInt64(out long ld) && ld >= 0) {
					_lastScanDurationSeconds = ld;
				}
				if (TryGetProp(state, "lastScanTargetCount", out JsonElement ltc)
					&& ltc.ValueKind == JsonValueKind.Number && ltc.TryGetInt32(out int lt) && lt >= 0) {
					_lastScanTargetCount = lt;
				}
				if (TryGetProp(state, "lastScanWasFullScan", out JsonElement lwf)) {
					if (lwf.ValueKind == JsonValueKind.True) { _lastScanWasFullScan = true; } else if (lwf.ValueKind == JsonValueKind.False) { _lastScanWasFullScan = false; }
				}
				if (TryGetProp(state, "resumeIsFullScan", out JsonElement rfs)) {
					if (rfs.ValueKind == JsonValueKind.True) { _resumeIsFullScan = true; } else if (rfs.ValueKind == JsonValueKind.False) { _resumeIsFullScan = false; }
				}
				if (TryGetProp(state, "lastDynamicCheckAt", out JsonElement ldc) && ldc.ValueKind == JsonValueKind.String) {
					string? raw = ldc.GetString();
					if (!string.IsNullOrEmpty(raw)
						&& DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)) {
						_lastDynamicCheckAt = parsed;
					}
				}
				if (TryGetProp(state, "gamesScannedFullAllTime", out JsonElement gfa)
					&& gfa.ValueKind == JsonValueKind.Number && gfa.TryGetInt64(out long gfat) && gfat >= 0) {
					_gamesScannedFullAllTime = gfat;
				}
				if (TryGetProp(state, "gamesScannedDynamicAllTime", out JsonElement gda)
					&& gda.ValueKind == JsonValueKind.Number && gda.TryGetInt64(out long gdat) && gdat >= 0) {
					_gamesScannedDynamicAllTime = gdat;
				}
				if (TryGetProp(state, "dynamicCheckIntervalHoursOverride", out JsonElement dchi)
					&& dchi.ValueKind == JsonValueKind.Number && dchi.TryGetUInt32(out uint dchih) && dchih > 0) {
					_dynamicCheckIntervalHoursOverride = dchih;
				}
				if (TryGetProp(state, "totalUptimeSeconds", out JsonElement upt)
					&& upt.ValueKind == JsonValueKind.Number && upt.TryGetInt64(out long uptSecs) && uptSecs >= 0) {
					_totalUptimeBaselineSeconds = uptSecs;
				}
				if (TryGetProp(state, "allTimeUnlocked", out JsonElement allEl) && allEl.ValueKind == JsonValueKind.Object) {
					foreach (JsonProperty prop in allEl.EnumerateObject()) {
						if (uint.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint appID) && appID > 0
							&& prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out long count) && count >= 0) {
							_allTimeUnlocked[appID] = count;
						}
					}
				}
				if (TryGetProp(state, "schemaTotal", out JsonElement schTotEl) && schTotEl.ValueKind == JsonValueKind.Object) {
					foreach (JsonProperty prop in schTotEl.EnumerateObject()) {
						if (uint.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint appID) && appID > 0
							&& prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int n) && n >= 0) {
							_schemaTotal[appID] = n;
						}
					}
				}
				if (TryGetProp(state, "schemaAlreadyUnlocked", out JsonElement schUnlEl) && schUnlEl.ValueKind == JsonValueKind.Object) {
					foreach (JsonProperty prop in schUnlEl.EnumerateObject()) {
						if (uint.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint appID) && appID > 0
							&& prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int n) && n >= 0) {
							_schemaAlreadyUnlocked[appID] = n;
						}
					}
				}
				if (TryGetProp(state, "lastScannedAt", out JsonElement lsEl) && lsEl.ValueKind == JsonValueKind.Object) {
					foreach (JsonProperty prop in lsEl.EnumerateObject()) {
						if (uint.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint appID) && appID > 0
							&& prop.Value.ValueKind == JsonValueKind.String) {
							string? raw = prop.Value.GetString();
							if (!string.IsNullOrEmpty(raw)
								&& DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)) {
								_lastScannedAt[appID] = parsed;
							}
						}
					}
				}
			}
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericException(ex);
		}
	}

	private void SavePersistentState() {
		// Caller holds _gate. Hand-rolled JSON to avoid trimmed reflection paths.
		string blacklistCsv = string.Join(",", _persistentBlacklist.Select(static x => x.ToString(CultureInfo.InvariantCulture)));
		string intervalPart = _scanIntervalDaysOverride.HasValue
			? ",\"scanIntervalDaysOverride\":" + _scanIntervalDaysOverride.Value.ToString(CultureInfo.InvariantCulture)
			: "";
		string enabledPart = _enabledOverride.HasValue
			? ",\"enabledOverride\":" + (_enabledOverride.Value ? "true" : "false")
			: "";
		string protectedPart = _attemptProtectedOverride.HasValue
			? ",\"attemptProtectedOverride\":" + (_attemptProtectedOverride.Value ? "true" : "false")
			: "";
		string allowFarmPart = _allowCardFarmingOverride.HasValue
			? ",\"allowCardFarmingOverride\":" + (_allowCardFarmingOverride.Value ? "true" : "false")
			: "";
		string resumePart = _resumeFromAppID.HasValue
			? ",\"resumeFromAppID\":" + _resumeFromAppID.Value.ToString(CultureInfo.InvariantCulture)
			: "";
		string totalPart = ",\"totalAchievementsUnlocked\":" + _totalAchievementsUnlocked.ToString(CultureInfo.InvariantCulture);
		string lastPart = _lastScanCompletedAt.HasValue
			? ",\"lastScanCompletedAt\":\"" + _lastScanCompletedAt.Value.ToString("o", CultureInfo.InvariantCulture) + "\""
			: "";
		string lastCancelPart = _lastScanCancelledAt.HasValue
			? ",\"lastScanCancelledAt\":\"" + _lastScanCancelledAt.Value.ToString("o", CultureInfo.InvariantCulture) + "\""
			: "";

		string scansAllPart = ",\"scansCompletedAllTime\":" + _scansCompletedAllTime.ToString(CultureInfo.InvariantCulture);
		string scanSecsPart = ",\"scanSecondsAllTime\":" + _scanSecondsAllTime.ToString(CultureInfo.InvariantCulture)
			+ ",\"lastScanDurationSeconds\":" + _lastScanDurationSeconds.ToString(CultureInfo.InvariantCulture)
			+ ",\"lastScanTargetCount\":" + _lastScanTargetCount.ToString(CultureInfo.InvariantCulture)
			+ ",\"lastScanWasFullScan\":" + (_lastScanWasFullScan ? "true" : "false")
			+ ",\"resumeIsFullScan\":" + (_resumeIsFullScan ? "true" : "false")
			+ (_lastDynamicCheckAt.HasValue
				? ",\"lastDynamicCheckAt\":\"" + _lastDynamicCheckAt.Value.ToString("o", CultureInfo.InvariantCulture) + "\""
				: "")
			+ ",\"gamesScannedFullAllTime\":" + _gamesScannedFullAllTime.ToString(CultureInfo.InvariantCulture)
			+ ",\"gamesScannedDynamicAllTime\":" + _gamesScannedDynamicAllTime.ToString(CultureInfo.InvariantCulture)
			+ (_dynamicCheckIntervalHoursOverride.HasValue
				? ",\"dynamicCheckIntervalHoursOverride\":" + _dynamicCheckIntervalHoursOverride.Value.ToString(CultureInfo.InvariantCulture)
				: "");

		long totalUptime = _totalUptimeBaselineSeconds + (long) (DateTime.UtcNow - _sessionStartedAt).TotalSeconds;
		string uptimePart = ",\"totalUptimeSeconds\":" + totalUptime.ToString(CultureInfo.InvariantCulture);

		string allTimeMap = SerializeUintLongMap(_allTimeUnlocked);
		string schemaTotalMap = SerializeUintIntMap(_schemaTotal);
		string schemaUnlockedMap = SerializeUintIntMap(_schemaAlreadyUnlocked);
		string lastScannedMap = SerializeUintDateMap(_lastScannedAt);

		string json = "{\"blacklist\":[" + blacklistCsv + "]"
			+ intervalPart + enabledPart + protectedPart + allowFarmPart + resumePart + totalPart + lastPart + lastCancelPart
			+ scansAllPart + scanSecsPart + uptimePart
			+ ",\"allTimeUnlocked\":" + allTimeMap
			+ ",\"schemaTotal\":" + schemaTotalMap
			+ ",\"schemaAlreadyUnlocked\":" + schemaUnlockedMap
			+ ",\"lastScannedAt\":" + lastScannedMap
			+ "}";

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement clone = doc.RootElement.Clone();
			_bot.BotDatabase.SaveToJsonStorage(PersistKey, clone);
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericException(ex);
		}
	}

	private static string SerializeUintLongMap(Dictionary<uint, long> map) {
		StringBuilder sb = new();
		sb.Append("{");
		bool first = true;
		foreach (KeyValuePair<uint, long> kvp in map) {
			if (!first) { sb.Append(","); }
			sb.Append("\""); sb.Append(kvp.Key.ToString(CultureInfo.InvariantCulture)); sb.Append("\":");
			sb.Append(kvp.Value.ToString(CultureInfo.InvariantCulture));
			first = false;
		}
		sb.Append("}");
		return sb.ToString();
	}

	private static string SerializeUintIntMap(Dictionary<uint, int> map) {
		StringBuilder sb = new();
		sb.Append("{");
		bool first = true;
		foreach (KeyValuePair<uint, int> kvp in map) {
			if (!first) { sb.Append(","); }
			sb.Append("\""); sb.Append(kvp.Key.ToString(CultureInfo.InvariantCulture)); sb.Append("\":");
			sb.Append(kvp.Value.ToString(CultureInfo.InvariantCulture));
			first = false;
		}
		sb.Append("}");
		return sb.ToString();
	}

	private static string SerializeUintDateMap(Dictionary<uint, DateTime> map) {
		StringBuilder sb = new();
		sb.Append("{");
		bool first = true;
		foreach (KeyValuePair<uint, DateTime> kvp in map) {
			if (!first) { sb.Append(","); }
			sb.Append("\""); sb.Append(kvp.Key.ToString(CultureInfo.InvariantCulture)); sb.Append("\":\"");
			sb.Append(kvp.Value.ToString("o", CultureInfo.InvariantCulture)); sb.Append("\"");
			first = false;
		}
		sb.Append("}");
		return sb.ToString();
	}

	private static string EscapeJsonString(string s) {
		StringBuilder sb = new(s.Length + 8);
		foreach (char c in s) {
			switch (c) {
				case '\\': sb.Append("\\\\"); break;
				case '"': sb.Append("\\\""); break;
				case '\b': sb.Append("\\b"); break;
				case '\f': sb.Append("\\f"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (c < 0x20) {
						sb.Append("\\u");
						sb.Append(((int) c).ToString("x4", CultureInfo.InvariantCulture));
					} else {
						sb.Append(c);
					}
					break;
			}
		}
		return sb.ToString();
	}

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

	private static bool ConfigEquivalent(PluginConfig a, PluginConfig b) =>
		a.Enabled == b.Enabled
		&& a.ScanIntervalDays == b.ScanIntervalDays
		&& a.DynamicCheckIntervalHours == b.DynamicCheckIntervalHours
		&& a.InitialDelaySeconds == b.InitialDelaySeconds
		&& a.PerGameDelayMilliseconds == b.PerGameDelayMilliseconds
		&& a.AttemptProtectedAchievements == b.AttemptProtectedAchievements
		&& a.AllowCardFarming == b.AllowCardFarming
		&& a.Blacklist.SetEquals(b.Blacklist);

	private sealed class ScanResult {
		internal int GamesProcessed;
		internal int GamesWithUnlocks;
		internal int GamesWithErrors;
		internal int GamesNoAchievements;
		internal int GamesAlreadyComplete;
		internal int GamesRejected;
		internal int AchievementsUnlocked;
		// True only when this run started from index 0 AND walked every game
		// in the library without being cancelled / breaking out. Resume scans
		// (picking up from a prior session's interrupted point), empty-pool
		// returns, and cancellations all leave this false so the "scans
		// completed" counter only reflects genuine full library passes.
		internal bool FullLibraryPass;
		// Whether this scan was invoked in full-library mode (true) or
		// dynamic check-for-new-games mode (false). Used by the loop to
		// distinguish "completed full scan" (resets the 7-day cooldown)
		// from "completed dynamic check" (resets only the dynamic timer).
		internal bool WasFullScan = true;
		internal readonly List<(uint AppID, int Unlocked, int Total)> Unlocks = new();
		internal readonly List<(uint AppID, string Reason)> Rejections = new();
		internal readonly List<(uint AppID, string Reason)> Errors = new();
	}

	private readonly record struct GameScanOutcome(
		int Unlocked,
		bool HadError,
		bool NoAchievements,
		bool AlreadyComplete,
		bool Rejected,
		int TotalInSchema,
		string? Detail = null
	);
}
