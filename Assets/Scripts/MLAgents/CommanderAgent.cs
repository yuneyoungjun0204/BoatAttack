using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

namespace BoatAttack
{
    /// <summary>
    /// Commander Agent: 전장 전체를 관측하여 페어 단위로 타겟/스폰 배정
    /// PPO, 이산 액션, 50스텝 주기 결정
    /// </summary>
    public class CommanderAgent : Agent
    {
        [Header("References")]
        [Tooltip("환경 컨트롤러")]
        public DefenseEnvController envController;

        [Header("BufferSensors")]
        [Tooltip("페어 상태 관측 (Inspector에서 BufferSensorComponent 할당)")]
        public BufferSensorComponent pairSensor;

        [Tooltip("적군 상태 관측 (Inspector에서 BufferSensorComponent 할당)")]
        public BufferSensorComponent enemySensor;

        [Header("Settings")]
        [Tooltip("결정 주기 (FixedUpdate 스텝)")]
        public int decisionPeriod = 50;

        [Tooltip("최대 적군 수 (이산 브랜치 크기 결정)")]
        public int maxEnemies = 5;

        [Tooltip("스폰 포인트 수 (이산 브랜치 크기 결정)")]
        public int numSpawnPoints = 6;

        [Header("Commander Reward Coefficients")]
        [Tooltip("분산 배정 보상 계수")]
        public float coverageRewardCoeff = 0.01f;

        [Tooltip("스폰 중복 시도 페널티")]
        public float spawnFailPenalty = -0.05f;

        [Tooltip("미배정 페널티 (적 있는데 대기 중인 페어)")]
        public float unassignedPenalty = -0.005f;

        private int _stepCounter = 0;

        public override void OnEpisodeBegin()
        {
            _stepCounter = 0;
        }

        /// <summary>
        /// VectorSensor: 고정 관측
        /// - activeEnemies / maxEnemies
        /// - activePairs / maxPairs
        /// - episodeProgress (0~1)
        /// - 각 페어 상태: 배치여부, 남은기회 (maxPairs × 2)
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            if (envController == null)
            {
                // 고정 관측 크기: 3 + maxPairs*2
                int fixedSize = 3 + (envController != null ? envController.maxPairs : 4) * 2;
                for (int i = 0; i < fixedSize; i++)
                    sensor.AddObservation(0f);
                return;
            }

            int maxPairs = envController.maxPairs;
            var pairs = envController.defensePairs;

            // 활성 적군 수
            int activeEnemies = 0;
            if (envController.enemyShips != null)
            {
                foreach (var e in envController.enemyShips)
                    if (e != null && e.activeInHierarchy) activeEnemies++;
            }
            sensor.AddObservation((float)activeEnemies / Mathf.Max(1, maxEnemies));

            // 활성 페어 수
            int activePairs = 0;
            foreach (var p in pairs)
                if (p.isDeployed) activePairs++;
            sensor.AddObservation((float)activePairs / Mathf.Max(1, maxPairs));

            // 에피소드 진행도
            float progress = (float)envController.CurrentStep / Mathf.Max(1, envController.maxEnvironmentSteps);
            sensor.AddObservation(Mathf.Clamp01(progress));

            // 각 페어 상태 (maxPairs 슬롯, 부족하면 0 패딩)
            for (int i = 0; i < maxPairs; i++)
            {
                if (i < pairs.Count)
                {
                    sensor.AddObservation(pairs[i].isDeployed ? 1f : 0f);
                    sensor.AddObservation((float)pairs[i].spawnAttemptsLeft / 3f);
                }
                else
                {
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                }
            }

            // BufferSensor: 페어 상태 (위치, 헤딩, 간격, 배정, 배치)
            if (pairSensor != null)
            {
                Vector3 motherPos = envController.motherShip != null
                    ? envController.motherShip.transform.position : Vector3.zero;

                foreach (var pair in pairs)
                {
                    if (pair.agent1 == null || pair.agent2 == null) continue;

                    Vector3 center = (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f;
                    Vector3 relCenter = center - motherPos;

                    float r = relCenter.magnitude > 0.1f ? relCenter.x / (Mathf.Abs(relCenter.x) + 250f) : 0f;
                    float f = relCenter.magnitude > 0.1f ? relCenter.z / (Mathf.Abs(relCenter.z) + 250f) : 0f;

                    float avgHdg = Mathf.DeltaAngle(
                        pair.agent1.transform.eulerAngles.y,
                        pair.agent2.transform.eulerAngles.y) / 180f;

                    float spacing = Vector3.Distance(pair.agent1.transform.position, pair.agent2.transform.position);
                    float normalizedSpacing = spacing / (spacing + 50f); // 0~1

                    float assignedNorm = (float)pair.assignedTargetIndex / Mathf.Max(1, maxEnemies);
                    float deployed = pair.isDeployed ? 1f : 0f;

                    pairSensor.AppendObservation(new float[] { r, f, avgHdg, normalizedSpacing, assignedNorm, deployed });
                }
            }

            // BufferSensor: 적군 상태 (모선 기준 위치, 헤딩, 거리)
            if (enemySensor != null && envController.enemyShips != null)
            {
                Vector3 motherPos = envController.motherShip != null
                    ? envController.motherShip.transform.position : Vector3.zero;

                foreach (var enemy in envController.enemyShips)
                {
                    if (enemy == null || !enemy.activeInHierarchy) continue;

                    Vector3 rel = enemy.transform.position - motherPos;
                    float r = rel.x / (Mathf.Abs(rel.x) + 250f);
                    float f = rel.z / (Mathf.Abs(rel.z) + 250f);
                    float hdg = enemy.transform.eulerAngles.y / 360f;
                    float dist = rel.magnitude / (rel.magnitude + 500f);

                    enemySensor.AppendObservation(new float[] { r, f, hdg, dist });
                }
            }
        }

