# ASF-AutoAchievement

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) plugin that **auto-detects every game on your account and unlocks all available achievements** — like [Steam Achievement Manager](https://github.com/gibbed/SteamAchievementManager) (SAM) Picker, but fully automated, recurring, and remote (no Steam client running). Re-scans your library on an interval to catch new achievements added in game updates.

No Steam Web API key. No game IDs to enter. Drop the DLL in `plugins/`, set `"AutoAchievement": { "Enabled": true }` in your bot config, restart ASF.

## How it works

1. Discover the bot's library via `IPlayerService.GetOwnedGames` (rides on the bot's already-authenticated SteamKit2 session).
2. For each owned game (skipping the blacklist):
   - Briefly enter `Playing(appID)` so Steam will accept stat writes.
   - Send a `ClientGetUserStats` message and parse the binary KeyValue schema in the response — this lists every achievement bit and its `permission` flag.
   - OR new bits into the existing stat values and push back via `ClientStoreUserStats2`.
3. Sleep `ScanIntervalHours`, repeat.

Achievements with a non-zero `permission` flag in the schema are server-protected — Steam normally rejects client-side writes for those. Off by default; flip `AttemptProtectedAchievements: true` (or use `aaprotected on` at runtime) if you want to try them anyway.

## Install

1. Build the DLL (see below) or grab a release.
2. Drop `ASF-AutoAchievement.dll` into `<your ASF folder>/plugins/ASF-AutoAchievement/`.
3. Add to any bot config under `<ASF>/config/<BotName>.json`, anywhere inside the outer `{ ... }`:
   ```json
   "AutoAchievement": { "Enabled": true }
   ```
4. Restart ASF.

You should see in the log:
```
ASF-AutoAchievement vX.Y.Z.0 loaded — every bot's library will be scanned for new achievements.
<Bot> > AutoAchievement: scan loop started.
<Bot> > AutoAchievement: profile owned-games returned N entries.
<Bot> > AutoAchievement: scanning N game(s) (skipping K blacklisted). Estimated time: ...
<Bot> > AutoAchievement: scan complete in 14m 32s.
  Games scanned: 570
    - With new achievements unlocked: 12
    - Already 100% complete: 8
    - No Steam achievements: 540
  Achievements unlocked this scan: 198
  Per-game unlocks:
    Rust (252490): 53 → 86/102
    ...
```

## Configuration

Every key is optional. Defaults shown.

```json
"AutoAchievement": {
    "Enabled": true,
    "ScanIntervalHours": 12,
    "InitialDelaySeconds": 60,
    "PerGameDelayMilliseconds": 750,
    "AttemptProtectedAchievements": false,
    "Blacklist": []
}
```

| Key | Type | Default | Effect |
|---|---|---|---|
| `Enabled` | bool | `true` if block exists | Master switch for that bot. |
| `ScanIntervalHours` | uint | `12` | How often to re-scan the whole library. Min enforced: 1. |
| `InitialDelaySeconds` | uint | `60` | Wait after login before the first scan. |
| `PerGameDelayMilliseconds` | uint | `750` | Delay between games inside one scan. Reduces Steam rate-limit risk. |
| `AttemptProtectedAchievements` | bool | `false` | Try to unlock achievements whose schema lists `permission > 0`. Steam usually rejects these. Off by default to keep logs quiet. Toggle at runtime with `aaprotected`. |
| `Blacklist` | uint[] | `[]` | AppIDs the plugin will never touch. Merged with the runtime blacklist. |

### Opt-out

A bot config without an `AutoAchievement` block is ignored entirely. Or set `"AutoAchievement": { "Enabled": false }` to log the opt-out explicitly.

## Runtime commands

Send these to a bot via ASF's command interface (web UI Commands tab, IPC, or a chat DM to the bot). Operator-level access is required. Pass the bot name as the first argument, or omit it to default to ASF's chosen bot.

| Command | Aliases | What it does |
|---|---|---|
| `aashow [bot]` | `aastatus` | Status: enabled, current scan progress, last scan, blacklist, totals. |
| `aanow [bot]` | `aascan` | Run a full library scan immediately. |
| `aagame [bot] <appid\|name>` | `aaone` | Unlock achievements for a single game (interactive use). |
| `aastats [bot] [N\|all]` | `aastat` | Per-game stats (default: all, sorted by all-time desc). Shows X/Y completion ratios. |
| `aablacklist [bot] <appid\|name>` | `aabl`, `aablock` | Add a game to the never-scan blacklist. |
| `aablacklistremove [bot] <appid\|name>` | `aablrm`, `aaunblock` | Remove from blacklist. |
| `aainterval [bot] <hours>` | `aaint` | Change scan interval (0 to clear override; min 1). |
| `aaprotected [bot] [on\|off\|reset]` | `aaprot` | Runtime override for `AttemptProtectedAchievements` (no arg = show). |
| `aatoggle [bot]` | — | Toggle the plugin on/off at runtime. |
| `aahelp` | — | Print the command list. |

The blacklist accepts either an AppID (e.g. `730`) or a substring of the game's display name (e.g. `Counter-Strike`).

### Persistence

Runtime state — blacklist additions, `ScanIntervalHours` override, enabled override, `AttemptProtectedAchievements` override, total achievements unlocked counter, per-game unlock counts, schema completion snapshots, last scan timestamp, scan totals, and total uptime — is saved per-bot in ASF's `BotDatabase` under the key `ASF.AutoAchievement.State`. Survives ASF restarts.

JSON-config `Blacklist` is read on every `OnBotInitModules` and **merged** with the runtime persistent set.

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

- **VAC / anti-cheat games** — unlocking achievements via direct stat writes is purely cosmetic / not detected by VAC, but **competitive games' leaderboards may flag accounts** (e.g. CS, Dota). Add those to the blacklist (the example config blacklists `730` CS and `570` Dota by default).
- **Server-protected achievements** — most modern multiplayer titles validate achievements server-side. The schema marks these with `permission > 0`. Steam responds `EResult.AccessDenied` to client-side writes; the plugin logs and moves on. There is no way to unlock these from outside the game's own backend.
- **Conflict with AutoIdle** — if you also run `ASF-AutoIdle`, the achievement scan briefly takes over the playing slot for one game at a time. AutoIdle's normal rotation resumes after the scan finishes (the plugin calls `Bot.Actions.Resume()` on completion).
- **Free-to-play games** — `IPlayerService.GetOwnedGames` only returns F2P titles you've actually launched. Those can be unlocked the same way. Untouched F2P titles won't show up.
- **Limited accounts** — bots that haven't spent money can still receive achievements; this is independent of the limited-account restriction (which mainly affects trading).

## License

MIT.
