using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// Web 오브젝트에 부착하여 attack_boat와의 충돌을 감지
    /// 충돌 시 DefenseEnvController를 통해 그룹 보상으로 처리
    /// </summary>
    public class WebCollisionDetector : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("충돌 감지할 적군 태그")]
        public string enemyTag = "attack_boat";

        [Tooltip("환경 컨트롤러 (충돌 시 알림)")]
        public DefenseEnvController envController;

        [Tooltip("충돌 효과 Prefab (선택적)")]
        public GameObject captureEffectPrefab;

        [Tooltip("효과 크기")]
        public float effectScale = 1f;

        [Header("Debug")]
        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = true;

        private bool _hasTriggered = false;

        private void Start()
        {
            // 환경 컨트롤러 자동 찾기
            if (envController == null)
            {
                envController = FindObjectOfType<DefenseEnvController>();
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            // 이미 트리거된 경우 무시
            if (_hasTriggered)
                return;

            // attack_boat 태그 확인
            if (other.CompareTag(enemyTag))
            {
                _hasTriggered = true;

                if (enableDebugLog)
                {
                    Debug.Log($"[WebCollisionDetector] 적군 포획! {other.gameObject.name}");
                }

                // 적의 위치 저장
                Vector3 enemyPosition = other.transform.position;

                // DefenseEnvController를 통해 그룹 보상으로 처리
                if (envController != null)
                {
                    envController.OnEnemyCaptured(enemyPosition);
                }
                else
                {
                    // Fallback: DefenseAgent들에게 직접 알림 (호환성)
                    DefenseAgent[] agents = FindObjectsOfType<DefenseAgent>();
                    foreach (var agent in agents)
                    {
                        if (agent != null)
                        {
                            agent.OnEnemyCaptured(enemyPosition);
                        }
                    }
                }

                // 효과 생성
                if (captureEffectPrefab != null)
                {
                    GameObject effect = Instantiate(captureEffectPrefab, transform.position, Quaternion.identity);
                    effect.transform.localScale = Vector3.one * effectScale;
                    Destroy(effect, 3f);
                }
            }
        }

        /// <summary>
        /// 에피소드 리셋 시 호출
        /// </summary>
        public void ResetDetector()
        {
            _hasTriggered = false;
        }

        private void OnDrawGizmos()
        {
            // Web 범위 시각화
            Gizmos.color = _hasTriggered ? Color.green : Color.yellow;
            Gizmos.DrawWireCube(transform.position, transform.localScale);
        }
    }
}
