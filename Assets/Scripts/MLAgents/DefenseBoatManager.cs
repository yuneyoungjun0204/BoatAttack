using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 방어 선박 훈련 환경 관리
    /// - 2대의 DefenseAgent 관리
    /// - 에피소드 시작/종료
    /// - 위치 리셋
    /// </summary>
    public class DefenseBoatManager : MonoBehaviour
    {
        [Header("Agents")]
        [Tooltip("방어 선박 1")]
        public DefenseAgent defenseAgent1;

        [Tooltip("방어 선박 2")]
        public DefenseAgent defenseAgent2;

        [Tooltip("공격 선박 (attack_boat 태그)")]
        public GameObject attackBoat;

        [Tooltip("Web 오브젝트")]
        public GameObject webObject;

        [Header("Spawn Positions")]
        [Tooltip("방어 선박 1 초기 위치")]
        public Vector3 defense1SpawnPos = new Vector3(-30f, 0.8f, 0f);

        [Tooltip("방어 선박 2 초기 위치")]
        public Vector3 defense2SpawnPos = new Vector3(30f, 0.8f, 0f);

        [Tooltip("공격 선박 초기 위치")]
        public Vector3 attackSpawnPos = new Vector3(0f, 0.8f, -50f);

        [Tooltip("Web 오브젝트 위치 (2대 중간)")]
        public Vector3 webSpawnPos = new Vector3(0f, 0.8f, 0f);

        [Header("Episode Settings")]
        [Tooltip("에피소드 최대 시간 (초)")]
        public float maxEpisodeTime = 120f;

        [Tooltip("타임아웃 시 페널티")]
        public float timeoutPenalty = -1f;

        [Header("Debug")]
        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = true;

        private float _episodeStartTime;
        private bool _episodeActive = false;
        private WebCollisionDetector _webDetector;

        private void Start()
        {
            // WebCollisionDetector 설정
            if (webObject != null)
            {
                _webDetector = webObject.GetComponent<WebCollisionDetector>();
                if (_webDetector == null)
                {
                    _webDetector = webObject.AddComponent<WebCollisionDetector>();
                }

                // DefenseAgent 참조 전달
                _webDetector.defenseAgent1 = defenseAgent1;
                _webDetector.defenseAgent2 = defenseAgent2;
            }

            // 에이전트 페어링
            if (defenseAgent1 != null && defenseAgent2 != null)
            {
                defenseAgent1.partnerAgent = defenseAgent2;
                defenseAgent2.partnerAgent = defenseAgent1;

                defenseAgent1.targetEnemy = attackBoat;
                defenseAgent2.targetEnemy = attackBoat;

                defenseAgent1.webObject = webObject;
                defenseAgent2.webObject = webObject;
            }

            // 첫 에피소드 시작
            StartNewEpisode();
        }

        private void Update()
        {
            if (!_episodeActive)
                return;

            // 타임아웃 체크
            float elapsedTime = Time.time - _episodeStartTime;
            if (elapsedTime >= maxEpisodeTime)
            {
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseBoatManager] 에피소드 타임아웃");
                }

                // 타임아웃 페널티
                if (defenseAgent1 != null)
                {
                    defenseAgent1.AddReward(timeoutPenalty);
                }
                if (defenseAgent2 != null)
                {
                    defenseAgent2.AddReward(timeoutPenalty);
                }

                EndCurrentEpisode();
            }
        }

        /// <summary>
        /// 새 에피소드 시작
        /// </summary>
        public void StartNewEpisode()
        {
            _episodeActive = true;
            _episodeStartTime = Time.time;

            // 위치 리셋
            ResetPositions();

            // WebDetector 리셋
            if (_webDetector != null)
            {
                _webDetector.ResetDetector();
            }

            if (enableDebugLog)
            {
                Debug.Log("[DefenseBoatManager] 새 에피소드 시작");
            }
        }

        /// <summary>
        /// 현재 에피소드 종료
        /// </summary>
        public void EndCurrentEpisode()
        {
            _episodeActive = false;

            // 에이전트 에피소드 종료
            if (defenseAgent1 != null)
            {
                defenseAgent1.EndEpisode();
            }
            if (defenseAgent2 != null)
            {
                defenseAgent2.EndEpisode();
            }

            // 새 에피소드 시작
            Invoke(nameof(StartNewEpisode), 0.5f);
        }

        /// <summary>
        /// 위치 리셋
        /// </summary>
        private void ResetPositions()
        {
            // DefenseAgent1 리셋
            if (defenseAgent1 != null && defenseAgent1.TryGetComponent<Rigidbody>(out var rb1))
            {
                rb1.velocity = Vector3.zero;
                rb1.angularVelocity = Vector3.zero;
                defenseAgent1.transform.position = defense1SpawnPos;
                defenseAgent1.transform.rotation = Quaternion.Euler(0, 0, 0);
            }

            // DefenseAgent2 리셋
            if (defenseAgent2 != null && defenseAgent2.TryGetComponent<Rigidbody>(out var rb2))
            {
                rb2.velocity = Vector3.zero;
                rb2.angularVelocity = Vector3.zero;
                defenseAgent2.transform.position = defense2SpawnPos;
                defenseAgent2.transform.rotation = Quaternion.Euler(0, 0, 0);
            }

            // AttackBoat 리셋
            if (attackBoat != null && attackBoat.TryGetComponent<Rigidbody>(out var rbAttack))
            {
                rbAttack.velocity = Vector3.zero;
                rbAttack.angularVelocity = Vector3.zero;
                attackBoat.transform.position = attackSpawnPos;
                attackBoat.transform.rotation = Quaternion.Euler(0, 0, 0);
            }

            // Web 위치 설정
            if (webObject != null)
            {
                webObject.transform.position = webSpawnPos;
            }
        }

        /// <summary>
        /// Gizmo 시각화
        /// </summary>
        private void OnDrawGizmos()
        {
            // 초기 스폰 위치 표시
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(defense1SpawnPos, 2f);
            Gizmos.DrawWireSphere(defense2SpawnPos, 2f);

            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(attackSpawnPos, 2f);

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(webSpawnPos, Vector3.one * 5f);

            // 방어선 연결
            Gizmos.color = Color.green;
            Gizmos.DrawLine(defense1SpawnPos, defense2SpawnPos);
        }
    }
}
