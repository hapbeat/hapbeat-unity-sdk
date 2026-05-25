using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// HUD widget that lets the user pick which Hapbeat device target the
    /// script-driven calls (Z4 / Z5 / global hotkeys) should aim at.
    /// Z1-Z3 ignore this picker because their EventMap entries have a fixed
    /// target — that is intentional and demonstrates the "design-time target"
    /// pattern alongside this "runtime target" pattern.
    ///
    /// Picker values map to <c>HapbeatManager.Play</c> target strings:
    ///   <list type="bullet">
    ///     <item>Both / Broadcast → empty string (sent to all devices)</item>
    ///     <item>Neck → <c>*/pos_neck</c></item>
    ///     <item>Arm  → <c>*/pos_r_arm</c></item>
    ///   </list>
    /// </summary>
    public class TargetPickerUI : MonoBehaviour
    {
        public enum PickerTarget
        {
            Both = 0,
            Neck = 1,
            Arm = 2,
        }

        [SerializeField] private ShowcaseBridge _bridge;
        [SerializeField] private Toggle _bothToggle;
        [SerializeField] private Toggle _neckToggle;
        [SerializeField] private Toggle _armToggle;
        [SerializeField] private Text _statusText;

        [Header("Target strings")]
        [SerializeField] private string _bothTarget = "";
        [SerializeField] private string _neckTarget = "*/pos_neck";
        [SerializeField] private string _armTarget = "*/pos_r_arm";

        private PickerTarget _current = PickerTarget.Both;

        private void Start()
        {
            HookToggle(_bothToggle, PickerTarget.Both);
            HookToggle(_neckToggle, PickerTarget.Neck);
            HookToggle(_armToggle, PickerTarget.Arm);
            ApplyTarget(_current);
        }

        private void HookToggle(Toggle toggle, PickerTarget value)
        {
            if (toggle == null) return;
            toggle.onValueChanged.AddListener(on =>
            {
                if (on) ApplyTarget(value);
            });
        }

        private void ApplyTarget(PickerTarget value)
        {
            _current = value;
            string target = value switch
            {
                PickerTarget.Neck => _neckTarget,
                PickerTarget.Arm => _armTarget,
                _ => _bothTarget,
            };
            if (_bridge != null) _bridge.CurrentTarget = target;
            if (_statusText != null) _statusText.text = $"Target: {value} ({(string.IsNullOrEmpty(target) ? "broadcast" : target)})";
        }
    }
}
