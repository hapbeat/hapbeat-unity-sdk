using UnityEditor;
using UnityEngine;

namespace Hapbeat.Samples.Tutorial.EditorTools
{
    /// <summary>
    /// Hierarchy 内で名前が "---" または ">>>" で始まる GameObject を
    /// 太字 + 背景塗りつぶしの区切り線として描画する。
    /// 例: "--- Zone 3 ---" / ">>> Player <<<" 等。
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
            // Unity 6 で EditorUtility.InstanceIDToObject(int) は obsolete。
            // 後継 API は EditorUtility.EntityIdToObject(EntityId)。
            // 古い Unity (6 未満) 互換のため reflection で呼び分け。
            var go = ResolveGameObject(instanceID);
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

        // Unity 6.3 で EditorUtility.InstanceIDToObject(int) は obsolete
        // (新しい EntityIdToObject(EntityId) 推奨)。ただし新 API は EntityId
        // 型引数を取り、reflection で渡しにくい。本サンプルでは旧 API を
        // 引き続き使用しつつ pragma で warning を抑止する (機能的には同じ)。
        private static GameObject ResolveGameObject(int instanceID)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            return EditorUtility.InstanceIDToObject(instanceID) as GameObject;
#pragma warning restore CS0618
        }
    }
}
