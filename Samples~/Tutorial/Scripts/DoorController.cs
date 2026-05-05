using UnityEngine;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z2 Swing Door: F key toggles an Animator bool to drive Open/Close states.
    /// Haptics are wired via HapbeatAnimatorTrigger components (manual setup
    /// in Inspector) listening for state Enter on Open / Closed.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class DoorController : MonoBehaviour
    {
        [SerializeField] private string _openParameter = "IsOpen";
        [SerializeField] private KeyCode _toggleKey = KeyCode.F;

        private Animator _animator;
        private bool _open;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey))
            {
                _open = !_open;
                _animator.SetBool(_openParameter, _open);
            }
        }
    }
}
