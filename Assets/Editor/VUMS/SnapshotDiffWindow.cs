using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UMS.Analysis;
using UMS.Analysis.Structures;
using UMS.Analysis.Structures.Objects;
using UMS.LowLevel.Structures;

namespace VUMS.Editor
{
    /// <summary>
    /// 多快照对比窗口：按 A → B → C → D → E 顺序选择 2～5 份快照，手动开始分析。
    /// A 为基准，最后一个已选择快照为最终对比，同时展示完整快照序列。
    /// </summary>
    public class SnapshotDiffWindow : EditorWindow
    {
        private const int MaxSnapshotCount = 5;
        private static readonly string[] SnapshotNames = { "A", "B", "C", "D", "E" };

        private readonly string[] _paths = new string[MaxSnapshotCount];
        private readonly List<SnapshotAnalysis> _snapshots = new List<SnapshotAnalysis>();
        private readonly List<TypeCountDiff> _typeDiffs = new List<TypeCountDiff>();
        private readonly List<LeakTypeDelta> _leakDeltas = new List<LeakTypeDelta>();

        private bool _diffing;
        private bool _hasResult;
        private string _statusText = "请按 A → B → C → D → E 的顺序选择快照，至少选择两个，然后点击分析。";
        private Vector2 _scroll;
        private readonly string[] _tabs = { "概览", "类型数量增量", "泄漏 Shell 增量", "重复字符串" };
        private int _selectedTab;

