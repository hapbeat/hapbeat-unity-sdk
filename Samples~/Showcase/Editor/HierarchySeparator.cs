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
            // Unity 6 で EditorUtility.InstanceIDToObject(int) は obsolete となり、
            // 後継 API は EditorUtility.EntityIdToObject(EntityId)。EntityId は int
            // からの暗黙変換を持つ（現行 Unity 6 では InstanceID == EntityId が数値的に
            // 一致）ため、hierarchyWindowItemOnGUI が渡す int をそのまま渡せる。
            // SDK 本体 (HapbeatEventMapPlaySnapshot) と同じ正攻法に揃える。
            var go = EditorUtility.EntityIdToObject(instanceID) as GameObject;
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
