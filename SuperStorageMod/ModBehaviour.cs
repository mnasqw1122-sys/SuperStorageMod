using System;
using System.Linq;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Cysharp.Threading.Tasks;
using Duckov.Modding;
using Duckov.PerkTrees;
using Duckov.Economy;
using SodaCraft.Localizations;
using UnityEngine;

namespace SuperStorageMod
{
    /// <summary>
    /// 超级仓库模组主行为类，用于在技能树中添加仓库扩容等级
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const int SMALL_BOX_ID = 50; 
        private const int MED_BOX_ID = 49;   
        private const int BIG_BOX_ID = 48;   
        private const int INJECT_DELAY_SECONDS = 1; 
        private const int BUFFER_GUARD_COUNT = 128; 
        private const float DEFAULT_NODE_SPACING = 6f; 
        private const float MIN_NODE_SPACING = 5f; 
        private const int BASE_STORAGE_MIN_CAP = 90; 
        private const int BASE_STORAGE_MAX_CAP = 110; 
        private const float TOLERANCE_X = 1f; 
        private const float MIN_DELTA_THRESHOLD = 0.1f; 
        private const string PERK_NAME_PREFIX = "SuperStorage_"; 
        private const string BACKUP_DIR_NAME = "SuperStorageMod"; 
        private const string BACKUP_FILE_PREFIX = "backup_slot_"; 
        private const string UNKNOWN_TREE_ID = "UnknownTree";

        /// <summary>
        /// 仓库扩容等级配置数组
        /// </summary>
        private readonly (string nameKey, string displayName, int addCap, int requireLevel, long money, (int id, int amount)[] items)[] tiers = new[]
        {
            ("SuperStorage_Lv2",  "超级仓库Lv.2", 150, 30, 600_000L, new[]{ (SMALL_BOX_ID,9),  (MED_BOX_ID,6),  (BIG_BOX_ID,3) }),
            ("SuperStorage_Lv3",  "超级仓库Lv.3", 200, 30, 700_000L, new[]{ (SMALL_BOX_ID,12), (MED_BOX_ID,8),  (BIG_BOX_ID,4) }),
            ("SuperStorage_Lv4",  "超级仓库Lv.4", 250, 30, 800_000L, new[]{ (SMALL_BOX_ID,15), (MED_BOX_ID,10), (BIG_BOX_ID,5) }),
            ("SuperStorage_Lv5",  "超级仓库Lv.5", 300, 30, 900_000L, new[]{ (SMALL_BOX_ID,18), (MED_BOX_ID,12), (BIG_BOX_ID,6) }),
            ("SuperStorage_Lv6",  "超级仓库Lv.6", 350, 30, 1_000_000L, new[]{ (SMALL_BOX_ID,21), (MED_BOX_ID,14), (BIG_BOX_ID,7) }),
            ("SuperStorage_Lv7",  "超级仓库Lv.7", 450, 30, 1_200_000L, new[]{ (SMALL_BOX_ID,24), (MED_BOX_ID,16), (BIG_BOX_ID,8) }),
            ("SuperStorage_Lv8",  "超级仓库Lv.8", 500, 30, 1_400_000L, new[]{ (SMALL_BOX_ID,27), (MED_BOX_ID,18), (BIG_BOX_ID,9) }),
            ("SuperStorage_Lv9",  "超级仓库Lv.9", 550, 35, 1_600_000L, new[]{ (SMALL_BOX_ID,30), (MED_BOX_ID,20), (BIG_BOX_ID,10) }),
            ("SuperStorage_Lv10", "超级仓库Lv.10",600, 40, 2_000_000L, new[]{ (SMALL_BOX_ID,33), (MED_BOX_ID,22), (BIG_BOX_ID,11) })
        };

        /// <summary>
        /// 模组唤醒时调用，设置本地化文本
        /// </summary>
        private void Awake()
        {
            Debug.Log("[SuperStorageMod] Awake started.");
            try
            {
                foreach (var t in tiers)
                {
                    LocalizationManager.SetOverrideText(t.nameKey, t.displayName);
                }
                Debug.Log("[SuperStorageMod] Localization set.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] Error in Awake: {ex}");
            }
        }

