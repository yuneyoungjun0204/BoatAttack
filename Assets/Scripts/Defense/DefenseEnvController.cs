using UnityEngine;
using Unity.MLAgents;

namespace BoatAttack
{
    /// <summary>
    /// 방어 환경 컨트롤러
    /// 그룹 보상을 분배하고 환경을 관리
    /// </summary>
    public class DefenseEnvController : MonoBehaviour
    {
        [Header("Agents")]
        [Tooltip("방어 에이전트 1")]
        public DefenseAgent defenseAgent1;
        
        [Tooltip("방어 에이전트 2")]
        public DefenseAgent defenseAgent2;
        
        [Header("Components")]
        [Tooltip("SimpleMultiAgentGroup (Unity Scene에 추가 필요)")]
        public SimpleMultiAgentGroup m_AgentGroup;
        
        [Tooltip("보상 계산기")]
        public DefenseRewardCalculator rewardCalculator;
        
        [Header("Settings")]
        [Tooltip("보상 계산 주기 (프레임 단위, 1 = 매 프레임)")]
        public int rewardCalculationInterval = 1;
        
        [Tooltip("모선 참조")]
        public GameObject motherShip;
        
        [Tooltip("적군 선박들")]
        public GameObject[] enemyShips = new GameObject[5];
        
        [Tooltip("Web 오브젝트")]
        public GameObject webObject;
        
        [Header("Mission Rewards")]
        [Tooltip("포획 성공 보상")]
        public float captureReward = 1.0f;
        
        [Tooltip("포획 위치 보너스 (최대)")]
        public float captureCenterBonus = 0.3f;
        
        [Tooltip("포획 위치 보너스 최대 거리")]
        public float captureCenterMaxDistance = 30f;
        
        [Tooltip("모선 방어 성공 보상")]
        public float motherShipDefenseReward = 0.5f;
        
        [Tooltip("모선 방어 반경 (m)")]
        public float motherShipDefenseRadius = 1000f;
        
        [Tooltip("방어선 침범 패널티")]
        public float boundaryBreachPenalty = -0.1f;
        
        [Tooltip("2km 경계선")]
        public float boundary2km = 2000f;
        
        [Tooltip("1km 경계선")]
        public float boundary1km = 1000f;
        
        [Tooltip("모선 충돌 패널티 (Game Over)")]
        public float motherShipCollisionPenalty = -1.75f;
        
        [Header("Collision Detection")]
        [Tooltip("충돌 패널티 (아군-아군, 아군-모선)")]
        public float collisionPenalty = -1.0f;
        
        [Header("Debug")]
        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = false;
        
        private int _frameCount = 0;
        private bool _episodeActive = false;
        private bool _enemyEnteredDefenseZone = false;
        private bool _boundary2kmBreached = false;
        private bool _boundary1kmBreached = false;
        
        private void Start()
        {
            // SimpleMultiAgentGroup 초기화
            if (m_AgentGroup == null)
            {
                m_AgentGroup = GetComponent<SimpleMultiAgentGroup>();
                if (m_AgentGroup == null)
                {
                    Debug.LogError("[DefenseEnvController] SimpleMultiAgentGroup을 찾을 수 없습니다!");
                }
            }
            
            // 에이전트 등록
            if (m_AgentGroup != null)
            {
                if (defenseAgent1 != null)
                    m_AgentGroup.RegisterAgent(defenseAgent1);
                if (defenseAgent2 != null)
                    m_AgentGroup.RegisterAgent(defenseAgent2);
            }
            
            // RewardCalculator 초기화
            if (rewardCalculator == null)
            {
                rewardCalculator = GetComponent<DefenseRewardCalculator>();
                if (rewardCalculator == null)
                {
                    Debug.LogError("[DefenseEnvController] DefenseRewardCalculator를 찾을 수 없습니다!");
                }
            }
            
            // 모선 찾기
            if (motherShip == null)
            {
                motherShip = GameObject.FindGameObjectWithTag("MotherShip");
            }
        }
        
        private void Update()
        {
            if (!_episodeActive)
                return;
            
            _frameCount++;
            
            // 보상 계산 주기 확인
            if (_frameCount % rewardCalculationInterval != 0)
                return;
            
            if (defenseAgent1 == null || defenseAgent2 == null || 
                rewardCalculator == null || m_AgentGroup == null)
                return;
            
            // 1. 관측: 두 에이전트의 상태 수집
            var agent1State = rewardCalculator.GetAgentState(defenseAgent1);
            var agent2State = rewardCalculator.GetAgentState(defenseAgent2);
            
            // 2. 계산: 협동 기동 보상
            float cooperativeReward = rewardCalculator.CalculateCooperativeRewards(
                agent1State, agent2State);
            
            // 3. 계산: 전술 기동 보상
            float tacticalReward = rewardCalculator.CalculateTacticalRewards(
                agent1State, agent2State, enemyShips, webObject);
            
            // 4. 계산: 안전 및 제약 페널티
            float safetyPenalty = rewardCalculator.CalculateSafetyPenalties(
                agent1State, agent2State);
            
            // 5. 계산: 방어선 침범 체크
            float boundaryPenalty = CheckBoundaryBreach();
            
            // 6. 부여: 그룹 전체에 보상 분배
            float totalReward = cooperativeReward + tacticalReward + safetyPenalty + boundaryPenalty;
            if (Mathf.Abs(totalReward) > 0.0001f)
            {
                m_AgentGroup.AddGroupReward(totalReward);
            }
        }
        
