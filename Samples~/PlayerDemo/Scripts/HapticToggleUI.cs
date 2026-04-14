using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples
{
    /// <summary>
    /// 触覚 ON/OFF パネル制御。WorldSpace Canvas に配置し、
    /// Global + ゾーン別の ON/OFF トグルを操作する。
    /// </summary>
    public class HapticToggleUI : MonoBehaviour
    {
        [Header("Toggle Buttons")]
        [SerializeField] private Toggle _globalToggle;
        [SerializeField] private Toggle _zoneAToggle;
        [SerializeField] private Toggle _zoneBToggle;
        [SerializeField] private Toggle _zoneCToggle;

        private bool[] _savedStates = { true, true, true };

        private void Start()
        {
            if (_globalToggle != null)
                _globalToggle.onValueChanged.AddListener(OnGlobalToggle);
            if (_zoneAToggle != null)
                _zoneAToggle.onValueChanged.AddListener(v => OnZoneToggle(Zone.A_Active, v));
            if (_zoneBToggle != null)
                _zoneBToggle.onValueChanged.AddListener(v => OnZoneToggle(Zone.B_Passive, v));
            if (_zoneCToggle != null)
                _zoneCToggle.onValueChanged.AddListener(v => OnZoneToggle(Zone.C_UI, v));
        }

        private void OnGlobalToggle(bool on)
        {
            if (DemoManager.Instance == null) return;

            if (!on)
            {
                // 個別設定を保存してから全 OFF
                _savedStates[0] = _zoneAToggle != null && _zoneAToggle.isOn;
                _savedStates[1] = _zoneBToggle != null && _zoneBToggle.isOn;
                _savedStates[2] = _zoneCToggle != null && _zoneCToggle.isOn;
            }

            DemoManager.Instance.SetGlobalHaptics(on);

            // Global ON 復帰時に個別設定を復元
            if (on)
            {
                if (_zoneAToggle != null) _zoneAToggle.isOn = _savedStates[0];
                if (_zoneBToggle != null) _zoneBToggle.isOn = _savedStates[1];
                if (_zoneCToggle != null) _zoneCToggle.isOn = _savedStates[2];
            }
        }

        private void OnZoneToggle(Zone zone, bool on)
        {
            DemoManager.Instance?.SetZoneHaptics(zone, on);
        }
    }
}
