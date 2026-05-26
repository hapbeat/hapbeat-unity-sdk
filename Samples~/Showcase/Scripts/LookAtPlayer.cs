using UnityEngine;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// この GO をプレイヤー (デフォルトは Camera.main) の方向に向ける。
    /// 標準では水平面 (Yaw) のみ回転するので、板状ターゲットが傾かない。
    /// </summary>
    public class LookAtPlayer : MonoBehaviour
    {
        [Tooltip("見る対象。未割当なら Camera.main を使用。")]
        [SerializeField] private Transform _target;
        [Tooltip("Y 軸 (水平面) のみ回転する。OFF にすると 3 軸自由に回転 (ピッチも追従)。")]
        [SerializeField] private bool _yawOnly = true;
        [Tooltip("OFF にすると追従しない (デバッグ用)。")]
        [SerializeField] private bool _enabled = true;

        private void Start()
        {
            if (_target == null && Camera.main != null)
                _target = Camera.main.transform;
        }

        private void LateUpdate()
        {
            if (!_enabled || _target == null) return;

            Vector3 dir = _target.position - transform.position;
            if (_yawOnly) dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;

            transform.rotation = Quaternion.LookRotation(dir);
        }
    }
}
