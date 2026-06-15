# ASF-AutoAchievement

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) plugin that **auto-detects every game your account has access to and unlocks all available achievements** - like [Steam Achievement Manager](https://github.com/gibbed/SteamAchievementManager) (SAM) Picker, but fully automated, recurring, and remote (no Steam client running).

No Steam Web API key. No game IDs to enter. Drop the DLL in `plugins/`, set `"AutoAchievement": { "Enabled": true }` in your bot config, restart ASF.

## How it works

Two scan modes work together:

**Full library scan** (every `ScanIntervalDays`, default 7 days):
1. Enumerate every AppID the account has access to via the store `dynamicstore/userdata` endpoint. This includes paid games, played free-to-play, **unplayed F2P claims**, demos with stat schemas - basically every AppID that could possibly have achievements.
2. For each AppID (skipping the blacklist):
   - Briefly enter `Playing(appID)` so Steam will accept stat writes.
   - Send a `ClientGetUserStats` message and parse the binary KeyValue schema in the response - this lists every achievement bit and its `permission` flag.
   - OR new bits into the existing stat values and push back via `ClientStoreUserStats2`.
3. Record each AppID in the per-bot scan database (`_lastScannedAt`).
4. Sleep until next full-scan due.

**Dynamic check** (every `DynamicCheckIntervalHours`, default 24 hours, between full scans - set to `0` or use `aadint off` to disable):
1. Same library enumeration as above.
2. Filter to AppIDs **not** in the scan database - i.e. never seen before.
3. **If filtered set is empty: return immediately without signalling AutoIdle.** Zero downtime for sibling plugins.
4. If new games exist: signal sibling pause, scan only those AppIDs, signal resume.

Achievements with a non-zero `permission` flag in the schema are server-protected - Steam normally rejects client-side writes for those. Off by default; flip `AttemptProtectedAchievements: true` (or use `aaprotected on` at runtime) if you want to try them anyway.

## Features

- **Plug-and-play discovery** - uses ASF's authenticated session; rides on the bot's already-authenticated protocol connection. No Web API key needed.
- **Two scan modes** - full library scan on a configurable day interval + dynamic checks (every N hours, default 24, can be disabled) that catch newly-acquired games (free claims, gifts, etc.) without waiting for the next full scan.
- **Persistent scan database** - every AppID that's been scanned is recorded in `BotDatabase` (`_lastScannedAt`). Dynamic checks use this to filter to genuinely-new AppIDs.
- **Coexist with ASF's card farmer** - `AllowCardFarming: true` (default). When ASF is actively farming a card during a scan, AA waits at the current game until farming completes, then resumes scan from the same AppID - no progress lost.
- **Coexist with ASF-AutoIdle** - AA sends `idlepause` / `idleresume` signals via ASF's command bus before/after each scan. If AutoIdle isn't installed, AA falls back to `Bot.Actions.Resume()` directly.
- **Resume from interruption** - if ASF disconnects mid-scan (e.g. `LoggedInElsewhere` when you open Steam on your PC), the AppID being scanned is persisted. Next session picks up at the exact same game.
- **Honour the cooldown across reconnects** - disconnect/reconnect doesn't trigger a fresh scan if one already completed within the configured interval.
- **User cancel via `!aacancel`** - interrupts the in-flight scan, wipes the resume point (so next scan starts from index 0), AutoIdle resumes within seconds. The configured cooldown is honoured - the next scan won't fire until the interval elapses, even if the cancel happened mid-cycle.
- **Per-mode metrics** - game count scanned in full scans vs dynamic checks, session + all-time, persisted.
- **Per-game stats** - total achievements unlocked per game, last-scanned timestamp, schema completion ratio (X/Y unlocked), persisted.
- **Plugin uptime tracking** - per-session and total across all sessions.
- **Per-game blacklist** - both via JSON config and via runtime commands; merged.
- **Live config reload** - edit a bot's JSON config and ASF re-runs `OnBotInitModules`; the plugin restarts that bot's loop transparently when relevant fields change.
- **Independence** - works with or without ASF-AutoIdle installed; AutoIdle isn't a hard dependency. Card farming coexistence works either way.

## Install

1. Download `ASF-AutoAchievement.dll` from the [latest release](../../releases) (or build from source).
2. Drop it into `<your ASF folder>/plugins/ASF-AutoAchievement/`.
3. Add to any bot config under `<ASF>/config/<BotName>.json`, anywhere inside the outer `{ ... }`:
   ```json
   "AutoAchievement": { "Enabled": true }
   ```
4. Restart ASF.

You should see in the log:
```
ASF-AutoAchievement vX.Y.Z.0 loaded - every bot's library will be scanned for new achievements.
<Bot> > AutoAchievement: scan loop started.
<Bot> > AutoAchievement: starting full library scan.
<Bot> > AutoAchievement: dynamicstore returned 2480 entries (including DLC / demos / unplayed free games).
<Bot> > AutoAchievement: full library scan - scanning 2480 game(s). Estimated time: 1h 33m.
<Bot> > AutoAchievement: full library scan complete in 1h 33m.
  Games scanned: 2480
    - With new achievements unlocked: 24
    - Already 100% complete: 8
    - No Steam achievements: 2440
  Achievements unlocked this scan: 412
```

## Configuration

Every key is optional. Defaults shown.

```json
"AutoAchievement": {
    "Enabled": true,
    "ScanIntervalDays": 7,
    "DynamicCheckIntervalHours": 24,
    "InitialDelaySeconds": 60,
    "PerGameDelayMilliseconds": 750,
    "AttemptProtectedAchievements": false,
    "AllowCardFarming": true,
    "Blacklist": []
}
```

| Key | Type | Default | Effect |
|---|---|---|---|
| `Enabled` | bool | `true` if block exists | Master switch for that bot. |
| `ScanIntervalDays` | uint | `7` | Full library scan cadence. Min enforced: 1. |
| `DynamicCheckIntervalHours` | uint | `24` | Between full scans, how often to check for newly-acquired games. Set to `0` to disable dynamic checks entirely (full scan only). Otherwise any positive integer of hours. |
| `InitialDelaySeconds` | uint | `60` | Wait after login before first scan attempt. |
| `PerGameDelayMilliseconds` | uint | `750` | Delay between games inside one scan. Reduces Steam rate-limit risk. |
| `AttemptProtectedAchievements` | bool | `false` | Try to unlock achievements whose schema lists `permission > 0`. Steam usually rejects these. Off by default to keep logs quiet. Toggle at runtime with `aaprotected`. |
| `AllowCardFarming` | bool | `true` | When `true`, scans pause at the current game while ASF's card farmer is active; resume at that same AppID when farming finishes. When `false`, scans run unconditionally and may preempt card farming. |
| `Blacklist` | uint[] | `[]` | AppIDs the plugin will never touch. Merged with the runtime blacklist. |

### Opt-out

A bot config without an `AutoAchievement` block is ignored entirely. Or set `"AutoAchievement": { "Enabled": false }` to log the opt-out explicitly.

## Runtime commands

Send these to a bot via ASF's command interface (web UI Commands tab, IPC, or a chat DM). Operator-level access is required. Pass the bot name as the first argument, or omit it to default to ASF's chosen bot.

| Command | Aliases | What it does |
|---|---|---|
| `aashow [bot]` | `aastatus` | Status: scan intervals, last scan type (full / dynamic) + duration, next due times, per-mode game-scan metrics, next-scan duration estimate. |
| `aanow [bot]` | `aascan` | Run a full library scan immediately. |
| `aacheck [bot]` | `aadynamic` | Run a dynamic check immediately - scans only games never seen before. Useful right after claiming a free game. |
| `aacancel [bot]` | `aastop` | Cancel the in-flight scan. Wipes the resume point so the next scan starts from index 0. AutoIdle resumes within seconds. |
| `aagame [bot] <appid\|name>` | `aaone` | Unlock achievements for a single game (interactive use). |
| `aastats [bot] [N\|all]` | `aastat` | Per-game stats (default: all, sorted by all-time desc). Shows X/Y completion ratios. |
| `aablacklist [bot] <appid\|name>` | `aabl`, `aablock` | Add a game to the never-scan blacklist. |
| `aablacklistremove [bot] <appid\|name>` | `aablrm`, `aaunblock` | Remove from blacklist. |
| `aainterval [bot] <days>` | `aaint` | Change full-scan interval (0 to clear override; min 1). |
| `aadynamicinterval [bot] <hours\|off\|reset>` | `aadint`, `aadinterval` | Change dynamic-check interval (positive integer of hours), `off` to disable dynamic checks entirely, `reset` to clear the runtime override and fall back to JSON config. No arg = show current value. |
| `aaprotected [bot] [on\|off\|reset]` | `aaprot` | Runtime override for `AttemptProtectedAchievements` (no arg = show). |
| `aacards [bot]` | - | Toggle `AllowCardFarming` (yield play slot to ASF card farmer vs scan unconditionally). |
| `aatoggle [bot]` | - | Toggle the plugin on/off at runtime. |
| `aahelp` | - | Print the command list. |

The blacklist accepts either an AppID (e.g. `730`) or a substring of the game's display name (e.g. `Counter-Strike`).

### Persistence

Runtime state is saved per-bot in ASF's `BotDatabase` under the key `ASF.AutoAchievement.State`. Survives ASF restarts. Includes:
- Persistent blacklist additions
- `ScanIntervalDays`, `DynamicCheckIntervalHours`, enabled, `AttemptProtectedAchievements`, `AllowCardFarming` runtime overrides
- Total achievements unlocked counter
- Per-game unlock counts + schema snapshots (completion ratios)
- Per-AppID `_lastScannedAt` (the database used to filter dynamic-check targets)
- Last full-scan completion timestamp + last-scan-cancelled timestamp + last dynamic check timestamp
- Last scan duration + target count (for "Next scan estimated duration")
- Last scan type (full vs dynamic) + resume scan type
- Scans-completed counters (session + all-time)
- Per-mode game-scan counters (full + dynamic, session + all-time)
- Resume-from-AppID for interrupted scans
- Plugin uptime baseline

JSON-config `Blacklist` is read on every `OnBotInitModules` and **merged** with the runtime persistent set.

## Sibling-plugin coordination

When [ASF-AutoIdle](https://github.com/sturq/ASF-AutoIdle) is installed and idling games, AutoAchievement signals it to pause before each scan:

1. **Before scan starts**: AA sends `idlepause ASF-AutoAchievement` via ASF's command bus.
2. **During scan**: each per-game scan briefly enters `Playing(appID)` for stat writes.
3. **Game-by-game pauses**: if the user opens a Steam game on their PC (`IsPlayingPossible=false`) or ASF's card farmer becomes active, AA waits at the current game until the slot frees up. Resumes the scan at the same AppID.
4. **After scan ends** (complete, cancelled, or errored): AA sends `idleresume` so AutoIdle picks back up.

If AutoIdle isn't installed, the `idlepause`/`idleresume` commands return no response (ASF returns nothing for unrecognised commands), and AA falls back to `Bot.Actions.Resume()` directly. Independence is preserved.

The **least-downtime path** for AutoIdle:
- Full scans always pause AutoIdle for the scan duration (necessary - every game in the library is scanned).
- Dynamic checks pause AutoIdle **only if there are new games to scan**. If the filtered target set is empty after applying the `_lastScannedAt` filter, AA returns immediately without sending `idlepause`. AutoIdle keeps idling uninterrupted.

## Build from source

Requires the .NET SDK matching your ASF runtime TFM (currently `net10.0` for ASF 6.3.x).

You also need `ArchiSteamFarm.dll`, `SteamKit2.dll`, `protobuf-net.dll`, and `protobuf-net.Core.dll` from the [exact ASF release](https://github.com/JustArchiNET/ArchiSteamFarm/releases) you intend to load the plugin into. Place them in a folder, then point `ASF_DIR` at it (or use the project's default sibling `ASF/` folder).

```bash
# Linux / macOS
ASF_DIR=/path/to/ArchiSteamFarm dotnet publish src/ASF-AutoAchievement.csproj -c Release -o ./publish
```

```powershell
# Windows
$env:ASF_DIR = "C:\path\to\ArchiSteamFarm"
dotnet publish src\ASF-AutoAchievement.csproj -c Release -o .\publish
```

The compiled `ASF-AutoAchievement.dll` ends up in `./publish`. Copy it into `<ASF>/plugins/ASF-AutoAchievement/`.

## Notes / gotchas

- **VAC / anti-cheat games** - unlocking achievements via direct stat writes is purely cosmetic / not detected by VAC, but **competitive games' leaderboards may flag accounts** (e.g. CS, Dota). Add those to the blacklist if you care.
- **Server-protected achievements** - most modern multiplayer titles validate achievements server-side. The schema marks these with `permission > 0`. Steam responds `EResult.AccessDenied` to client-side writes; the plugin logs and moves on. There is no way to unlock these from outside the game's own backend.
- **Conflict with AutoIdle** - handled via the `idlepause`/`idleresume` signals described above. No manual setup required.
- **Unplayed free-to-play games** - covered by the dynamicstore enumeration. F2P claims, demos with achievements, etc. are all picked up.
- **Limited accounts** - bots that haven't spent money can still receive achievements; this is independent of the limited-account restriction (which mainly affects trading).
- **Disconnect/reconnect during scan** - `_resumeFromAppID` is set before each game is scanned. Next session resumes at the same AppID. `_lastScanCancelledAt` is set on user cancel (`!aacancel`) and the resume point is wiped so the next scan starts fresh from index 0.
- **Estimated next-scan duration** - surfaced in `aashow` as `~Xh Ym (N games × 2250ms each)`. Real time depends on Steam response speed and per-game pauses.

## License

AGPL-3.0 — see [LICENSE](LICENSE). Forks and hosted modifications must keep the source open.
