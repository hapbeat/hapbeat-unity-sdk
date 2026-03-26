using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Fires a haptic event on physics collision or trigger events.
    /// Supports both 2D and 3D physics. Attach to the GameObject with the Collider.
    /// Non-invasive: runs alongside existing scripts without modifying them.
    /// </summary>
    [AddComponentMenu("Hapbeat/Collision Trigger")]
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

        [Tooltip("Only fire if the other object is on one of these layers. Set to Everything for no filter.")]
        [SerializeField]
        private LayerMask _layerMask = ~0; // Everything

        // 2D callbacks
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionEnter && ShouldUse2D())
                TryFire(collision.gameObject);
        }

        private void OnCollisionExit2D(Collision2D collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionExit && ShouldUse2D())
                TryFire(collision.gameObject);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_triggerEvent == TriggerEvent.TriggerEnter && ShouldUse2D())
                TryFire(other.gameObject);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (_triggerEvent == TriggerEvent.TriggerExit && ShouldUse2D())
                TryFire(other.gameObject);
        }

        // 3D callbacks
        private void OnCollisionEnter(Collision collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionEnter && ShouldUse3D())
                TryFire(collision.gameObject);
        }

        private void OnCollisionExit(Collision collision)
        {
            if (_triggerEvent == TriggerEvent.CollisionExit && ShouldUse3D())
                TryFire(collision.gameObject);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_triggerEvent == TriggerEvent.TriggerEnter && ShouldUse3D())
                TryFire(other.gameObject);
        }

        private void OnTriggerExit(Collider other)
        {
            if (_triggerEvent == TriggerEvent.TriggerExit && ShouldUse3D())
                TryFire(other.gameObject);
        }

        private void TryFire(GameObject other)
        {
            // Tag filter
            if (!string.IsNullOrEmpty(_tagFilter) && !other.CompareTag(_tagFilter))
                return;

            // Layer filter
            if ((_layerMask & (1 << other.layer)) == 0)
                return;

            FireHaptic();
        }

        private bool ShouldUse2D()
        {
            if (_physicsMode == PhysicsMode.Force2D) return true;
            if (_physicsMode == PhysicsMode.Force3D) return false;
            // Auto: use 2D if this GO has a 2D collider
            return GetComponent<Collider2D>() != null;
        }

        private bool ShouldUse3D()
        {
            if (_physicsMode == PhysicsMode.Force3D) return true;
            if (_physicsMode == PhysicsMode.Force2D) return false;
            // Auto: use 3D if this GO has a 3D collider
            return GetComponent<Collider>() != null;
        }
    }
}
