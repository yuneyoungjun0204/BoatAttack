using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 모선과 적군 선박의 충돌을 감지하는 컴포넌트
    /// 모선 GameObject에 부착하여 사용
    /// </summary>
    public class MotherShipCollisionDetector : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("충돌 감지할 적군 태그")]
        public string enemyTag = "attack_boat";
        
        [Tooltip("환경 컨트롤러 (충돌 시 알림)")]
        public DefenseEnvController envController;
        
        [Header("Debug")]
        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = true;
        
        private void Start()
        {
            // 환경 컨트롤러 자동 찾기
            if (envController == null)
            {
                envController = FindObjectOfType<DefenseEnvController>();
                if (envController == null)
                {
                    Debug.LogWarning("[MotherShipCollisionDetector] DefenseEnvController를 찾을 수 없습니다!");
                }
            }
        }
        
        /// <summary>
        /// 충돌 감지 (Collision)
        /// </summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (collision.gameObject.CompareTag(enemyTag))
            {
                if (enableDebugLog)
                {
                    Debug.Log($"[MotherShipCollisionDetector] 모선 충돌 감지: {collision.gameObject.name}");
                }
                
                if (envController != null)
                {
                    envController.OnMotherShipCollision(collision.gameObject);
                }
            }
        }
        
        /// <summary>
        /// 충돌 감지 (Trigger)
        /// </summary>
        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag(enemyTag))
            {
                if (enableDebugLog)
                {
                    Debug.Log($"[MotherShipCollisionDetector] 모선 충돌 감지 (Trigger): {other.gameObject.name}");
                }
                
                if (envController != null)
                {
                    envController.OnMotherShipCollision(other.gameObject);
                }
            }
        }
    }
}
