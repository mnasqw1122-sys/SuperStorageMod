using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Cysharp.Threading.Tasks;
using Duckov.Modding;
using Duckov.PerkTrees;
using Duckov.PerkTrees.Interactable;
using Duckov.Economy;
using SodaCraft.Localizations;
using UnityEngine;
using NodeCanvas.Framework;

namespace SuperStorageMod
{
    /// <summary>
    /// 超级仓库模组主行为类，全新重置版：独立技能树，独立UI入口，不再干涉官方节点
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const int SMALL_BOX_ID = 50; 
        private const int MED_BOX_ID = 49;   
        private const int BIG_BOX_ID = 48;   
        private const int INJECT_DELAY_SECONDS = 1; 
        private const string PERK_NAME_PREFIX = "SuperStorage_"; 
        private const string BACKUP_DIR_NAME = "SuperStorageMod"; 
        private const string BACKUP_FILE_PREFIX = "backup_slot_"; 
        
        // 我们自己的独立技能树ID
        private const string CUSTOM_TREE_ID = "SuperStorageExpand";
        // 交互按钮的名称键
        private const string CUSTOM_INTERACT_KEY = "SuperStorage_InteractName";

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

        private void Awake()
        {
            Debug.Log("[SuperStorageMod] Awake started.");
            try
            {
                // 注册各等级名称
                foreach (var t in tiers)
                {
                    LocalizationManager.SetOverrideText(t.nameKey, t.displayName);
                }
                
                // 注册独立技能树和按钮的名称
                LocalizationManager.SetOverrideText("PerkTree_" + CUSTOM_TREE_ID, "超库扩容");
                LocalizationManager.SetOverrideText(CUSTOM_INTERACT_KEY, "超库扩容");
                
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
            var tree = PerkTreeManager.GetPerkTree(CUSTOM_TREE_ID);
            if (tree != null)
            {
                SaveUnlockedBackupToDisk(tree);
            }
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
                await UniTask.Delay(TimeSpan.FromSeconds(INJECT_DELAY_SECONDS)); 
                
                var officialTree = PerkTreeManager.GetPerkTree("StorageExpand");
                if (officialTree == null)
                {
                    Debug.LogWarning("[SuperStorageMod] Official StorageExpand tree not found! Cannot copy base data.");
                    return;
                }

                // 1. 创建我们自己的独立技能树
                var myTree = PerkTreeManager.GetPerkTree(CUSTOM_TREE_ID);
                if (myTree == null)
                {
                    myTree = CreateCustomPerkTree(officialTree);
                    if (myTree == null) return;
                }

                // 2. 恢复解锁状态
                RestoreUnlockedFromDisk(myTree);
                
                // 3. 在场景中注入我们的交互入口
                InjectInvokerIntoScene();
                
                Debug.Log("[SuperStorageMod] Injection complete.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SuperStorageMod] Fatal error in Inject: {ex}");
            }
        }

