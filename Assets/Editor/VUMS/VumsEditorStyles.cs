using System;
using UnityEditor;
using UnityEngine;

namespace VUMS.Editor
{
    /// <summary>
    /// 统一视觉层。当前版本刻意只使用 Unity 原生 IMGUI 样式（EditorStyles），
    /// 不引入任何自定义颜色或背景纹理，从而与 Unity 编辑器自身主题保持一致。
    /// </summary>
    internal static class VumsEditorStyles
    {
        internal const float PagePadding = 12f;
        internal const float SectionSpacing = 8f;
        internal const float TreeIndent = 18f;

        private static bool _initialized;

        internal static GUIStyle HeaderTitle { get; private set; }
        internal static GUIStyle HeaderSubtitle { get; private set; }
        internal static GUIStyle Card { get; private set; }
        internal static GUIStyle CompactCard { get; private set; }
        internal static GUIStyle SectionTitle { get; private set; }
        internal static GUIStyle SectionDescription { get; private set; }
        internal static GUIStyle MetricLabel { get; private set; }
        internal static GUIStyle MetricValue { get; private set; }
        internal static GUIStyle PrimaryButton { get; private set; }
        internal static GUIStyle SecondaryButton { get; private set; }
        internal static GUIStyle DangerButton { get; private set; }
        internal static GUIStyle StatusText { get; private set; }
        internal static GUIStyle MutedLabel { get; private set; }
        internal static GUIStyle SelectableRow { get; private set; }
        internal static GUIStyle Foldout { get; private set; }

        internal static Color Success => GUI.skin.label.normal.textColor;
        internal static Color Warning => GUI.skin.label.normal.textColor;
        internal static Color Danger => GUI.skin.label.normal.textColor;
        internal static Color MutedText => GUI.skin.label.normal.textColor;

        internal static void EnsureInitialized()
        {
            if (_initialized)
                return;
            _initialized = true;

            // 全部复用 Unity 自带样式，不创建任何自定义纹理或配色。
            HeaderTitle = EditorStyles.boldLabel;
            HeaderSubtitle = EditorStyles.wordWrappedMiniLabel;
            Card = EditorStyles.helpBox;
            CompactCard = EditorStyles.helpBox;
            SectionTitle = EditorStyles.boldLabel;
            SectionDescription = EditorStyles.wordWrappedMiniLabel;
            MetricLabel = EditorStyles.label;
            MetricValue = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
            };
            PrimaryButton = GUI.skin.button;
            SecondaryButton = GUI.skin.button;
            DangerButton = new GUIStyle(GUI.skin.button)
            {
                normal = { textColor = new Color(0.88f, 0.30f, 0.30f, 1f) },
                hover = { textColor = new Color(0.88f, 0.30f, 0.30f, 1f) },
            };
            StatusText = EditorStyles.wordWrappedLabel;
            MutedLabel = EditorStyles.miniLabel;
            SelectableRow = EditorStyles.label;
            Foldout = EditorStyles.foldout;
        }

        // 原生标题 + 副标题，无彩色 banner。
        internal static void DrawHeader(string title, string subtitle, string badge = null)
        {
            EnsureInitialized();
            GUILayout.Label(title, HeaderTitle);
            if (!string.IsNullOrEmpty(subtitle))
                GUILayout.Label(subtitle, HeaderSubtitle);
            if (!string.IsNullOrEmpty(badge))
                GUILayout.Label(badge, EditorStyles.miniLabel);
        }

        // 原生 Toolbar：选中态由 Unity 自身绘制（Pro 皮肤下即蓝色高亮）。
        internal static int DrawTabs(int selectedIndex, string[] tabs)
        {
            EnsureInitialized();
            if (tabs == null || tabs.Length == 0)
                return 0;
            return GUILayout.Toolbar(selectedIndex, tabs);
        }

        internal static void DrawSectionHeader(string title, string description = null)
        {
            EnsureInitialized();
            GUILayout.Label(title, SectionTitle);
            if (!string.IsNullOrEmpty(description))
                GUILayout.Label(description, SectionDescription);
        }

        internal static void DrawMetricRow(string label, string value, float labelWidth = 200f)
        {
            EnsureInitialized();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, MetricLabel, GUILayout.Width(labelWidth));
                GUILayout.Space(20f);
                GUILayout.Label(value, MetricValue, GUILayout.ExpandWidth(true));
            }
        }

        /// <summary>
        /// 绘制一行内容，并支持在该行上右键弹出菜单。
        /// drawContent 内部使用普通 GUILayout 即可；本方法会预留行高、拦截右键
        /// ContextClick 事件，弹出含 “Copy” 的上下文菜单，点击 Copy 才把整行文本
        /// 写入剪贴板，并通过窗口 ShowNotification 给出“已复制”反馈。
        /// </summary>
        internal static void CopyableRow(EditorWindow window, float height, string copyText, Action drawContent)
        {
            CopyableRow(window, height, copyText, drawContent, null);
        }

        /// <summary>
        /// 可复制行；onTap 不为 null 时，左键点击整行触发该回调（右键仍复制）。
        /// </summary>
        internal static void CopyableRow(EditorWindow window, float height, string copyText, Action drawContent, Action onTap)
        {
            EnsureInitialized();
            if (drawContent == null)
                return;

            // 没有可复制文本时直接绘制，避免无意义的交互区域。
            if (string.IsNullOrEmpty(copyText))
            {
                drawContent();
                return;
            }

            var rect = EditorGUILayout.GetControlRect(false, height, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
                var menu = new GenericMenu();
                menu.AddItem(
                    new GUIContent("Copy"),
                    false,
                    () =>
                    {
                        EditorGUIUtility.systemCopyBuffer = copyText;
                        window?.ShowNotification(new GUIContent("已复制行内容"));
                    });
                menu.ShowAsContext();
                return;
            }

            if (onTap != null && Event.current.type == EventType.MouseDown
                && Event.current.button == 0 && rect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
                onTap();
            }

            // 把内容拉回到刚预留出的行区域内，使其正好覆盖整行。
            GUILayout.Space(-height);
            drawContent();
        }

        internal static void DrawStatus(string text, MessageType type)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(text))
                return;
            EditorGUILayout.HelpBox(text, type);
        }

        internal static void DrawDivider()
        {
            EnsureInitialized();
            var rect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                var color = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.10f)
                    : new Color(0f, 0f, 0f, 0.20f);
                EditorGUI.DrawRect(rect, color);
            }
        }

        internal static void DrawEmptyState(string title, string description, MessageType type = MessageType.Info)
        {
            EnsureInitialized();
            using (new EditorGUILayout.VerticalScope(Card))
            {
                GUILayout.Label(title, SectionTitle);
                GUILayout.Label(description, SectionDescription);
                if (type != MessageType.None)
                    EditorGUILayout.HelpBox(description, type);
            }
        }

        internal static Color GetDeltaColor(long delta)
        {
            return GUI.skin.label.normal.textColor;
        }

    }
}
