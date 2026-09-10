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
using ItemStatsSystem;
using NodeCanvas.Framework;
using Saves;
using SodaCraft.Localizations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SuperStorageMod
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // 扩容箱的“内部名 → 兜底 ID”。运行期优先用 ItemAssetsCollection.TryGetIDByName 反查，
        // 只有反查失败时才退回硬编码 ID（原实现完全依赖硬编码，游戏更新导致 ID 漂移即失效）。
        //
        // ⚠️ 内部名与本地化键不同：entries 里 metaData.Name = "ContinerS/M/L"，
        //    而 metaData.DisplayNameKey = "Item_ContinerS/M/L"（本地化 CSV 的键）。
        //    TryGetIDByName 比对的是 Name，所以必须用 "ContinerS" 这类短名。
        //    实机日志证据（Player.log）：typeID=50 name='ContinerS' displayKey='Item_ContinerS'。
        private static readonly (string internalName, int fallbackId)[] BoxDefinitions = new[]
        {
            ("ContinerS", 50),
            ("ContinerM", 49),
            ("ContinerL", 48)
        };

        // 每个等级需求的箱子数量，顺序与 BoxDefinitions 一致（小 / 中 / 大）。
        private static readonly int[][] TierBoxAmounts = new[]
        {
            new[] {  9,  6,  3 },
            new[] { 12,  8,  4 },
            new[] { 15, 10,  5 },
            new[] { 18, 12,  6 },
            new[] { 21, 14,  7 },
            new[] { 24, 16,  8 },
            new[] { 27, 18,  9 },
            new[] { 30, 20, 10 },
            new[] { 33, 22, 11 }
        };

        private static readonly (string nameKey, string displayName, int addCap, int requireLevel, long money)[] Tiers = new[]
        {
            ("SuperStorage_Lv2",  "超级仓库Lv.2",  150, 30, 600_000L),
            ("SuperStorage_Lv3",  "超级仓库Lv.3",  200, 30, 700_000L),
            ("SuperStorage_Lv4",  "超级仓库Lv.4",  250, 30, 800_000L),
            ("SuperStorage_Lv5",  "超级仓库Lv.5",  300, 30, 900_000L),
            ("SuperStorage_Lv6",  "超级仓库Lv.6",  350, 30, 1_000_000L),
            ("SuperStorage_Lv7",  "超级仓库Lv.7",  450, 30, 1_200_000L),
            ("SuperStorage_Lv8",  "超级仓库Lv.8",  500, 30, 1_400_000L),
            ("SuperStorage_Lv9",  "超级仓库Lv.9",  550, 35, 1_600_000L),
            ("SuperStorage_Lv10", "超级仓库Lv.10", 600, 40, 2_000_000L)
        };

        // 运行期解析出的箱子 ID（未解析前为 null）。
        private static int[]? _boxIds;
        private static bool _boxIdsResolved;
        private static bool _snapshotLogged;

        private const string PERK_NAME_PREFIX = "SuperStorage_";
        private const float INJECT_DELAY_SECONDS = 1.5f;
        private const string BACKUP_DIR_NAME = "SuperStorageMod";
        private const string BACKUP_FILE_PREFIX = "backup_slot_";
        private const string BACKUP_FILE_EXT = ".json";
        private const string CUSTOM_TREE_ID = "SuperStorageExpand";
        private const string OFFICIAL_TREE_ID = "StorageExpand";
        private const string CUSTOM_INTERACT_KEY = "SuperStorage_InteractName";
        private const int MOD_DATA_VERSION = 1;

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

                // 解析扩容箱 ID（替代硬编码），必须在创建节点前完成。
                ResolveBoxIds();

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
                else
                {
                    // 已存在的树（场景切换后复用）：检查图节点是否丢失并修复。
                    EnsureGraphNodes(myTree);
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

            SetGraphOnOwner(owner, ownerType, graph);

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

        // 把图实例挂到 PerkTreeRelationGraphOwner 上。
        // 主路径：GraphOwner<T>.graph（public sealed override 属性，GraphOwner.cs:559-569）。
        // 兜底路径：GraphOwner<T> 的私有字段 _graph（注意不是 PerkTreeRelationGraphOwner._relationGraph，
        // 后者只是 RelationGraph 属性的缓存，写它不会让 GraphOwner 真正持有图）。
        private static bool SetGraphOnOwner(Component owner, Type ownerType, Graph graph)
        {
            var propGraph = ownerType.GetProperty("graph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? ownerType.BaseType?.GetProperty("graph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (propGraph != null && propGraph.CanWrite)
            {
                propGraph.SetValue(owner, graph);
                return true;
            }

            var graphField = ownerType.GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                          ?? ownerType.BaseType?.GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (graphField != null)
            {
                graphField.SetValue(owner, graph);
                return true;
            }

            Debug.LogError("[SuperStorageMod] 无法把 PerkRelationGraph 挂到 owner 上：既找不到 graph 属性也找不到 _graph 字段。");
            return false;
        }

        // 修复场景切换 / GameObject 重新激活后可能出现的“图节点丢失”：
        // NodeCanvas 的 GraphOwner.OnEnable → StartBehaviour → Graph.Clone 用 Object.Instantiate 克隆图，
        // 而 GraphSource._nodes 只有 [fsSerializeAs] 没有 [SerializeField]，
        // 因此克隆出来的图是空的（PerkTreeView 会过滤掉所有节点，Perk.GetLayoutPosition 还会 NRE）。
        // 这里检测每个 Perk 是否还有对应图节点，缺失就补建。
        private void EnsureGraphNodes(PerkTree tree)
        {
            if (tree == null) return;

            var owner = tree.RelationGraphOwner;
            var graph = owner?.RelationGraph;
            if (owner == null || graph == null)
            {
                Debug.LogWarning("[SuperStorageMod] EnsureGraphNodes: RelationGraphOwner 或 graph 为空，跳过。");
                return;
            }

            int missing = 0;
            for (int i = 0; i < Tiers.Length; i++)
            {
                Perk? perk = i < tree.Perks.Count ? tree.Perks[i] : null;
                if (perk == null) continue;
                if (graph.GetRelatedNode(perk) != null) continue;

                var node = AddGraphNode(graph, perk);
                if (node != null)
                {
                    node.cachedPosition = new Vector2(0, i * 150f);
                    missing++;
                }
            }

            if (missing > 0)
            {
                Debug.LogWarning($"[SuperStorageMod] 检测到 {missing} 个 Perk 缺少图节点（可能是图被克隆），已重建。");
            }
        }

        // 创建单个等级节点：Perk 组件 + PerkRequirement + AddPlayerStorage + 解锁事件订阅 + 图节点。
        private void CreatePerkNode(
            PerkTree tree,
            Graph graph,
            (string nameKey, string displayName, int addCap, int requireLevel, long money) tier,
            int index,
            Sprite? icon,
            ItemStatsSystem.DisplayQuality quality,
            long? requireTimeTicks)
        {
            // GameObject 名与 displayName 保持一致：
            // PerkTree 的存档以 perk.name 为键（PerkTree.cs:28），备份文件以 DisplayNameRaw 为键，
            // 两者同名后原生存档与模组备份就指向同一标识，避免双轨失配导致解锁记录清零。
            var perkGO = new GameObject(tier.nameKey);
            perkGO.transform.SetParent(tree.transform);
            _createdObjects.Add(perkGO);

            var perk = perkGO.AddComponent<Perk>();

            SetFieldValue(perk, "master", tree);
            SetFieldValue(perk, "icon", icon);
            SetFieldValue(perk, "quality", quality);
            SetFieldValue(perk, "displayName", tier.nameKey);
            // 不设描述：模组没有注册 "<key>_Desc" 本地化键，开启后会显示 "*SuperStorage_LvN_Desc*"。
            // 容量说明由 AddPlayerStorage.Description 自动提供（PerkBehaviour_AddPlayerStorage）。
            SetFieldValue(perk, "hasDescription", false);
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
                    var tierItems = GetTierItems(index);
                    bool costCreated = false;

                    if (tierItems.Length > 0)
                    {
                        try
                        {
                            var itemsArr = tierItems.Select(e => new ValueTuple<int, long>(e.id, e.amount)).ToArray();
                            object? cost = Activator.CreateInstance(costType, tier.money, itemsArr);
                            reqType.GetField("cost")?.SetValue(req, cost);
                            costCreated = true;
                        }
                        catch (Exception costEx)
                        {
                            Debug.LogWarning($"[SuperStorageMod] Failed to create Cost for {tier.nameKey}: {costEx.Message}. Creating money-only cost.");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[SuperStorageMod] {tier.nameKey}: 没有可用的扩容箱 ID，退化为纯金钱消耗。");
                    }

                    if (!costCreated)
                    {
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
                    // 关卡的 enabledPerkTrees 为空时，游戏会回退到 GameplayDataSettings.DefaultEnabledPerkTrees
                    // （LevelConfig.cs:197-208）。这里必须拿到官方默认列表再复制，
                    // 否则新建的列表里只有自定义树 ID，会让本关卡所有官方技能树都判定为“未启用”。
                    var defaultTrees = GetDefaultEnabledPerkTrees();
                    if (defaultTrees == null || defaultTrees.perkTrees == null || defaultTrees.perkTrees.Count == 0)
                    {
                        Debug.LogError("[SuperStorageMod] 无法读取官方默认技能树列表，跳过 enabledPerkTrees 注册（避免误关掉官方技能树）。将依赖 AddPlayerStorage 自身的 Unlocked 判定。");
                        return;
                    }

                    idList = ScriptableObject.CreateInstance<PerkTreeIDList>();
                    foreach (var id in defaultTrees.perkTrees)
                    {
                        if (!idList.perkTrees.Contains(id))
                            idList.perkTrees.Add(id);
                    }
                    _enabledPerkTreesField.SetValue(levelConfig, idList);
                    Debug.Log($"[SuperStorageMod] 关卡 enabledPerkTrees 为空，已基于官方默认列表（{idList.perkTrees.Count} 项）创建。");
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

            // 克隆时官方 invoker 处于 inactive，所以克隆体也是 inactive，其 Awake 还没跑。
            // 这里强制激活：否则 Awake 永不执行（交互组同步、collider 补全都会缺失），
            // 玩家在游戏里永远看不到自定义交互入口。
            myInvokerGo.SetActive(true);
            if (!wasActive)
            {
                Debug.LogWarning("[SuperStorageMod] 官方 invoker 在场景中处于 inactive，已强制激活自定义 invoker。");
            }
            officialInvoker.gameObject.SetActive(wasActive);

            // 位置/旋转跟随官方 invoker，保证交互射线能命中。
            myInvokerGo.transform.position = officialInvoker.transform.position;
            myInvokerGo.transform.rotation = officialInvoker.transform.rotation;

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
                // 幂等：若该 Perk 已有图节点就直接复用，避免重复调用产生重复节点
                // （PerkRelationGraph.GetRelatedNode 只返回第一个匹配项，重复节点会让 UI 错乱）。
                var existing = (graph as PerkRelationGraph)?.GetRelatedNode(perk);
                if (existing != null) return existing;

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
            if (perk == null) return;
            try
            {
                // onUnlockStateChanged 是 public 事件（Perk.cs:146），直接 += 即可，
                // 无需反射。反射路径仅作为兼容兜底保留。
                perk.onUnlockStateChanged += OnPerkUnlockStateChanged;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] 直接订阅 Perk 解锁事件失败，尝试反射兜底: {ex.Message}");
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
                    _onUnlockStateChangedField.SetValue(perk, Delegate.Combine(currentDel, (Action<Perk, bool>)OnPerkUnlockStateChanged));
                }
                catch (Exception innerEx)
                {
                    Debug.LogWarning($"[SuperStorageMod] 反射订阅 Perk 解锁事件也失败: {innerEx.Message}");
                }
            }
        }

        // 解锁瞬间立即落盘备份（事件驱动，替代旧的轮询）。
        private static void OnPerkUnlockStateChanged(Perk perk, bool unlocked)
        {
            if (!unlocked) return;
            try
            {
                var tree = perk.Master ?? GetFieldObject<PerkTree>(perk, "master");
                if (tree != null)
                {
                    SaveUnlockedBackupToDisk(tree);
                    _cachedBackupCapacity = -1;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] 处理解锁事件时异常: {ex.Message}");
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

        #region 扩容箱 ID 解析（替代硬编码）

        // 运行期把扩容箱的内部名解析成 TypeID；解析失败退回硬编码兜底值。
        // 只在第一次调用时真正解析，之后走缓存。
        private static void ResolveBoxIds()
        {
            if (_boxIdsResolved) return;
            _boxIdsResolved = true;

            var resolved = new int[BoxDefinitions.Length];
            for (int i = 0; i < BoxDefinitions.Length; i++)
            {
                var (internalName, fallbackId) = BoxDefinitions[i];
                resolved[i] = ResolveBoxId(internalName, fallbackId);
            }
            _boxIds = resolved;

            Debug.Log($"[SuperStorageMod] 扩容箱 ID 解析结果: S={resolved[0]}, M={resolved[1]}, L={resolved[2]}");
        }

        private static int ResolveBoxId(string internalName, int fallbackId)
        {
            try
            {
                if (ItemAssetsCollection.Instance == null)
                {
                    Debug.LogWarning($"[SuperStorageMod] ItemAssetsCollection 尚未就绪，'{internalName}' 退回硬编码 ID {fallbackId}。");
                    return fallbackId;
                }

                // ① 官方按名查表
                int id = ItemAssetsCollection.TryGetIDByName(internalName);
                if (id > 0)
                {
                    if (id != fallbackId)
                    {
                        Debug.Log($"[SuperStorageMod] '{internalName}' 运行期 ID={id}（硬编码为 {fallbackId}，已采用运行期值）。");
                    }
                    return id;
                }

                // ② 忽略大小写再查一次
                int byDisplay = ItemAssetsCollection.TryGetIDByName(internalName, true);
                if (byDisplay > 0)
                {
                    Debug.Log($"[SuperStorageMod] '{internalName}' 按忽略大小写查到 ID={byDisplay}。");
                    return byDisplay;
                }

                // ③ 直接扫 entries：用 ItemMetaData.Name 做包含匹配，兼容前缀/大小写差异
                var scanned = ScanEntriesForName(internalName);
                if (scanned > 0)
                {
                    Debug.Log($"[SuperStorageMod] '{internalName}' 扫描 entries 命中 ID={scanned}。");
                    return scanned;
                }

                // ④ 仍失败：把疑似相关的条目打出来，便于实机定位真实内部名
                DumpSimilarEntries(internalName);

                Debug.LogWarning($"[SuperStorageMod] 找不到物品 '{internalName}'，退回硬编码 ID {fallbackId}。若该 ID 无效，升级所需物品会被跳过。");
                return fallbackId;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] 解析物品 '{internalName}' 时异常: {ex.Message}，退回硬编码 ID {fallbackId}。");
                return fallbackId;
            }
        }

        // 扫描 ItemAssetsCollection.entries，按 metaData.Name 做“包含”匹配。
        // 取最长的匹配名，避免 "Item_ContinerS" 被 "Item_Continer" 之类更短的名字抢先命中。
        private static int ScanEntriesForName(string name)
        {
            var entries = ItemAssetsCollection.Instance?.entries;
            if (entries == null) return -1;

            int bestId = -1;
            int bestLen = -1;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                var metaName = entry.metaData.Name;
                if (string.IsNullOrEmpty(metaName)) continue;
                if (metaName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (metaName.Length > bestLen)
                {
                    bestLen = metaName.Length;
                    bestId = entry.typeID;
                }
            }
            return bestId;
        }

        // 诊断用：打印名字里含 "Continer" 的条目（最多 12 条），一次性输出。
        private static bool _entriesDumped;

        private static void DumpSimilarEntries(string name)
        {
            if (_entriesDumped) return;
            _entriesDumped = true;

            try
            {
                var entries = ItemAssetsCollection.Instance?.entries;
                if (entries == null)
                {
                    Debug.LogWarning("[SuperStorageMod][诊断] entries 为 null。");
                    return;
                }

                // 用 internalName 的主体部分做模糊关键词，例如 Item_ContinerS -> Continer
                string keyword = "Continer";
                var sb = new System.Text.StringBuilder();
                sb.Append($"[SuperStorageMod][诊断] entries 总数={entries.Count}；名字含 '{keyword}' 的条目：");

                int shown = 0;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    var metaName = entry.metaData.Name;
                    if (string.IsNullOrEmpty(metaName)) continue;
                    if (metaName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    sb.Append($"\n    typeID={entry.typeID} name='{metaName}' displayKey='{entry.metaData.DisplayNameKey}'");
                    if (++shown >= 12) break;
                }

                if (shown == 0)
                {
                    sb.Append("（无）");
                }

                Debug.LogWarning(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod][诊断] dump entries 失败: {ex.Message}");
            }
        }

        // 根据已解析的箱子 ID + 等级索引，构造 Cost 需要的 (id, amount) 数组。
        // 返回空数组表示“这些箱子都不可用”，调用方会退化为纯金钱消耗。
        private static (int id, int amount)[] GetTierItems(int tierIndex)
        {
            if (_boxIds == null || tierIndex < 0 || tierIndex >= TierBoxAmounts.Length)
            {
                return Array.Empty<(int, int)>();
            }

            var amounts = TierBoxAmounts[tierIndex];
            var list = new List<(int id, int amount)>(amounts.Length);
            for (int i = 0; i < amounts.Length && i < _boxIds.Length; i++)
            {
                int id = _boxIds[i];
                if (id > 0 && amounts[i] > 0)
                {
                    list.Add((id, amounts[i]));
                }
            }
            return list.ToArray();
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
                int totalPerks = 0;
                int unlockedPerks = 0;
                var ids = new List<string>(Tiers.Length);

                foreach (var p in tree.Perks)
                {
                    if (p == null) continue;
                    totalPerks++;
                    if (p.Unlocked) unlockedPerks++;

                    var raw = p.DisplayNameRaw ?? string.Empty;
                    if (raw.StartsWith(PERK_NAME_PREFIX) && p.Unlocked)
                    {
                        ids.Add(raw);
                    }
                }

                // 诊断：暴露 Perks 集合规模与解锁计数（每个会话只打一次，避免刷屏）
                if (!_snapshotLogged)
                {
                    _snapshotLogged = true;
                    Debug.Log($"[SuperStorageMod] 备份快照: Perks={totalPerks}, Unlocked={unlockedPerks}, 匹配前缀={ids.Count}");
                }

                var path = GetBackupPath();

                // 空集合也要写盘：否则旧备份会一直残留，CalculateCapacityFromBackup 会继续为
                // 已经不再解锁的等级发放容量。但为避免“树暂时异常导致全锁”把好备份抹掉，
                // 只在文件本来就不存在、或本来就已为空时，才用空集合覆盖。
                if (ids.Count == 0)
                {
                    if (File.Exists(path) && HasAnyUnlockedEntry(path))
                    {
                        Debug.LogWarning("[SuperStorageMod] 当前没有任何已解锁等级，但备份文件非空，保留原备份（避免误删）。");
                        return;
                    }
                }

                var lines = new List<string>(ids.Count + 1) { $"#version:{MOD_DATA_VERSION}" };
                lines.AddRange(ids);

                // 原子写：先写临时文件再替换，避免崩溃时留下半截文件。
                var tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tmp, path);

                _cachedBackupCapacity = -1;
                Debug.Log($"[SuperStorageMod] Saved {ids.Count} unlocked perks to backup.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to save backup: {ex.Message}");
            }
        }

        // 备份文件里是否存在至少一个非注释、非空行（即至少有一个已解锁条目）。
        private static bool HasAnyUnlockedEntry(string path)
        {
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 读不出来时保守处理：当作有内容，避免误删。
                return true;
            }
            return false;
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

            // 诊断：进入恢复前的状态
            int perksBefore = tree.Perks.Count(p => p != null);
            int unlockedBefore = tree.Perks.Count(p => p != null && p.Unlocked);
            Debug.Log($"[SuperStorageMod] 恢复前: 备份条目={set.Count}, Perks={perksBefore}, 已解锁={unlockedBefore}");

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

            int unlockedAfter = tree.Perks.Count(p => p != null && p.Unlocked);
            Debug.Log($"[SuperStorageMod] 恢复后: 已解锁={unlockedAfter}");

            PlayerStorage.NotifyCapacityDirty();
        }

        #endregion
    }
}
