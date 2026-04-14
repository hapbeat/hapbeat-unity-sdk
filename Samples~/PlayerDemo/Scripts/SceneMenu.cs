using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Hapbeat.Samples
{
    /// <summary>
    /// ゾーン選択メニュー。手元の WorldSpace Canvas に配置し、
    /// ボタンでシーンを即座に切り替える。
    /// 各ゾーンシーンにも配置して「Hub に戻る」「他のゾーンに移動」を提供する。
    /// </summary>
    public class SceneMenu : MonoBehaviour
    {
        [Header("Scene Names")]
        [SerializeField] private string _hubScene = "PlayerDemoHub";
        [SerializeField] private string _zoneAScene = "PlayerDemoZoneA";
        [SerializeField] private string _zoneBScene = "PlayerDemoZoneB";
        [SerializeField] private string _zoneCScene = "PlayerDemoZoneC";
        [SerializeField] private string _zoneDScene = "PlayerDemoZoneD";

        [Header("UI (optional - auto-generated if null)")]
        [SerializeField] private Button _hubButton;
        [SerializeField] private Button _zoneAButton;
        [SerializeField] private Button _zoneBButton;
        [SerializeField] private Button _zoneCButton;
        [SerializeField] private Button _zoneDButton;

        private void Start()
        {
            if (_hubButton != null) _hubButton.onClick.AddListener(GoToHub);
            if (_zoneAButton != null) _zoneAButton.onClick.AddListener(GoToZoneA);
            if (_zoneBButton != null) _zoneBButton.onClick.AddListener(GoToZoneB);
            if (_zoneCButton != null) _zoneCButton.onClick.AddListener(GoToZoneC);
            if (_zoneDButton != null) _zoneDButton.onClick.AddListener(GoToZoneD);
        }

        public void GoToHub() => LoadScene(_hubScene);
        public void GoToZoneA() => LoadScene(_zoneAScene);
        public void GoToZoneB() => LoadScene(_zoneBScene);
        public void GoToZoneC() => LoadScene(_zoneCScene);
        public void GoToZoneD() => LoadScene(_zoneDScene);

        private void LoadScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            SceneManager.LoadScene(sceneName);
        }
    }
}
