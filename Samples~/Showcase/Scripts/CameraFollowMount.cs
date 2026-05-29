using UnityEngine;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// GameObject を Player Camera 直下の固定相対位置に常駐させる
    /// (FPS の銃や釣り竿のような "常に手元にある" オブジェクト用)。
    /// Reparent ではなく LateUpdate で position/rotation を毎フレーム
    /// コピーするため、GameObject の親 (Z*_root 等) を変えずに済む =
    /// ZoneSwitcher の SetActive で自然に出し入れできる。
    /// </summary>
    public class CameraFollowMount : MonoBehaviour
    {
        [Tooltip("追従先 Camera。未割当の場合は Camera.main を使用。")]
        [SerializeField] private Camera _targetCamera;

        [Tooltip("Camera ローカル空間でのオフセット位置 (FPS gun like = (0.3, -0.15, 0.5))。")]
        [SerializeField] private Vector3 _localPosition = new Vector3(0.3f, -0.15f, 0.5f);

        [Tooltip("Camera ローカル空間でのオイラー角オフセット (degree)。")]
        [SerializeField] private Vector3 _localEulerAngles = new Vector3(-15f, 0f, 0f);

        private Camera _cachedCamera;

        private void LateUpdate()
        {
            var cam = _targetCamera != null ? _targetCamera : (_cachedCamera != null ? _cachedCamera : (_cachedCamera = Camera.main));
            if (cam == null) return;
            var ct = cam.transform;
            transform.SetPositionAndRotation(
                ct.TransformPoint(_localPosition),
                ct.rotation * Quaternion.Euler(_localEulerAngles));
        }
    }
}
