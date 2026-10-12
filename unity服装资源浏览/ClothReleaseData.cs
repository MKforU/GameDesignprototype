using System;
using System.Collections.Generic;
using System.IO;
using OfficeOpenXml;
using UnityEngine;

namespace GameEditor
{
    [Serializable]
    public class ClothReleaseEntry
    {
        public int id;
        public bool released;
        public string channel;
    }

    [Serializable]
    public class ClothReleaseCache
    {
        public string sourcePath;
        public string importTime;
        public ClothReleaseEntry[] entries;
    }

    /// <summary>
    /// 投放表（已完成服装统计_本地.xlsx）导入：
    /// 「服饰套装」表中 E=男件ID、L=男件投放；P=女件ID、W=女件投放。
    /// L/W 有数据即视为已对外，内容作为投放渠道保留。
    /// 结果缓存在 Library 下，窗口打开自动加载，支持反复重导。
    /// </summary>
    public class ClothReleaseData
    {
        public string SourcePath;
        public string ImportTime;

        readonly Dictionary<uint, ClothReleaseEntry> m_Map = new Dictionary<uint, ClothReleaseEntry>();

        public int Count => m_Map.Count;

        public int ReleasedCount
        {
            get
            {
                var n = 0;
                foreach (var e in m_Map.Values)
                    if (e.released)
                        n++;
                return n;
            }
        }

        public static string CachePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/ClothReleaseStatus.json"));

        public ClothReleaseEntry Get(uint id)
        {
            return m_Map.TryGetValue(id, out var e) ? e : null;
        }

        public static ClothReleaseData LoadCache()
        {
            try
            {
                var path = CachePath;
                if (!File.Exists(path))
                    return null;
                var cache = JsonUtility.FromJson<ClothReleaseCache>(File.ReadAllText(path));
                if (cache?.entries == null)
                    return null;
                var data = new ClothReleaseData
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
                Debug.LogWarning($"[服装试穿] 读取投放缓存失败: {ex.Message}");
                return null;
            }
        }

        public void SaveCache()
        {
            try
            {
                var list = new List<ClothReleaseEntry>(m_Map.Values);
                list.Sort((a, b) => a.id.CompareTo(b.id));
                var cache = new ClothReleaseCache
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
                Debug.LogWarning($"[服装试穿] 写入投放缓存失败: {ex.Message}");
            }
        }

        /// <summary>导入 xlsx，返回导入摘要（已投放/未投放条数）。</summary>
        public static ClothReleaseData Import(string path, out string summary)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var data = new ClothReleaseData
            {
                SourcePath = path,
                ImportTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };

            using (var fs = File.OpenRead(path))
            using (var pkg = new ExcelPackage(fs))
            {
                var ws = FindSheet(pkg);
                if (ws == null)
                    throw new InvalidDataException("工作簿里没找到「服饰套装」表");

                if (ws.Dimension == null)
                    throw new InvalidDataException("「服饰套装」表是空的");

                // E=5 男件ID, L=12 男件投放；P=16 女件ID, W=23 女件投放
                for (var row = 2; row <= ws.Dimension.End.Row; row++)
                {
                    AddCell(data, ws, row, 5, 12);   // 男
                    AddCell(data, ws, row, 16, 23);  // 女
                }
            }

            if (data.Count == 0)
                throw new InvalidDataException("表里没有识别到道具ID（E列/P列）");

            data.SaveCache();
            summary = $"共{data.Count}件，已投放{data.ReleasedCount}件，未投放{data.Count - data.ReleasedCount}件";
            return data;
        }

        static ExcelWorksheet FindSheet(ExcelPackage pkg)
        {
            var ws = pkg.Workbook.Worksheets["服饰套装"];
            if (ws != null)
                return ws;
            foreach (var candidate in new[] { "服饰套装", "套装" })
            {
                foreach (var s in pkg.Workbook.Worksheets)
                {
                    if (s.Name != null && s.Name.Contains(candidate))
                        return s;
                }
            }

            return null;
        }

        static void AddCell(ClothReleaseData data, ExcelWorksheet ws, int row, int idCol, int releaseCol)
        {
            var idText = GetText(ws, row, idCol);
            if (string.IsNullOrWhiteSpace(idText))
                return;
            if (!uint.TryParse(idText.Trim(), out var id))
                return;

            var channel = GetText(ws, row, releaseCol)?.Trim() ?? "";
            var released = channel.Length > 0;

            if (data.m_Map.TryGetValue(id, out var old))
            {
                // 同一ID多行时：任一行有投放即算已投放，渠道保留最早非空的
                if (released && !old.released)
                {
                    old.released = true;
                    old.channel = channel;
                }
                else if (string.IsNullOrEmpty(old.channel) && released)
                {
                    old.channel = channel;
                }

                return;
            }

            data.m_Map.Add(id, new ClothReleaseEntry
            {
                id = (int)id,
                released = released,
                channel = channel,
            });
        }

        static string GetText(ExcelWorksheet ws, int row, int col)
        {
            var val = ws.GetValue(row, col);
            if (val == null)
                return "";
            if (val is DateTime dt)
                return dt.ToString("yyyy-MM-dd");
            return val.ToString();
        }
    }
}
