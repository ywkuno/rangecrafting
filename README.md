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
4. Optional: copy `config.example.json` to `config.json` (recommended once for clean, documented defaults).
5. Start the game and verify the mod appears in the mod list.
6. For your setup, place the folder here:
   - `S:\SteamLibrary\steamapps\common\7 Days To Die\Mods\RangeCrafting\`

## Quick start

1. Drop the folder into Mods.
2. Start the game.
3. Open crafting UI and try a recipe that needs nearby materials.
4. If nothing appears, run:
   - `/rsearch <item> scope=claim`
   - `/rsearch <item> scope=range`
   - `/rsearch <item> state=unlocked`
5. To allow storage consume in a locked security scenario run:
   - `/rconfirm 30`

If you use claim-only mode and the recipe is still not pulling from nearby storage:
- verify the container is inside a valid claim area,
- verify you are owner/friend on that claim,
- verify the container is not denied by `blockedStorageContainerNames`.

## First run config behavior

- On first launch, the mod auto-generates `config.json` if missing.
- The repository also ships `config.example.json` with a safe baseline and inline notes.
- Recommended publish-safe setup:
  - copy `config.example.json` → `config.json`
  - edit only the keys you want to change
  - keep `storageUseConfirmationSeconds` and search toggles as desired

## Changelog

- `1.0.2`:
  - Added Steam publish polish:
    - clearer install and quick-start docs,
    - explicit first-run config guidance,
    - search/filter usage notes refreshed.
  - added version bump for publish metadata.

## Workshop packaging

For Steam Workshop uploads, also include:
- `RangeCrafting.dll`
- `ModInfo.xml`
- `README.md`
- `config.example.json` (or your edited `config.json`)

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
/rsearch <item name or item id> [max=<n>] [r=<range>] [count=<n>] [scope=claim|range|all] [state=locked|unlocked|all]
```

- `/rsearch wire max=5 r=30` finds nearest matching storages and tries to highlight them.
- `/rsearch wire scope=claim state=unlocked` filters to unlocked containers in active claim scope.
- `/rsearch stone all r=30 state=locked` returns locked-only matches within 30m.
- `/rconfirm [seconds]` grants a short approval window (default from config) to allow storage extraction.
- `/rlog [n]` shows the recent craft/storage action log.
- `/rhelp` shows command usage in chat.
- Search result tags:
  - `[C]` in claim scope
  - `[R]` range-only (outside active claim scope)
  - `[L]` locked (accessible)
  - `[U]` unlocked
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

Tip: `config.example.json` is included and can be copied to `config.json` to start with documented defaults and easy edit notes.

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
