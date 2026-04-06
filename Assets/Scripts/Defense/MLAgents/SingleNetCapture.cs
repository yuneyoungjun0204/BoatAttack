using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 개별 선박에 부착하는 소형 포획 존.
    /// Disarm(그물 이탈) 이후 활성화되어 선박 전방의 구체 범위 안에 들어온 적군을 포획.
    /// WebCollisionDetector와 동일한 방식으로 DefenseEnvController.OnEnemyHitSingleNet() 호출.
    /// </summary>
    public class SingleNetCapture : MonoBehaviour
    {
        [Header("Capture Zone")]
        [Tooltip("포획 구체 반경 (m)")]
        public float captureRadius = 15f;

        [Tooltip("선박 전방 오프셋 (m) — 0이면 선박 중심")]
        public float forwardOffset = 10f;

        [Tooltip("포획 감지할 적군 레이어 마스크")]
        public LayerMask captureLayerMask = Physics.DefaultRaycastLayers;

        [Tooltip("포획 감지할 적군 태그")]
        public string enemyTag = "attack_boat";

        [Header("Cooldown")]
        [Tooltip("연속 포획 쿨다운 (초) — 같은 적 중복 처리 방지")]
        public float captureCooldown = 1f;

        [Header("Debug")]
        public bool showGizmo = true;

        // 참조
        private DefenseEnvController _envController;
        private DefenseAgent _agent;

        // 상태
        private bool _isActive = false;
        private float _lastCaptureTime = -999f;

        // 마지막 포획된 적 (쿨다운용)
        private GameObject _lastCapturedEnemy;

        private void Awake()
        {
            _agent = GetComponent<DefenseAgent>();
        }

        private void Start()
        {
            // DefenseEnvController 자동 탐색 (멀티 환경 호환)
            if (_envController == null)
            {
                Transform root = transform;
                while (root.parent != null) root = root.parent;
                _envController = root.GetComponentInChildren<DefenseEnvController>();
            }
        }

        /// <summary>
        /// 외부 참조 주입 (LaunchZoneManager or DefenseAgent에서 호출)
        /// </summary>
        public void Init(DefenseEnvController envController)
        {
            _envController = envController;
        }

        /// <summary>
        /// Disarm 이후 호출 — 소형 포획 존 활성화
        /// </summary>
        public void Activate()
        {
            _isActive = true;
            _lastCapturedEnemy = null;
            _lastCaptureTime = -999f;
        }

        /// <summary>
        /// 에피소드 리셋 or 비활성화 시 호출
        /// </summary>
        public void Deactivate()
        {
            _isActive = false;
            _lastCapturedEnemy = null;
        }

        public bool IsActive => _isActive;

        private void FixedUpdate()
        {
            if (!_isActive || _envController == null) return;

            // 쿨다운 체크
            if (Time.time - _lastCaptureTime < captureCooldown) return;

            // 선박 전방 오프셋 위치에서 OverlapSphere
            Vector3 captureCenter = transform.position + transform.forward * forwardOffset;
            Collider[] hits = Physics.OverlapSphere(captureCenter, captureRadius, captureLayerMask);

            foreach (var col in hits)
            {
                if (!col.CompareTag(enemyTag)) continue;

                GameObject enemy = col.gameObject;
                if (enemy == _lastCapturedEnemy) continue;
                if (_envController.IsEnemyNeutralized(enemy)) continue;

                // 포획 처리
                _lastCapturedEnemy = enemy;
                _lastCaptureTime = Time.time;
                _envController.OnEnemyHitSingleNet(enemy, _agent);
                break; // 한 FixedUpdate에 1개만 처리
            }
        }

        private void OnDrawGizmos()
        {
            if (!showGizmo) return;

            Vector3 captureCenter = transform.position + transform.forward * forwardOffset;

            if (_isActive)
            {
                Gizmos.color = new Color(1f, 0.4f, 0f, 0.35f);
                Gizmos.DrawSphere(captureCenter, captureRadius);
                Gizmos.color = new Color(1f, 0.4f, 0f, 0.9f);
                Gizmos.DrawWireSphere(captureCenter, captureRadius);
            }
            else
            {
                Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.2f);
                Gizmos.DrawWireSphere(captureCenter, captureRadius);
            }
        }
    }
}
