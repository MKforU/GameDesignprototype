using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public class AnimPreviewWindow : EditorWindow
{
    // 路径已做模糊处理（占位示例），使用时改成自己工程的实际路径
    const string kMaleFolder = "Assets/<你的工程>/Animation/male";
    const string kFemaleFolder = "Assets/<你的工程>/Animation/female";
    const string kBoyPath = "Assets/<你的工程>/Prefab/boy.prefab";
    const string kGirlPath = "Assets/<你的工程>/Prefab/girl.prefab";
    const float kRowHeight = 20f;

    // 10 个分类（下标0 起）；分类规则见 Classify()，中文名来自 t_animation_card.txt
    static readonly string[] kCategories =
    {
        "舞蹈", "双人互动", "待机基础", "家具场景", "骑乘",
        "换装展示", "表情情绪", "比赛结算", "乐器演出", "其他玩法"
    };

    sealed class AnimEntry
    {
        public string Name;
        public string MalePath;
        public string FemalePath;
        public string Category;
        public string CnName;
    }

    sealed class ModelSlot
    {
        public GameObject Instance;
        public readonly List<Animation> Anims = new List<Animation>();
        public readonly Dictionary<Animation, string> AddedStates = new Dictionary<Animation, string>();
        public AnimationClip Clip;
        public AnimationClip DefaultClip;
        public string StateName;
        public float Time;
        // 曲线绑定诊断：TotalCurves=clip 曲线总数，BoundCurves=能在当前骨架上解析到路径的数量。
        // Unity 对找不到路径的曲线是静默丢弃的，这里显式数出来，避免误判成预览器不播放。
        public int TotalCurves;
        public int BoundCurves;

        public float Length => Clip != null ? Clip.length : 0f;
    }

    [SerializeField] GameObject m_BoyPrefab;
    [SerializeField] GameObject m_GirlPrefab;
    [SerializeField] bool m_ShowP1 = true;
    [SerializeField] bool m_ShowP2 = true;
    [SerializeField] bool m_Loop = true;
    [SerializeField] float m_Speed = 1f;
    [SerializeField] bool m_Playing;
    [SerializeField] string m_Search = "";
    [SerializeField] int m_GenderFilter;
    [SerializeField] int m_CatFilter; // 0=全部，1..10 = kCategories[i-1]
    [SerializeField] string m_SelectedName;
    string[] m_CatLabels;
    [SerializeField] float m_Yaw = 180f;
    [SerializeField] float m_Pitch = -5f;
    [SerializeField] float m_Dist = 3f;
    [SerializeField] Vector3 m_Pivot = new Vector3(0f, 1f, 0f);

    // 外部动作清单（CSV 导入）：用于「仅查看这批动作」
    [SerializeField] List<string> m_MarkNames = new List<string>();
    [SerializeField] string m_MarkPath = "";
    [SerializeField] bool m_MarkOnly;
    HashSet<string> m_MarkSet; // 由 m_MarkNames 构建（忽略大小写）
    int m_MarkHit; // 清单中能在当前工程找到的数量

    PreviewRenderUtility m_Preview;
    ModelSlot m_P1 = new ModelSlot();
    ModelSlot m_P2 = new ModelSlot();
    readonly List<AnimEntry> m_All = new List<AnimEntry>();
    readonly List<AnimEntry> m_Filtered = new List<AnimEntry>();
    // 动作卡中文名：f_animation → f_name（来自 Assets/<你的工程>/Config/Tbl/t_animation_card.txt）
    readonly Dictionary<string, string> m_CnNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    AnimEntry m_Selected;
    Vector2 m_ListScroll;
    double m_LastTick;
    int m_MaleCount;
    int m_FemaleCount;
    GUIStyle m_NameStyle;
    GUIStyle m_YesStyle;
    GUIStyle m_NoStyle;
    GUIStyle m_StatusStyle;
    GUIStyle m_WarnStyle;

    [MenuItem("Tools/动作预览器")]
    static void Open()
    {
        GetWindow<AnimPreviewWindow>("动作预览器");
    }

    void OnEnable()
    {
        EditorApplication.update += OnEdUpdate;
        if (m_BoyPrefab == null) m_BoyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(kBoyPath);
        if (m_GirlPrefab == null) m_GirlPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(kGirlPath);
        EnsurePreview();
        ScanAnimations();
        if (!string.IsNullOrEmpty(m_SelectedName))
        {
            m_Selected = m_All.Find(x => x.Name == m_SelectedName);
            if (m_Selected == null) m_SelectedName = null;
        }

        BuildModels();
        ApplySelection(false);
        m_LastTick = EditorApplication.timeSinceStartup;
        Repaint();
    }

    void OnDisable()
    {
        EditorApplication.update -= OnEdUpdate;
        if (m_Preview != null)
        {
            m_Preview.Cleanup();
            m_Preview = null;
        }

        m_P1 = new ModelSlot();
        m_P2 = new ModelSlot();
    }

    void EnsurePreview()
    {
        if (m_Preview != null) return;
        m_Preview = new PreviewRenderUtility();
        m_Preview.ambientColor = new Color(0.32f, 0.32f, 0.35f);
        Camera cam = m_Preview.camera;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.13f, 0.13f, 0.15f);
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 100f;
        cam.fieldOfView = 32f;
        Light[] lights = m_Preview.lights;
        if (lights != null && lights.Length > 0)
        {
            lights[0].intensity = 1.15f;
            if (lights.Length > 1) lights[1].intensity = 0.45f;
        }
    }

    #region 动作列表

    void ScanAnimations()
    {
        m_All.Clear();
        m_MaleCount = 0;
        m_FemaleCount = 0;
        Dictionary<string, AnimEntry> byName = new Dictionary<string, AnimEntry>(StringComparer.OrdinalIgnoreCase);
        ScanFolder(kMaleFolder, true, byName);
        ScanFolder(kFemaleFolder, false, byName);
        m_All.AddRange(byName.Values);
        m_All.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        LoadCnNames();
        foreach (AnimEntry e in m_All)
        {
            e.Category = Classify(e.Name);
            e.CnName = LookupCn(e.Name);
        }

        RebuildMarkSet();
        RebuildFilter();
    }

    void ScanFolder(string assetFolder, bool male, Dictionary<string, AnimEntry> byName)
    {
        string full = Path.Combine(Application.dataPath, assetFolder.Substring("Assets/".Length));
        if (!Directory.Exists(full)) return;

        foreach (string file in Directory.GetFiles(full, "*.anim", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (IsExcludedAnim(name)) continue;
            if (!byName.TryGetValue(name, out AnimEntry entry))
            {
                entry = new AnimEntry { Name = name };
                byName[name] = entry;
            }

            string rel = file.Substring(full.Length).TrimStart('/', '\\').Replace('\\', '/');
            string assetPath = assetFolder + "/" + rel;
            if (male)
            {
                if (entry.MalePath == null) entry.MalePath = assetPath;
            }
            else
            {
                if (entry.FemalePath == null) entry.FemalePath = assetPath;
            }
        }
    }

    void RebuildFilter()
    {
        m_Filtered.Clear();
        m_MaleCount = 0;
        m_FemaleCount = 0;
        string query = (m_Search ?? string.Empty).Trim();
        foreach (AnimEntry e in m_All)
        {
            if (m_MarkOnly && (m_MarkSet == null || !m_MarkSet.Contains(e.Name))) continue;
            if (m_GenderFilter == 1 && e.MalePath == null) continue;
            if (m_GenderFilter == 2 && e.FemalePath == null) continue;
            if (m_CatFilter > 0 && e.Category != kCategories[m_CatFilter - 1]) continue;
            if (query.Length > 0 &&
                e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                (e.CnName == null || e.CnName.IndexOf(query, StringComparison.Ordinal) < 0)) continue;
            m_Filtered.Add(e);
            if (e.MalePath != null) m_MaleCount++;
            if (e.FemalePath != null) m_FemaleCount++;
        }
    }

    #endregion

    #region 动作清单导入

    const string kMarkDirKey = "AnimPreviewWindow.MarkDir";

    void RebuildMarkSet()
    {
        m_MarkSet = m_MarkNames.Count == 0
            ? null
            : new HashSet<string>(m_MarkNames, StringComparer.OrdinalIgnoreCase);
        RecalcMarkHit();
    }

    // 清单名在当前扫描结果里的命中数（用于导入后提示填错的名字）
    void RecalcMarkHit()
    {
        m_MarkHit = 0;
        if (m_MarkSet == null) return;
        foreach (AnimEntry e in m_All)
            if (m_MarkSet.Contains(e.Name)) m_MarkHit++;
    }

    void ImportMarkList()
    {
        string dir = EditorPrefs.GetString(kMarkDirKey, "");
        string path = EditorUtility.OpenFilePanel("选择动作清单 CSV", dir, "csv,txt");
        if (string.IsNullOrEmpty(path)) return;
        EditorPrefs.SetString(kMarkDirKey, Path.GetDirectoryName(path));

        List<string> names = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string raw in ReadAllLinesCompat(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string cell = FirstCell(line);
                if (cell.Length == 0) continue;
                if (names.Count == 0 && IsHeaderCell(cell)) continue; // 首行表头
                if (seen.Add(cell)) names.Add(cell);
            }
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("导入清单失败", ex.Message, "确定");
            return;
        }

        if (names.Count == 0)
        {
            EditorUtility.DisplayDialog("导入清单", "未读到任何动作名，请检查文件内容。", "确定");
            return;
        }

        m_MarkNames = names;
        m_MarkPath = path;
        m_MarkOnly = true;
        RebuildMarkSet();
        m_ListScroll = Vector2.zero;
        RebuildFilter();
        Repaint();

        // 报告清单里在工程中找不到的名字（多半是拼写不一致）
        List<string> missing = new List<string>();
        foreach (string n in names)
        {
            bool found = false;
            foreach (AnimEntry e in m_All)
            {
                if (string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found) missing.Add(n);
        }

        string msg = $"导入 {names.Count} 项，命中 {m_MarkHit} 项";
        if (missing.Count > 0)
        {
            int show = Mathf.Min(missing.Count, 15);
            msg += $"\n未找到 {missing.Count} 项（前 {show} 个）：\n" +
                   string.Join("\n", missing.GetRange(0, show)) +
                   (missing.Count > show ? "\n…" : "");
            Debug.LogWarning($"[动作预览器] 清单未找到：{string.Join(", ", missing)}");
        }

        EditorUtility.DisplayDialog("导入动作清单", msg, "确定");
    }

    void ClearMarkList()
    {
        m_MarkNames.Clear();
        m_MarkPath = "";
        m_MarkOnly = false;
        RebuildMarkSet();
        RebuildFilter();
        Repaint();
    }

    // Excel 保存的 CSV 可能是 UTF-8（带/不带 BOM）或 GBK；动作名是 ASCII，但备注/表头可能是中文
    static string[] ReadAllLinesCompat(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split('\n');
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).Split('\n'); // 严格 UTF-8，失败抛异常
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(936).GetString(bytes).Split('\n'); // GBK（Excel 中文默认）
        }
    }

    // 取首个字段：支持逗号或 Tab 分隔、双引号包裹
    static string FirstCell(string line)
    {
        string cell;
        int tab = line.IndexOf('\t');
        if (tab >= 0)
        {
            cell = line.Substring(0, tab);
        }
        else if (line.Length >= 2 && line[0] == '"')
        {
            int end = line.IndexOf('"', 1);
            cell = end > 0 ? line.Substring(1, end - 1) : line.Substring(1);
        }
        else
        {
            int comma = line.IndexOf(',');
            cell = comma >= 0 ? line.Substring(0, comma) : line;
        }

        return cell.Trim();
    }

    static bool IsHeaderCell(string s)
    {
        return s.Equals("name", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("anim", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("动作名", StringComparison.Ordinal) ||
               s.Equals("命名", StringComparison.Ordinal) ||
               s.Equals("动作名称", StringComparison.Ordinal);
    }

    #endregion

    #region 分类与中文名

    // 相机动画、创建角色用的换性别过渡、备份文件不参与预览
    static bool IsExcludedAnim(string name)
    {
        if (name.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("_bak", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(name, "female_male", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "male_female", StringComparison.OrdinalIgnoreCase);
    }

    // 按 kCategories 顺序先命中先得：前缀 → 关键词 → 兜底"其他玩法"
    static string Classify(string name)
    {
        // dzk=舞蹈卡；dj=蹦迪/DJ舞曲（dj_bd_*、dj_bengdi）；dance=getin_dance* 进入舞蹈
        if (StartsWithAny(name, "dzk_", "dj_") || ContainsAny(name, "wudao", "tiaowu", "disco", "jiangshi", "dance"))
            return kCategories[0]; // 舞蹈
        if (StartsWithAny(name, "sr_", "2v2_", "3v3_", "qinglv_", "ql_") ||
            ContainsAny(name, "jiewen", "sajiao", "zhendatui"))
            return kCategories[1]; // 双人互动
        if (StartsWithAny(name, "in_", "daiji", "tsdaiji_", "ttx_", "spy_"))
            return kCategories[2]; // 待机基础
        if (StartsWithAny(name, "jiaju_", "a_", "zhangpeng_", "qiuqian_", "zuoxia_", "zuodi_"))
            return kCategories[3]; // 家具场景
        if (StartsWithAny(name, "ride_"))
            return kCategories[4]; // 骑乘
        if (StartsWithAny(name, "sc_", "admission_", "kaichang_", "xuanjue_", "createrole_",
                "create_role", "tujian_", "ugc_", "putongzhanshi_"))
            return kCategories[5]; // 换装展示
        if (StartsWithAny(name, "expression_", "bq_"))
            return kCategories[6]; // 表情情绪
        // rank 同时覆盖 rank_ 与 rank1_*；guanjun 覆盖裸名与 guanjun_ouxiang_*
        if (StartsWithAny(name, "rank", "guanjun", "sd_", "shengli_", "phb_"))
            return kCategories[7]; // 比赛结算
        if (StartsWithAny(name, "drums_", "guitar_", "microphone_", "keyboard_", "ktv_", "xm_"))
            return kCategories[8]; // 乐器演出
        return kCategories[9]; // 其他玩法
    }

    static bool StartsWithAny(string s, params string[] prefixes)
    {
        foreach (string p in prefixes)
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool ContainsAny(string s, params string[] keys)
    {
        foreach (string k in keys)
            if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // 读取 t_animation_card.txt 的 f_animation → f_name（中文名），缺失时静默降级
    void LoadCnNames()
    {
        m_CnNames.Clear();
        string path = Path.Combine(Application.dataPath, "<你的工程>/Config/Tbl/t_animation_card.txt");
        if (!File.Exists(path)) return;
        try
        {
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '/') continue;
                string[] cols = line.Split('\t');
                if (cols.Length < 3 || cols[0] == "f_id") continue;
                string anim = cols[2].Trim();
                string cn = cols[1].Trim();
                if (anim.Length == 0 || cn.Length == 0) continue;
                if (!m_CnNames.ContainsKey(anim)) m_CnNames.Add(anim, cn);
            }
        }
        catch (Exception)
        {
            // 配置缺失/读取失败时不阻断扫描
        }
    }

    // 文件名常带 _01/_02 序号后缀，逐段剥掉数字后缀再查
    string LookupCn(string fileName)
    {
        if (m_CnNames.TryGetValue(fileName, out string cn)) return cn;
        string baseName = fileName;
        for (int i = 0; i < 2; i++)
        {
            int us = baseName.LastIndexOf('_');
            if (us <= 0) break;
            string tail = baseName.Substring(us + 1);
            if (tail.Length == 0 || !IsDigits(tail)) break;
            baseName = baseName.Substring(0, us);
            if (m_CnNames.TryGetValue(baseName, out cn)) return cn;
        }

        return null;
    }

    static bool IsDigits(string s)
    {
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }

    #endregion

    void SetCatFilter(int idx)
    {
        if (m_CatFilter == idx) return;
        m_CatFilter = idx;
        m_ListScroll = Vector2.zero;
        RebuildFilter();
        Repaint();
    }

    void EnsureCatLabels()
    {
        if (m_CatLabels != null) return;
        m_CatLabels = new string[kCategories.Length + 1];
        m_CatLabels[0] = "全部";
        Array.Copy(kCategories, 0, m_CatLabels, 1, kCategories.Length);
    }

    #region 模型

    void BuildModels()
    {
        DestroySlot(m_P1);
        DestroySlot(m_P2);
        m_P1 = CreateSlot(m_BoyPrefab, "P1");
        m_P2 = CreateSlot(m_GirlPrefab, "P2");
        ApplyVisibility();
    }

    ModelSlot CreateSlot(GameObject prefab, string label)
    {
        ModelSlot slot = new ModelSlot();
        if (prefab == null || m_Preview == null) return slot;

        slot.Instance = m_Preview.InstantiatePrefabInScene(prefab);
        slot.Instance.name = label + "_" + prefab.name;
        foreach (Animation a in slot.Instance.GetComponentsInChildren<Animation>(true))
            a.playAutomatically = false;

        // DCC 导出的面部/头骨辅助节点上挂着 MeshFilter 代理网格（材质 guid 已丢失，会渲染成色块）。
        // boy.prefab 靠禁用头发实例的骨架根连带隐藏了它们，girl.prefab 漏禁用 → 头上多一块；
        // 这里对男女统一关闭这类辅助渲染，只禁 MeshRenderer 不动层级，避免影响骨骼与动画。
        foreach (MeshFilter mf in slot.Instance.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf.name.StartsWith("AnimBone_") && !mf.name.StartsWith("PinchBone_")) continue;
            MeshRenderer helper = mf.GetComponent<MeshRenderer>();
            if (helper != null) helper.enabled = false;
        }

        // boy/girl 这类 prefab 是「身体实例 + 服装实例」双结构，各自带一套独立的 Bip001 骨架，
        // 必须给每套骨架都挂上 Animation 并同步驱动；只驱动一套会让另一套停在绑定姿势、部件错位飘散。
        List<Transform> bips = new List<Transform>();
        FindDeepAll(slot.Instance.transform, "Bip001", bips);
        foreach (Transform bip in bips)
        {
            // 脚尖等子节点也叫 Bip001，只取每套骨架的最顶层根节点
            if (bip.parent == null || HasAncestor(bip.parent, "Bip001")) continue;
            Animation host = bip.parent.GetComponent<Animation>();
            if (host == null) host = bip.parent.gameObject.AddComponent<Animation>();
            host.playAutomatically = false;
            if (!slot.Anims.Contains(host)) slot.Anims.Add(host);
        }

        if (slot.Anims.Count == 0)
        {
            Animation fallback = FindHostAnimation(slot.Instance);
            if (fallback != null) slot.Anims.Add(fallback);
        }

        // 记录模型自带的默认待机动作，作为「无选中动作」时的回退，且所有骨架共用同一份保持同步
        foreach (Animation a in slot.Anims)
        {
            AnimationClip[] defaults = AnimationUtility.GetAnimationClips(a.gameObject);
            if (defaults == null) continue;
            foreach (AnimationClip c in defaults)
            {
                if (c != null)
                {
                    slot.DefaultClip = c;
                    break;
                }
            }

            if (slot.DefaultClip != null) break;
        }

        RestoreDefault(slot);
        return slot;
    }

    static void FindDeepAll(Transform root, string name, List<Transform> results)
    {
        if (root.name == name) results.Add(root);
        for (int i = 0; i < root.childCount; i++)
            FindDeepAll(root.GetChild(i), name, results);
    }

    static bool HasAncestor(Transform t, string name)
    {
        while (t != null)
        {
            if (t.name == name) return true;
            t = t.parent;
        }

        return false;
    }

    static Animation FindHostAnimation(GameObject root)
    {
        foreach (Animation a in root.GetComponentsInChildren<Animation>(true))
        {
            if (a.transform.Find("Bip001") != null) return a;
        }

        Transform bip = DeepFind(root.transform, "Bip001");
        if (bip != null && bip.parent != null)
        {
            Animation host = bip.parent.GetComponent<Animation>();
            if (host == null) host = bip.parent.gameObject.AddComponent<Animation>();
            host.playAutomatically = false;
            return host;
        }

        Animation any = root.GetComponentInChildren<Animation>(true);
        if (any != null) return any;
        return root.AddComponent<Animation>();
    }

    static Transform DeepFind(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = DeepFind(root.GetChild(i), name);
            if (hit != null) return hit;
        }

        return null;
    }

    void DestroySlot(ModelSlot slot)
    {
        if (slot == null) return;
        if (slot.Instance != null) DestroyImmediate(slot.Instance);
        slot.Instance = null;
        slot.Anims.Clear();
        slot.AddedStates.Clear();
        slot.Clip = null;
        slot.DefaultClip = null;
        slot.StateName = null;
        slot.TotalCurves = 0;
        slot.BoundCurves = 0;
    }

    void ApplyVisibility()
    {
        if (m_P1.Instance != null) m_P1.Instance.SetActive(m_ShowP1);
        if (m_P2.Instance != null) m_P2.Instance.SetActive(m_ShowP2);
        bool both = m_ShowP1 && m_ShowP2;
        if (m_P1.Instance != null)
            m_P1.Instance.transform.position = both ? new Vector3(-0.6f, 0f, 0f) : Vector3.zero;
        if (m_P2.Instance != null)
            m_P2.Instance.transform.position = both ? new Vector3(0.6f, 0f, 0f) : Vector3.zero;
        Reframe();
    }

    void Reframe()
    {
        bool has = false;
        Bounds bounds = default;
        foreach (ModelSlot slot in new[] { m_P1, m_P2 })
        {
            bool visible = slot == m_P1 ? m_ShowP1 : m_ShowP2;
            if (!visible || slot.Instance == null) continue;
            foreach (Renderer r in slot.Instance.GetComponentsInChildren<Renderer>())
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
            m_Dist = 3f;
            return;
        }

        m_Pivot = bounds.center;
        float radius = Mathf.Max(bounds.extents.magnitude, 0.3f);
        float fov = m_Preview != null ? m_Preview.camera.fieldOfView : 32f;
        m_Dist = radius / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.25f;
    }

    #endregion

    #region 动画播放

    void SelectAnimation(AnimEntry entry)
    {
        m_Selected = entry;
        m_SelectedName = entry != null ? entry.Name : null;
        ApplySelection(true);
        bool anyClip = entry != null &&
                       ((m_ShowP1 && entry.MalePath != null) || (m_ShowP2 && entry.FemalePath != null));
        m_Playing = anyClip;
        Repaint();
    }

    void ApplySelection(bool resetTime)
    {
        if (resetTime)
        {
            m_P1.Time = 0f;
            m_P2.Time = 0f;
            m_LastTick = EditorApplication.timeSinceStartup;
        }

        if (m_Selected == null)
        {
            if (m_ShowP1) RestoreDefault(m_P1);
            if (m_ShowP2) RestoreDefault(m_P2);
            return;
        }

        if (m_ShowP1) ApplyClipFor(m_P1, m_Selected.MalePath);
        if (m_ShowP2) ApplyClipFor(m_P2, m_Selected.FemalePath);
        SampleAll();
    }

    void ApplyClipFor(ModelSlot slot, string assetPath)
    {
        AnimationClip clip = null;
        if (!string.IsNullOrEmpty(assetPath))
            clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
        if (clip == null || slot.Anims.Count == 0)
        {
            RestoreDefault(slot);
            return;
        }

        ClearAddedStates(slot);

        string stateName = clip.name;
        bool any = false;
        foreach (Animation anim in slot.Anims)
        {
            bool existed = anim.GetClip(stateName) != null;
            if (!existed)
            {
                anim.AddClip(clip, stateName);
                slot.AddedStates[anim] = stateName;
            }

            AnimationState st = anim[stateName];
            if (st == null) continue;

            anim.Play(stateName);
            st.speed = 0f;
            st.wrapMode = WrapMode.Once;
            st.time = 0f;
            any = true;
        }

        if (!any)
        {
            RestoreDefault(slot);
            return;
        }

        slot.Clip = clip;
        slot.StateName = stateName;
        ComputeBindingStats(slot, clip);
        foreach (Animation anim in slot.Anims) anim.Sample();
    }

    // 统计 clip 的曲线路径里有多少条能在当前骨架上解析到目标。
    // 路径解析规则与 legacy Animation 一致：相对 Animation 宿主的 Transform.Find。
    // 同一路径可能带多条属性曲线（旋转 x/y/z/w 等），按路径去重计数更直观。
    void ComputeBindingStats(ModelSlot slot, AnimationClip clip)
    {
        slot.TotalCurves = 0;
        slot.BoundCurves = 0;
        if (clip == null || slot.Anims.Count == 0) return;

        HashSet<string> paths = new HashSet<string>();
        foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
            paths.Add(b.path);
        foreach (EditorCurveBinding b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            paths.Add(b.path);

        int total = 0;
        int bound = 0;
        foreach (string p in paths)
        {
            total++;
            if (PathResolves(slot, p)) bound++;
        }

        slot.TotalCurves = total;
        slot.BoundCurves = bound;
    }

    // 任一 Animation 宿主能找到该路径即算绑定（身体/服装两套骨架结构相同）
    static bool PathResolves(ModelSlot slot, string path)
    {
        if (string.IsNullOrEmpty(path)) return true; // 曲线打在宿主自身上
        foreach (Animation a in slot.Anims)
        {
            if (a == null) continue;
            if (a.transform.Find(path) != null) return true;
        }

        return false;
    }

    void RestoreDefault(ModelSlot slot)
    {
        ClearAddedStates(slot);
        slot.Clip = null;
        slot.StateName = null;
        slot.TotalCurves = 0;
        slot.BoundCurves = 0;
        if (slot.Anims.Count == 0) return;

        // 所有骨架统一播放模型自带的默认待机，保证身体与服装两套骨架同步
        AnimationClip def = slot.DefaultClip;
        if (def == null) return;

        foreach (Animation anim in slot.Anims)
        {
            anim.Stop();
            bool existed = anim.GetClip(def.name) != null;
            if (!existed)
            {
                anim.AddClip(def, def.name);
                slot.AddedStates[anim] = def.name;
            }

            AnimationState st = anim[def.name];
            if (st == null) continue;
            anim.Play(def.name);
            st.speed = 0f;
            st.time = 0f;
            anim.Sample();
        }
    }

    void ClearAddedStates(ModelSlot slot)
    {
        foreach (KeyValuePair<Animation, string> kv in slot.AddedStates)
        {
            if (kv.Key != null) kv.Key.RemoveClip(kv.Value);
        }

        slot.AddedStates.Clear();
        foreach (Animation anim in slot.Anims) anim.Stop();
    }

    ModelSlot PrimarySlot()
    {
        if (m_ShowP1 && m_P1.Clip != null) return m_P1;
        if (m_ShowP2 && m_P2.Clip != null) return m_P2;
        return null;
    }

    void SetSlotProgress(float t01)
    {
        t01 = Mathf.Clamp01(t01);
        if (m_ShowP1 && m_P1.Clip != null && m_P1.Length > 0f) m_P1.Time = t01 * m_P1.Length;
        if (m_ShowP2 && m_P2.Clip != null && m_P2.Length > 0f) m_P2.Time = t01 * m_P2.Length;
    }

    void SampleAll()
    {
        SampleSlot(m_P1, m_ShowP1);
        SampleSlot(m_P2, m_ShowP2);
    }

    void SampleSlot(ModelSlot slot, bool visible)
    {
        if (!visible || slot.Anims.Count == 0 || slot.Clip == null) return;
        float len = slot.Length;
        if (len <= 0f) return;

        float t = slot.Time;
        if (m_Loop)
        {
            if (t >= len) t = t % len;
        }
        else
        {
            t = Mathf.Min(t, len);
        }

        foreach (Animation anim in slot.Anims)
        {
            AnimationState st = anim[slot.StateName];
            if (st == null) continue;
            st.time = t;
            anim.Sample();
        }
    }

    void OnEdUpdate()
    {
        if (!m_Playing || m_Selected == null) return;
        double now = EditorApplication.timeSinceStartup;
        float dt = Mathf.Clamp((float)(now - m_LastTick), 0f, 0.1f);
        m_LastTick = now;

        bool anyVisible = false;
        bool allDone = true;
        AdvanceSlot(m_P1, m_ShowP1, dt, ref anyVisible, ref allDone);
        AdvanceSlot(m_P2, m_ShowP2, dt, ref anyVisible, ref allDone);
        if (!anyVisible)
        {
            m_Playing = false;
            return;
        }

        if (!m_Loop && allDone) m_Playing = false;
        SampleAll();
        Repaint();
    }

    void AdvanceSlot(ModelSlot slot, bool visible, float dt, ref bool anyVisible, ref bool allDone)
    {
        if (slot.Clip == null || slot.Length <= 0f) return;
        if (visible) anyVisible = true;

        slot.Time += dt * m_Speed;
        if (m_Loop)
        {
            if (slot.Time >= slot.Length) slot.Time = slot.Time % slot.Length;
        }
        else
        {
            if (slot.Time >= slot.Length) slot.Time = slot.Length;
            if (visible && slot.Time < slot.Length - 1e-5f) allDone = false;
        }
    }

    void TogglePlay()
    {
        if (m_Selected == null || PrimarySlot() == null) return;
        if (!m_Playing)
        {
            if (!m_Loop)
            {
                bool done = true;
                if (m_ShowP1 && m_P1.Clip != null && m_P1.Time < m_P1.Length - 1e-4f) done = false;
                if (m_ShowP2 && m_P2.Clip != null && m_P2.Time < m_P2.Length - 1e-4f) done = false;
                if (done) Restart();
            }

            m_LastTick = EditorApplication.timeSinceStartup;
            m_Playing = true;
        }
        else
        {
            m_Playing = false;
        }

        Repaint();
    }

    void Restart()
    {
        m_P1.Time = 0f;
        m_P2.Time = 0f;
        m_LastTick = EditorApplication.timeSinceStartup;
        SampleAll();
        m_Playing = m_Selected != null && PrimarySlot() != null;
        Repaint();
    }

    #endregion

    #region GUI

    void OnGUI()
    {
        if (m_Preview == null) EnsurePreview();
        EnsureStyles();
        DrawToolbar();

        EditorGUILayout.BeginHorizontal();
        DrawPreviewArea();
        DrawSidePanel();
        EditorGUILayout.EndHorizontal();

        DrawBottomBar();
        HandleKeys();
    }

    void EnsureStyles()
    {
        if (m_NameStyle != null) return;
        m_NameStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip
        };
        m_YesStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.45f, 0.95f, 0.55f) }
        };
        m_NoStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
        {
            alignment = TextAnchor.MiddleCenter
        };
        m_StatusStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
        m_WarnStyle = new GUIStyle(EditorStyles.whiteMiniLabel)
        {
            normal = { textColor = new Color(1f, 0.42f, 0.42f) }
        };
    }

    void DrawToolbar()
    {
        GUILayout.BeginHorizontal(EditorStyles.toolbar);
        bool p1 = GUILayout.Toggle(m_ShowP1, "玩家1(男)", EditorStyles.toolbarButton, GUILayout.Width(96));
        bool p2 = GUILayout.Toggle(m_ShowP2, "玩家2(女)", EditorStyles.toolbarButton, GUILayout.Width(96));
        if (p1 != m_ShowP1)
        {
            m_ShowP1 = p1;
            ApplyVisibility();
            ApplySelection(false);
        }

        if (p2 != m_ShowP2)
        {
            m_ShowP2 = p2;
            ApplyVisibility();
            ApplySelection(false);
        }

        GUILayout.Space(12);
        if (GUILayout.Button(m_Playing ? "暂停" : "播放", EditorStyles.toolbarButton, GUILayout.Width(48)))
            TogglePlay();
        if (GUILayout.Button("重播", EditorStyles.toolbarButton, GUILayout.Width(40)))
            Restart();
        bool loop = GUILayout.Toggle(m_Loop, "循环", EditorStyles.toolbarButton, GUILayout.Width(44));
        if (loop != m_Loop) m_Loop = loop;

        GUILayout.Space(8);
        GUILayout.Label("速度", EditorStyles.miniLabel, GUILayout.Width(26));
        float speed = GUILayout.HorizontalSlider(m_Speed, 0.1f, 3f, GUILayout.Width(80));
        if (!Mathf.Approximately(speed, m_Speed))
            m_Speed = Mathf.Round(speed * 100f) / 100f;
        GUILayout.Label(m_Speed.ToString("0.00") + "x", EditorStyles.miniLabel, GUILayout.Width(34));

        GUILayout.Space(10);
        string catLabel = m_CatFilter == 0 ? "分类·全部" : "分类·" + kCategories[m_CatFilter - 1];
        if (GUILayout.Button(catLabel, EditorStyles.toolbarDropDown, GUILayout.Width(110)))
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("全部"), m_CatFilter == 0, () => SetCatFilter(0));
            for (int i = 0; i < kCategories.Length; i++)
            {
                int idx = i + 1;
                string cat = kCategories[i];
                menu.AddItem(new GUIContent(cat), m_CatFilter == idx, () => SetCatFilter(idx));
            }

            menu.DropDown(GUILayoutUtility.GetLastRect());
        }

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("重置视角", EditorStyles.toolbarButton, GUILayout.Width(64)))
        {
            Reframe();
            Repaint();
        }

        GUILayout.EndHorizontal();
    }

    void DrawPreviewArea()
    {
        Rect rect = GUILayoutUtility.GetRect(200, 4000, 200, 4000,
            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        HandleCamInput(rect);
        if (Event.current.type != EventType.Repaint) return;

        m_Preview.BeginPreview(rect, GUIStyle.none);
        UpdateCamera();
        m_Preview.Render(true);
        m_Preview.EndAndDrawPreview(rect);
        DrawOverlays(rect);
    }

    void UpdateCamera()
    {
        Quaternion rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
        Camera cam = m_Preview.camera;
        cam.transform.position = m_Pivot + rot * new Vector3(0f, 0f, -m_Dist);
        cam.transform.rotation = rot;
    }

    void DrawOverlays(Rect rect)
    {
        string title = m_Selected != null
            ? (m_Selected.CnName != null ? m_Selected.Name + "（" + m_Selected.CnName + "）" : m_Selected.Name)
            : "未选择动作 — 右侧列表单击开始";
        GUI.Label(new Rect(rect.x + 8, rect.y + 6, rect.width - 16, 20), title,
            EditorStyles.whiteMiniLabel);
        string bindWarn = BindingWarnText();
        if (bindWarn != null)
            GUI.Label(new Rect(rect.x + 8, rect.y + 26, rect.width - 16, 18), bindWarn, m_WarnStyle);
        DrawSlotTag(rect, m_P1, m_ShowP1, "玩家1(男)");
        DrawSlotTag(rect, m_P2, m_ShowP2, "玩家2(女)");
        GUI.Label(new Rect(rect.x + 8, rect.yMax - 22, rect.width - 16, 16),
            "左键拖拽旋转 · 滚轮缩放 · 空格播放/暂停", EditorStyles.whiteMiniLabel);
    }

    // 有曲线绑定失败时给出红字提示（Unity 静默丢弃这些曲线，模型会表现为「不跟着动」）
    string BindingWarnText()
    {
        if (m_Selected == null) return null;
        List<string> parts = new List<string>();
        if (m_ShowP1) AppendBindWarn(m_P1, "P1", parts);
        if (m_ShowP2) AppendBindWarn(m_P2, "P2", parts);
        if (parts.Count == 0) return null;
        return string.Join("   ", parts) + " — 曲线路径与骨架不匹配，缺失部分不会动";
    }

    static void AppendBindWarn(ModelSlot slot, string tag, List<string> parts)
    {
        if (slot.Clip == null || slot.TotalCurves <= 0) return;
        if (slot.BoundCurves >= slot.TotalCurves) return;
        parts.Add($"{tag} 仅绑定 {slot.BoundCurves}/{slot.TotalCurves} 条曲线");
    }

    void DrawSlotTag(Rect rect, ModelSlot slot, bool visible, string label)
    {
        if (!visible || slot.Instance == null) return;
        bool has = false;
        Bounds bounds = default;
        foreach (Renderer r in slot.Instance.GetComponentsInChildren<Renderer>())
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

        if (!has) return;
        Camera cam = m_Preview.camera;
        float camW = cam.pixelWidth;
        float camH = cam.pixelHeight;
        if (camW <= 0f || camH <= 0f) return;

        Vector3 sp = cam.WorldToScreenPoint(new Vector3(bounds.center.x, bounds.max.y + 0.05f, bounds.center.z));
        if (sp.z <= 0f) return;
        float gx = rect.x + sp.x / camW * rect.width;
        float gy = rect.y + rect.height - sp.y / camH * rect.height;
        GUIContent content = new GUIContent(label);
        float w = EditorStyles.whiteMiniLabel.CalcSize(content).x;
        GUI.Label(new Rect(gx - w * 0.5f, gy - 16f, w + 6f, 16f), content, EditorStyles.whiteMiniLabel);
    }

    void HandleCamInput(Rect rect)
    {
        Event e = Event.current;
        int id = GUIUtility.GetControlID("AnimPreviewCam".GetHashCode(), FocusType.Passive);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button == 0 && rect.Contains(e.mousePosition))
                {
                    GUIUtility.hotControl = id;
                    e.Use();
                }

                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id)
                {
                    m_Yaw += e.delta.x * 0.3f;
                    m_Pitch = Mathf.Clamp(m_Pitch - e.delta.y * 0.3f, -89f, 89f);
                    e.Use();
                    Repaint();
                }

                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    e.Use();
                }

                break;
            case EventType.ScrollWheel:
                if (rect.Contains(e.mousePosition))
                {
                    m_Dist = Mathf.Clamp(m_Dist * (1f + e.delta.y * 0.08f), 0.3f, 60f);
                    e.Use();
                    Repaint();
                }

                break;
        }
    }

    void DrawSidePanel()
    {
        GUILayout.BeginVertical(GUILayout.Width(330));

        EditorGUI.BeginChangeCheck();
        GameObject nb = (GameObject)EditorGUILayout.ObjectField("玩家1 模型", m_BoyPrefab, typeof(GameObject), false);
        GameObject ng = (GameObject)EditorGUILayout.ObjectField("玩家2 模型", m_GirlPrefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck())
        {
            m_BoyPrefab = nb;
            m_GirlPrefab = ng;
            BuildModels();
            ApplySelection(false);
        }

        GUILayout.BeginHorizontal();
        string ns = GUILayout.TextField(m_Search ?? string.Empty, EditorStyles.toolbarSearchField,
            GUILayout.ExpandWidth(true));
        if (ns != m_Search)
        {
            m_Search = ns;
            RebuildFilter();
        }

        if (GUILayout.Button("刷新", GUILayout.Width(44)))
        {
            ScanAnimations();
            Repaint();
        }

        GUILayout.EndHorizontal();

        // 动作清单：导入 CSV 后可只看这批动作
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(m_MarkNames.Count > 0 ? "重导清单" : "导入清单", GUILayout.Height(18)))
            ImportMarkList();
        bool markOnly = GUILayout.Toggle(m_MarkOnly && m_MarkSet != null, "仅看清单",
            EditorStyles.miniButton, GUILayout.Height(18));
        if (markOnly != m_MarkOnly)
        {
            m_MarkOnly = markOnly;
            RebuildFilter();
            Repaint();
        }

        using (new EditorGUI.DisabledScope(m_MarkNames.Count == 0))
        {
            if (GUILayout.Button("清除", GUILayout.Width(40), GUILayout.Height(18)))
                ClearMarkList();
        }

        GUILayout.EndHorizontal();
        if (m_MarkNames.Count > 0)
        {
            string markInfo = $"清单 {m_MarkHit}/{m_MarkNames.Count} · {Path.GetFileName(m_MarkPath)}";
            if (m_MarkHit < m_MarkNames.Count)
                markInfo += "（部分名字未找到，导入时已提示）";
            GUILayout.Label(markInfo, EditorStyles.miniLabel);
        }

        GUILayout.BeginHorizontal();
        int nf = GUILayout.Toolbar(m_GenderFilter, new[] { "全部", "仅男", "仅女" },
            EditorStyles.miniButton, GUILayout.Height(18));
        if (nf != m_GenderFilter)
        {
            m_GenderFilter = nf;
            RebuildFilter();
        }

        GUILayout.EndHorizontal();

        // 分类下拉：与顶栏按钮等价，放在列表正上方保证可见
        EnsureCatLabels();
        int pick = EditorGUILayout.Popup("分类", m_CatFilter, m_CatLabels, GUILayout.Height(18));
        if (pick != m_CatFilter) SetCatFilter(pick);

        string catText = m_CatFilter == 0 ? "" : " · " + kCategories[m_CatFilter - 1];
        GUILayout.Label($"显示 {m_Filtered.Count} / 共 {m_All.Count}（男 {m_MaleCount} · 女 {m_FemaleCount}）{catText}",
            EditorStyles.miniLabel);

        DrawList();
        GUILayout.Label("单击动作行开始预览 · 空格 播放/暂停 · F 重置视角", EditorStyles.miniLabel);
        GUILayout.EndVertical();
    }

    void DrawList()
    {
        Rect viewRect = GUILayoutUtility.GetRect(100, 10000, 100, 10000,
            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        int count = m_Filtered.Count;
        Rect content = new Rect(0f, 0f, Mathf.Max(0f, viewRect.width - 16f), count * kRowHeight);
        m_ListScroll = GUI.BeginScrollView(viewRect, m_ListScroll, content);

        EventType ev = Event.current.type;
        if ((ev == EventType.Repaint || ev == EventType.MouseDown) && count > 0)
        {
            int first = Mathf.Max(0, Mathf.FloorToInt(m_ListScroll.y / kRowHeight));
            int last = Mathf.Min(count,
                Mathf.CeilToInt((m_ListScroll.y + viewRect.height) / kRowHeight) + 1);
            for (int i = first; i < last; i++)
            {
                AnimEntry e = m_Filtered[i];
                Rect row = new Rect(0f, i * kRowHeight, content.width, kRowHeight - 1f);
                bool selected = m_Selected == e;
                bool hover = row.Contains(Event.current.mousePosition);
                if (selected)
                    EditorGUI.DrawRect(row, new Color(0.24f, 0.49f, 0.90f, 0.35f));
                else if (hover)
                    EditorGUI.DrawRect(row, new Color(1f, 1f, 1f, 0.06f));

                string label = e.CnName != null ? e.Name + "（" + e.CnName + "）" : e.Name;
                GUI.Label(new Rect(row.x + 6, row.y, row.width - 126, row.height), label, m_NameStyle);
                // 分类标签（灰字，跟在名称后、性别徽章前）
                GUI.Label(new Rect(row.xMax - 118, row.y, 56, row.height),
                    e.Category ?? "", EditorStyles.centeredGreyMiniLabel);
                GUI.Label(new Rect(row.xMax - 58, row.y, 26, row.height),
                    e.MalePath != null ? "男" : "-", e.MalePath != null ? m_YesStyle : m_NoStyle);
                GUI.Label(new Rect(row.xMax - 30, row.y, 26, row.height),
                    e.FemalePath != null ? "女" : "-", e.FemalePath != null ? m_YesStyle : m_NoStyle);

                if (ev == EventType.MouseDown && Event.current.button == 0 && hover)
                {
                    SelectAnimation(e);
                    Event.current.Use();
                }
            }
        }

        GUI.EndScrollView();
    }

    void DrawBottomBar()
    {
        GUILayout.BeginHorizontal(EditorStyles.toolbar);
        ModelSlot primary = PrimarySlot();
        float len = primary != null ? primary.Length : 0f;
        float cur = primary != null ? primary.Time : 0f;
        using (new EditorGUI.DisabledScope(len <= 0f))
        {
            EditorGUI.BeginChangeCheck();
            float nt = GUILayout.HorizontalSlider(cur, 0f, len > 0f ? len : 1f,
                GUILayout.ExpandWidth(true));
            if (EditorGUI.EndChangeCheck())
            {
                SetSlotProgress(len > 0f ? nt / len : 0f);
                m_LastTick = EditorApplication.timeSinceStartup;
                SampleAll();
                Repaint();
                cur = primary != null ? primary.Time : 0f;
            }
        }

        GUILayout.Label($"{cur:0.00} / {len:0.00}s", EditorStyles.miniLabel, GUILayout.Width(96));
        GUILayout.Label(StatusText(), m_StatusStyle);
        GUILayout.EndHorizontal();
    }

    string StatusText()
    {
        if (m_Selected == null) return "选择右侧列表开始预览";
        List<string> parts = new List<string>();
        if (m_ShowP1)
        {
            if (m_P1.Clip != null) parts.Add("P1:" + m_P1.Clip.name + BindStat(m_P1));
            else parts.Add(m_Selected.MalePath == null ? "P1:无男版" : "P1:未加载");
        }

        if (m_ShowP2)
        {
            if (m_P2.Clip != null) parts.Add("P2:" + m_P2.Clip.name + BindStat(m_P2));
            else parts.Add(m_Selected.FemalePath == null ? "P2:无女版" : "P2:未加载");
        }

        if (parts.Count == 0) parts.Add("未显示任何模型");
        return string.Join("   ", parts);
    }

    // 已绑定/总曲线数；有缺失时标红
    static string BindStat(ModelSlot slot)
    {
        if (slot.TotalCurves <= 0) return string.Empty;
        if (slot.BoundCurves >= slot.TotalCurves)
            return $" <color=#7BD88F>{slot.BoundCurves}/{slot.TotalCurves}</color>";
        return $" <color=#FF6B6B>{slot.BoundCurves}/{slot.TotalCurves}</color>";
    }

    void HandleKeys()
    {
        Event e = Event.current;
        if (e.type != EventType.KeyDown || GUIUtility.keyboardControl != 0) return;
        if (e.keyCode == KeyCode.Space)
        {
            TogglePlay();
            e.Use();
        }
        else if (e.keyCode == KeyCode.F)
        {
            Reframe();
            e.Use();
            Repaint();
        }
    }

    #endregion
}
