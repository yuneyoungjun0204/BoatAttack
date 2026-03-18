using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 보상 계산기
    /// 매 스텝: 대형 유지
    /// 이벤트: 포획/모선충돌/아군충돌 (EnvController에서 직접 참조)
    /// </summary>
    public class DefenseRewardCalculator : MonoBehaviour
    {
        [Header("=== 매 스텝 보상 ===")]
        [Tooltip("대형 유지 보상 (아군 간격이 적정 범위 내일 때)")]
        public float formationReward = 0.001f;

        [Tooltip("아군 간 최적 거리 (m)")]
        public float optimalDistance = 50f;

        [Tooltip("거리 허용 범위 (±m) - 최적 거리 기준")]
        public float distanceTolerance = 25f;

        [Tooltip("추력 보상 계수 (throttle × 계수 = 매 스텝 보상, 가속할수록 보상)")]
        public float throttleRewardCoeff = 0.0002f;

        [Header("=== 이벤트 보상 ===")]
        [Tooltip("포획 성공 (적이 Web에 충돌)")]
        public float captureReward = 1.0f;

        [Tooltip("포획 거리 보너스 최대값 (모선에서 멀리 잡을수록)")]
        public float captureDistanceBonus = 0.5f;

        [Tooltip("연속 포획 보너스 계수 (n번째 포획: 기본보상 × (1 + (n-1) × 계수))")]
        public float sequentialCaptureBonus = 0.1f;

        [Tooltip("모선 충돌 페널티 (적이 모선에 충돌)")]
        public float motherShipHitPenalty = -1.0f;

        [Tooltip("충돌 페널티 (아군끼리/모선/거리초과 등)")]
        public float collisionPenalty = -0.5f;

        [Tooltip("아군 Web 충돌 페널티")]
        public float allyWebCollisionPenalty = -0.3f;

        [Tooltip("적군 방어선 돌파 페널티")]
        public float enemyBreachPenalty = -1.0f;

        [Tooltip("에피소드 종료 시 사용된 페어 수 × 이 계수 = 페널티 (음수)")]
        public float pairUsagePenaltyCoeff = -0.05f;

        [Tooltip("NoPairsLeft 종료 시 남은 적군 1대당 페널티 (음수)")]
        public float remainingEnemyPenalty = -0.5f;

        [Tooltip("모든 적군 제압 시 추가 보너스")]
        public float allClearBonus = 2.0f;

        [Header("=== 커버리지 보상 ===")]
        [Tooltip("적군 커버리지 거리 감소 1m당 그룹 보상")]
        public float coverageRewardPerMeter = 0.001f;

        [Header("=== Raycast 차단 보상 ===")]
        [Tooltip("적→모선 Ray가 Web에 닿을 때 해당 쌍에 매 스텝 보상")]
        public float raycastInterceptReward = 0.002f;

        [Tooltip("수직 차단 보너스 계수 (perpScore × 계수가 보상 배율에 추가)")]
        public float perpendicularBonusCoeff = 0.5f;

        [Tooltip("중앙 차단 보너스 계수 (centerScore × 계수가 보상 배율에 추가)")]
        public float centerBonusCoeff = 0.5f;

        [Header("=== 레이캐스트 타임아웃 ===")]
        [Tooltip("레이캐스트 미차단 허용 스텝 수 (0=비활성화). 50스텝≈5초@timescale10")]
        public int raycastTimeoutSteps = 50;

        [Tooltip("타임아웃 비활성화 시 페널티")]
        public float raycastTimeoutPenalty = -0.5f;

        [Header("=== 근접 포획 보너스 (Bridge Reward) ===")]
        [Tooltip("Web중심↔적 거리가 임계값 이내일 때 보상 계수")]
        public float proximityBridgeCoeff = 0.005f;

        [Tooltip("근접 포획 보너스 활성화 거리 (m)")]
        public float proximityThreshold = 30f;

        [Header("=== 거리 제한 ===")]
        [Tooltip("아군 간 최대 허용 거리 (초과 시 쌍 무력화)")]
        public float maxAllyDistance = 100f;

        [Tooltip("아군 간 최소 허용 거리 (미만 시 쌍 무력화)")]
        public float minAllyDistance = 4f;

        [Header("=== Phantom 페널티 ===")]
        [Tooltip("가상 아군쌍 접근 시 최소 허용 거리 (m)")]
        public float phantomMinDistance = 30f;

        [Tooltip("가상 아군쌍 침범 페널티")]
        public float phantomViolationPenalty = -0.5f;

        [Header("=== 쌍 간 근접 페널티 ===")]
        [Tooltip("다른 쌍과의 최소 허용 거리 (m) — 이 이내로 접근하면 연속 페널티")]
        public float pairProximityMinDistance = 40f;

        [Tooltip("쌍 간 근접 페널티 계수 (거리 1m 침범당 페널티)")]
        public float pairProximityPenaltyCoeff = -0.001f;

        [Header("=== 적 추월 페널티 ===")]
        [Tooltip("적이 아군보다 모선에 가까울 때 페널티")]
        public float enemyOvertakePenalty = -1.0f;

        // 이전 스텝의 담당 적 인덱스 (쌍별 추적, 적 변경 시 prev 리셋)
        private readonly System.Collections.Generic.Dictionary<int, int> _prevEnemyByPair
            = new System.Collections.Generic.Dictionary<int, int>();

        /// <summary>
        /// 에이전트 상태
        /// </summary>
        public struct AgentState
        {
            public Vector3 position;
            public float heading;
            public float speed;
        }

        /// <summary>
        /// 에이전트 상태 수집
        /// </summary>
        public AgentState GetAgentState(DefenseAgent agent)
        {
            if (agent == null)
                return new AgentState();

            AgentState state = new AgentState
            {
                position = agent.transform.position,
                heading = agent.transform.eulerAngles.y
            };

            Rigidbody rb = agent.GetComponent<Rigidbody>();
            if (rb != null)
            {
                state.speed = rb.velocity.magnitude;
            }

            return state;
        }

        /// <summary>
        /// 쌍별 매 스텝 보상 계산 (멀티 쌍 대응)
        /// 대형 유지만 계산 (차단/접근/시간 보상은 제거됨)
        /// </summary>
        public float CalculatePairStepReward(int pairIdx, AgentState agent1, AgentState agent2,
            float interceptDist, float alongDist, int enemyIdx = -1)
        {
            float reward = 0f;

            // 대형 유지: 아군 간 거리가 적정 범위(optimalDistance ± tolerance) 내면 보상
            float allyDist = Vector3.Distance(agent1.position, agent2.position);
            float error = Mathf.Abs(allyDist - optimalDistance);
            if (error <= distanceTolerance)
            {
                reward += formationReward * (1f - error / distanceTolerance);
            }

            // 담당 적 변경 추적 (외부에서 사용 가능)
            _prevEnemyByPair[pairIdx] = enemyIdx;

            return reward;
        }

        /// <summary>
        /// 리셋 (에피소드 시작 시)
        /// </summary>
        public void Reset()
        {
            _prevEnemyByPair.Clear();
        }
    }
}
