using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Pong (Ping 応答) の RTT を HUD テキストに反映するためだけの小さな
    /// MonoBehaviour。Q / P キー入力自体は <c>HapbeatKeyDispatcher</c> +
    /// <c>HapbeatActionHelper</c> 経由で宣言的に wiring する設計に変えたため、
    /// このスクリプトは Update での入力監視を持たない。
    /// </summary>
    public class GlobalHotkeys : MonoBehaviour
    {
        [SerializeField] private Text _pingResultText;
        [SerializeField] private bool _verboseLog = true;

        private bool _pongSubscribed;
        // ユーザーが P キーで明示的に Ping を送った時のみ console に出すフラグ。
        // HapbeatManager は keep-alive で定期 ping を打つので、毎回ログを
        // 出すと console が埋まる。Pong 1 回分だけ消費される one-shot flag。
        private bool _userInitiatedPing;

        private void Start() => TrySubscribe();

        private void Update()
        {
            // HapbeatManager が遅れて初期化されるケースに対応 (Subscribe 失敗時にリトライ)。
            if (!_pongSubscribed) TrySubscribe();
        }

        private void OnDestroy()
        {
            if (_pongSubscribed && HapbeatManager.Instance != null)
                HapbeatManager.Instance.OnPong -= HandlePong;
            _pongSubscribed = false;
        }

        private void TrySubscribe()
        {
            if (_pongSubscribed) return;
            var mgr = HapbeatManager.Instance;
            if (mgr == null) return;
            mgr.OnPong += HandlePong;
            _pongSubscribed = true;
            if (_verboseLog) Debug.Log("[Hotkey] OnPong subscribed.");
        }

        private void HandlePong(long rttUs)
        {
            // 自動 ping (keep-alive) の場合 console 出さない。HUD は常に更新。
            if (_userInitiatedPing)
            {
                if (_verboseLog) Debug.Log($"[Hotkey] Pong received: {rttUs / 1000f:F1} ms");
                _userInitiatedPing = false;
            }
            if (_pingResultText != null)
            {
                // 慣例通り "Ping" = 往復時間 (RTT)。
                _pingResultText.text = $"Ping: {rttUs / 1000f:F1} ms";
            }
        }

        /// <summary>UnityEvent から呼ばれて即時表示するため (Pong 待ち間の placeholder)。
        /// 同時に「次の Pong は user-initiated」フラグを立てて console ログ許可。</summary>
        public void NotifyPingSent()
        {
            _userInitiatedPing = true;
            if (_pingResultText != null) _pingResultText.text = "Ping: ...";
        }
    }
}