        /// <summary>
        /// 이산 액션: 각 페어에 대해 (타겟, 스폰포인트) 2 브랜치
        /// 브랜치 i*2: 타겟 (0=대기, 1~maxEnemies=특정 적)
        /// 브랜치 i*2+1: 스폰포인트 (0~numSpawnPoints-1)
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (envController == null) return;

            var pairs = envController.defensePairs;
            int activeEnemies = 0;
            if (envController.enemyShips != null)
            {
                foreach (var e in envController.enemyShips)
                    if (e != null && e.activeInHierarchy) activeEnemies++;
            }

            // 분산 배정 추적
            var assignedTargets = new System.Collections.Generic.HashSet<int>();
            int unassignedCount = 0;

            for (int pi = 0; pi < pairs.Count && pi < envController.maxPairs; pi++)
            {
                var pair = pairs[pi];
                int targetAction = actions.DiscreteActions[pi * 2];         // 0=대기, 1+=타겟
                int spawnAction = actions.DiscreteActions[pi * 2 + 1];      // 스폰포인트

                // 타겟 배정
                if (targetAction > 0 && targetAction <= maxEnemies)
                {
                    pair.assignedTargetIndex = targetAction; // 1-indexed
                    assignedTargets.Add(targetAction);

                    // 에이전트에도 전파
                    if (pair.agent1 != null) pair.agent1.assignedTargetIndex = targetAction;
                    if (pair.agent2 != null) pair.agent2.assignedTargetIndex = targetAction;
                }
                else
                {
                    pair.assignedTargetIndex = -1;
                    if (pair.agent1 != null) pair.agent1.assignedTargetIndex = -1;
                    if (pair.agent2 != null) pair.agent2.assignedTargetIndex = -1;

                    // 적이 있는데 대기 중이면 페널티
                    if (activeEnemies > 0 && pair.isDeployed)
                        unassignedCount++;
                }

                // 스폰 처리는 Phase 2 후반에서 구현 (현재는 타겟 배정만)
                // TODO: spawnPoints 배열과 연동
            }

            // === Commander 전략 보상 ===

            // 1. 분산 배정 보상: 서로 다른 적에 배정된 비율
            if (activeEnemies > 0)
            {
                float coverage = (float)assignedTargets.Count / activeEnemies;
                AddReward(coverageRewardCoeff * coverage);
            }

            // 2. 미배정 페널티
            if (unassignedCount > 0)
            {
                AddReward(unassignedPenalty * unassignedCount);
            }
        }

        /// <summary>
        /// 이산 액션 브랜치 정의: maxPairs × 2 브랜치
        /// </summary>
        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            // 기본 구현: 모든 액션 허용
            // 필요 시 유효하지 않은 조합 마스킹 가능
        }

        /// <summary>
        /// Heuristic: 수동 테스트용 (모든 페어에 가장 가까운 적 배정)
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discreteActions = actionsOut.DiscreteActions;
            for (int i = 0; i < discreteActions.Length; i++)
                discreteActions[i] = 0;

            if (envController == null) return;

            // 각 페어에 순서대로 적 배정 (간단 휴리스틱)
            var pairs = envController.defensePairs;
            for (int pi = 0; pi < pairs.Count && pi < envController.maxPairs; pi++)
            {
                // 타겟: 페어 인덱스+1 (1부터 시작, 적 수 이하)
                int targetIdx = Mathf.Min(pi + 1, maxEnemies);
                discreteActions[pi * 2] = targetIdx;
                // 스폰: 0 (기본 스폰 포인트)
                discreteActions[pi * 2 + 1] = 0;
            }
        }

        private void FixedUpdate()
        {
            _stepCounter++;

            // 결정 주기마다 RequestDecision
            if (_stepCounter % decisionPeriod == 0)
            {
                RequestDecision();
            }
        }
    }
}
