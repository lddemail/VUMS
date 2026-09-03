using System;
using System.IO;
using UnityEngine;

namespace VUMS.Editor
{
    /// <summary>
    /// VUMS 的统一配置（当前含 AI 配置，后续可扩展其它设置）。
    /// 所有字段持久化到 Assets/Editor/VUMS/Settings.json。
    /// </summary>
    [Serializable]
    internal sealed class VumsSettings
    {
        public const string FileName = "Settings.json";

        private static readonly string FilePath =
            Path.Combine(Application.dataPath, "Editor", "VUMS", FileName);

        public static string SettingsFilePath => FilePath;

        public string BaseUrl = "https://api.openai.com/v1";
        public string ApiKey = "";
        public string Model = "gpt-5.6-terra";
        public string ProjectRoot = "";
        public int TimeoutSeconds = 30;

        public static VumsSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return Default();
                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json))
                    return Default();
                var settings = JsonUtility.FromJson<VumsSettings>(json);
                return settings ?? Default();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[VUMS] 读取 AI 配置失败，回退默认: {exception.Message}");
                return Default();
            }
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[VUMS] 保存 AI 配置失败: {exception.Message}");
            }
        }

        public static VumsSettings Default()
        {
            return new VumsSettings();
        }

        public bool IsConfigured()
        {
            return !string.IsNullOrWhiteSpace(BaseUrl)
                && !string.IsNullOrWhiteSpace(ApiKey)
                && !string.IsNullOrWhiteSpace(Model);
        }
    }
}