        [MenuItem("VUMS/SnapshotDiff", false, 3)]
        public static void OpenWindow()
        {
            var window = GetWindow<SnapshotDiffWindow>(true, "Snapshot Diff", true);
            window.minSize = new Vector2(760, 620);
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("Snapshot Diff", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "按 A → B → C → D → E 顺序添加，不能跳过；最少 2 个、最多 5 个。A 为基准，最后一个快照为最终对比。",
                MessageType.None);

            DrawSnapshotSelectors();
            DrawAnalyzeButton();

            if (!_hasResult)
            {
                EditorGUILayout.HelpBox(_statusText, MessageType.Info);
                return;
            }

            GUILayout.Space(8);
            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabs);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_selectedTab)
            {
                case 0:
                    DrawOverviewTab();
                    break;
                case 1:
                    DrawTypeDeltaTab();
                    break;
                case 2:
                    DrawLeakDeltaTab();
                    break;
                case 3:
                    DrawDuplicateStringTab();
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSnapshotSelectors()
        {
            for (var index = 0; index < MaxSnapshotCount; index++)
            {
                var canSelect = !_diffing && (index == 0 || !string.IsNullOrEmpty(_paths[index - 1]));
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginDisabledGroup(!canSelect);
                    var suffix = index == 0 ? " (基准)" : "";
                    if (GUILayout.Button($"选择快照 {SnapshotNames[index]}{suffix}", GUILayout.Width(160)))
                    {
                        var startDir = GetStartDirectory(index);
                        var picked = EditorUtility.OpenFilePanel($"选择快照 {SnapshotNames[index]}", startDir, "snap");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            _paths[index] = picked;
                            InvalidateResult();
                        }
                    }
                    EditorGUI.EndDisabledGroup();

                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.TextField(_paths[index] ?? "");
                    EditorGUI.EndDisabledGroup();

                    var canClear = !_diffing && !string.IsNullOrEmpty(_paths[index]);
                    EditorGUI.BeginDisabledGroup(!canClear);
                    if (GUILayout.Button("清除", GUILayout.Width(52)))
                        ClearFrom(index);
                    EditorGUI.EndDisabledGroup();
                }
            }
        }

        private void DrawAnalyzeButton()
        {
            var selectedCount = GetSelectedCount();
            GUILayout.Space(6);
            EditorGUI.BeginDisabledGroup(_diffing || selectedCount < 2);
            if (GUILayout.Button(_diffing ? "分析中..." : $"分析 {selectedCount} 个快照", GUILayout.Height(30)))
                BeginAnalysis();
            EditorGUI.EndDisabledGroup();

            if (selectedCount < 2)
                EditorGUILayout.LabelField("至少还需要选择快照 A 和 B。", EditorStyles.miniLabel);
        }

        private string GetStartDirectory(int index)
        {
            if (!string.IsNullOrEmpty(_paths[index]))
                return Path.GetDirectoryName(_paths[index]);
            if (index > 0 && !string.IsNullOrEmpty(_paths[index - 1]))
                return Path.GetDirectoryName(_paths[index - 1]);
            return "";
        }

        private int GetSelectedCount()
        {
            var count = 0;
            for (var index = 0; index < MaxSnapshotCount; index++)
            {
                if (string.IsNullOrEmpty(_paths[index]))
                    break;
                count++;
            }
            return count;
        }

        private void ClearFrom(int index)
        {
            for (var i = index; i < MaxSnapshotCount; i++)
                _paths[i] = "";
            InvalidateResult();
        }

        private void InvalidateResult()
        {
            _hasResult = false;
            _snapshots.Clear();
            _typeDiffs.Clear();
            _leakDeltas.Clear();
            _statusText = GetSelectedCount() < 2
                ? "请至少选择快照 A 和 B。"
                : "快照已选择，点击“分析”开始对比。";
            _scroll = Vector2.zero;
            Repaint();
        }

        private void BeginAnalysis()
        {
            if (_diffing || GetSelectedCount() < 2)
                return;

            _diffing = true;
            _hasResult = false;
            _statusText = "正在分析快照...";
            _selectedTab = 0;
            _scroll = Vector2.zero;
            EditorApplication.delayCall -= RunAnalysisOnce;
            EditorApplication.delayCall += RunAnalysisOnce;
            Repaint();
        }

        private void RunAnalysisOnce()
        {
            EditorApplication.delayCall -= RunAnalysisOnce;
            AnalyzeSnapshots();
            EditorApplication.delayCall += DeferredRepaint;
        }

        private void DeferredRepaint()
        {
            EditorApplication.delayCall -= DeferredRepaint;
            Repaint();
        }

        private void AnalyzeSnapshots()
        {
            var count = GetSelectedCount();
            if (count < 2)
            {
                _statusText = "错误：至少需要两个连续选择的快照。";
                _diffing = false;
                return;
            }

            for (var index = 0; index < count; index++)
            {
                if (!File.Exists(_paths[index]))
                {
                    _statusText = $"错误：快照 {SnapshotNames[index]} 文件不存在。";
                    _diffing = false;
                    return;
                }
            }

            _diffing = true;
            _snapshots.Clear();
            _typeDiffs.Clear();
            _leakDeltas.Clear();
            Repaint();

            try
            {
                for (var index = 0; index < count; index++)
                {
                    var progress = 0.05f + 0.75f * index / count;
                    EditorUtility.DisplayProgressBar(
                        "多快照对比",
                        $"正在分析快照 {SnapshotNames[index]} ({index + 1}/{count})...",
                        progress);
                    _snapshots.Add(AnalyzeSnapshot(_paths[index], SnapshotNames[index]));
                }

                EditorUtility.DisplayProgressBar("多快照对比", "正在计算多快照增量...", 0.85f);
                BuildTypeDiffs();
                BuildLeakDeltas();

                _hasResult = true;
                _statusText = $"已完成 {_snapshots.Count} 个快照的对比。";
            }
            catch (Exception exception)
            {
                _hasResult = false;
                _statusText = $"对比过程中发生错误:\n{exception.GetType().Name}: {exception.Message}\n\n{exception.StackTrace}";
                Debug.LogError($"[VUMS] SnapshotDiff 对比失败: {exception}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _diffing = false;
                Repaint();
            }
        }

        private static SnapshotAnalysis AnalyzeSnapshot(string path, string name)
        {
            using var file = new SnapshotFile(path);
            file.LoadManagedObjectsFromStaticFields();
            file.LoadManagedObjectsFromGcRoots();
            var objects = file.AllManagedClassInstances.ToArray();
            var leaks = CollectLeaks(file, objects);
            var duplicates = CollectDuplicateStrings(file);

            return new SnapshotAnalysis
            {
                Name = name,
                Path = path,
                CaptureTime = file.CaptureDateTime.ToString(),
                Platform = file.ProfileTargetInfo.ToString(),
                TotalObjects = objects.Length,
                Leaks = leaks,
                TypeCounts = CountByType(file, objects),
                DuplicateAvoidableBytes = duplicates.Values.Where(item => item.Count > 1).Sum(item => item.DuplicateBytes),
                Top50LargeObjectBytes = SumTopLargeObjects(file, objects),
                MemoryStats = file.ProfileTargetMemoryStats,
                DuplicateTop20 = duplicates.Values
                    .Where(item => item.Count > 1)
                    .OrderByDescending(item => item.DuplicateBytes)
                    .Take(20)
                    .ToList(),
            };
        }

        private void BuildTypeDiffs()
        {
            var allTypes = new HashSet<string>();
            foreach (var snapshot in _snapshots)
                allTypes.UnionWith(snapshot.TypeCounts.Keys);

            foreach (var type in allTypes)
            {
                var counts = new int[_snapshots.Count];
                for (var index = 0; index < _snapshots.Count; index++)
                    counts[index] = _snapshots[index].TypeCounts.TryGetValue(type, out var value) ? value : 0;

                if (counts.Distinct().Count() > 1)
                    _typeDiffs.Add(new TypeCountDiff { TypeName = type, Counts = counts });
            }

            _typeDiffs.Sort((x, y) =>
                Math.Abs(y.Counts[y.Counts.Length - 1] - y.Counts[0])
                    .CompareTo(Math.Abs(x.Counts[x.Counts.Length - 1] - x.Counts[0])));
        }

        private void BuildLeakDeltas()
        {
            var leakCounts = _snapshots
                .Select(snapshot => snapshot.Leaks
                    .GroupBy(item => item.TypeName)
                    .ToDictionary(group => group.Key, group => group.Count()))
                .ToArray();

            var allTypes = new HashSet<string>();
            foreach (var counts in leakCounts)
                allTypes.UnionWith(counts.Keys);

            var lastSnapshot = _snapshots[_snapshots.Count - 1];
            foreach (var type in allTypes)
            {
                var counts = new int[_snapshots.Count];
                for (var index = 0; index < leakCounts.Length; index++)
                    counts[index] = leakCounts[index].TryGetValue(type, out var value) ? value : 0;

                if (counts[counts.Length - 1] <= counts[0])
                    continue;

                _leakDeltas.Add(new LeakTypeDelta
                {
                    TypeName = type,
                    Counts = counts,
                    Objects = lastSnapshot.Leaks.Where(item => item.TypeName == type).Take(50).ToList(),
                });
            }

            _leakDeltas.Sort((x, y) =>
                (y.Counts[y.Counts.Length - 1] - y.Counts[0])
                    .CompareTo(x.Counts[x.Counts.Length - 1] - x.Counts[0]));
        }

        private void DrawOverviewTab()
        {
            foreach (var snapshot in _snapshots)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label($"快照 {snapshot.Name}{(snapshot.Name == "A" ? " (基准)" : "")}", EditorStyles.miniBoldLabel);
                    OverviewValueRow("采集时间", snapshot.CaptureTime);
                    OverviewValueRow("目标平台", snapshot.Platform);
                }
                GUILayout.Space(3);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("托管 (Managed)", EditorStyles.miniBoldLabel);
                OverviewSeriesRow("托管对象总数", _snapshots.Select(item => (long)item.TotalObjects), false);
                OverviewSeriesRow("泄漏 Managed Shell", _snapshots.Select(item => (long)item.Leaks.Count), false);
                OverviewSeriesRow("重复字符串可避免内存", _snapshots.Select(item => item.DuplicateAvoidableBytes), true);
                OverviewSeriesRow("Top50 大对象总大小", _snapshots.Select(item => item.Top50LargeObjectBytes), true);
            }

            GUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("原生 (Native)", EditorStyles.miniBoldLabel);
                MemorySeriesRow("总已用内存", item => item.TotalUsedMemory);
                MemorySeriesRow("GC 堆已用", item => item.GcHeapUsedMemory);
                MemorySeriesRow("GC 堆保留", item => item.GcHeapReservedMemory);
                MemorySeriesRow("图形 (Graphics)", item => item.GraphicsUsedMemory);
                MemorySeriesRow("音频 (Audio)", item => item.AudioUsedMemory);
                MemorySeriesRow("临时分配器 (Temp)", item => item.TempAllocatorUsedMemory);
                MemorySeriesRow("Profiler 已用", item => item.ProfilerUsedMemory);
                MemorySeriesRow("Memory Profiler 已用", item => item.MemoryProfilerUsedMemory);
                OverviewSeriesRow("真实内存占用", _snapshots.Select(item => (long)RealUsedMemory(item.MemoryStats)), true);
            }

            GUILayout.Space(2);
            EditorGUILayout.HelpBox(
                $"Δ 表示快照 {_snapshots[_snapshots.Count - 1].Name} 相对基准 A 的变化量。真实内存占用 = 总已用内存 − Profiler 已用 − Memory Profiler 已用。",
                MessageType.None);
        }

        private void DrawTypeDeltaTab()
        {
            EditorGUILayout.LabelField(
                $"共 {_typeDiffs.Count:N0} 个类型发生变化（按最终快照相对 A 的 |Δ| 降序，显示前 200）。");
            if (_typeDiffs.Count == 0)
            {
                EditorGUILayout.HelpBox("所选快照之间没有类型数量变化。", MessageType.Info);
                return;
            }

            var shown = Math.Min(200, _typeDiffs.Count);
            for (var index = 0; index < shown; index++)
            {
                var diff = _typeDiffs[index];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(diff.TypeName, EditorStyles.label,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.ExpandWidth(true));
                    GUILayout.Label(BuildCountSeries(diff.Counts), GUILayout.Width(GetSeriesWidth()));
                }
            }

            if (_typeDiffs.Count > shown)
                EditorGUILayout.HelpBox($"还有 {_typeDiffs.Count - shown:N0} 个变化较小的类型未列出。", MessageType.None);
        }

        private void DrawLeakDeltaTab()
        {
            var lastName = _snapshots[_snapshots.Count - 1].Name;
            if (_leakDeltas.Count == 0)
            {
                EditorGUILayout.HelpBox($"快照 {lastName} 相对 A 未检测到泄漏 Managed Shell 数量增加。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"共 {_leakDeltas.Count:N0} 个类型在快照 {lastName} 相对 A 的泄漏数量增加。");
            foreach (var delta in _leakDeltas)
            {
                var finalDelta = delta.Counts[delta.Counts.Length - 1] - delta.Counts[0];
                delta.Expanded = EditorGUILayout.Foldout(
                    delta.Expanded,
                    $"{delta.TypeName}  ({BuildCountSeries(delta.Counts)}, Δ:+{finalDelta:N0})",
                    true);
                if (!delta.Expanded)
                    continue;

                EditorGUILayout.LabelField(
                    $"快照 {lastName} 中该类型泄漏对象（显示前 {delta.Objects.Count:N0}，展开看路径）:",
                    EditorStyles.miniLabel);
                foreach (var leakedObject in delta.Objects)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        leakedObject.Expanded = EditorGUILayout.Foldout(
                            leakedObject.Expanded,
                            $"{leakedObject.TypeName} @ 0x{leakedObject.Address:X}",
                            true);
                        if (leakedObject.Expanded)
                            DrawLeakedObjectRetentionNodes(leakedObject);
                    }
                }
            }
        }

        private void DrawDuplicateStringTab()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                OverviewSeriesRow("重复字符串可避免内存", _snapshots.Select(item => item.DuplicateAvoidableBytes), true);

            foreach (var snapshot in _snapshots)
            {
                GUILayout.Space(6);
                EditorGUILayout.LabelField($"{snapshot.Name} 快照重复字符串 Top 20（按可避免内存排序）:", EditorStyles.boldLabel);
                DrawDuplicateStringList(snapshot.DuplicateTop20);
            }
        }

        private const float OverviewLabelWidth = 200f;
        private const float OverviewValueSpacing = 24f;

        private static void OverviewValueRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(OverviewLabelWidth));
                GUILayout.Space(OverviewValueSpacing);
                GUILayout.Label(value, GUILayout.ExpandWidth(true));
            }
        }

        private void OverviewSeriesRow(string label, IEnumerable<long> values, bool isBytes)
        {
            var array = values.ToArray();
            var parts = new string[array.Length];
            for (var index = 0; index < array.Length; index++)
            {
                var value = isBytes ? FormatBytes(array[index]) : array[index].ToString("N0");
                parts[index] = $"{_snapshots[index].Name}: {value}";
            }

            var delta = array[array.Length - 1] - array[0];
            var deltaText = isBytes ? FormatBytes(delta) : delta.ToString("N0");
            OverviewValueRow(label, $"{string.Join("    ", parts)}    Δ: {(delta > 0 ? "+" : "")}{deltaText}");
        }

        private void MemorySeriesRow(string label, Func<ProfileTargetMemoryStats, ulong> selector)
        {
            OverviewSeriesRow(label, _snapshots.Select(item => (long)selector(item.MemoryStats)), true);
        }

        private float GetSeriesWidth()
        {
            return Mathf.Max(260f, _snapshots.Count * 105f + 80f);
        }

        private string BuildCountSeries(int[] counts)
        {
            var parts = new string[counts.Length];
            for (var index = 0; index < counts.Length; index++)
                parts[index] = $"{_snapshots[index].Name}:{counts[index]:N0}";
            return string.Join("  ", parts);
        }

        private static ulong RealUsedMemory(ProfileTargetMemoryStats memory)
        {
            var value = memory.TotalUsedMemory;
            value = value > memory.ProfilerUsedMemory ? value - memory.ProfilerUsedMemory : 0;
            value = value > memory.MemoryProfilerUsedMemory ? value - memory.MemoryProfilerUsedMemory : 0;
            return value;
        }

        private static void DrawDuplicateStringList(List<DuplicateStringStat> list)
        {
            if (list == null || list.Count == 0)
            {
                EditorGUILayout.HelpBox("未发现重复字符串实例。", MessageType.Info);
                return;
            }

            foreach (var stat in list)
            {
                var preview = stat.Value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                if (preview.Length > 100)
                    preview = preview.Substring(0, 100) + "...";
                EditorGUILayout.LabelField($"  {stat.Count,6:N0} x | 总计 {FormatBytes(stat.TotalBytes),10} | \"{preview}\"");
            }
        }

        private static void DrawLeakedObjectRetentionNodes(LeakedObjectInfo leakedObject)
        {
            var nodes = leakedObject.RetentionPathNodes;
            if (nodes == null || nodes.Length == 0)
            {
                EditorGUILayout.HelpBox("(无)", MessageType.None);
                return;
            }

            if (leakedObject.RetentionNodeExpanded == null || leakedObject.RetentionNodeExpanded.Length != nodes.Length)
            {
                leakedObject.RetentionNodeExpanded = new bool[nodes.Length];
                leakedObject.RetentionNodeExpanded[0] = true;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                for (var depth = 0; depth < nodes.Length; depth++)
                {
                    if (depth > 0 && !leakedObject.RetentionNodeExpanded[depth - 1])
                        break;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(depth * 18f);
                        if (depth < nodes.Length - 1)
                        {
                            leakedObject.RetentionNodeExpanded[depth] = EditorGUILayout.Foldout(
                                leakedObject.RetentionNodeExpanded[depth], nodes[depth], true);
                        }
                        else
                        {
                            GUILayout.Space(14f);
                            GUILayout.Label(nodes[depth], EditorStyles.wordWrappedLabel);
                        }
                    }
                }
            }
        }

        private static Dictionary<string, int> CountByType(SnapshotFile file, ManagedClassInstance[] objects)
        {
            var result = new Dictionary<string, int>();
            foreach (var obj in objects)
            {
                if (obj.ObjectAddress == 0)
                    continue;
                var type = file.GetTypeName(obj.TypeInfo.TypeIndex);
                result.TryGetValue(type, out var count);
                result[type] = count + 1;
            }
            return result;
        }

        private static List<LeakedObjectInfo> CollectLeaks(SnapshotFile file, ManagedClassInstance[] objects)
        {
            var result = new List<LeakedObjectInfo>();
            foreach (var obj in objects.Where(item => item.InheritsFromUnityEngineObject(file)))
            {
                if (!obj.IsLeakedManagedShell(file))
                    continue;
                result.Add(new LeakedObjectInfo
                {
                    TypeName = file.GetTypeName(obj.TypeInfo.TypeIndex),
                    Address = obj.ObjectAddress,
                    RetentionPathNodes = GetSafeRetentionPathNodes(file, obj),
                });
            }
            return result;
        }

        private static Dictionary<string, DuplicateStringStat> CollectDuplicateStrings(SnapshotFile file)
        {
            var result = new Dictionary<string, DuplicateStringStat>(StringComparer.Ordinal);
            foreach (var managedString in file.AllManagedStrings)
            {
                if (!result.TryGetValue(managedString.Value, out var stat))
                {
                    stat = new DuplicateStringStat
                    {
                        Value = managedString.Value,
                        SingleInstanceBytes = managedString.SizeBytes,
                    };
                    result.Add(managedString.Value, stat);
                }
                stat.Count++;
                stat.TotalBytes += managedString.SizeBytes;
            }
            return result;
        }

        private static long SumTopLargeObjects(SnapshotFile file, ManagedClassInstance[] objects)
        {
            var sizes = new List<long>();
            foreach (var obj in objects)
            {
                if (obj.ObjectAddress == 0)
                    continue;
                var info = file.ParseManagedObjectInfo(obj.ObjectAddress);
                if (info.IsKnownType && info.Size > 0)
                    sizes.Add(info.Size);
            }
            sizes.Sort((x, y) => y.CompareTo(x));
            return sizes.Take(50).Sum();
        }

        private static string[] GetSafeRetentionPathNodes(SnapshotFile file, ManagedClassInstance instance)
        {
            try
            {
                return instance.GetFirstObservedRetentionPathNodes(file);
            }
            catch (Exception exception)
            {
                return new[] { $"保留路径解析失败: {exception.Message}" };
            }
        }

        private static string FormatBytes(long bytes)
        {
            var negative = bytes < 0;
            var absolute = Math.Abs((double)bytes);
            const double kb = 1024.0;
            const double mb = kb * 1024.0;
            const double gb = mb * 1024.0;
            var formatted = absolute >= gb ? $"{absolute / gb:F2} GB"
                : absolute >= mb ? $"{absolute / mb:F2} MB"
                : absolute >= kb ? $"{absolute / kb:F2} KB"
                : $"{absolute:F0} B";
            return negative ? $"-{formatted}" : formatted;
        }

        private sealed class SnapshotAnalysis
        {
            public string Name;
            public string Path;
            public string CaptureTime;
            public string Platform;
            public int TotalObjects;
            public Dictionary<string, int> TypeCounts;
            public List<LeakedObjectInfo> Leaks;
            public long DuplicateAvoidableBytes;
            public long Top50LargeObjectBytes;
            public ProfileTargetMemoryStats MemoryStats;
            public List<DuplicateStringStat> DuplicateTop20;
        }

        private sealed class TypeCountDiff
        {
            public string TypeName;
            public int[] Counts;
        }

        private sealed class LeakTypeDelta
        {
            public string TypeName;
            public int[] Counts;
            public List<LeakedObjectInfo> Objects = new List<LeakedObjectInfo>();
            public bool Expanded;
        }

        private sealed class LeakedObjectInfo
        {
            public string TypeName;
            public ulong Address;
            public string[] RetentionPathNodes;
            public bool Expanded;
            public bool[] RetentionNodeExpanded = Array.Empty<bool>();
        }

        private sealed class DuplicateStringStat
        {
            public string Value;
            public int Count;
            public long TotalBytes;
            public long SingleInstanceBytes;
            public long DuplicateBytes => Math.Max(0, TotalBytes - SingleInstanceBytes);
        }
    }
}
