using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Config;
using UnityEditor;
using UnityEngine;

namespace GameEditor
{
    /// <summary>
    /// 服装资源预览窗口：对照 t_item / t_equip / t_mall / t_pokedex 配置，
    /// 按「有套装 / 无套装」分类浏览服装图标与配置详情，并输出配置比对问题清单。
    /// 套装口径 = t_item.f_GroupID（t_equip.f_SuitID 当前全表为空，不作为判定依据）。
    /// </summary>
    public class ClothResourcePreviewWindow : EditorWindow
    {
        const string IconFolderItem = "Assets/<你的工程>/Asset/BuildAssets/MainImage/Image/ItemIcon";
        const string IconFolderPinch = "Assets/<你的工程>/Asset/BuildAssets/MainImage/Image/PinchFaceIcon";

        const float DetailWidth = 320f;
        const float CellWidth = 116f;
        const float CellHeight = 132f;
        const float SuitRowHeight = 64f;
        const int TexCacheLimit = 600;

        enum Category
        {
            All = 0,
            Suit = 1,
            NoSuit = 2,
        }

        enum ViewMode
        {
            Flat = 0,
            Suit = 1,
        }

        enum SortMode
        {
            Id = 0,
            Name = 1,
            Part = 2,
        }

        class ClothEntry
        {
            public uint Id;
            public string Name;
            public ushort Type2;
            public ushort Sex;
            public string GroupRaw;
            public string[] GroupIds = new string[0];
            public string IconName;
            public ushort Label;
            public uint Charm;
            public string Description;
            public Item_t_equip Equip;
            public string IconPath;

            public bool HasSuit => GroupIds.Length > 0;
        }

        class SuitGroup
        {
            public string Id;
            public string Name;
            public bool IsMale;
            public readonly List<ClothEntry> Entries = new List<ClothEntry>();

            public string PartsSummary
            {
                get
                {
                    var parts = new List<string>();
                    foreach (var e in Entries)
                    {
                        var part = PartName(e.Type2);
                        if (!parts.Contains(part))
                            parts.Add(part);
                    }
                    return string.Join("/", parts);
                }
            }
        }

        /// <summary>网格卡片：单品直接是衣服；套装只显示主件图标，点击穿整套。</summary>
        class ViewCard
        {
            public ClothEntry Entry;
            public SuitGroup Group;   // null = 单品
        }

        class Issue
        {
            public string Type;
            public string Message;
            public uint ItemId;
            public bool HasItem => ItemId != 0;
        }

        // ---- 运行数据 ----
        readonly List<ClothEntry> _entries = new List<ClothEntry>();
        readonly List<ClothEntry> _viewList = new List<ClothEntry>();
        readonly List<ViewCard> _viewCards = new List<ViewCard>();
        readonly Dictionary<string, SuitGroup> _groups = new Dictionary<string, SuitGroup>();
        readonly List<SuitGroup> _groupList = new List<SuitGroup>();
        readonly List<Issue> _issues = new List<Issue>();
        readonly Dictionary<string, string> _iconIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<uint, List<Item_t_mall>> _mallByItem = new Dictionary<uint, List<Item_t_mall>>();
        readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<string>> _modelSearchCache = new Dictionary<string, List<string>>();

        // ---- 界面状态 ----
        Category _category = Category.All;
        ViewMode _view = ViewMode.Flat;
        SortMode _sort = SortMode.Id;
        int _partFilter = -1;      // -1 = 全部
        int _sexFilter = -1;       // -1 = 全部
        string _search = "";
        bool _onlyIssue;

        // ---- 投放表 ----
        ClothReleaseData _release;
        int _releaseFilter;   // 0全部 1仅已投放 2仅未投放
        int _starFilter = -1;   // -1=全部，0..4 = t_item.f_LabelType（0未配置 1二星 2三星 3四星 4五星）
        int _relReleased;
        int _relUnreleased;
        int _relOut;
        long _lastWearMs;
        float m_FxAccum;
        readonly List<string> _texOrder = new List<string>();
        uint _selectedId;
        Vector2 _gridScroll;
        Vector2 _suitScroll;
        Vector2 _detailScroll;
        Vector2 _issueScroll;
        bool _showIssues;

        // ---- 3D试穿 ----
        ClothTryOnPreview m_TryOn;
        bool m_ShowTryOn = true;
        string m_WornGroupId;
        double m_LastEditorTick;
        string[] _partLabels = new string[0];
        int[] _partValues = new int[0];

        GUIStyle _cellStyle;
        GUIStyle _cellNameStyle;
        GUIStyle _cellMetaStyle;
        GUIStyle _badgeStyle;
        GUIStyle _badgeReleased;
        GUIStyle _badgeUnreleased;
        GUIStyle _badgePartial;
        GUIStyle _badgeOut;
        GUIStyle _sectionTitle;

        static readonly Dictionary<int, string> PartNames = new Dictionary<int, string>
        {
            {0,"发型"},{1,"上衣"},{2,"下衣"},{3,"鞋子"},{4,"连身"},{5,"上全身"},{6,"整全身"},{7,"头顶"},
            {8,"左精灵"},{9,"右精灵"},{10,"翅膀饰"},{11,"表情"},{12,"项链"},{13,"尾巴"},{14,"左手腕"},{15,"右手腕"},
            {16,"左手持"},{17,"右手持"},{18,"手套"},{19,"戒指"},{20,"脚环"},{21,"徽章"},{22,"脸饰"},{23,"肤色(弃用)"},
            {24,"袜子"},{25,"坐骑"},{26,"头"},{27,"耳环"},{36,"眉毛"},{37,"唇彩"},{38,"瞳孔"},{39,"眼影"},
            {40,"眼线"},{41,"腮红"},{42,"睫毛"},{43,"脸部贴花"},{44,"肤色"},{45,"脸妆大贴画"},{46,"胡子"},{47,"眼影贴花"},
            {48,"吉他"},{49,"架子鼓"},{50,"钢琴"},{51,"麦克风"},{52,"腰饰"},{53,"手部光环"},{54,"帽子"},{55,"挎肩包"},
            {56,"修容"},{57,"卧蚕"},{58,"双眼皮"},{59,"发饰"},{60,"眼饰"},{61,"脚底特效"},
        };

        /// <summary>与 Config_t_item 校验逻辑保持一致：这些部位不要求 t_equip 存在。</summary>
        static readonly int[] EquipNotRequiredParts = { 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47 };

        [MenuItem("Tools/服装资源预览")]
        public static void OpenWindow()
        {
            var win = GetWindow<ClothResourcePreviewWindow>("服装资源预览");
            win.minSize = new Vector2(940f, 720f);
            win.Rebuild();
        }

        void OnEnable()
        {
            if (_entries.Count == 0)
                Rebuild();
            m_LastEditorTick = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnEditorUpdate;
            _release = ClothReleaseData.LoadCache();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            m_TryOn?.Dispose();
            m_TryOn = null;
        }

        void OnEditorUpdate()
        {
            if (m_TryOn == null || !m_ShowTryOn)
                return;
            var wantFx = m_TryOn.EffectsOn;
            if (!m_TryOn.Playing && !wantFx && !m_TryOn.Flying)
                return;

            var now = EditorApplication.timeSinceStartup;
            var dt = (float)Math.Max(0.0, Math.Min(now - m_LastEditorTick, 0.1));
            m_LastEditorTick = now;

            // 约30fps 节流，避免常驻满速重绘
            m_FxAccum += dt;
            if (m_FxAccum < 0.033f)
                return;
            var step = m_FxAccum;
            m_FxAccum = 0f;

            var need = false;
            if (m_TryOn.Flying)
                need |= m_TryOn.TickFlyInput(step);
            if (m_TryOn.Playing)
                need |= m_TryOn.Tick(step);
            if (wantFx)
                need |= m_TryOn.TickEffects(step);
            if (need)
                Repaint();
        }

