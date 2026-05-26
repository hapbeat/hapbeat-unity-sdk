using UnityEngine;
using UnityEngine.InputSystem;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z3 Fishing: 釣り竿の先端から釣り糸が常時垂れている。LMB hold で
    /// 床の object が宙吊りになり、重力で自由落下しつつ、糸が張った瞬間
    /// (= rod tip との距離が <see cref="_maxLineLength"/> に達した瞬間) のみ
    /// rod tip の動きが慣性として object に伝わる。
    ///
    /// 糸が slack (緩んだ) 状態: object は重力のみで動き、rod tip 動かしても
    /// 影響を受けない (= 糸はたわむ表現)。糸が taut (張った) 状態: rod tip
    /// 速度が object に加算され、振り子的に挙動。
    ///
    /// Haptic は <see cref="HapbeatSequenceTrigger"/> (loop) と
    /// <see cref="HapbeatParameterBinding"/> (motion → StreamGain) が担当。
    /// </summary>
    public class FishingController : MonoBehaviour
    {
        [Header("Haptic")]
        [SerializeField] private HapbeatSequenceTrigger _sequence;

        [Header("Object")]
        [Tooltip("吊り上げる物体の Rigidbody。useGravity = true 推奨。")]
        [SerializeField] private Rigidbody _object;
        [Tooltip("Detach 時に戻る床上の rest pose。")]
        [SerializeField] private Transform _restPose;

        [Header("Line physics")]
        [Tooltip("釣り糸の上端 (= rod 先端)。")]
        [SerializeField] private Transform _rodTip;
        [Tooltip("糸の長さ (m)。これより遠ざかると糸が張って rod tip の動きが伝わる。")]
        [Range(0.3f, 5f)] [SerializeField] private float _maxLineLength = 1.2f;
        [Tooltip("糸が張った状態で rod tip 速度が object に伝わる係数 (1.0=完全伝達)。\n小さくするほど暴れない (推奨 0.2〜0.4)。")]
        [Range(0f, 1.5f)] [SerializeField] private float _rodInertiaFactor = 0.25f;
        [Tooltip("rod tip 速度の上限 (m/s)。視点振りで rod tip が瞬間的に超高速移動すると object が暴れるため cap。")]
        [Range(0.5f, 10f)] [SerializeField] private float _maxTransferSpeed = 2f;
        [Tooltip("Attach 中の物体の linear damping (高いと揺れが早く減衰)。")]
        [Range(0f, 5f)] [SerializeField] private float _attachedLinearDamping = 1.5f;
        [Tooltip("Attach 中の物体の angular damping。")]
        [Range(0f, 5f)] [SerializeField] private float _attachedAngularDamping = 1f;

        [Header("Line visual")]
        [SerializeField] private LineRenderer _line;

        [SerializeField] private bool _verboseLog;

        private bool _attached;
        private Vector3 _prevRodTipPos;
        private float _origLinearDamping;
        private float _origAngularDamping;

        private void Update()
        {
            HandleInput();
            UpdateLineVisual();
        }

        private void FixedUpdate()
        {
            if (!_attached || _rodTip == null || _object == null) return;

            // 1. Rod tip の速度を計算 (これ自体は常に取得)
            Vector3 rodTipDelta = _rodTip.position - _prevRodTipPos;
            _prevRodTipPos = _rodTip.position;

            // 2. 距離チェック
            Vector3 toObject = _object.position - _rodTip.position;
            float dist = toObject.magnitude;

            if (dist < _maxLineLength)
            {
                // 糸が slack: 物理任せ (重力で自由落下)。何もしない。
                return;
            }

            // 糸が taut: rod tip 速度を慣性として object に伝える + 距離制約
            Vector3 dir = toObject / Mathf.Max(dist, 1e-4f);

            if (Time.fixedDeltaTime > 0f)
            {
                Vector3 rodTipVel = rodTipDelta / Time.fixedDeltaTime;
                // 視点振りで rod tip が一瞬高速移動した時の暴れ防止: cap
                float speed = rodTipVel.magnitude;
                if (speed > _maxTransferSpeed)
                    rodTipVel = rodTipVel * (_maxTransferSpeed / speed);
                _object.linearVelocity += rodTipVel * _rodInertiaFactor;
            }

            // 距離スナップを soft lerp に (hard snap だと跳ねる)
            Vector3 snapPos = _rodTip.position + dir * _maxLineLength;
            _object.position = Vector3.Lerp(_object.position, snapPos, 0.5f);

            // 外向き (radial) 速度成分を damping (完全 kill だと反対方向に揺れすぎる)
            float radialSpeed = Vector3.Dot(_object.linearVelocity, dir);
            if (radialSpeed > 0f)
                _object.linearVelocity -= dir * radialSpeed * 0.7f;
        }

        private void HandleInput()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (Cursor.lockState != CursorLockMode.Locked) return;

            if (mouse.leftButton.wasPressedThisFrame && !_attached)
            {
                if (_verboseLog) Debug.Log("[Fishing] LMB down → Attach");
                Attach();
            }
            else if (mouse.leftButton.wasReleasedThisFrame && _attached)
            {
                if (_verboseLog) Debug.Log("[Fishing] LMB up → Detach");
                Detach();
            }
        }

        private void UpdateLineVisual()
        {
            if (_line == null || _rodTip == null) return;
            _line.positionCount = 2;
            _line.SetPosition(0, _rodTip.position);
            Vector3 endpoint = _attached && _object != null
                ? _object.transform.position
                : (_rodTip.position + Vector3.down * _maxLineLength);
            _line.SetPosition(1, endpoint);
        }

        private void Attach()
        {
            if (_object == null) { Debug.LogWarning("[Fishing] _object 未割当"); return; }
            _attached = true;
            _object.isKinematic = false;
            _object.useGravity = true;
            _object.linearVelocity = Vector3.zero;
            _object.angularVelocity = Vector3.zero;
            // Attach 中だけ damping を上げて高周波振動を抑える。
            _origLinearDamping = _object.linearDamping;
            _origAngularDamping = _object.angularDamping;
            _object.linearDamping = _attachedLinearDamping;
            _object.angularDamping = _attachedAngularDamping;
            // 床から離して line 終端あたりに持ち上げる (= 即座に taut state へ)
            if (_rodTip != null)
                _object.position = _rodTip.position + Vector3.down * _maxLineLength;
            _prevRodTipPos = _rodTip != null ? _rodTip.position : transform.position;
            _sequence?.Fire();
        }

        private void Detach()
        {
            if (_object == null) return;
            _attached = false;
            _object.linearDamping = _origLinearDamping;
            _object.angularDamping = _origAngularDamping;
            _sequence?.Stop();
            // Rest pose にスナップ (落下中継したい場合はコメントアウト)
            if (_restPose != null)
            {
                _object.linearVelocity = Vector3.zero;
                _object.angularVelocity = Vector3.zero;
                _object.position = _restPose.position;
                _object.rotation = _restPose.rotation;
            }
        }
    }
}
