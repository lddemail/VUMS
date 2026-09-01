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
    public class ManagedMemoryLeakAnalysisWindow : EditorWindow
    {
        private string _snapPath = "";
        private string _captureTimeText = "-";
        private string _platformText = "-";
        private const int LeakPageSize = 20;
        private const long LargeNativeObjectThreshold = 2L * 1024 * 1024;

        // 概览指标（与 SnapshotDiff 概览保持一致，便于横向比对）
        private long _duplicateAvoidableBytes;
        private long _top50LargeObjectTotalBytes;
        private ProfileTargetMemoryStats _nativeStats;
        private bool _hasNativeStats;

        private string _managedLeakResultText = "请选择一个 .snap 快照文件。";
        private string _duplicateStringResultText = "请选择一个 .snap 快照文件。";
        private readonly string[] _resultTabs = { "概览", "托管内存泄漏", "重复字符串", "大对象引用路径" };
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
        private readonly HashSet<string> _expandedNativeTypeDuplicateKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _expandedNativeTypeLargeObjectKeys = new HashSet<string>(StringComparer.Ordinal);

        [MenuItem("VUMS/ManagedMemoryLeakAnalysis", false, 2)]
        public static void OpenWindow()
        {
            var window = GetWindow<ManagedMemoryLeakAnalysisWindow>(true, "Managed Memory Leak Analysis", true);
            window.minSize = new Vector2(560, 520);
            window.Show();
        }

        [MenuItem("VUMS/ExportPackage", false, 1)]
        public static void ExportPackage()
        {
            // 只导出工具本身：UMS 库 DLL 与 Editor 窗口脚本，不带入任何依赖
            var folders = new[]
            {
                "Assets/ThirdParty/VUMS",
                "Assets/Editor/VUMS",
            };

            // Build 目录放在与 Assets 同级（Unity 不会导入该目录）
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var buildDir = Path.Combine(projectRoot, "Build");
            if (!Directory.Exists(buildDir))
                Directory.CreateDirectory(buildDir);

            var exportPath = Path.Combine(buildDir, "VUMS.unitypackage");

            Debug.Log($"[VUMS] 开始导出 package 到: {exportPath}");
            AssetDatabase.ExportPackage(folders, exportPath, ExportPackageOptions.Recurse);
            Debug.Log($"[VUMS] 已导出 package 到: {exportPath}");

            EditorUtility.DisplayDialog("导出完成", $"已导出 Unity Package 到:\n{exportPath}", "确定");
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
                        using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
                        {
                            VumsEditorStyles.DrawSectionHeader(
                                "重复字符串 Top 20",
                                "按可避免内存从高到低排列，文本内容仅显示前 100 个字符。");
                            EditorGUILayout.TextArea(
                                _duplicateStringResultText,
                                VumsEditorStyles.CodeTextArea,
                                GUILayout.ExpandHeight(true));
                        }
                        break;
                    case 3:
                        DrawLargeObjectRetentionTab();
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
                OverviewValueRow("重复字符串可避免内存（Top 20）", FormatBytes(_duplicateAvoidableBytes));
                OverviewValueRow("Top 50 大对象总大小", FormatBytes(_top50LargeObjectTotalBytes));
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
                VumsEditorStyles.DrawSectionHeader(
                    "Unity Objects 优化建议",
                    "仅显示存在疑似重复资源或重点单体资源。结果用于提供排查线索，不等同于实际可释放内存");

                var recommendationTypeCount = _nativeTypeStats.Count(stat =>
                    _duplicateNativeResources.Any(item => string.Equals(item.TypeName, stat.TypeName, StringComparison.Ordinal)) ||
                    _largeNativeObjects.Any(item => string.Equals(item.TypeName, stat.TypeName, StringComparison.Ordinal)));
                EditorGUILayout.LabelField(
                    $"优化建议（{recommendationTypeCount:N0} 个类型）",
                    EditorStyles.miniBoldLabel);

                if (recommendationTypeCount == 0)
                {
                    EditorGUILayout.LabelField("未发现疑似重复资源或重点单体资源。", EditorStyles.miniLabel);
                    return;
                }

                foreach (var stat in _nativeTypeStats)
                {
                    var duplicates = _duplicateNativeResources
                        .Where(item => string.Equals(item.TypeName, stat.TypeName, StringComparison.Ordinal))
                        .ToArray();
                    var largeObjects = _largeNativeObjects
                        .Where(item => string.Equals(item.TypeName, stat.TypeName, StringComparison.Ordinal))
                        .ToArray();
                    if (duplicates.Length == 0 && largeObjects.Length == 0)
                        continue;

                    using (new EditorGUILayout.VerticalScope(VumsEditorStyles.CompactCard))
                    {
                        var expanded = _expandedNativeTypeKeys.Contains(stat.TypeName);
                        expanded = EditorGUILayout.Foldout(
                            expanded,
                            $"{stat.TypeName} | 疑似重复 {duplicates.Length:N0} 组 | 重点单体 {largeObjects.Length:N0} 个",
                            true,
                            VumsEditorStyles.Foldout);
                        SetGroupState(_expandedNativeTypeKeys, stat.TypeName, expanded);
                        if (!expanded)
                            continue;

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(18f);
                            using (new EditorGUILayout.VerticalScope())
                            {
                                var duplicateKey = stat.TypeName + "\u001FDuplicates";
                                var duplicateExpanded = _expandedNativeTypeDuplicateKeys.Contains(duplicateKey);
                                duplicateExpanded = EditorGUILayout.Foldout(
                                    duplicateExpanded,
                                    $"疑似重复资源（{duplicates.Length:N0} 组）",
                                    true);
                                SetGroupState(_expandedNativeTypeDuplicateKeys, duplicateKey, duplicateExpanded);
                                if (duplicateExpanded)
                                {
                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        GUILayout.Space(18f);
                                        using (new EditorGUILayout.VerticalScope())
                                        {
                                            if (duplicates.Length == 0)
                                            {
                                                EditorGUILayout.LabelField("未发现名称和单体大小都相同的重复资源候选。", EditorStyles.miniLabel);
                                            }
                                            else
                                            {
                                                foreach (var duplicate in duplicates)
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

                                if (largeObjects.Length > 0)
                                {
                                    var largeObjectKey = stat.TypeName + "\u001FLargeObjects";
                                    var largeObjectExpanded = _expandedNativeTypeLargeObjectKeys.Contains(largeObjectKey);
                                    largeObjectExpanded = EditorGUILayout.Foldout(
                                        largeObjectExpanded,
                                        $"重点单体资源（≥ {FormatBytes(LargeNativeObjectThreshold)}，{largeObjects.Length:N0} 个）",
                                        true);
                                    SetGroupState(_expandedNativeTypeLargeObjectKeys, largeObjectKey, largeObjectExpanded);
                                    if (largeObjectExpanded)
                                    {
                                        using (new EditorGUILayout.HorizontalScope())
                                        {
                                            GUILayout.Space(18f);
                                            using (new EditorGUILayout.VerticalScope())
                                            {
                                                foreach (var item in largeObjects)
                                                {
                                                    var largeText =
                                                        $"{FormatBytes(item.NativeSize),10} | {item.Name} | Instance ID {item.InstanceId}";
                                                    VumsEditorStyles.CopyableRow(
                                                        this,
                                                        EditorGUIUtility.singleLineHeight,
                                                        largeText,
                                                        () => EditorGUILayout.SelectableLabel(
                                                            largeText,
                                                            GUILayout.Height(EditorGUIUtility.singleLineHeight)));
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
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

            if (_leakedObjects.Count == 0)
            {
                GUILayout.Space(VumsEditorStyles.SectionSpacing);
                VumsEditorStyles.DrawEmptyState("未发现泄漏 Shell", "当前快照中没有检测到泄漏的 Managed Shell。", MessageType.None);
                return;
            }

            GUILayout.Space(VumsEditorStyles.SectionSpacing);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader("查看方式", "按引用路径聚合可快速识别共同持有源；按对象查看用于核对具体地址。");
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
                var objectHeaderText = $"{leakedObject.TypeName} @ 0x{leakedObject.Address:X}";
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
                        $"{group.Objects.Count:N0} 个（{group.Objects.Count * 100f / objects.Count:F1}%） | {GetRootSummary(group.Representative.RetentionPathNodes)}";
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

        private void DrawLargeObjectRetentionTab()
        {
            if (_largeObjectOptions.Length == 0)
            {
                VumsEditorStyles.DrawEmptyState(
                    "暂无大对象引用路径",
                    _analyzing ? "正在分析大对象引用路径..." : "请选择一个 .snap 快照文件开始分析。");
                return;
            }

            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                VumsEditorStyles.DrawSectionHeader(
                    "Top 50 大对象引用路径",
                    "选择一个托管大对象，沿引用链逐层展开至 GC Root。");
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
            _duplicateStringResultText = "正在分析重复字符串...";
            _duplicateAvoidableBytes = 0;
            _top50LargeObjectTotalBytes = 0;
            _nativeStats = default;
            _hasNativeStats = false;
            _captureTimeText = "-";
            _platformText = "-";
            _nativeTypeStats.Clear();
            _duplicateNativeResources.Clear();
            _largeNativeObjects.Clear();
            _expandedNativeTypeKeys.Clear();
            _expandedNativeTypeDuplicateKeys.Clear();
            _expandedNativeTypeLargeObjectKeys.Clear();
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
                .Where(item => item.NativeSize >= LargeNativeObjectThreshold)
                .OrderByDescending(item => item.NativeSize));
        }

        private void Analyze()
        {
            if (string.IsNullOrEmpty(_snapPath) || !File.Exists(_snapPath))
            {
                const string error = "错误：文件路径为空或文件不存在。";
                _managedLeakResultText = error;
                _duplicateStringResultText = error;
                _analyzing = false;
                Repaint();
                return;
            }

            _analyzing = true;
            Repaint();

            try
            {
                var duplicateStringSb = new StringBuilder();
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
                    .Take(50)
                    .ToArray();
                _largeObjects.Clear();
                _largeObjects.AddRange(selectedLargeObjects);
                _top50LargeObjectTotalBytes = selectedLargeObjects.Sum(item => item.SizeBytes);
                _largeObjectOptions = selectedLargeObjects
                    .Select(item => $"{FormatBytes(item.SizeBytes)} | {item.TypeName} @ 0x{item.Address:X}")
                    .ToArray();
                _selectedLargeObjectIndex = 0;
                ResetRetentionTreeExpansion();

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
                        };
                        duplicateStrings.Add(managedString.Value, stringStat);
                    }

                    stringStat.Count++;
                    stringStat.TotalBytes += managedString.SizeBytes;
                }

                var repeatedStrings = duplicateStrings.Values
                    .Where(item => item.Count > 1)
                    .OrderByDescending(item => item.DuplicateBytes)
                    .Take(20)
                    .ToArray();
                duplicateStringSb.AppendLine("重复字符串检查 Top 20:");
                if (repeatedStrings.Length == 0)
                {
                    duplicateStringSb.AppendLine("  未发现重复字符串实例。");
                }
                else
                {
                    foreach (var stringStat in repeatedStrings)
                    {
                        var preview = stringStat.Value
                            .Replace("\r", "\\r")
                            .Replace("\n", "\\n")
                            .Replace("\t", "\\t");
                        if (preview.Length > 100)
                            preview = preview.Substring(0, 100) + "...";

                        duplicateStringSb.AppendLine(
                            $"  {stringStat.Count,6:N0} x | 总计 {FormatBytes(stringStat.TotalBytes),10} | \"{preview}\"");
                    }
                }
                _duplicateAvoidableBytes = repeatedStrings.Sum(item => item.DuplicateBytes);

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
                    };
                    _leakedObjects.Add(leakedObject);
                }

                _leakGroups = BuildRetentionPathGroups(_leakedObjects);
                _leakPage = 0;
                _hasManagedLeakResult = true;
                _managedLeakResultText = "";
                _duplicateStringResultText = duplicateStringSb.ToString();
            }
            catch (Exception e)
            {
                var error = $"分析过程中发生错误:\n{e.GetType().Name}: {e.Message}\n\n{e.StackTrace}";
                _managedLeakResultText = error;
                _duplicateStringResultText = error;
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
