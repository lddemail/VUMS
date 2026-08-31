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
    /// 双快照对比窗口：选择两份 .snap（基准 A 与对比 B），第二个选完后自动执行对比，
    /// 聚焦“增量”——哪些类型对象变多、哪些泄漏 Managed Shell 新增、重复字符串是否恶化。
    /// 仅做排查线索展示，不直接判定泄漏。
    /// </summary>
    public class SnapshotDiffWindow : EditorWindow
    {
        private string _pathA = "";
        private string _pathB = "";
        private string _captureTimeA = "-";
        private string _captureTimeB = "-";
        private string _platformA = "-";
        private string _platformB = "-";
        private bool _diffing;
        private bool _hasResult;
        private string _statusText = "请选择两个 .snap 快照文件（先选基准 A，再选对比 B）。";
        private string _lastDiffKey = "";
        private Vector2 _scroll;

        // 概览指标
        private int _totalA, _totalB;
        private int _leakA, _leakB;
        private long _dupAvoidA, _dupAvoidB;
        private long _bigTopSumA, _bigTopSumB;
        private ProfileTargetMemoryStats _memA;
        private ProfileTargetMemoryStats _memB;

        // 类型数量增量（按 |Δ| 降序）
        private readonly List<TypeCountDiff> _typeDiffs = new List<TypeCountDiff>();

        // 泄漏 Managed Shell 增量（按类型，B 比 A 多）
        private readonly List<LeakTypeDelta> _leakDeltas = new List<LeakTypeDelta>();

        // A / B 快照重复字符串 Top 20
        private readonly List<DuplicateStringStat> _dupTopA = new List<DuplicateStringStat>();
        private readonly List<DuplicateStringStat> _dupTopB = new List<DuplicateStringStat>();

        private readonly string[] _tabs = { "概览", "类型数量增量", "泄漏 Shell 增量", "重复字符串" };
        private int _selectedTab;

        [MenuItem("VUMS/SnapshotDiff", false, 3)]
        public static void OpenWindow()
        {
            var window = GetWindow<SnapshotDiffWindow>(true, "Snapshot Diff", true);
            window.minSize = new Vector2(600, 560);
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("Snapshot Diff", EditorStyles.boldLabel);

            // --- 选择快照 A ---
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_diffing);
            if (GUILayout.Button("选择快照 A (基准)", GUILayout.Width(180)))
            {
                string startDir = string.IsNullOrEmpty(_pathA) ? "" : Path.GetDirectoryName(_pathA);
                string picked = EditorUtility.OpenFilePanel("选择基准快照 (A)", startDir, "snap");
                if (!string.IsNullOrEmpty(picked))
                {
                    _pathA = picked;
                    TryAutoDiff();
                }
            }
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(_pathA);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            // --- 选择快照 B ---
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_diffing);
            if (GUILayout.Button("选择快照 B (对比)", GUILayout.Width(180)))
            {
                string startDir = string.IsNullOrEmpty(_pathB) ? "" : Path.GetDirectoryName(_pathB);
                string picked = EditorUtility.OpenFilePanel("选择对比快照 (B)", startDir, "snap");
                if (!string.IsNullOrEmpty(picked))
                {
                    _pathB = picked;
                    TryAutoDiff();
                }
            }
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(_pathB);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

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

        private void TryAutoDiff()
        {
            if (_diffing)
                return;
            if (string.IsNullOrEmpty(_pathA) || string.IsNullOrEmpty(_pathB))
                return;
            if (!File.Exists(_pathA) || !File.Exists(_pathB))
                return;

            var key = $"{_pathA}|{_pathB}";
            if (key == _lastDiffKey)
                return;
            _lastDiffKey = key;

            _diffing = true;
            _hasResult = false;
            _statusText = "正在对比两个快照...";
            _selectedTab = 0;
            _scroll = Vector2.zero;
            EditorApplication.delayCall -= RunDiffOnce;
            EditorApplication.delayCall += RunDiffOnce;
            Repaint();
        }

        private void RunDiffOnce()
        {
            EditorApplication.delayCall -= RunDiffOnce;
            AnalyzeDiff();
            EditorApplication.delayCall += DeferredRepaint;
        }

        private void DeferredRepaint()
        {
            EditorApplication.delayCall -= DeferredRepaint;
            Repaint();
        }

        private void AnalyzeDiff()
        {
            if (string.IsNullOrEmpty(_pathA) || !File.Exists(_pathA) ||
                string.IsNullOrEmpty(_pathB) || !File.Exists(_pathB))
            {
                _statusText = "错误：快照路径为空或文件不存在。";
                _diffing = false;
                Repaint();
                return;
            }

            _diffing = true;
            Repaint();

            try
            {
                Dictionary<string, int> typeCountA;
                Dictionary<string, int> typeCountB;
                List<LeakedObjectInfo> leakA;
                List<LeakedObjectInfo> leakB;
                Dictionary<string, DuplicateStringStat> dupA;
                Dictionary<string, DuplicateStringStat> dupB;

                // 先读取并释放 A，降低内存峰值
                EditorUtility.DisplayProgressBar("对比中", "正在读取快照 A (基准)...", 0.2f);
                using (var fileA = new SnapshotFile(_pathA))
                {
                    fileA.LoadManagedObjectsFromStaticFields();
                    fileA.LoadManagedObjectsFromGcRoots();
                    var objsA = fileA.AllManagedClassInstances.ToArray();
                    _totalA = objsA.Length;
                    _captureTimeA = fileA.CaptureDateTime.ToString();
                    _platformA = fileA.ProfileTargetInfo.ToString();
                    typeCountA = CountByType(fileA, objsA);
                    leakA = CollectLeaks(fileA, objsA);
                    dupA = CollectDuplicateStrings(fileA);
                    _dupAvoidA = dupA.Values.Where(s => s.Count > 1).Sum(s => s.DuplicateBytes);
                    _bigTopSumA = SumTopLargeObjects(fileA, objsA);
                    _memA = fileA.ProfileTargetMemoryStats;
                }

                EditorUtility.DisplayProgressBar("对比中", "正在读取快照 B (对比)...", 0.5f);
                using (var fileB = new SnapshotFile(_pathB))
                {
                    fileB.LoadManagedObjectsFromStaticFields();
                    fileB.LoadManagedObjectsFromGcRoots();
                    var objsB = fileB.AllManagedClassInstances.ToArray();
                    _totalB = objsB.Length;
                    _captureTimeB = fileB.CaptureDateTime.ToString();
                    _platformB = fileB.ProfileTargetInfo.ToString();
                    typeCountB = CountByType(fileB, objsB);
                    leakB = CollectLeaks(fileB, objsB);
                    dupB = CollectDuplicateStrings(fileB);
                    _dupAvoidB = dupB.Values.Where(s => s.Count > 1).Sum(s => s.DuplicateBytes);
                    _bigTopSumB = SumTopLargeObjects(fileB, objsB);
                    _memB = fileB.ProfileTargetMemoryStats;
                }

                EditorUtility.DisplayProgressBar("对比中", "正在计算增量...", 0.8f);

                // 类型数量增量
                _typeDiffs.Clear();
                var allTypes = new HashSet<string>(typeCountA.Keys);
                allTypes.UnionWith(typeCountB.Keys);
                foreach (var type in allTypes)
                {
                    int ca = typeCountA.TryGetValue(type, out var va) ? va : 0;
                    int cb = typeCountB.TryGetValue(type, out var vb) ? vb : 0;
                    if (ca != cb)
                        _typeDiffs.Add(new TypeCountDiff { TypeName = type, CountA = ca, CountB = cb });
                }
                _typeDiffs.Sort((x, y) =>
                    Math.Abs(y.CountB - y.CountA).CompareTo(Math.Abs(x.CountB - x.CountA)));

                // 泄漏 Managed Shell 增量（按类型，B 比 A 多）
                _leakA = leakA.Count;
                _leakB = leakB.Count;
                var leakTypeA = leakA.GroupBy(l => l.TypeName).ToDictionary(g => g.Key, g => g.Count());
                var leakTypeB = leakB.GroupBy(l => l.TypeName).ToDictionary(g => g.Key, g => g.Count());
                var allLeakTypes = new HashSet<string>(leakTypeA.Keys);
                allLeakTypes.UnionWith(leakTypeB.Keys);
                _leakDeltas.Clear();
                foreach (var type in allLeakTypes)
                {
                    int ca = leakTypeA.TryGetValue(type, out var va) ? va : 0;
                    int cb = leakTypeB.TryGetValue(type, out var vb) ? vb : 0;
                    if (cb > ca)
                    {
                        var list = leakB.Where(l => l.TypeName == type).Take(50).ToList();
                        _leakDeltas.Add(new LeakTypeDelta
                        {
                            TypeName = type,
                            CountA = ca,
                            CountB = cb,
                            Objects = list,
                        });
                    }
                }
                _leakDeltas.Sort((x, y) => (y.CountB - y.CountA).CompareTo(x.CountB - x.CountA));

                // A / B 快照重复字符串 Top 20
                _dupTopA.Clear();
                _dupTopA.AddRange(dupA.Values
                    .Where(s => s.Count > 1)
                    .OrderByDescending(s => s.DuplicateBytes)
                    .Take(20));
                _dupTopB.Clear();
                _dupTopB.AddRange(dupB.Values
                    .Where(s => s.Count > 1)
                    .OrderByDescending(s => s.DuplicateBytes)
                    .Take(20));

                _hasResult = true;
                _statusText = "对比完成。";
            }
            catch (Exception e)
            {
                _statusText = $"对比过程中发生错误:\n{e.GetType().Name}: {e.Message}\n\n{e.StackTrace}";
                Debug.LogError($"[VUMS] SnapshotDiff 对比失败: {e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _diffing = false;
                Repaint();
            }
        }

        private void DrawOverviewTab()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("快照 A (基准)", EditorStyles.miniBoldLabel);
                OverviewValueRow("采集时间", _captureTimeA);
                OverviewValueRow("目标平台", _platformA);
            }

            GUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("快照 B (对比)", EditorStyles.miniBoldLabel);
                OverviewValueRow("采集时间", _captureTimeB);
                OverviewValueRow("目标平台", _platformB);
            }

            GUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("托管 (Managed)", EditorStyles.miniBoldLabel);
                OverviewRow("托管对象总数", _totalA, _totalB);
                OverviewRow("泄漏 Managed Shell", _leakA, _leakB);
                OverviewRow("重复字符串可避免内存", _dupAvoidA, _dupAvoidB, true);
                OverviewRow("Top50 大对象总大小", _bigTopSumA, _bigTopSumB, true);
            }

            GUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("原生 (Native)", EditorStyles.miniBoldLabel);
                MemoryOverviewRow("总已用内存", _memA.TotalUsedMemory, _memB.TotalUsedMemory);
                MemoryOverviewRow("GC 堆已用", _memA.GcHeapUsedMemory, _memB.GcHeapUsedMemory);
                MemoryOverviewRow("GC 堆保留", _memA.GcHeapReservedMemory, _memB.GcHeapReservedMemory);
                MemoryOverviewRow("图形 (Graphics)", _memA.GraphicsUsedMemory, _memB.GraphicsUsedMemory);
                MemoryOverviewRow("音频 (Audio)", _memA.AudioUsedMemory, _memB.AudioUsedMemory);
                MemoryOverviewRow("临时分配器 (Temp)", _memA.TempAllocatorUsedMemory, _memB.TempAllocatorUsedMemory);
                MemoryOverviewRow("Profiler 已用", _memA.ProfilerUsedMemory, _memB.ProfilerUsedMemory);
                MemoryOverviewRow("Memory Profiler 已用", _memA.MemoryProfilerUsedMemory, _memB.MemoryProfilerUsedMemory);
                MemoryOverviewRow(
                    "真实内存占用",
                    RealUsedMemory(_memA),
                    RealUsedMemory(_memB));
            }

            GUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "真实内存占用 = 总已用内存 − Profiler 已用 − Memory Profiler 已用（剔除分析工具自身开销）。Δ 表示 B 相对 A 的变化量。泄漏 Shell 与重复字符串为排查线索，增量上升通常意味着对应问题在加剧。",
                MessageType.None);
        }

        // 概览用 key/value 行：label 固定宽度，value 占剩余宽度，两者之间留出固定间距，
        // 避免长 label 把 value 挤到下一行被截断。
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

        private void MemoryOverviewRow(string label, ulong a, ulong b)
        {
            long aSigned = (long)a;
            long bSigned = (long)b;
            long delta = bSigned - aSigned;
            string sign = delta > 0 ? "+" : "";
            OverviewValueRow(label, $"A: {FormatBytes(aSigned)}    B: {FormatBytes(bSigned)}    Δ: {sign}{FormatBytes(delta)}");
        }

        // 真实内存占用 = 总已用 − Profiler 已用 − Memory Profiler 已用
        // 用分步减避免 ulong 下溢绕回成巨大正数；任一步不够减则按 0 处理。
        private static ulong RealUsedMemory(ProfileTargetMemoryStats m)
        {
            var v = m.TotalUsedMemory;
            v = v > m.ProfilerUsedMemory ? v - m.ProfilerUsedMemory : 0;
            v = v > m.MemoryProfilerUsedMemory ? v - m.MemoryProfilerUsedMemory : 0;
            return v;
        }

        private void DrawTypeDeltaTab()
        {
            EditorGUILayout.LabelField(
                $"共 {_typeDiffs.Count:N0} 个类型在两个快照间数量发生变化（按变化量 |Δ| 降序列出前 200）。");
            if (_typeDiffs.Count == 0)
            {
                EditorGUILayout.HelpBox("两个快照之间没有任何类型的对象数量发生变化。", MessageType.Info);
                return;
            }

            var shown = Math.Min(200, _typeDiffs.Count);
            for (var index = 0; index < shown; index++)
            {
                var d = _typeDiffs[index];
                var delta = d.CountB - d.CountA;
                var sign = delta > 0 ? "+" : "";
                // 单行显示：类型名占剩余宽度（过长可滑动/选中复制），数字固定宽度在右
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.SelectableLabel(d.TypeName, EditorStyles.label,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight),
                    GUILayout.ExpandWidth(true));
                EditorGUILayout.LabelField(
                    $"A:{d.CountA,8:N0}  B:{d.CountB,8:N0}  Δ:{sign}{delta,8:N0}",
                    GUILayout.Width(260));
                EditorGUILayout.EndHorizontal();
            }

            if (_typeDiffs.Count > shown)
                EditorGUILayout.HelpBox($"还有 {_typeDiffs.Count - shown:N0} 个变化较小的类型未列出。", MessageType.None);
        }

        private void DrawLeakDeltaTab()
        {
            if (_leakDeltas.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "两个快照之间未检测到泄漏 Managed Shell 数量增加（B 的各类泄漏数 ≤ A）。",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"共 {_leakDeltas.Count:N0} 个类型的泄漏 Managed Shell 数量增加。");
            foreach (var delta in _leakDeltas)
            {
                delta.Expanded = EditorGUILayout.Foldout(
                    delta.Expanded,
                    $"{delta.TypeName}  (A:{delta.CountA:N0} → B:{delta.CountB:N0}, +{delta.CountB - delta.CountA:N0})",
                    true);
                if (!delta.Expanded)
                    continue;

                var shown = Math.Min(50, delta.Objects.Count);
                EditorGUILayout.LabelField(
                    $"B 中该类型泄漏对象（显示前 {shown:N0} / 共 {delta.Objects.Count:N0}，展开看路径）:",
                    EditorStyles.miniLabel);
                for (var index = 0; index < shown; index++)
                {
                    var obj = delta.Objects[index];
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        obj.Expanded = EditorGUILayout.Foldout(
                            obj.Expanded,
                            $"{obj.TypeName} @ 0x{obj.Address:X}",
                            true);
                        if (obj.Expanded)
                        {
                            GUILayout.Space(3);
                            DrawLeakedObjectRetentionNodes(obj);
                        }
                    }
                }
            }
        }

        private void DrawDuplicateStringTab()
        {
            var delta = _dupAvoidB - _dupAvoidA;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "重复字符串可避免内存",
                    $"A: {FormatBytes(_dupAvoidA)}   B: {FormatBytes(_dupAvoidB)}   Δ:{(delta > 0 ? "+" : "")}{FormatBytes(delta)}");
            }

            GUILayout.Space(6);
            EditorGUILayout.LabelField("A 快照重复字符串 Top 20（按可避免重复内存排序）:", EditorStyles.boldLabel);
            DrawDuplicateStringList(_dupTopA);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("B 快照重复字符串 Top 20（按可避免重复内存排序）:", EditorStyles.boldLabel);
            DrawDuplicateStringList(_dupTopB);
        }

        private void DrawDuplicateStringList(List<DuplicateStringStat> list)
        {
            if (list == null || list.Count == 0)
            {
                EditorGUILayout.HelpBox("未发现重复字符串实例。", MessageType.Info);
                return;
            }

            foreach (var stringStat in list)
            {
                var preview = stringStat.Value
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");
                if (preview.Length > 100)
                    preview = preview.Substring(0, 100) + "...";

                EditorGUILayout.LabelField(
                    $"  {stringStat.Count,6:N0} x | 总计 {FormatBytes(stringStat.TotalBytes),10} | \"{preview}\"");
            }
        }

        private void OverviewRow(string label, long a, long b, bool isBytes = false)
        {
            string sa = isBytes ? FormatBytes(a) : a.ToString("N0");
            string sb = isBytes ? FormatBytes(b) : b.ToString("N0");
            var delta = b - a;
            var sdelta = isBytes ? FormatBytes(delta) : delta.ToString("N0");
            var sign = delta > 0 ? "+" : "";
            OverviewValueRow(label, $"A: {sa}    B: {sb}    Δ: {sign}{sdelta}");
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

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                for (var depth = 0; depth < nodes.Length; depth++)
                {
                    if (depth > 0 && !leakedObject.RetentionNodeExpanded[depth - 1])
                        break;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(depth * 18f);
                        var hasParentNode = depth < nodes.Length - 1;
                        if (hasParentNode)
                        {
                            leakedObject.RetentionNodeExpanded[depth] = EditorGUILayout.Foldout(
                                leakedObject.RetentionNodeExpanded[depth],
                                nodes[depth],
                                true);
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

        // ---------------- 分析辅助 ----------------

        private static Dictionary<string, int> CountByType(SnapshotFile file, ManagedClassInstance[] objs)
        {
            var dict = new Dictionary<string, int>();
            foreach (var obj in objs)
            {
                if (obj.ObjectAddress == 0)
                    continue;
                var type = file.GetTypeName(obj.TypeInfo.TypeIndex);
                dict.TryGetValue(type, out var count);
                dict[type] = count + 1;
            }
            return dict;
        }

        private static List<LeakedObjectInfo> CollectLeaks(SnapshotFile file, ManagedClassInstance[] objs)
        {
            var result = new List<LeakedObjectInfo>();
            var unityObjects = objs.Where(o => o.InheritsFromUnityEngineObject(file)).ToArray();
            foreach (var obj in unityObjects)
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
            var dict = new Dictionary<string, DuplicateStringStat>(StringComparer.Ordinal);
            foreach (var managedString in file.AllManagedStrings)
            {
                if (!dict.TryGetValue(managedString.Value, out var stringStat))
                {
                    stringStat = new DuplicateStringStat
                    {
                        Value = managedString.Value,
                        SingleInstanceBytes = managedString.SizeBytes,
                    };
                    dict.Add(managedString.Value, stringStat);
                }

                stringStat.Count++;
                stringStat.TotalBytes += managedString.SizeBytes;
            }
            return dict;
        }

        private static long SumTopLargeObjects(SnapshotFile file, ManagedClassInstance[] objs)
        {
            var sizes = new List<long>();
            foreach (var obj in objs)
            {
                if (obj.ObjectAddress == 0)
                    continue;

                var rawInfo = file.ParseManagedObjectInfo(obj.ObjectAddress);
                if (rawInfo.IsKnownType && rawInfo.Size > 0)
                    sizes.Add(rawInfo.Size);
            }

            sizes.Sort((x, y) => y.CompareTo(x));
            long sum = 0;
            for (var i = 0; i < Math.Min(50, sizes.Count); i++)
                sum += sizes[i];
            return sum;
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

        // ---------------- 数据结构 ----------------

        private sealed class TypeCountDiff
        {
            public string TypeName;
            public int CountA;
            public int CountB;
        }

        private sealed class LeakTypeDelta
        {
            public string TypeName;
            public int CountA;
            public int CountB;
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
