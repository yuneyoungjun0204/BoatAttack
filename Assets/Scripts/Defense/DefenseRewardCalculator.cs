using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 보상 계산기 (단순화)
    /// 매 스텝: 대형 유지 + 적 접근 + 시간 페널티
    /// 이벤트: 포획/모선충돌/아군충돌 (EnvController에서 직접 참조)
    /// </summary>
    public class DefenseRewardCalculator : MonoBehaviour
    {
        [Header("=== 매 스텝 보상 ===")]
        [Tooltip("대형 유지 보상 (아군 간격이 적정 범위 내일 때)")]
        public float formationReward = 0f;

        [Tooltip("아군 간 최적 거리 (m)")]
        public float optimalDistance = 50f;

        [Tooltip("거리 허용 범위 (±m) - 최적 거리 기준")]
        public float distanceTolerance = 25f;

        [Tooltip("적 접근 보상 (레거시, 0=비활성화)")]
        public float approachRewardPerMeter = 0f;

        [Header("=== 차단 위치 보상 ===")]
        [Tooltip("차단 위치 개선 1m당 보상 (적 경로-Web 수직거리 감소)")]
        public float interceptRewardPerMeter = 0.0001f;

        [Tooltip("urgency 기준 거리 (이 along에서 배율 1.0)")]
        public float interceptRefDist = 200f;

        [Tooltip("along 최소 클램프 (0 나누기 방지)")]
        public float interceptMinClamp = 30f;

        [Tooltip("urgency 최대 배율")]
        public float interceptMaxMultiplier = 5f;

        [Tooltip("헤딩 정렬 보상 (0=비활성화, 적 돌진 유발 방지)")]
        public float headingAlignmentReward = 0f;

        [Tooltip("시간 페널티 (매 스텝)")]
        public float timePenalty = -0.0001f;

        [Header("=== 이벤트 보상 ===")]
        [Tooltip("포획 성공 (적이 Web에 충돌)")]
        public float captureReward = 3.0f;

        [Tooltip("포획 거리 보너스 최대값 (모선에서 멀리 잡을수록)")]
        public float captureDistanceBonus = 0.5f;

        [Tooltip("연속 포획 보너스 계수 (n번째 포획: 기본보상 × (1 + (n-1) × 계수))")]
        public float sequentialCaptureBonus = 0.1f;

        [Tooltip("모선 충돌 페널티 (적이 모선에 충돌)")]
        public float motherShipHitPenalty = -2.0f;

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

        [Header("=== 적 추월 페널티 ===")]
        [Tooltip("적이 아군보다 모선에 가까울 때 페널티")]
        public float enemyOvertakePenalty = -1.0f;

        // 이전 스텝의 Web-적 거리 (쌍별 접근 보상 계산용)
        private float _prevWebToEnemyDist = float.MaxValue; // 레거시 (단일 쌍 호환)
        private readonly System.Collections.Generic.Dictionary<int, float> _prevDistByPair
            = new System.Collections.Generic.Dictionary<int, float>();

        // 이전 스텝의 차단 수직거리 (쌍별 추적)
        private readonly System.Collections.Generic.Dictionary<int, float> _prevInterceptByPair
            = new System.Collections.Generic.Dictionary<int, float>();

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
        /// 매 스텝 보상 계산: 대형 유지 + 적 접근 + 시간 페널티
        /// </summary>
        /// <param name="responsibleEnemyDist">담당 적까지 거리 (Voronoi 배정, 없으면 float.MaxValue)</param>
        public float CalculateStepReward(AgentState agent1, AgentState agent2,
            GameObject[] enemyShips, GameObject webObject, float responsibleEnemyDist = float.MaxValue)
        {
            float reward = 0f;

            // 1. 대형 유지: 아군 간 거리가 적정 범위(optimalDistance ± tolerance) 내면 보상
            float allyDist = Vector3.Distance(agent1.position, agent2.position);
            float error = Mathf.Abs(allyDist - optimalDistance);
            if (error <= distanceTolerance)
            {
                reward += formationReward * (1f - error / distanceTolerance);
            }

            // 2. 적 접근: Web↔담당 적 거리가 줄었으면 보상 (Voronoi 기준)
            float currentDist = responsibleEnemyDist;
            // fallback: 담당 적 정보가 없으면 기존 방식 (가장 가까운 적)
            if (currentDist >= float.MaxValue && webObject != null && enemyShips != null)
            {
                currentDist = GetClosestEnemyDistance(webObject.transform.position, enemyShips);
            }

            if (currentDist < float.MaxValue && _prevWebToEnemyDist < float.MaxValue)
            {
                float delta = _prevWebToEnemyDist - currentDist;
                if (delta > 0f)
                {
                    reward += approachRewardPerMeter * delta;
                }
            }
            _prevWebToEnemyDist = currentDist;

            // 3. 시간 페널티
            reward += timePenalty;

            return reward;
        }

        /// <summary>
        /// 개별 에이전트 헤딩 정렬 보상 계산 (그룹이 아닌 개별 보상으로 부여)
        /// </summary>
        public float CalculateIndividualHeadingReward(AgentState agent, GameObject[] enemyShips)
        {
            if (enemyShips == null) return 0f;

            float alignment = GetHeadingAlignment(agent, enemyShips);
            if (alignment > 0f)
            {
                return headingAlignmentReward * alignment;
            }
            return 0f;
        }

        /// <summary>
        /// 에이전트 헤딩과 가장 가까운 적 방향의 정렬도 (-1~1)
        /// 1=정면, 0=수직, -1=등짐
        /// </summary>
        private float GetHeadingAlignment(AgentState agent, GameObject[] enemies)
        {
            float minDist = float.MaxValue;
            Vector3 closestPos = Vector3.zero;
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.activeInHierarchy) continue;
                float dist = Vector3.Distance(agent.position, enemy.transform.position);
                if (dist < minDist)
                {
                    minDist = dist;
                    closestPos = enemy.transform.position;
                }
            }
            if (minDist >= float.MaxValue) return 0f;

            // heading(euler Y) → forward 벡터
            float rad = agent.heading * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

            // 적 방향 (수평면)
            Vector3 toEnemy = closestPos - agent.position;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude < 0.01f) return 0f;
            toEnemy.Normalize();

            return Vector3.Dot(forward, toEnemy);
        }

        /// <summary>
        /// Web 위치에서 가장 가까운 활성 적군까지의 거리
        /// </summary>
        private float GetClosestEnemyDistance(Vector3 webPos, GameObject[] enemies)
        {
            float minDist = float.MaxValue;
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.activeInHierarchy) continue;
                float dist = Vector3.Distance(webPos, enemy.transform.position);
                if (dist < minDist) minDist = dist;
            }
            return minDist;
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
        /// 대형 유지 + 차단 위치 + 시간 페널티
        /// </summary>
        /// <param name="interceptDist">적 경로 Ray에서 Web 중심까지 수직 거리 (along≤0이면 float.MaxValue)</param>
        /// <param name="alongDist">적 전방 투영 거리 (urgency 계산용, along≤0이면 0)</param>
        public float CalculatePairStepReward(int pairIdx, AgentState agent1, AgentState agent2,
            float interceptDist, float alongDist, int enemyIdx = -1)
        {
            float reward = 0f;

            // 1. 대형 유지: 아군 간 거리가 적정 범위(optimalDistance ± tolerance) 내면 보상
            float allyDist = Vector3.Distance(agent1.position, agent2.position);
            float error = Mathf.Abs(allyDist - optimalDistance);
            if (error <= distanceTolerance)
            {
                reward += formationReward * (1f - error / distanceTolerance);
            }

            // 담당 적이 바뀌면 prev 리셋 (다른 적의 interceptDist와 비교 방지)
            if (_prevEnemyByPair.TryGetValue(pairIdx, out int prevEnemy) && prevEnemy != enemyIdx)
            {
                _prevInterceptByPair.Remove(pairIdx);
                _prevDistByPair.Remove(pairIdx);
            }
            _prevEnemyByPair[pairIdx] = enemyIdx;

            // 2. 차단 위치 보상: 적 경로-Web 수직거리가 줄어들면 보상
            // 적이 직진하면 interceptDist는 불변 → 아군 이동분만 보상
            if (!_prevInterceptByPair.TryGetValue(pairIdx, out float prevIntercept))
                prevIntercept = float.MaxValue;

            if (interceptDist < float.MaxValue && prevIntercept < float.MaxValue)
            {
                float delta = prevIntercept - interceptDist; // 줄어들면 양수
                if (delta > 0f && alongDist > 0f)
                {
                    // 적이 가까울수록 urgency 증가 (긴급 차단 유도)
                    float urgency = interceptRefDist / Mathf.Max(alongDist, interceptMinClamp);
                    urgency = Mathf.Min(urgency, interceptMaxMultiplier);
                    reward += interceptRewardPerMeter * delta * urgency;
                }
            }
            _prevInterceptByPair[pairIdx] = interceptDist;

            // 3. 레거시 접근 보상 (approachRewardPerMeter > 0일 때만, 기본 비활성)
            if (approachRewardPerMeter > 0f)
            {
                if (!_prevDistByPair.TryGetValue(pairIdx, out float prevDist))
                    prevDist = float.MaxValue;
                float webToEnemyDist = (interceptDist < float.MaxValue)
                    ? Mathf.Sqrt(interceptDist * interceptDist + alongDist * alongDist)
                    : float.MaxValue;
                if (webToEnemyDist < float.MaxValue && prevDist < float.MaxValue)
                {
                    float d = prevDist - webToEnemyDist;
                    if (d > 0f) reward += approachRewardPerMeter * d;
                }
                _prevDistByPair[pairIdx] = webToEnemyDist;
            }

            // 4. 시간 페널티
            reward += timePenalty;

            return reward;
        }

        /// <summary>
        /// 리셋 (에피소드 시작 시)
        /// </summary>
        public void Reset()
        {
            _prevWebToEnemyDist = float.MaxValue;
            _prevDistByPair.Clear();
            _prevInterceptByPair.Clear();
            _prevEnemyByPair.Clear();
        }
    }
}
