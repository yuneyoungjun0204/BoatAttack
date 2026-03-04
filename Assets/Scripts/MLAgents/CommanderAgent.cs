using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

namespace BoatAttack
{
    /// <summary>
    /// 전략 에이전트 (Commander): 아군 쌍 배치 시점/위치/매칭 결정
    /// BehaviorName: "Commander" (PPO, 독립 학습)
    /// DecisionRequester: DecisionPeriod=50 (Inspector 설정)
    ///
    /// 행동 (3 이산 브랜치):
    ///   Branch0 [11]: zone(0-9) or 대기(10)
    ///   Branch1 [6]:  배치 수(0-5)
    ///   Branch2 [11]: 타겟 적군(0-9) or 유지(10)
    ///
    /// 관측:
    ///   VectorSensor(4): 전역 상태
    ///   BufferSensor#1(max10, 2/entity): 적군 정보
    ///   BufferSensor#2(max10, 3/entity): 아군쌍 정보
    /// </summary>
    public class CommanderAgent : Agent
    {
        [Header("References")]
        public DefenseEnvController envController;
        public LaunchZoneManager launchZoneManager;

        [Header("BufferSensors")]
        [Tooltip("적군 가변 관측용 BufferSensor (Inspector에서 할당, obsSize=2, max=10)")]
        public BufferSensorComponent enemyBufferSensor;

        [Tooltip("아군 쌍 가변 관측용 BufferSensor (Inspector에서 할당, obsSize=3, max=10)")]
        public BufferSensorComponent pairBufferSensor;

        [Header("Observation NormK")]
        [Tooltip("거리 정규화 감도 (dist/(dist+k))")]
        [Range(50f, 1000f)]
        public float distNormK = 500f;

        [Header("Reward Coefficients")]
        [Tooltip("미교전 적 접근 페널티 (매 스텝)")]
        public float unengagedApproachPenalty = -0.001f;

        [Tooltip("시간 페널티 (매 스텝)")]
        public float timePenalty = -0.00005f;

        [Tooltip("배치 적정비율 보상 계수 (coeff / (|enemies/3 - deployed| + 1))")]
        public float deployEfficiencyCoeff = 0.3f;

        [Tooltip("중복 타겟 배정 페널티 계수")]
        public float duplicateTargetPenalty = -0.1f;

        [Tooltip("모선 도달 적 페널티 계수 (에피소드 종료 시)")]
        public float breachedPenaltyCoeff = 0.5f;

        [Tooltip("아군 낭비 페널티 계수 (에피소드 종료 시)")]
        public float wastedPairPenaltyCoeff = 0.3f;

        [Tooltip("적아 균형 보상 계수 (에피소드 종료 시)")]
        public float balanceRewardCoeff = 1.0f;

        [Tooltip("전승 보너스 (에피소드 종료 시 적 전멸)")]
        public float victoryBonus = 2.0f;

        [Header("Debug (Read Only)")]
        [SerializeField] private float _cumulativeReward = 0f;
        [SerializeField] private int _deployCount = 0;
        [SerializeField] private int _totalDeployed = 0;

        private bool _episodeEnded = false;

        // 포획 추적 (에피소드 종료 보상용)
        private int _capturedEnemies = 0;

        #region ML-Agents Lifecycle

        /// <summary>
        /// non-Commander Stage: Agent 등록 전에 자신을 비활성화
        /// Commander Stage: base.Awake() 호출하여 정상 초기화
        /// </summary>
        protected override void Awake()
        {
            if (envController == null || !envController.IsCommanderStage())
            {
                Debug.Log($"[CommanderAgent] Disabling self (stage={envController?.currentStage})");
                // gameObject.SetActive(false)는 Agent.OnDisable→CleanupSensors NullRef 유발
                // Agent + BehaviorParameters 비활성화 → trainer 등록 차단
                enabled = false;
                var bp = GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                if (bp != null) bp.enabled = false;
                return;
            }
            base.Awake();
        }

