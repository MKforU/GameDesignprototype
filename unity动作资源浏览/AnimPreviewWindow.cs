using System;
using System.Collections.Generic;
using System.IO;
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

    sealed class AnimEntry
    {
        public string Name;
        public string MalePath;
        public string FemalePath;
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
    [SerializeField] string m_SelectedName;
    [SerializeField] float m_Yaw = 180f;
    [SerializeField] float m_Pitch = -5f;
    [SerializeField] float m_Dist = 3f;
    [SerializeField] Vector3 m_Pivot = new Vector3(0f, 1f, 0f);

    PreviewRenderUtility m_Preview;
    ModelSlot m_P1 = new ModelSlot();
    ModelSlot m_P2 = new ModelSlot();
    readonly List<AnimEntry> m_All = new List<AnimEntry>();
    readonly List<AnimEntry> m_Filtered = new List<AnimEntry>();
    AnimEntry m_Selected;
    Vector2 m_ListScroll;
    double m_LastTick;
    int m_MaleCount;
    int m_FemaleCount;
    GUIStyle m_NameStyle;
    GUIStyle m_YesStyle;
    GUIStyle m_NoStyle;

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
        RebuildFilter();
    }

    void ScanFolder(string assetFolder, bool male, Dictionary<string, AnimEntry> byName)
    {
        string full = Path.Combine(Application.dataPath, assetFolder.Substring("Assets/".Length));
        if (!Directory.Exists(full)) return;

        foreach (string file in Directory.GetFiles(full, "*.anim", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(file);
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
            if (m_GenderFilter == 1 && e.MalePath == null) continue;
            if (m_GenderFilter == 2 && e.FemalePath == null) continue;
            if (query.Length > 0 && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            m_Filtered.Add(e);
            if (e.MalePath != null) m_MaleCount++;
            if (e.FemalePath != null) m_FemaleCount++;
        }
    }

    #endregion

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
        foreach (Animation anim in slot.Anims) anim.Sample();
    }

    void RestoreDefault(ModelSlot slot)
    {
        ClearAddedStates(slot);
        slot.Clip = null;
        slot.StateName = null;
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
        string title = m_Selected != null ? m_Selected.Name : "未选择动作 — 右侧列表双击/单击开始";
        GUI.Label(new Rect(rect.x + 8, rect.y + 6, rect.width - 16, 20), title,
            EditorStyles.whiteMiniLabel);
        DrawSlotTag(rect, m_P1, m_ShowP1, "玩家1(男)");
        DrawSlotTag(rect, m_P2, m_ShowP2, "玩家2(女)");
        GUI.Label(new Rect(rect.x + 8, rect.yMax - 22, rect.width - 16, 16),
            "左键拖拽旋转 · 滚轮缩放 · 空格播放/暂停", EditorStyles.whiteMiniLabel);
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

        GUILayout.BeginHorizontal();
        int nf = GUILayout.Toolbar(m_GenderFilter, new[] { "全部", "仅男", "仅女" },
            EditorStyles.miniButton, GUILayout.Height(18));
        if (nf != m_GenderFilter)
        {
            m_GenderFilter = nf;
            RebuildFilter();
        }

        GUILayout.EndHorizontal();

        GUILayout.Label($"显示 {m_Filtered.Count} / 共 {m_All.Count}（男 {m_MaleCount} · 女 {m_FemaleCount}）",
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

                GUI.Label(new Rect(row.x + 6, row.y, row.width - 66, row.height), e.Name, m_NameStyle);
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
        GUILayout.Label(StatusText(), EditorStyles.miniLabel);
        GUILayout.EndHorizontal();
    }

    string StatusText()
    {
        if (m_Selected == null) return "选择右侧列表开始预览";
        List<string> parts = new List<string>();
        if (m_ShowP1)
        {
            if (m_P1.Clip != null) parts.Add("P1:" + m_P1.Clip.name);
            else parts.Add(m_Selected.MalePath == null ? "P1:无男版" : "P1:未加载");
        }

        if (m_ShowP2)
        {
            if (m_P2.Clip != null) parts.Add("P2:" + m_P2.Clip.name);
            else parts.Add(m_Selected.FemalePath == null ? "P2:无女版" : "P2:未加载");
        }

        if (parts.Count == 0) parts.Add("未显示任何模型");
        return string.Join("   ", parts);
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
