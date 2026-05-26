using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// 後続 bool 更新のモード。
    /// </summary>
    public enum DeferredBoolMode
    {
        SetTrue,
        SetFalse,
        Toggle,
    }

    /// <summary>
    /// trigger 発火の 1 フレーム後に行う Animator bool 更新の 1 件。
    /// 「lock toggle 時に IsLocked を反転する」のような後続処理を表現する。
    /// </summary>
    [Serializable]
    public class DeferredBoolUpdate
    {
        [Tooltip("更新する Animator bool parameter 名 (空ならスキップ)。")]
        public string parameter = "";

        [Tooltip("更新方法。Toggle は実行時の現在値を反転。")]
        public DeferredBoolMode mode = DeferredBoolMode.Toggle;
    }

    /// <summary>
    /// 「1 つの input key → 1 つの Animator trigger 発火 (+ optional 後続 bool 更新リスト)」を
    /// 表現する。Animator transition 側の condition で必要な precondition チェックを行うため、
    /// script-side では gate を持たない (二重設定回避)。
    /// </summary>
    [Serializable]
    public class DoorInputBinding
    {
        [Tooltip("Inspector 表示用ラベル (動作には影響しない)。")]
        public string label = "";

        [Tooltip("発火させる input key。")]
        public Key key = Key.None;

        [Tooltip("空なら任意の state で発火。非空ならその Animator state にいる時のみ " +
                 "trigger を立てる (queue 残留を防ぐ用途)。bool gate ではなく state gate " +
                 "なので Animator transition condition とは二重にならない。")]
        public string requiredAnimatorState = "";

        [Tooltip("発火する Animator trigger parameter 名。")]
        public string triggerParameter = "";

        [Tooltip("trigger 発火の 1 フレーム後に更新する Animator bool 群。" +
                 "Animator transition が pre-action 値で condition 評価できるよう遅延する。")]
        public List<DeferredBoolUpdate> deferredBools = new List<DeferredBoolUpdate>();
    }

    /// <summary>
    /// Z2 Swing Door: <b>data-driven</b> な input → Animator parameter bridge。
    ///
    /// <para>script に if/else を書かず、Inspector の Bindings リストで全部設定する。
    /// 「分岐するかどうか」は Animator の transition condition が担当 (二重設定回避)。</para>
    ///
    /// <para>Z2 標準 binding (Populate Z2 Defaults ボタンで生成):</para>
    /// <list type="bullet">
    ///   <item>F → DoorAction trigger (deferred bool なし)</item>
    ///   <item>G → Slam trigger (deferred bool なし)</item>
    ///   <item>L → LockToggle trigger (deferred bool: IsLocked → Toggle)</item>
    /// </list>
    ///
    /// <para>Animator が transition condition で行先を決定:</para>
    /// <list type="bullet">
    ///   <item>Closed → Open : DoorAction, IsClosed=true, IsLocked=false</item>
    ///   <item>Closed → LockedRattle : DoorAction, IsClosed=true, IsLocked=true</item>
    ///   <item>Open → Closed : DoorAction, IsClosed=false</item>
    ///   <item>Open → Slam : Slam</item>
    ///   <item>Closed → LockEngage : LockToggle, IsLocked=false</item>
    ///   <item>Closed → LockRelease : LockToggle, IsLocked=true</item>
    /// </list>
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class DoorController : MonoBehaviour
    {
        [Header("Input → Animator Bindings")]
        [SerializeField] private List<DoorInputBinding> _bindings = new List<DoorInputBinding>();

        [Header("State Mirror (auto-managed)")]
        [Tooltip("ドアが現在 Closed か否かを mirror する Animator bool。" +
                 "_openStateName の Animator state にいる時のみ false。空なら mirror 機能を無効化。")]
        [SerializeField] private string _isClosedParameter = "IsClosed";

        [Tooltip("IsClosed=false にする Animator state 名 (通常は \"Open\")。")]
        [SerializeField] private string _openStateName = "Open";

        [Header("Input Filtering")]
        [Tooltip("Input を受け付ける Animator state 名リスト。\n" +
                 "現在の Animator state がここに含まれる時のみ binding を評価する。\n" +
                 "空なら任意の state で受付 (filter 無効)。\n" +
                 "Z2 推奨: Closed, Open (transient な Slam/LockEngage/LockRelease/LockedRattle 中は入力無視)。")]
        [SerializeField] private List<string> _inputAcceptingStates = new List<string>();

        // 蝶番オフセットは Animation clip 内で Rotation + Position を同時 animate することで
        // 表現する設計に統一 (script の hinge 補正は廃止)。
        [SerializeField] private bool _verboseLog = true;

        private Animator _animator;

        public IReadOnlyList<DoorInputBinding> Bindings => _bindings;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            if (!string.IsNullOrEmpty(_isClosedParameter))
                _animator.SetBool(_isClosedParameter, true);
            if (_verboseLog)
                Debug.Log($"[Door] Awake: {_bindings.Count} binding(s) configured.");
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            // Input filter: 「現在 state」 + 「transition 中の destination state」両方が
            // accept list に含まれる時のみ binding を評価する。
            // GetCurrentAnimatorStateInfo は transition 中も source を返すので、
            // 例えば Closed → Slam 遷移中は current=Closed (accept list 内) のまま
            // destination の Slam (list 外) を見逃してしまう。両方チェックで防ぐ。
            if (_inputAcceptingStates != null && _inputAcceptingStates.Count > 0)
            {
                bool accepting = IsAnimatorInAcceptedState();
                if (!accepting)
                {
                    if (_verboseLog)
                    {
                        // どれか key が押された瞬間だけ log (毎フレーム log 防止)
                        foreach (var b in _bindings)
                        {
                            if (b != null && b.key != Key.None && kb[b.key].wasPressedThisFrame)
                            {
                                Debug.Log($"[Door] input ignored: animator state not in accepted list (binding='{b.label}')");
                                break;
                            }
                        }
                    }
                    return;
                }
            }

            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                if (b == null || b.key == Key.None) continue;
                if (!kb[b.key].wasPressedThisFrame) continue;

                // Fire trigger
                if (!string.IsNullOrEmpty(b.triggerParameter))
                    _animator.SetTrigger(b.triggerParameter);

                // Schedule each deferred bool update
                if (b.deferredBools != null)
                {
                    foreach (var d in b.deferredBools)
                    {
                        if (d == null || string.IsNullOrEmpty(d.parameter)) continue;
                        StartCoroutine(UpdateDeferredBool(d.parameter, d.mode));
                    }
                }

                if (_verboseLog)
                {
                    int deferCount = b.deferredBools != null ? b.deferredBools.Count : 0;
                    Debug.Log($"[Door] '{b.label}' fired: trigger={b.triggerParameter}, " +
                              $"deferred=[{deferCount} update(s)]");
                }
            }
        }

        /// <summary>
        /// 「Animator が静止状態 (settled) かつ accept list 内の state にいる」時のみ true を返す。
        /// settled の定義:
        /// (1) transition 中ではない (= 状態遷移中の動きがない)
        /// (2) 現在 state の clip が再生し終わっている (= clip 内モーションが動いていない)
        ///     non-loop clip: normalizedTime >= 1.0 で終了とみなす
        ///     loop clip: 終了しないので「clip 再生中」と扱って reject
        /// (3) state 名が accept list に含まれる (transient state 除外)
        ///
        /// → Open clip が 0.3 秒かけて 0°→90° を描画中の間も F が ignored される。
        /// </summary>
        private bool IsAnimatorInAcceptedState()
        {
            // (1) transition 中なら reject (Closed→Open / Open→Closed 等の移行中も含む)
            if (_animator.IsInTransition(0)) return false;

            var current = _animator.GetCurrentAnimatorStateInfo(0);

            // (2) clip 再生中なら reject
            //   - non-loop: normalizedTime < 1 = まだ再生中
            //   - loop: 永遠に終わらないので「動いている扱い」で reject
            if (current.loop) return false;
            if (current.normalizedTime < 1.0f) return false;

            // (3) state 名が accept list 内か
            if (!StateNameInList(current)) return false;

            return true;
        }

        private bool StateNameInList(AnimatorStateInfo info)
        {
            for (int i = 0; i < _inputAcceptingStates.Count; i++)
            {
                var name = _inputAcceptingStates[i];
                if (!string.IsNullOrEmpty(name) && info.IsName(name))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// IsClosed mirror。Animator current state が Open なら false、それ以外は true。
        /// </summary>
        private void LateUpdate()
        {
            if (string.IsNullOrEmpty(_isClosedParameter)) return;
            var info = _animator.GetCurrentAnimatorStateInfo(0);
            bool inOpen = !string.IsNullOrEmpty(_openStateName) && info.IsName(_openStateName);
            bool desired = !inOpen;
            if (_animator.GetBool(_isClosedParameter) != desired)
                _animator.SetBool(_isClosedParameter, desired);
        }

        /// <summary>
        /// 1 フレーム待ってから Animator bool を更新する。
        /// Toggle モードの場合は実行時の現在値を反転 (script 内 cache ではなく Animator 現在値が真の出典)。
        /// </summary>
        private IEnumerator UpdateDeferredBool(string param, DeferredBoolMode mode)
        {
            yield return null;
            if (string.IsNullOrEmpty(param) || _animator == null) yield break;
            bool current = _animator.GetBool(param);
            bool newValue = mode switch
            {
                DeferredBoolMode.SetTrue => true,
                DeferredBoolMode.SetFalse => false,
                DeferredBoolMode.Toggle => !current,
                _ => current,
            };
            _animator.SetBool(param, newValue);
            if (_verboseLog)
                Debug.Log($"[Door] deferred bool: {param}={current} → {newValue} ({mode})");
        }

        // -------------------------------------------------------------------
        //  Editor-only helpers
        // -------------------------------------------------------------------
#if UNITY_EDITOR
        public void EditorPopulateZ2Defaults()
        {
            _inputAcceptingStates = new List<string> { "Closed", "Open" };
            _bindings = new List<DoorInputBinding>
            {
                new DoorInputBinding {
                    label = "Open / Close door",
                    key = Key.F,
                    triggerParameter = "DoorAction",
                },
                new DoorInputBinding {
                    label = "Slam door",
                    key = Key.G,
                    triggerParameter = "Slam",
                },
                new DoorInputBinding {
                    label = "Lock toggle",
                    key = Key.L,
                    triggerParameter = "LockToggle",
                    deferredBools = new List<DeferredBoolUpdate>
                    {
                        new DeferredBoolUpdate { parameter = "IsLocked", mode = DeferredBoolMode.Toggle },
                    },
                },
            };
        }
#endif
    }
}
