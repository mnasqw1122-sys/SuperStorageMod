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
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const int SMALL_BOX_ID = 50; // 小扩容箱
        private const int MED_BOX_ID = 49;   // 中扩容箱
        private const int BIG_BOX_ID = 48;   // 大扩容箱

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

        private void OnEnable()
        {
            LevelManager.OnLevelInitialized += OnLevelInitialized;
            Debug.Log("[SuperStorageMod] OnEnable: Subscribed to OnLevelInitialized.");
        }

        private void OnDisable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
        }

        private void OnLevelInitialized()
        {
            Debug.Log("[SuperStorageMod] OnLevelInitialized started.");
            Inject().Forget();
        }

        private async UniTaskVoid Inject()
        {
            try
            {
                Debug.Log("[SuperStorageMod] Injecting... Waiting for level load to settle.");
                // 增加延迟，确保 Level Initialization 完全结束，避免与 SetCharacterPosition 等逻辑冲突
                await UniTask.Delay(System.TimeSpan.FromSeconds(1)); 
                
                Debug.Log("[SuperStorageMod] Starting DrainBufferToStorage...");
                // 1. 处理缓存物品
                await DrainBufferToStorage();
                Debug.Log("[SuperStorageMod] DrainBufferToStorage finished.");
                
                // 2. 查找技能树
                var tree = FindStoragePerkTree();
                if (tree == null)
                {
                    Debug.LogWarning("[SuperStorageMod] Storage PerkTree not found! Mod will not function.");
                    return;
                }
                Debug.Log($"[SuperStorageMod] Found existing PerkTree: {tree.name}");

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

        private void InjectInternal(PerkTree tree)
        {
            var basePerk = FindBaseStoragePerk(tree);
            if (basePerk == null)
            {
                 Debug.LogWarning("[SuperStorageMod] Base storage perk not found.");
                 return;
            }

            var baseNode = tree.RelationGraphOwner.RelationGraph.GetRelatedNode(basePerk);
            if (baseNode == null) return;

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
                if (anchor == null) continue;
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
                if (node == null) continue;
                var offset = p.offsetFn(anchorNode);
                node.cachedPosition = anchorNode.cachedPosition + offset;
                // 不与官方节点建立连接，保持完全独立
            }

            tree.Load();
            SaveUnlockedBackupToDisk(tree);
            PlayerStorage.NotifyCapacityDirty();
        }

        private PerkTree? FindStoragePerkTree()
        {
            var trees = PerkTreeManager.Instance?.perkTrees;
            if (trees == null) return null;
            var byName = trees.FirstOrDefault(t => t != null && t.DisplayName.Contains("仓库扩容"));
            if (byName != null) return byName;
            return trees.FirstOrDefault(t => t != null && t.Perks.Any(p => p != null && p.GetComponent<AddPlayerStorage>() != null));
        }

        private static Perk? FindBaseStoragePerk(PerkTree tree)
        {
            var candidates = tree.Perks.Where(p => p != null && p.GetComponent<AddPlayerStorage>() != null).ToList();
            if (candidates.Count == 0) return null;
            var exactName = candidates.FirstOrDefault(p => (p.DisplayName ?? string.Empty).Contains("超级仓库"));
            if (exactName != null) return exactName;
            var byCap100 = candidates.FirstOrDefault(p =>
            {
                var add = p.GetComponent<AddPlayerStorage>();
                var fi = typeof(AddPlayerStorage).GetField("addCapacity", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fi == null) return false;
                var v = fi.GetValue(add);
                return v is int i && i >= 90 && i <= 110;
            });
            return byCap100 ?? candidates.First();
        }

        private bool TreeAlreadyHasTier(PerkTree tree, string nameKey)
        {
            return tree.Perks.Any(p => p != null && p.DisplayNameRaw == nameKey);
        }

        private static void AddPerkToTree(PerkTree tree, Perk perk)
        {
            var fi = typeof(PerkTree).GetField("perks", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = fi.GetValue(tree) as System.Collections.IList;
            if (list != null && !list.Contains(perk)) list.Add(perk);
        }

        private static PerkRelationNode? AddGraphNode(object graph, Perk perk)
        {
            try
            {
                var methods = graph.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "AddNode" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1).ToList();
                object? node = null;
                foreach (var m in methods)
                {
                    var gen = m.MakeGenericMethod(typeof(PerkRelationNode));
                    var ps = gen.GetParameters();
                    if (ps.Length == 0)
                    {
                        node = gen.Invoke(graph, Array.Empty<object>());
                        break;
                    }
                    if (ps.Length == 1 && ps[0].ParameterType.FullName == typeof(UnityEngine.Vector2).FullName)
                    {
                        node = gen.Invoke(graph, new object[] { Vector2.zero });
                        break;
                    }
                }
                PerkRelationNode? prn = node as PerkRelationNode;
                if (prn != null)
                {
                    prn.relatedNode = perk;
                    return prn;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            return null;
        }

        private static void CreateConnection(PerkRelationNode from, PerkRelationNode to)
        {
            try
            {
                // 查找 NodeCanvas.Framework.Connection 类型
                // 由于 NodeCanvas 可能在单独的程序集中，不能直接从 PerkRelationNode 的程序集获取
                var nodeType = from.GetType();
                while (nodeType != null && nodeType.Name != "Node")
                {
                    nodeType = nodeType.BaseType;
                }
                if (nodeType == null) return;

                var connType = nodeType.Assembly.GetType("NodeCanvas.Framework.Connection");
                if (connType == null) return;

                var methods = connType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                var create = methods.FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length >= 2);
                if (create == null) return;
                var ps = create.GetParameters();
                if (ps.Length == 2)
                {
                    create.Invoke(null, new object[] { from, to });
                }
                else
                {
                    // 使用 from.outConnectionType 获取连接类型，比直接 typeof 更安全
                    create.Invoke(null, new object[] { from, to, from.outConnectionType });
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static void SetPrivate(object target, string field, object value)
        {
            var fi = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            fi?.SetValue(target, value);
        }

        [return: MaybeNull]
        private static T GetPrivate<T>(object target, string field)
        {
            var fi = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return fi != null ? (T)fi.GetValue(target) : default;
        }

        private static long GetPrivateLong(object target, string field)
        {
            var fi = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi == null) return 0L;
            var v = fi.GetValue(target);
            if (v is long l) return l;
            return 0L;
        }

        private static (float extremeY, float step, float downSign) MeasureColumn(PerkTree tree, PerkRelationNode baseNode, float toleranceX = 1f)
        {
            var graph = tree.RelationGraphOwner.RelationGraph;
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
                        if (Mathf.Abs(d) > 0.1f) deltas.Add(d);
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

        private static Perk? FindPerkByName(PerkTree tree, string contains)
        {
            return tree.Perks.FirstOrDefault(p => p != null && (p.DisplayName ?? string.Empty).Contains(contains));
        }

        private static Perk? FindPerkByRawName(PerkTree tree, string raw)
        {
            return tree.Perks.FirstOrDefault(p => p != null && p.DisplayNameRaw == raw);
        }

        private static float ComputeDx(PerkRelationNode anchor, PerkTree tree)
        {
            var graph = tree.RelationGraphOwner.RelationGraph;
            var nodes = graph.GetIncomingNodes(anchor).Concat(graph.GetOutgoingNodes(anchor)).ToList();
            float baseX = anchor.cachedPosition.x;
            var diffs = nodes.Where(n => n != null).Select(n => Mathf.Abs(n.cachedPosition.x - baseX)).Where(d => d > 0.1f).ToList();
            float dx = diffs.Count > 0 ? diffs.Average() : 6f;
            return Mathf.Max(dx, 5f);
        }

        private static float ComputeDyUp(PerkRelationNode anchor, PerkTree tree)
        {
            var graph = tree.RelationGraphOwner.RelationGraph;
            var nodes = graph.GetIncomingNodes(anchor).ToList();
            float baseY = anchor.cachedPosition.y;
            var diffs = nodes.Where(n => n != null).Select(n => n.cachedPosition.y - baseY).ToList();
            if (diffs.Count == 0) return 6f;
            float avg = diffs.Average();
            float sign = avg < 0 ? -1f : 1f; 
            float mag = diffs.Select(d => Mathf.Abs(d)).Max();
            mag = Mathf.Max(mag, 6f);
            return sign * mag;
        }

        private static async UniTask DrainBufferToStorage()
        {
            var buf = PlayerStorage.IncomingItemBuffer;
            if (buf == null) return;
            int guard = 128;
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

        private class ModUnlockWatcher : MonoBehaviour
        {
            private PerkTree? tree;
            private Perk? perk;
            private bool last;
            public void Init(PerkTree t, Perk p)
            {
                tree = t;
                perk = p;
                last = perk != null && perk.Unlocked;
            }
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

        private static string GetTreeID(PerkTree tree)
        {
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
            var name = tree != null ? tree.name : string.Empty;
            return string.IsNullOrEmpty(name) ? "UnknownTree" : name;
        }

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

        private static string GetBackupDir()
        {
            var dir = Path.Combine(Application.persistentDataPath, "SuperStorageMod");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string GetBackupPath()
        {
            int slot = GetCurrentSlot();
            return Path.Combine(GetBackupDir(), $"backup_slot_{slot}.txt");
        }

        private static void SaveUnlockedBackupToDisk(PerkTree tree)
        {
            try
            {
                var ids = tree.Perks.Where(p => p != null && (p.DisplayNameRaw ?? string.Empty).StartsWith("SuperStorage_") && p.Unlocked)
                    .Select(p => p.gameObject.name).ToArray();
                File.WriteAllLines(GetBackupPath(), ids);
            }
            catch { }
        }

        private static void RestoreUnlockedFromDisk(PerkTree tree)
        {
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
                if (p == null || !set.Contains(p.gameObject.name)) continue;
                
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
                    Debug.LogError($"[SuperStorageMod] Failed to restore perk {p.gameObject.name}: {ex}");
                }
            }
        }
    }
}
