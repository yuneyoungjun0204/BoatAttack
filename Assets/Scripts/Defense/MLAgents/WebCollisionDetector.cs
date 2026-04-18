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

        /// <summary>
        /// 부모 DynamicWeb 참조 (포획 시 쌍 식별용)
        /// </summary>
        [HideInInspector] public DynamicWeb parentDynamicWeb;

        private bool _hasTriggered = false;
        public bool HasTriggered => _hasTriggered;

        private void Start()
        {
            // 환경 컨트롤러 자동 찾기 (멀티 환경 호환)
            if (envController == null)
            {
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                envController = envRoot.GetComponentInChildren<DefenseEnvController>();
            }

            // 부모 DynamicWeb 자동 찾기
            if (parentDynamicWeb == null)
            {
                parentDynamicWeb = GetComponent<DynamicWeb>();
                if (parentDynamicWeb == null)
                    parentDynamicWeb = GetComponentInParent<DynamicWeb>();
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

                // 적군 선박 참조
                GameObject enemyBoat = other.gameObject;

                // DefenseEnvController를 통해 포획 처리 (적 무력화 + 쌍 풀 반환)
                if (envController != null)
                {
                    envController.OnEnemyHitWeb(enemyBoat, parentDynamicWeb);
                }
                else
                {
                    if (enableDebugLog)
                    {
                        Debug.LogWarning("[WebCollisionDetector] DefenseEnvController를 찾을 수 없습니다!");
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
            // 아군 선박이 타 쌍의 그물에 걸린 경우 → 걸린 아군 쌍 비활성화
            var hitAgent = other.GetComponent<DefenseAgent>();
            if (hitAgent == null)
                hitAgent = other.GetComponentInParent<DefenseAgent>();
            if (hitAgent != null && envController != null)
            {
                // 자기 쌍 그물에 자기가 걸리는 경우는 무시
                bool isSamePair = parentDynamicWeb != null
                    && (parentDynamicWeb.defenseShip1 == hitAgent.transform
                     || parentDynamicWeb.defenseShip2 == hitAgent.transform);
                if (!isSamePair)
                    envController.OnAllyHitWeb(hitAgent.gameObject, parentDynamicWeb);
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
