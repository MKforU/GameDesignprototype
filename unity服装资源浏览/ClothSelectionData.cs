using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GameEditor
{
    [Serializable]
    public class ClothSelectionEntry
    {
        public int id;
        public string name;
        public string dest;   // 去向；空 = 未定
    }

    [Serializable]
    public class ClothSelectionCache
    {
        public string sourcePath;
        public string importTime;
        public ClothSelectionEntry[] entries;
    }

    /// <summary>
    /// 已选服装去向记录（CSV：套装,部位,男装/女装,具体套装,ID,名称,星级,去向）。
    /// 表里出现的 ID = 已选；去向非空 = 释出方向已定。
    /// 结果缓存在工程 Library 下，支持重复导入更新。
    /// </summary>
    public class ClothSelectionData
    {
        public string SourcePath;
        public string ImportTime;

        readonly Dictionary<uint, ClothSelectionEntry> m_Map = new Dictionary<uint, ClothSelectionEntry>();

        public int Count => m_Map.Count;

        public int WithDestCount
        {
            get
            {
                var n = 0;
                foreach (var e in m_Map.Values)
                    if (!string.IsNullOrEmpty(e.dest))
                        n++;
                return n;
            }
        }

        public static string CachePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/ClothSelectionStatus.json"));

        public ClothSelectionEntry Get(uint id)
        {
            return m_Map.TryGetValue(id, out var e) ? e : null;
        }

        public bool IsSelected(uint id) => m_Map.ContainsKey(id);

        public static ClothSelectionData LoadCache()
        {
            try
            {
                var path = CachePath;
                if (!File.Exists(path))
                    return null;
                var cache = JsonUtility.FromJson<ClothSelectionCache>(File.ReadAllText(path));
                if (cache?.entries == null)
                    return null;
                var data = new ClothSelectionData
                {
                    SourcePath = cache.sourcePath,
                    ImportTime = cache.importTime,
                };
                foreach (var e in cache.entries)
                    data.m_Map[(uint)e.id] = e;
                return data.Count > 0 ? data : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[服装试穿] 读取已选缓存失败: {ex.Message}");
                return null;
            }
        }

        public void SaveCache()
        {
            try
            {
                var list = new List<ClothSelectionEntry>(m_Map.Values);
                list.Sort((a, b) => a.id.CompareTo(b.id));
                var cache = new ClothSelectionCache
                {
                    sourcePath = SourcePath,
                    importTime = ImportTime,
                    entries = list.ToArray(),
                };
                var path = CachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(cache, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[服装试穿] 写入已选缓存失败: {ex.Message}");
            }
        }

        /// <summary>导入 CSV，返回导入摘要。</summary>
        public static ClothSelectionData Import(string path, out string summary)
        {
            var data = new ClothSelectionData
            {
                SourcePath = path,
                ImportTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var withDest = 0;
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var v = line.Split(',');
                if (v.Length < 5)
                    continue;
                if (!uint.TryParse(v[4].Trim(), out var id))
                    continue;

                var name = v.Length > 5 ? v[5].Trim() : "";
                var dest = v.Length > 7 ? v[7].Trim() : "";

                if (data.m_Map.TryGetValue(id, out var old))
                {
                    // 同ID多行：去向取非空的
                    if (string.IsNullOrEmpty(old.dest) && !string.IsNullOrEmpty(dest))
                    {
                        old.dest = dest;
                        withDest++;
                    }

                    continue;
                }

                if (!string.IsNullOrEmpty(dest))
                    withDest++;
                data.m_Map.Add(id, new ClothSelectionEntry { id = (int)id, name = name, dest = dest });
            }

            if (data.Count == 0)
                throw new InvalidDataException("CSV 里没有识别到道具ID（第5列）");

            data.SaveCache();
            summary = $"共已选{data.Count}件，去向已定{withDest}件，未定{data.Count - withDest}件";
            return data;
        }
    }
}
