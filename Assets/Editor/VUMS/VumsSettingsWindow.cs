using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VUMS.Editor
{
    /// <summary>
    /// VUMS 统一设置窗口。通过菜单 VUMS → Settings 打开。
    /// 两个区块竖向铺开（不做切页）：
    ///   - AISetting：填写 Base URL / API Key / Model / 项目目录，提供「保存」与「测试连接」。
    ///   - ExportPackage：将 VUMS 工具本身导出为 Unity Package。
    /// </summary>
    internal sealed class VumsSettingsWindow : EditorWindow
    {
        private VumsSettings _settings;
        private string _baseUrl;
        private string _apiKey;
        private string _model;
        private string _projectRoot;
        private int _timeoutSeconds;

        private bool _testing;
        private string _testStatus = "";
        private MessageType _testStatusType = MessageType.None;

        private string _exportStatus = "";
        private MessageType _exportStatusType = MessageType.None;

        private Vector2 _scroll;

        public static void ShowWindow()
        {
            var window = GetWindow<VumsSettingsWindow>("VUMS Settings");
            window.minSize = new Vector2(460f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = VumsSettings.Load();
            _baseUrl = _settings.BaseUrl;
            _apiKey = _settings.ApiKey;
            _model = _settings.Model;
            _projectRoot = _settings.ProjectRoot;
            _timeoutSeconds = _settings.TimeoutSeconds;
        }

        private void OnGUI()
        {
            VumsEditorStyles.EnsureInitialized();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                DrawAiSection();
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(VumsEditorStyles.Card))
            {
                DrawExportSection();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawAiSection()
        {
            VumsEditorStyles.DrawSectionHeader(
                "AISetting",
                "配置 VUMS 调用的大模型。所有信息保存在Settings.json。");

            EditorGUILayout.Space();
            _baseUrl = EditorGUILayout.TextField("接口地址 (Base URL)", _baseUrl);
            _apiKey = EditorGUILayout.PasswordField("API Key", _apiKey);
            _model = EditorGUILayout.TextField("模型 (Model)", _model);
            _timeoutSeconds = EditorGUILayout.IntField("超时 (秒)", _timeoutSeconds);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("项目目录");
                if (GUILayout.Button("浏览", GUILayout.Width(60f)))
                {
                    var picked = EditorUtility.OpenFolderPanel("选择项目根目录", _projectRoot, "");
                    if (!string.IsNullOrEmpty(picked))
                        _projectRoot = picked;
                }
            }

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(_projectRoot);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存", VumsEditorStyles.PrimaryButton))
                    Save();
                if (GUILayout.Button("测试连接", VumsEditorStyles.SecondaryButton))
                    TestConnection();
            }

            if (!string.IsNullOrEmpty(_testStatus) && _testStatusType != MessageType.None)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_testStatus, _testStatusType);
            }
        }

        private void DrawExportSection()
        {
            VumsEditorStyles.DrawSectionHeader(
                "ExportPackage",
                "将 VUMS 工具本身（UMS 库 DLL 与 Editor 窗口脚本）导出为 Unity Package，便于分发到其它项目。" +
                "不带入任何依赖。导出时 AI 配置中的 API Key 会被自动清空，不影响本机配置。");

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("导出 VUMS.unitypackage", VumsEditorStyles.PrimaryButton))
                    DoExport();
            }

            if (!string.IsNullOrEmpty(_exportStatus) && _exportStatusType != MessageType.None)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_exportStatus, _exportStatusType);
            }
        }

        private void Save()
        {
            _settings.BaseUrl = _baseUrl.Trim();
            _settings.ApiKey = _apiKey;
            _settings.Model = _model.Trim();
            _settings.ProjectRoot = _projectRoot.Trim();
            _settings.TimeoutSeconds = Mathf.Clamp(_timeoutSeconds, 5, 600);
            _settings.Save();
            ShowNotification(new GUIContent("已保存"));
        }

        private async void TestConnection()
        {
            if (_testing)
                return;
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_model))
            {
                _testStatus = "请先填写 Base URL / API Key / Model";
                _testStatusType = MessageType.Warning;
                Repaint();
                return;
            }

            _testing = true;
            _testStatus = "正在测试连接...";
            _testStatusType = MessageType.Info;
            Repaint();

            var result = await OpenAiCompatibleProvider.ChatAsync(
                _baseUrl.Trim(), _apiKey, _model.Trim(),
                "你是一个用于验证连接的助手，只用一句话回答。",
                "用一句中文回答：连接是否成功？",
                30, Mathf.Clamp(_timeoutSeconds, 5, 600),
                CancellationToken.None);

            _testing = false;
            if (result.Success)
            {
                _testStatus = $"连接成功：模型返回「{result.Content}」"
                    + (result.CompletionTokens > 0
                        ? $"（用量 {result.PromptTokens}+{result.CompletionTokens} tokens）"
                        : string.Empty);
                _testStatusType = MessageType.Info;
                Save();
            }
            else
            {
                _testStatus = $"连接失败：{result.Error}";
                _testStatusType = MessageType.Error;
            }

            Repaint();
        }

        private void DoExport()
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

            // 导出时排除本机 Settings.json（含 API Key，且导入会覆盖用户的本地配置）。
            // Unity 的 ExportPackage 不支持单文件排除，故临时把 Settings.json 及其 .meta
            // 移出 Assets 目录，导出结束后再原样移回，本机配置完全不受影响。
            var settingsPath = VumsSettings.SettingsFilePath;
            var settingsMetaPath = settingsPath + ".meta";
            var excludeDir = Path.Combine(buildDir, "ExportExcludeTmp");
            if (!Directory.Exists(excludeDir))
                Directory.CreateDirectory(excludeDir);
            var tmpJson = Path.Combine(excludeDir, "Settings.json");
            var tmpMeta = Path.Combine(excludeDir, "Settings.json.meta");

            var movedJson = File.Exists(settingsPath);
            var movedMeta = File.Exists(settingsMetaPath);
            try
            {
                if (movedJson)
                    File.Move(settingsPath, tmpJson);
                if (movedMeta)
                    File.Move(settingsMetaPath, tmpMeta);

                // 让 AssetDatabase 与磁盘同步，确保 Settings.json 不被带入 package
                AssetDatabase.Refresh();

                Debug.Log($"[VUMS] 开始导出 package 到: {exportPath}");
                AssetDatabase.ExportPackage(folders, exportPath, ExportPackageOptions.Recurse);
                Debug.Log($"[VUMS] 已导出 package 到: {exportPath}");
                _exportStatus = $"已导出 Unity Package 到:\n{exportPath}（已排除本机 Settings.json）";
                _exportStatusType = MessageType.Info;
                ShowNotification(new GUIContent("导出完成"));
            }
            catch (System.Exception exception)
            {
                _exportStatus = $"导出失败：{exception.Message}";
                _exportStatusType = MessageType.Error;
            }
            finally
            {
                // 恢复本机配置
                if (movedJson)
                    File.Move(tmpJson, settingsPath);
                if (movedMeta)
                    File.Move(tmpMeta, settingsMetaPath);
                if (Directory.Exists(excludeDir))
                    Directory.Delete(excludeDir, true);
                AssetDatabase.Refresh();
            }

            Repaint();
        }
    }
}
