using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// 1〜5 キーで Tutorial のゾーンを 1 つだけ表示するスイッチャ。
    /// 各 Zone は scene 上の固定位置に配置し、切替時は Player を
    /// 各 Zone 固有の <see cref="ZoneEntry.playerSpawn"/> へテレポートする。
    /// </summary>
    public class ZoneSwitcher : MonoBehaviour
    {
        [Serializable]
        public class ZoneEntry
        {
            [Tooltip("HUD 表示用ラベル (例: \"Bowling\")。")]
            public string label;

            [Tooltip("ゾーンの root GameObject。SetActive で表示制御 (位置は動かさない)。")]
            public GameObject root;

            [Tooltip("このゾーン表示時に Player をテレポートする位置 & 向き。")]
            public Transform playerSpawn;

            [TextArea(2, 8)]
            [Tooltip("このゾーン専用のキーコマンド説明 (HUD)。各行 \"KEY | DESC\" 形式。")]
            public string commands;

            [Tooltip("このゾーン表示時にマウスカーソルを unlock する (UI 操作用)。他ゾーンへ抜ける時 lock に戻る。")]
            public bool unlockCursorOnEnter;
        }

        [SerializeField] private List<ZoneEntry> _zones = new List<ZoneEntry>();

        [Header("Player teleport")]
        [Tooltip("Zone 切替時にテレポートする対象 (通常は Player)。")]
        [SerializeField] private Transform _player;

        [Tooltip("起動時に表示するゾーン (1 始まり)。")]
        [Range(1, 9)]
        [SerializeField] private int _initialZone = 1;

        [Header("Runtime zone placement")]
        [Tooltip("ON にすると Play 中、表示する zone root を _activeZonePosition へ自動移動。" +
                 "Edit 時は zone root を scattered に配置して個別編集 → Play 時は中央に集約、という運用に。")]
        [SerializeField] private bool _centerActiveZoneOnEnter = true;
        [Tooltip("表示中の zone root を移動させる world 位置。通常は (0, 0, 0)。")]
        [SerializeField] private Vector3 _activeZonePosition = Vector3.zero;

        [SerializeField] private bool _verboseLog = true;

        [Serializable] public class ZoneChangedEvent : UnityEngine.Events.UnityEvent<int> { }
        [Tooltip("ゾーン切替時に発火 (引数: 1 始まりの zone index)。")]
        public ZoneChangedEvent OnZoneChanged = new ZoneChangedEvent();

        private static readonly Key[] s_digitKeys =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
            Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9,
        };

        private int _currentZone;

        public int CurrentZone => _currentZone;
        public ZoneEntry CurrentEntry =>
            (_currentZone >= 1 && _currentZone <= _zones.Count) ? _zones[_currentZone - 1] : null;
        public IReadOnlyList<ZoneEntry> Zones => _zones;

        private void Start()
        {
            int idx = Mathf.Clamp(_initialZone, 1, Mathf.Max(1, _zones.Count));
            ShowZone(idx);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            int count = Mathf.Min(_zones.Count, s_digitKeys.Length);
            for (int i = 0; i < count; i++)
            {
                if (kb[s_digitKeys[i]].wasPressedThisFrame)
                {
                    ShowZone(i + 1);
                    return;
                }
            }
        }

        /// <summary>1 始まりのゾーン番号を表示する。</summary>
        public void ShowZone(int oneBasedIndex)
        {
            if (_zones.Count == 0) return;
            int idx = Mathf.Clamp(oneBasedIndex, 1, _zones.Count);

            for (int i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                if (z == null || z.root == null) continue;
                bool active = (i + 1 == idx);
                z.root.SetActive(active);
                // Active zone を center に移動 (位置のみ、rotation は edit-time のまま)
                // Play 終了時 Unity は scene を元に戻すので edit 時の scattered 位置は保持される。
                if (active && _centerActiveZoneOnEnter)
                    z.root.transform.position = _activeZonePosition;
            }

            var entry = _zones[idx - 1];
            var spawn = entry?.playerSpawn;
            if (spawn != null && _player != null)
            {
                TeleportPlayer(spawn);
            }
            else if (spawn == null && _verboseLog)
            {
                Debug.LogWarning($"[Zone] Zone {idx} の PlayerSpawn が未設定 (Player は移動しません)。");
            }

            // Cursor lock 状態をゾーン設定に合わせて切替 (UI 操作が必要な zone のみ unlock)。
            if (entry != null)
            {
                if (entry.unlockCursorOnEnter)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }

            _currentZone = idx;
            if (_verboseLog)
            {
                var label = _zones[idx - 1]?.label ?? "(no label)";
                Debug.Log($"[Zone] ShowZone({idx}) → {label}");
            }
            OnZoneChanged?.Invoke(idx);
        }

        private void TeleportPlayer(Transform spawn)
        {
            // CharacterController は SetPositionAndRotation を上書きするので
            // 一時的に disable してから移動する。
            var cc = _player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            _player.SetPositionAndRotation(spawn.position, spawn.rotation);
            if (cc != null) cc.enabled = true;

            // SimpleFPSController の pitch リセット (カメラ Y 角度のみ spawn 追従)。
            var fps = _player.GetComponent<SimpleFPSController>();
            if (fps != null) fps.ResetLook();
        }
    }
}
