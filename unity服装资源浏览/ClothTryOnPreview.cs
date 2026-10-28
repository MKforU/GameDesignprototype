using System;
using System.Collections.Generic;
using System.Linq;
using Config;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameEditor
{
    /// <summary>
    /// 编辑器内「男女双角色同屏」3D 试穿预览。
    /// 数据链路对齐游戏 BaseRoleCloth：骨架取 t_default_fashion 的模型，
    /// 服装 prefab 取 BuildAssets/Clothes/Prefab/{f_Model}，按 FBSkinnedInfo.BoneNames 绑到骨架。
    /// 渲染复用 AnimPreviewWindow 的 PreviewRenderUtility 方案（本工程 URP 下已验证可用）。
    /// </summary>
    public class ClothTryOnPreview : IDisposable
    {
        const string MaleRigPath = "Assets/<你的工程>/Asset/NewRoleAsset/Role/Cloth_H/b_cloth_1000000/b_cloth_1000000.prefab";
        const string FemaleRigPath = "Assets/<你的工程>/Asset/NewRoleAsset/Role/Cloth_H/g_cloth_5000000/g_cloth_5000000.prefab";
        const string MaleIdlePath = "Assets/<你的工程>/Asset/BuildAssets/Animation/male/daiji.anim";
        const string FemaleIdlePath = "Assets/<你的工程>/Asset/BuildAssets/Animation/female/daiji.anim";
        const string ClothPrefabFolder = "Assets/<你的工程>/Asset/BuildAssets/Clothes/Prefab";
        const string ClothLowPrefabFolder = "Assets/<你的工程>/Asset/BuildAssets/Clothes/LowPrefab";
        const string ModelEquipFolder = "Assets/<你的工程>/Asset/BuildAssets/Model/Equip";
        const string EffectClothFolder = "Assets/<你的工程>/Asset/BuildAssets/Effect/Cloth";

        const string CamControlName = "ClothTryOnCam";

        /// <summary>互斥部位组（与 BaseRoleCloth.ChangeCostumeData 的覆盖关系对应）。</summary>
        static readonly int[][] ConflictGroups =
        {
            new[] { 0, 5, 6 },        // 发型 ←→ 上全身 ←→ 整全身
            new[] { 1, 4, 5, 6 },     // 上衣 ←→ 连身 ←→ 上全身 ←→ 整全身
            new[] { 2, 4, 6 },        // 下衣 ←→ 连身 ←→ 整全身
            new[] { 3, 6 },           // 鞋子 ←→ 整全身
            new[] { 18, 6 },          // 手套 ←→ 整全身
        };

        /// <summary>不适合在试穿模型上展示的部位（坐骑等）。</summary>
        static readonly int[] SkipTypes = { 25 };

        /// <summary>一件已穿服装：实例 + 运行时创建的动态骨骼（销毁时一起清）。</summary>
        public class WornPart
        {
            public readonly List<GameObject> Objects = new List<GameObject>();
            public readonly List<GameObject> DynRoots = new List<GameObject>();
            public readonly List<GameObject> Effects = new List<GameObject>();
            public readonly List<ParticleSystem> Particles = new List<ParticleSystem>();
        }

        public class Slot
        {
            public bool IsMale;
            public GameObject Rig;
            public readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            public readonly Dictionary<uint, WornPart> Worn = new Dictionary<uint, WornPart>();
            public readonly HashSet<uint> DefaultIds = new HashSet<uint>();
            public readonly List<SkinnedMeshRenderer> BaseRenderers = new List<SkinnedMeshRenderer>();
            public Animation AnimHost;
            public AnimationClip IdleClip;
            public string IdleState;
            public string GroupLabel;
            public readonly List<string> Recent = new List<string>();
            public readonly List<string> Warnings = new List<string>();

            public string Summary => !string.IsNullOrEmpty(GroupLabel)
                ? GroupLabel
                : Recent.Count == 0
                    ? "默认装扮"
                    : string.Join(" · ", Recent.Take(3)) + (Recent.Count > 3 ? "…" : "");
        }

        PreviewRenderUtility m_Preview;
        readonly Slot m_Male = new Slot { IsMale = true };
        readonly Slot m_Female = new Slot { IsMale = false };
        bool m_Built;
        readonly Dictionary<string, GameObject> m_PrefabCache = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        // 相机
        float m_Yaw = 180f;
        float m_Pitch = -4f;
        float m_Dist = 3.2f;
        Vector3 m_Pivot = new Vector3(0f, 1f, 0f);

        // 动画
        // 漫游（右键+WASD，对齐 Scene 视图）
        bool m_Flying;
        Vector3 m_FlyPos;
        bool m_KeyW, m_KeyA, m_KeyS, m_KeyD, m_KeyQ, m_KeyE, m_KeyShift;

        float m_Time;
        bool m_Playing;
        float m_Speed = 1f;
        bool m_EffectsOn = true;
        float m_EffectsTime;

        public bool Playing => m_Playing;
        public bool EffectsOn => m_EffectsOn;
        public bool Flying => m_Flying;

        /// <summary>诊断：已创建的特效对象数/粒子系统数/累计驱动时间。</summary>
        public string EffectStats
        {
            get
            {
                var objs = 0;
                var systems = 0;
                foreach (var slot in new[] { m_Male, m_Female })
                {
                    foreach (var part in slot.Worn.Values)
                    {
                        objs += part.Effects.Count;
                        systems += part.Particles.Count;
                    }
                }

                return $"特效 物×{objs} 粒×{systems} t={m_EffectsTime:0.0}s";
            }
        }
        public float Speed { get => m_Speed; set => m_Speed = Mathf.Clamp(value, 0.1f, 3f); }
        public string MaleSummary => m_Male.Summary;
        public string FemaleSummary => m_Female.Summary;

        public string WarningText
        {
            get
            {
                var list = m_Male.Warnings.Concat(m_Female.Warnings).Distinct().ToList();
                if (list.Count == 0)
                    return null;
                return string.Join("；", list.Take(3)) + (list.Count > 3 ? $" 等{list.Count}件" : "");
            }
        }

        // ======================================================================
        // 生命周期
        // ======================================================================

        void Ensure()
        {
            if (m_Preview == null)
            {
                m_Preview = new PreviewRenderUtility();
                m_Preview.ambientColor = new Color(0.34f, 0.34f, 0.37f);
                var cam = m_Preview.camera;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.13f, 0.13f, 0.15f);
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                cam.fieldOfView = 32f;
                var lights = m_Preview.lights;
                if (lights != null && lights.Length > 0)
                {
                    lights[0].intensity = 1.15f;
                    if (lights.Length > 1) lights[1].intensity = 0.45f;
                }
            }

            if (!m_Built)
                BuildRigs();
        }

        public void Dispose()
        {
            if (m_Preview != null)
            {
                m_Preview.Cleanup();
                m_Preview = null;
            }

            m_Built = false;
            m_Male.Rig = null;
            m_Female.Rig = null;
            m_Male.Worn.Clear();
            m_Female.Worn.Clear();
            m_PrefabCache.Clear();
        }

        void BuildRigs()
        {
            m_Built = true;
            m_EffectsTime = 0f;
            m_Male.Warnings.Clear();
            m_Female.Warnings.Clear();
            BuildSlot(m_Male, MaleRigPath, MaleIdlePath, 0);
            BuildSlot(m_Female, FemaleRigPath, FemaleIdlePath, 1);
            Reframe();
        }

        void BuildSlot(Slot slot, string rigPath, string idlePath, uint gender)
        {
            slot.Rig = null;
            slot.Bones.Clear();
            slot.Worn.Clear();
            slot.GroupLabel = null;
            slot.Recent.Clear();
            slot.AnimHost = null;
            slot.IdleClip = null;
            slot.IdleState = null;

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(rigPath);
            if (rigPrefab == null)
            {
                slot.Warnings.Add($"角色模型缺失 {rigPath}");
                return;
            }

            slot.Rig = m_Preview.InstantiatePrefabInScene(rigPrefab);
            slot.Rig.name = (slot.IsMale ? "男_" : "女_") + rigPrefab.name;
            slot.Rig.transform.position = new Vector3(slot.IsMale ? -0.62f : 0.62f, 0f, 0f);

            foreach (var t in slot.Rig.GetComponentsInChildren<Transform>(true))
            {
                if (!slot.Bones.ContainsKey(t.name))
                    slot.Bones.Add(t.name, t);
            }

            slot.BaseRenderers.Clear();
            foreach (var smrBase in slot.Rig.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                slot.BaseRenderers.Add(smrBase);

            // 动画宿主挂在骨架根（Bip001 的直接父节点），曲线路径与游戏一致
            var bip = slot.Rig.transform.Find("Bip001");
            var hostGo = bip != null && bip.parent != null ? bip.parent.gameObject : slot.Rig;
            slot.AnimHost = hostGo.GetComponent<Animation>();
            if (slot.AnimHost == null)
                slot.AnimHost = hostGo.AddComponent<Animation>();
            slot.AnimHost.playAutomatically = false;

            // 默认装扮：t_default_fashion 中该性别的 衣/裤/鞋/发/脸/手
            slot.DefaultIds.Clear();
            foreach (var id in GetDefaultOutfitIds(gender))
            {
                slot.DefaultIds.Add(id);
                WearInternal(slot, id);
            }

            // 待机姿势：daiji 静态采样
            slot.IdleClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(idlePath);
            if (slot.IdleClip != null && slot.AnimHost != null)
            {
                slot.IdleState = slot.IdleClip.name;
                if (slot.AnimHost.GetClip(slot.IdleState) == null)
                    slot.AnimHost.AddClip(slot.IdleClip, slot.IdleState);
                slot.AnimHost.Play(slot.IdleState);
                var st = slot.AnimHost[slot.IdleState];
                if (st != null)
                {
                    st.speed = 0f;
                    st.wrapMode = WrapMode.Loop;
                    st.time = 0f;
                }

                slot.AnimHost.Sample();
            }
        }

        static IEnumerable<uint> GetDefaultOutfitIds(uint gender)
        {
            var arr = Config_t_default_fashion.Singleton.Array;
            if (arr == null)
                yield break;
            var row = arr.FirstOrDefault(x => x.f_Gender == gender);
            if (row == null)
                yield break;
            foreach (var id in new[] { row.f_Clothes, row.f_Trousers, row.f_Shoes, row.f_Hair, row.f_face, row.f_Hands })
            {
                if (id != 0)
                    yield return id;
            }
        }

        // ======================================================================
        // 穿脱
        // ======================================================================

        /// <summary>单件穿上（按性别路由，通用件男女都穿）。</summary>
        public bool PutOnItem(uint itemId)
        {
            Ensure();
            m_Male.Warnings.Clear();
            m_Female.Warnings.Clear();
            var item = Config_t_item.Singleton.GetConfig(itemId);
            if (item == null)
                return false;

            var ok = false;
            if (CanWearOn(item, true) && WearInternal(m_Male, itemId))
            {
                AddRecent(m_Male, item);
                ok = true;
            }

            if (CanWearOn(item, false) && WearInternal(m_Female, itemId))
            {
                AddRecent(m_Female, item);
                ok = true;
            }

            return ok;
        }

        /// <summary>整套穿上：先恢复默认装扮，再逐件穿。</summary>
        public void PutOnSuite(string groupName, IEnumerable<uint> itemIds)
        {
            Ensure();
            m_Male.Warnings.Clear();
            m_Female.Warnings.Clear();
            ClearNonDefault(m_Male);
            ClearNonDefault(m_Female);
            var maleCount = 0;
            var femaleCount = 0;
            foreach (var id in itemIds)
            {
                var item = Config_t_item.Singleton.GetConfig(id);
                if (item == null)
                    continue;
                if (CanWearOn(item, true) && WearInternal(m_Male, id))
                    maleCount++;
                if (CanWearOn(item, false) && WearInternal(m_Female, id))
                    femaleCount++;
            }

            if (maleCount > 0)
            {
                m_Male.GroupLabel = $"{groupName}（男{maleCount}件）";
                m_Male.Recent.Clear();
            }

            if (femaleCount > 0)
            {
                m_Female.GroupLabel = $"{groupName}（女{femaleCount}件）";
                m_Female.Recent.Clear();
            }
        }

        public void ResetAll()
        {
            Ensure();
            ResetSlot(m_Male);
            ResetSlot(m_Female);
        }

        /// <summary>只脱掉非默认的搭配（默认装扮保留，省去整套重建的开销）。</summary>
        void ClearNonDefault(Slot slot)
        {
            foreach (var id in slot.Worn.Keys.ToList())
            {
                if (slot.DefaultIds.Contains(id))
                    continue;
                DestroyWorn(slot, id);
            }

            slot.GroupLabel = null;
            slot.Recent.Clear();
            RefreshBaseParts(slot);
        }

        void ResetSlot(Slot slot)
        {
            ClearNonDefault(slot);
            // 默认装扮若被之前的搭配顶掉，补回来
            foreach (var id in slot.DefaultIds)
            {
                if (!slot.Worn.ContainsKey(id))
                    WearInternal(slot, id);
            }

            slot.GroupLabel = null;
            slot.Recent.Clear();
            RefreshBaseParts(slot);
        }

        static bool CanWearOn(Item_t_item item, bool male)
        {
            if (Array.IndexOf(SkipTypes, item.f_Type2) >= 0)
                return false;
            if (item.f_SexNeed == 0)
                return male;
            if (item.f_SexNeed == 1)
                return !male;
            return true; // 通用
        }

        static void AddRecent(Slot slot, Item_t_item item)
        {
            slot.GroupLabel = null;
            var name = string.IsNullOrEmpty(item.f_Name) ? item.f_ItemID.ToString() : item.f_Name;
            slot.Recent.Remove(name);
            slot.Recent.Insert(0, name);
            if (slot.Recent.Count > 4)
                slot.Recent.RemoveAt(slot.Recent.Count - 1);
        }

        /// <summary>是否互斥：同部位，或同处一个互斥组。</summary>
        static bool Conflicts(int a, int b)
        {
            if (a == b)
                return true;
            foreach (var group in ConflictGroups)
            {
                if (Array.IndexOf(group, a) >= 0 && Array.IndexOf(group, b) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>按基础模型网格名推断所属部位（-1=未知）。</summary>
        static int ClassifyPart(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return -1;
            var n = objectName.ToLowerInvariant();
            if (n.Contains("hair")) return 0;
            if (n.Contains("body")) return 1;
            if (n.Contains("leg")) return 2;
            if (n.Contains("foot") || n.Contains("shoes")) return 3;
            if (n.Contains("head") || n.Contains("face") || n.Contains("eyeball") || n.Contains("eyelash")) return 11;
            if (n.Contains("glove")) return 18;
            if (n.Contains("sock")) return 24;
            return -1;
        }

        /// <summary>被任一已穿服装覆盖的基础身体网格隐藏，其余显示（穿衣前后重算）。</summary>
        static void RefreshBaseParts(Slot slot)
        {
            foreach (var smr in slot.BaseRenderers)
            {
                if (smr == null)
                    continue;
                var basePart = ClassifyPart(smr.name);
                var hide = false;
                if (basePart >= 0)
                {
                    foreach (var kv in slot.Worn)
                    {
                        var w = Config_t_item.Singleton.GetConfig(kv.Key);
                        if (w != null && Conflicts((int)w.f_Type2, basePart))
                        {
                            hide = true;
                            break;
                        }
                    }
                }

                smr.enabled = !hide;
            }
        }

        bool WearInternal(Slot slot, uint itemId)
        {
            if (slot.Rig == null)
                return false;

            var item = Config_t_item.Singleton.GetConfig(itemId);
            var equip = Config_t_equip.Singleton.GetConfig(itemId);
            if (item == null || equip == null || string.IsNullOrEmpty(equip.f_Model))
                return false;

            var type = (int)item.f_Type2;

            // 互斥部位先卸下：含同部位（此前漏掉同部位导致旧衣服不脱，新衣叠在旧衣里）
            var toRemove = slot.Worn.Keys.Where(id =>
            {
                var w = Config_t_item.Singleton.GetConfig(id);
                return w != null && Conflicts(type, (int)w.f_Type2);
            }).ToList();
            foreach (var id in toRemove)
                DestroyWorn(slot, id);

            var dynRoots = new List<GameObject>();
            var effectOut = new List<GameObject>();
            var created = InstantiateParts(slot, item, equip.f_Model, dynRoots, effectOut, out var bindIssues);
            if (created.Count == 0)
            {
                DestroyDynRoots(slot, dynRoots);
                var tag = $"{item.f_Name}：{equip.f_Model}";
                if (!slot.Warnings.Contains(tag))
                    slot.Warnings.Add(tag);
                RefreshBaseParts(slot);
                return false;
            }

            var part = new WornPart();
            part.Objects.AddRange(created);
            part.DynRoots.AddRange(dynRoots);
            part.Effects.AddRange(effectOut);
            foreach (var fx in part.Effects)
                part.Particles.AddRange(fx.GetComponentsInChildren<ParticleSystem>(true));
            if (part.Effects.Count > 0)
                Debug.Log($"[服装试穿] {item.f_Name}：特效×{part.Effects.Count}，粒子系统×{part.Particles.Count}");
            slot.Worn[itemId] = part;
            if (bindIssues > 0)
            {
                var issueTag = $"{item.f_Name}：{bindIssues}块骨骼未绑定";
                if (!slot.Warnings.Contains(issueTag))
                    slot.Warnings.Add(issueTag);
            }

            RefreshBaseParts(slot);
            return true;
        }

        static void DestroyWorn(Slot slot, uint id)
        {
            if (!slot.Worn.TryGetValue(id, out var part) || part == null)
                return;

            DestroyDynRoots(slot, part.DynRoots);

            foreach (var fx in part.Effects)
            {
                if (fx != null)
                    Object.DestroyImmediate(fx);
            }

            foreach (var obj in part.Objects)
            {
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }

            slot.Worn.Remove(id);
        }

        /// <summary>
        /// 按部件实例化：带 FBSkinnedInfo 的服装走「动态骨骼+绑骨」（同 PlayerCloth）；
        /// 没有的（翅膀/耳环/肩饰等自带骨骼的饰品）按 t_america_equip_position 挂点挂上（同 PlayerEquip）。
        /// </summary>
        List<GameObject> InstantiateParts(Slot slot, Item_t_item item, string modelNames, List<GameObject> dynOut, List<GameObject> effectOut, out int bindIssues)
        {
            bindIssues = 0;
            var created = new List<GameObject>();
            var parts = modelNames.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                var name = parts[index].Trim();
                if (name.Length == 0)
                    continue;

                var prefab = LoadPrefabCached(ClothPrefabFolder, name)
                             ?? LoadPrefabCached(ClothLowPrefabFolder, name + "_low")
                             ?? LoadPrefabCached(ModelEquipFolder, name);
                if (prefab == null)
                {
                    var missTag = $"{item.f_Name}：{name} 资源缺失";
                    if (!slot.Warnings.Contains(missTag))
                        slot.Warnings.Add(missTag);
                    continue;
                }

                var go = Object.Instantiate(prefab, slot.Rig.transform, true);
                go.name = name;
                PrepareInstance(go);

                if (HasSkinnedBind(go))
                {
                    go.transform.localScale = Vector3.one;
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localEulerAngles = new Vector3(270f, 0f, 0f);
                    CreateDynamicBones(slot, go, dynOut);
                    bindIssues += BindBones(slot, go);
                }
                else
                {
                    AttachEquip(slot, go, item, index);
                }

                CreateEffects(slot, go, item, effectOut);
                created.Add(go);
            }

            return created;
        }

        /// <summary>prefab 加载缓存（未命中的路径也缓存，避免每次换装重复查盘）。</summary>
        GameObject LoadPrefabCached(string folder, string fileName)
        {
            var path = $"{folder}/{fileName}.prefab";
            if (m_PrefabCache.TryGetValue(path, out var cached))
                return cached;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            m_PrefabCache[path] = prefab;
            return prefab;
        }

        static bool HasSkinnedBind(GameObject go)
        {
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.GetComponent<FBSkinnedInfo>() != null)
                    return true;
            }

            return false;
        }

        /// <summary>饰品挂点：查 t_america_equip_position(f_EquipType=部位) 挂到骨骼，保留 prefab 自带骨骼动画。</summary>
        void AttachEquip(Slot slot, GameObject go, Item_t_item item, int partIndex)
        {
            var all = Config_t_america_equip_position.Singleton.Array;
            var type2 = (ushort)item.f_Type2;
            var row = all == null ? null : Array.Find(all, x => x.f_EquipType == type2);
            var boneStr = row != null ? (slot.IsMale ? row.f_MaleBone : row.f_FemleBone) : null;
            var posStr = row != null ? (slot.IsMale ? row.f_MalePosition : row.f_FemlePosition) : null;
            var rotStr = row != null ? (slot.IsMale ? row.f_MaleRotation : row.f_FemleRotation) : null;

            var boneNames = string.IsNullOrEmpty(boneStr) ? new string[0] : boneStr.Split('|');
            var pick = boneNames.Length > 0 ? boneNames[Mathf.Min(partIndex, boneNames.Length - 1)] : null;

            Transform parent = null;
            if (pick == "Foot")
            {
                // 游戏 FootTrans = 角色根部节点
                parent = slot.Rig.transform;
            }
            else if (!string.IsNullOrEmpty(pick))
            {
                slot.Bones.TryGetValue(pick, out parent);
            }

            if (parent == null)
            {
                parent = slot.Rig.transform;
                var warn = $"{item.f_Name}：挂点未配置({(pick ?? "空")})";
                if (!slot.Warnings.Contains(warn))
                    slot.Warnings.Add(warn);
            }

            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one;
            if (item.f_Type2 == 10 && !slot.IsMale)
                go.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f); // 女角色翅膀缩小（同 PlayerEquip）
            go.transform.localPosition = ParseVector(posStr);
            go.transform.localEulerAngles = ParseVector(rotStr);
        }

        static Vector3 ParseVector(string str)
        {
            if (string.IsNullOrEmpty(str))
                return Vector3.zero;
            var w = str.Split(',');
            float.TryParse(w.Length > 0 ? w[0] : "0", out var x);
            float.TryParse(w.Length > 1 ? w[1] : "0", out var y);
            float.TryParse(w.Length > 2 ? w[2] : "0", out var z);
            return new Vector3(x, y, z);
        }

        /// <summary>关掉编辑态跑不动的组件（布料模拟等），保留 FBSkinnedInfo / ClothConfigData。</summary>
        static void PrepareInstance(GameObject go)
        {
            foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null)
                    continue;
                if (b is FBSkinnedInfo || b is ClothConfigData)
                    continue;
                if (b is Animation || b is Animator)
                    continue;

                var type = b.GetType();
                var full = type.FullName ?? type.Name;
                if (full.IndexOf("Magica", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.Name.IndexOf("DynamicBone", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    b.enabled = false;
                    continue;
                }

                b.enabled = false;
            }
        }

        /// <summary>
        /// 从服装的 ClothConfigData.DynamicBoneInfos 创建运行时动态骨骼（同 PlayerClothBase.CreateDynamicBoneObjs）。
        /// 裤子/头发的 Bone_*_sb_* 蒙皮骨骼由它提供，缺了这些骨骼网格不会渲染。
        /// </summary>
        /// <summary>创建服装特效（同 PlayerClothBase.CreateEffect）：挂到对应骨骼，按配置偏移旋转。</summary>
        void CreateEffects(Slot slot, GameObject clothInstance, Item_t_item item, List<GameObject> effectOut)
        {
            var cfg = clothInstance.GetComponent<ClothConfigData>();
            if (cfg == null || cfg.Effects == null || cfg.Effects.Length == 0)
                return;

            var equipCfg = Config_t_equip.Singleton.GetConfig(item.f_ItemID);
            if (equipCfg != null && equipCfg.f_CanEffect == 0)
            {
                var gateTag = $"{item.f_Name}：有特效但 f_CanEffect=0（游戏内不显示）";
                if (!slot.Warnings.Contains(gateTag))
                    slot.Warnings.Add(gateTag);
            }

            foreach (var element in cfg.Effects)
            {
                if (element == null || string.IsNullOrEmpty(element.EffectName))
                    continue;

                var prefab = LoadPrefabCached(EffectClothFolder, element.EffectName);
                if (prefab == null)
                {
                    var missTag = $"{item.f_Name}：特效 {element.EffectName} 资源缺失";
                    if (!slot.Warnings.Contains(missTag))
                        slot.Warnings.Add(missTag);
                    continue;
                }

                var fx = Object.Instantiate(prefab);
                fx.name = element.EffectName;
                PrepareInstance(fx);

                Transform parent = null;
                if (element.RootName == "root" || element.RootName == "Bip002")
                {
                    // 游戏：root→角色根、Bip002→FootTrans（均为根级节点）
                    parent = slot.Rig.transform;
                }
                else if (!string.IsNullOrEmpty(element.RootName))
                {
                    slot.Bones.TryGetValue(element.RootName, out parent);
                }

                if (parent == null)
                {
                    parent = slot.Rig.transform;
                    var rootTag = $"{item.f_Name}：特效根节点缺失 {element.RootName}";
                    if (!slot.Warnings.Contains(rootTag))
                        slot.Warnings.Add(rootTag);
                }

                fx.transform.SetParent(parent, false);
                fx.transform.localScale = Vector3.one;
                fx.transform.localPosition = element.LocalOffset;
                fx.transform.localEulerAngles = element.LocalEuler;

                // 编辑态没有自动更新：关掉 playOnAwake、清干净初始状态，统一交给 TickEffects 手动 Simulate
                foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.playOnAwake = false;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Simulate(0f, true, true);
                }

                fx.SetActive(m_EffectsOn && IsItemFxEnabled(item.f_ItemID));
                effectOut.Add(fx);
            }
        }

        void CreateDynamicBones(Slot slot, GameObject clothInstance, List<GameObject> dynOut)
        {
            var cfg = clothInstance.GetComponent<ClothConfigData>();
            if (cfg == null || cfg.DynamicBoneInfos == null)
                return;

            foreach (var db in cfg.DynamicBoneInfos)
            {
                if (db == null || db.Bones == null || db.Bones.Count == 0 || string.IsNullOrEmpty(db.RootBone))
                    continue;

                // 与游戏一致：动态骨骼名不允许重复
                var dup = false;
                foreach (var b in db.Bones)
                {
                    if (b == null)
                        continue;
                    var count = 0;
                    foreach (var c in db.Bones)
                        if (c != null && c.BoneName == b.BoneName)
                            count++;
                    if (count > 1)
                    {
                        dup = true;
                        break;
                    }
                }

                if (dup)
                    continue;

                var rootInfo = db.Bones.Find(a => a != null && a.BoneName == db.RootBone);
                if (rootInfo == null)
                    continue;
                CreateDynamicBoneNode(slot, db, rootInfo, dynOut);
            }
        }

        void CreateDynamicBoneNode(Slot slot, DynamicBoneInfoLow db, BoneInfo info, List<GameObject> dynOut)
        {
            if (info.ChildList != null && info.ChildList.Contains(info.BoneName))
                return;

            var go = new GameObject(info.BoneName);
            dynOut.Add(go);
            slot.Bones[info.BoneName] = go.transform;

            Transform parent = null;
            if (!string.IsNullOrEmpty(info.RootName))
                slot.Bones.TryGetValue(info.RootName, out parent);
            go.transform.parent = parent;
            go.transform.localScale = Vector3.one;
            go.transform.localPosition = info.LocalPos;
            go.transform.localEulerAngles = info.LocalEuler;

            if (info.ChildList == null)
                return;
            foreach (var childName in info.ChildList)
            {
                var child = db.Bones.Find(b => b != null && b.BoneName == childName);
                if (child != null)
                    CreateDynamicBoneNode(slot, db, child, dynOut);
            }
        }

        static void DestroyDynRoots(Slot slot, List<GameObject> dynRoots)
        {
            if (dynRoots == null || dynRoots.Count == 0)
                return;
            foreach (var dyn in dynRoots)
            {
                if (dyn == null)
                    continue;
                foreach (var tr in dyn.GetComponentsInChildren<Transform>(true))
                {
                    if (slot.Bones.TryGetValue(tr.name, out var cur) && cur == tr)
                        slot.Bones.Remove(tr.name);
                }
            }

            foreach (var dyn in dynRoots)
            {
                if (dyn != null)
                    Object.DestroyImmediate(dyn);
            }

            dynRoots.Clear();
        }

        /// <summary>按 FBSkinnedInfo 把服装蒙皮绑到骨架（同 BaseRoleCloth.SetBones）；返回未绑定骨骼数。</summary>
        static int BindBones(Slot slot, GameObject instance)
        {
            var missing = 0;
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var info = smr.GetComponent<FBSkinnedInfo>();
                if (info == null || info.BoneNames == null)
                {
                    missing++;
                    continue;
                }

                var bones = new List<Transform>(info.BoneNames.Length);
                foreach (var boneName in info.BoneNames)
                {
                    if (slot.Bones.TryGetValue(boneName, out var bone) && bone != null)
                        bones.Add(bone);
                    else
                        missing++;
                }

                if (bones.Count > 0)
                    smr.bones = bones.ToArray();
                if (!string.IsNullOrEmpty(info.RootBone) && slot.Bones.TryGetValue(info.RootBone, out var root))
                    smr.rootBone = root;
            }

            if (missing > 0)
                Debug.LogWarning($"[服装试穿] {instance.name}：{missing} 处骨骼未绑定");
            return missing;
        }

        // ======================================================================
        // 动画驱动
        // ======================================================================

        /// <summary>每帧推进待机动画；返回是否需要重绘。</summary>
        public bool Tick(float dt)
        {
            if (!m_Playing)
                return false;

            var len = ClipLength();
            if (len <= 0f)
                return false;

            m_Time += dt * m_Speed;
            if (m_Time >= len)
                m_Time %= len;

            ApplySample();
            return true;
        }

        public void SetPlaying(bool playing)
        {
            m_Playing = playing;
        }

        public void ResetTime()
        {
            m_Time = 0f;
            ApplySample();
        }

        /// <summary>漫游移动：WASD 平移、Q/E 降/升、Shift 加速（仅右键按住期间生效）。</summary>
        public bool TickFlyInput(float dt)
        {
            if (!m_Flying)
                return false;

            var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            var speed = (m_Dist * 1.0f + 0.5f) * (m_KeyShift ? 4f : 1f) * Mathf.Min(dt, 0.1f);
            var move = Vector3.zero;
            if (m_KeyW) move += rot * Vector3.forward;
            if (m_KeyS) move -= rot * Vector3.forward;
            if (m_KeyD) move += rot * Vector3.right;
            if (m_KeyA) move -= rot * Vector3.right;
            if (m_KeyE) move += Vector3.up;
            if (m_KeyQ) move -= Vector3.up;
            if (move.sqrMagnitude < 1e-6f)
                return false;

            m_FlyPos += move.normalized * speed;
            return true;
        }

        void ClearFlyKeys()
        {
            m_KeyW = m_KeyA = m_KeyS = m_KeyD = m_KeyQ = m_KeyE = m_KeyShift = false;
        }

        float ClipLength()
        {
            if (m_Male.IdleClip != null)
                return m_Male.IdleClip.length;
            if (m_Female.IdleClip != null)
                return m_Female.IdleClip.length;
            return 0f;
        }

        void ApplySample()
        {
            SampleSlot(m_Male, m_Time);
            SampleSlot(m_Female, m_Time);
        }

        static void SampleSlot(Slot slot, float time)
        {
            if (slot.AnimHost == null || slot.IdleState == null)
                return;
            var st = slot.AnimHost[slot.IdleState];
            if (st == null)
                return;
            st.time = time;
            slot.AnimHost.Sample();
        }

        // ======================================================================
        // 特效驱动
        // ======================================================================

        /// <summary>特效总开关：关闭=隐藏已创建的特效对象。</summary>
        /// <summary>按件特效开关（itemId → false=该件特效关闭）；默认跟随全局开关。</summary>
        readonly Dictionary<uint, bool> m_ItemFxEnabled = new Dictionary<uint, bool>();

        public bool IsItemFxEnabled(uint id) => !m_ItemFxEnabled.TryGetValue(id, out var v) || v;

        /// <summary>
        /// 按件/按套开关特效。ids 传关联道具（男女同编号套装的成员一起关/开）；
        /// 打开时重播该件粒子——隐藏过的粒子若不重播，重新显示后会停在旧状态（看着像没有特效）。
        /// </summary>
        public void SetItemFxEnabled(IEnumerable<uint> ids, bool enabled)
        {
            var idSet = new HashSet<uint>(ids);
            foreach (var id in idSet)
                m_ItemFxEnabled[id] = enabled;

            foreach (var slot in new[] { m_Male, m_Female })
            {
                foreach (var kv in slot.Worn)
                {
                    if (!idSet.Contains(kv.Key))
                        continue;
                    var visible = m_EffectsOn && enabled;
                    foreach (var fx in kv.Value.Effects)
                    {
                        if (fx != null)
                            fx.SetActive(visible);
                    }

                    if (visible)
                        RestartParticles(kv.Value);
                }
            }
        }

        /// <summary>该道具的服装 prefab 是否配置了特效（供界面判断要不要显示开关）。</summary>
        public bool ItemHasEffects(uint id)
        {
            var equip = Config_t_equip.Singleton.GetConfig(id);
            if (equip == null || string.IsNullOrEmpty(equip.f_Model))
                return false;
            foreach (var raw in equip.f_Model.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = raw.Trim();
                if (name.Length == 0)
                    continue;
                var prefab = LoadPrefabCached(ClothPrefabFolder, name)
                             ?? LoadPrefabCached(ClothLowPrefabFolder, name + "_low")
                             ?? LoadPrefabCached(ModelEquipFolder, name);
                if (prefab == null)
                    continue;
                var cfg = prefab.GetComponent<ClothConfigData>();
                if (cfg != null && cfg.Effects != null && cfg.Effects.Length > 0)
                    return true;
            }

            return false;
        }

        public void SetEffectsOn(bool on)
        {
            if (m_EffectsOn == on)
                return;
            m_EffectsOn = on;
            foreach (var slot in new[] { m_Male, m_Female })
            {
                foreach (var kv in slot.Worn)
                {
                    var visible = on && IsItemFxEnabled(kv.Key);
                    foreach (var fx in kv.Value.Effects)
                    {
                        if (fx != null)
                            fx.SetActive(visible);
                    }
                }
            }

            if (on)
                RestartEffects();
        }

        /// <summary>重播一件已穿服装的全部粒子（清空后从 0 重新模拟）。</summary>
        static void RestartParticles(WornPart part)
        {
            foreach (var ps in part.Particles)
            {
                if (ps == null)
                    continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Simulate(0f, true, true);
            }
        }

        void RestartEffects()
        {
            foreach (var slot in new[] { m_Male, m_Female })
            {
                foreach (var part in slot.Worn.Values)
                    RestartParticles(part);
            }
        }

        /// <summary>推进所有已穿服装的粒子特效（编辑态用 Simulate 手动驱动）；返回是否需要重绘。</summary>
        public bool TickEffects(float dt)
        {
            if (!m_EffectsOn)
                return false;
            if (dt > 0.1f)
                dt = 0.1f;
            m_EffectsTime += dt;

            var needRepaint = false;
            foreach (var slot in new[] { m_Male, m_Female })
            {
                foreach (var part in slot.Worn.Values)
                {
                    foreach (var ps in part.Particles)
                    {
                        if (ps == null || !ps.gameObject.activeInHierarchy)
                            continue;
                        ps.Simulate(dt, true, false);
                        needRepaint = true;
                    }
                }
            }

            return needRepaint;
        }

        // ======================================================================
        // 绘制
        // ======================================================================

        /// <summary>由窗口注入的重绘回调（拖拽/缩放时触发）。</summary>
        public Action OnRepaint;

        void RepaintHost() => OnRepaint?.Invoke();

        public void Draw(Rect rect, string titleLabel)
        {
            Ensure();
            HandleCamInput(rect);

            if (Event.current.type != EventType.Repaint)
                return;

            m_Preview.BeginPreview(rect, GUIStyle.none);
            UpdateCamera();
            m_Preview.Render(true);
            m_Preview.EndAndDrawPreview(rect);
            DrawOverlays(rect, titleLabel);
        }

        public void Reframe()
        {
            Ensure();
            var has = false;
            var bounds = default(Bounds);
            foreach (var slot in new[] { m_Male, m_Female })
            {
                if (slot.Rig == null)
                    continue;
                foreach (var r in slot.Rig.GetComponentsInChildren<Renderer>())
                {
                    if (!has)
                    {
                        bounds = r.bounds;
                        has = true;
                    }
                    else
                    {
                        bounds.Encapsulate(r.bounds);
                    }
                }
            }

            if (!has)
            {
                m_Pivot = new Vector3(0f, 1f, 0f);
                m_Dist = 3.2f;
                return;
            }

            m_Pivot = bounds.center;
            var radius = Mathf.Max(bounds.extents.magnitude, 0.4f);
            var fov = m_Preview != null ? m_Preview.camera.fieldOfView : 32f;
            m_Dist = radius / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.3f;
        }

        /// <summary>相机位置：漫游中用飞行点，否则绕 pivot 环绕。</summary>
        Vector3 GetCamPos()
        {
            if (m_Flying)
                return m_FlyPos;
            var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            return m_Pivot + rot * new Vector3(0f, 0f, -m_Dist);
        }

        void UpdateCamera()
        {
            var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            var cam = m_Preview.camera;
            cam.transform.position = GetCamPos();
            cam.transform.rotation = rot;
        }

        void DrawOverlays(Rect rect, string titleLabel)
        {
            var y = rect.y + 6f;
            if (!string.IsNullOrEmpty(titleLabel))
            {
                GUI.Label(new Rect(rect.x + 8f, y, rect.width - 16f, 18f), titleLabel, EditorStyles.whiteMiniLabel);
                y += 16f;
            }

            GUI.Label(new Rect(rect.x + 8f, y, rect.width * 0.5f, 16f), "男：" + m_Male.Summary,
                EditorStyles.whiteMiniLabel);
            GUI.Label(new Rect(rect.x + rect.width * 0.5f, y, rect.width * 0.5f - 8f, 16f), "女：" + m_Female.Summary,
                EditorStyles.whiteMiniLabel);

            DrawSlotTag(rect, m_Male, "男");
            DrawSlotTag(rect, m_Female, "女");

            if (m_EffectsOn)
                GUI.Label(new Rect(rect.x + 8f, rect.yMax - 54f, rect.width - 16f, 16f), EffectStats, EditorStyles.whiteMiniLabel);

            var warn = WarningText;
            if (!string.IsNullOrEmpty(warn))
                GUI.Label(new Rect(rect.x + 8f, rect.yMax - 36f, rect.width - 16f, 16f), "提示：" + warn,
                    EditorStyles.whiteMiniLabel);

            GUI.Label(new Rect(rect.x + 8f, rect.yMax - 20f, rect.width - 16f, 16f),
                "右键按住+WASD 漫游（QE 升降 Shift 加速）· Alt+左键 环绕 · 中键 平移 · 滚轮 缩放 · F 重置视角", EditorStyles.whiteMiniLabel);
        }

        void DrawSlotTag(Rect rect, Slot slot, string label)
        {
            if (slot.Rig == null)
                return;
            var has = false;
            var bounds = default(Bounds);
            foreach (var r in slot.Rig.GetComponentsInChildren<Renderer>())
            {
                if (!has)
                {
                    bounds = r.bounds;
                    has = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (!has)
                return;

            var cam = m_Preview.camera;
            var camW = cam.pixelWidth;
            var camH = cam.pixelHeight;
            if (camW <= 0f || camH <= 0f)
                return;

            var sp = cam.WorldToScreenPoint(new Vector3(bounds.center.x, bounds.max.y + 0.05f, bounds.center.z));
            if (sp.z <= 0f)
                return;

            var gx = rect.x + sp.x / camW * rect.width;
            var gy = rect.y + rect.height - sp.y / camH * rect.height;
            var content = new GUIContent(label);
            var w = EditorStyles.whiteMiniLabel.CalcSize(content).x;
            GUI.Label(new Rect(gx - w * 0.5f, gy - 16f, w + 6f, 16f), content, EditorStyles.whiteMiniLabel);
        }

        void HandleCamInput(Rect rect)
        {
            var e = Event.current;
            var id = GUIUtility.GetControlID(CamControlName.GetHashCode(), FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.F && rect.Contains(e.mousePosition))
                    {
                        Reframe();
                        e.Use();
                        RepaintHost();
                        break;
                    }
                    if (m_Flying)
                    {
                        switch (e.keyCode)
                        {
                            case KeyCode.W: m_KeyW = true; break;
                            case KeyCode.A: m_KeyA = true; break;
                            case KeyCode.S: m_KeyS = true; break;
                            case KeyCode.D: m_KeyD = true; break;
                            case KeyCode.E: m_KeyE = true; break;
                            case KeyCode.Q: m_KeyQ = true; break;
                            case KeyCode.LeftShift:
                            case KeyCode.RightShift: m_KeyShift = true; break;
                        }
                    }
                    break;
                case EventType.KeyUp:
                    if (m_Flying)
                    {
                        switch (e.keyCode)
                        {
                            case KeyCode.W: m_KeyW = false; break;
                            case KeyCode.A: m_KeyA = false; break;
                            case KeyCode.S: m_KeyS = false; break;
                            case KeyCode.D: m_KeyD = false; break;
                            case KeyCode.E: m_KeyE = false; break;
                            case KeyCode.Q: m_KeyQ = false; break;
                            case KeyCode.LeftShift:
                            case KeyCode.RightShift: m_KeyShift = false; break;
                        }
                    }
                    break;
                case EventType.MouseDown:
                    // 对齐 Scene：右键=漫游(WASD) · Alt+左键=环绕 · 中键=平移 · Alt+右键=推拉
                    if (rect.Contains(e.mousePosition) &&
                        ((e.button == 1 && !e.alt) || (e.alt && e.button == 0) || e.button == 2 || (e.alt && e.button == 1)))
                    {
                        if (e.button == 1 && !e.alt)
                        {
                            // 先取当前位置再进入漫游（顺序反了 GetCamPos 会返回未初始化的 FlyPos）
                            m_FlyPos = GetCamPos();
                            m_Flying = true;
                            ClearFlyKeys();
                        }

                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id)
                        break;
                    if (e.button == 2)
                    {
                        // 中键平移（内容跟随鼠标）
                        var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
                        var panScale = m_Dist * 0.0035f;
                        m_Pivot -= (rot * Vector3.right * e.delta.x + rot * Vector3.up * e.delta.y) * panScale;
                    }
                    else if (e.button == 1)
                    {
                        if (e.alt)
                        {
                            m_Dist = Mathf.Clamp(m_Dist * (1f + e.delta.y * 0.04f), 0.5f, 60f);
                        }
                        else
                        {
                            // 右键漫游视角（Event.delta.y GUI坐标向下为正：鼠标上移=抬头）
                            m_Yaw += e.delta.x * 0.25f;
                            m_Pitch = Mathf.Clamp(m_Pitch + e.delta.y * 0.25f, -89f, 89f);
                        }
                    }
                    else
                    {
                        // Alt+左键 环绕
                        m_Yaw += e.delta.x * 0.3f;
                        m_Pitch = Mathf.Clamp(m_Pitch - e.delta.y * 0.3f, -89f, 89f);
                    }
                    e.Use();
                    RepaintHost();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        if (m_Flying)
                        {
                            // 退出漫游：把环绕 pivot 放到视线前方 dist 处，继续环绕不跳视角
                            var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
                            m_Pivot = m_FlyPos + rot * new Vector3(0f, 0f, m_Dist);
                            m_Flying = false;
                            ClearFlyKeys();
                        }

                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.ScrollWheel:
                    if (rect.Contains(e.mousePosition))
                    {
                        m_Dist = Mathf.Clamp(m_Dist * (1f + e.delta.y * 0.08f), 0.5f, 60f);
                        e.Use();
                        RepaintHost();
                    }
                    break;
            }
        }
    }
}