        // ======================================================================
        // 数据构建
        // ======================================================================

        void Rebuild()
        {
            _entries.Clear();
            _groups.Clear();
            _groupList.Clear();
            _issues.Clear();
            _iconIndex.Clear();
            _mallByItem.Clear();
            _texCache.Clear();
            _texOrder.Clear();
            _modelSearchCache.Clear();

            BuildIconIndex();
            BuildMallIndex();

            var equipDic = Config_t_equip.Singleton.configDic;
            var itemArray = Config_t_item.Singleton.Array;
            if (itemArray == null)
            {
                Debug.LogError("[服装资源预览] t_item 配置加载失败");
                return;
            }

            var itemIds = new HashSet<uint>();
            foreach (var item in itemArray)
            {
                itemIds.Add(item.f_ItemID);
                if (item.f_Type1 != 0)
                    continue;

                var entry = new ClothEntry
                {
                    Id = item.f_ItemID,
                    Name = item.f_Name,
                    Type2 = item.f_Type2,
                    Sex = item.f_SexNeed,
                    GroupRaw = (item.f_GroupID ?? "").Trim(),
                    IconName = item.f_IconName,
                    Label = item.f_LabelType,
                    Charm = item.f_charm,
                    Description = item.f_Description,
                };
                entry.GroupIds = SplitGroupIds(entry.GroupRaw);
                equipDic.TryGetValue(item.f_ItemID, out var equip);
                entry.Equip = equip;
                if (!string.IsNullOrEmpty(entry.IconName) && _iconIndex.TryGetValue(entry.IconName, out var iconPath))
                    entry.IconPath = iconPath;

                _entries.Add(entry);
            }

            BuildSuitGroups();
            BuildIssues(itemIds);
            RebuildPartFilter();
            ApplyFilter();
        }

        static string[] SplitGroupIds(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return new string[0];
            return raw.Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToArray();
        }

