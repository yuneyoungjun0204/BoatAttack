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
        public float throttleRewardCoeff = 0.00005f;

        [Tooltip("시간 페널티 (매 스텝, 빠른 포획 유도)")]
        public float timePenalty = 0f;

        [Header("=== 이벤트 보상 ===")]
        [Tooltip("포획 성공 (적이 Web에 충돌)")]
        public float captureReward = 1.0f;

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
        public float coverageRewardPerMeter = 0.0002f;

        [Tooltip("담당 적 정면 정렬 보상 계수 (Gaussian 최대값 × 계수 = 스텝당 최대 보상)")]
        public float bearingAlignRewardCoeff = 0.0002f;

        [Tooltip("Gaussian 폭 (σ): 작을수록 정면에서만 보상, 클수록 넓게 허용 (기본 0.3)")]
        [Range(0.05f, 1f)]
        public float bearingGaussianSigma = 0.3f;

        [Header("=== Raycast 차단 보상 ===")]
        [Tooltip("적→모선 Ray가 Web에 닿을 때 해당 쌍에 매 스텝 보상")]
        public float raycastInterceptReward = 0.0005f;

        [Tooltip("수직 차단 보너스 계수 (perpScore × 계수가 보상 배율에 추가)")]
        public float perpendicularBonusCoeff = 0.5f;

        [Tooltip("중앙 차단 보너스 계수 (centerScore × 계수가 보상 배율에 추가)")]
        public float centerBonusCoeff = 0.5f;

        [Tooltip("근접 포획 보상: 적이 Web hit 지점 nearCaptureDistance 이내 진입 시 추가 보상 (포획 직전 강화 신호)")]
        public float nearCaptureReward = 0.05f;

        [Tooltip("근접 포획 보상 발동 거리 (m) — 적→Web hit 거리가 이 이내일 때 발동")]
        public float nearCaptureDistance = 60f;

        [Tooltip("적→모선 Ray가 차단 없이 모선에 직통할 때 팀 전체 매 스텝 페널티 (적 1대당)")]
        public float raycastDirectHitPenalty = -0.002f;

        [Header("=== 레이캐스트 타임아웃 ===")]
        [Tooltip("레이캐스트 미차단 허용 스텝 수 (0=비활성화). 50스텝≈5초@timescale10")]
        public int raycastTimeoutSteps = 500;

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

        [Tooltip("아군이 설치된 정지 트랩 그물에 충돌 시 페널티 (해당 쌍 비활성화)")]
        public float allyHitTrapPenalty = -0.5f;

        [Tooltip("쌍 간 최소 허용 거리 (m)")]
        public float pairProximityMinDistance = 40f;

        [Tooltip("쌍 간 근접 페널티 계수")]
        public float pairProximityPenaltyCoeff = -0.001f;

        [Header("=== 적 추월 페널티 ===")]
        [Tooltip("적이 아군보다 모선에 가까울 때 페널티")]
        public float enemyOvertakePenalty = -1.0f;

        [Header("=== 추격 트랩 (Stage10 / Flank Phase) ===")]
        [Tooltip("적과 같은 방향 헤딩 정렬 보상 계수 (headingDiff≈0일수록 최대)")]
        public float flankHeadingAlignCoeff = 0.0002f;

        [Tooltip("측면 감지 Ray 최대 거리 (m) — 이 이내에 적이 있으면 측면 근접 보상")]
        public float flankSideRayRange = 20f;

        [Tooltip("담당 적 측면으로 접근 시 lateral 거리 감소 1m당 보상 계수 (직선 돌진 방지, 0=비활성)")]
        public float flankApproachRewardCoeff = 0.001f;

        [Tooltip("SingleNet 투척 트리거 거리 (m) — 측면 거리가 이 이하이면 자동 투척")]
        public float flankCaptureThreshold = 8f;

        [Header("=== Convoy / Deploy ===")]
        [Tooltip("적이 이 거리(m) 이내 진입 시 그물 전개 시작")]
        public float deployRange = 150f;

        [Tooltip("선박 간격이 이 거리(m) 이상이면 그물 고정 + 선박 정지")]
        public float webDeployedThreshold = 100f;

        // 이전 스텝의 담당 적 인덱스 (쌍별 추적, 적 변경 시 prev 리셋)
        private readonly System.Collections.Generic.Dictionary<int, int> _prevEnemyByPair
            = new System.Collections.Generic.Dictionary<int, int>();

        // 이전 스텝의 차단 거리 (Δdist 보상용)
        private readonly System.Collections.Generic.Dictionary<int, float> _prevInterceptDist
            = new System.Collections.Generic.Dictionary<int, float>();

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
            // 담당 적 변경 시 prev 리셋
            if (_prevEnemyByPair.TryGetValue(pairIdx, out int prevIdx) && prevIdx != enemyIdx)
            {
                _prevInterceptDist.Remove(pairIdx);
            }
            _prevEnemyByPair[pairIdx] = enemyIdx;

            float reward = 0f;

            // 1. 차단 위치 접근 보상: 적→모선 Ray에 가까워질수록 +
            if (interceptDist < float.MaxValue && coverageRewardPerMeter > 0f)
            {
                if (_prevInterceptDist.TryGetValue(pairIdx, out float prevDist))
                {
                    float delta = prevDist - interceptDist; // 양수 = 접근
                    reward += delta * coverageRewardPerMeter;
                }
                _prevInterceptDist[pairIdx] = interceptDist;
            }

            // 2. 추력 보상: 전진할수록 +
            if (throttleRewardCoeff > 0f)
            {
                float avgSpeed = (agent1.speed + agent2.speed) * 0.5f;
                reward += Mathf.Clamp01(avgSpeed / 15f) * throttleRewardCoeff;
            }

            return reward;
        }

        /// <summary>
        /// Stage10 Flank Phase 매 스텝 보상.
        /// Phase 0 보상 체계를 재활용하되 기준만 반전:
        ///   - 헤딩 정렬: 적과 같은 방향일수록 보상 (headingDiff≈0 → max)
        ///   - 측면 근접: 적이 측면 flankSideRayRange 이내에 있을수록 보상
        ///   - 시간 페널티: timePenalty 동일 적용
        /// </summary>
        /// <param name="headingDiff">NormalizeHeadingDiff(myYaw, enemyYaw) 결과 (-1~+1, 0=동방향)</param>
        /// <param name="lateralDist">적까지 측면 거리(m), transform.right 기준 절댓값</param>
        public float CalculateFlankStepReward(float headingDiff, float lateralDist)
        {
            float reward = 0f;

            // 1. 헤딩 정렬: headingDiff=0(동방향)일수록 보상 최대
            if (flankHeadingAlignCoeff > 0f)
                reward += flankHeadingAlignCoeff * Mathf.Max(1f - Mathf.Abs(headingDiff), 0f);

            // 2. 측면 근접: 적이 flankSideRayRange 이내에 있으면 거리에 반비례 보상
            if (lateralDist >= 0f && lateralDist < flankSideRayRange)
                reward += raycastInterceptReward * (1f - lateralDist / flankSideRayRange);

            // 3. 시간 페널티 (기존과 동일)
            reward += timePenalty;

            return reward;
        }

        /// <summary>
        /// 리셋 (에피소드 시작 시)
        /// </summary>
        public void Reset()
        {
            _prevEnemyByPair.Clear();
            _prevInterceptDist.Clear();
        }
    }
}
