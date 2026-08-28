using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UMS.Analysis;
using UMS.Analysis.Structures.Objects;

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
        private string _resultText = "请选择一个 .snap 快照文件，然后点击「分析」。";
        private Vector2 _scroll;
        private bool _analyzing;

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
                "Assets/ThirdParty/UMS",
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
            GUILayout.Label("Managed Memory Leak Analysis", EditorStyles.boldLabel);

            // --- 选择文件 + 分析按钮 + 路径框（同一行）---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("选择 .snap 文件", GUILayout.Width(150)))
            {
                string startDir = string.IsNullOrEmpty(_snapPath) ? "" : Path.GetDirectoryName(_snapPath);
                string picked = EditorUtility.OpenFilePanel("选择内存快照文件", startDir, "snap");
                if (!string.IsNullOrEmpty(picked))
                    _snapPath = picked;
            }

            EditorGUI.BeginDisabledGroup(_analyzing || string.IsNullOrEmpty(_snapPath));
            if (GUILayout.Button("分析", GUILayout.Width(80)))
            {
                if (!_analyzing)
                {
                    _analyzing = true;
                    EditorApplication.delayCall += RunAnalysisOnce;
                }
            }
            EditorGUI.EndDisabledGroup();

            _snapPath = EditorGUILayout.TextField(_snapPath);
            EditorGUILayout.EndHorizontal();

            // --- 结果展示 ---
            GUILayout.Space(8);
            GUILayout.Label("分析结果", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(_resultText, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private void RunAnalysisOnce()
        {
            EditorApplication.delayCall -= RunAnalysisOnce;
            Analyze();
        }

        private void Analyze()
        {
            if (string.IsNullOrEmpty(_snapPath) || !File.Exists(_snapPath))
            {
                _resultText = "错误：文件路径为空或文件不存在。";
                Repaint();
                return;
            }

            _analyzing = true;
            Repaint();

            try
            {
                var sb = new StringBuilder();
                var totalStart = DateTime.Now;

                EditorUtility.DisplayProgressBar("分析中", "正在读取快照文件...", 0.1f);
                using var file = new SnapshotFile(_snapPath);

                sb.AppendLine($"快照格式版本 : {file.SnapshotFormatVersion} ({(int) file.SnapshotFormatVersion})");
                sb.AppendLine($"采集时间     : {file.CaptureDateTime}");
                sb.AppendLine($"目标平台     : {file.ProfileTargetInfo}");
                sb.AppendLine($"采集时内存   : {file.ProfileTargetMemoryStats}");
                sb.AppendLine();

                EditorUtility.DisplayProgressBar("分析中", "正在加载托管对象 (GC Roots)...", 0.4f);
                file.LoadManagedObjectsFromGcRoots();
                EditorUtility.DisplayProgressBar("分析中", "正在加载托管对象 (静态字段)...", 0.6f);
                file.LoadManagedObjectsFromStaticFields();

                var allObjects = file.AllManagedClassInstances.ToArray();
                sb.AppendLine($"共发现 {allObjects.Length} 个托管对象。");

                EditorUtility.DisplayProgressBar("分析中", "正在检测泄漏的 Managed Shell...", 0.85f);
                var unityObjects = allObjects.Where(i => i.InheritsFromUnityEngineObject(file)).ToArray();
                sb.AppendLine($"其中 {unityObjects.Length} 个继承自 UnityEngine.Object。");
                sb.AppendLine();

                int numLeaked = 0;
                var leakedTypes = new Dictionary<string, int>();
                var leakedLines = new List<string>();

                foreach (var obj in unityObjects)
                {
                    if (!obj.IsLeakedManagedShell(file))
                        continue;

                    var typeName = file.GetTypeName(obj.TypeInfo.TypeIndex);
                    leakedLines.Add($"  泄漏对象类型 : {typeName} @ 0x{obj.ObjectAddress:X}");
                    leakedLines.Add($"    保留路径   : {obj.GetFirstObservedRetentionPath(file)}");

                    leakedTypes.TryGetValue(typeName, out var count);
                    leakedTypes[typeName] = count + 1;
                    numLeaked++;
                }

                sb.AppendLine($"检测完成，共 {numLeaked} 个泄漏的 Managed Shell。");
                if (numLeaked > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("泄漏详情:");
                    sb.AppendLine(string.Join("\n", leakedLines));
                    sb.AppendLine();
                    sb.AppendLine("按类型统计:");
                    foreach (var kvp in leakedTypes.OrderByDescending(k => k.Value))
                        sb.AppendLine($"  {kvp.Value} x {kvp.Key}");
                }

                sb.AppendLine();
                sb.AppendLine($"总耗时: {(DateTime.Now - totalStart).TotalMilliseconds:F0} ms");

                _resultText = sb.ToString();
            }
            catch (Exception e)
            {
                _resultText = $"分析过程中发生错误:\n{e.GetType().Name}: {e.Message}\n\n{e.StackTrace}";
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