        public override void OnEpisodeBegin()
        {
            _episodeEnded = false;
            _cumulativeReward = 0f;
            _deployCount = 0;
            _totalDeployed = 0;
            _capturedEnemies = 0;
        }

        /// <summary>
        /// DefenseEnvController.ResetScene()에서 호출
        /// </summary>
        public void OnEpisodeReset()
        {
            _episodeEnded = false;
            _cumulativeReward = 0f;
            _deployCount = 0;
            _totalDeployed = 0;
            _capturedEnemies = 0;
        }

        public void SetEpisodeEnded()
        {
            _episodeEnded = true;
        }

        /// <summary>
        /// 적 포획 시 호출 (EnvController에서 라우팅)
        /// </summary>
        public void OnEnemyCaptured()
        {
            _capturedEnemies++;
        }

        #endregion

        #region Observations

        public override void CollectObservations(VectorSensor sensor)
        {
            if (envController == null || launchZoneManager == null
                || !envController.IsCommanderStage())
            {
                // 안전 padding (비-Commander 스테이지 포함)
                for (int i = 0; i < 4; i++) sensor.AddObservation(0f);
                // BufferSensor는 AppendObservation 호출 안 하면 자동 zero-pad
                return;
            }

            // === VectorSensor (4) ===

            // 0: 에피소드 진행도
            float progress = envController.maxEnvironmentSteps > 0
                ? envController.CurrentStep / (float)envController.maxEnvironmentSteps
                : 0f;
            sensor.AddObservation(progress);

            // 1: 활성 적군 비율
            int activeEnemies = envController.GetActiveEnemyCount();
            sensor.AddObservation(envController.poolSize > 0
                ? activeEnemies / (float)envController.poolSize
                : 0f);

            // 2: 활성 아군쌍 비율
            int activePairs = launchZoneManager.GetActivePairCount();
            sensor.AddObservation(launchZoneManager.maxPairCount > 0
                ? activePairs / (float)launchZoneManager.maxPairCount
                : 0f);

            // 3: 배치가능 쌍 비율
            int availablePairs = launchZoneManager.GetInactivePairCount();
            sensor.AddObservation(launchZoneManager.maxPairCount > 0
                ? availablePairs / (float)launchZoneManager.maxPairCount
                : 0f);

            // === BufferSensor #1: 적군 (max 10, obsSize=2) ===
            if (enemyBufferSensor != null && envController.motherShip != null)
            {
                Vector3 motherPos = envController.motherShip.transform.position;

                for (int i = 0; i < envController.poolSize; i++)
                {
                    GameObject enemy = envController.GetPooledEnemy(i);
                    if (enemy == null || !enemy.activeSelf || envController.IsEnemyNeutralized(enemy))
                        continue;

                    // 적군당 2개 관측: closest zone index (정규화), closest zone distance (정규화)
                    var (closestZone, closestDist) = launchZoneManager.GetClosestZoneToPosition(
                        enemy.transform.position, motherPos);

                    float zoneNorm = launchZoneManager.zoneCount > 0
                        ? closestZone / (float)launchZoneManager.zoneCount
                        : 0f;
                    float distNorm = closestDist / (closestDist + distNormK);

                    enemyBufferSensor.AppendObservation(new float[] { zoneNorm, distNorm });
                }
            }

            // === BufferSensor #2: 아군쌍 (max 10, obsSize=3) ===
            if (pairBufferSensor != null && envController.motherShip != null)
            {
                Vector3 motherPos = envController.motherShip.transform.position;
                int poolCount = launchZoneManager.GetCurrentPoolCount();

                for (int i = 0; i < poolCount; i++)
                {
                    DefensePair pair = launchZoneManager.GetPair(i);
                    if (pair == null || !pair.isActive) continue;
                    if (pair.agent1 == null || pair.agent2 == null) continue;

                    // 쌍 중심 위치
                    Vector3 center = (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f;

                    // obs 0: 배치된 zone 인덱스 (정규화)
                    float zoneNorm = launchZoneManager.zoneCount > 0
                        ? pair.assignedZoneIndex / (float)launchZoneManager.zoneCount
                        : 0f;

                    // obs 1: 배정된 타겟 적군 (정규화, -1=auto → 0)
                    int targetIdx = pair.agent1.assignedTargetIndex;
                    float targetNorm = (targetIdx > 0 && envController.poolSize > 0)
                        ? (targetIdx - 1) / (float)envController.poolSize  // 1-indexed → 0-indexed 정규화
                        : 0f;

                    // obs 2: 모선까지 거리 (정규화)
                    float dist = Vector3.Distance(center, motherPos);
                    float distNorm = dist / (dist + distNormK);

                    pairBufferSensor.AppendObservation(new float[] { zoneNorm, targetNorm, distNorm });
                }
            }
        }

        #endregion

        #region Actions

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_episodeEnded || envController == null || launchZoneManager == null
                || !envController.IsCommanderStage()) return;

