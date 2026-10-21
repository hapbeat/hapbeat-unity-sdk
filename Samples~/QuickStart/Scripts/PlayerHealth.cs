using UnityEngine;

namespace Hapbeat.Samples.QuickStart
{
    /// <summary>
    /// QuickStart (B): fire a Hapbeat haptic from your own script with one line.
    /// The haptic itself (clip, gain, target) lives in the Event Map entry that the
    /// <see cref="HapbeatUnityEventTrigger"/> points at, so you tune it there,
    /// not in code.
    /// </summary>
    public class PlayerHealth : MonoBehaviour
    {
        [SerializeField] private int _health = 100;
        [SerializeField] private int _damagePerHit = 10;

        [Tooltip("A Hapbeat UnityEvent Trigger whose Event is the haptic to play on damage.")]
        [SerializeField] private HapbeatUnityEventTrigger _damageHaptic;

        public int Health => _health;

        public void TakeDamage(int amount)
        {
            _health = Mathf.Max(0, _health - amount);
            _damageHaptic.Fire();   // <- the one line that plays the haptic
            Debug.Log($"[QuickStart] {name} took {amount} damage (health {_health})", this);
        }

        // Anything with a Rigidbody hitting the player counts as a hit in this sample.
        private void OnCollisionEnter(Collision collision)
        {
            TakeDamage(_damagePerHit);
        }
    }
}
