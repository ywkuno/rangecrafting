# RangeCrafting (7 Days to Die, C# ModAPI)

This mod implements crafting-only linked inventory support for 7 Days to Die.

It enables consumable crafting ingredients to come from:
1. player inventory
2. nearby storage containers that pass claim permissions (default mode: `claimOnly`)

It does **not** alter pickup/drop, repair, reload, or vehicle-fuel behavior.

## Install

1. Build `RangeCrafting.dll` with references to `Assembly-CSharp.dll`, `0Harmony.dll`, and `UnityEngine` from your 7D2D install.
2. Copy the output folder contents into:
   - `<7D2D Installation>\Mods\RangeCrafting\`
3. Ensure `ModInfo.xml`, `RangeCrafting.dll`, and `config.json` are in the same folder.
4. Start the game and verify the mod appears in the mod list.
5. For your setup, place the folder here:
   - `S:\SteamLibrary\steamapps\common\7 Days To Die\Mods\RangeCrafting\`

## Workshop packaging

After building `RangeCrafting.dll`, create a Steam-ready zip:

```powershell
.\package-workshop.ps1 -Assembly RangeCrafting.dll
```

## Config (`config.json`)

Auto-created on first run.

- `mode`
  - `"claimOnly"` (default): only linked claim containers are eligible.
  - `"rangeOnly"`: uses raw radius mode.
  - `"disabled"`: emergency switch; mod is inert.
- `range`
  - Fallback range and default range for `rangeOnly` mode.
  - `0` uses only claim-only context in `claimOnly` mode.
- `claimRadius`
  - Radius (in blocks) from claim anchor when resolving eligible claim containers.
- `claimSearchRadius`
  - How far out to look for claim blocks when locating active claims.
  - `<= 0` uses `claimRadius + 10`.
- `scanCooldownFrames`
  - Throttle frequency for storage scans.
- `claimRefreshCooldownFrames`
  - Throttle frequency for claim-anchor scans.
- `claimOnlyAllowRangeFallback`
  - If no claim is found, allow fallback to `range` search.
- `requireOwnerMetadataMatch`
  - When true, claim blocks with missing owner metadata are not trusted for claim-only use.
- `permitClaimOwner`
  - If true, allows claim owner containers.
- `permitClaimFriend`
  - If true, allows friend access lists when detected.
- `permitClaimAlly`
  - If true, allows ally-style access entries when detected.
- `permitClaimParty`
  - If true, allows party-related permission checks when detected.
- `permitClaimClan`
  - If true, allows clan/group/faction-style permission checks when detected.
- `allowAllContainers`
  - `true` to include broader storage-like containers; `false` keeps player storage containers only.
- `patchHasItems`
  - Keep `false` unless needed for your exact build.
- `landClaimBlockNames`
  - Keywords used to identify claim blocks by block name.
- `isDebug`
  - Verbose debug logging.

## Notes

- This mod uses Harmony + C# patching so the claim-boundary checks execute server-authoritatively in the patched methods.
- For non-matching game versions, unsupported method signatures are skipped and no behavior is changed.

## Search + Highlight (Range Search)

In-game command (if the build supports the chat/console hook):

```text
/rsearch <item name or item id> [max=<n>] [r=<range>] [count=<n>]
```

- `/rsearch wire max=5 r=30` finds nearest matching storages and tries to highlight them.
- `/rconfirm [seconds]` grants a short approval window (default from config) to allow storage extraction.
- `/rlog [n]` shows the recent craft/storage action log.
- `/rhelp` shows command usage in chat.
- If marker APIs are unavailable, you still get results with coordinates.

Config:

- `enableRangeSearch`: enable/disable command integration.
- `searchMaxResults`: default max containers returned.
- `searchRange`: override range for search; `0` uses normal crafting range logic.
- `highlightSearchResults`: toggle marker highlight attempts.
- `highlightMarkerLimit`: max highlighted containers per search.
- `searchMarkerDuration`: marker lifetime in seconds (best effort).
- `storageConsumptionOrder`: how linked storages are consumed during crafting.
  - `nearest` (default): closest first
  - `farthest`: farthest first
  - `name`: alphabetic container name order
  - `quantity`: containers with higher match count first (fallback to distance)

## Storage confirmation + permissions

- `requireStorageUseConfirmation` (default `false`): when enabled, storage consumption via crafting UI is blocked until confirmed.
- `/rconfirm` sets a temporary confirmation window for the local player (configurable default by `storageUseConfirmationSeconds`).
- `storageUseConfirmationSeconds`: fallback allow-window duration when `/rconfirm` is called with no argument.
- `requireOwnerMetadataMatch` and claim permission flags are used to evaluate whether you can access another player's claim container.
- `permissionProfile`: helper preset that maps common trust levels:
  - `vanilla` (default)
  - `ownerOnly`
  - `friendsOnly`
  - `allies`
  - `trustedOnly`
  - `custom` (leave booleans untouched)

## Container filter (optional)

- `allowedStorageContainerNames`: optional whitelist of container/block name tokens.
  - Empty list means allow all eligible storage types from claims.
- `blockedStorageContainerNames`: optional blacklist of container/block name tokens.
  - If a token matches, that container is skipped even if it passes all other checks.
