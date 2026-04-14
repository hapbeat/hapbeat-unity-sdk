using System.Collections.Generic;
using UnityEngine;

namespace Hapbeat.Samples
{
    /// <summary>
    /// ドローン防衛。プレイヤーの周囲からドローンをスポーンし、
    /// 弾を発射してプレイヤーに被弾させる（受動フィードバック体験用）。
    /// 被弾の触覚は弾 Prefab の HapbeatCollisionTrigger が処理する。
    /// </summary>
    public class DroneDefense : MonoBehaviour
    {
        [Header("Spawn")]
        [SerializeField] private GameObject _dronePrefab;
        [SerializeField] private float _spawnRadius = 15f;
        [SerializeField] private float _spawnHeight = 3f;
        [SerializeField] private float _spawnInterval = 4f;
        [SerializeField] private int _maxDrones = 3;

        [Header("Drone Behavior")]
        [SerializeField] private float _droneSpeed = 2f;
        [SerializeField] private float _fireRange = 8f;
        [SerializeField] private float _fireInterval = 2f;

        [Header("Projectile")]
        [SerializeField] private GameObject _projectilePrefab;
        [SerializeField] private float _projectileSpeed = 8f;
        [SerializeField] private float _projectileLifetime = 5f;

        private Transform _player;
        private float _nextSpawnTime;
        private readonly List<DroneState> _drones = new();

        private class DroneState
        {
            public GameObject obj;
            public float nextFireTime;
        }

        private void Start()
        {
            _player = Camera.main != null ? Camera.main.transform : null;
            _nextSpawnTime = Time.time + 2f; // 初回は少し待つ
        }

        private void Update()
        {
            if (_player == null) return;

            // スポーン
            if (Time.time >= _nextSpawnTime && _drones.Count < _maxDrones)
            {
                SpawnDrone();
                _nextSpawnTime = Time.time + _spawnInterval;
            }

            // ドローン更新
            for (int i = _drones.Count - 1; i >= 0; i--)
            {
                var drone = _drones[i];
                if (drone.obj == null) { _drones.RemoveAt(i); continue; }

                // プレイヤーに向かって移動
                Vector3 dir = (_player.position - drone.obj.transform.position).normalized;
                drone.obj.transform.position += dir * _droneSpeed * Time.deltaTime;
                drone.obj.transform.LookAt(_player);

                // 射程内なら射撃
                float dist = Vector3.Distance(drone.obj.transform.position, _player.position);
                if (dist < _fireRange && Time.time >= drone.nextFireTime)
                {
                    FireProjectile(drone.obj.transform);
                    drone.nextFireTime = Time.time + _fireInterval;
                }
            }
        }

        private void SpawnDrone()
        {
            if (_dronePrefab == null) return;

            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * _spawnRadius;
            offset.y = _spawnHeight;

            Vector3 pos = _player.position + offset;
            var obj = Instantiate(_dronePrefab, pos, Quaternion.identity, transform);
            _drones.Add(new DroneState { obj = obj, nextFireTime = Time.time + _fireInterval });
        }

        private void FireProjectile(Transform droneTransform)
        {
            if (_projectilePrefab == null || _player == null) return;

            Vector3 dir = (_player.position - droneTransform.position).normalized;
            var proj = Instantiate(_projectilePrefab, droneTransform.position, Quaternion.LookRotation(dir), transform);

            var rb = proj.GetComponent<Rigidbody>();
            if (rb != null) rb.linearVelocity = dir * _projectileSpeed;

            Destroy(proj, _projectileLifetime);
        }

        private void OnDisable()
        {
            // クリーンアップ
            foreach (var drone in _drones)
            {
                if (drone.obj != null) Destroy(drone.obj);
            }
            _drones.Clear();
        }
    }
}
