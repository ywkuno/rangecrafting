# RangeCrafting (7 Days to Die, C# ModAPI)

This mod implements crafting-only linked inventory support for 7 Days to Die.

It enables consumable crafting ingredients to come from:
1. player inventory
2. nearby storage containers that pass claim permissions (default mode: `claimOnly`)

It does **not** alter pickup/drop, repair, reload, or vehicle-fuel behavior.

## Install

1. Build `ClaimLinkedCrafting.dll` with references to `Assembly-CSharp.dll`, `0Harmony.dll`, and `UnityEngine` from your 7D2D install.
2. Copy the output folder contents into:
   - `<7D2D Installation>\Mods\RangeCrafting\`
3. Ensure `ModInfo.xml`, `ClaimLinkedCrafting.dll`, and `config.json` are in the same folder.
4. Start the game and verify the mod appears in the mod list.

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
