using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.Barracuda;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 가상 아군 쌍: agent1+agent2 위치를 가진 PhantomPair
    /// Stage6에서 실제 아군 쌍이 없을 때 대형 학습용으로 사용
    /// </summary>
    [System.Serializable]
    public class PhantomPair
    {
        public Vector3 agent1Pos;
        public Vector3 agent2Pos;
        public float heading;
        public float noiseSeed1;
        public float noiseSeed2;
        public float speedMult;     // 개별 속도 배율 (각 쌍마다 랜덤)
        public bool isValid;
        public bool hasWeb;         // 가상 그물 유무 (관측용, 적에게는 영향 없음)
        public bool isStopped;      // 가다가 멈춘 상태
        public bool willStop;       // 중간에 멈출 예정
        public float stopTime;      // 멈출 시각 (Time.time 기준)

        public Vector3 WebCenter => (agent1Pos + agent2Pos) * 0.5f;
        public float WebLength => Vector3.Distance(agent1Pos, agent2Pos);
    }

    /// <summary>
    /// 방어 에이전트: 2대가 협력하여 적군 선박을 web 사이로 유도
    /// 상대 좌표 기반 관측, 보상은 DefenseEnvController에서 그룹 보상으로 분배
    /// </summary>
    public class DefenseAgent : Agent
    {
        [Header("Boat Components")]
        private Boat _boat;
        public Engine _engine;

        [Header("Observation Settings")]
        [Tooltip("관측할 적군 선박들 (인스펙터에서 할당)")]
        public GameObject[] enemyShips = new GameObject[5];

        [Tooltip("최대 관측 가능한 적군 수")]
        public int maxEnemyCount = 5;

        [Header("Target Settings")]
        public DefenseAgent partnerAgent;        public GameObject motherShip;
        public string motherShipTag = "MotherShip";
        public GameObject webObject;

        [Header("Hierarchical Control")]
        [Tooltip("배정된 타겟 인덱스 (-1=자동/가장 가까운 적, 1+=특정 적 1-indexed)")]
        [HideInInspector] public int assignedTargetIndex = -1;

        [Tooltip("환경 컨트롤러 참조 (Start에서 자동 설정)")]
        [HideInInspector] public DefenseEnvController envController;

        [Header("BufferSensor")]
        [Tooltip("적군 가변 관측용 BufferSensor (Inspector에서 할당)")]
        public BufferSensorComponent enemyBufferSensor;

        [Tooltip("아군 쌍 가변 관측용 BufferSensor (자동 생성)")]
        public BufferSensorComponent allyBufferSensor;

        [Header("Action Settings")]
        public float maxLinearVelocity = 200f;
        public float maxAngularVelocity = 90f;
        public float velocityControlGain = 2.5f;
        public float angularVelocityControlGain = 1.0f;
        public float maxLinearAcceleration = 12f;
        public float maxAngularAcceleration = 45f;

        [Header("Control Settings")]
        [Range(0f, 1.5f)]
        [Tooltip("최대 Throttle (기본 전진 속도)")]
        public float maxThrottle = 1.0f;

        [Range(0f, 1f)]
        [Tooltip("최소 Throttle (action=0일 때 최소 전진력)")]
        public float minThrottle = 0.3f;

        [Range(0.1f, 2.0f)]
        public float steeringSensitivity = 1.0f;

        [Range(1f, 50f)]
        [Tooltip("아군 선박 horsePower 배율 (Engine.horsePower에 곱함)")]
        public float speedMultiplier = 10f;

        [Range(0.01f, 1.0f)]
        [Tooltip("입력 스무스 처리 (1.0 = 즉각 반응)")]
        public float inputSmoothing = 1.0f;

        [Header("Observation NormK (출력 0.5 지점 거리)")]
        [Range(1f, 1000f)] public float partnerNormK = 50f;
        [Range(1f, 1000f)] public float enemyNormK = 250f;
        [Range(1f, 1000f)] public float motherNormK = 500f;

        [Header("Observation Scale (정규화 후 가중치)")]
        [Range(0f, 5f)] public float partnerRScale = 1f;
        [Range(0f, 5f)] public float partnerFScale = 1f;
        [Range(0f, 5f)] public float partnerDistScale = 1f;
        [Range(0f, 5f)] public float partnerHdgScale = 1f;
        [Range(0f, 5f)] public float enemyRScale = 1f;
        [Range(0f, 5f)] public float enemyFScale = 1f;
        [Range(0f, 5f)] public float enemyDistScale = 1f;
        [Range(0f, 5f)] public float enemyHdgScale = 1f;
        [Range(0f, 5f)] public float motherDistScale = 1f;

        [Header("Self State (자기 기동 상태)")]
        [Range(1f, 50f)] public float speedNormK = 10f;
        [Range(0f, 5f)] public float forwardSpeedScale = 1f;
        [Range(0f, 5f)] public float driftSpeedScale = 1f;

        [Header("Ally Pair Observation (좌/우 가장 가까운 아군 쌍)")]
        [Range(1f, 1000f)] public float allyPairNormK = 100f;

        [Header("Phantom Neighbors (Stage6: 좌3+우3 = 6쌍)")]
        [Tooltip("가상 아군쌍 간격 (방어선 방향, m)")]
        [Range(30f, 200f)] public float phantomSpacing = 80f;

        [Tooltip("가상 아군쌍 전진 속도 (m/s, fallback)")]
        [Range(1f, 50f)] public float phantomSpeed = 8f;

        [Tooltip("가상 아군쌍 속도 배율 (실제 아군 속도 × 배율, 에피소드마다 랜덤)")]
        [HideInInspector] public float phantomSpeedMult = 1.5f;

        [Tooltip("가상 아군쌍 좌우 노이즈 강도 (m/s)")]
        [Range(0f, 10f)] public float phantomNoiseStrength = 3f;

        [Tooltip("가상 아군쌍 내부 그물 반폭 (agent1↔agent2 사이 절반 거리, m)")]
        [Range(5f, 50f)] public float phantomWebHalfWidth = 25f;

        // Phantom 상태 (최대 10쌍, 실제 수는 에피소드마다 5~10 랜덤)
        public const int PHANTOM_MAX = 10;
        [HideInInspector] public PhantomPair[] phantomPairs;
        [HideInInspector] public int phantomCount;  // 이번 에피소드 실제 phantom 수
        [HideInInspector] public bool phantomsInitialized;

        /// <summary>하위 호환: EnvController에서 phantom 유효성 확인</summary>
        public bool lastPhantomValid => phantomsInitialized && phantomPairs != null;

        [Header("Heuristic")]
        [Tooltip("true면 화살표키, false면 WASD (페어 배치 시 자동 설정)")]
        public bool useArrowKeys = false;

        [Header("Debug")]
        public bool showRaycasts = true;
        public bool enableDebugLog = false;

        /// <summary>모니터링용: 전체 관측값 (VectorSensor + EnemyBuffer + AllyBuffer)</summary>
        [HideInInspector] public float[] lastObservations;
        /// <summary>모니터링용: 적군 버퍼 관측 임시 저장</summary>
        [HideInInspector] public List<float> lastEnemyBufferObs = new List<float>();
        /// <summary>모니터링용: 아군 버퍼 관측 임시 저장</summary>
        [HideInInspector] public List<float> lastAllyBufferObs = new List<float>();

        [Header("Reward Display")]
        #pragma warning disable CS0414
        [SerializeField] private float _totalReward = 0f;
        [SerializeField] private float _lastStepReward = 0f;
        #pragma warning restore CS0414

        private bool _episodeEnded = false;
        private bool _neutralized = false;
        private float _prevThrottle = 0f;
        private float _prevSteering = 0f;

        // 명령 변화량 (이전 스텝과의 차이, 보상 계산용)
        private float _throttleDelta = 0f;
        private float _steeringDelta = 0f;

        public float PrevThrottle => _prevThrottle;
        public float PrevSteering => _prevSteering;
        public float ThrottleDelta => _throttleDelta;
        public float SteeringDelta => _steeringDelta;

        private new void Awake()
        {
            if (TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
                if (_engine != null && speedMultiplier > 1f)
                    _engine.horsePower *= speedMultiplier;
            }

            // BufferSensor 자동 찾기 → 없으면 AddComponent → 크기 보정
            // 코드에서 AppendObservation(new float[] { r, f, d, h }) → 4개 값
            if (enemyBufferSensor == null)
                enemyBufferSensor = GetComponent<BufferSensorComponent>();
            if (enemyBufferSensor == null)
                enemyBufferSensor = gameObject.AddComponent<BufferSensorComponent>();
            enemyBufferSensor.SensorName = "EnemyBufferSensor";
            enemyBufferSensor.ObservableSize = 5;   // r, f, d, h, threat (responsible는 VectorSensor로 이동)
            enemyBufferSensor.MaxNumObservables = 3; // 가장 가까운 3개만

            // 아군 쌍 BufferSensor: 가변 개수 (실제 쌍 + phantom, 최대 PHANTOM_MAX+6)
            var buffers = GetComponents<BufferSensorComponent>();
            allyBufferSensor = buffers.Length > 1 ? buffers[1] : null;
            if (allyBufferSensor == null)
                allyBufferSensor = gameObject.AddComponent<BufferSensorComponent>();
            allyBufferSensor.SensorName = "AllyBufferSensor";
            allyBufferSensor.ObservableSize = 5;   // dist, fwd, right, hdg, webLength
            allyBufferSensor.MaxNumObservables = 3; // 가장 가까운 3쌍만
        }

        protected override void OnEnable()
        {
            // Stage4: DefenseAgent를 InferenceOnly로 전환 (base.OnEnable → LazyInitialize 전에 설정)
            if (envController != null && envController.IsCommanderStage()
                && envController.defenseOnnxModel != null)
            {
                var bp = GetComponent<BehaviorParameters>();
                if (bp != null)
                {
                    bp.BehaviorType = BehaviorType.InferenceOnly;
                    bp.Model = envController.defenseOnnxModel;
                }
            }

            base.OnEnable();

            if (motherShip == null)
            {
                // 멀티 환경 호환: 같은 환경 계층 내에서 먼저 찾기
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                var allWithTag = GameObject.FindGameObjectsWithTag(motherShipTag);
                foreach (var obj in allWithTag)
                {
                    if (obj != null && obj.transform.IsChildOf(envRoot))
                    {
                        motherShip = obj;
                        break;
                    }
                }
                // 환경 내에서 못 찾으면 글로벌 fallback
                if (motherShip == null && allWithTag.Length > 0)
                {
                    motherShip = allWithTag[0];
                }
            }
        }

        /// <summary>
        /// 무력화 상태 설정 (DisablePair에서 호출)
        /// true → OnActionReceived에서 엔진 구동 차단
        /// </summary>
        public void SetNeutralized(bool value) => _neutralized = value;

        /// <summary>
        /// 런타임 배치 시 에이전트 상태 리셋
        /// OnEpisodeBegin과 달리 ML-Agents 에피소드를 건드리지 않고 내부 플래그만 초기화
        /// </summary>
        public void ResetForDeployment()
        {
            _episodeEnded = false;
            _neutralized = false;
            assignedTargetIndex = -1; // Commander가 새로 배정
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
        }

        public override void OnEpisodeBegin()
        {
            _episodeEnded = false;
            assignedTargetIndex = -1; // Commander가 새로 배정
            // _neutralized는 여기서 리셋하지 않음
            // SetNeutralized(false)로만 해제 (DeployPairs/ResetScene에서 호출)
            _totalReward = 0f;
            _lastStepReward = 0f;
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
            phantomsInitialized = false;
        }

        /// <summary>
        /// 위치 정규화: x/(|x|+k) → (-1, 1)
        /// k = 감도 스케일 (출력 0.5 지점의 거리)
        /// </summary>
        private float NormalizePosition(float x, float k)
        {
            return x / (Mathf.Abs(x) + k);
        }

        /// <summary>
        /// 각도 정규화: sin(DeltaAngle) → [-1, 1]
        /// ±180° 부근 불연속 제거, ±90°에서 최대값
        /// </summary>
        private float NormalizeAngle(float fromAngle, float toAngle)
        {
            return Mathf.Sin(Mathf.DeltaAngle(fromAngle, toAngle) * Mathf.Deg2Rad);
        }

        /// <summary>
        /// 배정된 적군 반환 (Commander가 지정한 타겟 or 가장 가까운 적 fallback)
        /// 조건: 적군이 아군보다 모선에 더 가까워야 매칭 가능 (더 먼 적은 절대 매칭 불가)
        /// </summary>
        public GameObject GetAssignedEnemy()
        {
            // 배정된 타겟이 있으면 거리 무관하게 반환 (배정은 AutoAssign에서 주기적 갱신)
            if (assignedTargetIndex > 0 && enemyShips != null)
            {
                int idx = assignedTargetIndex - 1;
                if (idx < enemyShips.Length && enemyShips[idx] != null && enemyShips[idx].activeInHierarchy)
                {
                    return enemyShips[idx];
                }
                // 적이 비활성화된 경우에만 매칭 해제
                assignedTargetIndex = -1;
            }

            // Fallback: 가장 가까운 활성 적
            if (enemyShips == null) return null;
            float minDist = float.MaxValue;
            GameObject closest = null;
            Vector3 myPos = transform.position;
            foreach (var enemy in enemyShips)
            {
                if (enemy == null || !enemy.activeInHierarchy) continue;
                float dist = Vector3.Distance(myPos, enemy.transform.position);
                if (dist < minDist) { minDist = dist; closest = enemy; }
            }
            return closest;
        }

        /// <summary>
        /// 관측 수집 (VectorSensor 13개 + AllyBufferSensor 최대3 + EnemyBufferSensor 최대3)
        /// VectorSensor: 파트너(4) + 모선거리(1) + 자기상태(4) + 담당적(4) = 13
        /// AllyBufferSensor: 가까운 아군 쌍 최대 3개, 각 5개 (dist, fwd, right, hdg, webLength)
        /// EnemyBufferSensor: 가까운 적군 최대 3개, 각 5개 (R, F, Dist, Hdg, Threat)
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            // 13: 파트너(4) + 모선거리(1) + 자기상태(4) + 담당적(4: R,F,Dist,Threat)
            const int VECTOR_OBS_COUNT = 4 + 1 + 4 + 4;
            if (lastObservations == null || lastObservations.Length < VECTOR_OBS_COUNT)
                lastObservations = new float[VECTOR_OBS_COUNT];
            int oi = 0;

            if (_engine == null || _engine.RB == null)
            {
                for (int i = 0; i < VECTOR_OBS_COUNT; i++)
                {
                    sensor.AddObservation(0f);
                    lastObservations[i] = 0f;
                }
                return;
            }

            Vector3 myPos = transform.position;
            Vector3 myForward = transform.forward;
            Vector3 myRight = transform.right;
            float myAngle = transform.eulerAngles.y;

            // 1. 파트너 (4: R, F, Dist, Hdg) — 페어 내 고정 파트너
            if (partnerAgent != null && partnerAgent._engine != null && partnerAgent._engine.RB != null)
            {
                AddDirectionDistanceObs(sensor, partnerAgent.transform, myPos, myForward, myRight, myAngle,
                    partnerRScale, partnerFScale, partnerDistScale, partnerHdgScale, partnerNormK, ref oi);
            }
            else
            {
                for (int i = 0; i < 4; i++) AddObs(sensor, 0f, ref oi);
            }

            // 2. 모선 거리 (1: Dist만)
            if (motherShip != null)
            {
                float dist = Vector3.Distance(motherShip.transform.position, myPos);
                AddObs(sensor, NormalizePosition(dist, motherNormK) * motherDistScale, ref oi);
            }
            else
            {
                AddObs(sensor, 0f, ref oi);
            }

            // 3. 자기 기동 상태 (4: prevThrottle, prevSteering, driftSpeed, forwardSpeed)
            AddObs(sensor, _prevThrottle, ref oi);
            AddObs(sensor, _prevSteering, ref oi);
            AddObs(sensor, NormalizePosition(_engine.DriftSpeed, speedNormK) * driftSpeedScale, ref oi);
            float forwardSpeed = Vector3.Dot(_engine.RB.velocity, myForward);
            AddObs(sensor, NormalizePosition(forwardSpeed, speedNormK) * forwardSpeedScale, ref oi);

            // === 공용 변수 (담당적 + EnemyBuffer 공유) ===
            Vector3 myWebCenter = (partnerAgent != null)
                ? (myPos + partnerAgent.transform.position) * 0.5f
                : myPos;
            var lzm = envController != null ? envController.launchZoneManager : null;
            int poolCount = lzm != null ? lzm.GetCurrentPoolCount() : 0;
            float spawnDist = envController != null ? envController.enemySpawnDistance : 1000f;
            Vector3 motherPos = motherShip != null ? motherShip.transform.position : Vector3.zero;

            // 4. 담당 적 (Voronoi responsible) → VectorSensor (4: R, F, Dist, Threat)
            {
                int respIdx = -1;
                if (lzm != null && enemyShips != null)
                {
                    int myPairIdx = lzm.FindPairIndex(this);
                    respIdx = lzm.GetClosestResponsibleEnemy(myPairIdx, myWebCenter, enemyShips);
                }

                if (respIdx >= 0 && enemyShips[respIdx] != null && enemyShips[respIdx].activeInHierarchy)
                {
                    var respEnemy = enemyShips[respIdx];
                    Vector3 rel = respEnemy.transform.position - myPos;
                    float dist = rel.magnitude;
                    float rightDot = Vector3.Dot(rel, myRight);
                    float fwdDot = Vector3.Dot(rel, myForward);

                    AddObs(sensor, (dist > 0.1f) ? (rightDot / dist) * enemyRScale : 0f, ref oi);
                    AddObs(sensor, (dist > 0.1f) ? (fwdDot / dist) * enemyFScale : 0f, ref oi);
                    AddObs(sensor, NormalizePosition(dist, enemyNormK) * enemyDistScale, ref oi);

                    float enemyDistToMother = motherShip != null
                        ? Vector3.Distance(respEnemy.transform.position, motherPos) : spawnDist;
                    float threat = 1f - Mathf.Clamp01(enemyDistToMother / Mathf.Max(1f, spawnDist));
                    AddObs(sensor, threat, ref oi);
                }
                else
                {
                    for (int i = 0; i < 4; i++) AddObs(sensor, 0f, ref oi);
                }
            }

            // 5. AllyBufferSensor: 가까운 아군 쌍 최대 3개
            CollectAllyPairBufferObs(myPos, myForward, myRight, myAngle);

            // 6. EnemyBufferSensor: 가까운 적군 최대 3개 (5개/적: R, F, Dist, Hdg, Threat)
            lastEnemyBufferObs.Clear();
            if (enemyBufferSensor != null && enemyShips != null)
            {
                // 활성 적군을 거리순 정렬
                var enemyByDist = new List<(int idx, float dist)>();
                for (int i = 0; i < enemyShips.Length; i++)
                {
                    if (enemyShips[i] == null || !enemyShips[i].activeInHierarchy) continue;
                    float d = Vector3.Distance(myPos, enemyShips[i].transform.position);
                    enemyByDist.Add((i, d));
                }
                enemyByDist.Sort((a, b) => a.dist.CompareTo(b.dist));

                // 가까운 3개만 Buffer에 추가
                int count = Mathf.Min(enemyByDist.Count, 3);
                for (int ei = 0; ei < count; ei++)
                {
                    var enemy = enemyShips[enemyByDist[ei].idx];
                    Vector3 rel = enemy.transform.position - myPos;
                    float dist = rel.magnitude;
                    float rightDot = Vector3.Dot(rel, myRight);
                    float fwdDot = Vector3.Dot(rel, myForward);

                    float r = (dist > 0.1f) ? (rightDot / dist) * enemyRScale : 0f;
                    float f = (dist > 0.1f) ? (fwdDot / dist) * enemyFScale : 0f;
                    float d = NormalizePosition(dist, enemyNormK) * enemyDistScale;
                    float h = NormalizeAngle(myAngle, enemy.transform.eulerAngles.y) * enemyHdgScale;

                    float enemyDistToMother = motherShip != null
                        ? Vector3.Distance(enemy.transform.position, motherPos) : spawnDist;
                    float threat = 1f - Mathf.Clamp01(enemyDistToMother / Mathf.Max(1f, spawnDist));

                    enemyBufferSensor.AppendObservation(new float[] { r, f, d, h, threat });
                    lastEnemyBufferObs.Add(r); lastEnemyBufferObs.Add(f);
                    lastEnemyBufferObs.Add(d); lastEnemyBufferObs.Add(h);
                    lastEnemyBufferObs.Add(threat);
                }
            }

            // 전체 관측을 lastObservations에 병합 (VectorSensor + EnemyBuffer + AllyBuffer)
            int totalObs = oi + lastEnemyBufferObs.Count + lastAllyBufferObs.Count;
            if (lastObservations == null || lastObservations.Length != totalObs)
                lastObservations = new float[totalObs];
            for (int bi = 0; bi < lastEnemyBufferObs.Count; bi++)
                lastObservations[oi + bi] = lastEnemyBufferObs[bi];
            int allyStart = oi + lastEnemyBufferObs.Count;
            for (int bi = 0; bi < lastAllyBufferObs.Count; bi++)
                lastObservations[allyStart + bi] = lastAllyBufferObs[bi];
        }

        /// <summary>대상의 방향(R,F) + 거리 + 헤딩차이를 VectorSensor에 추가 (4개)</summary>
        private void AddDirectionDistanceObs(VectorSensor sensor, Transform target,
            Vector3 myPos, Vector3 myForward, Vector3 myRight, float myAngle,
            float rScale, float fScale, float distScale, float hdgScale, float normK, ref int oi)
        {
            Vector3 rel = target.position - myPos;
            float dist = rel.magnitude;
            float rightDot = Vector3.Dot(rel, myRight);
            float fwdDot = Vector3.Dot(rel, myForward);

            if (dist > 0.1f)
            {
                AddObs(sensor, (rightDot / dist) * rScale, ref oi);
                AddObs(sensor, (fwdDot / dist) * fScale, ref oi);
            }
            else
            {
                AddObs(sensor, 0f, ref oi);
                AddObs(sensor, 0f, ref oi);
            }
            AddObs(sensor, NormalizePosition(dist, normK) * distScale, ref oi);
            AddObs(sensor, NormalizeAngle(myAngle, target.eulerAngles.y) * hdgScale, ref oi);
        }

        /// <summary>
        /// 아군 쌍 BufferSensor 관측 (거리순 정렬, 가까운 3쌍만)
        /// 각 쌍: dist, fwd, right, hdg, webLength = 5개
        /// </summary>
        private void CollectAllyPairBufferObs(Vector3 myPos, Vector3 myForward, Vector3 myRight, float myAngle)
        {
            if (allyBufferSensor == null) return;
            lastAllyBufferObs.Clear();

            // 후보 수집 (실제 쌍 + phantom) → 거리순 정렬 → 상위 3개만
            var candidates = new List<(Vector3 center, float heading, float webLen, float dist)>();

            // 1. 실제 아군 쌍
            var lzm = envController != null ? envController.launchZoneManager : null;
            if (lzm != null && lzm.IsInitialized)
            {
                int myPairIdx = lzm.FindPairIndex(this);
                int poolCount = lzm.GetCurrentPoolCount();

                for (int i = 0; i < poolCount; i++)
                {
                    if (i == myPairIdx) continue;
                    DefensePair pair = lzm.GetPair(i);
                    if (pair == null) continue;
                    if (pair.agent1 == null || pair.agent2 == null) continue;

                    Vector3 a1Pos = pair.agent1.transform.position;
                    Vector3 a2Pos = pair.agent2.transform.position;
                    if (a1Pos.y < -100f || a2Pos.y < -100f) continue;

                    Vector3 otherCenter = (a1Pos + a2Pos) * 0.5f;
                    float wLen = Vector3.Distance(a1Pos, a2Pos);
                    float otherHeading = (pair.agent1.transform.eulerAngles.y + pair.agent2.transform.eulerAngles.y) * 0.5f;
                    float d = Vector3.Distance(myPos, otherCenter);

                    candidates.Add((otherCenter, otherHeading, wLen, d));
                }
            }

            // 2. Phantom 쌍
            if (phantomsInitialized && phantomPairs != null)
            {
                for (int i = 0; i < phantomCount; i++)
                {
                    if (!phantomPairs[i].isValid) continue;
                    Vector3 pc = phantomPairs[i].WebCenter;
                    float d = Vector3.Distance(myPos, pc);
                    candidates.Add((pc, phantomPairs[i].heading, phantomPairs[i].WebLength, d));
                }
            }

            // 거리순 정렬 → 가까운 3쌍만
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            int count = Mathf.Min(candidates.Count, 3);
            for (int i = 0; i < count; i++)
            {
                var c = candidates[i];
                AppendAllyPairObs(myPos, myForward, myRight, myAngle, c.center, c.heading, c.webLen);
            }
        }

        /// <summary>아군쌍 1개를 allyBufferSensor에 추가 (5개: dist, fwd, right, hdg, webLength)</summary>
        private void AppendAllyPairObs(Vector3 myPos, Vector3 myForward, Vector3 myRight, float myAngle,
            Vector3 center, float heading, float webLength)
        {
            Vector3 rel = center - myPos;
            float dist = rel.magnitude;
            float normDist = dist / (dist + allyPairNormK);
            float fwd = dist > 0.1f ? Vector3.Dot(rel, myForward) / dist : 0f;
            float right = dist > 0.1f ? Vector3.Dot(rel, myRight) / dist : 0f;
            float hdg = NormalizeAngle(myAngle, heading);
            float wLen = webLength / (webLength + allyPairNormK);

            allyBufferSensor.AppendObservation(new float[] { normDist, fwd, right, hdg, wLen });
            lastAllyBufferObs.Add(normDist); lastAllyBufferObs.Add(fwd);
            lastAllyBufferObs.Add(right); lastAllyBufferObs.Add(hdg);
            lastAllyBufferObs.Add(wLen);
        }

        /// <summary>
        /// Phantom 초기 배치: webCenter 기준 원형 배치 (360° 균등 + 랜덤 지터)
        /// 각 PhantomPair는 agent1+agent2 위치를 보유 (가상 그물)
        /// </summary>
        public void InitializePhantoms(Vector3 webCenter, Vector3 enemyPos, Vector3 motherPos)
        {
            float headingDeg = Mathf.Atan2(
                (enemyPos - motherPos).x,
                (enemyPos - motherPos).z) * Mathf.Rad2Deg;

            // 에피소드마다 5~10쌍 랜덤
            phantomCount = Random.Range(5, PHANTOM_MAX + 1);
            phantomPairs = new PhantomPair[phantomCount];

            float angleStep = 360f / phantomCount;

            for (int i = 0; i < phantomCount; i++)
            {
                // 원형 균등 배분 + 랜덤 각도 지터 (±반 스텝)
                float angleDeg = angleStep * i + Random.Range(-angleStep * 0.3f, angleStep * 0.3f);
                float angleRad = angleDeg * Mathf.Deg2Rad;

                // 거리 랜덤 (phantomSpacing의 1~5배)
                float radius = phantomSpacing * Random.Range(1f, 5f);

                Vector3 dir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                Vector3 pairCenter = webCenter + dir * radius;

                // 그물 방향: 중심→쌍 방향에 수직
                Vector3 webDir = new Vector3(-dir.z, 0f, dir.x);

                // 그물 길이 10~60m (반폭 5~30m) 랜덤
                float halfWidth = Random.Range(5f, 30f);

                // 각 쌍의 헤딩: 중심을 향하거나 적 방향 등 랜덤
                float pairHeading = angleDeg + 180f + Random.Range(-30f, 30f);

                phantomPairs[i] = new PhantomPair
                {
                    agent1Pos = pairCenter - webDir * halfWidth,
                    agent2Pos = pairCenter + webDir * halfWidth,
                    heading = pairHeading,
                    noiseSeed1 = Random.Range(0f, 1000f),
                    noiseSeed2 = Random.Range(0f, 1000f),
                    speedMult = Random.Range(0.7f, 2.0f),
                    isValid = true,
                    hasWeb = Random.value > 0.2f,
                    isStopped = false,
                    willStop = false,
                    stopTime = 0f
                };
            }

            // 절반 이상 랜덤 선택 → 가다가 중간에 멈추도록 예약
            int stopCount = Random.Range(phantomCount / 2, phantomCount + 1);
            int[] indices = new int[phantomCount];
            for (int i = 0; i < phantomCount; i++) indices[i] = i;
            for (int i = phantomCount - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                int tmp = indices[i]; indices[i] = indices[j]; indices[j] = tmp;
            }
            float now = Time.time;
            for (int k = 0; k < stopCount; k++)
            {
                var pp = phantomPairs[indices[k]];
                pp.willStop = true;
                pp.stopTime = now + Random.Range(1f, 5f); // 1~5초 후 멈춤
            }

            phantomsInitialized = true;
        }

        /// <summary>
        /// Phantom 독립 기동 업데이트: 차단 기동 + Perlin 노이즈
        /// DefenseEnvController.FixedUpdate에서 매 스텝 호출
        /// </summary>
        public void UpdatePhantoms(float dt, Vector3 enemyPos, Vector3 motherPos)
        {
            if (!phantomsInitialized)
            {
                Vector3 pPos = (partnerAgent != null) ? partnerAgent.transform.position : transform.position;
                Vector3 wc = (transform.position + pPos) * 0.5f;
                InitializePhantoms(wc, enemyPos, motherPos);
            }

            // 기본 속도 (실제 아군 기준)
            float baseSpeed = 0f;
            if (_engine != null && _engine.RB != null)
            {
                Vector3 vel = _engine.RB.velocity;
                vel.y = 0f;
                baseSpeed = vel.magnitude;
            }
            if (baseSpeed < 0.5f) baseSpeed = 0.5f;

            float time = Time.time;

            // 적 방향 및 방어선 방향
            Vector3 enemyDir = enemyPos - motherPos;
            enemyDir.y = 0f;
            if (enemyDir.sqrMagnitude < 1f) enemyDir = Vector3.forward;
            enemyDir.Normalize();
            Vector3 defenseDir = new Vector3(-enemyDir.z, 0f, enemyDir.x);
            float headingDeg = Mathf.Atan2(enemyDir.x, enemyDir.z) * Mathf.Rad2Deg;

            for (int i = 0; i < phantomCount; i++)
            {
                var pp = phantomPairs[i];
                if (!pp.isValid) continue;

                // 가다가 멈추기: 예약 시간 도달 시 정지
                if (pp.willStop && !pp.isStopped && time >= pp.stopTime)
                    pp.isStopped = true;
                if (pp.isStopped) continue;

                // 개별 속도: 아군 속도가 phantomSpeed 이하면 동일, 초과하면 초과분의 절반만 반영
                float pairSpeed;
                if (baseSpeed <= phantomSpeed)
                    pairSpeed = baseSpeed * pp.speedMult;
                else
                    pairSpeed = (phantomSpeed + (baseSpeed - phantomSpeed) * 0.1f) * pp.speedMult;

                // 이동: 실제 아군처럼 적을 향해 지속 전진
                // 헤딩 = 적 방향 + Perlin 노이즈 (각 phantom pair 독립)
                float headingNoiseDeg = (Mathf.PerlinNoise(pp.noiseSeed1 + pp.noiseSeed2, time * 0.15f) - 0.5f)
                    * 2f * phantomNoiseStrength * 10f; // ±도
                float moveAngle = (headingDeg + headingNoiseDeg) * Mathf.Deg2Rad;
                Vector3 moveDir = new Vector3(Mathf.Sin(moveAngle), 0f, Mathf.Cos(moveAngle));
                Vector3 movement = moveDir * pairSpeed * dt;

                // 방어선 방향 횡이동 노이즈 (각 agent 독립 → 쌍 내부 약간 비대칭)
                float noise1 = (Mathf.PerlinNoise(pp.noiseSeed1, time * 0.2f) - 0.5f) * 2f * phantomNoiseStrength;
                float noise2 = (Mathf.PerlinNoise(pp.noiseSeed2, time * 0.2f) - 0.5f) * 2f * phantomNoiseStrength;

                pp.agent1Pos += movement + defenseDir * noise1 * dt;
                pp.agent2Pos += movement + defenseDir * noise2 * dt;

                // 쌍 간격 보정: 초기 생성된 그물 길이(개별 랜덤) 유지
                float currentHalfWidth = pp.WebLength * 0.5f;
                if (currentHalfWidth < 1f) currentHalfWidth = 5f;
                Vector3 pairMid = (pp.agent1Pos + pp.agent2Pos) * 0.5f;
                Vector3 pairDir = (pp.agent2Pos - pp.agent1Pos);
                pairDir.y = 0f;
                if (pairDir.sqrMagnitude < 0.01f) pairDir = defenseDir;
                pairDir.Normalize();
                pp.agent1Pos = pairMid - pairDir * currentHalfWidth;
                pp.agent2Pos = pairMid + pairDir * currentHalfWidth;

                pp.heading = headingDeg + headingNoiseDeg;
            }
        }

        /// <summary>관측값 기록 + 센서 추가 헬퍼</summary>
        private void AddObs(VectorSensor sensor, float value, ref int index)
        {
            sensor.AddObservation(value);
            if (lastObservations != null && index < lastObservations.Length)
                lastObservations[index++] = value;
        }

        /// <summary>ㅊ
        /// 액션 수신: actions[0]=throttle(-1~1), actions[1]=steering(-1~1)
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_engine == null || _engine.RB == null || _episodeEnded || _neutralized)
            {
                Debug.LogWarning($"[{name}] OnAction BLOCKED: engine={_engine != null}, RB={_engine?.RB != null}, ended={_episodeEnded}, neutral={_neutralized}");
                return;
            }

            float throttleInput = actions.ContinuousActions[0];
            float steeringInput = actions.ContinuousActions[1];

            // NaN 방지
            if (float.IsNaN(throttleInput) || float.IsInfinity(throttleInput)) throttleInput = 0f;
            if (float.IsNaN(steeringInput) || float.IsInfinity(steeringInput)) steeringInput = 0f;

            throttleInput = Mathf.Clamp(throttleInput, -1f, 1f);
            steeringInput = Mathf.Clamp(steeringInput, -1f, 1f);

            // Throttle Mapping (simple):
            //   action [-1,+1] → throttle [minThrottle, maxThrottle]
            //   -1 → minThrottle(0.3), 0 → mid(0.65), +1 → maxThrottle(1.0)
            float throttle = minThrottle + (throttleInput + 1f) * 0.5f * (maxThrottle - minThrottle);

            // Steering
            float steering = Mathf.Clamp(steeringInput * steeringSensitivity, -1f, 1f);

            // Smoothing
            if (inputSmoothing < 1f)
            {
                throttle = Mathf.Lerp(_prevThrottle, throttle, inputSmoothing);
                steering = Mathf.Lerp(_prevSteering, steering, inputSmoothing);
            }
            _throttleDelta = Mathf.Abs(throttle - _prevThrottle);
            _steeringDelta = Mathf.Abs(steering - _prevSteering);

            _prevThrottle = throttle;
            _prevSteering = steering;

            _engine.Accelerate(throttle);
            _engine.Turn(steering);

            // 디버그 로그
            if (enableDebugLog)
            {
                Debug.Log($"[{gameObject.name}] Throttle: {throttle:F2}, Steering: {steering:F2}");
            }
        }

        /// <summary>
        /// Web-적군 충돌 처리 → DefenseEnvController로 전달
        /// </summary>
        public void OnEnemyCaptured(Vector3 enemyPosition)
        {
            if (_episodeEnded)
                return;

            _episodeEnded = true;

            // 멀티 환경 호환: 같은 환경 계층 내에서 컨트롤러 찾기
            Transform envRoot = transform.parent != null ? transform.parent : transform;
            DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();
            if (envController != null)
            {
                envController.OnEnemyCaptured(enemyPosition);
            }
        }

        /// <summary>
        /// 수동 조종 - agent1: WASD, agent2: 화살표키
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuousActions = actionsOut.ContinuousActions;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                continuousActions[0] = 0f;
                continuousActions[1] = 0f;
                return;
            }

            float throttle = 0f;
            float steering = 0f;

            if (useArrowKeys)
            {
                // Agent2: 화살표키
                if (keyboard.upArrowKey.isPressed) throttle = 1f;
                else if (keyboard.downArrowKey.isPressed) throttle = -0.5f;
                if (keyboard.rightArrowKey.isPressed) steering = 1f;
                else if (keyboard.leftArrowKey.isPressed) steering = -1f;
            }
            else
            {
                // Agent1: WASD
                if (keyboard.wKey.isPressed) throttle = 1f;
                else if (keyboard.sKey.isPressed) throttle = -0.5f;
                if (keyboard.dKey.isPressed) steering = 1f;
                else if (keyboard.aKey.isPressed) steering = -1f;
            }

            continuousActions[0] = Mathf.Clamp(throttle, -1f, 1f);
            continuousActions[1] = Mathf.Clamp(steering, -1f, 1f);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_episodeEnded || _neutralized)
                return;

            if (collision.gameObject.GetComponent<DefenseAgent>() != null ||
                collision.gameObject.CompareTag("MotherShip"))
            {
                // 멀티 환경 호환: 같은 환경 계층 내에서 컨트롤러 찾기
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();
                if (envController != null)
                {
                    envController.OnFriendlyCollision(this);
                }
            }
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;

            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, 2f);

            if (partnerAgent != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, partnerAgent.transform.position);
            }

            if (enemyShips != null)
            {
                Gizmos.color = Color.red;
                foreach (var enemy in enemyShips)
                {
                    if (enemy != null)
                        Gizmos.DrawLine(transform.position, enemy.transform.position);
                }
            }

            if (webObject != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(webObject.transform.position, 3f);
            }
        }
    }
}
