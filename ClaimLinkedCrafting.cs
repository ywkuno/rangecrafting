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
        private static readonly string[] SearchCommandAliases = new[] { "rsearch", "rangesearch", "rangecrafting", "rc" };
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
                    File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                    return;
                }

                config = JsonConvert.DeserializeObject<ModConfig>(File.ReadAllText(path)) ?? new ModConfig();
                config = ConfigSanitizer.Sanitize(config);
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

            if (!IsSearchCommand(text, out var queryText))
                return true;

            lastSearchCommandFrame = Time.frameCount;
            var handled = TryHandleRangeSearch(queryText, out var response);
            if (!string.IsNullOrEmpty(response))
            {
                DeliverToPlayer(response, __instance);
            }

            if (!handled)
                return true;
            return false;
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

        private static bool IsSearchCommand(string text, out string argsText)
        {
            argsText = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (!text.StartsWith("/", StringComparison.Ordinal))
                return false;

            var trimmed = text.Trim();
            var parts = trimmed.Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            var command = parts[0];
            if (SearchCommandAliases.All(a => !command.Equals(a, StringComparison.OrdinalIgnoreCase)))
                return false;

            argsText = parts.Length <= 1
                ? string.Empty
                : trimmed.Substring(trimmed.IndexOf(' ') + 1).Trim();
            return true;
        }

        private static bool TryHandleRangeSearch(string argsText, out string response)
        {
            response = null;
            if (!config.enableRangeSearch || !config.modEnabled)
                return true;

            if (string.IsNullOrWhiteSpace(argsText))
            {
                response = "Usage: /rsearch <item name or item id> [max results]";
                return true;
            }

            var args = ParseArguments(argsText);
            if (args.Count == 0)
            {
                response = "Usage: /rsearch <item name or item id> [max results]";
                return true;
            }

            int maxResults = config.searchMaxResults;
            var queryTokens = new List<string>(args);
            if (int.TryParse(queryTokens[^1], out var parsedMax) && parsedMax > 0)
            {
                maxResults = parsedMax;
                queryTokens.RemoveAt(queryTokens.Count - 1);
            }

            var query = string.Join(" ", queryTokens).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                response = "Usage: /rsearch <item name or item id> [max results]";
                return true;
            }

            var results = FindContainersWithItem(query, maxResults);
            var playerCount = CountItemInPlayerInventory(query, out _);

            if (results == null || results.Count == 0)
            {
                if (playerCount > 0)
                    response = $"Found only in player inventory: {playerCount}x {query}.";
                else
                    response = $"No matching items found in claim-linked storages/radius for '{query}'.";
                return true;
            }

            var lines = new List<string>();
            lines.Add(playerCount > 0
                ? $"Player inventory: {playerCount}x"
                : "Player inventory: 0x");
            lines.Add($"Search: '{query}' ({results.Count} container(s))");
            for (var i = 0; i < results.Count; i++)
            {
                var hit = results[i];
                lines.Add(
                    $"{i + 1}) {hit.containerName} @ {FormatVector(hit.position)} | " +
                    $"Qty {hit.totalCount} | {hit.distance:0.0}m"
                );
            }

            response = string.Join("\n", lines);

            if (config.highlightSearchResults)
                HighlightSearchResults(results);

            return true;
        }

        private static List<string> ParseArguments(string argsText)
        {
            if (string.IsNullOrWhiteSpace(argsText))
                return new List<string>();

            return argsText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static List<StorageSearchHit> FindContainersWithItem(string query, int maxResults)
        {
            var hits = new List<StorageSearchHit>();
            ReloadStorages();

            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            var playerPos = player?.position ?? Vector3.zero;
            var range = GetRangeForSearch();

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
                        hits.Add(new StorageSearchHit
                        {
                            position = pos,
                            containerName = GetContainerName(storage, $"Container@{FormatVector(pos)}"),
                            totalCount = count,
                            distance = GetDistance(playerPos, pos)
                        });
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
                        hits.Add(new StorageSearchHit
                        {
                            position = pos,
                            containerName = GetContainerName(storage, $"Container@{FormatVector(pos)}"),
                            totalCount = count,
                            distance = GetDistance(playerPos, pos)
                        });
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

            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (player == null)
                return 0;

            var invObj = ResolveMemberValue(player, "inventory", "Inv") ??
                         ResolveFieldValue(player, "inventory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (invObj == null)
                return 0;

            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemType))
            {
                return SumInventoryByType(invObj, itemType);
            }

            var lowered = query.ToLowerInvariant();
            return SumInventoryByName(invObj, lowered, out matchedName);
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
                yield break;

            var invType = inventory.GetType();
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
                                yield return stack;
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
                            yield return stack;
                    }
                }
            }
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
                    args[i] = $"{hit.containerName} ({hit.totalCount}x)";
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

            return count + GetAllItemCount(item);
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

        private static int DecItem(ItemValue item, int count)
        {
            int numLeft = count;

            foreach (var storage in currentStorageDict.Values.ToList())
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
                        if (numLeft <= 0)
                            return count;
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
                        if (numLeft <= 0)
                            return count;
                    }
                }
            }

            return count - numLeft;
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
            if (string.IsNullOrEmpty(localId))
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

        private static bool CanUseClaimBlockForPlayer(TileEntity claimTileEntity, string localId, string ownerId)
        {
            if (claimTileEntity == null || string.IsNullOrEmpty(localId))
                return false;

            if (config.permitClaimOwner && !string.IsNullOrEmpty(ownerId) && string.Equals(ownerId, localId, StringComparison.Ordinal))
                return true;

            if (IsClaimPermissionMatch(claimTileEntity, localId))
                return true;

            if (!config.requireOwnerMetadataMatch && string.IsNullOrEmpty(ownerId))
                return true;

            return false;
        }

        private static bool IsClaimPermissionMatch(TileEntity claimTileEntity, string playerIdentifier)
        {
            if (claimTileEntity == null || string.IsNullOrEmpty(playerIdentifier))
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
                    object converted = null;
                    try { converted = ConvertParameter(pars[0].ParameterType, playerIdentifier); } catch { }
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

        private static bool IsPlayerInEnumerable(object value, string playerIdentifier)
        {
            if (value == null || string.IsNullOrEmpty(playerIdentifier))
                return false;

            if (value is string s)
            {
                if (s.IndexOf(playerIdentifier, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            if (value is IEnumerable e)
            {
                foreach (var x in e)
                {
                    if (x == null)
                        continue;
                    if (string.Equals(Convert.ToString(x), playerIdentifier, StringComparison.OrdinalIgnoreCase))
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
            if (targetType == typeof(PlatformUserIdentifierAbs) || targetType == typeof(PlatformUserIdentifierAbs?))
                return null;
            if (targetType == typeof(object))
                return playerIdentifier;
            return null;
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
            else if (tileEntity is TileEntitySecureLootContainer secure && secure.IsPlayerStorage())
            {
                if (secure.IsLocked())
                {
                    if (!secure.IsUserAllowed(GetPlatformPlayerId()))
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

        private static string GetPlatformPlayerId()
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
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.GetAllStorageStacksList)));
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
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.GetAllStorageStacksList)));
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
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClaimLinkedCrafting), nameof(ClaimLinkedCrafting.GetAllStorageStacksArray)));
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
            if (!c.permitClaimOwner && !c.permitClaimFriend && !c.permitClaimAlly && !c.permitClaimParty && !c.permitClaimClan)
                c.permitClaimOwner = true;
            if (c.landClaimBlockNames == null || c.landClaimBlockNames.Length == 0)
                c.landClaimBlockNames = new[] { "landclaim", "claimblock", "deed", "claim block" };
            return c;
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
    }
}
