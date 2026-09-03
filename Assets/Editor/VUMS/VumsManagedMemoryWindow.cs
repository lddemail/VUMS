using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Editor window that drives the UMS (Unity Memory Snapshot) library to detect
    /// leaked managed shells (UnityEngine.Object instances whose native object is gone)
    /// inside a .snap memory snapshot captured by the Unity Memory Profiler.
    /// </summary>
    public class VumsManagedMemoryWindow : EditorWindow
    {
        private string _snapPath = "";
        private string _captureTimeText = "-";
        private string _platformText = "-";
        private const int LeakPageSize = 20;
        // 大对象引用路径 Tab 展示前 N 大对象；改这里即可调整展示数量
        private const int TopLargeObjectCount = 30;
        // 重复字符串 Tab 展示前 N 个重复串；改这里即可调整展示数量
        private const int TopDuplicateStringCount = 50;
        private const long LargeNativeObjectThreshold = 2L * 1024 * 1024;

        // 概览指标（与 SnapshotDiff 概览保持一致，便于横向比对）
        private long _duplicateAvoidableBytes;
        private long _top20LargeObjectTotalBytes;
        private ProfileTargetMemoryStats _nativeStats;
        private bool _hasNativeStats;

        private string _managedLeakResultText = "请选择一个 .snap 快照文件。";
        private DuplicateStringStat[] _duplicateStringStats = Array.Empty<DuplicateStringStat>();
        private bool _hasDuplicateStringResult;
        private readonly string[] _resultTabs =
        {
            "概览", "托管内存泄漏", "重复字符串", "大对象引用路径", "程序集内存", "静态字段持有",
        };
        private Vector2 _scroll;
        private int _selectedResultTab;
        private bool _analyzing;
        private bool _hasManagedLeakResult;
        private int _managedObjectCount;
        private int _leakPage;
        private readonly List<LeakedObjectInfo> _leakedObjects = new List<LeakedObjectInfo>();
        private List<RetentionPathGroup> _leakGroups = new List<RetentionPathGroup>();
        private int _leakDisplayMode;
        private readonly string[] _leakDisplayModes = { "按路径聚合", "按对象查看" };
        private readonly HashSet<string> _expandedLeakGroupKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _showLeakGroupObjectKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<RetainedObjectInfo> _largeObjects = new List<RetainedObjectInfo>();
        private string[] _largeObjectOptions = Array.Empty<string>();
        private bool[] _retentionNodeExpanded = Array.Empty<bool>();
        private int _selectedLargeObjectIndex;
        private readonly List<NativeTypeStat> _nativeTypeStats = new List<NativeTypeStat>();
        private readonly List<DuplicateNativeResource> _duplicateNativeResources = new List<DuplicateNativeResource>();
        private readonly List<NativeObjectInfo> _largeNativeObjects = new List<NativeObjectInfo>();
        private readonly HashSet<string> _expandedNativeTypeKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _expandedNativeTypeLargeObjectKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _expandedLargeObjectTypeKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ResidentNativeGroup> _residentNativeGroups = new List<ResidentNativeGroup>();
        private bool _residentSectionExpanded = false;
        private readonly HashSet<string> _expandedResidentTypeKeys = new HashSet<string>(StringComparer.Ordinal);
        private bool _duplicateSectionExpanded = false;
        private bool _largeObjectSectionExpanded = false;
        private int _residentObjectCount;
        private long _residentTotalBytes;

        // 按程序集聚合的托管内存（用于回答“内存是 C# 侧还是 Lua 侧吃掉的”）
        private readonly List<AssemblyMemoryStat> _assemblyStats = new List<AssemblyMemoryStat>();
        private long _assemblyTotalBytes;
        private int _assemblyObjectCount;
        private long _managedStringBytes;
        private int _managedStringCount;
        private bool _hasAssemblyResult;

        // 静态字段直接持有：定位“哪个静态字段吃掉了最多内存”，是泄漏根因最直接的入口
        private readonly List<StaticFieldHoldStat> _staticFieldStats = new List<StaticFieldHoldStat>();
        private readonly List<StaticFieldClassStat> _staticFieldClassStats = new List<StaticFieldClassStat>();
        private readonly HashSet<string> _expandedStaticFieldClassKeys = new HashSet<string>(StringComparer.Ordinal);
        private long _staticFieldTotalBytes;
        private int _staticFieldRootCount;
        private bool _hasStaticFieldResult;

        // 泄漏来源分类：按每个泄漏 Shell 首次被发现时的引用边（LoadedReason）聚合
        private int[] _leakReasonCounts = new int[4];

        [MenuItem("VUMS/ManagedMemoryLeakAnalysis", false, 2)]
        public static void OpenWindow()
        {
            var window = GetWindow<VumsManagedMemoryWindow>(true, "Managed Memory Leak Analysis", true);
            window.minSize = new Vector2(560, 520);
            window.Show();
        }

        [MenuItem("VUMS/Settings", false, 0)]
        public static void OpenSettings()
        {
            VumsSettingsWindow.ShowWindow();
        }

        private void OnGUI()
        {
            VumsEditorStyles.EnsureInitialized();

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginDisabledGroup(_analyzing);
                    if (GUILayout.Button(
                            _analyzing ? "正在分析..." : "选择并分析",
                            VumsEditorStyles.PrimaryButton,
                            GUILayout.Width(150f)))
                    {
                        var startDir = string.IsNullOrEmpty(_snapPath) ? "" : Path.GetDirectoryName(_snapPath);
                        var picked = EditorUtility.OpenFilePanel("选择内存快照文件", startDir, "snap");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            _snapPath = picked;
                            BeginAnalysis();
                        }
                    }
                    EditorGUI.EndDisabledGroup();

                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.TextField(
                        string.IsNullOrEmpty(_snapPath) ? "尚未选择快照" : _snapPath,
                        GUILayout.Height(30f));
                    EditorGUI.EndDisabledGroup();
                }

                if (_analyzing)
                    VumsEditorStyles.DrawStatus("正在解析快照并构建引用关系，请稍候。", MessageType.Info);
                else if (!_hasManagedLeakResult && !string.IsNullOrEmpty(_managedLeakResultText))
                    VumsEditorStyles.DrawStatus(_managedLeakResultText, MessageType.Info);
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            _selectedResultTab = VumsEditorStyles.DrawTabs(_selectedResultTab, _resultTabs);
            GUILayout.Space(6f);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUILayout.VerticalScope())
            {
                switch (_selectedResultTab)
                {
                    case 0:
                        DrawOverviewTab();
                        break;
                    case 1:
                        DrawManagedLeakTab();
                        break;
                    case 2:
                        DrawDuplicateStringTab();
                        break;
                    case 3:
                        DrawLargeObjectRetentionTab();
                        break;
                    case 4:
                        DrawAssemblyMemoryTab();
                        break;
                    case 5:
                        DrawStaticFieldTab();
                        break;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawOverviewTab()
        {
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("快照信息", "当前分析结果对应的采集环境。");
                OverviewValueRow("采集时间", _captureTimeText);
                OverviewValueRow("目标平台", _platformText);
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("托管内存", "用于快速判断托管对象规模与重点排查线索。");
                OverviewValueRow("托管对象总数", $"{_managedObjectCount:N0}");
                OverviewValueRow("泄漏 Managed Shell", $"{_leakedObjects.Count:N0}");
                OverviewValueRow($"重复字符串可避免内存（Top {TopDuplicateStringCount}）", FormatBytes(_duplicateAvoidableBytes));
                OverviewValueRow($"Top {TopLargeObjectCount} 大对象总大小", FormatBytes(_top20LargeObjectTotalBytes));
                OverviewValueRow("静态字段直接持有", _staticFieldStats.Count > 0
                    ? $"{_staticFieldRootCount:N0} 个对象 / {FormatBytes(_staticFieldTotalBytes)}"
                    : "-");
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("原生内存", "快照记录的 Unity 原生内存统计。");
                if (_hasNativeStats)
                {
                    OverviewValueRow("总已用内存", FormatBytes((long)_nativeStats.TotalUsedMemory));
                    OverviewValueRow("GC 堆已用", FormatBytes((long)_nativeStats.GcHeapUsedMemory));
                    OverviewValueRow("GC 堆保留", FormatBytes((long)_nativeStats.GcHeapReservedMemory));
                    OverviewValueRow("图形 (Graphics)", FormatBytes((long)_nativeStats.GraphicsUsedMemory));
                    OverviewValueRow("音频 (Audio)", FormatBytes((long)_nativeStats.AudioUsedMemory));
                    OverviewValueRow("Profiler 已用", FormatBytes((long)_nativeStats.ProfilerUsedMemory));
                    OverviewValueRow("Memory Profiler 已用", FormatBytes((long)_nativeStats.MemoryProfilerUsedMemory));
                    VumsEditorStyles.DrawDivider();
                    var realUsed = Math.Max(0L,
                        (long)_nativeStats.TotalUsedMemory -
                        (long)_nativeStats.ProfilerUsedMemory -
                        (long)_nativeStats.MemoryProfilerUsedMemory);
                    OverviewValueRow("真实内存占用（近似）", FormatBytes(realUsed));
                    GUILayout.Space(4f);
                    GUILayout.Label(
                        "计算口径：总已用内存 − Profiler 已用 − Memory Profiler 已用。该值用于剔除分析器开销，不等同于 OS 级 RSS/PSS。",
                        VumsEditorStyles.SectionDescription);
                    GUILayout.Space(2f);
                    GUILayout.Label(
                        "GC 堆已用 = 当前 Mono/IL2CPP GC 堆实际占用；GC 堆保留 = Unity已经向系统申请并保留下来的堆容量。",
                        VumsEditorStyles.SectionDescription);
                }
                else
                {
                    VumsEditorStyles.DrawStatus(
                        _hasManagedLeakResult ? "当前快照未提供原生内存统计。" : _managedLeakResultText,
                        MessageType.Info);
                }
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            DrawUnityObjectsRecommendations();
        }

        private static void OverviewValueRow(string label, string value)
        {
            VumsEditorStyles.DrawMetricRow(label, value, 220f);
        }

        private void DrawUnityObjectsRecommendations()
        {
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("优化建议");

                DrawResidentNativeSection();
                DrawDuplicateNativeSection();
                DrawLargeNativeSection();
            }
        }

        /// <summary>
        /// 重复资源（同名同大小）单独成组展示。与常驻对象互斥——常驻对象在快照中属正常，已在
        /// CollectNativeObjectRecommendations 阶段从候选中排除，这里只看非常驻的疑似重复资源。
        /// </summary>
        private void DrawDuplicateNativeSection()
        {
            if (_duplicateNativeResources.Count == 0)
                return;

            var perType = _duplicateNativeResources
                .GroupBy(item => item.TypeName, StringComparer.Ordinal)
                .Select(group => new
                {
                    TypeName = group.Key,
                    Groups = group
                        .OrderByDescending(item => item.PotentialDuplicateSize)
                        .ToArray(),
                })
                .OrderByDescending(entry => entry.Groups.Sum(item => item.PotentialDuplicateSize))
                .ToArray();
            var typeCount = perType.Length;
            var totalGroups = _duplicateNativeResources.Count;
            var totalPotential = perType.Sum(entry => entry.Groups.Sum(item => item.PotentialDuplicateSize));

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
            {
                _duplicateSectionExpanded = EditorGUILayout.Foldout(
                    _duplicateSectionExpanded,
                    $"重复资源 | {totalGroups:N0} 组（涉及 {typeCount:N0} 个类型）| 累计疑似可释放 {FormatBytes(totalPotential)}",
                    true,
                    VumsEditorStyles.Foldout);
                if (!_duplicateSectionExpanded)
                    return;

                GUILayout.Label(
                    "名称和单体大小都相同的资源候选；若释放一份重复副本，理论上可释放列出的字节数。",
                    VumsEditorStyles.SectionDescription);
                GUILayout.Space(2f);

                foreach (var entry in perType)
                {
                    var entryPotential = entry.Groups.Sum(item => item.PotentialDuplicateSize);
                    var headerText =
                        $"{entry.TypeName} | {entry.Groups.Length:N0} 组 | 累计疑似可释放 {FormatBytes(entryPotential)}";

                    var expanded = _expandedNativeTypeKeys.Contains(entry.TypeName);
                    expanded = EditorGUILayout.Foldout(expanded, headerText, true);
                    SetGroupState(_expandedNativeTypeKeys, entry.TypeName, expanded);
                    if (!expanded)
                        continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(18f);
                        using (new EditorGUILayout.VerticalScope())
                        {
                            foreach (var duplicate in entry.Groups)
                            {
                                var duplicateText =
                                    $"{duplicate.Name} | {duplicate.Count:N0} 个 × {FormatBytes(duplicate.SingleSize)} | 合计 {FormatBytes(duplicate.TotalSize)} | 疑似额外 {FormatBytes(duplicate.PotentialDuplicateSize)}";
                                VumsEditorStyles.CopyableRow(
                                    this,
                                    EditorGUIUtility.singleLineHeight,
                                    duplicateText,
                                    () => EditorGUILayout.SelectableLabel(
                                        duplicateText,
                                        GUILayout.Height(EditorGUIUtility.singleLineHeight)));
                            }
                        }
                    }
                }
            }

            GUILayout.Space(4f);
        }

        /// <summary>
        /// 重点单体资源（Native 占用 ≥ LargeNativeObjectThreshold）单独成组展示。与常驻对象、
        /// 重复资源并列，但关注维度不同——这里是单项偏大，需逐项核查是否合理
        /// （贴图过采样 / 未压缩 / 模型面数过高等）。
        /// </summary>
        private void DrawLargeNativeSection()
        {
            if (_largeNativeObjects.Count == 0)
                return;

            var perType = _largeNativeObjects
                .GroupBy(item => item.TypeName, StringComparer.Ordinal)
                .Select(group => new
                {
                    TypeName = group.Key,
                    Objects = group
                        .OrderByDescending(item => item.NativeSize)
                        .ToArray(),
                })
                .OrderByDescending(entry => entry.Objects.Sum(item => item.NativeSize))
                .ToArray();
            var typeCount = perType.Length;
            var totalCount = _largeNativeObjects.Count;
            var totalBytes = perType.Sum(entry => entry.Objects.Sum(item => item.NativeSize));

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
            {
                _largeObjectSectionExpanded = EditorGUILayout.Foldout(
                    _largeObjectSectionExpanded,
                    $"重点单体资源（≥ {FormatBytes(LargeNativeObjectThreshold)}）| {totalCount:N0} 个（涉及 {typeCount:N0} 个类型）| 累计 {FormatBytes(totalBytes)}",
                    true,
                    VumsEditorStyles.Foldout);
                if (!_largeObjectSectionExpanded)
                    return;

                GUILayout.Label(
                    "单体 Native 占用超过阈值的资源，逐项核查是否合理。",
                    VumsEditorStyles.SectionDescription);
                GUILayout.Space(2f);

                foreach (var entry in perType)
                {
                    var entryBytes = entry.Objects.Sum(item => item.NativeSize);
                    var headerText =
                        $"{entry.TypeName} | {entry.Objects.Length:N0} 个 | 累计 {FormatBytes(entryBytes)}";

                    var expanded = _expandedNativeTypeLargeObjectKeys.Contains(entry.TypeName);
                    expanded = EditorGUILayout.Foldout(expanded, headerText, true);
                    SetGroupState(_expandedNativeTypeLargeObjectKeys, entry.TypeName, expanded);
                    if (!expanded)
                        continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(18f);
                        using (new EditorGUILayout.VerticalScope())
                        {
                            foreach (var item in entry.Objects)
                            {
                                var largeText =
                                    $"{FormatBytes(item.NativeSize),10} | {item.Name} | Instance ID {item.InstanceId}";
                                VumsEditorStyles.CopyableRow(
                                    this,
                                    EditorGUIUtility.singleLineHeight,
                                    largeText,
                                    () => EditorGUILayout.SelectableLabel(
                                        largeText, GUILayout.Height(EditorGUIUtility.singleLineHeight)));
                            }
                        }
                    }
                }
            }

            GUILayout.Space(4f);
        }

        /// <summary>
        /// 常驻 Native 对象（DontDestroyOnLoad / HideAndDontSave / Unity 内部管理器）单独成组展示。
        /// 这类对象设计上就不销毁，与“疑似重复”“重点单体”混在一起会误导判断。
        /// </summary>
        private void DrawResidentNativeSection()
        {
            if (_residentNativeGroups.Count == 0)
                return;

            var sectionText =
                $"常驻对象（设计上不销毁） | {_residentNativeGroups.Count:N0} 个类型 | " +
                $"{_residentObjectCount:N0} 个对象 | {FormatBytes(_residentTotalBytes)}";

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
            {
                _residentSectionExpanded = EditorGUILayout.Foldout(
                    _residentSectionExpanded, sectionText, true, VumsEditorStyles.Foldout);
                if (!_residentSectionExpanded)
                    return;

                GUILayout.Label(
                    "这些对象带 DontDestroyOnLoad、HideAndDontSave 或属于 Unity 内部管理器，按设计就不随场景卸载，"
                    + "出现在快照中属正常现象，已从下方优化建议中排除。",
                    VumsEditorStyles.SectionDescription);
                GUILayout.Space(2f);

                foreach (var group in _residentNativeGroups)
                {
                    var groupText =
                        $"{group.TypeName} | {group.Objects.Count:N0} 个 | {FormatBytes(group.TotalSize)} | " +
                        $"DontDestroyOnLoad {group.DontDestroyOnLoadCount:N0} | " +
                        $"HideAndDontSave {group.HideAndDontSaveCount:N0} | " +
                        $"管理器 {group.UnityManagerCount:N0}";

                    VumsEditorStyles.CopyableRow(
                        this,
                        EditorGUIUtility.singleLineHeight + 4f,
                        groupText,
                        () =>
                        {
                            var expanded = _expandedResidentTypeKeys.Contains(group.TypeName);
                            expanded = EditorGUILayout.Foldout(expanded, groupText, true);
                            SetGroupState(_expandedResidentTypeKeys, group.TypeName, expanded);
                        });

                    if (!_expandedResidentTypeKeys.Contains(group.TypeName))
                        continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(18f);
                        using (new EditorGUILayout.VerticalScope())
                        {
                            var shown = Math.Min(20, group.Objects.Count);
                            for (var index = 0; index < shown; index++)
                            {
                                var item = group.Objects[index];
                                var objectText =
                                    $"{FormatBytes(item.NativeSize),10} | {item.Name} | " +
                                    $"{item.ResidentReason} | Instance ID {item.InstanceId}";
                                VumsEditorStyles.CopyableRow(
                                    this,
                                    EditorGUIUtility.singleLineHeight,
                                    objectText,
                                    () => EditorGUILayout.SelectableLabel(
                                        objectText, GUILayout.Height(EditorGUIUtility.singleLineHeight)));
                            }

                            if (group.Objects.Count > shown)
                                GUILayout.Label(
                                    $"还有 {group.Objects.Count - shown:N0} 个未列出（按大小降序）。",
                                    EditorStyles.miniLabel);
                        }
                    }
                }
            }

            GUILayout.Space(4f);
        }

        private void DrawManagedLeakTab()
        {
            if (!_hasManagedLeakResult)
            {
                VumsEditorStyles.DrawEmptyState("暂无泄漏分析结果", _managedLeakResultText);
                return;
            }

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("Managed Shell 检测", "Native 对象已经销毁但托管包装对象仍被引用时，会被识别为泄漏 Shell。");
                OverviewValueRow("托管对象总数", $"{_managedObjectCount:N0}");
                OverviewValueRow("泄漏 Managed Shell", $"{_leakedObjects.Count:N0}");
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    "泄漏来源分类",
                    "按每个泄漏 Shell 首次被发现时的引用边（LoadedReason）归类：静态字段占比高 = 全局单例 / 静态集合 / 静态事件未清理；GC Root = 直接 GC 根（线程栈 / GCHandle / 终结器等）。");
                var total = _leakedObjects.Count;
                var reasons = new[]
                {
                    LoadedReason.StaticField, LoadedReason.GcRoot, LoadedReason.InstanceField, LoadedReason.ArrayElement,
                };
                foreach (var reason in reasons)
                {
                    var count = _leakReasonCounts[(int)reason];
                    if (count == 0)
                        continue;

                    var ratio = total > 0 ? count * 100.0 / total : 0.0;
                    OverviewValueRow(GetLeakReasonLabel(reason), $"{count:N0} 个（{ratio:F1}%）");
                }
            }

            if (_leakedObjects.Count == 0)
            {
                GUILayout.Space(VumsEditorStyles.SectionSpacing);
                VumsEditorStyles.DrawEmptyState("未发现泄漏 Shell", "当前快照中没有检测到泄漏的 Managed Shell。", MessageType.None);
                return;
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("查看方式");
                _leakDisplayMode = VumsEditorStyles.DrawTabs(_leakDisplayMode, _leakDisplayModes);
            }
            GUILayout.Space(4f);

            if (_leakDisplayMode == 0)
            {
                DrawAggregatedLeakGroups(_leakedObjects, _leakGroups);
                return;
            }

            var pageCount = Mathf.Max(1, Mathf.CeilToInt(_leakedObjects.Count / (float)LeakPageSize));
            _leakPage = Mathf.Clamp(_leakPage, 0, pageCount - 1);
            var startIndex = _leakPage * LeakPageSize;
            var endIndex = Mathf.Min(startIndex + LeakPageSize, _leakedObjects.Count);

            for (var index = startIndex; index < endIndex; index++)
            {
                var leakedObject = _leakedObjects[index];
                var objectHeaderText = $"{leakedObject.TypeName} @ 0x{leakedObject.Address:X}  [{GetLeakReasonLabel(leakedObject.Reason)}]";
                using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
                {
                    VumsEditorStyles.CopyableRow(
                        this,
                        EditorGUIUtility.singleLineHeight + 8f,
                        objectHeaderText,
                        () =>
                        {
                            leakedObject.Expanded = EditorGUILayout.Foldout(
                                leakedObject.Expanded, objectHeaderText, true);
                        });
                    if (leakedObject.Expanded)
                    {
                        GUILayout.Space(3);
                        DrawLeakedObjectRetentionNodes(leakedObject);
                    }
                }
            }

            if (_leakedObjects.Count > LeakPageSize)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginDisabledGroup(_leakPage <= 0);
                    if (GUILayout.Button("上一页", GUILayout.Width(80)))
                    {
                        _leakPage--;
                        _scroll = Vector2.zero;
                    }
                    EditorGUI.EndDisabledGroup();

                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"第 {_leakPage + 1:N0} / {pageCount:N0} 页（每页 {LeakPageSize} 个）");
                    GUILayout.FlexibleSpace();

                    EditorGUI.BeginDisabledGroup(_leakPage >= pageCount - 1);
                    if (GUILayout.Button("下一页", GUILayout.Width(80)))
                    {
                        _leakPage++;
                        _scroll = Vector2.zero;
                    }
                    EditorGUI.EndDisabledGroup();
                }
            }
        }

        private void DrawAggregatedLeakGroups(List<LeakedObjectInfo> objects, List<RetentionPathGroup> groups)
        {
            EditorGUILayout.LabelField(
                $"{objects.Count:N0} 个泄漏对象聚合为 {groups.Count:N0} 条路径（按数量降序）。",
                EditorStyles.miniLabel);

            foreach (var group in groups)
            {
                using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
                {
                    var groupHeaderText =
                        $"{group.Objects.Count:N0} 个（{group.Objects.Count * 100f / objects.Count:F1}%） | {GetRootSummary(group.Representative.RetentionPathNodes)} | {GetLeakReasonLabel(group.Representative.Reason)}";
                    VumsEditorStyles.CopyableRow(
                        this,
                        EditorGUIUtility.singleLineHeight + 8f,
                        groupHeaderText,
                        () =>
                        {
                            var expanded = _expandedLeakGroupKeys.Contains(group.Key);
                            expanded = EditorGUILayout.Foldout(expanded, groupHeaderText, true);
                            SetGroupState(_expandedLeakGroupKeys, group.Key, expanded);
                        });
                    if (!_expandedLeakGroupKeys.Contains(group.Key))
                        continue;

                    DrawLeakedObjectRetentionNodes(group.Representative);
                }
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

        private static void SetGroupState(HashSet<string> states, string key, bool enabled)
        {
            if (enabled)
                states.Add(key);
            else
                states.Remove(key);
        }

        private void DrawLeakedObjectRetentionNodes(LeakedObjectInfo leakedObject)
        {
            var nodes = leakedObject.RetentionPathNodes;
            if (nodes == null || nodes.Length == 0)
            {
                EditorGUILayout.HelpBox("(无)", MessageType.None);
                return;
            }

            if (leakedObject.RetentionNodeExpanded == null ||
                leakedObject.RetentionNodeExpanded.Length != nodes.Length)
            {
                leakedObject.RetentionNodeExpanded = new bool[nodes.Length];
                leakedObject.RetentionNodeExpanded[0] = true;
            }

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
            {
                for (var depth = 0; depth < nodes.Length; depth++)
                {
                    if (depth > 0 && !leakedObject.RetentionNodeExpanded[depth - 1])
                        break;

                    var nodeText = nodes[depth];
                    VumsEditorStyles.CopyableRow(
                        this,
                        EditorGUIUtility.singleLineHeight,
                        nodeText,
                        () =>
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(depth * 18f);
                                var hasParentNode = depth < nodes.Length - 1;
                                if (hasParentNode)
                                {
                                    leakedObject.RetentionNodeExpanded[depth] = EditorGUILayout.Foldout(
                                        leakedObject.RetentionNodeExpanded[depth],
                                        nodeText,
                                        true);
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

        private void DrawDuplicateStringTab()
        {
            if (!_hasDuplicateStringResult || _duplicateStringStats.Length == 0)
            {
                VumsEditorStyles.DrawEmptyState(
                    "暂无重复字符串",
                    _analyzing ? "正在检测重复字符串..." : "请选择一个 .snap 快照文件开始分析。");
                return;
            }

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    $"重复字符串 Top {TopDuplicateStringCount}",
                    "仅统计快照中出现次数 > 1 的托管字符串；每行“可避免”= 总大小 − 单个实例大小，即重复分配浪费的部分。");
                OverviewValueRow("重复字符串可避免内存", FormatBytes(_duplicateAvoidableBytes));
                OverviewValueRow("重复字符串条目数", $"{_duplicateStringStats.Length:N0}");
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            DrawDuplicateSourceSummary();

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    $"重复字符串明细（{_duplicateStringStats.Length:N0} 条）",
                    "每行标注推断来源；悬停字符串可看完整内容与对应优化建议，右键复制整行。");
                foreach (var stat in _duplicateStringStats)
                {
                    var preview = stat.Value
                        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                    if (preview.Length > 120)
                        preview = preview.Substring(0, 120) + "...";

                    var sourceLabel = VumsStringSourceHelper.Label(stat.Source);
                    var suggestion = VumsStringSourceHelper.Suggestion(stat.Source);
                    var copyText =
                        $"{stat.Count,6:N0} x | 总计 {FormatBytes(stat.TotalBytes)} | 可避免 {FormatBytes(stat.DuplicateBytes)} | [{sourceLabel}] \"{stat.Value}\"";
                    var tooltip = $"完整内容：{stat.Value}\n来源：{sourceLabel}\n建议：{suggestion}";

                    VumsEditorStyles.CopyableRow(
                        this,
                        EditorGUIUtility.singleLineHeight * 2f,
                        copyText,
                        () =>
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Label($"{stat.Count:N0} x", GUILayout.Width(64f));
                                GUILayout.Label(FormatBytes(stat.TotalBytes), GUILayout.Width(88f));
                                GUILayout.Label(FormatBytes(stat.DuplicateBytes), GUILayout.Width(88f));
                                GUILayout.Label($"[{sourceLabel}]", VumsEditorStyles.MutedLabel, GUILayout.Width(96f));
                                GUILayout.Label(new GUIContent(preview, tooltip), EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
                            }
                        });
                }
            }
        }

        private void DrawDuplicateSourceSummary()
        {
            var groups = _duplicateStringStats
                .GroupBy(item => item.Source)
                .Select(g => new
                {
                    Source = g.Key,
                    Count = g.Count(),
                    Avoidable = g.Sum(item => item.DuplicateBytes),
                })
                .OrderByDescending(x => x.Avoidable)
                .ToArray();

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    "来源分类汇总",
                    "按推断来源聚合“可避免内存”并降序排列；优先修靠前的来源类别，收益最大。");
                foreach (var g in groups)
                {
                    var label = VumsStringSourceHelper.Label(g.Source);
                    OverviewValueRow($"[{label}]（{g.Count:N0} 条）", FormatBytes(g.Avoidable));
                    GUILayout.Label(
                        VumsStringSourceHelper.Suggestion(g.Source),
                        VumsEditorStyles.SectionDescription);
                    GUILayout.Space(2f);
                }
            }
        }

        private void DrawLargeObjectRetentionTab()
        {
            if (_largeObjectOptions.Length == 0)
            {
                VumsEditorStyles.DrawEmptyState(
                    "暂无大对象引用路径",
                    _analyzing ? "正在分析大对象引用路径..." : "请选择一个 .snap 快照文件开始分析。");
                return;
            }

            // 按类型聚合：直接看出“这个类型贡献了多少 MB”，并可下钻到单个对象
            DrawLargeObjectAggregation();

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                EditorGUI.BeginChangeCheck();
                _selectedLargeObjectIndex = EditorGUILayout.Popup(
                    "选择对象",
                    _selectedLargeObjectIndex,
                    _largeObjectOptions);
                if (EditorGUI.EndChangeCheck())
                    ResetRetentionTreeExpansion();
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            DrawRetentionTree(_largeObjects[_selectedLargeObjectIndex]);
        }

        /// <summary>
        /// 把 Top N 大对象按类型合并，汇总每类贡献的总字节与占比；展开类型可逐个查看对象，
        /// 点击对象即在下方引用链中定位它。重点回答“哪一类类型吃掉了最多内存”。
        /// </summary>
        private void DrawLargeObjectAggregation()
        {
            var total = _top20LargeObjectTotalBytes;
            var byType = _largeObjects
                .GroupBy(item => item.TypeName)
                .Select(g => new
                {
                    TypeName = g.Key,
                    Count = g.Count(),
                    Bytes = g.Sum(item => (long)item.SizeBytes),
                })
                .OrderByDescending(x => x.Bytes)
                .ToArray();

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    $"按类型聚合（Top {TopLargeObjectCount} 大对象）",
                    $"同类型大对象合并统计；展开可见每个对象，点击即在下方引用链定位。共 {byType.Length:N0} 个类型，合计 {FormatBytes(total)}。");
                foreach (var entry in byType)
                {
                    var ratio = total > 0 ? entry.Bytes * 100.0 / total : 0.0;
                    var headerText =
                        $"{entry.TypeName} | {entry.Count:N0} 个 | 合计 {FormatBytes(entry.Bytes)} | 占 {ratio:F1}%";
                    var expanded = _expandedLargeObjectTypeKeys.Contains(entry.TypeName);
                    expanded = EditorGUILayout.Foldout(expanded, headerText, true, VumsEditorStyles.Foldout);
                    SetGroupState(_expandedLargeObjectTypeKeys, entry.TypeName, expanded);
                    if (!expanded)
                        continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(18f);
                        using (new EditorGUILayout.VerticalScope())
                        {
                            foreach (var obj in _largeObjects.Where(item => item.TypeName == entry.TypeName))
                            {
                                var leaf = obj.RetentionPathNodes != null && obj.RetentionPathNodes.Length > 0
                                    ? obj.RetentionPathNodes[obj.RetentionPathNodes.Length - 1]
                                    : obj.TypeName;
                                var rowText =
                                    $"{FormatBytes(obj.SizeBytes),10} | @0x{obj.Address:X} | {leaf}";
                                VumsEditorStyles.CopyableRow(
                                    this,
                                    EditorGUIUtility.singleLineHeight,
                                    rowText,
                                    () =>
                                    {
                                        using (new EditorGUILayout.HorizontalScope())
                                        {
                                            GUILayout.Space(18f);
                                            GUILayout.Label(FormatBytes(obj.SizeBytes), GUILayout.Width(88f));
                                            GUILayout.Label($"@0x{obj.Address:X}", GUILayout.Width(120f));
                                            GUILayout.Label(leaf, GUILayout.ExpandWidth(true));
                                        }
                                    },
                                    () =>
                                    {
                                        _selectedLargeObjectIndex = _largeObjects.IndexOf(obj);
                                        ResetRetentionTreeExpansion();
                                        Repaint();
                                    });
                            }
                        }
                    }
                }
            }
        }

        private void DrawRetentionTree(RetainedObjectInfo retainedObject)
        {
            var nodes = retainedObject.RetentionPathNodes;
            if (nodes == null || nodes.Length == 0)
            {
                EditorGUILayout.HelpBox("该对象没有可用的引用保留路径。", MessageType.Info);
                return;
            }

            EnsureRetentionTreeExpansion(nodes.Length);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
            {
                for (var depth = 0; depth < nodes.Length; depth++)
                {
                    if (depth > 0 && !_retentionNodeExpanded[depth - 1])
                        break;

                    var nodeText = nodes[depth];
                    VumsEditorStyles.CopyableRow(
                        this,
                        EditorGUIUtility.singleLineHeight,
                        nodeText,
                        () =>
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(depth * 18f);
                                var hasParentNode = depth < nodes.Length - 1;
                                if (hasParentNode)
                                {
                                    _retentionNodeExpanded[depth] = EditorGUILayout.Foldout(
                                        _retentionNodeExpanded[depth],
                                        nodeText,
                                        true);
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

        private void EnsureRetentionTreeExpansion(int nodeCount)
        {
            if (_retentionNodeExpanded.Length == nodeCount)
                return;

            _retentionNodeExpanded = new bool[nodeCount];
            if (nodeCount > 0)
                _retentionNodeExpanded[0] = true;
        }

        private void ResetRetentionTreeExpansion()
        {
            var nodeCount = _largeObjects.Count == 0
                ? 0
                : _largeObjects[_selectedLargeObjectIndex].RetentionPathNodes.Length;
            _retentionNodeExpanded = new bool[nodeCount];
            if (nodeCount > 0)
                _retentionNodeExpanded[0] = true;
        }

        /// <summary>
        /// 按类型所属程序集聚合托管对象大小。对 XLua 项目，这一页直接回答
        /// “内存是 C# 侧还是 Lua 侧吃掉的”。
        /// </summary>
        private void CollectAssemblyStats(SnapshotFile file)
        {
            _assemblyStats.Clear();
            _assemblyTotalBytes = 0;
            _assemblyObjectCount = 0;
            _managedStringBytes = 0;
            _managedStringCount = 0;
            _hasAssemblyResult = false;

            var assemblies = ReadOptionalArray(() => file.TypeDescriptionAssemblies, "TypeDescriptions_Assembly");

            var byAssembly = new Dictionary<string, AssemblyMemoryStat>(StringComparer.Ordinal);
            var typeIndicesByAssembly = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

            foreach (var instance in file.AllManagedClassInstances)
            {
                if (instance.ObjectAddress == 0)
                    continue;

                var info = file.ParseManagedObjectInfo(instance.ObjectAddress);
                if (!info.IsKnownType || info.Size <= 0)
                    continue;

                var typeIndex = instance.TypeInfo.TypeIndex;
                var assembly = typeIndex >= 0 && typeIndex < assemblies.Length && !string.IsNullOrEmpty(assemblies[typeIndex])
                    ? assemblies[typeIndex]
                    : UnknownAssemblyLabel;

                if (!byAssembly.TryGetValue(assembly, out var stat))
                {
                    stat = new AssemblyMemoryStat
                    {
                        Assembly = assembly,
                        Category = ClassifyAssembly(assembly),
                    };
                    byAssembly.Add(assembly, stat);
                    typeIndicesByAssembly[assembly] = new HashSet<int>();
                }

                stat.ObjectCount++;
                stat.TotalBytes += info.Size;
                if (info.Size > stat.MaxObjectBytes)
                    stat.MaxObjectBytes = info.Size;
                typeIndicesByAssembly[assembly].Add(typeIndex);
            }

            // 托管字符串在快照中不作为普通对象实例出现（UMS 单独登记），单独成行统计，
            // 避免它们被算进某个具体程序集而扭曲结果。
            var maxStringBytes = 0L;
            foreach (var managedString in file.AllManagedStrings)
            {
                if (managedString.SizeBytes <= 0)
                    continue;

                _managedStringCount++;
                _managedStringBytes += managedString.SizeBytes;
                if (managedString.SizeBytes > maxStringBytes)
                    maxStringBytes = managedString.SizeBytes;
            }

            if (_managedStringCount > 0)
                byAssembly[ManagedStringAssemblyLabel] = new AssemblyMemoryStat
                {
                    Assembly = ManagedStringAssemblyLabel,
                    Category = AssemblyCategory.Runtime,
                    ObjectCount = _managedStringCount,
                    TypeCount = 1,
                    TotalBytes = _managedStringBytes,
                    MaxObjectBytes = maxStringBytes,
                };
            else
                typeIndicesByAssembly[ManagedStringAssemblyLabel] = new HashSet<int>();

            foreach (var pair in byAssembly)
            {
                if (typeIndicesByAssembly.TryGetValue(pair.Key, out var typeIndices))
                    pair.Value.TypeCount = typeIndices.Count;
                _assemblyTotalBytes += pair.Value.TotalBytes;
                _assemblyObjectCount += pair.Value.ObjectCount;
            }

            _assemblyStats.AddRange(byAssembly.Values
                .OrderByDescending(item => item.TotalBytes)
                .ThenBy(item => item.Assembly, StringComparer.Ordinal));
            _hasAssemblyResult = _assemblyStats.Count > 0;
        }

        /// <summary>
        /// 静态字段直接持有统计。对象首次被发现时的引用边若是某个静态字段，
        /// 就计入“被该静态字段直接持有”。按字段汇总总大小，直接指出
        /// “哪个全局单例 / 静态集合 / 静态事件在吃内存”。
        /// </summary>
        private void CollectStaticFieldStats(SnapshotFile file)
        {
            _staticFieldStats.Clear();
            _staticFieldTotalBytes = 0;
            _staticFieldRootCount = 0;
            _hasStaticFieldResult = false;

            var byField = new Dictionary<string, StaticFieldHoldStat>(StringComparer.Ordinal);

            foreach (var instance in file.AllManagedClassInstances)
            {
                if (instance.LoadedReason != LoadedReason.StaticField)
                    continue;
                if (instance.ObjectAddress == 0)
                    continue;

                RawManagedObjectInfo info;
                try
                {
                    info = file.ParseManagedObjectInfo(instance.ObjectAddress);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!info.IsKnownType || info.Size <= 0)
                    continue;

                var fieldIndex = instance.FieldIndexOrArrayOffset;
                if (!file.StaticFieldsToOwningTypes.TryGetValue(fieldIndex, out var owningTypeIndex))
                    continue;

                var declaringType = file.GetTypeName(owningTypeIndex);
                var fieldName = file.GetFieldName(fieldIndex);
                var key = $"{declaringType}.{fieldName}";

                if (!byField.TryGetValue(key, out var stat))
                {
                    stat = new StaticFieldHoldStat
                    {
                        DeclaringType = declaringType,
                        FieldName = fieldName,
                        Key = key,
                    };
                    byField.Add(key, stat);
                }

                stat.ObjectCount++;
                stat.TotalBytes += info.Size;
                _staticFieldRootCount++;
                _staticFieldTotalBytes += info.Size;
            }

            _staticFieldStats.AddRange(byField.Values
                .OrderByDescending(item => item.TotalBytes)
                .ThenBy(item => item.Key, StringComparer.Ordinal));

            // 按声明类二次聚合：一个类下的所有静态字段合计，用于「按声明类」视图
            _staticFieldClassStats.Clear();
            var byClass = new Dictionary<string, StaticFieldClassStat>(StringComparer.Ordinal);
            foreach (var stat in _staticFieldStats)
            {
                if (!byClass.TryGetValue(stat.DeclaringType, out var classStat))
                {
                    classStat = new StaticFieldClassStat { DeclaringType = stat.DeclaringType };
                    byClass.Add(stat.DeclaringType, classStat);
                }

                classStat.FieldCount++;
                classStat.ObjectCount += stat.ObjectCount;
                classStat.TotalBytes += stat.TotalBytes;
                classStat.Fields.Add(stat);
            }

            _staticFieldClassStats.AddRange(byClass.Values
                .OrderByDescending(item => item.TotalBytes)
                .ThenBy(item => item.DeclaringType, StringComparer.Ordinal));
            _hasStaticFieldResult = _staticFieldStats.Count > 0;
        }

        private static string GetLeakReasonLabel(LoadedReason reason)
        {
            switch (reason)
            {
                case LoadedReason.GcRoot:
                    return "GC Root / 句柄";
                case LoadedReason.StaticField:
                    return "静态字段";
                case LoadedReason.InstanceField:
                    return "实例字段";
                case LoadedReason.ArrayElement:
                    return "数组元素";
                default:
                    return reason.ToString();
            }
        }

        private void DrawStaticFieldTab()
        {
            if (!_hasStaticFieldResult)
            {
                VumsEditorStyles.DrawEmptyState(
                    "暂无静态字段持有统计",
                    _analyzing ? "正在统计静态字段持有..." : "请选择一个 .snap 快照文件开始分析。");
                return;
            }

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    "静态字段直接持有",
                    "仅统计首次被发现时引用边为静态字段的对象（直接持有，不含其递归子对象），直接指出“哪个全局字段在吃内存”。");
                OverviewValueRow("涉及静态字段数", $"{_staticFieldStats.Count:N0}");
                OverviewValueRow("直接持有对象数", $"{_staticFieldRootCount:N0}");
                OverviewValueRow("直接持有总大小", FormatBytes(_staticFieldTotalBytes));
                GUILayout.Space(4f);
                GUILayout.Label(
                    "静态字段常驻于整个 App 生命周期，是泄漏最典型的藏身处：未清空的静态 List/Dictionary、"
                    + "未反注册的静态事件、持有大量对象的单例。字段名带 Manager / Cache / Pool / List 时优先排查。",
                    VumsEditorStyles.SectionDescription);
            }

            if (_staticFieldStats.Count == 0)
                return;

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            DrawStaticFieldByClassTable();
        }

        private void DrawStaticFieldByClassTable()
        {
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    $"按声明类汇总（{_staticFieldClassStats.Count:N0} 个类）",
                    "按该类所有静态字段直接持有总大小降序；点击类可展开查看具体字段。");
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("声明类型", EditorStyles.miniBoldLabel, GUILayout.MinWidth(220f));
                    GUILayout.Label("字段数", EditorStyles.miniBoldLabel, GUILayout.Width(GcHandleCountColumnWidth));
                    GUILayout.Label("对象数", EditorStyles.miniBoldLabel, GUILayout.Width(GcHandleCountColumnWidth));
                    GUILayout.Label("直接持有大小", EditorStyles.miniBoldLabel, GUILayout.Width(GcHandleBytesColumnWidth));
                }

                foreach (var classStat in _staticFieldClassStats)
                {
                    var expanded = _expandedStaticFieldClassKeys.Contains(classStat.DeclaringType);
                    bool newExpanded;
                    var rowText =
                        $"{classStat.DeclaringType} | {classStat.FieldCount:N0} 个字段 | " +
                        $"{classStat.ObjectCount:N0} 个 | {FormatBytes(classStat.TotalBytes)}";

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        newExpanded = EditorGUILayout.Foldout(expanded, classStat.DeclaringType, true);
                        GUILayout.Label($"{classStat.FieldCount:N0}", GUILayout.Width(GcHandleCountColumnWidth));
                        GUILayout.Label($"{classStat.ObjectCount:N0}", GUILayout.Width(GcHandleCountColumnWidth));
                        GUILayout.Label(FormatBytes(classStat.TotalBytes), GUILayout.Width(GcHandleBytesColumnWidth));
                    }

                    if (newExpanded != expanded)
                    {
                        if (newExpanded)
                            _expandedStaticFieldClassKeys.Add(classStat.DeclaringType);
                        else
                            _expandedStaticFieldClassKeys.Remove(classStat.DeclaringType);
                    }

                    if (!newExpanded)
                        continue;

                    foreach (var stat in classStat.Fields)
                    {
                        var fieldText =
                            $"{stat.FieldName} | {stat.ObjectCount:N0} 个 | {FormatBytes(stat.TotalBytes)}";
                        VumsEditorStyles.CopyableRow(
                            this,
                            EditorGUIUtility.singleLineHeight,
                            fieldText,
                            () =>
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    GUILayout.Space(18f);
                                    GUILayout.Label(stat.FieldName, GUILayout.MinWidth(160f));
                                    GUILayout.Label($"{stat.ObjectCount:N0}", GUILayout.Width(GcHandleCountColumnWidth));
                                    GUILayout.Label(FormatBytes(stat.TotalBytes), GUILayout.Width(GcHandleBytesColumnWidth));
                                }
                            });
                    }
                }
            }
        }

        private const string UnknownAssemblyLabel = "（未标注程序集）";
        private const string ManagedStringAssemblyLabel = "System.String（托管字符串）";

        private static T[] ReadOptionalArray<T>(Func<T[]> reader, string chapterName)
        {
            try
            {
                return reader() ?? Array.Empty<T>();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[VUMS] 章节 {chapterName} 不可用，相关统计将为空: {exception.Message}");
                return Array.Empty<T>();
            }
        }

        private static AssemblyCategory ClassifyAssembly(string assembly)
        {
            if (string.IsNullOrEmpty(assembly) || string.Equals(assembly, UnknownAssemblyLabel, StringComparison.Ordinal))
                return AssemblyCategory.Unknown;

            // XLua 运行时随项目以第三方库形式引入，归入第三方库分类。
            if (assembly.IndexOf("XLua", StringComparison.OrdinalIgnoreCase) >= 0)
                return AssemblyCategory.ThirdParty;
            if (assembly.StartsWith("Assembly-CSharp", StringComparison.Ordinal))
                return AssemblyCategory.GameCSharp;
            if (assembly.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                assembly.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                assembly.StartsWith("Unity.", StringComparison.Ordinal))
                return AssemblyCategory.Unity;
            if (assembly.StartsWith("System", StringComparison.Ordinal) ||
                assembly.StartsWith("Microsoft", StringComparison.Ordinal) ||
                assembly.StartsWith("Mono.", StringComparison.Ordinal) ||
                string.Equals(assembly, "mscorlib", StringComparison.Ordinal) ||
                string.Equals(assembly, "netstandard", StringComparison.Ordinal))
                return AssemblyCategory.Runtime;

            return AssemblyCategory.ThirdParty;
        }

        private static string GetCategoryLabel(AssemblyCategory category)
        {
            switch (category)
            {
                case AssemblyCategory.GameCSharp:
                    return "游戏 C#";
                case AssemblyCategory.Unity:
                    return "Unity 引擎";
                case AssemblyCategory.Runtime:
                    return "运行时 BCL";
                case AssemblyCategory.ThirdParty:
                    return "第三方库";
                default:
                    return "未标注";
            }
        }

        private const float AssemblyCountColumnWidth = 78f;
        private const float AssemblyTypeColumnWidth = 66f;
        private const float AssemblyBytesColumnWidth = 92f;
        private const float AssemblyRatioColumnWidth = 54f;

        private void DrawAssemblyMemoryTab()
        {
            if (!_hasAssemblyResult)
            {
                VumsEditorStyles.DrawEmptyState(
                    "暂无程序集内存统计",
                    _analyzing ? "正在按程序集统计托管内存..." : "请选择一个 .snap 快照文件开始分析。");
                return;
            }

            // 分类汇总：一眼看全貌（各分类合计 + 占比），这里把全部分类都列出
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    "分类汇总",
                    "统计对象自身大小（不含其递归引用的子对象），因此各分类之和通常小于 GC 堆已用。");

                foreach (var category in Enum.GetValues(typeof(AssemblyCategory)).Cast<AssemblyCategory>())
                {
                    if (category == AssemblyCategory.Runtime)
                        continue;
                    var stats = _assemblyStats.Where(item => item.Category == category).ToArray();
                    if (stats.Length == 0)
                        continue;

                    var bytes = stats.Sum(item => item.TotalBytes);
                    var objects = stats.Sum(item => item.ObjectCount);
                    var ratio = _assemblyTotalBytes > 0 ? bytes * 100.0 / _assemblyTotalBytes : 0.0;
                    OverviewValueRow(
                        GetCategoryLabel(category),
                        $"{FormatBytes(bytes)}  ({ratio:F1}%) | {objects:N0} 个对象 | {stats.Length:N0} 个程序集");
                }

                VumsEditorStyles.DrawDivider();
                OverviewValueRow("合计", $"{FormatBytes(_assemblyTotalBytes)} | {_assemblyObjectCount:N0} 个对象");
            }

            // 明细：按分类依次铺开，跳过无数据的分类
            var displayedOrder = new[]
            {
                AssemblyCategory.GameCSharp,
                AssemblyCategory.Unity,
                AssemblyCategory.ThirdParty,
                AssemblyCategory.Unknown,
            };
            foreach (var category in displayedOrder)
            {
                var rows = _assemblyStats
                    .Where(item => item.Category == category)
                    .OrderByDescending(item => item.TotalBytes)
                    .ToArray();
                if (rows.Length == 0)
                    continue;

                GUILayout.Space(VumsEditorStyles.SectionSpacing);
                using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
                {
                    var bytes = rows.Sum(item => item.TotalBytes);
                    var ratio = _assemblyTotalBytes > 0 ? bytes * 100.0 / _assemblyTotalBytes : 0.0;
                    VumsEditorStyles.DrawSectionHeader(
                        $"{GetCategoryLabel(category)}（{rows.Length:N0} 个程序集）",
                        $"该分类合计 {FormatBytes(bytes)}（{ratio:F1}%）；按持有总大小降序。");
                    DrawAssemblyTableHeader();

                    foreach (var stat in rows)
                    {
                        var rowRatio = _assemblyTotalBytes > 0
                            ? stat.TotalBytes * 100.0 / _assemblyTotalBytes
                            : 0.0;
                        var rowText =
                            $"{stat.Assembly} | {stat.ObjectCount:N0} 个 | " +
                            $"{stat.TypeCount:N0} 类型 | {FormatBytes(stat.TotalBytes)} | {rowRatio:F1}% | " +
                            $"最大 {FormatBytes(stat.MaxObjectBytes)}";
                        VumsEditorStyles.CopyableRow(
                            this,
                            EditorGUIUtility.singleLineHeight,
                            rowText,
                            () => DrawAssemblyTableRow(stat, rowRatio));
                    }
                }
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
        }

        private static void DrawAssemblyTableHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("程序集", EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(true));
                GUILayout.Label("对象数", EditorStyles.miniBoldLabel, GUILayout.Width(AssemblyCountColumnWidth));
                GUILayout.Label("类型数", EditorStyles.miniBoldLabel, GUILayout.Width(AssemblyTypeColumnWidth));
                GUILayout.Label("总大小", EditorStyles.miniBoldLabel, GUILayout.Width(AssemblyBytesColumnWidth));
                GUILayout.Label("占比", EditorStyles.miniBoldLabel, GUILayout.Width(AssemblyRatioColumnWidth));
                GUILayout.Label("最大单体", EditorStyles.miniBoldLabel, GUILayout.Width(AssemblyBytesColumnWidth));
            }
        }

        private static void DrawAssemblyTableRow(AssemblyMemoryStat stat, double ratio)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(stat.Assembly, GUILayout.ExpandWidth(true));
                GUILayout.Label($"{stat.ObjectCount:N0}", GUILayout.Width(AssemblyCountColumnWidth));
                GUILayout.Label($"{stat.TypeCount:N0}", GUILayout.Width(AssemblyTypeColumnWidth));
                GUILayout.Label(FormatBytes(stat.TotalBytes), GUILayout.Width(AssemblyBytesColumnWidth));
                GUILayout.Label($"{ratio:F1}%", GUILayout.Width(AssemblyRatioColumnWidth));
                GUILayout.Label(FormatBytes(stat.MaxObjectBytes), GUILayout.Width(AssemblyBytesColumnWidth));
            }
        }

        private const float GcHandleCountColumnWidth = 86f;
        private const float GcHandleBytesColumnWidth = 100f;

        private void BeginAnalysis()
        {
            if (_analyzing || string.IsNullOrEmpty(_snapPath))
                return;

            _analyzing = true;
            _selectedResultTab = 0;
            _scroll = Vector2.zero;
            _leakPage = 0;
            _hasManagedLeakResult = false;
            _managedObjectCount = 0;
            _leakedObjects.Clear();
            _leakGroups.Clear();
            _leakDisplayMode = 0;
            _expandedLeakGroupKeys.Clear();
            _showLeakGroupObjectKeys.Clear();
            _managedLeakResultText = "正在分析托管内存泄漏...";
            _duplicateStringStats = Array.Empty<DuplicateStringStat>();
            _hasDuplicateStringResult = false;
            _duplicateAvoidableBytes = 0;
            _top20LargeObjectTotalBytes = 0;
            _nativeStats = default;
            _hasNativeStats = false;
            _captureTimeText = "-";
            _platformText = "-";
            _nativeTypeStats.Clear();
            _duplicateNativeResources.Clear();
            _largeNativeObjects.Clear();
            _expandedNativeTypeKeys.Clear();
            _expandedNativeTypeLargeObjectKeys.Clear();
            _expandedLargeObjectTypeKeys.Clear();
            _duplicateSectionExpanded = false;
            _largeObjectSectionExpanded = false;
            _residentNativeGroups.Clear();
            _residentObjectCount = 0;
            _residentTotalBytes = 0;
            _expandedResidentTypeKeys.Clear();
            _assemblyStats.Clear();
            _assemblyTotalBytes = 0;
            _assemblyObjectCount = 0;
            _managedStringBytes = 0;
            _managedStringCount = 0;
            _hasAssemblyResult = false;
            _staticFieldStats.Clear();
            _staticFieldClassStats.Clear();
            _expandedStaticFieldClassKeys.Clear();
            _staticFieldTotalBytes = 0;
            _staticFieldRootCount = 0;
            _hasStaticFieldResult = false;
            _leakReasonCounts = new int[4];
            EditorApplication.delayCall -= RunAnalysisOnce;
            EditorApplication.delayCall += RunAnalysisOnce;
            Repaint();
        }

        private void RunAnalysisOnce()
        {
            EditorApplication.delayCall -= RunAnalysisOnce;
            Analyze();
            // 进度条关闭当帧的 Repaint 常被模态进度条抑制，延后一帧再强制重绘，确保结果真正刷新
            EditorApplication.delayCall += DeferredRepaint;
        }

        private void DeferredRepaint()
        {
            EditorApplication.delayCall -= DeferredRepaint;
            Repaint();
        }

        private sealed class LeakedObjectInfo
        {
            public string TypeName;
            public ulong Address;
            public string[] RetentionPathNodes;
            public LoadedReason Reason;
            public bool Expanded;
            public bool[] RetentionNodeExpanded = Array.Empty<bool>();
        }

        private sealed class RetentionPathGroup
        {
            public string Key;
            public LeakedObjectInfo Representative;
            public List<LeakedObjectInfo> Objects;
        }

        private sealed class RetainedObjectInfo
        {
            public string TypeName;
            public ulong Address;
            public long SizeBytes;
            public string[] RetentionPathNodes;
        }

        private class NativeObjectInfo
        {
            public string TypeName;
            public string Name;
            public long NativeSize;
            public int InstanceId;
            public ulong NativeAddress;
            public long RootReferenceId;
            public uint HideFlags;
            public ObjectFlags Flags;

            public bool IsDontDestroyOnLoad => (Flags & ObjectFlags.IsDontDestroyOnLoad) != 0;
            public bool IsPersistentAsset => (Flags & ObjectFlags.IsPersistent) != 0;
            public bool IsUnityManager => (Flags & ObjectFlags.IsManager) != 0;

            /// <summary>HideFlags.HideAndDontSave 的完整位组合。</summary>
            public bool IsHideAndDontSave => (HideFlags & HideAndDontSaveMask) == HideAndDontSaveMask;

            /// <summary>按设计就不随场景卸载 / 不进入保存流程，出现即属正常，不该当泄漏处理。</summary>
            public bool IsResident => IsDontDestroyOnLoad || IsHideAndDontSave || IsUnityManager;

            public string ResidentReason
            {
                get
                {
                    var reasons = new List<string>(3);
                    if (IsDontDestroyOnLoad)
                        reasons.Add("DontDestroyOnLoad");
                    if (IsHideAndDontSave)
                        reasons.Add("HideAndDontSave");
                    if (IsUnityManager)
                        reasons.Add("Unity 内部管理器");
                    return reasons.Count == 0 ? string.Empty : string.Join(" / ", reasons);
                }
            }
        }

        /// <summary>
        /// UnityEngine.HideFlags 的位组合：DontSave(52) | NotEditable(8) | HideInHierarchy(1) | HideInInspector(2)。
        /// </summary>
        private const uint HideAndDontSaveMask = 52 | 8 | 1 | 2;

        private sealed class ResidentNativeGroup
        {
            public string TypeName;
            public List<NativeObjectInfo> Objects = new List<NativeObjectInfo>();

            public long TotalSize
            {
                get
                {
                    var sum = 0L;
                    foreach (var item in Objects)
                        sum += item.NativeSize;
                    return sum;
                }
            }

            public int DontDestroyOnLoadCount;
            public int HideAndDontSaveCount;
            public int UnityManagerCount;
        }

        private enum AssemblyCategory
        {
            GameCSharp,
            Unity,
            Runtime,
            ThirdParty,
            Unknown,
        }

        private sealed class AssemblyMemoryStat
        {
            public string Assembly;
            public AssemblyCategory Category;
            public int ObjectCount;
            public int TypeCount;
            public long TotalBytes;
            public long MaxObjectBytes;
        }

        private sealed class StaticFieldHoldStat
        {
            public string DeclaringType;
            public string FieldName;
            public string Key;
            public int ObjectCount;
            public long TotalBytes;
        }

        // 按声明类聚合（一个类的全部静态字段合计），用于「按声明类」视图
        private sealed class StaticFieldClassStat
        {
            public string DeclaringType;
            public int FieldCount;
            public int ObjectCount;
            public long TotalBytes;
            public readonly List<StaticFieldHoldStat> Fields = new List<StaticFieldHoldStat>();
        }

        private sealed class NativeTypeStat
        {
            public string TypeName;
            public int Count;
            public long TotalSize;
            public long MaxSize;
        }

        private sealed class DuplicateNativeResource
        {
            public string TypeName;
            public string Name;
            public int Count;
            public long SingleSize;
            public long TotalSize;
            public long PotentialDuplicateSize;
        }

        private sealed class DuplicateStringStat
        {
            public string Value;
            public int Count;
            public long TotalBytes;
            public long SingleInstanceBytes;
            public long DuplicateBytes => Math.Max(0, TotalBytes - SingleInstanceBytes);
            // 推断的来源写法，用于“来源建议”
            public DuplicateStringSource Source;
        }

        private static string FormatBytes(long bytes)
        {
            const double kb = 1024.0;
            const double mb = kb * 1024.0;
            const double gb = mb * 1024.0;

            if (bytes >= gb)
                return $"{bytes / gb:F2} GB";
            if (bytes >= mb)
                return $"{bytes / mb:F2} MB";
            if (bytes >= kb)
                return $"{bytes / kb:F2} KB";
            return $"{bytes} B";
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

        private void CollectNativeObjectRecommendations(SnapshotFile file)
        {
            var typeNames = file.NativeTypeNames;
            var names = file.NativeObjectNames;
            var typeIndices = file.ReadValueTypeChapter<int>(EntryType.NativeObjects_NativeTypeArrayIndex, 0, -1).ToArray();
            var instanceIds = file.ReadValueTypeChapter<int>(EntryType.NativeObjects_InstanceId, 0, -1).ToArray();
            var addresses = file.ReadValueTypeChapter<ulong>(EntryType.NativeObjects_NativeObjectAddress, 0, -1).ToArray();
            var sizes = file.ReadValueTypeChapter<ulong>(EntryType.NativeObjects_Size, 0, -1).ToArray();
            var rootIds = file.ReadValueTypeChapter<long>(EntryType.NativeObjects_RootReferenceId, 0, -1).ToArray();
            var hideFlags = ReadOptionalChapter<uint>(file, EntryType.NativeObjects_HideFlags);
            var objectFlags = ReadOptionalChapter<ObjectFlags>(file, EntryType.NativeObjects_Flags);
            var count = new[] { names.Length, typeIndices.Length, instanceIds.Length, addresses.Length, sizes.Length, rootIds.Length }.Min();
            var objects = new List<NativeObjectInfo>(count);

            for (var index = 0; index < count; index++)
            {
                var typeIndex = typeIndices[index];
                var typeName = typeIndex >= 0 && typeIndex < typeNames.Length ? typeNames[typeIndex] : $"NativeType[{typeIndex}]";
                objects.Add(new NativeObjectInfo
                {
                    TypeName = typeName,
                    Name = string.IsNullOrEmpty(names[index]) ? "(未命名)" : names[index],
                    NativeSize = sizes[index] > long.MaxValue ? long.MaxValue : (long)sizes[index],
                    InstanceId = instanceIds[index],
                    NativeAddress = addresses[index],
                    RootReferenceId = rootIds[index],
                    HideFlags = index < hideFlags.Length ? hideFlags[index] : 0u,
                    Flags = index < objectFlags.Length ? objectFlags[index] : ObjectFlags.None,
                });
            }

            _nativeTypeStats.Clear();
            _nativeTypeStats.AddRange(objects.GroupBy(item => item.TypeName, StringComparer.Ordinal)
                .Select(group => new NativeTypeStat
                {
                    TypeName = group.Key,
                    Count = group.Count(),
                    TotalSize = group.Sum(item => item.NativeSize),
                    MaxSize = group.Max(item => item.NativeSize),
                })
                .OrderByDescending(item => item.TotalSize));

            _duplicateNativeResources.Clear();
            _duplicateNativeResources.AddRange(objects
                .Where(item => !item.IsResident)
                .Where(item => item.Name != "(未命名)" && item.NativeSize > 0)
                .GroupBy(item => $"{item.TypeName}\u001F{item.Name}\u001F{item.NativeSize}", StringComparer.Ordinal)
                .Where(group => group.Count() >= 2)
                .Select(group => new DuplicateNativeResource
                {
                    TypeName = group.First().TypeName,
                    Name = group.First().Name,
                    Count = group.Count(),
                    SingleSize = group.First().NativeSize,
                    TotalSize = group.Sum(item => item.NativeSize),
                    PotentialDuplicateSize = group.Skip(1).Sum(item => item.NativeSize),
                })
                .OrderByDescending(item => item.PotentialDuplicateSize));

            _largeNativeObjects.Clear();
            _largeNativeObjects.AddRange(objects
                .Where(item => !item.IsResident)
                .Where(item => item.NativeSize >= LargeNativeObjectThreshold)
                .OrderByDescending(item => item.NativeSize));

            BuildResidentNativeGroups(objects);
        }

        /// <summary>读取可能不存在的章节，缺失时返回空数组，避免老格式快照直接抛异常。</summary>
        private static T[] ReadOptionalChapter<T>(SnapshotFile file, EntryType entryType) where T : unmanaged
        {
            try
            {
                if (file.GetChapterArrayLength(entryType) <= 0)
                    return Array.Empty<T>();
                return file.ReadValueTypeChapter<T>(entryType, 0, -1).ToArray();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[VUMS] 章节 {entryType} 不可用，相关标记将按默认值处理: {exception.Message}");
                return Array.Empty<T>();
            }
        }

        /// <summary>
        /// 把 DontDestroyOnLoad / HideAndDontSave / Unity 内部管理器这类“按设计不销毁”的
        /// Native 对象单独成组。它们混在优化建议里会误导判断——不销毁不是泄漏。
        /// </summary>
        private void BuildResidentNativeGroups(List<NativeObjectInfo> objects)
        {
            _residentNativeGroups.Clear();
            _residentObjectCount = 0;
            _residentTotalBytes = 0;

            foreach (var group in objects
                         .Where(item => item.IsResident)
                         .GroupBy(item => item.TypeName, StringComparer.Ordinal))
            {
                var residentGroup = new ResidentNativeGroup { TypeName = group.Key };
                foreach (var item in group)
                {
                    residentGroup.Objects.Add(item);
                    _residentObjectCount++;
                    _residentTotalBytes += item.NativeSize;
                    if (item.IsDontDestroyOnLoad)
                        residentGroup.DontDestroyOnLoadCount++;
                    if (item.IsHideAndDontSave)
                        residentGroup.HideAndDontSaveCount++;
                    if (item.IsUnityManager)
                        residentGroup.UnityManagerCount++;
                }

                residentGroup.Objects.Sort((x, y) => y.NativeSize.CompareTo(x.NativeSize));
                _residentNativeGroups.Add(residentGroup);
            }

            _residentNativeGroups.Sort((x, y) => y.TotalSize.CompareTo(x.TotalSize));
        }

        private void Analyze()
        {
            if (string.IsNullOrEmpty(_snapPath) || !File.Exists(_snapPath))
            {
                const string error = "错误：文件路径为空或文件不存在。";
                _managedLeakResultText = error;
                _hasDuplicateStringResult = false;
                _analyzing = false;
                Repaint();
                return;
            }

            _analyzing = true;
            Repaint();

            try
            {
                var totalStart = DateTime.Now;

                EditorUtility.DisplayProgressBar("分析中", "正在读取快照文件...", 0.1f);
                using var file = new SnapshotFile(_snapPath);

                _captureTimeText = file.CaptureDateTime.ToString();
                _platformText = file.ProfileTargetInfo.ToString();

                try
                {
                    _nativeStats = file.ProfileTargetMemoryStats;
                    _hasNativeStats = true;
                }
                catch (Exception nativeException)
                {
                    _nativeStats = default;
                    _hasNativeStats = false;
                    Debug.LogWarning($"[VUMS] 读取原生内存信息失败: {nativeException.Message}");
                }

                EditorUtility.DisplayProgressBar("分析中", "正在分析 Unity Objects 与资源热点...", 0.2f);
                CollectNativeObjectRecommendations(file);

                // UMS 保存对象第一次被发现时的引用路径。先扫描静态字段，才能保留静态单例、
                // 全局管理器、静态集合和静态事件等来源；随后再补充仅由 GC Root 发现的对象。
                EditorUtility.DisplayProgressBar("分析中", "正在加载托管对象 (静态字段)...", 0.35f);
                file.LoadManagedObjectsFromStaticFields();
                EditorUtility.DisplayProgressBar("分析中", "正在加载托管对象 (GC Roots)...", 0.55f);
                file.LoadManagedObjectsFromGcRoots();

                var allObjects = file.AllManagedClassInstances.ToArray();
                _managedObjectCount = allObjects.Length;

                EditorUtility.DisplayProgressBar("分析中", "正在分析静态字段持有和大对象引用路径...", 0.68f);
                var retainedObjects = new List<RetainedObjectInfo>();

                foreach (var obj in allObjects)
                {
                    if (obj.ObjectAddress == 0)
                        continue;

                    var rawInfo = file.ParseManagedObjectInfo(obj.ObjectAddress);
                    if (!rawInfo.IsKnownType || rawInfo.Size <= 0)
                        continue;

                    var retainedInfo = new RetainedObjectInfo
                    {
                        TypeName = file.GetTypeName(obj.TypeInfo.TypeIndex),
                        Address = obj.ObjectAddress,
                        SizeBytes = rawInfo.Size,
                        RetentionPathNodes = GetSafeRetentionPathNodes(file, obj),
                    };
                    retainedObjects.Add(retainedInfo);
                }

                var selectedLargeObjects = retainedObjects
                    .OrderByDescending(item => item.SizeBytes)
                    .Take(TopLargeObjectCount)
                    .ToArray();
                _largeObjects.Clear();
                _largeObjects.AddRange(selectedLargeObjects);
                _top20LargeObjectTotalBytes = selectedLargeObjects.Sum(item => item.SizeBytes);
                _largeObjectOptions = selectedLargeObjects
                    .Select(item => $"{FormatBytes(item.SizeBytes)} | {item.TypeName} @ 0x{item.Address:X}")
                    .ToArray();
                _selectedLargeObjectIndex = 0;
                ResetRetentionTreeExpansion();

                EditorUtility.DisplayProgressBar("分析中", "正在按程序集统计托管内存...", 0.72f);
                CollectAssemblyStats(file);

                EditorUtility.DisplayProgressBar("分析中", "正在统计静态字段持有...", 0.74f);
                CollectStaticFieldStats(file);

                EditorUtility.DisplayProgressBar("分析中", "正在检测重复字符串...", 0.76f);
                var duplicateStrings = new Dictionary<string, DuplicateStringStat>(StringComparer.Ordinal);
                foreach (var managedString in file.AllManagedStrings)
                {
                    if (!duplicateStrings.TryGetValue(managedString.Value, out var stringStat))
                    {
                        stringStat = new DuplicateStringStat
                        {
                            Value = managedString.Value,
                            SingleInstanceBytes = managedString.SizeBytes,
                            Source = VumsStringSourceHelper.Classify(managedString.Value),
                        };
                        duplicateStrings.Add(managedString.Value, stringStat);
                    }

                    stringStat.Count++;
                    stringStat.TotalBytes += managedString.SizeBytes;
                }

                var repeatedStrings = duplicateStrings.Values
                    .Where(item => item.Count > 1)
                    .OrderByDescending(item => item.DuplicateBytes)
                    .Take(TopDuplicateStringCount)
                    .ToArray();
                _duplicateStringStats = repeatedStrings;
                _duplicateAvoidableBytes = repeatedStrings.Sum(item => item.DuplicateBytes);
                _hasDuplicateStringResult = repeatedStrings.Length > 0;

                EditorUtility.DisplayProgressBar("分析中", "正在检测泄漏的 Managed Shell...", 0.85f);
                var unityObjects = allObjects.Where(i => i.InheritsFromUnityEngineObject(file)).ToArray();

                _leakedObjects.Clear();

                foreach (var obj in unityObjects)
                {
                    if (!obj.IsLeakedManagedShell(file))
                        continue;

                    var typeName = file.GetTypeName(obj.TypeInfo.TypeIndex);
                    var leakedObject = new LeakedObjectInfo
                    {
                        TypeName = typeName,
                        Address = obj.ObjectAddress,
                        RetentionPathNodes = GetSafeRetentionPathNodes(file, obj),
                        Reason = obj.LoadedReason,
                    };
                    _leakedObjects.Add(leakedObject);
                }

                _leakGroups = BuildRetentionPathGroups(_leakedObjects);
                _leakPage = 0;
                _leakReasonCounts = new int[Enum.GetValues(typeof(LoadedReason)).Length];
                foreach (var leaked in _leakedObjects)
                    _leakReasonCounts[(int)leaked.Reason]++;
                _hasManagedLeakResult = true;
                _managedLeakResultText = "";
            }
            catch (Exception e)
            {
                var error = $"分析过程中发生错误:\n{e.GetType().Name}: {e.Message}\n\n{e.StackTrace}";
                _managedLeakResultText = error;
                _hasDuplicateStringResult = false;
                Debug.LogError($"[VUMS] ManagedMemoryLeakAnalysis 分析失败: {e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _analyzing = false;
                Repaint();
            }
        }
    }
}