            int zoneAction = actions.DiscreteActions[0];   // 0-9: zone, 10: 대기
            int countAction = actions.DiscreteActions[1];   // 0-5: 배치 수
            int targetAction = actions.DiscreteActions[2];  // 0-9: 타겟 적군, 10: 유지

            bool isWait = (zoneAction == 10 || countAction == 0);

            if (isWait)
            {
                // 대기 모드: Branch2로 가장 최근 활성 쌍의 타겟 재배정
                if (targetAction < 10)
                {
                    ReassignLatestPairTarget(targetAction);
                }
                return;
            }

            // 배치 모드: centerZone 기준 N개 인접 빈 zone에 쌍 배치
            int[] zones = launchZoneManager.GetNearestAvailableZones(zoneAction, countAction);
            if (zones.Length == 0) return;

            int deployed = 0;
            int firstDeployedPairIdx = -1;

            for (int i = 0; i < zones.Length; i++)
            {
                bool success = envController.SpawnAllyPair(zones[i]);
                if (success)
                {
                    deployed++;
                    if (firstDeployedPairIdx < 0)
                    {
                        // 가장 최근 배치된 쌍 인덱스 찾기
                        firstDeployedPairIdx = FindLastActivePairIndex();
                    }
                }
            }

            if (deployed > 0)
            {
                _deployCount++;
                _totalDeployed += deployed;

                // 첫 쌍: Commander가 지정한 타겟에 매칭
                if (firstDeployedPairIdx >= 0 && targetAction < 10)
                {
                    int targetIdx1Based = targetAction + 1; // 0-indexed → 1-indexed
                    launchZoneManager.SetPairTarget(firstDeployedPairIdx, targetIdx1Based);

                    // 중복 타겟 페널티: 같은 적에 이미 배정된 쌍이 있으면
                    int duplicates = CountPairsTargeting(targetIdx1Based) - 1;
                    if (duplicates > 0)
                        AddReward(duplicates * duplicateTargetPenalty);
                }

                // 나머지 쌍: 자동 매칭 (가장 가까운 미교전 적)
                AutoAssignUnmatchedPairs();

                // 배치 결정 보상: coeff / (|totalEnemies/3 - deployedNow| + 1)
                int activeEnemies = envController.GetActiveEnemyCount();
                float idealDeploy = activeEnemies / 3f;
                float deployReward = deployEfficiencyCoeff / (Mathf.Abs(idealDeploy - deployed) + 1f);
                AddReward(deployReward);
            }
        }

        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            if (envController == null || launchZoneManager == null) return;

            int availablePairs = launchZoneManager.GetInactivePairCount();
            int availableZones = launchZoneManager.GetAvailableZoneCount();
            bool canDeploy = availablePairs > 0 && availableZones > 0;

