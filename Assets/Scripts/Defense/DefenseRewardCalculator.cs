using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 보상 계산기
    /// CONVOY: 쌍동선 (kinematic 고정) → 대형 유지 보상 불필요
    /// 이벤트: 포획/모선충돌 (EnvController에서 직접 참조)
    /// </summary>
    public class DefenseRewardCalculator : MonoBehaviour
    {
        [Header("=== 매 스텝 보상 ===")]
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

        [Header("=== 충돌 페널티 ===")]
        [Tooltip("충돌 페널티 (아군 간 물리 충돌)")]
        public float collisionPenalty = -0.5f;

        [Tooltip("아군 Web 충돌 페널티")]
        public float allyWebCollisionPenalty = -0.3f;

        [Tooltip("쌍 간 최소 허용 거리 (m)")]
        public float pairProximityMinDistance = 40f;

        [Tooltip("쌍 간 근접 페널티 계수")]
        public float pairProximityPenaltyCoeff = -0.001f;

        [Header("=== 적 추월 페널티 ===")]
        [Tooltip("적이 아군보다 모선에 가까울 때 페널티")]
        public float enemyOvertakePenalty = -1.0f;

        [Header("=== Convoy / Deploy ===")]
        [Tooltip("적이 이 거리(m) 이내 진입 시 그물 전개 시작")]
        public float deployRange = 150f;

        [Tooltip("선박 간격이 이 거리(m) 이상이면 그물 고정 + 선박 정지")]
        public float webDeployedThreshold = 100f;

        [Tooltip("Deploy 트리거 발동 시 일회성 보너스 (양 에이전트에 지급)")]
        public float deployTriggerBonus = 0.3f;

        [Header("=== Convoy Ray 보상 ===")]
        [Tooltip("Ray 차단 접근 보상 (|SignedRayDist|→0 유도, 매 스텝)")]
        public float rayApproachReward = 0.002f;

        [Tooltip("Ray 수직 헤딩 보상 (쌍 헤딩 ⊥ Ray 유도, 매 스텝)")]
        public float rayPerpendicularReward = 0.001f;

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
        /// 쌍별 매 스텝 보상 계산
        /// CONVOY: 대형 유지 제거 (kinematic 고정), 담당 적 추적만 유지
        /// </summary>
        public float CalculatePairStepReward(int pairIdx, AgentState agent1, AgentState agent2,
            float interceptDist, float alongDist, int enemyIdx = -1)
        {
            // 담당 적 변경 추적 (외부에서 사용 가능)
            _prevEnemyByPair[pairIdx] = enemyIdx;

            return 0f;
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
