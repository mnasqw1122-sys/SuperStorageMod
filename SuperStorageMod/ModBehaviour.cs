using BepInEx;                     // 引用 BepInEx 框架，用于创建插件
using BepInEx.Logging;             // 引用 BepInEx 的日志系统
using HarmonyLib;                  // 引用 Harmony 库，用于代码修补
using SodaCraft.Localizations;     // 引用本地化系统
using System;                      // 引用系统核心命名空间
using System.Collections.Generic;  // 引用集合类
using System.Linq;                 // 引用 LINQ 查询
using System.Reflection;           // 引用反射功能
using System.Diagnostics.CodeAnalysis; // 引用代码分析特性
using UnityEngine;                 // 引用 Unity 引擎
using Duckov.PerkTrees;            // 引用 Duckov 技能树系统
using Duckov.PerkTrees.Behaviours; // 引用技能树行为
using SodaCraft.Currencies;        // 引用货币系统
using NodeCanvas.Framework;        // 引用 NodeCanvas 框架
using ParadoxNotion;               // 引用 ParadoxNotion 库
using static Duckov.PerkTrees.PerkTreeRelationGraphOwner; // 引用技能树关系图所有者静态成员

namespace SuperStorageMod           // 定义 SuperStorageMod 命名空间
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)] // 定义 BepInEx 插件属性
    public class ModBehaviour : BaseUnityPlugin // 定义 ModBehaviour 类，继承自 BaseUnityPlugin
    {
        private ManualLogSource _logger; // 声明日志源
        private Harmony _harmony;        // 声明 Harmony 实例

        private void Awake()             // Unity Awake 方法，插件启动时调用
        {
            _logger = Logger;            // 初始化日志源
            _logger.LogInfo("SuperStorageMod 正在加载..."); // 输出加载信息
            try
            {
                Inject();                // 调用注入方法
                _logger.LogInfo("SuperStorageMod 加载完成！"); // 输出完成信息
            }
            catch (Exception ex)         // 捕获异常
            {
                _logger.LogError($"SuperStorageMod 加载失败: {ex}"); // 输出错误信息
                return;                  // 返回
            }

            _harmony = new Harmony(PluginInfo.PLUGIN_GUID); // 创建 Harmony 实例
            _harmony.PatchAll();         // 应用所有补丁
            _logger.LogInfo("SuperStorageMod Harmony 补丁已应用。"); // 输出补丁信息
        }

        private void OnDestroy()         // Unity OnDestroy 方法，插件销毁时调用
        {
            _harmony?.UnpatchSelf();     // 移除 Harmony 补丁
            _logger.LogInfo("SuperStorageMod 已卸载。"); // 输出卸载信息
        }

        // 定义超级仓库的层级数据
        private static readonly (string rawName, string displayNameKey, int capacity, int level, int gold, int[] materials)[] tiers = new[]
        {
            ("Perk_SuperStorage_2", "SuperStorage_Lv2", 60, 2, 10000, new[] { 50 }),   // 第 2 级
            ("Perk_SuperStorage_3", "SuperStorage_Lv3", 120, 3, 20000, new[] { 50 }),  // 第 3 级
            ("Perk_SuperStorage_4", "SuperStorage_Lv4", 180, 4, 30000, new[] { 50 }),  // 第 4 级
            ("Perk_SuperStorage_5", "SuperStorage_Lv5", 240, 5, 40000, new[] { 50 }),  // 第 5 级
            ("Perk_SuperStorage_6", "SuperStorage_Lv6", 300, 6, 50000, new[] { 49 }),  // 第 6 级
            ("Perk_SuperStorage_7", "SuperStorage_Lv7", 360, 7, 60000, new[] { 49 }),  // 第 7 级
            ("Perk_SuperStorage_8", "SuperStorage_Lv8", 420, 8, 70000, new[] { 48 }),  // 第 8 级
            ("Perk_SuperStorage_9", "SuperStorage_Lv9", 480, 9, 80000, new[] { 48 }),  // 第 9 级
            ("Perk_SuperStorage_10", "SuperStorage_Lv10", 540, 10, 90000, new[] { 48 }), // 第 10 级
        };

        // 注入新技能节点到技能树中
        private void Inject()
        {
            _logger.LogInfo("开始注入超级仓库技能节点..."); // 输出开始注入信息
            // 查找仓库扩容技能树
            var tree = Resources.FindObjectsOfTypeAll<PerkTree>().FirstOrDefault(t => t.treeID == "Storage");
            if (tree == null) // 如果未找到
            {
                _logger.LogError("未找到仓库扩容技能树！"); // 输出错误信息
                return; // 返回
            }
            _logger.LogInfo($"找到技能树: {tree.treeID}"); // 输出找到技能树信息

            // 获取技能树的关系图
            var graphOwner = tree.GetComponent<PerkTreeRelationGraphOwner>();
            if (graphOwner == null) // 如果未找到
            {
                _logger.LogError("技能树上没有 PerkTreeRelationGraphOwner 组件！"); // 输出错误信息
                return; // 返回
            }
            var graph = graphOwner.RelationGraph; // 获取关系图
            if (graph == null) // 如果图为空
            {
                _logger.LogError("关系图为空！"); // 输出错误信息
                return; // 返回
            }

            // 缓存现有节点位置，用于绝对坐标计算
            var nodeDict = new Dictionary<string, PerkRelationNode>(); // 创建字典存储节点
            foreach (var node in graph.allNodes.OfType<PerkRelationNode>()) // 遍历所有节点
            {
                nodeDict[node.RawName] = node; // 按原始名称缓存节点
            }

            // 定义新节点的锚点和偏移函数
            var placements = new (string anchorRawName, System.Func<PerkRelationNode, Vector2> offsetFn, int tierIndex)[]
            {
                ("Perk_Storage_1", n => new Vector2(-60f - n.cachedPosition.x, 200f - n.cachedPosition.y), 0),   // 第 2 级位置
                ("Perk_Storage_2", n => new Vector2(-60f - n.cachedPosition.x, 300f - n.cachedPosition.y), 1),   // 第 3 级位置
                ("Perk_Storage_3", n => new Vector2(-60f - n.cachedPosition.x, 400f - n.cachedPosition.y), 2),   // 第 4 级位置
                ("Perk_Storage_4", n => new Vector2(-60f - n.cachedPosition.x, 500f - n.cachedPosition.y), 3),   // 第 5 级位置
                ("Perk_Storage_y_5", n => new Vector2(120f - n.cachedPosition.x, 900f - n.cachedPosition.y), 4), // 第 6 级位置
                ("Perk_Storage_y_5", n => new Vector2(120f - n.cachedPosition.x, 820f - n.cachedPosition.y), 5), // 第 7 级位置
                ("Perk_Storage_y_5", n => new Vector2(30f - n.cachedPosition.x, 820f - n.cachedPosition.y), 6),  // 第 8 级位置（调整后）
                ("Perk_Storage_y_5", n => new Vector2(-60f - n.cachedPosition.x, 1040f - n.cachedPosition.y), 7), // 第 9 级位置
                ("Perk_Storage_y_5", n => new Vector2(300f - n.cachedPosition.x, 1040f - n.cachedPosition.y), 8), // 第 10 级位置
            };

            var injectedPerks = new List<Perk>(); // 创建列表存储注入的技能
            foreach (var (anchorRawName, offsetFn, tierIndex) in placements) // 遍历每个放置定义
            {
                if (!nodeDict.TryGetValue(anchorRawName, out var anchorNode)) // 如果锚点节点不存在
                {
                    _logger.LogWarning($"锚点节点 {anchorRawName} 未找到，跳过第 {tierIndex + 2} 级。"); // 输出警告
                    continue; // 继续下一个
                }
                var tier = tiers[tierIndex]; // 获取当前层级数据
                _logger.LogInfo($"创建 {tier.rawName} 在锚点 {anchorRawName} 处"); // 输出创建信息

                // 创建新技能节点
                var newNode = graph.AddNode<PerkRelationNode>(); // 在图中添加新节点
                newNode.RawName = tier.rawName; // 设置原始名称
                newNode.gameObject.name = tier.rawName; // 设置游戏对象名称
                newNode.cachedPosition = anchorNode.cachedPosition + offsetFn(anchorNode); // 计算并设置缓存位置
                _logger.LogDebug($"节点位置: {newNode.cachedPosition}"); // 输出位置信息

                // 创建 Perk 组件
                var perk = newNode.gameObject.AddComponent<Perk>(); // 添加 Perk 组件
                perk.tree = tree; // 设置所属技能树
                perk.rawName = tier.rawName; // 设置原始名称
                perk.displayName = new LocalizedString { key = tier.displayNameKey }; // 设置显示名称（本地化）
                perk.levelRequirement = tier.level; // 设置等级要求
                perk.isSecret = false; // 设置为非秘密技能
                perk.requirements = new List<PerkRequirement>(); // 初始化需求列表

                // 添加金币需求
                if (tier.gold > 0) // 如果金币需求大于 0
                {
                    var goldReq = new PerkRequirement // 创建金币需求
                    {
                        type = RequirementType.Currency, // 类型为货币
                        currency = CurrencyType.Gold,    // 货币类型为金币
                        amount = tier.gold,              // 金额
                    };
                    perk.requirements.Add(goldReq); // 添加到需求列表
                }

                // 添加材料需求
                foreach (var materialId in tier.materials) // 遍历每个材料 ID
                {
                    var matReq = new PerkRequirement // 创建材料需求
                    {
                        type = RequirementType.Item, // 类型为物品
                        itemId = materialId,         // 物品 ID
                        amount = 1,                  // 数量为 1
                    };
                    perk.requirements.Add(matReq); // 添加到需求列表
                }

                // 添加增加仓库容量的行为
                var addStorage = newNode.gameObject.AddComponent<AddPlayerStorage>(); // 添加 AddPlayerStorage 组件
                addStorage.Amount = tier.capacity; // 设置增加容量

                injectedPerks.Add(perk); // 将技能添加到列表
                _logger.LogInfo($"已创建技能节点: {tier.rawName}"); // 输出创建成功信息
            }

            // 恢复之前解锁的状态（从独立备份文件）
            RestoreUnlockedFromDisk(tree, injectedPerks); // 调用恢复方法

            // 添加解锁状态监视器
            var watcher = tree.gameObject.AddComponent<ModUnlockWatcher>(); // 添加监视器组件
            watcher.Initialize(injectedPerks); // 初始化监视器

            // 触发技能树重新加载，以确保状态生效
            tree.Load(); // 加载技能树
            _logger.LogInfo("超级仓库技能节点注入完成！"); // 输出注入完成信息
        }

        // 辅助方法：设置私有字段的值
        private static void SetPrivate(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic); // 获取私有字段
            field?.SetValue(obj, value); // 设置字段值
        }

        // 获取当前存档槽位
        private static int? GetCurrentSlot()
        {
            var savesSystem = UnityEngine.Object.FindObjectOfType<SavesSystem>(); // 查找 SavesSystem 实例
            if (savesSystem == null) // 如果未找到
                return null; // 返回空
            var slotProp = typeof(SavesSystem).GetProperty("CurrentSlot", BindingFlags.Public | BindingFlags.Instance); // 获取 CurrentSlot 属性
            if (slotProp == null) // 如果属性不存在
                return null; // 返回空
            return (int)slotProp.GetValue(savesSystem); // 返回当前槽位值
        }

        // 从独立备份文件恢复解锁状态
        private void RestoreUnlockedFromDisk(PerkTree tree, List<Perk> modPerks)
        {
            var slot = GetCurrentSlot(); // 获取当前存档槽位
            if (!slot.HasValue) // 如果无槽位
            {
                _logger.LogWarning("无法确定当前存档槽位，跳过恢复解锁状态。"); // 输出警告
                return; // 返回
            }
            var backupPath = System.IO.Path.Combine(Application.persistentDataPath, "SuperStorageMod", $"backup_slot_{slot.Value}.txt"); // 构建备份文件路径
            if (!System.IO.File.Exists(backupPath)) // 如果备份文件不存在
            {
                _logger.LogInfo($"没有找到备份文件 {backupPath}，无需恢复。"); // 输出信息
                return; // 返回
            }
            try
            {
                var lines = System.IO.File.ReadAllLines(backupPath); // 读取备份文件所有行
                var unlockedNames = new HashSet<string>(lines.Where(l => !string.IsNullOrWhiteSpace(l))); // 创建已解锁名称的哈希集合
                _logger.LogInfo($"从备份恢复 {unlockedNames.Count} 个解锁技能"); // 输出恢复数量信息
                foreach (var perk in modPerks) // 遍历每个 mod 技能
                {
                    if (unlockedNames.Contains(perk.rawName)) // 如果技能名称在已解锁集合中
                    {
                        perk.unlocked = true; // 设置为已解锁
                        perk.unlockingBeginTimeRaw = 0; // 设置解锁开始时间
                        _logger.LogDebug($"恢复解锁: {perk.rawName}"); // 输出恢复调试信息
                    }
                }
            }
            catch (Exception ex) // 捕获异常
            {
                _logger.LogError($"恢复解锁状态时出错: {ex}"); // 输出错误信息
            }
        }

        // 保存解锁状态到独立备份文件
        private void SaveUnlockedBackupToDisk(List<Perk> modPerks)
        {
            var slot = GetCurrentSlot(); // 获取当前存档槽位
            if (!slot.HasValue) // 如果无槽位
            {
                _logger.LogWarning("无法确定当前存档槽位，跳过备份解锁状态。"); // 输出警告
                return; // 返回
            }
            var dir = System.IO.Path.Combine(Application.persistentDataPath, "SuperStorageMod"); // 构建备份目录路径
            System.IO.Directory.CreateDirectory(dir); // 创建目录（如果不存在）
            var backupPath = System.IO.Path.Combine(dir, $"backup_slot_{slot.Value}.txt"); // 构建备份文件路径
            try
            {
                var unlockedNames = modPerks.Where(p => p.unlocked).Select(p => p.rawName).ToArray(); // 获取已解锁的技能名称数组
                System.IO.File.WriteAllLines(backupPath, unlockedNames); // 将所有已解锁名称写入文件
                _logger.LogInfo($"已备份 {unlockedNames.Length} 个解锁技能到 {backupPath}"); // 输出备份成功信息
            }
            catch (Exception ex) // 捕获异常
            {
                _logger.LogError($"备份解锁状态时出错: {ex}"); // 输出错误信息
            }
        }

        // 解锁状态监视器组件
        private class ModUnlockWatcher : MonoBehaviour
        {
            [AllowNull] private List<Perk> _trackedPerks; // 声明跟踪的技能列表（允许为空）
            private bool _dirty = false; // 脏标志，指示是否有变化

            public void Initialize(List<Perk> perks) // 初始化方法
            {
                _trackedPerks = perks; // 设置跟踪的技能列表
                foreach (var perk in _trackedPerks) // 遍历每个技能
                {
                    // 监听解锁状态变化事件
                    perk.OnUnlockStateChanged += OnPerkUnlockStateChanged;
                }
                // 监听存档设置文件事件，以便在存档时备份
                var savesSystem = FindObjectOfType<SavesSystem>(); // 查找 SavesSystem
                if (savesSystem != null) // 如果找到
                {
                    savesSystem.OnSetFile += OnSetFile; // 订阅 OnSetFile 事件
                }
            }

            private void OnPerkUnlockStateChanged(Perk perk) // 解锁状态变化事件处理
            {
                _dirty = true; // 设置脏标志
                _logger?.LogDebug($"技能 {perk.rawName} 解锁状态变化，脏标志已设置。"); // 输出调试信息
            }

            private void OnSetFile() // 存档设置文件事件处理
            {
                if (_dirty && _trackedPerks != null) // 如果脏标志为真且跟踪列表不为空
                {
                    var mod = FindObjectOfType<ModBehaviour>(); // 查找 ModBehaviour 实例
                    mod?.SaveUnlockedBackupToDisk(_trackedPerks); // 调用备份方法
                    _dirty = false; // 重置脏标志
                    _logger?.LogInfo("存档时已备份解锁状态。"); // 输出信息
                }
            }

            private void OnDestroy() // 组件销毁时调用
            {
                if (_trackedPerks != null) // 如果跟踪列表不为空
                {
                    foreach (var perk in _trackedPerks) // 遍历每个技能
                    {
                        perk.OnUnlockStateChanged -= OnPerkUnlockStateChanged; // 取消订阅事件
                    }
                }
                var savesSystem = FindObjectOfType<SavesSystem>(); // 查找 SavesSystem
                if (savesSystem != null) // 如果找到
                {
                    savesSystem.OnSetFile -= OnSetFile; // 取消订阅事件
                }
            }

            private ManualLogSource _logger => BepInEx.Logging.Logger.CreateLogSource("ModUnlockWatcher"); // 获取日志源
        }
    }
}