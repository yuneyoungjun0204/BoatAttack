using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 방어 환경 설정 (PushBlockSettings 패턴 기반)
    /// 씬에 하나만 존재하는 싱글톤 설정 클래스
    /// </summary>
    public class DefenseSettings : MonoBehaviour
    {
        [Header("Agent Movement")]
        [Tooltip("에이전트 이동 속도 (m/s)")]
        public float agentRunSpeed = 10f;
        
        [Tooltip("에이전트 회전 속도 (deg/s)")]
        public float agentRotationSpeed = 200f;
        
        [Header("Spawn Settings")]
        [Tooltip("스폰 영역 마진 배율 (PushBlockSettings 패턴)")]
        public float spawnAreaMarginMultiplier = 0.9f;
        
        [Header("Materials")]
        [Tooltip("목표 달성 시 그라운드 재질 (선택사항)")]
        public Material goalScoredMaterial;
        
        [Header("Debug")]
        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = false;
        
        // 싱글톤 인스턴스 (선택사항)
        private static DefenseSettings _instance;
        
        public static DefenseSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<DefenseSettings>();
                }
                return _instance;
            }
        }
        
        private void Awake()
        {
            // 싱글톤 설정 (중복 방지)
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"[DefenseSettings] 중복 인스턴스 발견! {gameObject.name} 제거됨.");
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
        }
    }
}
