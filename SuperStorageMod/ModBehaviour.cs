using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Duckov.Economy;
using Duckov.Modding;
using Duckov.PerkTrees;
using Duckov.PerkTrees.Interactable;
using NodeCanvas.Framework;
using Saves;
using SodaCraft.Localizations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SuperStorageMod
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const int SMALL_BOX_ID = 50;
        private const int MED_BOX_ID = 49;
        private const int BIG_BOX_ID = 48;
        private const float INJECT_DELAY_SECONDS = 1.5f;
        private const string PERK_NAME_PREFIX = "SuperStorage_";
        private const string BACKUP_DIR_NAME = "SuperStorageMod";
        private const string BACKUP_FILE_PREFIX = "backup_slot_";
        private const string BACKUP_FILE_EXT = ".json";
        private const string CUSTOM_TREE_ID = "SuperStorageExpand";
        private const string OFFICIAL_TREE_ID = "StorageExpand";
        private const string CUSTOM_INTERACT_KEY = "SuperStorage_InteractName";
        private const int MOD_DATA_VERSION = 1;

        private static readonly (string nameKey, string displayName, int addCap, int requireLevel, long money, (int id, int amount)[] items)[] Tiers = new[]
        {
            ("SuperStorage_Lv2",  "超级仓库Lv.2",  150, 30, 600_000L,   new[]{ (SMALL_BOX_ID,9),  (MED_BOX_ID,6),  (BIG_BOX_ID,3) }),
            ("SuperStorage_Lv3",  "超级仓库Lv.3",  200, 30, 700_000L,   new[]{ (SMALL_BOX_ID,12), (MED_BOX_ID,8),  (BIG_BOX_ID,4) }),
            ("SuperStorage_Lv4",  "超级仓库Lv.4",  250, 30, 800_000L,   new[]{ (SMALL_BOX_ID,15), (MED_BOX_ID,10), (BIG_BOX_ID,5) }),
            ("SuperStorage_Lv5",  "超级仓库Lv.5",  300, 30, 900_000L,   new[]{ (SMALL_BOX_ID,18), (MED_BOX_ID,12), (BIG_BOX_ID,6) }),
            ("SuperStorage_Lv6",  "超级仓库Lv.6",  350, 30, 1_000_000L, new[]{ (SMALL_BOX_ID,21), (MED_BOX_ID,14), (BIG_BOX_ID,7) }),
            ("SuperStorage_Lv7",  "超级仓库Lv.7",  450, 30, 1_200_000L, new[]{ (SMALL_BOX_ID,24), (MED_BOX_ID,16), (BIG_BOX_ID,8) }),
            ("SuperStorage_Lv8",  "超级仓库Lv.8",  500, 30, 1_400_000L, new[]{ (SMALL_BOX_ID,27), (MED_BOX_ID,18), (BIG_BOX_ID,9) }),
            ("SuperStorage_Lv9",  "超级仓库Lv.9",  550, 35, 1_600_000L, new[]{ (SMALL_BOX_ID,30), (MED_BOX_ID,20), (BIG_BOX_ID,10) }),
            ("SuperStorage_Lv10", "超级仓库Lv.10", 600, 40, 2_000_000L, new[]{ (SMALL_BOX_ID,33), (MED_BOX_ID,22), (BIG_BOX_ID,11) })
        };

        private static readonly Dictionary<string, Type?> TypeCache = new Dictionary<string, Type?>();
        private static readonly Dictionary<(Type, string), FieldInfo?> FieldCache = new Dictionary<(Type, string), FieldInfo?>();

        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        private bool _injected;

        private static Action? _onLevelInitializedEvent;
        private static FieldInfo? _perkTreesField;
        private static FieldInfo? _onUnlockStateChangedField;
        private static bool _backupDirCreated;

        private static int? _cachedSlot;
        private static int _cachedBackupCapacity = -1;
        private static DateTime _backupFileLastWrite;

        private PerkTree? _myCachedTree;

        private PerkTree? MyTree
        {
            get
            {
                if (_myCachedTree == null)
                {
                    _myCachedTree = FindCustomTreeQuietly();
                }
                return _myCachedTree;
            }
        }

        // 与 PerkTreeManager.GetPerkTree 相同的查找逻辑，但不会在找不到时打印 Error 日志。
        // 模组在注入完成前查询自定义树属于正常流程（此时树尚未创建），不应触发游戏内部的 LogError 噪音。
        private static PerkTree? FindCustomTreeQuietly()
        {
            var mgr = PerkTreeManager.Instance;
            if (mgr == null || mgr.perkTrees == null) return null;

            foreach (var tree in mgr.perkTrees)
            {
                if (tree != null && tree.ID == CUSTOM_TREE_ID) return tree;
            }
            return null;
        }

        private void Awake()
        {
            Debug.Log("[SuperStorageMod] Awake started.");
            try
            {
                foreach (var t in Tiers)
                {
                    LocalizationManager.SetOverrideText(t.nameKey, t.displayName);
                }

                LocalizationManager.SetOverrideText("PerkTree_" + CUSTOM_TREE_ID, "超库扩容");
                LocalizationManager.SetOverrideText(CUSTOM_INTERACT_KEY, "超库扩容");

                EnsureBackupDirExists();

                Debug.Log("[SuperStorageMod] Localization set.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] Error in Awake: {ex}");
            }
        }

        private void OnEnable()
        {
            SubscribeLevelInitialized(OnLevelInitialized);
            EnsureSafetyNetSubscribed();
            SavesSystem.OnSetFile += OnSetFileChanged;
            Debug.Log("[SuperStorageMod] OnEnable: Subscribed to OnLevelInitialized and OnRecalculateStorageCapacity.");
        }

        private void OnDisable()
        {
            UnsubscribeLevelInitialized(OnLevelInitialized);
            EnsureSafetyNetUnsubscribed();
            SavesSystem.OnSetFile -= OnSetFileChanged;

            var tree = MyTree;
            if (tree != null)
            {
                SaveUnlockedBackupToDisk(tree);
            }

            _myCachedTree = null;
            _cachedBackupCapacity = -1;
            _cachedSlot = null;
        }

        // 切换存档槽位时，槽位缓存与备份容量缓存必须失效，否则会读写旧槽位的备份文件。
        private static void OnSetFileChanged()
        {
            _cachedSlot = null;
            _cachedBackupCapacity = -1;
            _backupFileLastWrite = default;
        }

        private void OnDestroy()
        {
            foreach (var go in _createdObjects)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }
            _createdObjects.Clear();
        }

        private static void SubscribeLevelInitialized(Action callback)
        {
            try
            {
                var fi = GetCachedField(typeof(LevelManager), "OnLevelInitialized");
                if (fi == null)
                {
                    Debug.LogWarning("[SuperStorageMod] Could not find LevelManager.OnLevelInitialized field. Mod will not auto-inject on level load.");
                    return;
                }

                var del = (Action?)fi.GetValue(null);
                del = (Action?)Delegate.Combine(del, callback);
                fi.SetValue(null, del);
                _onLevelInitializedEvent = del;
                Debug.Log("[SuperStorageMod] Successfully subscribed to OnLevelInitialized via reflection.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] Failed to subscribe to OnLevelInitialized: {ex}");
            }
        }

        private static void UnsubscribeLevelInitialized(Action callback)
        {
            try
            {
                var fi = GetCachedField(typeof(LevelManager), "OnLevelInitialized");
                if (fi == null) return;

                var del = (Action?)fi.GetValue(null);
                del = (Action?)Delegate.Remove(del, callback);
                fi.SetValue(null, del);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to unsubscribe from OnLevelInitialized: {ex.Message}");
            }
        }

        private void OnLevelInitialized()
        {
            if (_injected)
            {
                if (MyTree != null) return;
                Debug.Log("[SuperStorageMod] Custom PerkTree was destroyed by scene change, re-injecting...");
                _injected = false;
                _myCachedTree = null;
                CleanupDestroyedObjects();
            }
            Debug.Log("[SuperStorageMod] OnLevelInitialized triggered.");
            Inject().Forget();
        }

        private void CleanupDestroyedObjects()
        {
            _createdObjects.RemoveAll(go => go == null);
        }

        private void OnRecalculateStorageCapacity(PlayerStorage.StorageCapacityCalculationHolder holder)
        {
            try
            {
                RegisterPerkTreeToLevelConfig();

                bool addActive = IsAddPlayerStorageActive();
                int backupCapacity = CalculateCapacityFromBackup();
                ApplyCapacitySafetyNet(holder, addActive, backupCapacity);
            }
            catch (Exception ex)
            {
                // 容量重算事件链中不能抛异常，否则会中断游戏后续订阅者的计算。
                Debug.LogError($"[SuperStorageMod] Error in OnRecalculateStorageCapacity: {ex}");

                // 兜底：无论发生什么，保证容量不低于 默认容量 + 备份容量。
                try
                {
                    ApplyCapacitySafetyNet(holder, false, CalculateCapacityFromBackup());
                }
                catch (Exception innerEx)
                {
                    Debug.LogError($"[SuperStorageMod] SafetyNet fallback failed: {innerEx.Message}");
                }
            }
        }

        private void ApplyCapacitySafetyNet(PlayerStorage.StorageCapacityCalculationHolder holder, bool addActive, int backupCapacity)
        {
            if (addActive)
            {
                Debug.Log($"[SuperStorageMod] AddPlayerStorage appears active (backup says {backupCapacity} capacity).");
            }

            if (!addActive && backupCapacity > 0)
            {
                holder.capacity += backupCapacity;
                Debug.Log($"[SuperStorageMod] SafetyNet: added {backupCapacity} capacity from backup (AddPlayerStorage inactive).");
            }

            if (PlayerStorage.Inventory != null)
            {
                int currentCap = holder.capacity;
                int lastItemPos = PlayerStorage.Inventory.GetLastItemPosition() + 1;
                int neededCap = Math.Max(currentCap, lastItemPos);

                if (backupCapacity > 0)
                {
                    neededCap = Math.Max(neededCap, PlayerStorage.Instance.DefaultCapacity + backupCapacity);
                }

                if (neededCap > currentCap)
                {
                    int added = neededCap - currentCap;
                    if (currentCap < lastItemPos)
                    {
                        Debug.LogWarning($"[SuperStorageMod] SafetyNet: capacity below item count! Adding {added} (from {currentCap} to {neededCap}).");
                    }
                    else
                    {
                        Debug.Log($"[SuperStorageMod] SafetyNet: ensuring backup minimum. Adding {added} (from {currentCap} to {neededCap}).");
                    }
                    holder.capacity = neededCap;
                }
            }
        }

        // 与游戏 AddPlayerStorage.OnRecalculatePlayerStorage 的判断完全一致：
        // 只检查 Perk.Unlocked，不检查 EnabledInCurrentLevel（官方不检查，检查会导致安全网与官方路径叠加、容量双倍）。
        private bool IsAddPlayerStorageActive()
        {
            var myTree = MyTree;
            if (myTree == null) return false;

            foreach (var perk in myTree.Perks)
            {
                if (perk == null || !perk.Unlocked) continue;
                var add = perk.GetComponent<AddPlayerStorage>();
                if (add != null) return true;
            }
            return false;
        }

        private static int CalculateCapacityFromBackup()
        {
            var path = GetBackupPath();
            if (!File.Exists(path)) return 0;

            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (_cachedBackupCapacity >= 0 && lastWrite == _backupFileLastWrite)
                {
                    return _cachedBackupCapacity;
                }

                var lines = File.ReadAllLines(path);

                // 版本不兼容的旧备份（未来升级导致格式变化时）直接忽略，避免误恢复。
                if (!IsBackupVersionCompatible(lines))
                {
                    Debug.LogWarning($"[SuperStorageMod] Backup file version is not compatible (require <= v{MOD_DATA_VERSION}), ignoring: {path}");
                    return 0;
                }

                var set = new HashSet<string>(lines.Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("#")));

                int total = 0;
                foreach (var tier in Tiers)
                {
                    if (set.Contains(tier.nameKey))
                    {
                        total += tier.addCap;
                    }
                }

                _backupFileLastWrite = lastWrite;
                _cachedBackupCapacity = total;
                return total;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to calculate capacity from backup: {ex.Message}");
                return 0;
            }
        }

        private async UniTaskVoid Inject()
        {
            if (_injected) return;

            _injected = true;

            // 记录本次注入开始前已创建的对象数量，失败时仅清理本次新建的对象，不影响历史对象。
            int injectStartIndex = _createdObjects.Count;

            try
            {
                Debug.Log("[SuperStorageMod] Injecting... Waiting for level load to settle.");
                await UniTask.Delay(TimeSpan.FromSeconds(INJECT_DELAY_SECONDS));

                if (LevelManager.Instance == null || LevelManager.Instance.MainCharacter == null)
                {
                    Debug.LogWarning("[SuperStorageMod] LevelManager or MainCharacter not ready yet, retrying...");
                    await UniTask.Delay(TimeSpan.FromSeconds(1f));
                    if (LevelManager.Instance == null || LevelManager.Instance.MainCharacter == null)
                    {
                        Debug.LogError("[SuperStorageMod] LevelManager still not ready. Aborting injection.");
                        _injected = false;
                        return;
                    }
                }

                var officialTree = PerkTreeManager.GetPerkTree(OFFICIAL_TREE_ID);
                if (officialTree == null)
                {
                    Debug.LogWarning("[SuperStorageMod] Official StorageExpand tree not found! Cannot copy base data.");
                    _injected = false;
                    return;
                }

                PerkTree? myTree = MyTree;
                if (myTree == null)
                {
                    myTree = CreateCustomPerkTree(officialTree);
                    if (myTree == null)
                    {
                        _injected = false;
                        return;
                    }
                    _myCachedTree = myTree;
                }

                RegisterPerkTreeToLevelConfig();

                EnsureSafetyNetSubscribed();

                RestoreUnlockedFromDisk(myTree);

                InjectInvokerIntoScene();

                Debug.Log("[SuperStorageMod] Injection complete.");
            }
            catch (Exception ex)
            {
                _injected = false;
                _myCachedTree = null;
                CleanupCreatedObjectsFrom(injectStartIndex);
                Debug.LogError($"[SuperStorageMod] Fatal error in Inject: {ex}");
            }
        }

        // 清理 _createdObjects 中从 startIndex 起新建的对象（用于失败回滚）。
        private void CleanupCreatedObjectsFrom(int startIndex)
        {
            for (int i = _createdObjects.Count - 1; i >= startIndex; i--)
            {
                var go = _createdObjects[i];
                if (go != null)
                {
                    Object.Destroy(go);
                }
                _createdObjects.RemoveAt(i);
            }
        }

        private PerkTree? CreateCustomPerkTree(PerkTree officialTree)
        {
            Debug.Log("[SuperStorageMod] Creating custom PerkTree...");

            int startIndex = _createdObjects.Count;

            var myTreeGo = new GameObject("PerkTree_" + CUSTOM_TREE_ID);
            myTreeGo.transform.SetParent(PerkTreeManager.Instance.transform);
            _createdObjects.Add(myTreeGo);

            var myTree = myTreeGo.AddComponent<PerkTree>();
            SetFieldValue(myTree, "perkTreeID", CUSTOM_TREE_ID);

            var ownerType = FindTypeCached("PerkTreeRelationGraphOwner");
            if (ownerType == null)
            {
                Debug.LogError("[SuperStorageMod] PerkTreeRelationGraphOwner type not found!");
                CleanupCreatedObjectsFrom(startIndex);
                return null;
            }

            var owner = myTreeGo.AddComponent(ownerType);

            var graphType = FindTypeCached("PerkRelationGraph");
            if (graphType == null)
            {
                Debug.LogError("[SuperStorageMod] PerkRelationGraph type not found!");
                CleanupCreatedObjectsFrom(startIndex);
                return null;
            }

            var graph = ScriptableObject.CreateInstance(graphType) as Graph;
            if (graph == null)
            {
                Debug.LogError("[SuperStorageMod] Failed to create PerkRelationGraph instance!");
                CleanupCreatedObjectsFrom(startIndex);
                return null;
            }

            var propGraph = ownerType.GetProperty("graph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? ownerType.BaseType?.GetProperty("graph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (propGraph != null)
            {
                propGraph.SetValue(owner, graph);
            }
            else
            {
                var graphField = ownerType.GetField("_relationGraph", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                              ?? ownerType.BaseType?.GetField("_relationGraph", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                graphField?.SetValue(owner, graph);
            }

            SetFieldValue(myTree, "relationGraphOwner", owner);

            var basePerk = officialTree.Perks.FirstOrDefault(p => p != null && p.GetComponent<AddPlayerStorage>() != null);
            var icon = basePerk?.Icon;
            var quality = basePerk != null ? basePerk.DisplayQuality : default;
            var requireTimeTicks = basePerk?.Requirement != null
                ? GetFieldValue<long>(basePerk.Requirement, "requireTime")
                : 0L;

            for (int i = 0; i < Tiers.Length; i++)
            {
                CreatePerkNode(myTree, graph, Tiers[i], i, icon, quality, requireTimeTicks);
            }

            RegisterPerkTree(myTree);
            myTree.Load();

            return myTree;
        }

        // 创建单个等级节点：Perk 组件 + PerkRequirement + AddPlayerStorage + 解锁事件订阅 + 图节点。
        private void CreatePerkNode(
            PerkTree tree,
            Graph graph,
            (string nameKey, string displayName, int addCap, int requireLevel, long money, (int id, int amount)[] items) tier,
            int index,
            Sprite? icon,
            ItemStatsSystem.DisplayQuality quality,
            long? requireTimeTicks)
        {
            var perkGO = new GameObject($"SuperStorageMod_{tier.nameKey}");
            perkGO.transform.SetParent(tree.transform);
            _createdObjects.Add(perkGO);

            var perk = perkGO.AddComponent<Perk>();

            SetFieldValue(perk, "master", tree);
            SetFieldValue(perk, "icon", icon);
            SetFieldValue(perk, "quality", quality);
            SetFieldValue(perk, "displayName", tier.nameKey);
            SetFieldValue(perk, "hasDescription", true);
            SetFieldValue(perk, "defaultUnlocked", false);

            var reqType = FindTypeCached("PerkRequirement");
            object? req = null;
            if (reqType != null)
            {
                req = Activator.CreateInstance(reqType);
                reqType.GetField("level")?.SetValue(req, tier.requireLevel);

                var costType = FindTypeCached("Cost");
                if (costType != null)
                {
                    try
                    {
                        var itemsArr = tier.items.Select(e => new ValueTuple<int, long>(e.id, e.amount)).ToArray();
                        object? cost = Activator.CreateInstance(costType, tier.money, itemsArr);
                        reqType.GetField("cost")?.SetValue(req, cost);
                    }
                    catch (Exception costEx)
                    {
                        Debug.LogWarning($"[SuperStorageMod] Failed to create Cost for {tier.nameKey}: {costEx.Message}. Creating money-only cost.");
                        try
                        {
                            object? cost = Activator.CreateInstance(costType, tier.money);
                            reqType.GetField("cost")?.SetValue(req, cost);
                        }
                        catch (Exception moneyCostEx)
                        {
                            Debug.LogWarning($"[SuperStorageMod] Failed to create money-only Cost for {tier.nameKey}: {moneyCostEx.Message}");
                        }
                    }
                }

                reqType.GetField("requireTime")?.SetValue(req, requireTimeTicks);
            }
            else
            {
                Debug.LogWarning("[SuperStorageMod] PerkRequirement type not found, perk will have no requirement.");
            }

            SetFieldValue(perk, "requirement", req);

            var add = perkGO.AddComponent<AddPlayerStorage>();
            SetFieldValue(add, "addCapacity", tier.addCap);

            SubscribePerkUnlockEvent(perk);

            AddPerkToTree(tree, perk);

            var node = AddGraphNode(graph, perk);
            if (node != null)
            {
                node.cachedPosition = new Vector2(0, index * 150f);
            }
        }

        private static void RegisterPerkTree(PerkTree tree)
        {
            try
            {
                if (_perkTreesField == null)
                {
                    var type = typeof(PerkTreeManager);
                    _perkTreesField = type.GetField("perkTrees", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                   ?? type.BaseType?.GetField("perkTrees", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    if (_perkTreesField == null)
                    {
                        foreach (var fi in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            if (fi.FieldType.IsGenericType && fi.FieldType.GetGenericTypeDefinition() == typeof(List<>) && fi.FieldType.GetGenericArguments()[0] == typeof(PerkTree))
                            {
                                _perkTreesField = fi;
                                break;
                            }
                        }
                    }
                }

                if (_perkTreesField != null && PerkTreeManager.Instance != null)
                {
                    var list = _perkTreesField.GetValue(PerkTreeManager.Instance) as IList<PerkTree>;
                    if (list != null && !list.Contains(tree))
                    {
                        list.Add(tree);
                        Debug.Log("[SuperStorageMod] Registered custom PerkTree to manager.");
                    }
                }
                else
                {
                    Debug.LogWarning("[SuperStorageMod] Could not find perkTrees field on PerkTreeManager. Tree may not appear in UI.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] Failed to register PerkTree: {ex}");
            }
        }

        private static FieldInfo? _enabledPerkTreesField;

        private static void RegisterPerkTreeToLevelConfig()
        {
            try
            {
                var levelConfig = LevelConfig.Instance;
                if (levelConfig == null)
                {
                    Debug.LogWarning("[SuperStorageMod] LevelConfig instance not found, cannot register perk tree ID.");
                    return;
                }

                if (_enabledPerkTreesField == null)
                {
                    _enabledPerkTreesField = typeof(LevelConfig).GetField("enabledPerkTrees", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (_enabledPerkTreesField == null)
                    {
                        Debug.LogWarning("[SuperStorageMod] Could not find enabledPerkTrees field on LevelConfig.");
                        return;
                    }
                }

                var idList = _enabledPerkTreesField.GetValue(levelConfig) as PerkTreeIDList;
                if (idList == null)
                {
                    idList = ScriptableObject.CreateInstance<PerkTreeIDList>();
                    _enabledPerkTreesField.SetValue(levelConfig, idList);

                    var defaultTrees = GetDefaultEnabledPerkTrees();
                    if (defaultTrees != null)
                    {
                        foreach (var id in defaultTrees.perkTrees)
                        {
                            if (!idList.perkTrees.Contains(id))
                                idList.perkTrees.Add(id);
                        }
                    }
                }

                if (!idList.perkTrees.Contains(CUSTOM_TREE_ID))
                {
                    idList.perkTrees.Add(CUSTOM_TREE_ID);
                    Debug.Log($"[SuperStorageMod] Registered '{CUSTOM_TREE_ID}' to LevelConfig.enabledPerkTrees.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] Failed to register perk tree ID to LevelConfig: {ex}");
            }
        }

        private static PerkTreeIDList? GetDefaultEnabledPerkTrees()
        {
            try
            {
                var gdsType = FindTypeCached("GameplayDataSettings");
                if (gdsType == null) return null;

                var prop = gdsType.GetProperty("DefaultEnabledPerkTrees", BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    return prop.GetValue(null) as PerkTreeIDList;
                }

                var defaultProp = gdsType.GetProperty("Default", BindingFlags.NonPublic | BindingFlags.Static);
                if (defaultProp != null)
                {
                    var defaultInstance = defaultProp.GetValue(null);
                    if (defaultInstance != null)
                    {
                        var field = defaultInstance.GetType().GetField("defaultEnabledPerkTrees", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (field != null)
                        {
                            return field.GetValue(defaultInstance) as PerkTreeIDList;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to read default enabled perk trees: {ex.Message}");
            }
            return null;
        }

        private void InjectInvokerIntoScene()
        {
            Debug.Log("[SuperStorageMod] Injecting Invoker into Scene...");

            var allBase = Resources.FindObjectsOfTypeAll<InteractableBase>();

            var officialInvokers = allBase
                .OfType<PerkTreeUIInvoker>()
                .Where(inv => inv.perkTreeID == OFFICIAL_TREE_ID && inv.gameObject.scene.IsValid())
                .ToList();

            if (officialInvokers.Count == 0)
            {
                Debug.LogWarning("[SuperStorageMod] Could not find official StorageExpand invoker in any scene.");
                return;
            }

            var alreadyInjectedScenes = allBase
                .OfType<PerkTreeUIInvoker>()
                .Where(inv => inv.perkTreeID == CUSTOM_TREE_ID && inv.gameObject.scene.IsValid())
                .Select(inv => inv.gameObject.scene)
                .ToHashSet();

            var targetInvokers = officialInvokers
                .Where(inv => !alreadyInjectedScenes.Contains(inv.gameObject.scene))
                .GroupBy(inv => inv.gameObject.scene)
                .Select(g => g.First())
                .ToList();

            if (targetInvokers.Count == 0)
            {
                Debug.Log("[SuperStorageMod] Custom invoker already exists in all relevant scenes, skipping.");
                return;
            }

            var allInteractables = allBase
                .Where(i => i.gameObject.scene.IsValid())
                .ToList();

            foreach (var invoker in targetInvokers)
            {
                InjectSingleInvoker(invoker, allInteractables);
            }

            Debug.Log($"[SuperStorageMod] Injected {targetInvokers.Count} custom invoker(s).");
        }

        private void InjectSingleInvoker(PerkTreeUIInvoker officialInvoker, List<InteractableBase> allInteractables)
        {
            bool wasActive = officialInvoker.gameObject.activeSelf;
            officialInvoker.gameObject.SetActive(false);

            var myInvokerGo = Object.Instantiate(officialInvoker.gameObject, officialInvoker.transform.parent);
            myInvokerGo.name = "SuperStorage_Invoker";
            _createdObjects.Add(myInvokerGo);

            var myInvoker = myInvokerGo.GetComponent<PerkTreeUIInvoker>();

            SetFieldValue(myInvoker, "otherInterablesInGroup", new List<InteractableBase>());
            myInvoker.interactableGroup = false;

            myInvoker.perkTreeID = CUSTOM_TREE_ID;
            myInvoker.overrideInteractName = true;
            myInvoker._overrideInteractNameKey = CUSTOM_INTERACT_KEY;
            myInvoker.InteractName = CUSTOM_INTERACT_KEY;

            myInvokerGo.SetActive(wasActive);
            officialInvoker.gameObject.SetActive(wasActive);

            myInvokerGo.layer = LayerMask.NameToLayer("Interactable");

            var coll = myInvoker.GetComponent<Collider>();
            if (coll != null)
            {
                coll.enabled = true;
                var offColl = officialInvoker.GetComponent<Collider>();
                if (coll is BoxCollider myBox && offColl is BoxCollider offBox)
                {
                    myBox.center = offBox.center;
                    myBox.size = offBox.size;
                }
            }

            var master = FindMasterInteractable(officialInvoker, allInteractables);
            if (master != null)
            {
                AddInvokerToGroup(master, myInvoker);
            }
            else
            {
                officialInvoker.interactableGroup = true;
                var list = GetFieldObject<List<InteractableBase>>(officialInvoker, "otherInterablesInGroup");
                if (list == null)
                {
                    list = new List<InteractableBase>();
                    SetFieldValue(officialInvoker, "otherInterablesInGroup", list);
                }
                AddInvokerToGroup(officialInvoker, myInvoker);
            }
        }

        private static void AddInvokerToGroup(InteractableBase groupMaster, PerkTreeUIInvoker myInvoker)
        {
            var list = GetFieldObject<List<InteractableBase>>(groupMaster, "otherInterablesInGroup");
            if (list != null && !list.Contains(myInvoker))
            {
                list.Add(myInvoker);
                myInvoker.transform.position = groupMaster.transform.position;
                myInvoker.transform.rotation = groupMaster.transform.rotation;
                myInvoker.interactMarkerOffset = groupMaster.interactMarkerOffset;
                myInvoker.MarkerActive = false;

                Debug.Log($"[SuperStorageMod] Injected custom invoker into group of {groupMaster.gameObject.name}.");
            }
        }

        private static InteractableBase? FindMasterInteractable(InteractableBase target, List<InteractableBase> allInteractables)
        {
            foreach (var i in allInteractables)
            {
                if (!i.interactableGroup) continue;

                var list = GetFieldObject<List<InteractableBase>>(i, "otherInterablesInGroup");
                if (list != null && list.Contains(target))
                {
                    return i;
                }
            }
            return null;
        }

        private static void AddPerkToTree(PerkTree tree, Perk perk)
        {
            var fi = GetCachedField(typeof(PerkTree), "perks");
            if (fi == null) return;

            var list = fi.GetValue(tree) as System.Collections.IList;
            if (list == null)
            {
                var listType = typeof(List<>).MakeGenericType(typeof(Perk));
                list = Activator.CreateInstance(listType) as System.Collections.IList;
                fi.SetValue(tree, list);
            }

            if (list != null && !list.Contains(perk))
            {
                list.Add(perk);
            }
        }

        private static PerkRelationNode? AddGraphNode(Graph graph, Perk perk)
        {
            if (graph == null) return null;
            try
            {
                var nodeType = typeof(PerkRelationNode);

                var addNodeMethod = typeof(Graph).GetMethod("AddNode", new[] { typeof(Type), typeof(Vector2) });
                if (addNodeMethod != null)
                {
                    var node = addNodeMethod.Invoke(graph, new object[] { nodeType, Vector2.zero }) as PerkRelationNode;
                    if (node != null)
                    {
                        node.relatedNode = perk;
                        return node;
                    }
                }

                var genericMethod = typeof(Graph).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "AddNode" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                if (genericMethod != null)
                {
                    var specialized = genericMethod.MakeGenericMethod(nodeType);
                    var node = specialized.Invoke(graph, new object[] { Vector2.zero }) as PerkRelationNode;
                    if (node != null)
                    {
                        node.relatedNode = perk;
                        return node;
                    }
                }

                Debug.LogWarning("[SuperStorageMod] Could not find any AddNode method on Graph.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] AddGraphNode failed: {ex}");
            }
            return null;
        }

        #region Perk 解锁事件订阅（事件驱动，替代轮询）

        private static void SubscribePerkUnlockEvent(Perk perk)
        {
            try
            {
                if (_onUnlockStateChangedField == null)
                {
                    _onUnlockStateChangedField = typeof(Perk).GetField("onUnlockStateChanged",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }

                if (_onUnlockStateChangedField == null)
                {
                    Debug.LogWarning("[SuperStorageMod] Could not find Perk.onUnlockStateChanged field.");
                    return;
                }

                var currentDel = _onUnlockStateChangedField.GetValue(perk) as Delegate;
                var handler = new Action<Perk, bool>((p, unlocked) =>
                {
                    if (unlocked)
                    {
                        var tree = GetFieldObject<PerkTree>(p, "master");
                        if (tree != null)
                        {
                            SaveUnlockedBackupToDisk(tree);
                            _cachedBackupCapacity = -1;
                        }
                    }
                });

                _onUnlockStateChangedField.SetValue(perk, Delegate.Combine(currentDel, handler));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to subscribe to Perk unlock event: {ex.Message}");
            }
        }

        #endregion

        #region 反射工具方法

        private static Type? FindTypeCached(string name)
        {
            if (TypeCache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            Type? result = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    result = asm.GetTypes().FirstOrDefault(x => x.Name == name);
                    if (result != null) break;
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    result = rtle.Types.FirstOrDefault(t => t?.Name == name);
                    if (result != null) break;
                }
                catch { }
            }

            TypeCache[name] = result;
            return result;
        }

        private static FieldInfo? GetCachedField(Type type, string fieldName)
        {
            var key = (type, fieldName);
            if (FieldCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            FieldInfo? fi = null;
            var current = type;
            while (current != null && fi == null)
            {
                fi = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                current = current.BaseType;
            }

            FieldCache[key] = fi;
            return fi;
        }

        private static void SetFieldValue(object target, string fieldName, object? value)
        {
            var fi = GetCachedField(target.GetType(), fieldName);
            fi?.SetValue(target, value);
        }

        private static T? GetFieldValue<T>(object target, string fieldName) where T : struct
        {
            var fi = GetCachedField(target.GetType(), fieldName);
            if (fi == null) return default;
            var v = fi.GetValue(target);
            return v is T t ? t : default;
        }

        private static T? GetFieldObject<T>(object target, string fieldName) where T : class
        {
            var fi = GetCachedField(target.GetType(), fieldName);
            if (fi == null) return null;
            return fi.GetValue(target) as T;
        }

        #endregion

        #region 安全网订阅管理（确保事件只订阅一次）

        private void EnsureSafetyNetSubscribed()
        {
            PlayerStorage.OnRecalculateStorageCapacity -= OnRecalculateStorageCapacity;
            PlayerStorage.OnRecalculateStorageCapacity += OnRecalculateStorageCapacity;
        }

        private void EnsureSafetyNetUnsubscribed()
        {
            PlayerStorage.OnRecalculateStorageCapacity -= OnRecalculateStorageCapacity;
        }

        #endregion

        #region 存档备份与恢复

        // 校验备份文件版本兼容性：无版本头视为 v0（兼容）；有 #version:N 头要求 N <= MOD_DATA_VERSION。
        private static bool IsBackupVersionCompatible(string[] lines)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("#version:", StringComparison.Ordinal)) continue;

                return int.TryParse(line.Substring("#version:".Length).Trim(), out var version) && version <= MOD_DATA_VERSION;
            }
            return true;
        }

        private static void EnsureBackupDirExists()
        {
            if (_backupDirCreated) return;
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, BACKUP_DIR_NAME);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _backupDirCreated = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to create backup directory: {ex.Message}");
            }
        }

        private static string GetBackupDir()
        {
            var dir = Path.Combine(Application.persistentDataPath, BACKUP_DIR_NAME);
            if (!_backupDirCreated)
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    _backupDirCreated = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SuperStorageMod] Failed to ensure backup directory: {ex.Message}");
                }
            }
            return dir;
        }

        private static int GetCurrentSlot()
        {
            if (_cachedSlot.HasValue) return _cachedSlot.Value;

            try
            {
                int slot = SavesSystem.CurrentSlot;
                if (slot > 0)
                {
                    _cachedSlot = slot;
                    return slot;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to get current slot: {ex.Message}");
            }

            _cachedSlot = 1;
            return 1;
        }

        private static string GetBackupPath()
        {
            int slot = GetCurrentSlot();
            return Path.Combine(GetBackupDir(), $"{BACKUP_FILE_PREFIX}{slot}{BACKUP_FILE_EXT}");
        }

        private static void SaveUnlockedBackupToDisk(PerkTree tree)
        {
            if (tree == null) return;
            try
            {
                var ids = tree.Perks
                    .Where(p => p != null && (p.DisplayNameRaw ?? string.Empty).StartsWith(PERK_NAME_PREFIX) && p.Unlocked)
                    .Select(p => p.DisplayNameRaw ?? p.gameObject.name)
                    .ToArray();

                if (ids.Length > 0)
                {
                    var lines = new List<string>(ids.Length + 1);
                    lines.Add($"#version:{MOD_DATA_VERSION}");
                    lines.AddRange(ids);
                    File.WriteAllLines(GetBackupPath(), lines);
                    _cachedBackupCapacity = -1;
                    Debug.Log($"[SuperStorageMod] Saved {ids.Length} unlocked perks to backup.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to save backup: {ex.Message}");
            }
        }

        private static void RestoreUnlockedFromDisk(PerkTree tree)
        {
            if (tree == null) return;
            var path = GetBackupPath();
            if (!File.Exists(path)) return;

            string[]? lines = null;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to read backup file: {ex.Message}");
                return;
            }

            if (lines == null || lines.Length == 0) return;

            // 版本不兼容的旧备份直接忽略，避免误恢复。
            if (!IsBackupVersionCompatible(lines))
            {
                Debug.LogWarning($"[SuperStorageMod] Backup file version is not compatible (require <= v{MOD_DATA_VERSION}), ignoring: {path}");
                return;
            }

            var set = new HashSet<string>(lines.Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("#")));

            if (set.Count == 0) return;

            foreach (var p in tree.Perks)
            {
                if (p == null) continue;

                var identifier = p.DisplayNameRaw ?? p.gameObject?.name;
                if (identifier == null || !set.Contains(identifier)) continue;

                try
                {
                    if (!p.Unlocked)
                    {
                        p.ForceUnlock();
                        Debug.Log($"[SuperStorageMod] Restored perk: {identifier}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SuperStorageMod] Failed to restore perk {identifier}: {ex}");
                }
            }

            PlayerStorage.NotifyCapacityDirty();
        }

        #endregion
    }
}
