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
            }
        }
    }
}
#endif
