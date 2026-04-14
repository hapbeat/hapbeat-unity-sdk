using System.Collections.Generic;
using UnityEngine;
using Hapbeat;

namespace Hapbeat.Samples
{
    public enum Zone { A_Active, B_Passive, C_UI }

    /// <summary>
    /// Player Demo 全体管理。ゾーン別の触覚 ON/OFF を制御する。
    /// 各ゾーンの Trigger コンポーネントを登録し、ON/OFF 時に enabled を切り替える。
    /// </summary>
    public class DemoManager : MonoBehaviour
    {
        public static DemoManager Instance { get; private set; }

        [SerializeField] private bool _globalHapticsOn = true;

        private readonly Dictionary<Zone, bool> _zoneStates = new()
        {
            { Zone.A_Active, true },
            { Zone.B_Passive, true },
            { Zone.C_UI, true }
        };

        private readonly Dictionary<Zone, List<MonoBehaviour>> _zoneTriggers = new()
        {
            { Zone.A_Active, new List<MonoBehaviour>() },
            { Zone.B_Passive, new List<MonoBehaviour>() },
            { Zone.C_UI, new List<MonoBehaviour>() }
        };

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>ゾーンに Trigger コンポーネントを登録する。各スクリプトの Awake から呼ぶ。</summary>
        public void Register(Zone zone, MonoBehaviour trigger)
        {
            if (_zoneTriggers.ContainsKey(zone))
                _zoneTriggers[zone].Add(trigger);
        }

        public void SetGlobalHaptics(bool on)
        {
            _globalHapticsOn = on;
            foreach (var zone in _zoneTriggers.Keys)
                ApplyZoneState(zone);
        }

        public void SetZoneHaptics(Zone zone, bool on)
        {
            _zoneStates[zone] = on;
            ApplyZoneState(zone);
        }

        public bool IsHapticsEnabled(Zone zone)
        {
            return _globalHapticsOn && _zoneStates[zone];
        }

        /// <summary>ON/OFF を考慮して再生する。Trigger を使わず直接呼ぶ場合用。</summary>
        public void PlayIfEnabled(Zone zone, string eventId, float gain = 1f)
        {
            if (!IsHapticsEnabled(zone)) return;
            HapbeatManager.Instance?.Play(eventId, gain);
        }

        private void ApplyZoneState(Zone zone)
        {
            bool enabled = IsHapticsEnabled(zone);
            foreach (var trigger in _zoneTriggers[zone])
            {
                if (trigger != null) trigger.enabled = enabled;
            }
        }
    }
}
