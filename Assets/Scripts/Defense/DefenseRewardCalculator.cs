using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 보상 계산기
    /// 매 스텝: LOS 수직 대형 + Web→적 접근 + 시간 페널티
    /// 이벤트: 포획/모선충돌/아군충돌 (EnvController에서 직접 참조)
    /// </summary>
    public class DefenseRewardCalculator : MonoBehaviour
    {
        [Header("=== 매 스텝 보상 ===")]
        [Tooltip("LOS 수직 방향 대형 보상 (두 선박이 LOS 기준 수직으로 벌어질수록). 권장: 0.002")]
        public float formationReward = 0.002f;

        [Tooltip("LOS 수직 방향 최적 간격 (m). 두 선박이 이 간격이 될 때 최대 보상. 권장: 100m")]
        public float optimalDistance = 100f;

        [Tooltip("거리 허용 범위 (±m). 권장: 40m → 60~140m 범위에서 보상")]
        public float distanceTolerance = 40f;

        [Tooltip("Web→적 거리 1m 감소당 보상. 권장: 0.001")]
        public float approachRewardPerMeter = 0.001f;

        [Tooltip("헤딩 정렬 보상 (에이전트가 적을 향할수록). 권장: 0.0003")]
        public float headingAlignmentReward = 0.0003f;

        [Tooltip("시간 페널티 (매 스텝). 권장: -0.0001")]
        public float timePenalty = -0.0001f;

        [Tooltip("RL 조향이 LOS 베이스라인에 가까울수록 보상. 권장: 0.001")]
        public float losAlignmentRewardCoeff = 0.001f;

        [Header("=== 선회 대형 보상 ===")]
        [Tooltip("그물이 optimalDistance보다 줄어드는 속도에 비례한 페널티 (선회 외곽 선박 가속 유도). 권장: 0.0003")]
        public float webShrinkPenalty = 0.0003f;

        [Tooltip("그물이 optimalDistance보다 짧을 때 개별 속도 비례 보상 — 외곽 선박이 더 빠를수록 유리. 권장: 0.0002")]
        public float webSpeedBonus = 0.0002f;

        [Tooltip("적군이 웹 존 안에 머무는 동안 매 스텝 보상. 권장: 0.005")]
        public float webBlockReward = 0.005f;

        [Header("=== Raycast 차단 보상 ===")]
        [Tooltip("적→모선 Ray가 Web에 닿을 때 해당 쌍에 매 스텝 보상. 권장: 0.002")]
        public float raycastInterceptReward = 0.002f;

        [Header("=== Raycast 타임아웃 ===")]
        [Tooltip("이 스텝 수 동안 Ray를 한 번도 차단 못하면 페널티 + 비활성화. 권장: 300")]
        public int raycastTimeoutSteps = 300;

        [Tooltip("타임아웃 비활성화 시 페널티")]
        public float raycastTimeoutPenalty = -0.5f;

        [Header("=== 이벤트 보상 ===")]
        [Tooltip("포획 성공 보상. ONE-attack: 1.0")]
        public float captureReward = 1.0f;

        [Tooltip("포획한 쌍에 추가 개별 보너스 (MA-POCA 개인 채널 강화)")]
        public float capturePairBonus = 0.3f;

        [Tooltip("모선 충돌 페널티. ONE-attack: -1.0")]
        public float motherShipHitPenalty = -1.0f;

        [Tooltip("충돌 페널티 (아군끼리/거리초과 등). ONE-attack: -0.5")]
        public float collisionPenalty = -0.5f;

        [Header("=== 타 페어 충돌 회피 보상 ===")]
        [Tooltip("타 페어 에이전트 접근 시 페널티 계수. 권장: 0.003")]
        public float interPairAvoidanceCoeff = 0.003f;

        [Tooltip("충돌 회피 경보 반경 (m). 이 거리 이내부터 페널티 시작. 권장: 30m")]
        public float avoidanceWarningRadius = 30f;

        [Tooltip("충돌 위험 반경 (m). 이 거리 이내에서 급격한 추가 페널티. 권장: 8m")]
        public float avoidanceDangerRadius = 8f;

        [Header("=== 아군 거리 제한 ===")]
        [Tooltip("아군 간 최대 허용 거리 (m). ONE-attack: 120m")]
        public float maxAllyDistance = 120f;

        [Tooltip("아군 간 최소 허용 거리 (m). ONE-attack: 4m")]
        public float minAllyDistance = 4f;

        [Header("=== 에피소드 종료 보상 ===")]
        [Tooltip("모든 적군 제압 시 보너스")]
        public float allClearBonus = 2.0f;

        [Tooltip("적군 방어선 돌파 페널티")]
        public float enemyBreachPenalty = -1.0f;

        [Tooltip("에피소드 종료 시 남은 적군 1대당 페널티")]
        public float remainingEnemyPenalty = -0.5f;

        [Tooltip("아군 선박 전부 비활성화 시 고정 페널티")]
        public float noPairsLeftPenalty = -5f;

        // 쌍별 이전 스텝의 Web→적 최근접 거리 (접근 보상 계산용)
        private readonly System.Collections.Generic.Dictionary<int, float> _prevWebToEnemyDist
            = new System.Collections.Generic.Dictionary<int, float>();

        // 쌍별 이전 스텝 그물 길이 (선회 대형 보상용)
        private readonly System.Collections.Generic.Dictionary<int, float> _prevWebLength
            = new System.Collections.Generic.Dictionary<int, float>();

        /// <summary>에이전트 상태</summary>
        public struct AgentState
        {
            public Vector3 position;
            public float heading;
            public float speed;
        }

        /// <summary>에이전트 상태 수집</summary>
        public AgentState GetAgentState(DefenseAgent agent)
        {
            if (agent == null) return new AgentState();

            var state = new AgentState
            {
                position = agent.transform.position,
                heading  = agent.transform.eulerAngles.y
            };

            var rb = agent.GetComponent<Rigidbody>();
            if (rb != null) state.speed = rb.velocity.magnitude;

            return state;
        }

        /// <summary>
        /// 매 스텝 쌍 보상: LOS 수직 대형 + Web→적 접근 + 시간 페널티
        /// losDir: 클러스터→모선 단위벡터 (Vector3.zero이면 총 거리 기반 fallback)
        /// </summary>
        public float CalculateStepReward(int pairIdx,
            AgentState agent1, AgentState agent2,
            GameObject[] enemyShips, GameObject webObject,
            Vector3 losDir = default)
        {
            float reward = 0f;

            // 1. 대형 보상: LOS 수직 방향 간격이 optimalDistance ± distanceTolerance 내면 보상
            float formationDist;
            if (losDir != Vector3.zero)
            {
                // LOS 수직 방향(perpRight)으로 투영한 거리
                Vector3 perpRight = new Vector3(losDir.z, 0f, -losDir.x);
                Vector3 diff = agent2.position - agent1.position;
                diff.y = 0f;
                formationDist = Mathf.Abs(Vector3.Dot(diff, perpRight));
            }
            else
            {
                // fallback: 클러스터 미배정 시 총 거리
                formationDist = Vector3.Distance(agent1.position, agent2.position);
            }

            // Gaussian 형태: optimalDistance에서 최대, 멀어질수록 지수 감쇠
            // sigma = distanceTolerance → error=0 시 1.0, error=tolerance 시 ~0.37
            float error = formationDist - optimalDistance;
            float sigma = distanceTolerance;
            reward += formationReward * Mathf.Exp(-0.5f * (error * error) / (sigma * sigma));

            // 2. Web→적 접근: Web 중심과 가장 가까운 적 거리가 줄었으면 보상
            if (webObject != null && enemyShips != null && approachRewardPerMeter > 0f)
            {
                Vector3 webPos = webObject.transform.position;
                float closestDist = GetClosestActiveEnemyDistance(webPos, enemyShips);
                if (closestDist < float.MaxValue
                    && _prevWebToEnemyDist.TryGetValue(pairIdx, out float prevDist)
                    && prevDist < float.MaxValue)
                {
                    float delta = prevDist - closestDist;
                    if (delta > 0f) reward += approachRewardPerMeter * delta;
                }
                _prevWebToEnemyDist[pairIdx] = closestDist;
            }

            // 3. 시간 페널티
            reward += timePenalty;

            // 4. 그물 길이 변화 페널티: 수축(deltaLen<0) + 팽창(deltaLen>0) 모두 페널티
            //    선회 시 외곽 선박 가속 유도, 과도한 벌어짐도 억제
            if (webShrinkPenalty > 0f)
            {
                float currLen = Vector3.Distance(agent1.position, agent2.position);
                if (_prevWebLength.TryGetValue(pairIdx, out float prevLen))
                {
                    float deltaLen = currLen - prevLen;
                    // 수축 페널티 (deltaLen < 0): webShrinkPenalty * |deltaLen|
                    // 팽창 페널티 (deltaLen > 0): webShrinkPenalty * |deltaLen| (동일 계수)
                    reward -= webShrinkPenalty * Mathf.Abs(deltaLen);
                }
                _prevWebLength[pairIdx] = currLen;
            }

            return reward;
        }

        /// <summary>
        /// 개별 에이전트 헤딩 정렬 보상 (가장 가까운 적을 향할수록)
        /// </summary>
        public float CalculateIndividualHeadingReward(AgentState agent, GameObject[] enemyShips)
        {
            if (enemyShips == null || headingAlignmentReward <= 0f) return 0f;

            float alignment = GetHeadingAlignment(agent, enemyShips);
            return alignment > 0f ? headingAlignmentReward * alignment : 0f;
        }

        private float GetHeadingAlignment(AgentState agent, GameObject[] enemies)
        {
            float minDist = float.MaxValue;
            Vector3 closestPos = Vector3.zero;
            foreach (var e in enemies)
            {
                if (e == null || !e.activeInHierarchy) continue;
                float d = Vector3.Distance(agent.position, e.transform.position);
                if (d < minDist) { minDist = d; closestPos = e.transform.position; }
            }
            if (minDist >= float.MaxValue) return 0f;

            float rad = agent.heading * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

            Vector3 toEnemy = closestPos - agent.position;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude < 0.01f) return 0f;

            return Vector3.Dot(forward, toEnemy.normalized);
        }

        private float GetClosestActiveEnemyDistance(Vector3 webPos, GameObject[] enemies)
        {
            float minDist = float.MaxValue;
            foreach (var e in enemies)
            {
                if (e == null || !e.activeInHierarchy) continue;
                float d = Vector3.Distance(webPos, e.transform.position);
                if (d < minDist) minDist = d;
            }
            return minDist;
        }

        /// <summary>에피소드 시작 시 리셋</summary>
        public void Reset()
        {
            _prevWebToEnemyDist.Clear();
            _prevWebLength.Clear();
        }
    }
}
