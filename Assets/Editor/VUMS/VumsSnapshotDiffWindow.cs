using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    public class VumsSnapshotDiffWindow : EditorWindow
    {
        private const int MaxSnapshotCount = 5;
        // 重复字符串合并对比表每对展示前 N 行；改这里即可调整对比展示数量
        private const int TopDuplicateStringDiffCount = 50;
        private const int DuplicatePreviewMaxLength = 60;
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
            var window = GetWindow<VumsSnapshotDiffWindow>(true, "Snapshot Diff", true);
            // 需要容纳“类型名 + 新增/消失标记 + A→E 序列 + 趋势列 + 迷你柱状图”，
            // 宽度不足时趋势列会自动降级为纯文字，故这里给出能完整展示的默认宽度。
            window.minSize = new Vector2(1000, 620);
            window.Show();
        }

        private void OnGUI()
        {
            VumsEditorStyles.EnsureInitialized();
            var selectedCount = GetSelectedCount();
            VumsEditorStyles.DrawHeader(
                "多快照趋势对比",
                "按 A → B → C → D → E 构建连续序列，以 A 为基准观察最终变化与中间趋势");

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                DrawSnapshotSelectors();
                GUILayout.Space(6f);
                DrawAnalyzeButton();
            }

            if (!_hasResult)
            {
                GUILayout.Space(VumsEditorStyles.SectionSpacing);
                VumsEditorStyles.DrawStatus(_statusText, MessageType.Info);
                return;
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            _selectedTab = VumsEditorStyles.DrawTabs(_selectedTab, _tabs);
            GUILayout.Space(6f);
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
            // 限制路径文本框最大宽度，避免未选快照时中间留白把左右按钮拉得太开
            var maxFieldWidth = Mathf.Max(160f, position.width - 340f);
            for (var index = 0; index < MaxSnapshotCount; index++)
            {
                var hasPath = !string.IsNullOrEmpty(_paths[index]);
                var canSelect = !_diffing && (index == 0 || !string.IsNullOrEmpty(_paths[index - 1]));
                using (new EditorGUILayout.HorizontalScope(VumsEditorStyles.CompactCard))
                {
                    var slotLabel = index == 0 ? $"{SnapshotNames[index]}  基准" : SnapshotNames[index];
                    GUILayout.Label(slotLabel, VumsEditorStyles.SectionTitle, GUILayout.Width(72f));

                    EditorGUI.BeginDisabledGroup(!canSelect);
                    if (GUILayout.Button(
                            hasPath ? "重新选择" : "选择快照",
                            VumsEditorStyles.SecondaryButton,
                            GUILayout.Width(92f)))
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
                    EditorGUILayout.TextField(hasPath ? _paths[index] : "未选择", GUILayout.Height(24f), GUILayout.MaxWidth(maxFieldWidth));
                    EditorGUI.EndDisabledGroup();

                    var canClear = !_diffing && hasPath;
                    EditorGUI.BeginDisabledGroup(!canClear);
                    if (GUILayout.Button("清除", VumsEditorStyles.DangerButton, GUILayout.Width(56f)))
                        ClearFrom(index);
                    EditorGUI.EndDisabledGroup();
                }
            }
        }

        private void DrawAnalyzeButton()
        {
            var selectedCount = GetSelectedCount();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                EditorGUI.BeginDisabledGroup(_diffing || selectedCount < 2);
                if (GUILayout.Button(
                        _diffing ? "正在分析..." : $"开始分析 {selectedCount} 个快照",
                        VumsEditorStyles.PrimaryButton,
                        GUILayout.Width(200f),
                        GUILayout.Height(26f)))
                    BeginAnalysis();
                EditorGUI.EndDisabledGroup();
                GUILayout.FlexibleSpace();
            }

           GUILayout.Space(2f);
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
                DuplicateStats = duplicates,
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
            {
                // 组内顺序：持续升 > 新增 > 其他，组间按最终快照相对 A 的 |Δ| 降序。
                // “持续升”表示中间没有任何一次回落，是最典型的只增不减泄漏特征，
                // 因此优先于仅在首尾比较上看起来变多的类型。
                var xRank = GetTypeOrderRank(x.Counts);
                var yRank = GetTypeOrderRank(y.Counts);
                if (xRank != yRank)
                    return xRank.CompareTo(yRank);

                return Math.Abs(y.Counts[y.Counts.Length - 1] - y.Counts[0])
                    .CompareTo(Math.Abs(x.Counts[x.Counts.Length - 1] - x.Counts[0]));
            });
        }

        /// <summary>类型增量列表的分组优先级，数值越小越靠前。</summary>
        private static int GetTypeOrderRank(int[] counts)
        {
            if (ClassifyTrend(ToLongSeries(counts)) == TrendShape.Rising)
                return 0;
            if (IsNewType(counts))
                return 1;
            return 2;
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

                var objects = lastSnapshot.Leaks.Where(item => item.TypeName == type).ToList();
                _leakDeltas.Add(new LeakTypeDelta
                {
                    TypeName = type,
                    Counts = counts,
                    Objects = objects,
                    PathGroups = BuildRetentionPathGroups(objects),
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
                using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
                {
                    VumsEditorStyles.DrawSectionHeader(
                        $"快照 {snapshot.Name}{(snapshot.Name == "A" ? "（基准）" : "")}",
                        snapshot.Path);
                    OverviewValueRow("采集时间", snapshot.CaptureTime);
                    OverviewValueRow("目标平台", snapshot.Platform);
                }
                GUILayout.Space(4f);
            }

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("托管内存", "展示完整序列，并计算最后快照相对 A 的变化量。");
                OverviewSeriesRow("托管对象总数", _snapshots.Select(item => (long)item.TotalObjects), false);
                OverviewSeriesRow("泄漏 Managed Shell", _snapshots.Select(item => (long)item.Leaks.Count), false);
                OverviewSeriesRow("重复字符串可避免内存", _snapshots.Select(item => item.DuplicateAvoidableBytes), true);
                OverviewSeriesRow("Top50 大对象总大小", _snapshots.Select(item => item.Top50LargeObjectBytes), true);
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("原生内存", "单位统一格式化显示，Δ 始终表示最终快照减去 A。");
                MemorySeriesRow("总已用内存", item => item.TotalUsedMemory);
                MemorySeriesRow("GC 堆已用", item => item.GcHeapUsedMemory);
                MemorySeriesRow("GC 堆保留", item => item.GcHeapReservedMemory);
                MemorySeriesRow("图形 (Graphics)", item => item.GraphicsUsedMemory);
                MemorySeriesRow("音频 (Audio)", item => item.AudioUsedMemory);
                MemorySeriesRow("Profiler 已用", item => item.ProfilerUsedMemory);
                MemorySeriesRow("Memory Profiler 已用", item => item.MemoryProfilerUsedMemory);
                OverviewSeriesRow("真实内存占用", _snapshots.Select(item => (long)RealUsedMemory(item.MemoryStats)), true);
            }

            GUILayout.Space(4f);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
            {
                GUILayout.Label(
                    $"Δ 表示快照 {_snapshots[_snapshots.Count - 1].Name} 相对基准 A 的变化量。真实内存占用 = 总已用内存 − Profiler 已用 − Memory Profiler 已用。",
                    VumsEditorStyles.SectionDescription);
                GUILayout.Space(2f);
                GUILayout.Label(
                    "GC 堆已用 = 当前 Mono/IL2CPP GC 堆实际占用；GC 堆保留 = Unity已经向系统申请并保留下来的堆容量。",
                    VumsEditorStyles.SectionDescription);
            }
        }

        private void DrawTypeDeltaTab()
        {
            var displayList = _typeDiffs.ToArray();
            var newTypeCount = displayList.Count(item => IsNewType(item.Counts));
            var removedTypeCount = displayList.Count(item => IsRemovedType(item.Counts));
            var risingCount = displayList.Count(item => ClassifyTrend(ToLongSeries(item.Counts)) == TrendShape.Rising);

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("类型数量增量",
                    $"共 {displayList.Length:N0} 个类型数量发生变化；最多显示 200 项。");

                // 排序优先级：持续升 > 新增 > 其他，同组内按 |Δ| 降序。
                var orderText = risingCount > 0
                    ? $"其中“持续升” {risingCount:N0} 个（中途无回落）已排在最前，其次为新增类型；同组内按 |Δ| 降序。"
                    : "按最终快照相对 A 的 |Δ| 降序。";
                GUILayout.Label(orderText, VumsEditorStyles.SectionDescription);
                if (newTypeCount > 0)
                    GUILayout.Label($"新增类型 {newTypeCount:N0} 个（A 中不存在、最终快照中出现）。",
                        VumsEditorStyles.SectionDescription);
                if (removedTypeCount > 0)
                    GUILayout.Label($"已消失类型 {removedTypeCount:N0} 个（A 中存在、最终快照归零）。",
                        VumsEditorStyles.SectionDescription);
                GUILayout.Label("趋势列：持续升 / 持续降 / 升后降 / 波动 / 平稳，右侧为 A→E 迷你柱状图。",
                    VumsEditorStyles.SectionDescription);
            }
            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            if (displayList.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "所选快照之间没有类型数量变化。",
                    MessageType.Info);
                return;
            }

            var seriesWidth = GetSeriesWidth();
            var showSparkline = CanShowSparkline(
                TypeTagWidth + seriesWidth + TrendLabelWidth + SparklineWidth, 260f);

            var shown = Math.Min(200, displayList.Length);
            for (var index = 0; index < shown; index++)
            {
                var diff = displayList[index];
                var tag = GetTypeTag(diff.Counts);
                var rowText = string.IsNullOrEmpty(tag)
                    ? $"{diff.TypeName}  {BuildCountSeries(diff.Counts)}"
                    : $"[{tag}] {diff.TypeName}  {BuildCountSeries(diff.Counts)}";
                VumsEditorStyles.CopyableRow(this, 32f, rowText, () =>
                {
                    using (new EditorGUILayout.HorizontalScope(VumsEditorStyles.CompactCard))
                    {
                        EditorGUILayout.SelectableLabel(
                            diff.TypeName,
                            VumsEditorStyles.SelectableRow,
                            GUILayout.Height(24f),
                            GUILayout.ExpandWidth(true));
                        GUILayout.Label(
                            tag,
                            VumsEditorStyles.MutedLabel,
                            GUILayout.Width(TypeTagWidth));
                        GUILayout.Label(
                            BuildCountSeries(diff.Counts),
                            VumsEditorStyles.MetricValue,
                            GUILayout.Width(seriesWidth));
                        DrawTrendShape(
                            ToLongSeries(diff.Counts),
                            EditorGUIUtility.singleLineHeight + 6f,
                            showSparkline);
                    }
                });
            }

            if (displayList.Length > shown)
                EditorGUILayout.HelpBox($"还有 {displayList.Length - shown:N0} 个变化较小的类型未列出。", MessageType.None);
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
            var showLeakSparkline = CanShowSparkline(TrendLabelWidth + SparklineWidth, 420f);
            foreach (var delta in _leakDeltas)
            {
                var finalDelta = delta.Counts[delta.Counts.Length - 1] - delta.Counts[0];
                var leakHeaderText = $"{delta.TypeName}  ({BuildCountSeries(delta.Counts)}, Δ:+{finalDelta:N0})";
                VumsEditorStyles.CopyableRow(
                    this,
                    EditorGUIUtility.singleLineHeight + 8f,
                    leakHeaderText,
                    () =>
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            delta.Expanded = EditorGUILayout.Foldout(delta.Expanded, leakHeaderText, true);
                            DrawTrendShape(
                                ToLongSeries(delta.Counts),
                                EditorGUIUtility.singleLineHeight + 4f,
                                showLeakSparkline);
                        }
                    });
                if (!delta.Expanded)
                    continue;

                EditorGUILayout.LabelField(
                    $"快照 {lastName} 中 {delta.Objects.Count:N0} 个对象聚合为 {delta.PathGroups.Count:N0} 条路径（按数量降序）:",
                    EditorStyles.miniLabel);
                foreach (var group in delta.PathGroups)
                {
                    using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
                    {
                        group.Expanded = EditorGUILayout.Foldout(
                            group.Expanded,
                            $"{group.Objects.Count:N0} 个（{group.Objects.Count * 100f / delta.Objects.Count:F1}%） | {GetRootSummary(group.Representative.RetentionPathNodes)}",
                            true);
                        if (!group.Expanded)
                            continue;

                        DrawLeakedObjectRetentionNodes(this, group.Representative);
                    }
                }
            }
        }

        private void DrawDuplicateStringTab()
        {
            if (_snapshots.Count < 2)
            {
                GUILayout.Space(VumsEditorStyles.SectionSpacing);
                VumsEditorStyles.DrawStatus("请至少选择两个快照以对比重复字符串。", MessageType.Info);
                return;
            }

            var lastName = _snapshots[_snapshots.Count - 1].Name;

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    "重复字符串对比",
                    $"依次对比每个字符串在 {string.Join("→", _snapshots.Select(item => item.Name))} 各快照的次数/字节；" +
                    $"单元格上排次数/Δ次数，下排字节/Δ字节，Δ 为相对上一快照的增量；" +
                    $"按最终快照 {lastName} 的字节大小降序展示（缺失快照按字节 0 排到末尾），最多 {TopDuplicateStringDiffCount} 行。");
                OverviewSeriesRow("重复字符串可避免内存", _snapshots.Select(item => item.DuplicateAvoidableBytes), true);
            }

            var series = BuildDuplicateStringSeries();
            if (series.Count == 0)
            {
                GUILayout.Space(VumsEditorStyles.SectionSpacing);
                VumsEditorStyles.DrawStatus("未发现重复字符串差异（所有快照中均未出现次数 > 1 的重复字符串）。", MessageType.Info);
                return;
            }

            var lastIndex = _snapshots.Count - 1;
            DrawDuplicateSourceSummary(series, lastIndex);

            var topRows = series
                .OrderByDescending(item => item.Bytes[lastIndex])
                .ThenBy(item => item.Value, StringComparer.Ordinal)
                .Take(TopDuplicateStringDiffCount)
                .ToList();

            var snapshotNames = _snapshots.Select(item => item.Name).ToArray();
            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    $"重复字符串对比明细（{topRows.Count:N0} / {series.Count:N0} 行）",
                    "缺失快照显示“无”；Δ 列红 = 增大/新增，绿 = 减小/释放。右键可复制整行（含全序列数据）。");
                DrawDuplicateSeriesHeader();
                foreach (var row in topRows)
                    DrawDuplicateSeriesRow(this, row, snapshotNames);
            }
        }

        /// <summary>
        /// 按推断来源聚合“最终快照占用字节”并降序展示，直接给出优化优先级。
        /// </summary>
        private void DrawDuplicateSourceSummary(List<DuplicateStringSeries> series, int lastIndex)
        {
            var groups = series
                .GroupBy(item => item.Source)
                .Select(g => new
                {
                    Source = g.Key,
                    Count = g.Count(),
                    Bytes = g.Sum(item => item.Bytes[lastIndex]),
                })
                .OrderByDescending(x => x.Bytes)
                .ToArray();

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    "来源分类汇总",
                    "按推断来源聚合“最终快照占用字节”并降序排列；优先修靠前的来源类别，收益最大。");
                foreach (var g in groups)
                {
                    var label = VumsStringSourceHelper.Label(g.Source);
                    OverviewValueRow($"[{label}]（{g.Count:N0} 条）", FormatBytes(g.Bytes));
                    GUILayout.Label(
                        VumsStringSourceHelper.Suggestion(g.Source),
                        VumsEditorStyles.SectionDescription);
                    GUILayout.Space(2f);
                }
            }
        }

        private static void OverviewValueRow(string label, string value)        {
            VumsEditorStyles.DrawMetricRow(label, value, 220f);
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
            var seriesText = $"{string.Join("    ", parts)}    Δ: {(delta > 0 ? "+" : "")}{deltaText}";

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, VumsEditorStyles.MetricLabel, GUILayout.Width(220f));
                GUILayout.Space(20f);
                GUILayout.Label(seriesText, VumsEditorStyles.MetricValue, GUILayout.ExpandWidth(true));
                DrawTrendShape(array, EditorGUIUtility.singleLineHeight + 6f,
                    CanShowSparkline(220f + 20f + TrendLabelWidth + SparklineWidth, 430f));
            }
        }

        /// <summary>
        /// 判断当前窗口宽度是否还容得下迷你柱状图。reservedFixed 是同行已占用的固定宽度，
        /// minBodyWidth 是仍需留给类型名/序列文本的最小宽度。空间不足时自动降级为纯文字标签，
        /// 保证形状结论仍在，但不会把长类型名挤没。
        /// </summary>
        private bool CanShowSparkline(float reservedFixed, float minBodyWidth)
        {
            return position.width - reservedFixed >= minBodyWidth;
        }

        private void MemorySeriesRow(string label, Func<ProfileTargetMemoryStats, ulong> selector)
        {
            OverviewSeriesRow(label, _snapshots.Select(item => (long)selector(item.MemoryStats)), true);
        }

        private float GetSeriesWidth()
        {
            // 每快照按 92px 估算（形如 "A:12,345"），比原先的 105px 更紧凑，
            // 为新增的“新增/消失”标记与趋势列留出空间。
            return Mathf.Max(260f, _snapshots.Count * 92f + 80f);
        }

        private string BuildCountSeries(int[] counts)
        {
            var parts = new string[counts.Length];
            for (var index = 0; index < counts.Length; index++)
                parts[index] = $"{_snapshots[index].Name}:{counts[index]:N0}";
            var delta = counts[counts.Length - 1] - counts[0];
            return $"{string.Join("  ", parts)}  Δ:{(delta > 0 ? "+" : "")}{delta:N0}";
        }

        // ---------- 趋势形状（A→E 序列的形状判定与迷你柱状图）----------

        private const float SparklineWidth = 56f;
        private const float TrendLabelWidth = 48f;
        private const float TypeTagWidth = 36f;

        private enum TrendShape
        {
            /// <summary>每步都在上升，疑似真泄漏。</summary>
            Rising,

            /// <summary>每步都在下降。</summary>
            Falling,

            /// <summary>中途到过峰值后明显回落，通常只是尚未回收或已释放。</summary>
            Peaked,

            /// <summary>有升有降，无稳定方向。</summary>
            Volatile,

            /// <summary>首尾接近，基本平稳。</summary>
            Flat,
        }

        /// <summary>基准快照中不存在、最终快照中出现的类型。</summary>
        private static bool IsNewType(int[] counts)
        {
            return counts != null && counts.Length > 1 && counts[0] == 0 &&
                   counts[counts.Length - 1] > 0;
        }

        /// <summary>基准快照中存在、最终快照中已归零的类型。</summary>
        private static bool IsRemovedType(int[] counts)
        {
            return counts != null && counts.Length > 1 && counts[0] > 0 &&
                   counts[counts.Length - 1] == 0;
        }

        private static string GetTypeTag(int[] counts)
        {
            if (IsNewType(counts))
                return "新增";
            if (IsRemovedType(counts))
                return "消失";
            return string.Empty;
        }

        private static TrendShape ClassifyTrend(long[] values)
        {
            if (values == null || values.Length < 2)
                return TrendShape.Flat;

            var first = values[0];
            var last = values[values.Length - 1];

            var max = values[0];
            var peakIndex = 0;
            for (var index = 1; index < values.Length; index++)
            {
                if (values[index] <= max)
                    continue;
                max = values[index];
                peakIndex = index;
            }

            var risingSteps = 0;
            var fallingSteps = 0;
            for (var index = 1; index < values.Length; index++)
            {
                if (values[index] > values[index - 1])
                    risingSteps++;
                else if (values[index] < values[index - 1])
                    fallingSteps++;
            }

            var steps = values.Length - 1;
            if (risingSteps == steps)
                return TrendShape.Rising;
            if (fallingSteps == steps)
                return TrendShape.Falling;

            // 峰值不在末位，且从峰值到末位的回落幅度明显，视为“曾增长后回落”。
            if (peakIndex < values.Length - 1 && max > 0)
            {
                var drop = max - last;
                if (drop > 0 && drop / (double)max >= 0.15)
                    return TrendShape.Peaked;
            }

            var scale = Math.Max(Math.Abs(first), Math.Abs(max));
            if (scale > 0 && Math.Abs(last - first) <= scale * 0.05)
                return TrendShape.Flat;

            return TrendShape.Volatile;
        }

        private static string GetTrendLabel(TrendShape shape)
        {
            switch (shape)
            {
                case TrendShape.Rising:
                    return "持续升";
                case TrendShape.Falling:
                    return "持续降";
                case TrendShape.Peaked:
                    return "升后降";
                case TrendShape.Volatile:
                    return "波动";
                default:
                    return "平稳";
            }
        }

        /// <summary>只有“持续上升”值得视觉突出，其余保持低调。</summary>
        private static bool IsTrendEmphasized(TrendShape shape)
        {
            return shape == TrendShape.Rising;
        }

        /// <summary>
        /// 绘制迷你柱状图。颜色直接取自当前主题的 label 文字色并调节透明度，
        /// 因此不引入任何自定义配色，深浅主题都能自动适配。
        /// </summary>
        private static void DrawSparkline(Rect rect, long[] values, bool emphasize)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            if (values == null || values.Length == 0 || rect.width <= 0f)
                return;

            var min = values[0];
            var max = values[0];
            foreach (var value in values)
            {
                if (value < min)
                    min = value;
                if (value > max)
                    max = value;
            }

            var range = max - min;
            var slotWidth = rect.width / values.Length;
            var barWidth = Mathf.Max(2f, slotWidth - 2f);
            var color = GUI.skin.label.normal.textColor;

            for (var index = 0; index < values.Length; index++)
            {
                // 各快照数值完全相同时画成齐平半高柱，避免误读成“满格”或“空”。
                var normalized = range <= 0
                    ? 0.5f
                    : (float)(values[index] - min) / range;

                var height = Mathf.Max(2f, normalized * (rect.height - 2f));
                var x = rect.x + index * slotWidth + 1f;
                var y = rect.y + rect.height - height;

                color.a = emphasize ? 0.80f : 0.32f;
                EditorGUI.DrawRect(new Rect(x, y, barWidth, height), color);
            }
        }

        /// <summary>把 int 序列转成 long 序列，供趋势判定与绘图复用。</summary>
        private static long[] ToLongSeries(int[] counts)
        {
            var series = new long[counts.Length];
            for (var index = 0; index < counts.Length; index++)
                series[index] = counts[index];
            return series;
        }

        /// <summary>
        /// 在一行末尾绘制趋势形状文字与迷你柱状图。showSparkline 为 false 时
        /// 只显示文字标签，用于窗口较窄、类型名需要更多横向空间的场景。
        /// </summary>
        private static void DrawTrendShape(long[] values, float height, bool showSparkline)
        {
            var shape = ClassifyTrend(values);
            GUILayout.Label(GetTrendLabel(shape), VumsEditorStyles.MutedLabel, GUILayout.Width(TrendLabelWidth));
            if (!showSparkline)
                return;

            var rect = GUILayoutUtility.GetRect(
                SparklineWidth,
                height,
                GUILayout.Width(SparklineWidth),
                GUILayout.Height(height));
            DrawSparkline(rect, values, IsTrendEmphasized(shape));
        }

        private static ulong RealUsedMemory(ProfileTargetMemoryStats memory)
        {
            var value = memory.TotalUsedMemory;
            value = value > memory.ProfilerUsedMemory ? value - memory.ProfilerUsedMemory : 0;
            value = value > memory.MemoryProfilerUsedMemory ? value - memory.MemoryProfilerUsedMemory : 0;
            return value;
        }

        private enum DuplicateDeltaStatus
        {
            New,
            Removed,
            Rising,
            Falling,
            Volatile,
            Same,
        }

        /// <summary>
        /// 一个重复字符串在全部快照序列上的横向视图：每个快照各记次数/字节，
        /// Present 标记该快照是否被视为重复（Count > 1）。Status 基于全序列判定。
        /// </summary>
        private sealed class DuplicateStringSeries
        {
            public string Value;
            public int[] Counts;
            public long[] Bytes;
            public bool[] Present;
            public DuplicateDeltaStatus Status;
            // 推断的来源写法，用于“来源建议”
            public DuplicateStringSource Source;
        }

        /// <summary>
        /// 收集所有快照中“至少在一处 Count > 1”的字符串，构建横向全序列视图。
        /// 某快照缺失或 Count ≤ 1 时记 Present=false，渲染显示“无”。
        /// </summary>
        private List<DuplicateStringSeries> BuildDuplicateStringSeries()
        {
            var snapCount = _snapshots.Count;
            var presentKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snap in _snapshots)
                foreach (var kv in snap.DuplicateStats)
                    if (kv.Value.Count > 1)
                        presentKeys.Add(kv.Key);

            var rows = new List<DuplicateStringSeries>(presentKeys.Count);
            foreach (var key in presentKeys)
            {
                var counts = new int[snapCount];
                var bytes = new long[snapCount];
                var present = new bool[snapCount];
                for (var i = 0; i < snapCount; i++)
                {
                    if (_snapshots[i].DuplicateStats.TryGetValue(key, out var stat) && stat.Count > 1)
                    {
                        counts[i] = stat.Count;
                        bytes[i] = stat.TotalBytes;
                        present[i] = true;
                    }
                }
                rows.Add(new DuplicateStringSeries
                {
                    Value = key,
                    Counts = counts,
                    Bytes = bytes,
                    Present = present,
                    Status = GetSeriesStatus(counts, present),
                    Source = VumsStringSourceHelper.Classify(key),
                });
            }
            return rows;
        }

        /// <summary>
        /// 基于全序列（A→E）判定状态：A 无 E 有=新增；A 有 E 无=消失；
        /// 中间既有涨又有跌=波动；否则按整体方向升/降；首尾一致=持平。
        /// </summary>
        private static DuplicateDeltaStatus GetSeriesStatus(int[] counts, bool[] present)
        {
            var n = counts.Length;
            if (!present[0] && present[n - 1])
                return DuplicateDeltaStatus.New;
            if (present[0] && !present[n - 1])
                return DuplicateDeltaStatus.Removed;

            var hasRise = false;
            var hasFall = false;
            for (var i = 1; i < n; i++)
            {
                if (!present[i] && !present[i - 1])
                    continue;
                if (counts[i] > counts[i - 1])
                    hasRise = true;
                else if (counts[i] < counts[i - 1])
                    hasFall = true;
            }
            if (hasRise && hasFall)
                return DuplicateDeltaStatus.Volatile;
            if (hasRise)
                return DuplicateDeltaStatus.Rising;
            if (hasFall)
                return DuplicateDeltaStatus.Falling;
            return DuplicateDeltaStatus.Same;
        }

        private void DrawDuplicateSeriesHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("字符串预览", EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(true));
                for (var i = 0; i < _snapshots.Count; i++)
                    GUILayout.Label(_snapshots[i].Name, EditorStyles.miniBoldLabel, GUILayout.Width(76f));
                GUILayout.Label("状态", EditorStyles.miniBoldLabel, GUILayout.Width(48f));
            }
        }

        private static void DrawDuplicateSeriesRow(EditorWindow window, DuplicateStringSeries row, string[] snapshotNames)
        {
            var preview = row.Value
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            var isTruncated = preview.Length > DuplicatePreviewMaxLength;
            var displayedPreview = isTruncated
                ? preview.Substring(0, DuplicatePreviewMaxLength) + "..."
                : preview;

            var sb = new System.Text.StringBuilder();
            sb.Append(preview);
            for (var i = 0; i < row.Counts.Length; i++)
            {
                sb.Append(" | ").Append(snapshotNames[i]).Append(' ');
                if (row.Present[i])
                {
                    var dCount = i == 0 ? 0 : row.Counts[i] - row.Counts[i - 1];
                    var dByte = i == 0 ? 0L : row.Bytes[i] - row.Bytes[i - 1];
                    sb.Append(row.Counts[i].ToString("N0"))
                        .Append('(').Append(DeltaCountText(dCount)).Append(") / ")
                        .Append(FormatBytes(row.Bytes[i]))
                        .Append('(').Append(FormatBytes(dByte)).Append(')');
                }
                else
                {
                    sb.Append("无");
                }
            }
            sb.Append(" | [").Append(VumsStringSourceHelper.Label(row.Source)).Append("] ").Append(StatusLabel(row.Status));

            VumsEditorStyles.CopyableRow(
                window,
                EditorGUIUtility.singleLineHeight * 4f,
                sb.ToString(),
                () =>
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var sourceLabel = VumsStringSourceHelper.Label(row.Source);
                        var suggestion = VumsStringSourceHelper.Suggestion(row.Source);
                        var tip = (isTruncated ? preview : row.Value)
                            + "\n来源：" + sourceLabel
                            + "\n建议：" + suggestion;
                        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                        {
                            GUILayout.Label(new GUIContent(displayedPreview, tip), EditorStyles.wordWrappedLabel);
                            GUILayout.Label("[" + sourceLabel + "]", VumsEditorStyles.MutedLabel);
                        }

                        for (var i = 0; i < row.Counts.Length; i++)
                            DrawSeriesCell(row, i);
                        DrawColoredStatus(row.Status, 48f);
                    }
                });
        }

        private static void DrawSeriesCell(DuplicateStringSeries row, int i)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(76f)))
            {
                if (row.Present[i])
                {
                    GUILayout.Label(row.Counts[i].ToString("N0"), EditorStyles.boldLabel);
                    var dCount = i == 0 ? 0 : row.Counts[i] - row.Counts[i - 1];
                    DrawDeltaMini(DeltaCountText(dCount), dCount);
                    GUILayout.Space(2f);
                    GUILayout.Label(FormatBytes(row.Bytes[i]), EditorStyles.miniLabel);
                    var dByte = i == 0 ? 0L : row.Bytes[i] - row.Bytes[i - 1];
                    DrawDeltaMini(FormatBytes(dByte), dByte);
                }
                else
                {
                    GUILayout.Label("无", VumsEditorStyles.MutedLabel);
                    GUILayout.Label("—", VumsEditorStyles.MutedLabel);
                    GUILayout.Space(2f);
                    GUILayout.Label("无", VumsEditorStyles.MutedLabel);
                    GUILayout.Label("—", VumsEditorStyles.MutedLabel);
                }
            }
        }

        private static void DrawDeltaMini(string text, long delta)
        {
            GUI.contentColor = DeltaColor(delta);
            GUILayout.Label(text, EditorStyles.miniLabel);
            GUI.contentColor = Color.white;
        }

        private static string DeltaCountText(int delta)
        {
            return delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta.ToString("N0");
        }

        private static void DrawColoredStatus(DuplicateDeltaStatus status, float width)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(width)))
            {
                GUI.contentColor = StatusColor(status);
                GUILayout.Label(StatusLabel(status));
                GUI.contentColor = Color.white;
            }
        }

        private static Color DeltaColor(long delta)
        {
            if (delta > 0)
                return new Color(0.88f, 0.30f, 0.30f, 1f);
            if (delta < 0)
                return new Color(0.40f, 0.78f, 0.40f, 1f);
            return GUI.skin.label.normal.textColor;
        }

        private static Color StatusColor(DuplicateDeltaStatus status)
        {
            switch (status)
            {
                case DuplicateDeltaStatus.New:
                case DuplicateDeltaStatus.Rising:
                    return new Color(0.88f, 0.30f, 0.30f, 1f);
                case DuplicateDeltaStatus.Removed:
                case DuplicateDeltaStatus.Falling:
                    return new Color(0.40f, 0.78f, 0.40f, 1f);
                case DuplicateDeltaStatus.Volatile:
                default:
                    return GUI.skin.label.normal.textColor;
            }
        }

        private static string StatusLabel(DuplicateDeltaStatus status)
        {
            switch (status)
            {
                case DuplicateDeltaStatus.New: return "新增";
                case DuplicateDeltaStatus.Removed: return "消失";
                case DuplicateDeltaStatus.Rising: return "升";
                case DuplicateDeltaStatus.Falling: return "降";
                case DuplicateDeltaStatus.Volatile: return "波动";
                default: return "持平";
            }
        }

        private static List<RetentionPathGroup> BuildRetentionPathGroups(IEnumerable<LeakedObjectInfo> objects)
        {
            return objects
                .GroupBy(item => BuildNormalizedPathKey(item.RetentionPathNodes), StringComparer.Ordinal)
                .Select(group => new RetentionPathGroup
                {
                    Key = group.Key,
                    Representative = group.First(),
                    Objects = group.ToList(),
                })
                .OrderByDescending(group => group.Objects.Count)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
        }

        private static string BuildNormalizedPathKey(string[] nodes)
        {
            if (nodes == null || nodes.Length == 0)
                return "(无路径)";
            return string.Join("\u001F", nodes.Select(NormalizeRetentionNode));
        }

        private static string NormalizeRetentionNode(string node)
        {
            if (string.IsNullOrEmpty(node))
                return "(空节点)";
            var normalized = Regex.Replace(node, @"0x[0-9A-Fa-f]+", "0x*");
            normalized = Regex.Replace(normalized, @"\[(?:\d+|0x[0-9A-Fa-f]+)\]", "[*]");
            normalized = Regex.Replace(normalized, @"\bArray Element \d+ of\b", "Array Element * of");
            normalized = Regex.Replace(normalized, @"\b(?:List|Collection) Element \d+ of\b", "Collection Element * of");
            return normalized.Trim();
        }

        private static string GetRootSummary(string[] nodes)
        {
            if (nodes == null || nodes.Length == 0)
                return "无可用路径";

            // 路径按“目标对象 → 持有者 → ... → GC Root”排列。
            // 聚合标题显示界面从上往下第二步（目标对象的直接持有者），更容易区分业务来源。
            var summaryIndex = nodes.Length >= 2 ? 1 : 0;
            return NormalizeRetentionNode(nodes[summaryIndex]);
        }

        private static void DrawLeakedObjectRetentionNodes(EditorWindow window, LeakedObjectInfo leakedObject)
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

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                for (var depth = 0; depth < nodes.Length; depth++)
                {
                    if (depth > 0 && !leakedObject.RetentionNodeExpanded[depth - 1])
                        break;

                    var nodeText = nodes[depth];
                    VumsEditorStyles.CopyableRow(
                        window,
                        EditorGUIUtility.singleLineHeight,
                        nodeText,
                        () =>
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(depth * 18f);
                                if (depth < nodes.Length - 1)
                                {
                                    leakedObject.RetentionNodeExpanded[depth] = EditorGUILayout.Foldout(
                                        leakedObject.RetentionNodeExpanded[depth], nodeText, true);
                                }
                                else
                                {
                                    GUILayout.Space(14f);
                                    GUILayout.Label(nodeText, EditorStyles.wordWrappedLabel);
                                }
                            }
                        });
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
            public Dictionary<string, DuplicateStringStat> DuplicateStats;
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
            public List<RetentionPathGroup> PathGroups = new List<RetentionPathGroup>();
            public bool Expanded;
        }

        private sealed class LeakedObjectInfo
        {
            public string TypeName;
            public ulong Address;
            public string[] RetentionPathNodes;
            public bool[] RetentionNodeExpanded = Array.Empty<bool>();
        }

        private sealed class RetentionPathGroup
        {
            public string Key;
            public LeakedObjectInfo Representative;
            public List<LeakedObjectInfo> Objects;
            public bool Expanded;
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