        /// <summary>
        /// 创建完全独立的技能树
        /// </summary>
        private PerkTree? CreateCustomPerkTree(PerkTree officialTree)
        {
            Debug.Log("[SuperStorageMod] Creating custom PerkTree...");
            
            var myTreeGo = new GameObject("PerkTree_" + CUSTOM_TREE_ID);
            myTreeGo.transform.SetParent(PerkTreeManager.Instance.transform);
            
            var myTree = myTreeGo.AddComponent<PerkTree>();
            SetPrivate(myTree, "perkTreeID", CUSTOM_TREE_ID);
            
            // 尝试创建图所有者
            var ownerType = FindTypeBySimpleName("PerkTreeRelationGraphOwner");
            if (ownerType == null)
            {
                Debug.LogError("[SuperStorageMod] PerkTreeRelationGraphOwner type not found!");
                return null;
            }
            var owner = myTreeGo.AddComponent(ownerType);
            
            // 创建图数据对象
            var graphType = FindTypeBySimpleName("PerkRelationGraph");
            if (graphType == null) return null;
            var graph = ScriptableObject.CreateInstance(graphType) as Graph;
            
            // 赋值给图所有者
            var propGraph = ownerType.GetProperty("graph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) 
                         ?? ownerType.BaseType?.GetProperty("graph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (propGraph != null && graph != null)
            {
                propGraph.SetValue(owner, graph);
            }
            
            SetPrivate(myTree, "relationGraphOwner", owner);
            
            // 获取官方基础数据（如图标，时间）
            var basePerk = officialTree.Perks.FirstOrDefault(p => p != null && p.GetComponent<AddPlayerStorage>() != null);
            var icon = basePerk?.Icon;
            var quality = basePerk != null ? basePerk.DisplayQuality : default;
            var requireTimeTicks = basePerk != null ? GetPrivateLong(basePerk.Requirement, "requireTime") : 0L;

            // 动态生成节点
            for (int i = 0; i < tiers.Length; i++)
            {
                var tier = tiers[i];
                var perkGO = new GameObject($"SuperStorageMod_{tier.nameKey}");
                perkGO.transform.SetParent(myTree.transform);
                var perk = perkGO.AddComponent<Perk>();
                
                SetPrivate(perk, "master", myTree);
                SetPrivate(perk, "icon", icon);
                SetPrivate(perk, "quality", quality);
                SetPrivate(perk, "displayName", tier.nameKey);
                SetPrivate(perk, "hasDescription", true);
                SetPrivate(perk, "defaultUnlocked", false);
                
                var reqType = FindTypeBySimpleName("PerkRequirement");
                object? req = null;
                if (reqType != null)
                {
                    req = Activator.CreateInstance(reqType);
                    reqType.GetField("level")?.SetValue(req, tier.requireLevel);
                    var costType = FindTypeBySimpleName("Cost");
                    if (costType != null)
                    {
                        var itemsArr = tier.items.Select(e => ((int)e.id, (long)e.amount)).ToArray();
                        object? cost = Activator.CreateInstance(costType, tier.money, itemsArr);
                        reqType.GetField("cost")?.SetValue(req, cost);
                    }
                    reqType.GetField("requireTime")?.SetValue(req, requireTimeTicks);
                }
                SetPrivate(perk, "requirement", req);
                
                var add = perkGO.AddComponent<AddPlayerStorage>();
                SetPrivate(add, "addCapacity", tier.addCap);
                
                var watcher = perkGO.AddComponent<ModUnlockWatcher>();
                watcher.Init(myTree, perk);
                
                AddPerkToTree(myTree, perk);
                
                var node = AddGraphNode(graph, perk);
                if (node != null) 
                {
                    // 将节点纵向排列，官方UI会自动适应边界
                    node.cachedPosition = new Vector2(0, i * 150f);
                }
            }
            
            // 将新的技能树注册到全局管理器
            PerkTreeManager.Instance.perkTrees.Add(myTree);
            myTree.Load(); // 尝试读取官方存档机制中的数据
            
            return myTree;
        }

        /// <summary>
        /// 将我们的独立交互入口注入到场景的官方交互组中
        /// </summary>
        private void InjectInvokerIntoScene()
        {
            Debug.Log("[SuperStorageMod] Injecting Invoker into Scene...");
            
            var allInvokers = Resources.FindObjectsOfTypeAll<PerkTreeUIInvoker>();
            var sceneInvokers = allInvokers.Where(inv => inv.perkTreeID == "StorageExpand" && inv.gameObject.scene.IsValid()).ToList();
            
            Debug.Log($"[SuperStorageMod] Found {sceneInvokers.Count} official StorageExpand invokers in scene.");
            
            if (sceneInvokers.Count == 0) 
            {
                Debug.LogWarning("[SuperStorageMod] Could not find official StorageExpand invoker in scene.");
                return;
            }
            
            // 我们为每一个找到的都注入，防止漏掉真正的那个
            foreach (var officialInvoker in sceneInvokers)
            {
                // 停用官方节点，以便我们可以安全地实例化它而不会触发克隆体的Awake
                bool wasActive = officialInvoker.gameObject.activeSelf;
                officialInvoker.gameObject.SetActive(false);

                // 复制官方的交互器
                var myInvokerGo = UnityEngine.Object.Instantiate(officialInvoker.gameObject, officialInvoker.transform.parent);
                myInvokerGo.name = "SuperStorage_Invoker";
                
                var myInvoker = myInvokerGo.GetComponent<PerkTreeUIInvoker>();
                
                // 清理可能复制过来的其他组内成员，防止引用错乱并解决 Awake 中的 NullReferenceException
                SetPrivate(myInvoker, "otherInterablesInGroup", new List<InteractableBase>());
                myInvoker.interactableGroup = false;

                myInvoker.perkTreeID = CUSTOM_TREE_ID;
                myInvoker.overrideInteractName = true;
                myInvoker._overrideInteractNameKey = CUSTOM_INTERACT_KEY;
                // 注意：在最新版本中我们直接调用 setter，它会自动设置 overrideInteractName
                myInvoker.InteractName = CUSTOM_INTERACT_KEY;
                
                // 重新激活
                myInvokerGo.SetActive(wasActive);
                officialInvoker.gameObject.SetActive(wasActive);
                
                // 强制将层级设置为 Interactable，以便射线检测能捕捉到
                myInvokerGo.layer = LayerMask.NameToLayer("Interactable");
                
                // 确保我们注入的交互器能够正常唤醒
                var awakeMethod = typeof(InteractableBase).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (awakeMethod != null)
                {
                    awakeMethod.Invoke(myInvoker, null);
                }

                // Awake可能会添加一个禁用的Collider，我们需要确保它可用
                var coll = myInvoker.GetComponent<Collider>();
                if (coll != null)
                {
                    coll.enabled = true;
                    // 同步官方的碰撞体大小（如果有的话）
                    var offColl = officialInvoker.GetComponent<Collider>();
                    if (offColl is BoxCollider myBox && coll is BoxCollider offBox)
                    {
                        myBox.center = offBox.center;
                        myBox.size = offBox.size;
                    }
                }

                // 找到包含官方交互器的Master交互组
                var master = FindMasterInteractable(officialInvoker);
                if (master != null) 
                {
                    var list = GetPrivate<List<InteractableBase>>(master, "otherInterablesInGroup");
                    if (list != null && !list.Contains(myInvoker)) 
                    {
                        list.Add(myInvoker);
                        
                        // 手动同步Master的变换和标识状态，因为我们是在运行时动态添加的
                        myInvoker.transform.position = master.transform.position;
                        myInvoker.transform.rotation = master.transform.rotation;
                        myInvoker.interactMarkerOffset = master.interactMarkerOffset;
                        myInvoker.MarkerActive = false;

                        Debug.Log($"[SuperStorageMod] Successfully injected custom invoker into master group of {master.gameObject.name}.");
                        
                        // 强制重置玩家当前的交互目标以刷新UI
                        ForceRefreshInteractHUD();
                    }
                } 
                else 
                {
                    // 如果官方交互器本身没有归属于组，则把它变成一个组
                    Debug.LogWarning($"[SuperStorageMod] Master interactable not found for {officialInvoker.gameObject.name}, trying to make official invoker a group.");
                    officialInvoker.interactableGroup = true;
                    var list = GetPrivate<List<InteractableBase>>(officialInvoker, "otherInterablesInGroup");
                    if (list == null) 
                    {
                        list = new List<InteractableBase>();
                        SetPrivate(officialInvoker, "otherInterablesInGroup", list);
                    }
                    if (!list.Contains(myInvoker)) 
                    {
                        list.Add(myInvoker);
                        myInvoker.transform.position = officialInvoker.transform.position;
                        myInvoker.transform.rotation = officialInvoker.transform.rotation;
                        myInvoker.interactMarkerOffset = officialInvoker.interactMarkerOffset;
                        myInvoker.MarkerActive = false;
                        
                        ForceRefreshInteractHUD();
                    }
                }
            }
        }

        private InteractableBase? FindMasterInteractable(InteractableBase target)
        {
            var all = Resources.FindObjectsOfTypeAll<InteractableBase>();
            foreach(var i in all) 
            {
                if (!i.gameObject.scene.IsValid()) continue;
                if (i.interactableGroup) 
                {
                    var list = GetPrivate<List<InteractableBase>>(i, "otherInterablesInGroup");
                    if (list != null && list.Contains(target)) 
                    {
                        return i;
                    }
                }
            }
            return null;
        }

        private void ForceRefreshInteractHUD()
        {
            try
            {
                var mainChar = LevelManager.Instance?.MainCharacter;
                if (mainChar != null && mainChar.interactAction != null)
                {
                    var currentMaster = mainChar.interactAction.MasterInteractableAround;
                    // 先置空
                    mainChar.interactAction.SetInteractableTarget(null);
                    // 稍微延迟一下或者下一帧再恢复，不过直接置空通常足以让HUD在下一帧Update时重新检测到差异并刷新
                    if (currentMaster != null)
                    {
                        mainChar.interactAction.SetInteractableTarget(currentMaster);
                    }
                    Debug.Log("[SuperStorageMod] Force refreshed InteractHUD.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuperStorageMod] Failed to force refresh HUD: {ex.Message}");
            }
        }

        private static void AddPerkToTree(PerkTree tree, Perk perk)
        {
            var fi = typeof(PerkTree).GetField("perks", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var list = fi?.GetValue(tree) as System.Collections.IList;
            if (list == null)
            {
                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(typeof(Perk));
                list = Activator.CreateInstance(listType) as System.Collections.IList;
                fi?.SetValue(tree, list);
            }
            if (list != null && !list.Contains(perk)) list.Add(perk);
        }

        private static PerkRelationNode? AddGraphNode(object? graph, Perk perk)
        {
            if (graph == null) return null;
            try
            {
                var methods = graph.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo? addNodeMethod = null;

                foreach (var m in methods)
                {
                    if (m.Name != "AddNode" || !m.IsGenericMethodDefinition) continue;
                    
                    var typeArgs = m.GetGenericArguments();
                    if (typeArgs.Length != 1) continue;

                    var parameters = m.GetParameters();
                    if (parameters.Length == 0) addNodeMethod = m;
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Vector2)) addNodeMethod = m;
                }

                if (addNodeMethod != null)
                {
                    var gen = addNodeMethod.MakeGenericMethod(typeof(PerkRelationNode));
                    var ps = gen.GetParameters();
                    object? node = ps.Length == 0 ? gen.Invoke(graph, Array.Empty<object>()) : gen.Invoke(graph, new object[] { Vector2.zero });

                    if (node is PerkRelationNode prn)
                    {
                        prn.relatedNode = perk;
                        return prn;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            return null;
        }

        private static void SetPrivate(object target, string field, object? value)
        {
            var type = target.GetType();
            FieldInfo? fi = null;
            while (type != null && fi == null)
            {
                fi = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                type = type.BaseType;
            }
            fi?.SetValue(target, value);
        }

        [return: MaybeNull]
        private static T GetPrivate<T>(object target, string field)
        {
            var type = target.GetType();
            FieldInfo? fi = null;
            while (type != null && fi == null)
            {
                fi = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                type = type.BaseType;
            }
            return fi != null ? (T)fi.GetValue(target) : default;
        }

        private static long GetPrivateLong(object target, string field)
        {
            var type = target.GetType();
            FieldInfo? fi = null;
            while (type != null && fi == null)
            {
                fi = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                type = type.BaseType;
            }
            if (fi == null) return 0L;
            var v = fi.GetValue(target);
            return v is long l ? l : 0L;
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
            var dir = Path.Combine(Application.persistentDataPath, BACKUP_DIR_NAME);
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string GetBackupPath()
        {
            int slot = GetCurrentSlot();
            return Path.Combine(GetBackupDir(), $"{BACKUP_FILE_PREFIX}{slot}.txt");
        }

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
            
            // 确保恢复后触发仓库容量刷新
            PlayerStorage.NotifyCapacityDirty();
        }
    }
}
