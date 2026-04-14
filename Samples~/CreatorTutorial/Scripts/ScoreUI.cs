using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples
{
    /// <summary>
    /// スコア表示 UI。Target から加算され、Canvas の Text に反映する。
    /// </summary>
    public class ScoreUI : MonoBehaviour
    {
        [SerializeField] private Text _scoreText;
        [SerializeField] private Text _timerText;
        [SerializeField] private float _gameDuration = 60f;

        private int _score;
        private float _remainingTime;
        private bool _gameActive = true;

        private void Start()
        {
            _remainingTime = _gameDuration;
            UpdateUI();
        }

        private void Update()
        {
            if (!_gameActive) return;

            _remainingTime -= Time.deltaTime;
            if (_remainingTime <= 0f)
            {
                _remainingTime = 0f;
                _gameActive = false;
            }
            UpdateUI();
        }

        public void AddScore(int value)
        {
            if (!_gameActive) return;
            _score += value;
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (_scoreText != null) _scoreText.text = $"Score: {_score}";
            if (_timerText != null) _timerText.text = $"Time: {_remainingTime:F0}s";
        }
    }
}