            // === Branch 0: zone 선택 (0-9) + 대기(10) ===
            if (!canDeploy)
            {
                // 배치 불가: 모든 zone 마스킹, 대기만 허용
                for (int z = 0; z < 10; z++)
                    actionMask.SetActionEnabled(0, z, false);
                // 10(대기)은 항상 허용
            }
            else
            {
                // 점유된 zone 마스킹
                int zoneCount = launchZoneManager.zoneCount;
                for (int z = 0; z < 10; z++)
                {
                    if (z < zoneCount && launchZoneManager.IsZoneOccupied(z))
                        actionMask.SetActionEnabled(0, z, false);
                    else if (z >= zoneCount)
                        actionMask.SetActionEnabled(0, z, false); // 존재하지 않는 zone
                }
            }

            // === Branch 1: 배치 수 (0-5) ===
            // 가용 쌍/빈 zone 수를 초과하는 값 마스킹
            int maxDeployable = Mathf.Min(availablePairs, availableZones);
            for (int c = 1; c <= 5; c++)
            {
                if (c > maxDeployable)
                    actionMask.SetActionEnabled(1, c, false);
            }
            // 0(미배치)은 항상 허용

            // === Branch 2: 타겟 적군 (0-9) + 유지(10) ===
            for (int e = 0; e < 10; e++)
            {
                if (e < envController.poolSize)
                {
                    GameObject enemy = envController.GetPooledEnemy(e);
                    bool valid = enemy != null && enemy.activeSelf && !envController.IsEnemyNeutralized(enemy);
                    if (!valid)
                        actionMask.SetActionEnabled(2, e, false);
                }
                else
                {
                    actionMask.SetActionEnabled(2, e, false); // poolSize 범위 초과
                }
            }
            // 10(유지)은 항상 허용
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var d = actionsOut.DiscreteActions;

            // 기본: 대기
            d[0] = 10; // 대기
            d[1] = 0;  // 미배치
            d[2] = 10; // 유지

            if (envController == null || launchZoneManager == null) return;

            int activeEnemies = envController.GetActiveEnemyCount();
            int availablePairs = launchZoneManager.GetInactivePairCount();
            int availableZones = launchZoneManager.GetAvailableZoneCount();

