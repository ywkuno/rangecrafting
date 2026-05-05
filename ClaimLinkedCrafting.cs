using HarmonyLib;
using Newtonsoft.Json;
using Platform;
using System;
using System.Collections;
using System.Collections.Generic;
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

        private static MethodInfo playerInventoryGetAllItemStacks;
        private static MethodInfo bagGetItemCount;
        private static bool isReady;

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

            if (!string.IsNullOrEmpty(ownerId) && string.Equals(ownerId, localId, StringComparison.Ordinal))
                return true;

            if (IsFriendOnClaimEntity(claimTileEntity, localId))
                return true;

            if (!config.requireOwnerMetadataMatch && string.IsNullOrEmpty(ownerId))
                return true;

            return false;
        }

        private static bool IsFriendOnClaimEntity(TileEntity claimTileEntity, string playerIdentifier)
        {
            if (claimTileEntity == null || string.IsNullOrEmpty(playerIdentifier))
                return false;

            // Reflection-first approach: check any method like IsFriend/IsAllowed that can evaluate ownership.
            var teType = claimTileEntity.GetType();
            foreach (var m in teType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.ReturnType != typeof(bool) || m.IsGenericMethod)
                    continue;

                var name = m.Name.ToLowerInvariant();
                if (!name.Contains("friend") && !name.Contains("allowed") && !name.Contains("access"))
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
                if (!m.Name.ToLowerInvariant().Contains("friend") && !m.Name.ToLowerInvariant().Contains("allow"))
                    continue;

                object value = fi.GetValue(claimTileEntity);
                if (IsPlayerInEnumerable(value, playerIdentifier))
                    return true;
            }

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
