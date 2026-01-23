using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// Web 오브젝트에 부착하여 attack_boat와의 충돌을 감지
    /// 충돌 시 DefenseAgent들에게 보상 신호 전송
    /// </summary>
    public class WebCollisionDetector : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("충돌 감지할 적군 태그")]
        public string enemyTag = "attack_boat";

        [Tooltip("충돌 시 알림받을 DefenseAgent 1")]
        public DefenseAgent defenseAgent1;

        [Tooltip("충돌 시 알림받을 DefenseAgent 2")]
        public DefenseAgent defenseAgent2;

        [Tooltip("충돌 효과 Prefab (선택적)")]
        public GameObject captureEffectPrefab;

        [Tooltip("효과 크기")]
        public float effectScale = 1f;

        [Header("Debug")]
        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = true;

        private bool _hasTriggered = false;

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

                // DefenseAgent들에게 포획 성공 알림
                if (defenseAgent1 != null)
                {
                    defenseAgent1.OnEnemyCaptured();
                }

                if (defenseAgent2 != null)
                {
                    defenseAgent2.OnEnemyCaptured();
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