        void BuildIconIndex()
        {
            foreach (var folder in new[] { IconFolderItem, IconFolderPinch })
            {
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".tga")
                        continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!_iconIndex.ContainsKey(name))
                        _iconIndex.Add(name, file.Replace('\\', '/'));
                }
            }
        }

        void BuildMallIndex()
        {
            var mallArray = Config_t_mall.Singleton.Array;
            if (mallArray == null)
                return;
            foreach (var mall in mallArray)
            {
                if (!_mallByItem.TryGetValue(mall.f_ItemID, out var list))
                {
                    list = new List<Item_t_mall>();
                    _mallByItem.Add(mall.f_ItemID, list);
                }
                list.Add(mall);
            }
        }

        void BuildSuitGroups()
        {
            foreach (var entry in _entries)
            {
                foreach (var gid in entry.GroupIds)
                {
                    if (!_groups.TryGetValue(gid, out var group))
                    {
                        group = new SuitGroup
                        {
                            Id = gid,
                            IsMale = gid.StartsWith("m_", StringComparison.OrdinalIgnoreCase),
                        };
                        _groups.Add(gid, group);
                    }
                    group.Entries.Add(entry);
                }
            }

            foreach (var group in _groups.Values)
                group.Name = DeriveSuitName(group.Entries, group.Id, group.IsMale);

            _groupList.AddRange(_groups.Values);
            _groupList.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        }

        /// <summary>
        /// 由成员名称推断套装名，例如「城市边缘·头发」→「城市边缘」。
        /// 优先只用同性别成员推导：共享配件（翅膀/耳环等）名字杂，混进来会把阈值顶掉。
        /// </summary>
        static string DeriveSuitName(List<ClothEntry> entries, string groupId, bool isMale)
        {
            if (entries.Count == 0)
                return groupId;

            var sameSex = entries.Where(e => isMale ? e.Sex == 0 : e.Sex == 1).ToList();
            var pool = sameSex.Count > 0 ? sameSex : entries;

            var seps = new[] { '·', '・', '/', '—', '－', '-' };
            var prefixCount = new Dictionary<string, int>();
            foreach (var e in pool)
            {
                var name = e.Name ?? "";
                var idx = name.LastIndexOfAny(seps);
                var prefix = idx > 0 ? name.Substring(0, idx) : name;
                prefixCount.TryGetValue(prefix, out var c);
                prefixCount[prefix] = c + 1;
            }

            var best = prefixCount.OrderByDescending(kv => kv.Value).First();
            if (best.Value >= 2 && best.Value * 2 >= pool.Count && !string.IsNullOrEmpty(best.Key))
                return best.Key;

            // 退回公共前缀，例如「初始发型 / 初始上衣」→「初始」
            var common = pool[0].Name ?? groupId;
            foreach (var e in pool)
            {
                var name = e.Name ?? "";
                var max = Math.Min(common.Length, name.Length);
                var i = 0;
                while (i < max && common[i] == name[i])
                    i++;
                common = common.Substring(0, i);
                if (common.Length == 0)
                    break;
            }

            if (common.Length >= 2)
                return common;

            return groupId;
        }

        void BuildIssues(HashSet<uint> itemIds)
        {
            var equipDic = Config_t_equip.Singleton.configDic;

            // 1. 服饰缺图标
            foreach (var e in _entries)
            {
                if (string.IsNullOrEmpty(e.IconName))
                    _issues.Add(new Issue { Type = "缺图标", ItemId = e.Id, Message = $"{e.Id} {e.Name}：f_IconName 为空" });
                else if (e.IconPath == null)
                    _issues.Add(new Issue { Type = "缺图标", ItemId = e.Id, Message = $"{e.Id} {e.Name}：图标 {e.IconName} 在 ItemIcon/PinchFaceIcon 中不存在" });
            }

            // 2. 服饰缺 t_equip 记录
            foreach (var e in _entries)
            {
                if (e.Equip != null)
                    continue;
                if (Array.IndexOf(EquipNotRequiredParts, e.Type2) >= 0)
                    continue;
                _issues.Add(new Issue { Type = "缺t_equip", ItemId = e.Id, Message = $"{e.Id} {e.Name}：t_equip 中无该装备记录" });
            }

            // 3. 套装只有一件
            foreach (var g in _groupList)
            {
                if (g.Entries.Count <= 1)
                    _issues.Add(new Issue { Type = "套装件数异常", ItemId = g.Entries[0].Id, Message = $"套装 {g.Id}（{g.Name}）只有 {g.Entries.Count} 件" });
            }

            // 4. t_equip 孤儿记录（有装备没道具）
            foreach (var kv in equipDic)
            {
                if (!itemIds.Contains(kv.Key))
                    _issues.Add(new Issue { Type = "t_equip孤儿", ItemId = kv.Key, Message = $"{kv.Key} {kv.Value.f_Name}：存在于 t_equip 但 t_item 中没有该道具" });
            }

            // 5. 图鉴套装引用了不存在的装备
            var entryIds = new HashSet<uint>(_entries.Select(x => x.Id));
            var pokedexArray = Config_t_pokedex.Singleton.Array;
            if (pokedexArray != null)
            {
                foreach (var dex in pokedexArray)
                {
                    foreach (var list in new[] { dex.f_MaleEquipList, dex.f_FemaleEquipList })
                    {
                        if (string.IsNullOrEmpty(list))
                            continue;
                        foreach (var token in list.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!uint.TryParse(token.Trim(), out var id))
                                continue;
                            if (!entryIds.Contains(id))
                                _issues.Add(new Issue { Type = "图鉴引用缺失", ItemId = id, Message = $"图鉴 {dex.f_PokedexID}（{dex.f_MalePokedexName}）引用的装备 {id} 在 t_item 服饰中不存在" });
                        }
                    }
                }
            }

            // 6. 全局提示：t_equip.f_SuitID 是否在用
            var suitIdUsed = equipDic.Values.Any(x => x.f_SuitID != 0);
            if (!suitIdUsed)
                _issues.Add(new Issue { Type = "全局", ItemId = 0, Message = "t_equip.f_SuitID 全表为 0：套装判定只能依赖 t_item.f_GroupID（本工具已按此口径分类）" });

            _issues.Sort((a, b) => string.CompareOrdinal(a.Type, b.Type));
        }

        void RebuildPartFilter()
        {
            var parts = _entries.Select(x => (int)x.Type2).Distinct().OrderBy(x => x).ToList();
            _partValues = new int[parts.Count + 1];
            _partLabels = new string[parts.Count + 1];
            _partValues[0] = -1;
            _partLabels[0] = "全部部位";
            for (var i = 0; i < parts.Count; i++)
            {
                _partValues[i + 1] = parts[i];
                _partLabels[i + 1] = $"{PartName(parts[i])}({parts[i]})";
            }
        }

        // ======================================================================
        // 过滤 / 排序
        // ======================================================================

        void ApplyFilter()
        {
            _viewList.Clear();
            var keyword = (_search ?? "").Trim();

            foreach (var e in _entries)
            {
                if (_category == Category.Suit && !e.HasSuit) continue;
                if (_category == Category.NoSuit && e.HasSuit) continue;
                if (_partFilter >= 0 && e.Type2 != _partFilter) continue;
                if (_sexFilter >= 0 && e.Sex != _sexFilter) continue;
                if (_onlyIssue && !IsIssueItem(e.Id)) continue;
                if (keyword.Length > 0 && !MatchKeyword(e, keyword)) continue;
                if (_starFilter >= 0 && e.Label != _starFilter) continue;

                if (_releaseFilter > 0 && _release != null)
                {
                    var rel = _release.Get(e.Id);
                    var released = rel != null && rel.released;
                    if (_releaseFilter == 1 && !released) continue;
                    if (_releaseFilter == 2 && released) continue;
                }

                _viewList.Add(e);
            }

            switch (_sort)
            {
                case SortMode.Name:
                    _viewList.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
                    break;
                case SortMode.Part:
                    _viewList.Sort((a, b) =>
                    {
                        var r = a.Type2.CompareTo(b.Type2);
                        return r != 0 ? r : a.Id.CompareTo(b.Id);
                    });
                    break;
                default:
                    _viewList.Sort((a, b) => a.Id.CompareTo(b.Id));
                    break;
            }
            BuildCards();
        }

        void BuildCards()
        {
            _viewCards.Clear();
            var emitted = new HashSet<string>();
            foreach (var e in _viewList)
            {
                if (e.GroupIds.Length == 0)
                {
                    _viewCards.Add(new ViewCard { Entry = e });
                    continue;
                }

                var gid = e.GroupIds[0];
                if (!emitted.Add(gid))
                    continue;
                _groups.TryGetValue(gid, out var g);
                var entry = g != null ? GetRepresentative(g) : e;
                _viewCards.Add(new ViewCard { Entry = entry, Group = g });
            }

            RefreshReleaseCounts();
        }

        void RefreshReleaseCounts()
        {
            _relReleased = 0;
            _relUnreleased = 0;
            _relOut = 0;
            if (_release == null)
                return;
            foreach (var e in _entries)
            {
                var rel = _release.Get(e.Id);
                if (rel == null)
                    _relOut++;
                else if (rel.released)
                    _relReleased++;
                else
                    _relUnreleased++;
            }
        }

        bool IsIssueItem(uint id)
        {
            for (var i = 0; i < _issues.Count; i++)
                if (_issues[i].ItemId == id)
                    return true;
            return false;
        }

        static bool MatchKeyword(ClothEntry e, string keyword)
        {
            if (e.Id.ToString().Contains(keyword)) return true;
            if (!string.IsNullOrEmpty(e.Name) && e.Name.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(e.GroupRaw) && e.GroupRaw.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(e.IconName) && e.IconName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (e.Equip != null && !string.IsNullOrEmpty(e.Equip.f_Model) && e.Equip.f_Model.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string PartName(ushort type2) => PartName((int)type2);

        static string PartName(int type2) => PartNames.TryGetValue(type2, out var name) ? name : $"部位{type2}";

        static string SexName(ushort sex)
        {
            switch (sex)
            {
                case 0: return "男";
                case 1: return "女";
                default: return "通用";
            }
        }

        static string CurrencyName(ushort currency)
        {
            switch (currency)
            {
                case 1: return "金币";
                case 2: return "点券";
                case 3: return "钻石";
                default: return $"货币{currency}";
            }
        }

        // ======================================================================
        // 图标加载
        // ======================================================================

        Texture2D GetIconTexture(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            if (_texCache.TryGetValue(path, out var cached))
            {
                _texOrder.Remove(path);
                _texOrder.Add(path);
                return cached;
            }

            // 淘汰最久未用的一批，避免整表清空导致换装后重新加载全部图标
            if (_texCache.Count >= TexCacheLimit)
            {
                var evict = Mathf.Min(200, _texOrder.Count);
                for (var k = 0; k < evict; k++)
                    _texCache.Remove(_texOrder[k]);
                _texOrder.RemoveRange(0, evict);
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            _texCache[path] = tex;
            _texOrder.Add(path);
            return tex;
        }

        Texture2D GetIconTexture(ClothEntry e) => GetIconTexture(e?.IconPath);

        // ======================================================================
        // 界面
        // ======================================================================

        void EnsureStyles()
        {
            if (_cellStyle != null)
                return;

            _cellStyle = new GUIStyle("HelpBox")
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                richText = false,
                wordWrap = true,
            };
            _cellNameStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
            };
            _cellMetaStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
            };
            _badgeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 9,
            };
            _sectionTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            _badgeReleased = new GUIStyle(_badgeStyle) { normal = { textColor = new Color(0.45f, 0.95f, 0.55f) } };
            _badgeUnreleased = new GUIStyle(_badgeStyle) { normal = { textColor = new Color(0.62f, 0.62f, 0.62f) } };
            _badgePartial = new GUIStyle(_badgeStyle) { normal = { textColor = new Color(1f, 0.78f, 0.28f) } };
            _badgeOut = new GUIStyle(_badgeStyle) { normal = { textColor = new Color(0.9f, 0.55f, 0.45f) } };
        }

        void OnGUI()
        {
            EnsureStyles();
            DrawToolbar();
            DrawFilterRow();

            if (_showIssues)
            {
                DrawIssuePanel();
            }
            else if (_view == ViewMode.Flat)
            {
                DrawFlatView();
            }
            else
            {
                DrawSuitView();
            }

            DrawStatusBar();
        }

        void DrawToolbar()
        {
            var suitCount = _entries.Count(x => x.HasSuit);
            var noSuitCount = _entries.Count - suitCount;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            var cat = GUILayout.Toolbar((int)_category,
                new[]
                {
                    $"全部 ({_entries.Count})",
                    $"有套装 ({suitCount} / {_groupList.Count}组)",
                    $"无套装 ({noSuitCount})",
                },
                EditorStyles.toolbarButton);
            if (cat != (int)_category)
            {
                _category = (Category)cat;
                _view = _category == Category.NoSuit ? ViewMode.Flat : _view;
                ApplyFilter();
            }

            GUILayout.FlexibleSpace();

            var view = GUILayout.Toolbar((int)_view,
                new[] { "单品平铺", "按套装分组" },
                EditorStyles.toolbarButton, GUILayout.Width(160f));
            if (view != (int)_view)
            {
                _view = (ViewMode)view;
                _showIssues = false;
            }

            var showIssues = GUILayout.Toggle(_showIssues, "配置校验", EditorStyles.toolbarButton, GUILayout.Width(76f));
            if (showIssues != _showIssues)
                _showIssues = showIssues;

            var showTryOn = GUILayout.Toggle(m_ShowTryOn, "3D试穿", EditorStyles.toolbarButton, GUILayout.Width(76f));
            if (showTryOn != m_ShowTryOn)
            {
                m_ShowTryOn = showTryOn;
                Repaint();
            }

            if (GUILayout.Button("重新加载", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                Rebuild();
            EditorGUILayout.EndHorizontal();
        }

        void DrawFilterRow()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginChangeCheck();
            var newSearch = GUILayout.TextField(_search ?? "", EditorStyles.toolbarSearchField, GUILayout.MinWidth(160f));
            if (EditorGUI.EndChangeCheck())
            {
                _search = newSearch;
                ApplyFilter();
            }

            if (_partValues.Length == 0)
                RebuildPartFilter();
            var newPart = EditorGUILayout.Popup(Mathf.Max(0, Array.IndexOf(_partValues, _partFilter)),
                _partLabels, EditorStyles.toolbarPopup, GUILayout.Width(130f));
            var partValue = _partValues[Mathf.Clamp(newPart, 0, _partValues.Length - 1)];
            if (partValue != _partFilter)
            {
                _partFilter = partValue;
                ApplyFilter();
            }

            var newSex = EditorGUILayout.Popup(_sexFilter + 1, new[] { "全部性别", "男", "女", "通用" },
                EditorStyles.toolbarPopup, GUILayout.Width(88f));
            if (newSex - 1 != _sexFilter)
            {
                _sexFilter = newSex - 1;
                ApplyFilter();
            }

            var newStar = EditorGUILayout.Popup(_starFilter + 1, StarFilterLabels,
                EditorStyles.toolbarPopup, GUILayout.Width(96f));
            if (newStar - 1 != _starFilter)
            {
                _starFilter = newStar - 1;
                ApplyFilter();
            }

            var newSort = (SortMode)EditorGUILayout.Popup((int)_sort, new[] { "按ID排序", "按名称排序", "按部位排序" },
                EditorStyles.toolbarPopup, GUILayout.Width(96f));
            if (newSort != _sort)
            {
                _sort = newSort;
                ApplyFilter();
            }

            var onlyIssue = GUILayout.Toggle(_onlyIssue, "只看异常", EditorStyles.toolbarButton, GUILayout.Width(72f));
            if (onlyIssue != _onlyIssue)
            {
                _onlyIssue = onlyIssue;
                ApplyFilter();
            }

            if (_release != null)
            {
                var newRel = EditorGUILayout.Popup(_releaseFilter, new[] { "投放:全部", "仅已投放", "仅未投放" },
                    EditorStyles.toolbarPopup, GUILayout.Width(96f));
                if (newRel != _releaseFilter)
                {
                    _releaseFilter = newRel;
                    ApplyFilter();
                }
            }

            if (GUILayout.Button(_release == null ? "导入投放表" : "重导投放表", EditorStyles.toolbarButton,
                    GUILayout.Width(84f)))
                ImportReleaseTable();

            if (!string.IsNullOrEmpty(_search) && GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(24f)))
            {
                _search = "";
                ApplyFilter();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ======================================================================
        // 3D 试穿
        // ======================================================================

        [SerializeField] float m_TryOnHeight = 460f;   // 3D试穿区高度，可在窗口内拖拽调整并记忆

        // 星级筛选选项；下标 = _starFilter + 1（-1→0），取值对应 t_item.f_LabelType：0未配置 1二星 2三星 3四星 4五星
        static readonly string[] StarFilterLabels =
        {
            "星级:全部", "未配置", "二星", "三星", "四星", "五星",
        };

        float TryOnOverhead() => m_ShowTryOn ? m_TryOnHeight + 32f : 0f;


        void EnsureTryOn()
        {
            if (m_TryOn == null)
            {
                m_TryOn = new ClothTryOnPreview();
                m_TryOn.OnRepaint = Repaint;
            }
        }

        void DrawTryOnPanel()
        {
            EnsureTryOn();
            var rect = GUILayoutUtility.GetRect(200f, m_TryOnHeight, GUILayout.ExpandWidth(true));
            m_TryOn.Draw(rect, "3D试穿 · 点击单品直接穿上，套装行点击穿整套");

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("恢复默认", EditorStyles.miniButton, GUILayout.Width(72f)))
            {
                m_TryOn.ResetAll();
                m_WornGroupId = null;
                Repaint();
            }

            if (GUILayout.Button(m_TryOn.Playing ? "暂停" : "播放", EditorStyles.miniButton, GUILayout.Width(52f)))
            {
                EnsureTryOn();
                m_LastEditorTick = EditorApplication.timeSinceStartup;
                m_TryOn.SetPlaying(!m_TryOn.Playing);
                Repaint();
            }

            if (GUILayout.Button("重播", EditorStyles.miniButton, GUILayout.Width(46f)))
            {
                m_TryOn.ResetTime();
                Repaint();
            }

            if (GUILayout.Button("重置视角", EditorStyles.miniButton, GUILayout.Width(64f)))
            {
                m_TryOn.Reframe();
                Repaint();
            }

            if (GUILayout.Button(m_TryOn.EffectsOn ? "特效:开" : "特效:关", EditorStyles.miniButton, GUILayout.Width(64f)))
            {
                m_TryOn.SetEffectsOn(!m_TryOn.EffectsOn);
                Repaint();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("男: " + m_TryOn.MaleSummary + "    女: " + m_TryOn.FemaleSummary,
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            DrawTryOnSplitter();
        }


        /// <summary>预览区与网格之间的拖拽条：上下拖动调整3D试穿区高度。</summary>
        void DrawTryOnSplitter()
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(6f), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.45f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);

            var e = Event.current;
            var id = GUIUtility.GetControlID("TryOnSplitter".GetHashCode(), FocusType.Passive);
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
                        var maxH = Mathf.Max(220f, position.height - 300f);
                        m_TryOnHeight = Mathf.Clamp(m_TryOnHeight + e.delta.y, 180f, maxH);
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
            }
        }

        void WearItem(ClothEntry e)
        {
            EnsureTryOn();
            var swItem = System.Diagnostics.Stopwatch.StartNew();
            m_TryOn.PutOnItem(e.Id);
            swItem.Stop();
            _lastWearMs = swItem.ElapsedMilliseconds;
            m_WornGroupId = null;
            Repaint();
        }

        void WearSuit(SuitGroup group)
        {
            EnsureTryOn();
            var ids = new List<uint>();
            foreach (var entry in group.Entries)
                ids.Add(entry.Id);

            // 对侧套装一起穿（男女同屏对比）：优先同编号 m_0039 ↔ f_0039，其次同名
            var baseId = group.Id.StartsWith("m_") || group.Id.StartsWith("f_") ? group.Id.Substring(2) : group.Id;
            foreach (var other in _groupList)
            {
                if (ReferenceEquals(other, group))
                    continue;
                if (other.IsMale == group.IsMale)
                    continue;
                var otherBase = other.Id.StartsWith("m_") || other.Id.StartsWith("f_") ? other.Id.Substring(2) : other.Id;
                if (otherBase != baseId && other.Name != group.Name)
                    continue;
                foreach (var entry in other.Entries)
                    ids.Add(entry.Id);
            }

            var swWear = System.Diagnostics.Stopwatch.StartNew();
            m_TryOn.PutOnSuite(group.Name, ids);
            swWear.Stop();
            _lastWearMs = swWear.ElapsedMilliseconds;
            m_WornGroupId = group.Id;
            Repaint();
        }

        /// <summary>与该件联动的道具：自身 + 同编号套装（m_0007 ↔ f_0007）全部成员，男女两侧一起开关。</summary>
        List<uint> GetFxLinkedIds(uint id)
        {
            var result = new List<uint> { id };
            ClothEntry entry = null;
            foreach (var e in _entries)
            {
                if (e.Id != id)
                    continue;
                entry = e;
                break;
            }

            if (entry == null || entry.GroupIds.Length == 0)
                return result;

            var bases = new HashSet<string>();
            foreach (var gid in entry.GroupIds)
                bases.Add(gid.StartsWith("m_") || gid.StartsWith("f_") ? gid.Substring(2) : gid);

            foreach (var g in _groupList)
            {
                var gb = g.Id.StartsWith("m_") || g.Id.StartsWith("f_") ? g.Id.Substring(2) : g.Id;
                if (!bases.Contains(gb))
                    continue;
                foreach (var member in g.Entries)
                {
                    if (!result.Contains(member.Id))
                        result.Add(member.Id);
                }
            }

            return result;
        }

        string ReleaseText(uint id)
        {
            if (_release == null)
                return "未导入投放表";
            var rel = _release.Get(id);
            if (rel == null)
                return "不在投放表中（该件未收录）";
            return rel.released
                ? $"已投放（{(string.IsNullOrEmpty(rel.channel) ? "渠道未填" : rel.channel)}）"
                : "未投放";
        }

        void ImportReleaseTable()
        {
            var last = EditorPrefs.GetString("ClothReleaseXlsxPath", "");
            var dir = string.IsNullOrEmpty(last) ? "" : (Path.GetDirectoryName(last) ?? "");
            var path = EditorUtility.OpenFilePanel("导入投放表（已完成服装统计_本地.xlsx）", dir, "xlsx");
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                var data = ClothReleaseData.Import(path, out var summary);
                _release = data;
                EditorPrefs.SetString("ClothReleaseXlsxPath", path);
                ApplyFilter();
                Repaint();
                ShowNotification(new GUIContent("投放表已导入 " + summary));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("导入失败", ex.Message, "确定");
            }
        }

        void DrawFlatView()
        {
            EditorGUILayout.BeginHorizontal();

            // ---------- 左列：试穿预览 + 虚拟化网格 ----------
            EditorGUILayout.BeginVertical();
            if (m_ShowTryOn)
                DrawTryOnPanel();
            _gridScroll = EditorGUILayout.BeginScrollView(_gridScroll);
            var gridWidth = Mathf.Max(position.width - DetailWidth - 24f, 200f);
            var columns = Mathf.Max(1, Mathf.FloorToInt((gridWidth - 12f) / CellWidth));
            var rows = Mathf.Max(1, Mathf.CeilToInt(_viewCards.Count / (float)columns));
            var contentHeight = rows * CellHeight + 8f;
            var area = GUILayoutUtility.GetRect(gridWidth, contentHeight, GUILayout.ExpandWidth(true));

            var viewHeight = Mathf.Max(position.height - 130f - TryOnOverhead(), 160f);
            var firstRow = Mathf.Max(0, Mathf.FloorToInt(_gridScroll.y / CellHeight) - 1);
            var lastRow = Mathf.Min(rows - 1, Mathf.FloorToInt((_gridScroll.y + viewHeight) / CellHeight) + 1);

            if (_viewCards.Count == 0)
            {
                GUILayout.Label("当前筛选条件下没有服装", EditorStyles.centeredGreyMiniLabel);
            }

            for (var row = firstRow; row <= lastRow; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var index = row * columns + col;
                    if (index >= _viewCards.Count)
                        break;
                    var rect = new Rect(area.x + col * CellWidth + 3f, area.y + row * CellHeight + 3f,
                        CellWidth - 6f, CellHeight - 6f);
                    DrawCard(rect, _viewCards[index]);
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // ---------- 右：详情 ----------
            EditorGUILayout.BeginVertical(GUILayout.Width(DetailWidth));
            DrawDetailPanel();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        void DrawCard(Rect rect, ViewCard card)
        {
            if (card.Group != null)
                DrawSuiteCard(rect, card);
            else
                DrawSingleCard(rect, card.Entry);
        }

        void DrawReleaseBadge(Rect rect, ClothEntry entry, SuitGroup group)
        {
            if (!TryGetReleaseBadge(entry, group, out var text, out var style))
                return;
            GUI.Label(new Rect(rect.xMax - 56f, rect.y, 52f, 16f), text, style);
        }

        bool TryGetReleaseBadge(ClothEntry entry, SuitGroup group, out string text, out GUIStyle style)
        {
            text = null;
            style = null;
            if (_release == null)
                return false;

            if (group == null)
            {
                var rel = _release.Get(entry.Id);
                if (rel == null)
                {
                    text = "表外";
                    style = _badgeOut;
                }
                else if (rel.released)
                {
                    text = "已投放";
                    style = _badgeReleased;
                }
                else
                {
                    text = "未投放";
                    style = _badgeUnreleased;
                }

                return true;
            }

            var total = group.Entries.Count;
            var releasedN = 0;
            var inTable = 0;
            foreach (var m in group.Entries)
            {
                var rel = _release.Get(m.Id);
                if (rel == null)
                    continue;
                inTable++;
                if (rel.released)
                    releasedN++;
            }

            if (total > 0 && releasedN >= total)
            {
                text = "已投放";
                style = _badgeReleased;
            }
            else if (releasedN > 0)
            {
                text = $"部分{releasedN}/{total}";
                style = _badgePartial;
            }
            else if (inTable > 0)
            {
                text = "未投放";
                style = _badgeUnreleased;
            }
            else
            {
                text = "表外";
                style = _badgeOut;
            }

            return true;
        }

        void DrawSingleCard(Rect rect, ClothEntry e)
        {
            var selected = e.Id == _selectedId;
            if (selected)
                EditorGUI.DrawRect(rect, new Color(0.23f, 0.48f, 0.90f, 0.30f));

            var bg = new Rect(rect.x, rect.y, rect.width, 16f);
            EditorGUI.DrawRect(bg, new Color(0.15f, 0.15f, 0.15f, 0.35f));
            GUI.Label(bg, "单品", _badgeStyle);
            DrawReleaseBadge(rect, e, null);

            var iconRect = new Rect(rect.x + 6f, rect.y + 20f, rect.width - 12f, 64f);
            var tex = GetIconTexture(e);
            if (tex != null)
                GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
            else
                GUI.Label(iconRect, "无图标", EditorStyles.centeredGreyMiniLabel);
            if (m_TryOn != null && m_TryOn.ItemHasEffects(e.Id))
                GUI.Label(new Rect(iconRect.x + 2f, iconRect.y + 2f, 22f, 14f), "FX", _badgePartial);

            var nameRect = new Rect(rect.x + 4f, rect.y + 86f, rect.width - 8f, 30f);
            GUI.Label(nameRect, string.IsNullOrEmpty(e.Name) ? e.Id.ToString() : e.Name, _cellNameStyle);

            var metaRect = new Rect(rect.x + 4f, rect.y + 104f, rect.width - 8f, 24f);
            GUI.Label(metaRect, $"{PartName(e.Type2)} · {e.Id}\n无套装", _cellMetaStyle);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
            {
                _selectedId = e.Id;
                WearItem(e);
            }
        }

        void DrawSuiteCard(Rect rect, ViewCard card)
        {
            var group = card.Group;
            var selected = card.Entry.Id == _selectedId;
            var worn = m_WornGroupId == group.Id;
            if (worn || selected)
                EditorGUI.DrawRect(rect, new Color(0.23f, 0.49f, 0.90f, 0.30f));

            var bg = new Rect(rect.x, rect.y, rect.width, 16f);
            EditorGUI.DrawRect(bg, new Color(0.55f, 0.40f, 0.08f, 0.55f));
            GUI.Label(bg, worn ? "套装·已穿" : "套装", _badgeStyle);
            DrawReleaseBadge(rect, card.Entry, group);

            var iconRect = new Rect(rect.x + 6f, rect.y + 20f, rect.width - 12f, 64f);
            var tex = GetIconTexture(card.Entry);
            if (tex != null)
                GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
            else
                GUI.Label(iconRect, "无图标", EditorStyles.centeredGreyMiniLabel);
            if (m_TryOn != null)
            {
                foreach (var member in group.Entries)
                {
                    if (!m_TryOn.ItemHasEffects(member.Id))
                        continue;
                    GUI.Label(new Rect(iconRect.x + 2f, iconRect.y + 2f, 22f, 14f), "FX", _badgePartial);
                    break;
                }
            }

            var nameRect = new Rect(rect.x + 4f, rect.y + 86f, rect.width - 8f, 30f);
            GUI.Label(nameRect, string.IsNullOrEmpty(group.Name) ? group.Id : group.Name, _cellNameStyle);

            var metaRect = new Rect(rect.x + 4f, rect.y + 104f, rect.width - 8f, 24f);
            GUI.Label(metaRect, $"{group.Id} · {group.Entries.Count}件 · {group.PartsSummary}", _cellMetaStyle);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
            {
                _selectedId = card.Entry.Id;
                WearSuit(group);
            }
        }

        // ======================================================================
        // 套装分组视图
        // ======================================================================

        void DrawSuitView()
        {
            if (_category == Category.NoSuit)
            {
                EditorGUILayout.HelpBox("「无套装」的服装没有套装分组，请切换到单品平铺视图。", MessageType.Info);
                return;
            }

            var keyword = (_search ?? "").Trim();
            var list = _groupList.AsEnumerable();
            if (!string.IsNullOrEmpty(keyword))
                list = list.Where(g => g.Id.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                                    || g.Name.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0
                                    || g.Entries.Any(e => MatchKeyword(e, keyword)));
            if (_partFilter >= 0)
                list = list.Where(g => g.Entries.Any(e => e.Type2 == _partFilter));
            if (_sexFilter >= 0)
                list = list.Where(g => g.Entries.Any(e => e.Sex == _sexFilter));
            if (_onlyIssue)
                list = list.Where(g => g.Entries.Any(e => IsIssueItem(e.Id)));
            var groups = list.ToList();

            EditorGUILayout.BeginVertical();
            if (m_ShowTryOn)
                DrawTryOnPanel();
            _suitScroll = EditorGUILayout.BeginScrollView(_suitScroll);
            var contentHeight = Mathf.Max(1, groups.Count) * SuitRowHeight + 8f;
            var area = GUILayoutUtility.GetRect(position.width - 30f, contentHeight, GUILayout.ExpandWidth(true));
            var viewHeight = Mathf.Max(position.height - 130f - TryOnOverhead(), 160f);
            var firstRow = Mathf.Max(0, Mathf.FloorToInt(_suitScroll.y / SuitRowHeight) - 1);
            var lastRow = Mathf.Min(groups.Count - 1, Mathf.FloorToInt((_suitScroll.y + viewHeight) / SuitRowHeight) + 1);

            if (groups.Count == 0)
                GUILayout.Label("没有匹配的套装", EditorStyles.centeredGreyMiniLabel);

            for (var row = firstRow; row <= lastRow; row++)
            {
                DrawSuitRow(new Rect(area.x + 4f, area.y + row * SuitRowHeight + 3f, area.width - 8f, SuitRowHeight - 6f),
                    groups[row]);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void DrawSuitRow(Rect rect, SuitGroup group)
        {
            var worn = m_WornGroupId == group.Id;
            if (worn)
                EditorGUI.DrawRect(rect, new Color(0.23f, 0.49f, 0.90f, 0.30f));
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, worn ? 0.10f : 0.18f));

            // 整行点击 = 穿整套
            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
            {
                _selectedId = group.Entries[0].Id;
                WearSuit(group);
            }

            var labelRect = new Rect(rect.x + 8f, rect.y + 4f, 236f, rect.height - 8f);
            var title = group.Name + (worn ? "  ●已穿" : "");
            var sub = $"{group.Id} · {group.Entries.Count}件 · {group.PartsSummary}";
            GUI.Label(labelRect, title + "\n" + sub, _cellNameStyle);

            var badgeRect = new Rect(labelRect.xMax + 4f, rect.y + 4f, 34f, rect.height - 8f);
            GUI.Label(badgeRect, group.IsMale ? "男" : "女", _badgeStyle);

            // 只显示主件图标（上衣/连身优先），点击即穿整套
            var rep = GetRepresentative(group);
            var iconRect = new Rect(rect.xMax - 206f, rect.y + 6f, rect.height - 12f, rect.height - 12f);
            if (rep != null)
            {
                var tex = GetIconTexture(rep);
                if (tex != null)
                    GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
                else
                    GUI.Label(iconRect, "?", EditorStyles.centeredGreyMiniLabel);
                if (GUI.Button(iconRect, GUIContent.none, GUIStyle.none))
                {
                    _selectedId = rep.Id;
                    WearSuit(group);
                }
            }

            var mainRect = new Rect(rect.xMax - 148f, rect.y + 4f, 84f, rect.height - 8f);
            GUI.Label(mainRect, rep != null ? $"{PartName(rep.Type2)}·{rep.Id}" : "", _cellMetaStyle);

            var onlyRect = new Rect(rect.xMax - 60f, rect.y + 14f, 54f, rect.height - 28f);
            if (GUI.Button(onlyRect, "只看", EditorStyles.miniButton))
            {
                _search = group.Id;
                _view = ViewMode.Flat;
                _showIssues = false;
                ApplyFilter();
                Repaint();
            }
        }

        static ClothEntry GetRepresentative(SuitGroup group)
        {
            foreach (var part in new[] { 1, 4, 5, 6 })
            {
                foreach (var e in group.Entries)
                {
                    if (e.Type2 == part)
                        return e;
                }
            }

            return group.Entries.Count > 0 ? group.Entries[0] : null;
        }

        // ======================================================================
        // 详情面板
        // ======================================================================

        void DrawDetailPanel()
        {
            ClothEntry selected = null;
            foreach (var e in _entries)
            {
                if (e.Id == _selectedId)
                {
                    selected = e;
                    break;
                }
            }

            EditorGUILayout.LabelField("详情", _sectionTitle);
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

            if (selected == null)
            {
                EditorGUILayout.HelpBox("点击左侧图标查看详情。\n\n分类口径：\n· 有套装 = t_item.f_GroupID 非空\n· t_equip.f_SuitID 当前全表为 0，不参与判定", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            // 大图
            var rect = GUILayoutUtility.GetRect(DetailWidth - 20f, 150f);
            var tex = GetIconTexture(selected);
            if (tex != null)
                GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
            else
                GUI.Label(rect, "图标缺失", EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(string.IsNullOrEmpty(selected.Name) ? selected.Id.ToString() : selected.Name, EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("穿上这件", EditorStyles.miniButton))
                    WearItem(selected);

                SuitGroup suit = null;
                foreach (var gid in selected.GroupIds)
                {
                    if (_groups.TryGetValue(gid, out suit))
                        break;
                }

                using (new EditorGUI.DisabledScope(suit == null))
                {
                    if (GUILayout.Button("穿整套", EditorStyles.miniButton))
                    {
                        _selectedId = suit.Entries[0].Id;
                        WearSuit(suit);
                    }
                }

                using (new EditorGUI.DisabledScope(m_TryOn == null))
                {
                    if (GUILayout.Button("恢复默认", EditorStyles.miniButton))
                    {
                        m_TryOn.ResetAll();
                        m_WornGroupId = null;
                        Repaint();
                    }
                }
            }

            // 试穿中的整套：列出所有部位与对应ID
            if (!string.IsNullOrEmpty(m_WornGroupId) && _groups.TryGetValue(m_WornGroupId, out var wornGroup))
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField($"试穿中：{wornGroup.Name}", _sectionTitle);
                foreach (var g in _groupList)
                {
                    if (!ReferenceEquals(g, wornGroup) && !(g.IsMale != wornGroup.IsMale && g.Name == wornGroup.Name))
                        continue;
                    foreach (var member in g.Entries)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label((g.IsMale ? "男·" : "女·") + PartName(member.Type2), GUILayout.Width(78f));
                            var relWorn = _release?.Get(member.Id);
                            var mark = relWorn != null && relWorn.released ? " ✓" : "";
                            if (GUILayout.Button($"{member.Name}（{member.Id}）{mark}", EditorStyles.miniButton))
                            {
                                _selectedId = member.Id;
                                Repaint();
                            }
                        }
                    }
                }
            }

            EditorGUI.BeginChangeCheck();
            var newId = EditorGUILayout.DelayedIntField("道具ID", (int)selected.Id);
            if (EditorGUI.EndChangeCheck() && newId != selected.Id && newId > 0)
                SelectById((uint)newId);

            EditorGUILayout.LabelField("部位", $"{PartName(selected.Type2)}（{selected.Type2}）");
            EditorGUILayout.LabelField("性别", SexName(selected.Sex));
            EditorGUILayout.LabelField("星级", selected.Label > 0 ? new string('★', selected.Label) : "未配置");
            EditorGUILayout.LabelField("魅力值", selected.Charm.ToString());
            EditorGUILayout.LabelField("套装ID", string.IsNullOrEmpty(selected.GroupRaw) ? "（无，单品）" : selected.GroupRaw);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("套装状态", GUILayout.Width(120f));
                GUILayout.Label(selected.HasSuit
                    ? new GUIContent("有套装", "该道具属于套装组")
                    : new GUIContent("无套装", "该道具不在任何套装组"), selected.HasSuit ? EditorStyles.boldLabel : EditorStyles.label);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("投放状态", GUILayout.Width(120f));
                GUILayout.Label(ReleaseText(selected.Id), EditorStyles.label);
            }

            // 商城配置
            if (_mallByItem.TryGetValue(selected.Id, out var mallRows) && mallRows.Count > 0)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("商城配置", _sectionTitle);
                foreach (var m in mallRows)
                {
                    var price = m.f_NotForSale == 1
                        ? "非卖"
                        : $"{m.f_Price3} {CurrencyName(m.f_Currency)}（永久）/ {m.f_Price1} 七天 / {m.f_Price2} 三十天";
                    EditorGUILayout.LabelField($"[{m.f_MallType}]", price);
                    if (m.f_Discount > 0)
                        EditorGUILayout.LabelField("折扣", m.f_Discount + "%");
                }
            }
            else
            {
                EditorGUILayout.LabelField("商城配置", "未在 t_mall 中上架");
            }

            // t_equip 配置
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("t_equip 配置", _sectionTitle);
            if (selected.Equip != null)
            {
                EditorGUILayout.LabelField("模型", string.IsNullOrEmpty(selected.Equip.f_Model) ? "（空）" : selected.Equip.f_Model);
                EditorGUILayout.LabelField("贴图", string.IsNullOrEmpty(selected.Equip.f_MainTexture) ? "（空）" : selected.Equip.f_MainTexture);
                EditorGUILayout.LabelField("换色", string.IsNullOrEmpty(selected.Equip.f_OverlyColor) ? "（空）" : selected.Equip.f_OverlyColor);
                EditorGUILayout.LabelField("套装编号", selected.Equip.f_SuitID == 0 ? "0（未配置）" : selected.Equip.f_SuitID.ToString());
                EditorGUILayout.LabelField("预览位置", string.IsNullOrEmpty(selected.Equip.f_PreviewLocation) ? "（空）" : selected.Equip.f_PreviewLocation);
                EditorGUILayout.LabelField("相机参数", string.IsNullOrEmpty(selected.Equip.f_CamInfo) ? "（空）" : selected.Equip.f_CamInfo);
                EditorGUILayout.LabelField("待机动画", string.IsNullOrEmpty(selected.Equip.f_StandbyAction) ? "（空）" : selected.Equip.f_StandbyAction);
            EditorGUILayout.LabelField("特效开关", selected.Equip == null ? "-" : (selected.Equip.f_CanEffect == 1 ? "显示（f_CanEffect=1）" : "隐藏（f_CanEffect=0）"));

            // 按件试穿特效开关：只有配置了特效的衣服显示
            if (m_TryOn != null && m_TryOn.ItemHasEffects(selected.Id))
            {
                var fxOn = m_TryOn.IsItemFxEnabled(selected.Id);
                var fxNew = EditorGUILayout.Toggle(new GUIContent("试穿特效", "关闭/打开时男女同编号套装一起生效"), fxOn);
                if (fxNew != fxOn)
                {
                    m_TryOn.SetItemFxEnabled(GetFxLinkedIds(selected.Id), fxNew);
                    Repaint();
                }
            }
            }
            else
            {
                EditorGUILayout.HelpBox("t_equip 中没有这条装备记录", MessageType.Warning);
            }

            // 资源定位
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("资源定位", _sectionTitle);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(selected.IconPath)))
                {
                    if (GUILayout.Button("定位图标"))
                        PingAsset(selected.IconPath);
                }
                using (new EditorGUI.DisabledScope(selected.Equip == null || string.IsNullOrEmpty(selected.Equip.f_Model)))
                {
                    if (GUILayout.Button("定位模型"))
                        PingModel(selected.Equip?.f_Model);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("复制道具ID"))
                    EditorGUIUtility.systemCopyBuffer = selected.Id.ToString();
                if (GUILayout.Button("复制详情"))
                    EditorGUIUtility.systemCopyBuffer = BuildDetailText(selected);
            }

            // 同组部件
            if (selected.GroupIds.Length > 0)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("同套装部件", _sectionTitle);
                foreach (var gid in selected.GroupIds)
                {
                    if (!_groups.TryGetValue(gid, out var group))
                        continue;
                    EditorGUILayout.LabelField(gid, $"{group.Name} · {group.Entries.Count}件", EditorStyles.miniBoldLabel);
                    foreach (var member in group.Entries)
                    {
                        var isCurrent = member.Id == selected.Id;
                        var label = $"{(isCurrent ? "▶ " : "")}{PartName(member.Type2)} {member.Id} {member.Name}";
                        var relMember = _release?.Get(member.Id);
                        if (relMember != null && relMember.released)
                            label += " ✓";
                        if (GUILayout.Button(label, EditorStyles.miniButton))
                        {
                            _selectedId = member.Id;
                            WearItem(member);
                        }
                    }
                }
            }

            // 描述
            if (!string.IsNullOrEmpty(selected.Description))
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("描述", _sectionTitle);
                EditorGUILayout.HelpBox(selected.Description, MessageType.None);
            }

            EditorGUILayout.EndScrollView();
        }

        void SelectById(uint id)
        {
            _selectedId = id;
            _category = Category.All;
            _partFilter = -1;
            _sexFilter = -1;
            _search = id.ToString();
            _showIssues = false;
            ApplyFilter();
            Repaint();
        }

        void PingAsset(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                Debug.LogWarning($"[服装资源预览] 资源不存在: {path}");
                return;
            }
            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }

        void PingModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return;
            // 模型可能带左右耳之类的后缀，先按完整名找
            if (!_modelSearchCache.TryGetValue(modelName, out var guids))
            {
                var filter = modelName.Split('|')[0].Trim();
                guids = AssetDatabase.FindAssets(filter + " t:Prefab", new[] { "Assets/<你的工程>" }).ToList();
                if (guids.Count == 0)
                    guids = AssetDatabase.FindAssets(filter + " t:Model", new[] { "Assets/<你的工程>" }).ToList();
                _modelSearchCache[modelName] = guids;
            }

            if (guids.Count == 0)
            {
                Debug.LogWarning($"[服装资源预览] 未找到模型资源: {modelName}");
                return;
            }

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            PingAsset(path);
        }

        static string BuildDetailText(ClothEntry e)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"道具ID: {e.Id}");
            sb.AppendLine($"名称: {e.Name}");
            sb.AppendLine($"部位: {PartName(e.Type2)}({e.Type2})");
            sb.AppendLine($"性别: {SexName(e.Sex)}");
            sb.AppendLine($"套装ID: {(string.IsNullOrEmpty(e.GroupRaw) ? "无" : e.GroupRaw)}");
            sb.AppendLine($"图标: {e.IconName} -> {(e.IconPath ?? "缺失")}");
            if (e.Equip != null)
            {
                sb.AppendLine($"模型: {e.Equip.f_Model}");
                sb.AppendLine($"贴图: {e.Equip.f_MainTexture}");
                sb.AppendLine($"预览位置: {e.Equip.f_PreviewLocation}");
                sb.AppendLine($"相机参数: {e.Equip.f_CamInfo}");
            }
            else
            {
                sb.AppendLine("t_equip: 缺失");
            }
            return sb.ToString();
        }

        // ======================================================================
        // 校验报告
        // ======================================================================

        void DrawIssuePanel()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"配置校验（{_issues.Count} 条）", _sectionTitle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("复制报告", GUILayout.Width(80f)))
            {
                EditorGUIUtility.systemCopyBuffer = BuildIssueText();
                ShowNotification(new GUIContent("已复制到剪贴板"));
            }
            if (GUILayout.Button("导出报告", GUILayout.Width(80f)))
                ExportIssues();
            if (GUILayout.Button("关闭", GUILayout.Width(60f)))
                _showIssues = false;
            EditorGUILayout.EndHorizontal();

            var byType = _issues.GroupBy(x => x.Type).OrderBy(x => x.Key);
            _issueScroll = EditorGUILayout.BeginScrollView(_issueScroll);
            foreach (var typeGroup in byType)
            {
                EditorGUILayout.LabelField($"{typeGroup.Key}（{typeGroup.Count()}）", EditorStyles.boldLabel);
                foreach (var issue in typeGroup)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (issue.HasItem && GUILayout.Button(issue.ItemId.ToString(), EditorStyles.miniButton, GUILayout.Width(84f)))
                        {
                            SelectById(issue.ItemId);
                            _showIssues = false;
                            return;
                        }
                        EditorGUILayout.LabelField(issue.Message, EditorStyles.wordWrappedLabel);
                    }
                }
                EditorGUILayout.Space(4f);
            }
            EditorGUILayout.EndScrollView();
        }

        string BuildIssueText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"服装配置校验报告  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"数据源: t_item / t_equip / t_mall / t_pokedex");
            sb.AppendLine($"服饰总数: {_entries.Count}  有套装: {_entries.Count(x => x.HasSuit)}  无套装: {_entries.Count(x => !x.HasSuit)}  套装组: {_groupList.Count}");
            sb.AppendLine($"问题总数: {_issues.Count}");
            sb.AppendLine(new string('-', 60));
            foreach (var typeGroup in _issues.GroupBy(x => x.Type).OrderBy(x => x.Key))
            {
                sb.AppendLine($"[{typeGroup.Key}] {typeGroup.Count()} 条");
                foreach (var issue in typeGroup)
                    sb.AppendLine("  " + issue.Message);
            }
            return sb.ToString();
        }

        void ExportIssues()
        {
            var path = EditorUtility.SaveFilePanel("导出校验报告", "", $"服装配置校验_{DateTime.Now:yyyyMMdd_HHmmss}", "txt");
            if (string.IsNullOrEmpty(path))
                return;
            File.WriteAllText(path, BuildIssueText(), new UTF8Encoding(true));
            EditorUtility.RevealInFinder(path);
        }

        void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            var selectedName = "";
            foreach (var e in _entries)
            {
                if (e.Id == _selectedId)
                {
                    selectedName = $"{e.Id} {e.Name}";
                    break;
                }
            }
            GUILayout.Label($"显示 {_viewCards.Count} 张卡片（命中 {_viewList.Count} 件）· 套装组 {_groupList.Count} · 校验 {_issues.Count}", EditorStyles.miniLabel);
            if (_release != null)
                GUILayout.Label($"投放: 已投{_relReleased} · 未投{_relUnreleased} · 表外{_relOut}（{_release.ImportTime}导入）", EditorStyles.miniLabel);
            GUILayout.Label($"上次换装 {_lastWearMs}ms", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(selectedName))
                GUILayout.Label("选中: " + selectedName, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }
    }
}
