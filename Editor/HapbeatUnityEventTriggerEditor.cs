#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for HapbeatUnityEventTrigger.
    /// Extends the base trigger editor with a usage guide and event flow diagram.
    /// </summary>
    [CustomEditor(typeof(HapbeatUnityEventTrigger))]
    [CanEditMultipleObjects]
    public class HapbeatUnityEventTriggerEditor : HapbeatTriggerBaseEditor
    {
        private bool _showUsageGuide = false;

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            EditorGUILayout.Space(5);
            _showUsageGuide = EditorGUILayout.Foldout(_showUsageGuide, "Usage Guide", true);

            if (_showUsageGuide)
            {
                EditorGUILayout.HelpBox(
                    "UnityEvent (Inspector) \u306b\u4ee5\u4e0b\u306e\u30e1\u30bd\u30c3\u30c9\u3092\u63a5\u7d9a\u3057\u3066\u4f7f\u3044\u307e\u3059:\n\n" +
                    "\u25b6 Fire()\n" +
                    "  \u89e6\u899a\u30a4\u30d9\u30f3\u30c8\u3092\u767a\u706b\u3002\u6700\u3082\u57fa\u672c\u7684\u306a\u4f7f\u3044\u65b9\u3002\n" +
                    "  \u4f8b: Button.onClick, XRI selectEntered\n\n" +
                    "\u25b6 FireWithGain(float)\n" +
                    "  gain \u3092\u4e0a\u66f8\u304d\u3057\u3066\u767a\u706b\u3002\n" +
                    "  \u4f8b: Animation Event (\u52d5\u7684\u5f37\u5ea6), Slider.onValueChanged\n\n" +
                    "\u25b6 Stop()\n" +
                    "  \u89e6\u899a\u30a4\u30d9\u30f3\u30c8\u3092\u505c\u6b62\u3002\n" +
                    "  \u4f8b: XRI selectExited, \u96e2\u3059/\u89e3\u9664\u30a4\u30d9\u30f3\u30c8",
                    MessageType.Info);

                EditorGUILayout.HelpBox(
                    "\u30a4\u30d9\u30f3\u30c8\u30d5\u30ed\u30fc:\n\n" +
                    "XRI Interactable          Hapbeat SDK              Device\n" +
                    "  selectEntered  \u2500\u2500\u2500\u2500\u2500>  Fire()\n" +
                    "                            \u2502\n" +
                    "                     HapbeatManager.Play()\n" +
                    "                            \u2502\n" +
                    "                       UDP broadcast  \u2500\u2500\u2500\u2500>  Hapbeat\n" +
                    "                                          eventId \u2192 clip \u2192 \u518d\u751f",
                    MessageType.None);

                EditorGUILayout.HelpBox(
                    "XRI \u63a5\u7d9a\u4f8b:\n" +
                    "\u2022 XRGrabInteractable \u306e selectEntered \u2192 Fire()\n" +
                    "\u2022 XRGrabInteractable \u306e selectExited  \u2192 Fire() \u307e\u305f\u306f Stop()\n" +
                    "\u2022 XRSimpleInteractable \u306e selectEntered \u2192 Fire()\n" +
                    "\u2022 Button.onClick \u2192 Fire()\n\n" +
                    "\u2018Batch Setup\u2019 (Window > Hapbeat > Batch Setup) \u3067\n" +
                    "\u8907\u6570\u30aa\u30d6\u30b8\u30a7\u30af\u30c8\u306b\u4e00\u62ec\u8ffd\u52a0\u3067\u304d\u307e\u3059\u3002",
                    MessageType.Info);

                EditorGUILayout.HelpBox(
                    "\u26a0 Poke \u30dc\u30bf\u30f3\u306e\u9023\u7d9a\u632f\u52d5 (\u62bc\u3057\u59cb\u3081\u306f\u5f31\u304f\u3001\u62bc\u3057\u5207\u308b\u3068\u5f37\u304f):\n\n" +
                    "  \u307e\u305a\u300c\u4f55\u306e\u30a4\u30d9\u30f3\u30c8\u304c\u5b9f\u969b\u306b\u767a\u706b\u3057\u3066\u3044\u308b\u304b\u300d\u3092\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002\n" +
                    "  \u2192 Hapbeat > Debug > Attach Event Logger to Selected \u3092\u5b9f\u884c\u3059\u308b\u3068\u3001\n" +
                    "    \u5bfe\u8c61 GameObject \u306b HapbeatEventLogger \u304c\u8ffd\u52a0\u3055\u308c\u3001\n" +
                    "    XRI Interactable \u306e\u5168\u30a4\u30d9\u30f3\u30c8 (hoverEntered, selectEntered \u7b49) \u304c\n" +
                    "    \u81ea\u52d5 wire \u3055\u308c\u3066 Console \u306b\u30bf\u30a4\u30e0\u30b9\u30bf\u30f3\u30d7\u4ed8\u304d\u3067\u51fa\u3066\u304d\u307e\u3059\u3002\n\n" +
                    "  \u305d\u306e\u30ed\u30b0\u3092\u898b\u306a\u304c\u3089\u3001\u8907\u6570 Interactor \u304c\u4ea4\u9014\u3059\u308b\u30bf\u30a4\u30df\u30f3\u30b0\u3084\n" +
                    "  first/last \u7cfb\u30a4\u30d9\u30f3\u30c8\u306e\u6319\u52d5\u3092\u628a\u63e1\u3057\u3066\u3001\u9069\u5207\u306a\u30a4\u30d9\u30f3\u30c8\u306b\n" +
                    "  Fire() / Stop() \u3092 wire \u3057\u307e\u3057\u3087\u3046\u3002\n\n" +
                    "  \u4e00\u822c\u7684\u306b\u306f:\n" +
                    "    \u2022 \u8907\u6570 Interactor \u306e\u904b\u642c\u3092\u7121\u8996\u3057\u305f\u3044 \u2192 firstHoverEntered / lastHoverExited\n" +
                    "    \u2022 \u4e00\u767a\u30af\u30ea\u30c3\u30af\u306e\u30d5\u30a3\u30fc\u30c9\u30d0\u30c3\u30af \u2192 selectEntered\n" +
                    "    \u2022 Ray \u76e3\u8996\u3057\u305f\u3044 \u2192 \u5404 Interactor \u306e Activated / Deactivated",
                    MessageType.Warning);
            }
        }
    }
}
#endif