        /// <summary>
        /// 에피소드 시작
        /// </summary>
        public void OnEpisodeBegin()
        {
            _episodeActive = true;
            _frameCount = 0;
            _enemyEnteredDefenseZone = false;
            _boundary2kmBreached = false;
            _boundary1kmBreached = false;
            
            if (rewardCalculator != null)
            {
                rewardCalculator.Reset();
            }
            
            if (enableDebugLog)
            {
                Debug.Log("[DefenseEnvController] 에피소드 시작");
            }
        }
        
        /// <summary>
        /// 적 포획 성공 처리
        /// </summary>
        public void OnEnemyCaptured(Vector3 enemyPosition)
        {
            if (!_episodeActive || m_AgentGroup == null)
                return;
            
            // 기본 포획 보상
            float totalReward = captureReward;
            
            // 위치 보너스 계산 (그물 중심에 가까울수록)
            if (defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 netCenter = (defenseAgent1.transform.position + 
                                   defenseAgent2.transform.position) / 2f;
                float distanceToCenter = Vector3.Distance(enemyPosition, netCenter);
                
                if (distanceToCenter <= captureCenterMaxDistance)
                {
                    float centerBonus = captureCenterBonus * 
                        (1f - distanceToCenter / captureCenterMaxDistance);
                    totalReward += centerBonus;
                }
            }
            
            // 그룹 보상 분배
            m_AgentGroup.AddGroupReward(totalReward);
            
            // 에피소드 종료
            _episodeActive = false;
            m_AgentGroup.EndGroupEpisode();
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 적 포획 성공! 보상: {totalReward:F3}");
            }
        }
        
        /// <summary>
        /// 모선 충돌 처리 (Game Over)
        /// </summary>
        public void OnMotherShipCollision(GameObject enemyBoat)
        {
            if (!_episodeActive || m_AgentGroup == null)
                return;
            
            // 큰 패널티 부여 (그룹 보상)
            m_AgentGroup.AddGroupReward(motherShipCollisionPenalty);
            
            // 즉시 에피소드 종료
            _episodeActive = false;
            m_AgentGroup.EndGroupEpisode();
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 모선 충돌! Game Over. 패널티: {motherShipCollisionPenalty}");
            }
        }
        
        /// <summary>
        /// 아군 충돌 처리
        /// </summary>
        public void OnFriendlyCollision()
        {
            if (!_episodeActive || m_AgentGroup == null)
                return;
            
            // 충돌 패널티 부여
            m_AgentGroup.AddGroupReward(collisionPenalty);
            
            // 에피소드 종료
            _episodeActive = false;
            m_AgentGroup.EndGroupEpisode();
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 아군 충돌! 패널티: {collisionPenalty}");
            }
        }
        
        /// <summary>
        /// 방어선 침범 체크
        /// </summary>
        private float CheckBoundaryBreach()
        {
            if (motherShip == null || enemyShips == null)
                return 0f;
            
            float totalPenalty = 0f;
            
            foreach (var enemy in enemyShips)
            {
                if (enemy == null) continue;
                
                float distanceToMotherShip = Vector3.Distance(
                    enemy.transform.position, motherShip.transform.position);
                
                // 2km 경계선 체크
                if (!_boundary2kmBreached && distanceToMotherShip <= boundary2km)
                {
                    _boundary2kmBreached = true;
                    totalPenalty += boundaryBreachPenalty;
                }
                
                // 1km 경계선 체크
                if (!_boundary1kmBreached && distanceToMotherShip <= boundary1km)
                {
                    _boundary1kmBreached = true;
                    totalPenalty += boundaryBreachPenalty;
                }
                
                // 모선 방어 존 진입 체크
                if (distanceToMotherShip <= motherShipDefenseRadius)
                {
                    _enemyEnteredDefenseZone = true;
                }
            }
            
            return totalPenalty;
        }
        
        /// <summary>
        /// 에피소드 종료 시 모선 방어 성공 체크
        /// </summary>
        public void OnEpisodeEnd()
        {
            if (!_episodeActive || m_AgentGroup == null)
                return;
            
            // 모선 방어 성공 체크 (적이 방어 존에 진입하지 않았을 때)
            if (!_enemyEnteredDefenseZone && motherShip != null)
            {
                m_AgentGroup.AddGroupReward(motherShipDefenseReward);
                
                if (enableDebugLog)
                {
                    Debug.Log($"[DefenseEnvController] 모선 방어 성공! 보상: {motherShipDefenseReward}");
                }
            }
            
            _episodeActive = false;
        }
    }
}
