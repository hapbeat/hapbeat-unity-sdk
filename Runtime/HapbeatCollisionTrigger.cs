using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Fires a haptic event on physics collision or trigger events.
    /// Supports both 2D and 3D physics. Attach to the GameObject with the Collider.
    /// Non-invasive: runs alongside existing scripts without modifying them.
    ///
    /// Gain modes:
    /// - Fixed: uses the EventMap entry's gain as-is
    /// - VelocityScaled: maps collision velocity to gain via AnimationCurve
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Collision Trigger")]
    public class HapbeatCollisionTrigger : HapbeatTriggerBase
    {
        public enum PhysicsMode { Auto, Force2D, Force3D }
        public enum TriggerEvent
        {
            CollisionEnter,
            CollisionExit,
            TriggerEnter,
            TriggerExit
        }
        public enum GainMode { Fixed, VelocityScaled }

        [Header("Collision Settings")]
        [Tooltip("Auto detects 2D/3D from the Collider on this GameObject.")]
        [SerializeField]
        private PhysicsMode _physicsMode = PhysicsMode.Auto;

        [Tooltip("Which physics event to listen for.")]
        [SerializeField]
        private TriggerEvent _triggerEvent = TriggerEvent.CollisionEnter;

        [Tooltip("Only fire if the other object has this tag. Leave empty for any.")]
        [SerializeField]
        private string _tagFilter = "";

        [Tooltip("Only fire if the other object is on one of these layers.")]
        [SerializeField]
        private LayerMask _layerMask = ~0; // Everything

        [Header("Gain Scaling")]
        [Tooltip("Fixed: use EventMap gain. VelocityScaled: map collision velocity to gain.")]
        [SerializeField]
        private GainMode _gainMode = GainMode.Fixed;

        [Tooltip("Velocity below this threshold won't trigger. (VelocityScaled only)")]
        [SerializeField]
        private float _velocityThreshold = 0f;

        [Tooltip("Maps normalized velocity (0-1) to gain multiplier. X axis: velocity/maxVelocity, Y axis: gain multiplier. (VelocityScaled only)")]
        [SerializeField]
        private AnimationCurve _velocityCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Tooltip("Velocity at which gain reaches the curve's maximum. (VelocityScaled only)")]
        [SerializeField]
        private float _maxVelocity = 10f;

        // 2D callbacks
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionEnter && ShouldUse2D())
                TryFire(collision.gameObject, collision.relativeVelocity.magnitude);
        }

        private void OnCollisionExit2D(Collision2D collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionExit && ShouldUse2D())
                TryFire(collision.gameObject, collision.relativeVelocity.magnitude);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_triggerEvent == TriggerEvent.TriggerEnter && ShouldUse2D())
                TryFire(other.gameObject, 0f);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (_triggerEvent == TriggerEvent.TriggerExit && ShouldUse2D())
                TryFire(other.gameObject, 0f);
        }

        // 3D callbacks
        private void OnCollisionEnter(Collision collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionEnter && ShouldUse3D())
                TryFire(collision.gameObject, collision.relativeVelocity.magnitude);
        }

        private void OnCollisionExit(Collision collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionExit && ShouldUse3D())
                TryFire(collision.gameObject, collision.relativeVelocity.magnitude);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_triggerEvent == TriggerEvent.TriggerEnter && ShouldUse3D())
                TryFire(other.gameObject, 0f);
        }

        private void OnTriggerExit(Collider other)
        {
            if (_triggerEvent == TriggerEvent.TriggerExit && ShouldUse3D())
                TryFire(other.gameObject, 0f);
        }

        private void TryFire(GameObject other, float velocity)
        {
            // Tag filter
            if (!string.IsNullOrEmpty(_tagFilter) && !other.CompareTag(_tagFilter))
                return;

            // Layer filter
            if ((_layerMask & (1 << other.layer)) == 0)
                return;

            if (_gainMode == GainMode.VelocityScaled)
                FireWithVelocity(velocity);
            else
                FireHaptic();
        }

        private void FireWithVelocity(float velocity)
        {
            if (!_triggerEnabled) return;
            if (velocity < _velocityThreshold) return;
            if (_eventMap == null) return;

            var entry = ResolveEntry();
            if (entry == null) return;

            // Cooldown check
            if (_cooldown > 0f && Time.unscaledTime - _lastFireTimeInternal < _cooldown)
                return;
            _lastFireTimeInternal = Time.unscaledTime;

            // Map velocity through curve, then apply manifest.intensity.
            // Device is a pure executor (req.gain only) so the sender must
            // multiply in intensity. Per-trigger gain multiplier composes on
            // top so the same EventMap entry can drive different intensities
            // across collision objects.
            float normalizedVel = Mathf.Clamp01(velocity / _maxVelocity);
            float curveValue = _velocityCurve.Evaluate(normalizedVel);
            float intensity = entry.CachedManifestIntensity;
            float intensityFactor = intensity < 0f ? 1f : intensity;
            float scaledGain = curveValue * entry.gain * intensityFactor * _gainMultiplier;

            var mgr = HapbeatManager.Instance;
            if (mgr == null) return;

            string baseName = string.IsNullOrEmpty(entry.displayName) ? entry.GetSummary() : entry.displayName;
            string label = $"{gameObject.name}: {baseName}";
            string target = entry.HasTarget ? entry.target : null;

            // entry.mode に応じて Command / StreamClip を呼び分け。
            // 旧実装は常に Manager.Play (Command) を叩いていたため、
            // StreamClip mode の entry (Tutorial の pin_hit 等) では
            // device 側で event_id 未登録 → silently 無音となるバグ有り。
            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        if (_verboseLog)
                            Debug.LogWarning($"[Hapbeat] VelocityScaled Command but eventId empty on '{label}'", this);
                        return;
                    }
                    mgr.Play(entry.eventId, scaledGain, label, target);
                    break;

                case HapticMode.StreamClip:
                    if (entry.streamClip == null)
                    {
                        if (_verboseLog)
                            Debug.LogWarning($"[Hapbeat] VelocityScaled StreamClip but streamClip null on '{label}'", this);
                        return;
                    }
                    mgr.StreamAudioClip(entry.streamClip, scaledGain, target, loop: false);
                    if (_verboseLog)
                        Debug.Log($"[Hapbeat] ♪ {label} velocity-scaled stream (gain={scaledGain:F2})", this);
                    break;
            }
        }

        private float _lastFireTimeInternal = float.NegativeInfinity;

        private bool ShouldUse2D()
        {
            if (_physicsMode == PhysicsMode.Force2D) return true;
            if (_physicsMode == PhysicsMode.Force3D) return false;
            return GetComponent<Collider2D>() != null;
        }

        private bool ShouldUse3D()
        {
            if (_physicsMode == PhysicsMode.Force3D) return true;
            if (_physicsMode == PhysicsMode.Force2D) return false;
            return GetComponent<Collider>() != null;
        }
    }
}
