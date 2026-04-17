using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 보상 계산기 (Only-oneattack 방식)
    /// 매 스텝: 대형 유지 + 적 접근 + 시간 페널티
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

        [Tooltip("적 접근 보상 (Web-적 거리 1m 감소당)")]
        public float approachRewardPerMeter = 0.001f;

        [Tooltip("헤딩 정렬 보상 (에이전트가 적을 향할수록)")]
        public float headingAlignmentReward = 0.0005f;

        [Tooltip("시간 페널티 (매 스텝)")]
        public float timePenalty = -0.0001f;

        [Header("=== 이벤트 보상 ===")]
        [Tooltip("포획 성공 (적이 Web에 충돌)")]
        public float captureReward = 1.0f;

        [Tooltip("모선 충돌 페널티 (적이 모선에 충돌)")]
        public float motherShipHitPenalty = -1.0f;

        [Tooltip("충돌 페널티 (아군끼리/거리초과 등)")]
        public float collisionPenalty = -0.5f;

        [Header("=== 아군 거리 제한 ===")]
        [Tooltip("아군 간 최대 허용 거리 (m). 초과 시 에피소드 종료")]
        public float maxAllyDistance = 120f;

        [Tooltip("아군 간 최소 허용 거리 (m). 미만 시 에피소드 종료")]
        public float minAllyDistance = 4f;

        [Header("=== 에피소드 종료 보상 ===")]
        [Tooltip("모든 적군 제압 시 보너스")]
        public float allClearBonus = 2.0f;

        [Tooltip("적군 방어선 돌파 페널티")]
        public float enemyBreachPenalty = -1.0f;

        [Tooltip("에피소드 종료 시 남은 적군 1대당 페널티")]
        public float remainingEnemyPenalty = -0.5f;

        // 쌍별 이전 스텝의 Web-적 거리 (접근 보상 계산용)
        private readonly System.Collections.Generic.Dictionary<int, float> _prevWebToEnemyDist
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
        /// 쌍별 매 스텝 보상: 대형 유지 + 적 접근 + 시간 페널티
        /// </summary>
        public float CalculateStepReward(int pairIdx,
            AgentState agent1, AgentState agent2,
            GameObject[] enemyShips, GameObject webObject)
        {
            float reward = 0f;

            // 1. 대형 유지: 아군 간격이 optimalDistance ± distanceTolerance 내면 보상
            float allyDist = Vector3.Distance(agent1.position, agent2.position);
            float error = Mathf.Abs(allyDist - optimalDistance);
            if (error <= distanceTolerance)
                reward += formationReward * (1f - error / distanceTolerance);

            // 2. 적 접근: Web 중심과 가장 가까운 적 거리가 줄었으면 보상
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
        }
    }
}
