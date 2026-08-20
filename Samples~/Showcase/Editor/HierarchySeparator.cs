using UnityEditor;
using UnityEngine;

namespace Hapbeat.Samples.Showcase.EditorTools
{
    /// <summary>
    /// Hierarchy 内で名前が "---" / ">>>" / "===" で始まる GameObject を
    /// 太字 + 背景塗りつぶしの区切り線として描画する。
    /// 例: "-------- Zone --------" / ">>> Player <<<" 等。
    ///
    /// 区切り用 GameObject は Transform だけの空オブジェクトなので、
    /// このスクリプトが無くても scene は壊れない（名前がそのまま出るだけ）。
    /// あくまで Showcase の Hierarchy 可読性のための装飾。
    /// </summary>
    [InitializeOnLoad]
    public static class HierarchySeparator
    {
        private static readonly Color s_bgColor = new Color(0.18f, 0.22f, 0.28f, 1f);
        private static readonly Color s_textColor = new Color(0.92f, 0.92f, 0.92f, 1f);

        static HierarchySeparator()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyGUI;
        }

        private static void OnHierarchyGUI(int instanceID, Rect rect)
        {
            // 解決 API は Unity 6 の途中で InstanceIDToObject → EntityIdToObject に改名され、
            // 新名は 6000.0 LTS に存在しない。SDK 本体と同じ互換ヘルパー経由で呼ぶ。
            var go = Hapbeat.Editor.HapbeatEditorCompat.IdToObject(instanceID) as GameObject;
            if (go == null) return;

            string n = go.name;
            if (string.IsNullOrEmpty(n)) return;

            bool isSep = n.StartsWith("---") || n.StartsWith(">>>") || n.StartsWith("===");
            if (!isSep) return;

            EditorGUI.DrawRect(rect, s_bgColor);
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = s_textColor },
                fontSize = 11,
            };
            string label = n.Trim('-', '>', '<', '=', ' ');
            EditorGUI.LabelField(rect, label, style);
        }
    }
}