            if (activeEnemies > 0 && availablePairs > 0 && availableZones > 0)
            {
                // 적 접근 방향 가장 가까운 zone
                float approachAngle = envController.GetCurrentEnemyApproachAngle();
                int bestZone = launchZoneManager.FindClosestZone(approachAngle);

                // 점유되어 있으면 인접 빈 zone 찾기
                if (launchZoneManager.IsZoneOccupied(bestZone))
                {
                    int[] available = launchZoneManager.GetNearestAvailableZones(bestZone, 1);
                    if (available.Length > 0)
                        bestZone = available[0];
                    else
                        return; // 배치 불가
                }

                d[0] = bestZone;

                // 배치 수: min(가용쌍, 빈zone, 3)
                int count = Mathf.Min(availablePairs, availableZones, 3);
                d[1] = count;

                // 타겟: 가장 가까운 미교전 적
                int closestEnemy = FindClosestUnengagedEnemy(bestZone);
                d[2] = closestEnemy >= 0 ? closestEnemy : 10;
            }
        }

        #endregion

        #region Reward Helpers

        /// <summary>
        /// FixedUpdate에서 매 스텝 호출 (EnvController에서 호출)
        /// 미교전 적 접근 페널티 + 시간 페널티
        /// </summary>
        public void CalculateStepReward()
        {
            if (_episodeEnded || envController == null) return;

            float reward = 0f;

            // 미교전 적 접근 페널티
            if (envController.motherShip != null)
            {
                Vector3 motherPos = envController.motherShip.transform.position;

                for (int i = 0; i < envController.poolSize; i++)
                {
                    GameObject enemy = envController.GetPooledEnemy(i);
                    if (enemy == null || !enemy.activeSelf || envController.IsEnemyNeutralized(enemy))
                        continue;
                    if (IsEnemyEngaged(i)) continue; // 교전 중이면 스킵

                    float dist = Vector3.Distance(enemy.transform.position, motherPos);
                    float normalizedDist = dist / (dist + distNormK);
                    reward += unengagedApproachPenalty * (1f - normalizedDist);
                }
            }

            // 시간 페널티
            reward += timePenalty;

            if (Mathf.Abs(reward) > 0.000001f)
                AddReward(reward);
        }

        /// <summary>
        /// 에피소드 종료 시 종합 보상 계산 (EnvController에서 호출)
        /// </summary>
        public void CalculateEpisodeEndReward()
        {
            if (envController == null) return;

            float reward = 0f;

            // 1. 모선 도달 적 페널티: -(breachedCount × coeff)
            int breached = envController.GetBreachedEnemyCount();
            reward -= breached * breachedPenaltyCoeff;

            // 2. 아군 낭비 페널티: 배치됐지만 포획 0인 쌍
            int wastedPairs = CalculateWastedPairs();
            reward -= wastedPairs * wastedPairPenaltyCoeff;

            // 3. 전체 효율: 1 / (|totalEnemies - totalAllies| + 1)
            int totalEnemies = envController.poolSize > 0
                ? Mathf.Min(envController.poolSize, GetStageEnemyCount())
                : 0;
            reward += balanceRewardCoeff / (Mathf.Abs(totalEnemies - _totalDeployed) + 1f);

            // 4. 전승 보너스
            if (breached == 0 && envController.GetActiveEnemyCount() == 0 && totalEnemies > 0)
                reward += victoryBonus;

            AddReward(reward);
        }

        #endregion

        #region Internal Helpers

        /// <summary>
        /// 특정 적군이 어떤 활성 쌍에 매칭되어 있는지 확인 (0-indexed)
        /// </summary>
        public bool IsEnemyEngaged(int enemyPoolIndex)
        {
            int target1Indexed = enemyPoolIndex + 1;
            int poolCount = launchZoneManager.GetCurrentPoolCount();
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                if (pair.agent1 != null && pair.agent1.assignedTargetIndex == target1Indexed)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 특정 타겟(1-indexed)에 매칭된 활성 쌍 수
        /// </summary>
        private int CountPairsTargeting(int target1Indexed)
        {
            int count = 0;
            int poolCount = launchZoneManager.GetCurrentPoolCount();
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                if (pair.agent1 != null && pair.agent1.assignedTargetIndex == target1Indexed)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 가장 최근 활성화된 쌍 인덱스 반환 (deployStep 기준)
        /// </summary>
        private int FindLastActivePairIndex()
        {
            int bestIdx = -1;
            int bestStep = -1;
            int poolCount = launchZoneManager.GetCurrentPoolCount();
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                if (pair.deployStep > bestStep)
                {
                    bestStep = pair.deployStep;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// 타겟 미배정(-1) 쌍에 가장 가까운 미교전 적 자동 배정
        /// </summary>
        private void AutoAssignUnmatchedPairs()
        {
            int poolCount = launchZoneManager.GetCurrentPoolCount();
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                if (pair.agent1 == null) continue;
                if (pair.agent1.assignedTargetIndex > 0) continue; // 이미 배정됨

                int bestEnemy = FindClosestUnengagedEnemyForPair(pair);
                if (bestEnemy >= 0)
                    launchZoneManager.SetPairTarget(i, bestEnemy + 1); // 1-indexed
            }
        }

        /// <summary>
        /// 대기 시 가장 최근 활성 쌍의 타겟 재배정
        /// </summary>
        private void ReassignLatestPairTarget(int targetAction)
        {
            int latestIdx = FindLastActivePairIndex();
            if (latestIdx >= 0)
            {
                int target1Indexed = targetAction + 1;
                launchZoneManager.SetPairTarget(latestIdx, target1Indexed);
            }
        }

        /// <summary>
        /// 가장 가까운 미교전 적 (zone 기준, 0-indexed 반환, -1=없음)
        /// </summary>
        private int FindClosestUnengagedEnemy(int zoneIndex)
        {
            if (envController.motherShip == null) return -1;

            Vector3 motherPos = envController.motherShip.transform.position;
            float zoneAngleRad = 0f;
            if (launchZoneManager.launchZones != null && zoneIndex < launchZoneManager.launchZones.Length)
                zoneAngleRad = launchZoneManager.launchZones[zoneIndex].angleDeg * Mathf.Deg2Rad;

            Vector3 zoneDir = new Vector3(Mathf.Sin(zoneAngleRad), 0f, Mathf.Cos(zoneAngleRad));
            float zoneDist = launchZoneManager.GetEllipseDistance(
                launchZoneManager.launchZones != null && zoneIndex < launchZoneManager.launchZones.Length
                    ? launchZoneManager.launchZones[zoneIndex].angleDeg : 0f);
            Vector3 zoneWorldPos = motherPos + zoneDir * zoneDist;
            float zoneDistToMother = Vector3.Distance(zoneWorldPos, motherPos);

            float minDist = float.MaxValue;
            int bestIdx = -1;

            for (int i = 0; i < envController.poolSize; i++)
            {
                GameObject enemy = envController.GetPooledEnemy(i);
                if (enemy == null || !enemy.activeSelf || envController.IsEnemyNeutralized(enemy))
                    continue;
                if (IsEnemyEngaged(i)) continue;

                // 적이 진수구역(아군 배치 위치)보다 모선에서 더 멀면 매칭 불가
                float enemyDistToMother = Vector3.Distance(enemy.transform.position, motherPos);
                if (enemyDistToMother > zoneDistToMother) continue;

                float dist = Vector3.Distance(enemy.transform.position, zoneWorldPos);
                if (dist < minDist)
                {
                    minDist = dist;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// 쌍 위치 기준 가장 가까운 미교전 적 (0-indexed 반환, -1=없음)
        /// </summary>
        private int FindClosestUnengagedEnemyForPair(DefensePair pair)
        {
            if (pair.agent1 == null || pair.agent2 == null) return -1;
            if (envController.motherShip == null) return -1;

            Vector3 center = (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f;
            Vector3 motherPos = envController.motherShip.transform.position;
            float pairDistToMother = Vector3.Distance(center, motherPos);

            float minDist = float.MaxValue;
            int bestIdx = -1;

            for (int i = 0; i < envController.poolSize; i++)
            {
                GameObject enemy = envController.GetPooledEnemy(i);
                if (enemy == null || !enemy.activeSelf || envController.IsEnemyNeutralized(enemy))
                    continue;
                if (IsEnemyEngaged(i)) continue;

                // 적이 아군보다 모선에서 더 멀면 매칭 불가
                float enemyDistToMother = Vector3.Distance(enemy.transform.position, motherPos);
                if (enemyDistToMother > pairDistToMother) continue;

                float dist = Vector3.Distance(enemy.transform.position, center);
                if (dist < minDist)
                {
                    minDist = dist;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// 낭비된 쌍 수 계산: 배치됐지만 포획에 기여하지 않은 쌍
        /// (단순 추정: 총 배치 - 포획 수, 최소 0)
        /// </summary>
        private int CalculateWastedPairs()
        {
            // 포획한 적보다 많이 배치했으면 그 차이가 "낭비"
            return Mathf.Max(0, _totalDeployed - _capturedEnemies);
        }

        /// <summary>
        /// 현재 Stage의 적군 수 반환
        /// </summary>
        private int GetStageEnemyCount()
        {
            if (envController == null) return 0;
            // 현재 활성화된 적군 수 기준 (poolSize 이하)
            switch (envController.currentStage)
            {
                case TrainingStage.Stage4_Commander:
                    return envController.stage4EnemyCount;
                case TrainingStage.Stage5_FullScale:
                    return envController.stage5EnemyCount;
                default:
                    return envController.stage3EnemyCount;
            }
        }

        #endregion
    }
}
