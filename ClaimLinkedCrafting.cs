using HarmonyLib;
using Newtonsoft.Json;
using Platform;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace ClaimLinkedCrafting
{
    public enum ClaimMode
    {
        ClaimOnly,
        RangeOnly,
        Disabled
    }

    public struct StorageSearchHit
    {
        public Vector3i position;
        public string containerName;
        public int totalCount;
        public float distance;
        public bool isInClaimScope;
        public bool isLocked;
    }

    internal enum SearchScopeFilter
    {
        All,
        ClaimOnly,
        RangeOnly
    }

    internal enum SearchStateFilter
    {
        All,
        LockedOnly,
        UnlockedOnly
    }

    internal struct SearchResultFilter
    {
        public SearchScopeFilter scope;
        public SearchStateFilter state;

        public static SearchResultFilter Default => new SearchResultFilter
        {
            scope = SearchScopeFilter.All,
            state = SearchStateFilter.All
        };
    }

    public class ClaimLinkedCrafting : IModApi
    {
        private static ClaimLinkedCrafting context;
        private static Mod mod;
        private static Harmony harmony;

        private static ModConfig config;

        private static readonly Dictionary<Vector3i, object> currentStorageDict = new Dictionary<Vector3i, object>();
        private static readonly List<Vector3> activeClaimCenters = new List<Vector3>();
        // Item count/mutation helpers are resolved at call time to stay resilient to API drift.

        private static int lastStorageRefreshFrame;
        private static int lastClaimRefreshFrame;
        private static int lastSearchCommandFrame;

        private static MethodInfo playerInventoryGetAllItemStacks;
        private static MethodInfo bagGetItemCount;
        private static bool isReady;
        private static bool isSearchCommandPatched;
        private static MethodInfo[] cachedMarkerMethods;
        private static readonly int StorageActionLogLimit = 30;
        private static readonly Queue<string> storageActionLog = new Queue<string>();
        private static readonly Dictionary<string, float> storageUseApprovalUntil = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] SearchCommandAliases = new[] { "rsearch", "rangesearch", "rangecrafting", "rc" };
        private static readonly string[] ConfirmCommandAliases = new[] { "rconfirm", "rcconfirm", "rcraftconfirm" };
        private static readonly string[] StorageLogCommandAliases = new[] { "rlog", "craftlog", "rcraftlog" };
        private static readonly string[] HelpCommandAliases = new[] { "rhelp", "rangecraftinghelp", "rinfo" };
        private static readonly int StorageConfirmationHintCooldownFrames = 300;
        private static readonly int StorageStatusBannerCooldownFrames = 180;
        private static int lastStorageUseHintFrame;
        private static int lastStorageStatusFrame;
        private static readonly string[] SearchChatTypeCandidates = new[]
        {
            "XUiC_ChatWindow",
            "XUiC_ConsoleWindow",
            "XUiC_Console",
            "GameConsole",
            "ConsoleManager",
            "HUDChat"
        };
        private static readonly string[] SearchChatMethodFragments = new[]
        {
            "submit",
            "send",
            "execute",
            "command",
            "onreturn",
            "enter",
            "handle",
            "process",
            "accept",
            "input"
        };

        public void InitMod(Mod modInstance)
        {
            context = this;
            mod = modInstance;
            LoadConfig();
            if (!config.modEnabled)
                return;

            if (!TryInitializeMethodReferences())
            {
                Dbgl("Required game APIs are missing. Mod disabled.");
                return;
            }

            harmony = new Harmony(GetType().ToString());
            ApplyPatches();
            TryPatchRangeSearchCommand();
            isReady = true;
            Dbgl("Initialized");
        }

        public static void Dbgl(object str, bool prefix = true)
        {
            if (config?.isDebug == true)
            {
                Debug.Log((prefix ? mod?.DisplayName + " " : string.Empty) + str);
            }
        }

        private static bool TryInitializeMethodReferences()
        {
            var xuiPlayerInventory = AccessTools.TypeByName("XUiM_PlayerInventory");
            if (xuiPlayerInventory == null)
                return false;

            playerInventoryGetAllItemStacks = AccessTools.Method(xuiPlayerInventory, "GetAllItemStacks");
            if (playerInventoryGetAllItemStacks == null)
                return false;

            var bagType = AccessTools.TypeByName("Bag");
            if (bagType != null)
            {
                bagGetItemCount = AccessTools.Method(bagType, "GetItemCount", new[]
                {
                    AccessTools.TypeByName("ItemValue"), typeof(bool), typeof(int), typeof(int), typeof(bool)
                });
            }

            return playerInventoryGetAllItemStacks != null;
        }

        private static void TryPatchRangeSearchCommand()
        {
            if (!config.modEnabled || !config.enableRangeSearch)
                return;

            if (isSearchCommandPatched)
                return;

            var target = FindRangeSearchInputMethod();
            if (target == null)
            {
                Dbgl("No suitable chat/console input method found; /rsearch command hook disabled (manual use only).");
                return;
            }

            var prefix = typeof(ClaimLinkedCrafting).GetMethod(
                nameof(RangeSearchCommand_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static
            );
            if (prefix == null)
                return;

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                isSearchCommandPatched = true;
                Dbgl($"Patched command input for search: {target.DeclaringType?.Name}.{target.Name}");
            }
            catch (Exception ex)
            {
                Dbgl($"Failed to patch search command input ({target.DeclaringType?.Name}.{target.Name}): {ex}");
            }
        }

        private static MethodInfo FindRangeSearchInputMethod()
        {
            foreach (var typeName in SearchChatTypeCandidates)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                    continue;

                var method = FindLikelyTextInputMethod(type);
                if (method != null)
                    return method;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    var name = type.Name.ToLowerInvariant();
                    if (!name.Contains("chat") && !name.Contains("console") && !name.Contains("command") && !name.Contains("ui"))
                        continue;

                    var method = FindLikelyTextInputMethod(type);
                    if (method != null)
                        return method;
                }
            }

            return null;
        }

        private static MethodInfo FindLikelyTextInputMethod(Type type)
        {
            MethodInfo best = null;
            int bestScore = 0;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var method in methods)
            {
                if (method.IsAbstract || method.IsGenericMethod)
                    continue;

                var parms = method.GetParameters();
                int stringParmCount = parms.Count(p => p.ParameterType == typeof(string));
                if (stringParmCount == 0)
                    continue;

                if (!(method.ReturnType == typeof(void) || method.ReturnType == typeof(bool)))
                    continue;

                var name = method.Name.ToLowerInvariant();
                var score = 0;
                score += stringParmCount * 2;
                if (name.Contains("submit") || name.Contains("send") || name.Contains("command") || name.Contains("execute"))
                    score += 5;
                if (SearchChatMethodFragments.Any(f => name.Contains(f)))
                    score += 2;

                if (score > bestScore)
                {
                    best = method;
                    bestScore = score;
                }
            }

            return best;
        }

        private void LoadConfig()
        {
            try
            {
                var path = Path.Combine(Directory.GetParent(Assembly.GetExecutingAssembly().Location).FullName, "config.json");
                if (!File.Exists(path))
                {
                    config = new ModConfig();
                    config = ConfigSanitizer.Sanitize(config);
                    config = ConfigSanitizer.ApplyPermissionProfile(config);
                    File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                    return;
                }

                config = JsonConvert.DeserializeObject<ModConfig>(File.ReadAllText(path)) ?? new ModConfig();
                config = ConfigSanitizer.Sanitize(config);
                config = ConfigSanitizer.ApplyPermissionProfile(config);
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                config = new ModConfig();
                Debug.LogError($"[ClaimLinkedCrafting] Failed to load config, using defaults: {ex}");
            }
        }

        private static bool RangeSearchCommand_Prefix(object __instance, object[] __args)
        {
            if (!config.enableRangeSearch || !config.modEnabled || !isReady)
                return true;

            if (Time.frameCount - lastSearchCommandFrame < Mathf.Max(1, config.searchCommandCooldownFrames))
                return true;

            if (!TryExtractCommandText(__args, out var text))
                return true;

            if (!HandleRangeCraftingSlashCommand(text, out var response))
            {
                if (!string.IsNullOrEmpty(response))
                    DeliverToPlayer(response, __instance);
                return false;
            }

            return true;
        }

        private static bool HandleRangeCraftingSlashCommand(string text, out string response)
        {
            response = null;
            if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/", StringComparison.Ordinal))
                return false;

            if (IsCommand(text, SearchCommandAliases, out var queryText))
            {
                lastSearchCommandFrame = Time.frameCount;
                return TryHandleRangeSearch(queryText, out response);
            }

            if (IsCommand(text, ConfirmCommandAliases, out var confirmArgs))
            {
                lastSearchCommandFrame = Time.frameCount;
                return TryHandleStorageConfirmation(confirmArgs, out response);
            }

            if (IsCommand(text, StorageLogCommandAliases, out var logArgs))
            {
                lastSearchCommandFrame = Time.frameCount;
                return TryHandleStorageLogCommand(logArgs, out response);
            }

            if (IsCommand(text, HelpCommandAliases, out var _))
            {
                response = BuildRangeCraftingHelpText();
                return true;
            }

            return false;
        }

        private static bool IsCommand(string text, string[] aliases, out string argsText)
        {
            argsText = null;
            var trimmed = text.Trim();
            var parts = trimmed.Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            var command = parts[0];
            if (aliases.All(a => !command.Equals(a, StringComparison.OrdinalIgnoreCase)))
                return false;

            argsText = parts.Length <= 1 ? string.Empty : trimmed.Substring(trimmed.IndexOf(' ') + 1).Trim();
            return true;
        }

        private static bool TryExtractCommandText(object[] args, out string text)
        {
            text = null;
            if (args == null || args.Length == 0)
                return false;

            foreach (var arg in args)
            {
                if (arg == null)
                    continue;
                if (arg is string str)
                {
                    text = str;
                    return !string.IsNullOrWhiteSpace(text);
                }

                if (arg.GetType().FullName?.Contains("String") == true && arg != null)
                {
                    var asString = arg?.ToString();
                    if (!string.IsNullOrWhiteSpace(asString))
                    {
                        text = asString;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string BuildRangeCraftingHelpText()
        {
            return "[RangeCrafting] commands: " +
                   "/rsearch <item> [max=N] [r=N] [count=N] [scope=claim|range|all] [state=locked|unlocked|all] | " +
                   "/rconfirm [seconds=30] | /rlog [n] | /rhelp";
        }

        private static bool TryHandleRangeSearch(string argsText, out string response)
        {
            response = null;
            if (!config.enableRangeSearch || !config.modEnabled)
                return true;

            if (string.IsNullOrWhiteSpace(argsText))
            {
                response = "Usage: /rsearch <item name or item id> [max=N] [r=N] [count=N] [scope=claim|range|all] [state=locked|unlocked|all]";
                return true;
            }

            var args = ParseArguments(argsText);
            if (args.Count == 0)
            {
                response = "Usage: /rsearch <item name or item id> [max=N] [r=N] [count=N] [scope=claim|range|all] [state=locked|unlocked|all]";
                return true;
            }

            int maxResults = config.searchMaxResults;
            float range = GetRangeForSearch();
            var filter = SearchResultFilter.Default;
            ParseSearchCommandArgs(args, ref maxResults, ref range, out var queryTokens, out filter);

            var query = string.Join(" ", queryTokens).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                response = "Usage: /rsearch <item name or item id> [max=N] [r=N] [count=N] [scope=claim|range|all] [state=locked|unlocked|all]";
                return true;
            }

            var results = FindContainersWithItem(query, maxResults, range, filter);
            var playerCount = CountItemInPlayerInventory(query, out _);

            if (results == null || results.Count == 0)
            {
                var noResultSummary = BuildSearchFilterSummary(filter, range);
                if (playerCount > 0)
                    response = $"Found only in player inventory: {playerCount}x {query}. {noResultSummary}";
                else
                    response = $"No matching items found for '{query}' in nearby storage. {noResultSummary}";
                return true;
            }

            var lines = new List<string>();
            lines.Add(playerCount > 0
                ? $"Player inventory: {playerCount}x"
                : "Player inventory: 0x");
            lines.Add($"Search: '{query}' ({results.Count} container(s))");
            lines.Add($"Scope={filter.scope} | State={filter.state} | radius={range:0.0}m");
            lines.Add(BuildSearchResultLegend());
            for (var i = 0; i < results.Count; i++)
            {
                var hit = results[i];
                lines.Add(
                    $"{i + 1}) {BuildStorageResultTag(hit)} {hit.containerName} @ {FormatVector(hit.position)} | " +
                    $"Qty {hit.totalCount} | {hit.distance:0.0}m"
                );
            }

            response = string.Join("\n", lines);

            if (config.highlightSearchResults)
                HighlightSearchResults(results);

            return true;
        }

        private static void ParseSearchCommandArgs(List<string> args, ref int maxResults, ref float range, out List<string> queryTokens, out SearchResultFilter filter)
        {
            queryTokens = new List<string>();
            filter = SearchResultFilter.Default;
            if (args == null || args.Count == 0)
                return;

            var leftoverRange = range;
            var leftoverMax = maxResults;

            foreach (var token in args)
            {
                var normalized = token.Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (TryParseColonEqualsPair(normalized, out var key, out var value))
                {
                    if ((key.Equals("max", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("count", StringComparison.OrdinalIgnoreCase)) &&
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax) &&
                        parsedMax > 0)
                    {
                        leftoverMax = parsedMax;
                        continue;
                    }

                    if ((key.Equals("r", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("range", StringComparison.OrdinalIgnoreCase)) &&
                        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRange) &&
                        parsedRange >= 0f)
                    {
                        leftoverRange = parsedRange;
                        continue;
                    }

                    if (key.Equals("scope", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Equals("claim", StringComparison.OrdinalIgnoreCase))
                            filter.scope = SearchScopeFilter.ClaimOnly;
                        else if (value.Equals("range", StringComparison.OrdinalIgnoreCase))
                            filter.scope = SearchScopeFilter.RangeOnly;
                        else
                            filter.scope = SearchScopeFilter.All;
                        continue;
                    }

                    if (key.Equals("state", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Equals("locked", StringComparison.OrdinalIgnoreCase))
                            filter.state = SearchStateFilter.LockedOnly;
                        else if (value.Equals("unlocked", StringComparison.OrdinalIgnoreCase))
                            filter.state = SearchStateFilter.UnlockedOnly;
                        else
                            filter.state = SearchStateFilter.All;
                        continue;
                    }
                }

                if (TryParseScopeToken(normalized, out var scopeFilter))
                {
                    filter.scope = scopeFilter;
                    continue;
                }

                if (TryParseStateToken(normalized, out var stateFilter))
                {
                    filter.state = stateFilter;
                    continue;
                }

                if (int.TryParse(normalized, out var parsed) && parsed > 0 && queryTokens.Count == 0 && args.Count == 1)
                {
                    // Preserve legacy `/rsearch 8` behavior as max-result shorthand.
                    leftoverMax = parsed;
                    continue;
                }

                queryTokens.Add(normalized);
            }

            maxResults = Mathf.Max(1, Mathf.Min(50, leftoverMax));
            range = Mathf.Max(0f, leftoverRange);
        }

        private static bool TryParseScopeToken(string token, out SearchScopeFilter scope)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                scope = SearchScopeFilter.All;
                return false;
            }

            if (token.Equals("claim", StringComparison.OrdinalIgnoreCase))
            {
                scope = SearchScopeFilter.ClaimOnly;
                return true;
            }

            if (token.Equals("range", StringComparison.OrdinalIgnoreCase))
            {
                scope = SearchScopeFilter.RangeOnly;
                return true;
            }

            if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                scope = SearchScopeFilter.All;
                return true;
            }

            scope = SearchScopeFilter.All;
            return false;
        }

        private static bool TryParseStateToken(string token, out SearchStateFilter state)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                state = SearchStateFilter.All;
                return false;
            }

            if (token.Equals("locked", StringComparison.OrdinalIgnoreCase))
            {
                state = SearchStateFilter.LockedOnly;
                return true;
            }

            if (token.Equals("unlocked", StringComparison.OrdinalIgnoreCase))
            {
                state = SearchStateFilter.UnlockedOnly;
                return true;
            }

            if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                state = SearchStateFilter.All;
                return true;
            }

            state = SearchStateFilter.All;
            return false;
        }

        private static bool TryHandleStorageConfirmation(string argsText, out string response)
        {
            response = null;
            var seconds = config.storageUseConfirmationSeconds;
            if (!string.IsNullOrWhiteSpace(argsText) && float.TryParse(argsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0f)
            {
                seconds = parsed;
            }

            if (seconds <= 0f)
                seconds = 30f;

            var playerId = GetPlayerAuthorizationKey(GetPlatformPlayerId());
            storageUseApprovalUntil[playerId] = Time.time + seconds;

            response = $"[RangeCrafting] Storage-usage confirmation enabled for {seconds:0}s.";
            ShowStorageConfirmationStatusBanner(seconds);
            return true;
        }

        private static bool TryHandleStorageLogCommand(string argsText, out string response)
        {
            response = null;
            if (storageActionLog.Count == 0)
            {
                response = "[RangeCrafting] No activity yet.";
                return true;
            }

            int maxLines = 5;
            var request = argsText;
            if (!string.IsNullOrWhiteSpace(request) && int.TryParse(request, out var parsedLines) && parsedLines > 0)
                maxLines = Mathf.Clamp(parsedLines, 1, StorageActionLogLimit);

            var lines = storageActionLog.Reverse().Take(maxLines).Reverse().ToList();
            response = "[RangeCrafting] Recent craft/storage log:\n" + string.Join("\n", lines);
            return true;
        }

        private static float GetStorageUseApprovalRemainingSeconds()
        {
            if (!config.requireStorageUseConfirmation)
                return 0f;

            var player = GetPlatformPlayerId();
            if (player.Equals(default(PlatformUserIdentifierAbs)))
                return 0f;

            var key = GetPlayerAuthorizationKey(player);
            if (string.IsNullOrEmpty(key))
                return 0f;

            if (!storageUseApprovalUntil.TryGetValue(key, out var expiresAt))
                return 0f;

            var remaining = expiresAt - Time.time;
            if (remaining <= 0f)
            {
                storageUseApprovalUntil.Remove(key);
                return 0f;
            }

            return remaining;
        }

        private static void ShowStorageConfirmationStatusBanner(float seconds = -1f)
        {
            if (!config.requireStorageUseConfirmation || !config.modEnabled || !isReady)
                return;

            var remaining = seconds > 0f ? seconds : GetStorageUseApprovalRemainingSeconds();
            if (remaining <= 0f)
                return;

            if (Time.frameCount - lastStorageStatusFrame < StorageStatusBannerCooldownFrames)
                return;

            lastStorageStatusFrame = Time.frameCount;
            DeliverToPlayer($"[RangeCrafting] Storage confirmation active: {remaining:0.0}s remaining. Storage crafting from linked containers is enabled.", GetLocalPlayer());
        }

        private static void ShowStorageConfirmationNeededHint(ItemValue item, int fromStorage)
        {
            if (!config.requireStorageUseConfirmation || item == null || fromStorage <= 0)
                return;

            if (Time.frameCount - lastStorageUseHintFrame < StorageConfirmationHintCooldownFrames)
                return;

            lastStorageUseHintFrame = Time.frameCount;
            var name = ItemDisplayName(item);
            var remaining = GetStorageUseApprovalRemainingSeconds();
            if (remaining > 0f)
                DeliverToPlayer($"[RangeCrafting] {fromStorage}x {name} needs storage confirmation. {remaining:0.0}s left in approval window.", GetLocalPlayer());
            else
                DeliverToPlayer($"[RangeCrafting] {fromStorage}x {name} needs storage approval. Run /rconfirm {config.storageUseConfirmationSeconds:0}s.", GetLocalPlayer());
        }

        private static bool TryParseColonEqualsPair(string token, out string key, out string value)
        {
            key = null;
            value = null;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var index = token.IndexOf('=');
            var index2 = token.IndexOf(':');
            if (index < 0 || index >= token.Length - 1)
            {
                if (index2 < 0 || index2 >= token.Length - 1)
                    return false;
                index = index2;
            }

            key = token.Substring(0, index).Trim();
            value = token.Substring(index + 1).Trim();
            return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value);
        }

        private static List<string> ParseArguments(string argsText)
        {
            if (string.IsNullOrWhiteSpace(argsText))
                return new List<string>();

            return argsText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static string BuildSearchResultLegend()
        {
            return "Legend: [C]=claim scope, [R]=range scope, [L]=locked, [U]=unlocked";
        }

        private static string BuildSearchFilterSummary(SearchResultFilter filter, float range)
        {
            var scope = filter.scope switch
            {
                SearchScopeFilter.ClaimOnly => "claim",
                SearchScopeFilter.RangeOnly => "range",
                _ => "all"
            };

            var state = filter.state switch
            {
                SearchStateFilter.LockedOnly => "locked only",
                SearchStateFilter.UnlockedOnly => "unlocked only",
                _ => "all states"
            };

            var scopeHint = range > 0f ? $"range={range:0.0}m" : "default range";
            return $"Filters: scope={scope}, state={state}, {scopeHint}.";
        }

        private static string BuildStorageResultTag(StorageSearchHit hit)
        {
            var scope = hit.isInClaimScope ? "C" : "R";
            var lockState = hit.isLocked ? "L" : "U";
            return $"[{scope} {lockState}]";
        }

        private static StorageSearchHit BuildStorageSearchHit(object storage, Vector3i position, int totalCount, Vector3 playerPos)
        {
            return new StorageSearchHit
            {
                position = position,
                containerName = GetContainerName(storage, $"{position.x}, {position.y}, {position.z}"),
                totalCount = totalCount,
                distance = GetDistance(playerPos, position),
                isInClaimScope = IsInAnyAccessibleClaim(position),
                isLocked = IsStorageLocked(storage)
            };
        }

        private static bool PassesSearchFilters(StorageSearchHit hit, SearchResultFilter filter)
        {
            if (filter.scope == SearchScopeFilter.ClaimOnly && !hit.isInClaimScope)
                return false;

            if (filter.scope == SearchScopeFilter.RangeOnly && hit.isInClaimScope)
                return false;

            if (filter.state == SearchStateFilter.LockedOnly && !hit.isLocked)
                return false;

            if (filter.state == SearchStateFilter.UnlockedOnly && hit.isLocked)
                return false;

            return true;
        }

        private static bool IsStorageLocked(object storage)
        {
            if (storage == null)
                return false;

            if (storage is ILockable directLockable && directLockable.IsLocked())
                return true;

            if (storage is TileEntityComposite composite)
            {
                var lockable = composite.GetFeature<ILockable>();
                return lockable != null && lockable.IsLocked();
            }

            return false;
        }

        private static List<StorageSearchHit> FindContainersWithItem(string query, int maxResults, float rangeOverride, SearchResultFilter filter)
        {
            var hits = new List<StorageSearchHit>();
            ReloadStorages();

            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            var playerPos = player?.position ?? Vector3.zero;
            var range = Mathf.Max(0f, rangeOverride);

            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemType))
            {
                foreach (var kvp in currentStorageDict)
                {
                    var pos = kvp.Key;
                    var storage = kvp.Value;
                    if (!storageLocationInRange(playerPos, pos, range))
                        continue;

                    var count = CountItemInStorage(storage, itemType);
                    if (count > 0)
                    {
                        var hit = BuildStorageSearchHit(storage, pos, count, playerPos);
                        if (PassesSearchFilters(hit, filter))
                            hits.Add(hit);
                    }
                }
            }
            else
            {
                var normalizedQuery = query.Trim().ToLowerInvariant();
                foreach (var kvp in currentStorageDict)
                {
                    var pos = kvp.Key;
                    var storage = kvp.Value;
                    if (!storageLocationInRange(playerPos, pos, range))
                        continue;

                    var count = CountItemInStorage(storage, normalizedQuery);
                    if (count > 0)
                    {
                        var hit = BuildStorageSearchHit(storage, pos, count, playerPos);
                        if (PassesSearchFilters(hit, filter))
                            hits.Add(hit);
                    }
                }
            }

            return hits
                .OrderBy(h => h.distance)
                .ThenByDescending(h => h.totalCount)
                .Take(Mathf.Max(1, maxResults))
                .ToList();
        }

        private static float GetRangeForSearch()
        {
            if (config.searchRange > 0f)
                return config.searchRange;
            return GetRange();
        }

        private static bool storageLocationInRange(Vector3 playerPos, Vector3i storagePos, float range)
        {
            if (range <= 0f)
                return true;

            return GetDistance(playerPos, storagePos) <= range;
        }

        private static Vector3 GetCurrentPlayerPosition()
        {
            return GameManager.Instance?.World?.GetPrimaryPlayer()?.position ?? Vector3.zero;
        }

        private static float GetDistance(Vector3 from, Vector3i to)
        {
            return Vector3.Distance(from, new Vector3(to.x, to.y, to.z));
        }

        private static string FormatVector(Vector3i pos)
        {
            return $"{pos.x}, {pos.y}, {pos.z}";
        }

        private static string GetContainerName(object storage, string fallback)
        {
            if (storage == null)
                return fallback;

            try
            {
                var type = storage.GetType();
                var blockField = type.GetField("block", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (blockField != null)
                {
                    var block = blockField.GetValue(storage);
                    var blockNameProp = block?.GetType().GetProperty("blockName");
                    var blockName = blockNameProp?.GetValue(block) as string;
                    if (!string.IsNullOrWhiteSpace(blockName))
                        return blockName;

                    var blockNameField = block?.GetType().GetField("blockName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    blockName = blockNameField?.GetValue(block) as string;
                    if (!string.IsNullOrWhiteSpace(blockName))
                        return blockName;
                }
            }
            catch
            {
                // swallow; fallback remains.
            }

            return fallback;
        }

        private static int CountItemInStorage(object storage, int itemType)
        {
            var total = 0;
            foreach (var item in GetStorageItemStacks(storage))
            {
                if (item?.itemValue == null)
                    continue;
                if (item.itemValue.type == itemType)
                    total += item.count;
            }
            return total;
        }

        private static int CountItemInStorage(object storage, string queryLower)
        {
            var total = 0;
            foreach (var item in GetStorageItemStacks(storage))
            {
                if (item?.itemValue?.ItemClass == null)
                    continue;
                var displayName = item.itemValue.ItemClass.GetItemName();
                if (displayName != null && displayName.ToLowerInvariant().Contains(queryLower))
                    total += item.count;
            }
            return total;
        }

        private static int CountItemInPlayerInventory(string query, out string matchedName)
        {
            matchedName = query;
            if (string.IsNullOrWhiteSpace(query))
                return 0;

            var player = GetLocalPlayer();
            if (player == null)
                return 0;

            var invObj = GetPlayerInventory(player);
            if (invObj == null)
                return 0;

            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemType))
            {
                return SumInventoryByType(invObj, itemType);
            }

            var lowered = query.ToLowerInvariant();
            return SumInventoryByName(invObj, lowered, out matchedName);
        }

        private static object GetLocalPlayer()
        {
            return GameManager.Instance?.World?.GetPrimaryPlayer();
        }

        private static object GetPlayerInventory(object player)
        {
            if (player == null)
                return null;

            return ResolveMemberValue(player, "inventory", "Inv") ??
                   ResolveFieldValue(player, "inventory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private static int GetPlayerInventoryCount(ItemValue item)
        {
            if (item == null)
                return 0;

            var player = GetLocalPlayer();
            if (player == null)
                return 0;

            var inventory = GetPlayerInventory(player);
            if (inventory == null)
                return 0;

            return SumInventoryByType(inventory, item.type);
        }

        private static int SumInventoryByType(object inventory, int itemType)
        {
            var count = 0;
            foreach (var slot in ExtractItemStacksFromInventory(inventory))
            {
                if (slot?.itemValue != null && slot.itemValue.type == itemType)
                    count += slot.count;
            }
            return count;
        }

        private static int SumInventoryByName(object inventory, string query, out string matchedName)
        {
            matchedName = query;
            var count = 0;
            foreach (var slot in ExtractItemStacksFromInventory(inventory))
            {
                if (slot?.itemValue == null || slot.itemValue.ItemClass == null)
                    continue;

                var name = slot.itemValue.ItemClass.GetItemName();
                if (name != null && name.ToLowerInvariant().Contains(query))
                {
                    count += slot.count;
                    matchedName = name;
                }
            }
            return count;
        }

        private static IEnumerable<ItemStack> ExtractItemStacksFromInventory(object inventory)
        {
            if (inventory == null)
                return Enumerable.Empty<ItemStack>();

            var invType = inventory.GetType();
            var results = new List<ItemStack>();
            foreach (var methodName in new[] { "GetSlots", "GetAllItems", "GetItemStacks", "GetItems" })
            {
                var method = invType.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null)
                    continue;

                try
                {
                    var result = method.Invoke(inventory, null);
                    if (result == null)
                        continue;

                    if (result is IEnumerable enumerable)
                    {
                        foreach (var entry in enumerable.Cast<object>())
                        {
                            if (entry is ItemStack stack)
                                results.Add(stack);
                        }
                    }
                }
                catch
                {
                    // ignore and continue.
                }
            }

            var slotField = invType.GetField("slots", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (slotField != null)
            {
                if (slotField.GetValue(inventory) is IEnumerable slotsEnumerable)
                {
                    foreach (var entry in slotsEnumerable)
                    {
                        if (entry is ItemStack stack)
                            results.Add(stack);
                    }
                }
            }

            return results;
        }

        private static object ResolveMemberValue(object obj, string memberName, string fallbackMemberName = null)
        {
            if (obj == null)
                return null;

            var value = ResolveFieldValue(obj, memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (value != null)
                return value;

            if (!string.IsNullOrEmpty(fallbackMemberName))
                value = ResolveFieldValue(obj, fallbackMemberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return value;
        }

        private static object ResolveFieldValue(object obj, string memberName, BindingFlags flags)
        {
            if (obj == null)
                return null;

            var type = obj.GetType();
            while (type != null)
            {
                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    try { return field.GetValue(obj); } catch { }
                }
                type = type.BaseType;
            }
            return null;
        }

        private static void HighlightSearchResults(List<StorageSearchHit> results)
        {
            if (results == null || results.Count == 0)
                return;

            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
                return;

            var markerMethods = GetMarkerMethods(player.GetType());
            if (markerMethods == null || markerMethods.Length == 0)
                return;

            var toMark = results.Take(Mathf.Max(1, config.highlightMarkerLimit)).ToList();
            var markerDuration = Mathf.Max(1f, config.searchMarkerDuration);
            foreach (var hit in toMark)
            {
                var pos = new Vector3(hit.position.x, hit.position.y, hit.position.z);
                foreach (var markerMethod in markerMethods)
                {
                    var markerArgs = BuildMarkerArgs(markerMethod, pos, hit, markerDuration);
                    if (markerArgs == null)
                        continue;
                    try
                    {
                        markerMethod.Invoke(player, markerArgs);
                        break;
                    }
                    catch
                    {
                        // ignore.
                    }
                }
            }
        }

        private static MethodInfo[] GetMarkerMethods(Type targetType)
        {
            if (cachedMarkerMethods != null)
                return cachedMarkerMethods;

            var methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                    m.GetParameters().Length >= 2 &&
                    m.Name.IndexOf("marker", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.GetParameters().Any(p => p.ParameterType == typeof(Vector3) || p.ParameterType == typeof(Vector3i)))
                .ToArray();

            cachedMarkerMethods = methods;
            return methods;
        }

        private static object[] BuildMarkerArgs(MethodInfo method, Vector3 position, StorageSearchHit hit, float markerDuration)
        {
            var parms = method.GetParameters();
            var args = new object[parms.Length];
            var usedVector = false;
            var usedString = false;
            var usedDuration = false;

            for (var i = 0; i < parms.Length; i++)
            {
                var p = parms[i];
                if (!usedVector && (p.ParameterType == typeof(Vector3) || p.ParameterType == typeof(Vector3i)))
                {
                    args[i] = p.ParameterType == typeof(Vector3i)
                        ? new Vector3i((int)position.x, (int)position.y, (int)position.z)
                        : position;
                    usedVector = true;
                }
                else if (!usedString && p.ParameterType == typeof(string))
                {
                    args[i] = $"{BuildStorageResultTag(hit)} {hit.containerName} ({hit.totalCount}x)";
                    usedString = true;
                }
                else if (!usedDuration && p.ParameterType == typeof(float))
                {
                    args[i] = markerDuration;
                    usedDuration = true;
                }
                else if (p.ParameterType.IsValueType && p.ParameterType == typeof(int))
                {
                    args[i] = 0;
                }
                else if (p.ParameterType == typeof(bool))
                {
                    args[i] = true;
                }
                else
                {
                    args[i] = GetDefaultValueForType(p.ParameterType);
                }
            }

            if (!usedVector || !usedString)
                return null;
            return args;
        }

        private static object GetDefaultValueForType(Type t)
        {
            if (!t.IsValueType)
                return null;
            if (t == typeof(Color)) return Color.white;
            return Activator.CreateInstance(t);
        }

        private static void DeliverToPlayer(string message, object source)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
            {
                Debug.Log($"[RangeCrafting] {message}");
                return;
            }

            if (!TryInvokePlayerMessageMethod(player, "ShowNotification", message) &&
                !TryInvokePlayerMessageMethod(player, "AddHUDMessage", message) &&
                !TryInvokePlayerMessageMethod(player, "ShowInfo", message) &&
                !TryInvokePlayerMessageMethod(player, "ShowError", message) &&
                !TryInvokePlayerMessageMethod(player, "AddInfo", message) &&
                !TryInvokePlayerMessageMethod(player, "ShowDialog", message))
            {
                Debug.Log($"[RangeCrafting] {message}");
            }

            if (source != null && message.Length > 160)
            {
                Dbgl($"Search output for source {source.GetType().Name}: {message}");
            }
        }

        private static bool TryInvokePlayerMessageMethod(object player, string methodName, string message)
        {
            try
            {
                var methods = player.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));
                foreach (var method in methods)
                {
                    var pars = method.GetParameters();
                    if (pars.Length < 1 || pars[0].ParameterType != typeof(string))
                        continue;

                    if (pars.Length == 1)
                    {
                        method.Invoke(player, new object[] { message });
                        return true;
                    }

                    var args = new object[pars.Length];
                    args[0] = message;
                    for (int i = 1; i < pars.Length; i++)
                        args[i] = GetDefaultValueForType(pars[i].ParameterType);

                    method.Invoke(player, args);
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static void ApplyPatches()
        {
            TryPatchTranspiler("ItemActionEntryCraft", "OnActivated", nameof(ItemActionEntryCraft_OnActivated_Transpiler));
            TryPatchTranspiler("XUiM_PlayerInventory", "CanSwapItems", nameof(XUiM_PlayerInventory_CanSwapItems_Transpiler));
            TryPatchTranspiler("XUiM_PlayerInventory", "RemoveItems", nameof(XUiM_PlayerInventory_RemoveItems_Transpiler));
            TryPatchTranspiler("XUiC_RecipeCraftCount", "calcMaxCraftable", nameof(XUiC_RecipeCraftCount_calcMaxCraftable_Transpiler));
            TryPatchTranspiler("XUiC_IngredientEntry", "GetBindingValueInternal", nameof(XUiC_IngredientEntry_GetBindingValueInternal_Transpiler));
            TryPatchTranspiler("XUiM_PlayerInventory", "HasItems", nameof(XUiM_PlayerInventory_HasItems_Transpiler));
        }

        private static void TryPatchTranspiler(string typeName, string methodName, string transpilerMethodName)
        {
            if (!config.modEnabled)
                return;

            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                Dbgl($"Type missing ({typeName}) - patch skipped");
                return;
            }

            var method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Dbgl($"Method missing ({typeName}.{methodName}) - patch skipped");
                return;
            }

            var transpiler = typeof(ClaimLinkedCrafting).GetMethod(transpilerMethodName, BindingFlags.NonPublic | BindingFlags.Static);
            if (transpiler == null)
            {
                Dbgl($"Patch method missing ({transpilerMethodName})");
                return;
            }

            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                Dbgl($"Patched {typeName}.{methodName}");
            }
            catch (Exception ex)
            {
                Dbgl($"Failed patch {typeName}.{methodName}: {ex}");
            }
        }

        // Called from patches
        private static List<ItemStack> GetAllStorageStacksList(List<ItemStack> items)
        {
            if (!config.modEnabled || !isReady)
                return items;

            ReloadStorages();
            if (currentStorageDict.Count == 0)
                return items;

            var combined = new List<ItemStack>(items);
            combined.AddRange(GetStorageItems());
            return combined;
        }

        private static ItemStack[] GetAllStorageStacksArray(ItemStack[] items)
        {
            if (!config.modEnabled || !isReady)
                return items;

            return GetAllStorageStacksList(items.ToList()).ToArray();
        }

        private static int GetTrueRemaining(IList<ItemStack> _itemStacks, int i, int numLeft)
        {
            if (!config.modEnabled || !isReady)
                return numLeft;

            if (config.requireStorageUseConfirmation && !IsStorageUseApprovedForCurrentPlayer())
                return numLeft;

            return numLeft - GetAllItemCount(_itemStacks[i].itemValue);
        }

        private static void DecItemForRemoveItems(IList<ItemStack> _itemStacks, int i, int numLeft)
        {
            if (!config.modEnabled || !isReady)
                return;

            ReloadStorages();
            if (currentStorageDict.Count == 0)
                return;

            var itemStack = _itemStacks[i];
            Dbgl($"Attempting to remove {numLeft}x {itemStack.itemValue.ItemClass.GetItemName()} from linked storages");
            DecItem(itemStack.itemValue, numLeft);
        }

        private static int AddAllStorageCountItemValue(int count, ItemValue item)
        {
            if (!config.modEnabled || !isReady)
                return count;

            return count + GetRelevantItemCount(item);
        }

        private static int AddAllStorageCountIngEntry(int count, XUiC_IngredientEntry entry)
        {
            if (!config.modEnabled || !isReady || entry == null)
                return count;

            return AddAllStorageCountItemValue(count, entry.Ingredient.itemValue);
        }

        private static int AddAllStorageCountItemStack(int count, ItemStack itemStack)
        {
            if (!config.modEnabled || !isReady || itemStack == null)
                return count;

            return AddAllStorageCountItemValue(count, itemStack.itemValue);
        }

        private static void RemoveItemsFromLinkedStorage(int itemType, int countToRemove)
        {
            // kept for compatibility with older transpiler patterns
            DecItem(new ItemValue(itemType, false), countToRemove);
        }

        private static int GetAllItemCount(ItemValue item)
        {
            if (!config.modEnabled || !isReady || item == null)
                return 0;

            int count = 0;
            ReloadStorages();
            foreach (var storage in currentStorageDict.Values)
            {
                foreach (var stack in GetStorageItemStacks(storage))
                {
                    if (stack == null || stack.itemValue == null)
                        continue;
                    if (stack.itemValue.type == item.type)
                        count += stack.count;
                }
            }
            return count;
        }

        private static int GetRelevantItemCount(ItemValue item)
        {
            if (item == null)
                return 0;

            if (config.requireStorageUseConfirmation && !IsStorageUseApprovedForCurrentPlayer())
                return GetPlayerInventoryCount(item);

            var storageCount = GetAllItemCount(item);
            var playerCount = GetPlayerInventoryCount(item);

            return storageCount + playerCount;
        }

        private static int DecItem(ItemValue item, int count)
        {
            int numLeft = count;
            if (item == null || count <= 0)
                return 0;

            if (config.requireStorageUseConfirmation && !IsStorageUseApprovedForCurrentPlayer())
            {
                var playerCount = GetPlayerInventoryCount(item);
                var fromStorage = Mathf.Max(0, count - playerCount);
                if (fromStorage > 0)
                {
                    RecordStorageAction($"Blocked storage consume request: {fromStorage}x {ItemDisplayName(item)} from linked storages (run /rconfirm).");
                    ShowStorageConfirmationNeededHint(item, fromStorage);
                }

                return count;
            }

            foreach (var storage in GetStoragesInConsumptionOrder(item))
            {
                if (storage == null)
                    continue;

                if (storage is ITileEntityLootable tel)
                {
                    var telItems = tel.items;
                    if (telItems == null)
                        continue;

                    for (int i = 0; i < telItems.Length; i++)
                    {
                        if (telItems[i]?.itemValue == null || telItems[i].itemValue.type != item.type)
                            continue;

                        int take = Mathf.Min(numLeft, telItems[i].count);
                        numLeft -= take;
                        if (telItems[i].count <= take)
                            telItems[i].Clear();
                        else
                            telItems[i].count -= take;

                        tel.SetModified();
                        RecordStorageAction($"Took {take}x {ItemDisplayName(item)} from storage tile entity.");
                        if (numLeft <= 0)
                        {
                            RecordStorageAction($"Craft consumption complete for {ItemDisplayName(item)}: requested {count}, consumed {count}.");
                            return count;
                        }
                    }
                }
                else if (storage is Bag bag)
                {
                    var bagItems = bag.GetSlots();
                    if (bagItems == null)
                        continue;

                    for (int i = 0; i < bagItems.Length; i++)
                    {
                        if (bagItems[i]?.itemValue == null || bagItems[i].itemValue.type != item.type)
                            continue;

                        int take = Mathf.Min(numLeft, bagItems[i].count);
                        numLeft -= take;
                        if (bagItems[i].count <= take)
                            bagItems[i].Clear();
                        else
                            bagItems[i].count -= take;

                        bag.onBackpackChanged();
                        RecordStorageAction($"Took {take}x {ItemDisplayName(item)} from linked bag.");
                        if (numLeft <= 0)
                        {
                            RecordStorageAction($"Craft consumption complete for {ItemDisplayName(item)}: requested {count}, consumed {count}.");
                            return count;
                        }
                    }
                }
            }

            RecordStorageAction($"Craft consumption incomplete for {ItemDisplayName(item)}: requested {count}, consumed {count - numLeft}.");
            return count - numLeft;
        }

        private static IEnumerable<object> GetStoragesInConsumptionOrder(ItemValue itemFilter)
        {
            if (currentStorageDict.Count == 0)
                yield break;

            if (string.IsNullOrWhiteSpace(config.storageConsumptionOrder))
                config.storageConsumptionOrder = "nearest";

            var order = config.storageConsumptionOrder.Trim().ToLowerInvariant();
            var ordered = currentStorageDict.ToList();
            var playerPos = GetCurrentPlayerPosition();

            if (order == "farthest")
            {
                ordered.Sort((a, b) => GetDistance(playerPos, b.Key).CompareTo(GetDistance(playerPos, a.Key)));
            }
            else if (order == "name")
            {
                ordered.Sort((a, b) =>
                {
                    var nameA = GetContainerName(a.Value, $"{a.Key.x}, {a.Key.y}, {a.Key.z}");
                    var nameB = GetContainerName(b.Value, $"{b.Key.x}, {b.Key.y}, {b.Key.z}");
                    return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
                });
            }
            else if (order == "quantity")
            {
                ordered.Sort((a, b) =>
                {
                    var itemType = itemFilter.type;
                    var qtyA = itemType >= 0 ? CountItemInStorage(a.Value, itemType) : 0;
                    var qtyB = itemType >= 0 ? CountItemInStorage(b.Value, itemType) : 0;
                    if (qtyA == qtyB)
                        return GetDistance(playerPos, a.Key).CompareTo(GetDistance(playerPos, b.Key));
                    return qtyB.CompareTo(qtyA);
                });
            }
            else
            {
                ordered.Sort((a, b) => GetDistance(playerPos, a.Key).CompareTo(GetDistance(playerPos, b.Key)));
            }

            foreach (var entry in ordered)
                yield return entry.Value;
        }

        private static string ItemDisplayName(ItemValue item)
        {
            if (item == null)
                return "unknown";

            if (item.ItemClass != null)
                return item.ItemClass.GetItemName();

            return item.type.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsStorageUseApprovedForCurrentPlayer()
        {
            return IsStorageUseApprovedForPlayer(GetPlatformPlayerId());
        }

        private static bool IsStorageUseApprovedForPlayer(PlatformUserIdentifierAbs playerIdentifier)
        {
            if (!config.requireStorageUseConfirmation)
                return true;

            var key = GetPlayerAuthorizationKey(playerIdentifier);
            if (string.IsNullOrEmpty(key))
                return false;
            if (!storageUseApprovalUntil.TryGetValue(key, out var expiresAt))
                return false;

            if (Time.time > expiresAt)
            {
                storageUseApprovalUntil.Remove(key);
                return false;
            }

            return true;
        }

        private static string GetPlayerAuthorizationKey(PlatformUserIdentifierAbs playerIdentifier)
        {
            if (playerIdentifier.Equals(default(PlatformUserIdentifierAbs)))
            {
                var player = GetLocalPlayer();
                if (player == null)
                    return string.Empty;
                var identity = ResolveFieldValue(player, "steamId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) as string;
                return identity ?? "local";
            }

            return PlatformIdentifierToComparableString(playerIdentifier) ?? playerIdentifier.ToString();
        }

        private static void RecordStorageAction(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            storageActionLog.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
            while (storageActionLog.Count > StorageActionLogLimit)
                storageActionLog.Dequeue();

            if (config.isDebug)
                Dbgl(message);
        }

        private static List<ItemStack> GetStorageItems()
        {
            var list = new List<ItemStack>();
            foreach (var storage in currentStorageDict.Values)
                list.AddRange(GetStorageItemStacks(storage));
            return list;
        }

        private static IEnumerable<ItemStack> GetStorageItemStacks(object storage)
        {
            if (storage is ITileEntityLootable tel)
            {
                if (tel?.items == null)
                    yield break;
                foreach (var item in tel.items)
                {
                    if (item != null)
                        yield return item;
                }
            }
            else if (storage is Bag bag)
            {
                var slots = bag.GetSlots();
                if (slots == null)
                    yield break;
                foreach (var item in slots)
                {
                    if (item != null)
                        yield return item;
                }
            }
        }

        private static void ReloadStorages()
        {
            if (!config.modEnabled || !isReady)
                return;

            if (Time.frameCount - lastStorageRefreshFrame < Mathf.Max(1, config.scanCooldownFrames))
                return;

            lastStorageRefreshFrame = Time.frameCount;
            currentStorageDict.Clear();
            activeClaimCenters.Clear();

            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (world == null || player == null)
                return;

            var playerPos = player.position;
            if (config.mode == ClaimMode.ClaimOnly)
                RefreshAccessibleClaims(world, player, playerPos);

            float range = GetRange();
            bool canUseRangeFallback = config.mode == ClaimMode.RangeOnly || config.claimOnlyAllowRangeFallback;
            bool rangeCapConfigured = range > 0f;

            for (int i = 0; i < world.ChunkClusters.Count; i++)
            {
                var chunkCluster = world.ChunkClusters[i];
                foreach (var keyValue in chunkCluster.chunks.dict)
                {
                    var chunk = keyValue.Value;
                    chunk.EnterReadLock();
                    foreach (var tileEntityId in chunk.tileEntities.dict.Keys.ToArray())
                    {
                        if (!chunk.tileEntities.dict.TryGetValue(tileEntityId, out var tileEntity) || tileEntity == null)
                            continue;

                        var loc = tileEntity.ToWorldPos();
                        if (!ConfigMatchesRange(playerPos, loc, range))
                            continue;

                        if (IsClaimBlock(tileEntity, out var _, out var _))
                            continue;

                        if (!IsPlayerStorageTileEntity(tileEntity))
                            continue;

                        if (!IsStorageAccessibleByCurrentPlayer(tileEntity))
                            continue;

                        if (config.mode == ClaimMode.ClaimOnly && activeClaimCenters.Count > 0)
                        {
                            if (!IsInAnyAccessibleClaim(loc))
                                continue;
                        }
                        else if (config.mode == ClaimMode.ClaimOnly && canUseRangeFallback && rangeCapConfigured)
                        {
                            // optional fallback for versions where claim metadata is not exposed reliably
                            if (!ConfigMatchesRange(playerPos, loc, range))
                                continue;
                        }

                        currentStorageDict[loc] = tileEntity;
                    }
                    chunk.ExitReadLock();
                }
            }
        }

        private static bool IsClaimBlock(TileEntity tileEntity, out string ownerId, out Vector3i ownerPosition)
        {
            ownerId = null;
            ownerPosition = tileEntity.ToWorldPos();

            if (tileEntity?.block == null || tileEntity.block.blockName == null)
                return false;

            var blockName = tileEntity.block.blockName.ToLowerInvariant();
            foreach (var keyword in config.landClaimBlockNames)
            {
                if (blockName.Contains(keyword))
                    return true;
            }

            return false;
        }

        private static void RefreshAccessibleClaims(World world, EntityPlayerLocal player, Vector3 playerPos)
        {
            if (Time.frameCount - lastClaimRefreshFrame < Mathf.Max(1, config.claimRefreshCooldownFrames))
                return;

            lastClaimRefreshFrame = Time.frameCount;
            var localId = GetPlatformPlayerId();
            if (localId.Equals(default(PlatformUserIdentifierAbs)))
                return;

            float searchRadius = GetClaimBlockSearchRadius();

            for (int i = 0; i < world.ChunkClusters.Count; i++)
            {
                var chunkCluster = world.ChunkClusters[i];
                foreach (var keyValue in chunkCluster.chunks.dict)
                {
                    var chunk = keyValue.Value;
                    chunk.EnterReadLock();
                    foreach (var tileEntityId in chunk.tileEntities.dict.Keys.ToArray())
                    {
                        if (!chunk.tileEntities.dict.TryGetValue(tileEntityId, out var te))
                            continue;

                        var loc = te.ToWorldPos();
                        if (searchRadius > 0f && Vector3.Distance(playerPos, loc) > searchRadius)
                            continue;

                        if (!IsClaimBlock(te, out var owner, out _))
                            continue;

                        if (!CanUseClaimBlockForPlayer(te, localId, owner))
                            continue;

                        var c = te.ToWorldPos();
                        activeClaimCenters.Add(c);
                        Dbgl($"Accessible claim anchor detected at {c}");
                    }
                    chunk.ExitReadLock();
                }
            }

            if (activeClaimCenters.Count == 0 && config.claimOnlyAllowRangeFallback)
                Dbgl("No accessible claim anchors detected; using configured range fallback.");
        }

        private static bool CanUseClaimBlockForPlayer(TileEntity claimTileEntity, PlatformUserIdentifierAbs localId, string ownerId)
        {
            if (claimTileEntity == null || localId.Equals(default(PlatformUserIdentifierAbs)))
                return false;

            var owner = ownerId?.ToLowerInvariant();
            var local = PlatformIdentifierToComparableString(localId);
            if (config.permitClaimOwner && !string.IsNullOrEmpty(owner) && string.Equals(owner, local, StringComparison.OrdinalIgnoreCase))
                return true;

            if (IsClaimPermissionMatch(claimTileEntity, localId))
                return true;

            if (!config.requireOwnerMetadataMatch && string.IsNullOrEmpty(ownerId))
                return true;

            return false;
        }

        private static bool IsClaimPermissionMatch(TileEntity claimTileEntity, PlatformUserIdentifierAbs playerIdentifier)
        {
            if (claimTileEntity == null || playerIdentifier.Equals(default(PlatformUserIdentifierAbs)))
                return false;

            // Reflection-first approach: check any method or field exposing permission lists/ownership checks.
            var teType = claimTileEntity.GetType();
            foreach (var m in teType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.ReturnType != typeof(bool) || m.IsGenericMethod)
                    continue;

                var name = m.Name.ToLowerInvariant();
                if (!IsPermissionKeywordAllowed(name))
                    continue;

                var pars = m.GetParameters();
                if (pars.Length == 1)
                {
                    var playerIdAsString = PlatformIdentifierToComparableString(playerIdentifier);
                    if (string.IsNullOrEmpty(playerIdAsString))
                        return false;

                    object converted = null;
                    try { converted = ConvertParameter(pars[0].ParameterType, playerIdAsString); } catch { }
                    if (converted == null) continue;
                    if ((bool)m.Invoke(claimTileEntity, new[] { converted }))
                        return true;
                }
            }

            // Reflection for lists: fields/properties with names containing friend/allowed and array-like content.
            foreach (var m in teType.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!(m is FieldInfo fi))
                    continue;
                if (!IsPermissionKeywordAllowed(m.Name))
                    continue;

                object value = fi.GetValue(claimTileEntity);
                if (IsPlayerInEnumerable(value, playerIdentifier))
                    return true;
            }

            return false;
        }

        private static bool IsPermissionKeywordAllowed(string lowerName)
        {
            var name = lowerName?.ToLowerInvariant() ?? string.Empty;
            if (name.Contains("friend") || name.Contains("buddy"))
                return config.permitClaimFriend;

            if (name.Contains("ally"))
                return config.permitClaimAlly;

            if (name.Contains("party") || name.Contains("group") || name.Contains("guild") || name.Contains("clan") || name.Contains("faction"))
                return config.permitClaimParty || config.permitClaimClan;

            if (name.Contains("allowed") || name.Contains("allow") || name.Contains("access") || name.Contains("permission"))
                return config.permitClaimFriend || config.permitClaimAlly || config.permitClaimParty || config.permitClaimClan;

            return false;
        }

        private static bool IsPlayerInEnumerable(object value, PlatformUserIdentifierAbs playerIdentifier)
        {
            if (value == null || playerIdentifier.Equals(default(PlatformUserIdentifierAbs)))
                return false;

            var comparable = PlatformIdentifierToComparableString(playerIdentifier);
            if (string.IsNullOrEmpty(comparable))
                return false;

            if (value is string s)
            {
                if (s.IndexOf(comparable, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            if (value is IEnumerable e)
            {
                foreach (var x in e)
                {
                    if (PlatformIdentifierMatch(x, playerIdentifier))
                        return true;
                }
            }

            return false;
        }

        private static object ConvertParameter(Type targetType, string playerIdentifier)
        {
            if (targetType == typeof(string))
                return playerIdentifier;
            if (targetType == typeof(int) || targetType == typeof(int?))
                return int.TryParse(playerIdentifier, out var i) ? i : (int?)null;
            if (targetType == typeof(long) || targetType == typeof(long?))
                return long.TryParse(playerIdentifier, out var l) ? l : (long?)null;
            if (targetType == typeof(PlatformUserIdentifierAbs))
                return ParsePlatformIdentifier(playerIdentifier);
            if (targetType == typeof(object))
                return playerIdentifier;
            return null;
        }

        private static PlatformUserIdentifierAbs ParsePlatformIdentifier(string playerIdentifier)
        {
            if (string.IsNullOrEmpty(playerIdentifier))
                return default;

            try
            {
                var fromCombined = typeof(PlatformUserIdentifierAbs).GetMethod(
                    "FromCombinedString",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(bool) },
                    null);
                if (fromCombined != null)
                {
                    var parsed = fromCombined.Invoke(null, new object[] { playerIdentifier, false });
                    if (parsed is PlatformUserIdentifierAbs parsedId)
                        return parsedId;
                }
            }
            catch
            {
                // ignore
            }

            return default;
        }

        private static bool PlatformIdentifierMatch(object candidate, PlatformUserIdentifierAbs playerIdentifier)
        {
            if (candidate == null || playerIdentifier.Equals(default(PlatformUserIdentifierAbs)))
                return false;

            if (candidate is PlatformUserIdentifierAbs candidateIdentifier)
                return candidateIdentifier.Equals(playerIdentifier);

            var candidateText = Convert.ToString(candidate);
            var compare = PlatformIdentifierToComparableString(playerIdentifier);
            if (string.IsNullOrEmpty(compare) || string.IsNullOrEmpty(candidateText))
                return false;

            return string.Equals(candidateText, compare, StringComparison.OrdinalIgnoreCase);
        }

        private static string PlatformIdentifierToComparableString(PlatformUserIdentifierAbs playerIdentifier)
        {
            if (playerIdentifier.Equals(default(PlatformUserIdentifierAbs)))
                return null;

            return playerIdentifier.PlatformIdentifierString ??
                   playerIdentifier.ReadablePlatformUserIdentifier ??
                   playerIdentifier.ToString();
        }

        private static bool IsInAnyAccessibleClaim(Vector3i loc)
        {
            return IsInAnyAccessibleClaim(new Vector3(loc.x, loc.y, loc.z));
        }

        private static bool IsInAnyAccessibleClaim(Vector3 loc)
        {
            if (activeClaimCenters.Count == 0)
                return false;
            float radius = Mathf.Max(1f, config.claimRadius);
            foreach (var claimCenter in activeClaimCenters)
            {
                if (Vector3.Distance(loc, claimCenter) <= radius)
                    return true;
            }
            return false;
        }

        private static bool IsPlayerStorageTileEntity(TileEntity tileEntity)
        {
            if (tileEntity == null)
                return false;

            if (!IsStorageTypeAllowed(tileEntity))
                return false;

            if (tileEntity is TileEntityComposite entityComposite)
            {
                var lootable = entityComposite.GetFeature<ITileEntityLootable>();
                if (lootable != null && lootable.bPlayerStorage)
                {
                    var lockable = entityComposite.GetFeature<ILockable>();
                    if (lockable != null && lockable.IsLocked())
                    {
                        if (lockable.IsUserAllowed(GetPlatformPlayerId()))
                            return true;

                        return false;
                    }
                    return true;
                }
            }
            else if (tileEntity is TileEntitySecureLootContainer secure && IsSecureStorageTileEntity(secure))
            {
                if (secure is ILockable lockable && lockable.IsLocked())
                {
                    if (!lockable.IsUserAllowed(GetPlatformPlayerId()))
                        return false;
                }
                return true;
            }
            else if (tileEntity is TileEntityLootContainer lootContainer && (config.allowAllContainers || lootContainer.bPlayerStorage))
            {
                return true;
            }
            return false;
        }

        private static bool IsStorageTypeAllowed(TileEntity tileEntity)
        {
            var blockName = GetContainerName(
                tileEntity,
                tileEntity?.block?.blockName ?? tileEntity?.GetType().Name ?? string.Empty
            ).ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(blockName))
                return true;

            var allowedFilters = NormalizeFilterList(config.allowedStorageContainerNames);
            if (allowedFilters.Length > 0)
            {
                if (!MatchesAnyFilter(blockName, allowedFilters))
                    return false;
            }

            var blockedFilters = NormalizeFilterList(config.blockedStorageContainerNames);
            if (MatchesAnyFilter(blockName, blockedFilters))
                return false;

            return true;
        }

        private static bool MatchesAnyFilter(string text, string[] filters)
        {
            if (string.IsNullOrWhiteSpace(text) || filters == null || filters.Length == 0)
                return false;

            for (var i = 0; i < filters.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(filters[i]))
                    continue;
                if (text.Contains(filters[i]))
                    return true;
            }

            return false;
        }

        private static string[] NormalizeFilterList(string[] filters)
        {
            if (filters == null || filters.Length == 0)
                return Array.Empty<string>();

            var normalized = new List<string>();
            foreach (var filter in filters)
            {
                if (string.IsNullOrWhiteSpace(filter))
                    continue;
                var clean = filter.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(clean) && !normalized.Contains(clean))
                    normalized.Add(clean);
            }

            return normalized.ToArray();
        }

        private static bool IsSecureStorageTileEntity(TileEntitySecureLootContainer container)
        {
            if (container == null)
                return false;

            var storageField = container.GetType().GetField("bPlayerStorage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (storageField != null && storageField.GetValue(container) is bool isPlayerStorageField)
                return isPlayerStorageField;

            var storageProperty = container.GetType().GetProperty("bPlayerStorage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (storageProperty != null && storageProperty.GetValue(container) is bool isPlayerStorageProperty)
                return isPlayerStorageProperty;

            return false;
        }

        private static bool IsStorageAccessibleByCurrentPlayer(TileEntity tileEntity)
        {
            if (tileEntity == null)
                return false;

            if (tileEntity is ILockable lockable && lockable.IsLocked())
            {
                return lockable.IsUserAllowed(GetPlatformPlayerId());
            }

            return true;
        }

        private static bool ConfigMatchesRange(Vector3 playerPos, Vector3i point, float range)
        {
            if (range <= 0f)
                return true;
            return Vector3.Distance(playerPos, point) <= range;
        }

        private static float GetRange()
        {
            if (config.mode == ClaimMode.Disabled)
                return 0f;

            if (config.mode == ClaimMode.RangeOnly)
                return Mathf.Max(0f, config.range);

            // claim-only mode with optional fallback
            return Mathf.Max(0f, config.range);
        }

        private static float GetClaimBlockSearchRadius()
        {
            return Mathf.Max(0f, config.claimSearchRadius <= 0f ? (config.claimRadius * 2f + 10f) : config.claimSearchRadius);
        }

        private static PlatformUserIdentifierAbs GetPlatformPlayerId()
        {
            return PlatformManager.InternalLocalUserIdentifier;
        }

        // --- Harmony transpilers ---
        private static IEnumerable<CodeInstruction> ItemActionEntryCraft_OnActivated_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && IsCallToGetAllItemStacks(codes[i].operand))
                {
                    Dbgl("Patching ItemActionEntryCraft.OnActivated for claim-linked storage");
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.GetAllStorageStacksList))));
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        private static IEnumerable<CodeInstruction> XUiM_PlayerInventory_CanSwapItems_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call && IsCallToGetAllItemStacks(codes[i].operand))
                {
                    Dbgl("Patching XUiM_PlayerInventory.CanSwapItems for claim-linked storage");
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.GetAllStorageStacksList))));
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        private static IEnumerable<CodeInstruction> XUiC_RecipeCraftCount_calcMaxCraftable_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && IsCallToGetAllItemStacks(codes[i].operand))
                {
                    Dbgl("Patching XUiC_RecipeCraftCount.calcMaxCraftable for claim-linked storage");
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.GetAllStorageStacksArray))));
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        private static IEnumerable<CodeInstruction> XUiC_IngredientEntry_GetBindingValueInternal_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && IsCallToItemCount(codes[i].operand))
                {
                    Dbgl("Patching XUiC_IngredientEntry.GetBindingValueInternal for claim-linked counts");
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.AddAllStorageCountIngEntry))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        private static IEnumerable<CodeInstruction> XUiM_PlayerInventory_RemoveItems_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && IsCallToInventoryDecItem(codes[i].operand))
                {
                    Dbgl("Patching XUiM_PlayerInventory.RemoveItems for claim-linked storage");
                    var marker = new CodeInstruction(codes[i + 3]);
                    codes.Insert(i + 3, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.DecItemForRemoveItems))));
                    codes.Insert(i + 3, new CodeInstruction(OpCodes.Ldloc_1));
                    codes.Insert(i + 3, new CodeInstruction(OpCodes.Ldloc_0));
                    codes.Insert(i + 3, marker);
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        private static IEnumerable<CodeInstruction> XUiM_PlayerInventory_HasItems_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 2; i < codes.Count - 1; i++)
            {
                if (codes[i].opcode == OpCodes.Ldc_I4_0 && codes[i + 1].opcode == OpCodes.Ret)
                {
                    Dbgl("Patching XUiM_PlayerInventory.HasItems for claim-linked remaining count");
                    codes.Insert(i, new CodeInstruction(codes[i - 1]));
                    codes.Insert(i, new CodeInstruction(codes[i - 2]));
                    codes.Insert(i, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.GetTrueRemaining))));
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldloc_1));
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldloc_0));
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_1));
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        private static bool IsCallToGetAllItemStacks(object operand)
        {
            return operand is MethodInfo m && m == playerInventoryGetAllItemStacks;
        }

        private static bool IsCallToItemCount(object operand)
        {
            if (operand is not MethodInfo method)
                return false;

            if (method.Name != "GetItemCount")
                return false;
            if (method.DeclaringType == null)
                return false;
            if (method.DeclaringType.Name != "XUiM_PlayerInventory" && method.DeclaringType.Name != "Bag")
                return false;

            return true;
        }

        private static bool IsCallToInventoryDecItem(object operand)
        {
            if (operand is not MethodInfo method)
                return false;
            if (method.Name != "DecItem")
                return false;
            return method.DeclaringType?.Name == "Bag" || method.DeclaringType?.Name == "Inventory";
        }
    }

    public static class ConfigSanitizer
    {
        public static ModConfig Sanitize(ModConfig c)
        {
            if (!Enum.IsDefined(typeof(ClaimMode), c.mode))
                c.mode = ClaimMode.ClaimOnly;

            c.mode = NormalizeMode(c.mode);
            if (c.scanCooldownFrames <= 0)
                c.scanCooldownFrames = 6;
            if (c.claimRefreshCooldownFrames <= 0)
                c.claimRefreshCooldownFrames = 10;
            if (c.claimRadius <= 0)
                c.claimRadius = 20;
            if (c.range < 0)
                c.range = 0;
            if (c.searchMaxResults <= 0)
                c.searchMaxResults = 8;
            if (c.searchRange < 0)
                c.searchRange = 0;
            if (c.searchMarkerDuration < 1f)
                c.searchMarkerDuration = 30f;
            if (c.highlightMarkerLimit <= 0)
                c.highlightMarkerLimit = 4;
            if (c.searchCommandCooldownFrames <= 0)
                c.searchCommandCooldownFrames = 10;
            if (string.IsNullOrWhiteSpace(c.storageConsumptionOrder))
                c.storageConsumptionOrder = "nearest";
            var storageOrder = c.storageConsumptionOrder.Trim().ToLowerInvariant();
            if (storageOrder != "nearest" && storageOrder != "farthest" && storageOrder != "name" && storageOrder != "quantity")
                c.storageConsumptionOrder = "nearest";
            if (!c.permitClaimOwner && !c.permitClaimFriend && !c.permitClaimAlly && !c.permitClaimParty && !c.permitClaimClan)
                c.permitClaimOwner = true;
            if (c.landClaimBlockNames == null || c.landClaimBlockNames.Length == 0)
                c.landClaimBlockNames = new[] { "landclaim", "claimblock", "deed", "claim block" };
            if (string.IsNullOrWhiteSpace(c.permissionProfile))
                c.permissionProfile = "vanilla";
            else
                c.permissionProfile = c.permissionProfile.Trim();
            if (c.storageUseConfirmationSeconds <= 0f)
                c.storageUseConfirmationSeconds = 30f;
            c.allowedStorageContainerNames = NormalizeFilters(c.allowedStorageContainerNames);
            c.blockedStorageContainerNames = NormalizeFilters(c.blockedStorageContainerNames);
            return c;
        }

        private static string[] NormalizeFilters(string[] filters)
        {
            if (filters == null || filters.Length == 0)
                return Array.Empty<string>();

            var normalized = new List<string>(filters.Length);
            foreach (var filter in filters)
            {
                if (string.IsNullOrWhiteSpace(filter))
                    continue;

                var clean = filter.Trim().ToLowerInvariant();
                if (!normalized.Contains(clean))
                    normalized.Add(clean);
            }

            return normalized.ToArray();
        }

        private static ClaimMode NormalizeMode(ClaimMode mode)
        {
            return mode switch
            {
                ClaimMode.ClaimOnly => ClaimMode.ClaimOnly,
                ClaimMode.RangeOnly => ClaimMode.RangeOnly,
                ClaimMode.Disabled => ClaimMode.Disabled,
                _ => ClaimMode.ClaimOnly
            };
        }

        public static ModConfig ApplyPermissionProfile(ModConfig c)
        {
            if (c == null)
                return c;

            if (string.IsNullOrWhiteSpace(c.permissionProfile))
                return c;

            var profile = c.permissionProfile.Trim().ToLowerInvariant();
            if (profile == "custom")
                return c;

            switch (profile)
            {
                case "friendsonly":
                case "friend":
                    c.permitClaimOwner = true;
                    c.permitClaimFriend = true;
                    c.permitClaimAlly = false;
                    c.permitClaimParty = false;
                    c.permitClaimClan = false;
                    break;
                case "trustedonly":
                case "vanilla":
                    c.permitClaimOwner = true;
                    c.permitClaimFriend = true;
                    c.permitClaimAlly = true;
                    c.permitClaimParty = true;
                    c.permitClaimClan = true;
                    break;
                case "allies":
                case "allysonly":
                    c.permitClaimOwner = true;
                    c.permitClaimFriend = true;
                    c.permitClaimAlly = true;
                    c.permitClaimParty = false;
                    c.permitClaimClan = false;
                    break;
                case "owneronly":
                    c.permitClaimOwner = true;
                    c.permitClaimFriend = false;
                    c.permitClaimAlly = false;
                    c.permitClaimParty = false;
                    c.permitClaimClan = false;
                    break;
                default:
                    break;
            }

            return c;
        }
    }
}
