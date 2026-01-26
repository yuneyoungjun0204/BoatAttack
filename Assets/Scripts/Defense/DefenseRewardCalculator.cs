using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace BoatAttack
{
    /// <summary>
    /// 방어 에이전트들의 보상을 중앙에서 계산하는 클래스
    /// 그룹 보상을 계산하여 DefenseEnvController에 전달
    /// </summary>
    public class DefenseRewardCalculator : MonoBehaviour
    {
        [Header("Cooperative Rewards")]
        [Tooltip("헤딩 동기화 보상")]
        public float headingSyncReward = 0.0002f;
        
        [Tooltip("헤딩 동기화 허용 범위 (도)")]
        public float headingSyncTolerance = 15f;
        
        [Tooltip("속도 동기화 보상")]
        public float speedSyncReward = 0.0002f;
        
        [Tooltip("속도 동기화 허용 범위 (m/s)")]
        public float speedSyncTolerance = 2f;
        
        [Tooltip("간격 유지 보상")]
        public float distanceMaintainReward = 0.0002f;
        
        [Tooltip("최적 거리 (m)")]
        public float optimalDistance = 50f;
        
        [Tooltip("거리 허용 범위 (m)")]
        public float distanceTolerance = 5f;
        
        [Header("Net Tension")]
        [Tooltip("그물 장력 보상")]
        public float netTensionReward = 0.0005f;
        
        [Tooltip("그물 최대 길이 (m)")]
        public float netMaxLength = 50f;
        
        [Tooltip("최적 거리 비율 (최소)")]
        [Range(0.8f, 1.0f)]
        public float netOptimalMinRatio = 0.90f;
        
        [Tooltip("최적 거리 비율 (최대)")]
        [Range(0.8f, 1.0f)]
        public float netOptimalMaxRatio = 0.95f;
        
        [Header("Tactical Rewards")]
        [Tooltip("수직 차단 보상")]
        public float perpendicularInterceptReward = 0.0005f;
        
        [Tooltip("수직 차단 각도 허용 범위 (도)")]
        public float perpendicularAngleTolerance = 15f;
        
        [Tooltip("추적 이득 보상")]
        public float trackingGainReward = 0.0002f;
        
        [Header("Safety Penalties")]
        [Tooltip("충돌 패널티 (아군-아군, 아군-모선)")]
        public float collisionPenalty = -1.0f;
        
        [Tooltip("대형 붕괴 패널티")]
        public float formationBreakPenalty = -0.05f;
        
        [Tooltip("최대 대형 거리 (m)")]
        public float maxFormationDistance = 100f;
        
        [Tooltip("최대 대형 각도 차이 (도)")]
        public float maxFormationAngleDiff = 90f;
        
        [Header("Time Penalty")]
        [Tooltip("시간 패널티 (매 프레임)")]
        public float timePenalty = -0.001f;
        
        // 이전 스텝의 적-그물 거리 (추적 이득 계산용)
        private float _lastEnemyToWebDistance = float.MaxValue;
        
        /// <summary>
        /// 에이전트 상태 구조체
        /// </summary>
        public struct AgentState
        {
            public Vector3 position;
            public float heading;
            public float speed;
            public Rigidbody rb;
            public Transform transform;
        }
        
        /// <summary>
        /// 협동 기동 보상 계산
        /// </summary>
        public float CalculateCooperativeRewards(AgentState agent1, AgentState agent2)
        {
            float totalReward = 0f;
            
            // 1. 헤딩 동기화
            float headingDiff = Mathf.Abs(Mathf.DeltaAngle(agent1.heading, agent2.heading));
            if (headingDiff <= headingSyncTolerance)
            {
                totalReward += headingSyncReward;
            }
            
            // 2. 속도 동기화
            float speedDiff = Mathf.Abs(agent1.speed - agent2.speed);
            if (speedDiff <= speedSyncTolerance)
            {
                totalReward += speedSyncReward;
            }
            
            // 3. 간격 유지
            float distance = Vector3.Distance(agent1.position, agent2.position);
            float distanceError = Mathf.Abs(distance - optimalDistance);
            if (distanceError <= distanceTolerance)
            {
                totalReward += distanceMaintainReward;
            }
            
            // 4. 그물 장력 (Net Tension)
            float optimalMin = netMaxLength * netOptimalMinRatio;
            float optimalMax = netMaxLength * netOptimalMaxRatio;
            if (distance >= optimalMin && distance <= optimalMax)
            {
                // 최적 범위 내에서 거리에 따라 보상 조정
                float centerDistance = (optimalMin + optimalMax) / 2f;
                float distanceFromCenter = Mathf.Abs(distance - centerDistance);
                float maxDeviation = (optimalMax - optimalMin) / 2f;
                float tensionFactor = 1f - (distanceFromCenter / maxDeviation);
                totalReward += netTensionReward * tensionFactor;
            }
            
            return totalReward;
        }
        
        /// <summary>
        /// 전술 기동 보상 계산
        /// </summary>
        public float CalculateTacticalRewards(AgentState agent1, AgentState agent2, 
            GameObject[] enemyShips, GameObject webObject)
        {
            float totalReward = 0f;
            
            if (enemyShips == null || enemyShips.Length == 0 || webObject == null)
                return totalReward;
            
            // 가장 가까운 적군 찾기
            GameObject nearestEnemy = null;
            float minDistance = float.MaxValue;
            
            foreach (var enemy in enemyShips)
            {
                if (enemy == null) continue;
                
                float dist = Vector3.Distance(webObject.transform.position, enemy.transform.position);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    nearestEnemy = enemy;
                }
            }
            
            if (nearestEnemy == null)
                return totalReward;
            
            // 1. 수직 차단 보상
            Vector3 netVector = agent2.position - agent1.position;
            Vector3 enemyToWeb = webObject.transform.position - nearestEnemy.transform.position;
            
            if (netVector.magnitude > 0.1f && enemyToWeb.magnitude > 0.1f)
            {
                float angle = Vector3.Angle(netVector, enemyToWeb);
                float angleDiff = Mathf.Abs(angle - 90f);  // 90도에서 얼마나 벗어났는지
                
                if (angleDiff <= perpendicularAngleTolerance)
                {
                    float angleFactor = 1f - (angleDiff / perpendicularAngleTolerance);
                    totalReward += perpendicularInterceptReward * angleFactor;
                }
            }
            
            // 2. 추적 이득 (적-그물 거리 감소)
            float currentDistance = minDistance;
            if (_lastEnemyToWebDistance != float.MaxValue && currentDistance < _lastEnemyToWebDistance)
            {
                float distanceReduction = _lastEnemyToWebDistance - currentDistance;
                totalReward += trackingGainReward * (distanceReduction / 10f);  // 10m당 보상
            }
            _lastEnemyToWebDistance = currentDistance;
            
            return totalReward;
        }
        
        /// <summary>
        /// 안전 및 제약 페널티 계산
        /// </summary>
        public float CalculateSafetyPenalties(AgentState agent1, AgentState agent2)
        {
            float totalPenalty = 0f;
            
            // 1. 대형 붕괴 체크
            float distance = Vector3.Distance(agent1.position, agent2.position);
            float headingDiff = Mathf.Abs(Mathf.DeltaAngle(agent1.heading, agent2.heading));
            
            if (distance > maxFormationDistance || headingDiff > maxFormationAngleDiff)
            {
                totalPenalty += formationBreakPenalty;
            }
            
            // 2. 시간 패널티
            totalPenalty += timePenalty;
            
            return totalPenalty;
        }
        
        /// <summary>
        /// 에이전트 상태 수집
        /// </summary>
        public AgentState GetAgentState(DefenseAgent agent)
        {
            if (agent == null)
            {
                return new AgentState();
            }
            
            AgentState state = new AgentState
            {
                position = agent.transform.position,
                heading = agent.transform.eulerAngles.y,
                transform = agent.transform
            };
            
            // Rigidbody에서 속도 가져오기
            Rigidbody rb = agent.GetComponent<Rigidbody>();
            if (rb != null)
            {
                state.rb = rb;
                state.speed = rb.velocity.magnitude;
            }
            else
            {
                state.speed = 0f;
            }
            
            return state;
        }
        
        /// <summary>
        /// 리셋 (에피소드 시작 시)
        /// </summary>
        public void Reset()
        {
            _lastEnemyToWebDistance = float.MaxValue;
        }
    }
}
