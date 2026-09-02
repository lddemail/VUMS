using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VUMS.Editor
{
    /// <summary>
    /// 重复字符串的来源分类。.snap 中的托管字符串会被 Mono 反复分配出等价实例，
    /// 本枚举用于推断“这些重复实例最可能来自哪类代码写法”，从而给出可执行的优化建议。
    /// </summary>
    internal enum DuplicateStringSource
    {
        Constant,       // 业务常量被多次独立分配
        AssetPath,      // 资源 / AssetBundle 路径拼接
        FormatLike,     // string.Format / 字符串插值
        ConcatenatedId, // id / 索引拼接
        JsonOrMarkup,   // 序列化 / 协议 / UI 标记文本
    }

    internal static class VumsStringSourceHelper
    {
        // {0} / {} / {12} 等 C# 格式占位符
        private static readonly Regex FormatPlaceholderRegex = new Regex(@"\{\d*\}", RegexOptions.Compiled);
        // %d %s %x %f %c 等 C 风格 printf 占位符
        private static readonly Regex PercentFormatRegex = new Regex(@"%[dscxfge]", RegexOptions.Compiled);
        // 常见资源 / 配置扩展名结尾
        private static readonly HashSet<string> AssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prefab", "asset", "assets", "unity", "unity3d", "assetbundle", "bundle", "ab",
            "png", "jpg", "jpeg", "tga", "bmp", "tif", "tiff", "psd", "exr", "hdr",
            "mat", "shader", "shadergraph", "compute", "controller", "anim", "overridecontroller",
            "wav", "mp3", "ogg", "aiff", "fsb", "bank",
            "fbx", "obj", "dae", "3ds", "max", "blend",
            "ttf", "otf", "fontsettings", "guiskin",
            "txt", "bytes", "csv", "xml", "json", "yaml", "lua", "proto",
        };

        /// <summary>
        /// 推断重复字符串最可能的来源写法。返回枚举，配合 Label / Suggestion 给出 UI 文案。
        /// 优先级：格式化 > 序列化/标记 > 资源路径 > id 拼接 > 业务常量。
        /// </summary>
        internal static DuplicateStringSource Classify(string value)
        {
            if (string.IsNullOrEmpty(value))
                return DuplicateStringSource.Constant;

            // 1) string.Format / $"" 插值占位符
            if (value.IndexOf('{') >= 0 && FormatPlaceholderRegex.IsMatch(value))
                return DuplicateStringSource.FormatLike;
            if (value.IndexOf('%') >= 0 && PercentFormatRegex.IsMatch(value))
                return DuplicateStringSource.FormatLike;

            // 2) 序列化 / 标记文本（JSON 或 XML/HTML）
            if ((value.IndexOf('<') >= 0 && value.IndexOf('>') >= 0)
                || (value.IndexOf('{') >= 0 && value.IndexOf('"') >= 0 && value.IndexOf(':') >= 0))
                return DuplicateStringSource.JsonOrMarkup;

            // 3) 资源 / AssetBundle 路径拼接
            if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0)
                return DuplicateStringSource.AssetPath;
            var lastDot = value.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < value.Length - 1)
            {
                var ext = value.Substring(lastDot + 1);
                if (AssetExtensions.Contains(ext))
                    return DuplicateStringSource.AssetPath;
            }

            // 4) id / 索引拼接（字母与数字相邻，或有 2+ 位连续数字）
            if (HasConcatenatedIdPattern(value))
                return DuplicateStringSource.ConcatenatedId;

            // 5) 其余：业务常量被多次独立分配
            return DuplicateStringSource.Constant;
        }

        /// <summary>
        /// 字母与数字直接相邻，或出现 2+ 位连续数字 → 疑似由 id / 编号 / 数量拼接生成。
        /// </summary>
        private static bool HasConcatenatedIdPattern(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!char.IsDigit(c))
                    continue;

                // 连续 2+ 位数字：数量 / 编号
                if (i + 1 < value.Length && char.IsDigit(value[i + 1]))
                    return true;
                // 字母与数字直接相邻：拼接生成
                if (i > 0 && char.IsLetter(value[i - 1]))
                    return true;
                if (i + 1 < value.Length && char.IsLetter(value[i + 1]))
                    return true;
            }

            return false;
        }

        internal static string Label(DuplicateStringSource src)
        {
            switch (src)
            {
                case DuplicateStringSource.AssetPath: return "资源路径";
                case DuplicateStringSource.FormatLike: return "字符串格式化";
                case DuplicateStringSource.ConcatenatedId: return "id 拼接";
                case DuplicateStringSource.JsonOrMarkup: return "序列化/标记";
                default: return "业务常量";
            }
        }

        internal static string Suggestion(DuplicateStringSource src)
        {
            switch (src)
            {
                case DuplicateStringSource.AssetPath:
                    return "疑似资源 / AssetBundle key 在代码里反复拼接生成（如 \"Assets/UI/\" + name）。建议把路径抽成 Addressables key 常量或路径常量池，避免运行时拼接出大量等价字符串。";
                case DuplicateStringSource.FormatLike:
                    return "含 {0} / % 等格式占位符，疑似 string.Format / $\"\" 插值每次调用都生成新实例。建议对高频日志 / 路径用 StringBuilder 缓存，或对固定模板预生成常量。";
                case DuplicateStringSource.ConcatenatedId:
                    return "含可变数字后缀，疑似由 id / index 拼接生成（如 \"item_\" + id）。建议把生成结果缓存进 Dictionary<string,…> / NameCache，或对固定 key 集合预构建字符串池。";
                case DuplicateStringSource.JsonOrMarkup:
                    return "含 {} / <> 等结构符，疑似序列化 / 协议 / UI 标记文本反复分配。建议对重复结构复用同一字符串，或对高频序列化开启字符串池。";
                default:
                    return "疑似同一业务常量被多次独立分配。Unity / Mono 不会自动 intern 字符串，建议改为 static readonly 常量或 string.Intern(…)，让所有引用共享同一实例，省去重复分配。";
            }
        }
    }
}