        /// <summary>
        /// 模组启用时调用，订阅关卡初始化事件
        /// </summary>
        private void OnEnable()
        {
            LevelManager.OnLevelInitialized += OnLevelInitialized;
            Debug.Log("[SuperStorageMod] OnEnable: Subscribed to OnLevelInitialized.");
        }

        /// <summary>
        /// 模组禁用时调用，取消订阅事件并保存解锁状态
        /// </summary>
        private void OnDisable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            // 保存当前解锁状态，确保下次启用时能正确恢复
            var tree = FindStoragePerkTree();
            if (tree != null)
            {
                SaveUnlockedBackupToDisk(tree);
            }
        }

        /// <summary>
        /// 关卡初始化时调用，开始注入逻辑
        /// </summary>
        private void OnLevelInitialized()
        {
            Debug.Log("[SuperStorageMod] OnLevelInitialized started.");
            Inject().Forget();
        }

        /// <summary>
        /// 主注入函数，处理缓存物品并添加仓库扩容技能
        /// </summary>
        private async UniTaskVoid Inject()
        {
            try
            {
                Debug.Log("[SuperStorageMod] Injecting... Waiting for level load to settle.");
                // 增加延迟，确保 Level Initialization 完全结束，避免与 SetCharacterPosition 等逻辑冲突
                await UniTask.Delay(System.TimeSpan.FromSeconds(INJECT_DELAY_SECONDS)); 
                
                Debug.Log("[SuperStorageMod] Starting DrainBufferToStorage...");
                // 1. 处理缓存物品
                // 修复：禁用自动提取功能。
                // 原有的逻辑会自动将“马蜂自提点”（Express Cabinet）的物品移动到仓库/背包。
                // 这导致玩家误以为物品丢失（其实是进了背包），或者在背包满时可能导致物品真正丢失。
                // await DrainBufferToStorage(); 
                Debug.Log("[SuperStorageMod] DrainBufferToStorage skipped (Fixed).");
                
                // 2. 查找技能树
                var tree = FindStoragePerkTree();
                if (tree == null)
                {
                    Debug.LogWarning("[SuperStorageMod] Storage PerkTree not found! Mod will not function.");
                    return;
                }
                Debug.Log($"[SuperStorageMod] Found existing PerkTree: {tree.name} (ID: {tree.ID})");

                // 3. 继续原有逻辑
                InjectInternal(tree);

                // 4. 恢复解锁状态
                RestoreUnlockedFromDisk(tree);
                
                Debug.Log("[SuperStorageMod] Injection complete.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] Fatal error in Inject: {ex}");
            }
        }

        /// <summary>
        /// 内部注入函数，向技能树添加仓库扩容节点
        /// </summary>
        /// <param name="tree">技能树对象</param>
        private void InjectInternal(PerkTree tree)
        {
            var basePerk = FindBaseStoragePerk(tree);
            if (basePerk == null)
            {
                 Debug.LogWarning("[SuperStorageMod] Base storage perk not found.");
                 return;
            }

            // 确保找到的是有效的 RelationNode
            var baseNode = tree.RelationGraphOwner.RelationGraph.GetRelatedNode(basePerk);
            if (baseNode == null) 
            {
                Debug.LogWarning($"[SuperStorageMod] Related node for base perk {basePerk.name} not found.");
                return;
            }

            var requireTimeTicks = GetPrivateLong(basePerk.Requirement, "requireTime");
            var icon = basePerk.Icon;
            var quality = basePerk.DisplayQuality;

            var graph = tree.RelationGraphOwner.graph;

            // 将新增节点放到官方空白区域并分别挂载到指定节点
            var placements = new (string anchorRawName, System.Func<PerkRelationNode, Vector2> offsetFn, int tierIndex)[]
            {
                ("Perk_Storage_1", n => new Vector2(-60f - n.cachedPosition.x, 200f - n.cachedPosition.y), 0),
                ("Perk_Storage_2", n => new Vector2(-60f - n.cachedPosition.x, 300f - n.cachedPosition.y), 1),
                ("Perk_Storage_3", n => new Vector2(-60f - n.cachedPosition.x, 400f - n.cachedPosition.y), 2),
                ("Perk_Storage_4", n => new Vector2(-60f - n.cachedPosition.x, 500f - n.cachedPosition.y), 3),
                ("Perk_Storage_y_5", n => new Vector2(120f - n.cachedPosition.x, 900f - n.cachedPosition.y), 4),
                ("Perk_Storage_y_5", n => new Vector2(120f - n.cachedPosition.x, 820f - n.cachedPosition.y), 5),
                ("Perk_Storage_y_5", n => new Vector2(30f - n.cachedPosition.x, 820f - n.cachedPosition.y), 6),
                ("Perk_Storage_y_5", n => new Vector2(-60f - n.cachedPosition.x, 1040f - n.cachedPosition.y), 7),
                ("Perk_Storage_y_5", n => new Vector2(300f - n.cachedPosition.x, 1040f - n.cachedPosition.y), 8),
            };

            foreach (var p in placements)
            {
                var tier = tiers[p.tierIndex];
                if (TreeAlreadyHasTier(tree, tier.nameKey)) continue;

                var anchor = FindPerkByRawName(tree, p.anchorRawName);
                if (anchor == null) 
                {
                    // 尝试用 basePerk 作为备用锚点，如果指定的锚点找不到
                    anchor = basePerk;
                }
                
                var anchorNode = tree.RelationGraphOwner.RelationGraph.GetRelatedNode(anchor);
                if (anchorNode == null) continue;

                var perkGO = new GameObject($"SuperStorageMod_{tier.nameKey}");
                perkGO.transform.SetParent(tree.transform);
                var perk = perkGO.AddComponent<Perk>();

                SetPrivate(perk, "master", tree);
                SetPrivate(perk, "icon", icon);
                SetPrivate(perk, "quality", quality);
                SetPrivate(perk, "displayName", tier.nameKey);
                SetPrivate(perk, "hasDescription", true);
                SetPrivate(perk, "defaultUnlocked", false);

                var req = new PerkRequirement
                {
                    level = tier.requireLevel,
                    cost = new Cost(tier.money, tier.items.Select(e => ((int)e.id, (long)e.amount)).ToArray()),
                    requireTime = requireTimeTicks
                };
                SetPrivate(perk, "requirement", req);

                var add = perkGO.AddComponent<AddPlayerStorage>();
                SetPrivate(add, "addCapacity", tier.addCap);

                var watcher = perkGO.AddComponent<ModUnlockWatcher>();
                watcher.Init(tree, perk);

                AddPerkToTree(tree, perk);
                var node = AddGraphNode(graph, perk);
                if (node == null) 
                {
                    Debug.LogError($"[SuperStorageMod] Failed to add graph node for {tier.nameKey}");
                    continue;
                }
                var offset = p.offsetFn(anchorNode);
                node.cachedPosition = anchorNode.cachedPosition + offset;
                // 不与官方节点建立连接，保持完全独立
            }

            tree.Load();
            PlayerStorage.NotifyCapacityDirty();
        }

        /// <summary>
        /// 查找仓库技能树
        /// </summary>
        /// <returns>仓库技能树对象，找不到返回null</returns>
        private PerkTree? FindStoragePerkTree()
        {
            var trees = PerkTreeManager.Instance?.perkTrees;
            if (trees == null) return null;
            
            // 优先查找包含 AddPlayerStorage 组件的 PerkTree，这更可靠
            var byComponent = trees.FirstOrDefault(t => t != null && t.Perks.Any(p => p != null && p.GetComponent<AddPlayerStorage>() != null));
            if (byComponent != null) return byComponent;

            // 其次尝试通过 ID 查找（假设官方 ID 不变）
            var byID = trees.FirstOrDefault(t => t != null && (t.ID == "Main" || t.ID == "PerkTree_Main")); // 示例ID，实际可能不同
            if (byID != null) return byID;

            // 最后尝试通过名称查找
            return trees.FirstOrDefault(t => t != null && (t.DisplayName.Contains("仓库") || t.DisplayName.Contains("Storage")));
        }

        /// <summary>
        /// 查找基础仓库技能节点
        /// </summary>
        /// <param name="tree">技能树对象</param>
        /// <returns>基础仓库技能节点</returns>
        private static Perk? FindBaseStoragePerk(PerkTree tree)
        {
            if (tree == null) return null;
            
            var candidates = tree.Perks.Where(p => p != null && p.GetComponent<AddPlayerStorage>() != null).ToList();
            if (candidates.Count == 0) return null;

            // 优先通过组件数值查找 (90-110 容量的通常是基础包)
            var byCap100 = candidates.FirstOrDefault(p =>
            {
                var add = p.GetComponent<AddPlayerStorage>();
                var fi = typeof(AddPlayerStorage).GetField("addCapacity", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fi == null) return false;
                var v = fi.GetValue(add);
                return v is int i && i >= BASE_STORAGE_MIN_CAP && i <= BASE_STORAGE_MAX_CAP;
            });
            if (byCap100 != null) return byCap100;

            // 其次尝试名称
            var exactName = candidates.FirstOrDefault(p => (p.DisplayName ?? string.Empty).Contains("超级仓库") || (p.DisplayName ?? string.Empty).Contains("Storage"));
            if (exactName != null) return exactName;

            return candidates.First();
        }

        /// <summary>
        /// 检查技能树是否已包含指定等级
        /// </summary>
        /// <param name="tree">技能树对象</param>
        /// <param name="nameKey">等级名称键</param>
        /// <returns>是否已包含</returns>
        private bool TreeAlreadyHasTier(PerkTree tree, string nameKey)
        {
            return tree.Perks.Any(p => p != null && p.DisplayNameRaw == nameKey);
        }

        /// <summary>
        /// 向技能树添加技能节点
        /// </summary>
        /// <param name="tree">技能树对象</param>
        /// <param name="perk">技能节点</param>
        private static void AddPerkToTree(PerkTree tree, Perk perk)
        {
            var fi = typeof(PerkTree).GetField("perks", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = fi.GetValue(tree) as System.Collections.IList;
            if (list != null && !list.Contains(perk)) list.Add(perk);
        }

        /// <summary>
        /// 向关系图添加节点
        /// </summary>
        /// <param name="graph">关系图对象</param>
        /// <param name="perk">技能节点</param>
        /// <returns>关系图节点</returns>
        private static PerkRelationNode? AddGraphNode(object graph, Perk perk)
        {
            try
            {
                // 查找 Graph.AddNode<T>() 方法
                var methods = graph.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo? addNodeMethod = null;

                foreach (var m in methods)
                {
                    if (m.Name != "AddNode") continue;
                    if (!m.IsGenericMethodDefinition) continue;
                    
                    var typeArgs = m.GetGenericArguments();
                    if (typeArgs.Length != 1) continue;

                    var parameters = m.GetParameters();
                    if (parameters.Length == 0)
                    {
                        // AddNode<T>()
                        addNodeMethod = m;
                        // break; // 移除以允许回退到 AddNode<T>(Vector2)（如果可用）
                    }
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Vector2))
                    {
                        // AddNode<T>(Vector2) - 优先使用带坐标的版本，设为0
                        addNodeMethod = m;
                        // 不break，继续看有没有无参的，或者就用这个
                    }
                }

                if (addNodeMethod != null)
                {
                    var gen = addNodeMethod.MakeGenericMethod(typeof(PerkRelationNode));
                    var ps = gen.GetParameters();
                    object? node = null;
                    if (ps.Length == 0)
                    {
                         node = gen.Invoke(graph, Array.Empty<object>());
                    }
                    else
                    {
                         node = gen.Invoke(graph, new object[] { Vector2.zero });
                    }

                    if (node is PerkRelationNode prn)
                    {
                        prn.relatedNode = perk;
                        return prn;
                    }
                }
                else
                {
                    Debug.LogError("[SuperStorageMod] Failed to find Graph.AddNode generic method.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            return null;
        }

        /// <summary>
        /// 设置对象的私有字段值
        /// </summary>
        /// <param name="target">目标对象</param>
        /// <param name="field">字段名</param>
        /// <param name="value">字段值</param>
        private static void SetPrivate(object target, string field, object value)
        {
            var fi = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            fi?.SetValue(target, value);
        }

        /// <summary>
        /// 获取对象的私有字段值
        /// </summary>
        /// <typeparam name="T">字段类型</typeparam>
        /// <param name="target">目标对象</param>
        /// <param name="field">字段名</param>
        /// <returns>字段值</returns>
        [return: MaybeNull]
        private static T GetPrivate<T>(object target, string field)
        {
            var fi = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return fi != null ? (T)fi.GetValue(target) : default;
        }

        /// <summary>
        /// 获取对象的私有长整型字段值
        /// </summary>
        /// <param name="target">目标对象</param>
        /// <param name="field">字段名</param>
        /// <returns>字段值</returns>
        private static long GetPrivateLong(object target, string field)
        {
            var fi = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi == null) return 0L;
            var v = fi.GetValue(target);
            if (v is long l) return l;
            return 0L;
        }

        /// <summary>
        /// 测量技能树列的属性，包括极值Y坐标、步长和方向符号
        /// </summary>
        /// <param name="tree">技能树对象</param>
        /// <param name="baseNode">基础节点</param>
        /// <param name="toleranceX">X轴容差</param>
        /// <returns>包含极值Y、步长和方向符号的元组</returns>
        private static (float extremeY, float step, float downSign) MeasureColumn(PerkTree tree, PerkRelationNode baseNode, float toleranceX = TOLERANCE_X)
        {
            if (tree == null || baseNode == null)
            {
                return (0f, DEFAULT_NODE_SPACING, -1f);
            }

            var graph = tree.RelationGraphOwner?.RelationGraph;
            if (graph == null)
            {
                return (baseNode.cachedPosition.y, DEFAULT_NODE_SPACING, -1f);
            }

            float baseX = baseNode.cachedPosition.x;
            float minY = baseNode.cachedPosition.y;
            float maxY = baseNode.cachedPosition.y;
            System.Collections.Generic.List<float> deltas = new System.Collections.Generic.List<float>();
            var q = new System.Collections.Generic.Queue<PerkRelationNode>();
            var visited = new System.Collections.Generic.HashSet<PerkRelationNode>();
            q.Enqueue(baseNode);
            visited.Add(baseNode);
            
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                foreach (var child in graph.GetOutgoingNodes(n))
                {
                    if (child == null || !visited.Add(child)) continue;
                    if (Mathf.Abs(child.cachedPosition.x - baseX) < toleranceX)
                    {
                        minY = Mathf.Min(minY, child.cachedPosition.y);
                        maxY = Mathf.Max(maxY, child.cachedPosition.y);
                        var d = child.cachedPosition.y - n.cachedPosition.y;
                        if (Mathf.Abs(d) > MIN_DELTA_THRESHOLD) deltas.Add(d);
                    }
                    q.Enqueue(child);
                }
            }
            
            float avgDelta = deltas.Count > 0 ? deltas.Average() : -4.0f;
            float downSign = avgDelta < 0 ? -1f : 1f;
            float step = deltas.Count > 0 ? deltas.Select(Mathf.Abs).Max() : Mathf.Abs(avgDelta);
            float extremeY = downSign < 0 ? minY : maxY;
            return (extremeY, step, downSign);
        }

        /// <summary>
        /// 根据显示名称查找技能节点
        /// </summary>
        /// <param name="tree">技能树对象</param>
        /// <param name="contains">包含的字符串</param>
        /// <returns>找到的技能节点，找不到返回null</returns>
        private static Perk? FindPerkByName(PerkTree tree, string contains)
        {
            if (tree == null || string.IsNullOrEmpty(contains)) return null;
            return tree.Perks.FirstOrDefault(p => p != null && (p.DisplayName ?? string.Empty).Contains(contains));
        }

        /// <summary>
        /// 根据原始名称查找技能节点
        /// </summary>
        /// <param name="tree">技能树对象</param>
        /// <param name="raw">原始名称</param>
        /// <returns>找到的技能节点，找不到返回null</returns>
        private static Perk? FindPerkByRawName(PerkTree tree, string raw)
        {
            if (tree == null || string.IsNullOrEmpty(raw)) return null;
            return tree.Perks.FirstOrDefault(p => p != null && p.DisplayNameRaw == raw);
        }

        /// <summary>
        /// 计算节点的水平间距
        /// </summary>
        /// <param name="anchor">锚点节点</param>
        /// <param name="tree">技能树对象</param>
        /// <returns>水平间距</returns>
        private static float ComputeDx(PerkRelationNode anchor, PerkTree tree)
        {
            if (anchor == null || tree == null) return DEFAULT_NODE_SPACING;

            var graph = tree.RelationGraphOwner?.RelationGraph;
            if (graph == null) return DEFAULT_NODE_SPACING;

            var nodes = graph.GetIncomingNodes(anchor).Concat(graph.GetOutgoingNodes(anchor)).ToList();
            float baseX = anchor.cachedPosition.x;
            var diffs = nodes.Where(n => n != null).Select(n => Mathf.Abs(n.cachedPosition.x - baseX)).Where(d => d > MIN_DELTA_THRESHOLD).ToList();
            float dx = diffs.Count > 0 ? diffs.Average() : DEFAULT_NODE_SPACING;
            return Mathf.Max(dx, MIN_NODE_SPACING);
        }

        /// <summary>
        /// 计算节点的向上垂直间距
        /// </summary>
        /// <param name="anchor">锚点节点</param>
        /// <param name="tree">技能树对象</param>
        /// <returns>垂直间距</returns>
        private static float ComputeDyUp(PerkRelationNode anchor, PerkTree tree)
        {
            if (anchor == null || tree == null) return DEFAULT_NODE_SPACING;

            var graph = tree.RelationGraphOwner?.RelationGraph;
            if (graph == null) return DEFAULT_NODE_SPACING;

            var nodes = graph.GetIncomingNodes(anchor).ToList();
            float baseY = anchor.cachedPosition.y;
            var diffs = nodes.Where(n => n != null).Select(n => n.cachedPosition.y - baseY).ToList();
            if (diffs.Count == 0) return DEFAULT_NODE_SPACING;
            float avg = diffs.Average();
            float sign = avg < 0 ? -1f : 1f; 
            float mag = diffs.Select(d => Mathf.Abs(d)).Max();
            mag = Mathf.Max(mag, DEFAULT_NODE_SPACING);
            return sign * mag;
        }

        /// <summary>
        /// 将缓存物品转移到仓库
        /// </summary>
        private static async UniTask DrainBufferToStorage()
        {
            var buf = PlayerStorage.IncomingItemBuffer;
            if (buf == null) return;
            int guard = BUFFER_GUARD_COUNT;
            while (buf.Count > 0 && guard-- > 0)
            {
                int idx = buf.Count - 1;
                // 防止越界
                if (idx < 0 || idx >= buf.Count) break;

                int countBefore = buf.Count;
                try
                {
                    await PlayerStorage.TakeBufferItem(idx);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SuperStorageMod] Failed to take buffer item at {idx}: {ex}");
                }
                if (buf.Count == countBefore) break;
            }
        }

        /// <summary>
        /// 模组解锁状态监视组件
        /// </summary>
        private class ModUnlockWatcher : MonoBehaviour
        {
            private PerkTree? tree;
            private Perk? perk;
            private bool last;
            
            /// <summary>
            /// 初始化监视组件
            /// </summary>
            /// <param name="t">技能树对象</param>
            /// <param name="p">技能节点</param>
            public void Init(PerkTree t, Perk p)
            {
                tree = t;
                perk = p;
                last = perk != null && perk.Unlocked;
            }
            
            /// <summary>
            /// 每帧更新，检查解锁状态变化
            /// </summary>
            private void Update()
            {
                var cur = perk != null && perk.Unlocked;
                if (cur && !last)
                {
                    if (tree != null) SaveUnlockedBackupToDisk(tree);
                    last = true;
                }
                else
                {
                    last = cur;
                }
            }
        }

        /// <summary>
        /// 获取技能树ID
        /// </summary>
        /// <param name="tree">技能树对象</param>
        /// <returns>技能树ID</returns>
        private static string GetTreeID(PerkTree tree)
        {
            if (tree == null) return UNKNOWN_TREE_ID;
            
            var tp = typeof(PerkTree);
            var pi = tp.GetProperty("ID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi != null)
            {
                var v = pi.GetValue(tree) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            var fi = tp.GetField("perkTreeID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi != null)
            {
                var v = fi.GetValue(tree) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            var name = tree.name;
            return string.IsNullOrEmpty(name) ? UNKNOWN_TREE_ID : name;
        }

        /// <summary>
        /// 通过简单名称查找类型
        /// </summary>
        /// <param name="name">类型名称</param>
        /// <returns>类型对象</returns>
        private static Type? FindTypeBySimpleName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == name);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 获取当前存档槽位
        /// </summary>
        /// <returns>存档槽位编号</returns>
        private static int GetCurrentSlot()
        {
            var t = FindTypeBySimpleName("SavesSystem");
            var pi = t?.GetProperty("CurrentSlot", BindingFlags.Public | BindingFlags.Static);
            if (pi != null)
            {
                object? v = pi.GetValue(null, null);
                if (v is int i && i > 0) return i;
            }
            return 1;
        }

        /// <summary>
        /// 获取备份目录路径
        /// </summary>
        /// <returns>备份目录路径</returns>
        private static string GetBackupDir()
        {
            var dir = Path.Combine(Application.persistentDataPath, BACKUP_DIR_NAME);
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>
        /// 获取备份文件路径
        /// </summary>
        /// <returns>备份文件路径</returns>
        private static string GetBackupPath()
        {
            int slot = GetCurrentSlot();
            return Path.Combine(GetBackupDir(), $"{BACKUP_FILE_PREFIX}{slot}.txt");
        }

        /// <summary>
        /// 保存解锁状态备份到磁盘
        /// </summary>
        /// <param name="tree">技能树对象</param>
        private static void SaveUnlockedBackupToDisk(PerkTree tree)
        {
            if (tree == null) return;
            
            try
            {
                var ids = tree.Perks.Where(p => p != null && (p.DisplayNameRaw ?? string.Empty).StartsWith(PERK_NAME_PREFIX) && p.Unlocked)
                    .Select(p => p.gameObject.name).ToArray();
                File.WriteAllLines(GetBackupPath(), ids);
            }
            catch { }
        }

        /// <summary>
        /// 从磁盘恢复解锁状态
        /// </summary>
        /// <param name="tree">技能树对象</param>
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
            var set = new System.Collections.Generic.HashSet<string>(lines.Where(s => !string.IsNullOrEmpty(s)));

            foreach (var p in tree.Perks)
            {
                if (p == null || p.gameObject == null || !set.Contains(p.gameObject.name)) continue;
                
                try
                {
                    if (!p.Unlocked)
                    {
                        p.ForceUnlock();
                        Debug.Log($"[SuperStorageMod] Restored perk: {p.gameObject.name}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SuperStorageMod] Failed to restore perk {p.gameObject?.name ?? "Unknown"}: {ex}");
                }
            }
        }
    }
}
