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

        [Tooltip("적군 BufferSensor 최대 관측 수")]
        [Range(1, 20)] public int enemyMaxObservables = 3;

        [Tooltip("아군 BufferSensor 최대 관측 수")]
        [Range(1, 20)] public int allyMaxObservables = 3;

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
        public float minThrottle = 0.1f;

        [Range(0.1f, 2.0f)]
        public float steeringSensitivity = 1.0f;

        [Range(1f, 50f)]
        [Tooltip("아군 선박 horsePower 배율 (Engine.horsePower에 곱함)")]
        public float speedMultiplier = 10f;

        [Range(0.01f, 1.0f)]
        [Tooltip("입력 스무스 처리 (1.0 = 즉각 반응)")]
        public float inputSmoothing = 1.0f;

        [Header("Observation NormK (출력 0.5 지점 거리)")]
        [Range(1f, 1000f)] public float enemyNormK = 250f;
        // rayNormK 제거됨 (LOS 관측 제거)

        [Header("Observation Scale (정규화 후 가중치)")]
        [Tooltip("베어링 유리함수 k값 (작을수록 정면 민감도↑, 0.5지점=k도)")]
        [Range(5f, 90f)] public float bearingNormK = 30f;

        [Header("Enemy Observation Scale (적군 관측 계수)")]
        [Range(1f, 10f)] public float enemyDistScale = 1f;
        [Range(1f, 10f)] public float enemyBearingScale = 1f;  // 2번 관측: CTE 스케일 (Inspector 이름 유지)
        [Range(1f, 10f)] public float enemyHeadingScale = 1f;

        [Header("Ally Observation Scale (아군 관측 계수)")]
        [Range(1f, 10f)] public float allyDistScale = 1f;
        [Range(1f, 10f)] public float allyBearingScale = 1f;
        [Range(1f, 10f)] public float allyWebLengthScale = 1f;

        [Header("Self State (자기 기동 상태)")]
        [Range(1f, 50f)] public float speedNormK = 10f;
        [Range(0f, 5f)] public float forwardSpeedScale = 1f;
        [Range(0f, 5f)] public float driftSpeedScale = 1f;

        [Header("Ally Pair NormK")]
        [Range(1f, 1000f)] public float allyPairNormK = 100f;
        [Range(1f, 200f)] public float webLengthNormK = 50f;

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

        [Header("=== Imitation Learning (LOS Heuristic) ===")]
        [Tooltip("true면 LOS 가이던스 자동 휴리스틱 사용 (키보드 대체)")]
        public bool enableLOSHeuristic = false;

        [Tooltip("LOS 목표점 측방 거리 절반 (적 기준 좌/우 이격, m). webDeployedThreshold/2 권장")]
        [Range(10f, 200f)]
        public float losOffset = 50f;

        [Tooltip("LOS PD 조향 감도 (bearing 오차 → 조향 출력 배율)")]
        [Range(0.5f, 20f)]
        public float losSteerGain = 3f;

        [Tooltip("LOS D 조향 감도 (bearing 변화율 → 조향 출력 배율, 오버슈트 억제)")]
        [Range(0f, 10f)]
        public float losDerivGain = 0.3f;

        [Tooltip("전방 아군 감지 거리 (m) — 이 안에 아군이 있으면 감속")]
        [Range(5f, 300f)]
        public float allyAvoidanceDist = 40f;

        [Tooltip("전방 감지 측방 허용폭 (m) — 이 이상 옆에 있으면 무시")]
        [Range(3f, 90f)]
        public float allyAvoidanceLateralMax = 12f;

        [Tooltip("전방 아군 페어 감속 강도 (가까울수록 최대 이 값만큼 감속)")]
        [Range(0.1f, 5f)]
        public float allyBrakingStrength = 1.5f;

        [Tooltip("후방 아군 페어 가속 강도 (가까울수록 최대 이 값만큼 가속)")]
        [Range(0.1f, 5f)]
        public float allyAccelStrength = 0.8f;

        [Tooltip("데모 녹화 활성화 시 BehaviorType=HeuristicOnly + DemonstrationRecorder 자동 설정")]
        public bool enableDemoRecording = false;

        [Tooltip("데모 파일 저장 경로")]
        public string demoDirectory = "Assets/Demos";

        [Tooltip("데모 파일 이름 접두사 (뒤에 오브젝트 이름 자동 추가)")]
        public string demoName = "LOS_Defense";

        // ── Heuristic 캐시 (CollectObservations에서 채워짐, Heuristic에서 소비) ──
        private GameObject _heuristicNearestEnemy = null;
        private float _prevGoalBrg = 0f;  // LOS D 제어용 이전 bearing 오차

        [Header("Debug")]
        public bool enableDebugLog = false;
        [Tooltip("Game View에서 배정 라인 실시간 표시 (LineRenderer 사용)")]
        public bool showMatchingLine = false;

        // ── 런타임 매칭 LineRenderer ──
        private LineRenderer _matchingLR;   // Web 중심 → 배정 적군 (노란선)
        private LineRenderer _partnerLR;    // Agent1 → Agent2 (초록선)

        /// <summary>모니터링용: 전체 관측값 (VectorSensor + EnemyBuffer + AllyBuffer)</summary>
        [HideInInspector] public float[] lastObservations;
        /// <summary>모니터링용: lastObservations 중 실제 유효한 개수</summary>
        [HideInInspector] public int lastObservationsCount;
        /// <summary>디버그: CollectObservations 호출 횟수 (UI에서 증가 확인용)</summary>
        [HideInInspector] public int collectObsCallCount;
        /// <summary>모니터링용: 적군 버퍼 관측 임시 저장</summary>
        [HideInInspector] public List<float> lastEnemyBufferObs = new List<float>();
        /// <summary>모니터링용: 아군 버퍼 관측 임시 저장</summary>
        [HideInInspector] public List<float> lastAllyBufferObs = new List<float>();

        private bool _episodeEnded = false;
        private bool _neutralized = false;
        private bool _straightMode = false;  // Stage9: 직진 이탈 모드
        private bool _deployMode = false;   // Convoy-Deploy: 그물 전개 모드
        private float _deploySteerOverride = -0.5f;
        private bool _convoyMode = false;   // FixedJoint 쌍동선: 차동 추력 모드

        [Header("=== Convoy (FixedJoint) ===")]
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
            var bp = GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
            if (bp != null) bp.TeamId = 0; // 아군 팀 (Self-Play 대응)

            if (TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
                if (_engine != null && speedMultiplier > 1f)
                    _engine.horsePower *= speedMultiplier;
            }

            // BufferSensor 자동 찾기 → 없으면 AddComponent → 크기 보정
            // 코드에서 AppendObservation(new float[] { dist, brg, hdg, srd }) → 4개 값
            if (enemyBufferSensor == null)
                enemyBufferSensor = GetComponent<BufferSensorComponent>();
            if (enemyBufferSensor == null)
                enemyBufferSensor = gameObject.AddComponent<BufferSensorComponent>();
            enemyBufferSensor.SensorName = "EnemyBufferSensor";
            enemyBufferSensor.ObservableSize = 3;   // Dist, SignedBrg, Hdg
            enemyBufferSensor.MaxNumObservables = enemyMaxObservables;

            // 아군 쌍 BufferSensor: 가변 개수 (실제 쌍 + phantom, 최대 PHANTOM_MAX+6)
            var buffers = GetComponents<BufferSensorComponent>();
            allyBufferSensor = buffers.Length > 1 ? buffers[1] : null;
            if (allyBufferSensor == null)
                allyBufferSensor = gameObject.AddComponent<BufferSensorComponent>();
            allyBufferSensor.SensorName = "AllyBufferSensor";
            allyBufferSensor.ObservableSize = 3;   // dist, bearing, webLength
            allyBufferSensor.MaxNumObservables = allyMaxObservables;

            // 데모 녹화 모드: BehaviorType=HeuristicOnly + DemonstrationRecorder 자동 추가
            if (enableDemoRecording)
            {
                var demoBp = GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                if (demoBp != null) demoBp.BehaviorType = Unity.MLAgents.Policies.BehaviorType.HeuristicOnly;
                var rec = GetComponent<Unity.MLAgents.Demonstrations.DemonstrationRecorder>();
                if (rec == null) rec = gameObject.AddComponent<Unity.MLAgents.Demonstrations.DemonstrationRecorder>();
                rec.Record = true;
                rec.DemonstrationName = demoName + "_" + name;
                rec.DemonstrationDirectory = demoDirectory;
                rec.NumStepsToRecord = 0; // Play 종료까지 녹화
                Debug.Log($"[{name}] DemoRecording 활성화: {demoDirectory}/{demoName}_{name}.demo");
            }

            // 런타임 매칭 LineRenderer 생성
            _matchingLR = CreateLineRenderer("_MatchingLine", new Color(1f, 0.9f, 0.1f, 0.85f), 0.6f);
            _partnerLR  = CreateLineRenderer("_PartnerLine",  new Color(0.2f, 1f, 0.3f, 0.7f),  0.4f);
        }

        private LineRenderer CreateLineRenderer(string childName, Color col, float width)
        {
            var go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.startWidth  = width;
            lr.endWidth    = width;
            lr.useWorldSpace = true;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = col;
            lr.endColor   = new Color(col.r, col.g, col.b, col.a * 0.4f);
            lr.enabled = false;
            return lr;
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
        public bool IsNeutralized => _neutralized;
        public void SetNeutralized(bool value) => _neutralized = value;

        /// <summary>
        /// Stage9: 직진 이탈 모드 설정
        /// true → FixedUpdate에서 ML 정책 무시, 현재 헤딩으로 전속력 직진
        /// </summary>
        public void SetStraightMode(bool value)
        {
            Debug.Log($"[{name}] SetStraightMode: {_straightMode} → {value}");
            _straightMode = value;
            _straightModeLogCount = 0;
        }

        /// <summary>
        /// Stage9: 직진 이탈 중 엔진 구동 (UnregisterAgent 후 OnActionReceived가 호출되지 않으므로 직접 구동)
        /// </summary>
        private void LateUpdate()
        {
            UpdateMatchingLines();
        }

        private void UpdateMatchingLines()
        {
            if (!showMatchingLine)
            {
                if (_matchingLR != null) _matchingLR.enabled = false;
                if (_partnerLR  != null) _partnerLR.enabled  = false;
                return;
            }

            // 파트너 라인
            if (_partnerLR != null && partnerAgent != null)
            {
                _partnerLR.enabled = true;
                _partnerLR.SetPosition(0, transform.position + Vector3.up * 1f);
                _partnerLR.SetPosition(1, partnerAgent.transform.position + Vector3.up * 1f);
            }
            else if (_partnerLR != null)
            {
                _partnerLR.enabled = false;
            }

            // 배정 적군 매칭 라인 (Web 중심 → 적)
            // partnerAgent가 있는 쪽(agent1)만 그림 — agent2는 중복 선 방지
            if (_matchingLR != null)
            {
                GameObject assigned = GetAssignedEnemy();
                if (assigned != null && assigned.activeInHierarchy && partnerAgent != null)
                {
                    Vector3 webCenter = (transform.position + partnerAgent.transform.position) * 0.5f + Vector3.up * 1f;
                    _matchingLR.enabled = true;
                    _matchingLR.SetPosition(0, webCenter);
                    _matchingLR.SetPosition(1, assigned.transform.position + Vector3.up * 1f);
                }
                else
                {
                    _matchingLR.enabled = false;
                }
            }
        }

        private void FixedUpdate()
        {
            if (!_straightMode) return;
            if (_engine == null || _engine.RB == null) return;

            _engine.Accelerate(maxThrottle);
            _engine.Turn(0f);

            // 디버그: 직진 모드 동작 확인 (첫 5프레임만)
            if (_straightModeLogCount < 5)
            {
                Debug.Log($"[{name}] StraightMode FixedUpdate: throttle={maxThrottle}, vel={_engine.RB.velocity.magnitude:F1}, neutralized={_neutralized}");
                _straightModeLogCount++;
            }
        }
        private int _straightModeLogCount = 0;

        /// <summary>
        /// Convoy-Deploy: 그물 전개 모드 설정
        /// true → ML 정책 무시, 지정 방향으로 조타하며 전속력 이동
        /// </summary>
        public bool IsDeployMode => _deployMode;

        public void SetDeployMode(bool value, float steerOverride = -0.5f)
        {
            _deployMode = value;
            _deploySteerOverride = steerOverride;
        }

        /// <summary>
        /// FixedJoint 쌍동선 모드: Agent1이 차동 추력으로 양쪽 엔진 제어
        /// </summary>
        public bool IsConvoyMode => _convoyMode;

        public void SetConvoyMode(bool value)
        {
            _convoyMode = value;
        }

        /// <summary>
        /// 런타임 배치 시 에이전트 상태 리셋
        /// OnEpisodeBegin과 달리 ML-Agents 에피소드를 건드리지 않고 내부 플래그만 초기화
        /// </summary>
        public void ResetForDeployment()
        {
            _episodeEnded = false;
            _neutralized = false;
            _straightMode = false;
            _deployMode = false;
            _convoyMode = false;
            assignedTargetIndex = -1; // Commander가 새로 배정
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
        }

        public override void OnEpisodeBegin()
        {
            _episodeEnded = false;
            _deployMode = false;
            _convoyMode = false;
            assignedTargetIndex = -1; // Commander가 새로 배정
            // _neutralized는 여기서 리셋하지 않음
            // SetNeutralized(false)로만 해제 (DeployPairs/ResetScene에서 호출)
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
            phantomsInitialized = false;
        }

        /// <summary>
        /// 거리 정규화: x/(x+k) → [0, 1)
        /// k = 감도 기준 거리 (출력 0.5 지점)
        /// x=0 → 0, x=k → 0.5, x=∞ → 1
        /// 항상 양수, 부호 전환 없음, 근거리 민감
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
        /// 헤딩 차이 정규화: ±180°(정면대치) → 0, 0°(동방향) → ±1
        /// cos(delta/2) × sign(delta), 좌/우 부호 보존
        /// 방어 시나리오에서 정면 대치(180°)가 기본 상태이므로 0으로 매핑
        /// </summary>
        private float NormalizeHeadingDiff(float fromAngle, float toAngle)
        {
            float delta = Mathf.DeltaAngle(fromAngle, toAngle); // -180 ~ +180
            return Mathf.Cos(delta * 0.5f * Mathf.Deg2Rad) * Mathf.Sign(delta);
        }

        /// <summary>
        /// 부호 있는 베어링: 바디 좌표계 기준 (-1~+1)
        /// 0=정면, ±1=후방, 부호=좌(-)우(+)
        /// sqrt(angle/180)로 정면 민감도 유지, XZ 평면 cross product로 좌/우 판별
        /// </summary>
        private float ComputeSignedBearing(Vector3 myForward, Vector3 toTarget)
        {
            Vector3 fwd = myForward; fwd.y = 0f;
            Vector3 dir = toTarget; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return 1f;
            float angleDeg = Vector3.Angle(fwd, dir);
            float norm = Mathf.Sqrt(angleDeg / 180f);
            float cross = fwd.x * dir.z - fwd.z * dir.x;
            return cross >= 0f ? norm : -norm;
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
        /// 관측 수집 (VectorSensor 0개 + AllyBufferSensor 최대10 + EnemyBufferSensor 최대10)
        /// VectorSensor: 없음 (모든 정보가 BufferSensor의 상대값으로 충분)
        /// AllyBufferSensor: 아군쌍+트랩 최대10개, 각 3개 (dist, bearing, webLength)
        /// EnemyBufferSensor: 활성 적군 최대10대, 각 3개 (Dist, SignedBrg, Hdg)
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            collectObsCallCount++;

            const int VECTOR_OBS_COUNT = 0;
            if (lastObservations == null)
                lastObservations = new float[1];
            int oi = 0;

            if (_engine == null || _engine.RB == null)
            {
                lastObservationsCount = 0;
                return;
            }

            // Web 중심 기준: 자기 쌍의 agent1+agent2 중점을 관측 원점으로 사용
            Vector3 partnerPos = (partnerAgent != null) ? partnerAgent.transform.position : transform.position;
            Vector3 webCenter = (transform.position + partnerPos) * 0.5f;
            Vector3 avgForward = (transform.forward + (partnerAgent != null ? partnerAgent.transform.forward : transform.forward)) * 0.5f;
            avgForward.y = 0f;  // pitch 영향 제거, XZ 평면(yaw)만 사용
            Vector3 webForward = avgForward.sqrMagnitude > 0.01f ? avgForward.normalized : transform.forward;
            float webAngle = Quaternion.LookRotation(webForward).eulerAngles.y;

            // 4. AllyBufferSensor: 가까운 아군 쌍 최대 3개
            CollectAllyPairBufferObs(webCenter, webForward);

            // 5. EnemyBufferSensor: 모든 활성 적군 (3개/적: Dist, SignedBrg, Hdg) — 거리순 정렬
            lastEnemyBufferObs.Clear();
            if (enemyBufferSensor != null && enemyShips != null)
            {
                // 활성 적군을 거리순 정렬 (Web 중심 기준)
                var enemyByDist = new List<(int idx, float dist)>();
                for (int i = 0; i < enemyShips.Length; i++)
                {
                    if (enemyShips[i] == null || !enemyShips[i].activeInHierarchy) continue;
                    // 무력화된 적 제외 (트랩/포획됨)
                    if (envController != null && envController.IsEnemyNeutralized(enemyShips[i])) continue;
                    float d = Vector3.Distance(webCenter, enemyShips[i].transform.position);
                    enemyByDist.Add((i, d));
                }
                enemyByDist.Sort((a, b) => a.dist.CompareTo(b.dist));

                // Heuristic() LOS 캐시 갱신 (가장 가까운 적군)
                _heuristicNearestEnemy = enemyByDist.Count > 0 ? enemyShips[enemyByDist[0].idx] : null;

                // 모든 활성 적군을 Buffer에 추가 (MaxNumObservables까지)
                int count = Mathf.Min(enemyByDist.Count, enemyBufferSensor.MaxNumObservables);
                for (int ei = 0; ei < count; ei++)
                {
                    var enemy = enemyShips[enemyByDist[ei].idx];
                    Vector3 rel = enemy.transform.position - webCenter;
                    float dist = rel.magnitude;

                    float eDistS = (envController != null) ? envController.enemyDistScale    : enemyDistScale;
                    float eBrgS  = (envController != null) ? envController.enemyBearingScale : enemyBearingScale;
                    float eHdgS  = (envController != null) ? envController.enemyHeadingScale : enemyHeadingScale;
                    float d   = NormalizePosition(dist, enemyNormK) * eDistS;
                    float brg = ComputeSignedBearing(webForward, rel) * eBrgS;
                    // hdg: Web 헤딩과 적 헤딩의 차이 (±180°대치→0, 동방향→±1)
                    float hdg = NormalizeHeadingDiff(webAngle, enemy.transform.eulerAngles.y) * eHdgS;
                    enemyBufferSensor.AppendObservation(new float[] { d, brg, hdg });
                    lastEnemyBufferObs.Add(d); lastEnemyBufferObs.Add(brg); lastEnemyBufferObs.Add(hdg);
                }
            }

            // 전체 관측을 lastObservations에 병합 (VectorSensor + EnemyBuffer + AllyBuffer)
            int totalObs = oi + lastEnemyBufferObs.Count + lastAllyBufferObs.Count;
            if (lastObservations == null || lastObservations.Length < totalObs)
            {
                // 배열은 오직 커지기만 함 (축소 안 함 → UI 히스토리 초기화 방지)
                var oldObs = lastObservations;
                lastObservations = new float[totalObs];
                // 재할당 시 VectorSensor 값(0~oi-1) 복사 (소실 방지)
                if (oldObs != null)
                    System.Array.Copy(oldObs, lastObservations, Mathf.Min(oi, oldObs.Length));
            }
            lastObservationsCount = totalObs;
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
        /// 아군 쌍 + 트랩 BufferSensor 관측 (거리순 정렬, 가까운 10개)
        /// 각: dist, bearing, webLength = 3개
        /// </summary>
        private void CollectAllyPairBufferObs(Vector3 myPos, Vector3 myForward)
        {
            if (allyBufferSensor == null) return;
            lastAllyBufferObs.Clear();

            // 후보 수집 (실제 쌍 + phantom + 트랩) → 거리순 정렬 → 상위 10개
            // heading: 아군 쌍의 평균 yaw (없으면 0f)
            var candidates = new List<(Vector3 center, float dist, float heading)>();

            float myAngle = Quaternion.LookRotation(myForward).eulerAngles.y;

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
                    if (pair == null || !pair.isActive) continue;
                    if (pair.agent1 == null || pair.agent2 == null) continue;

                    Vector3 a1Pos = pair.agent1.transform.position;
                    Vector3 a2Pos = pair.agent2.transform.position;
                    if (a1Pos.y < -100f || a2Pos.y < -100f) continue;

                    Vector3 otherCenter = (a1Pos + a2Pos) * 0.5f;
                    float d = Vector3.Distance(myPos, otherCenter);
                    float allyYaw = (pair.agent1.transform.eulerAngles.y + pair.agent2.transform.eulerAngles.y) * 0.5f;

                    candidates.Add((otherCenter, d, allyYaw));
                }
            }

            // 2. Phantom 쌍 (heading 정보 없음 → 0f)
            if (phantomsInitialized && phantomPairs != null)
            {
                for (int i = 0; i < phantomCount; i++)
                {
                    if (!phantomPairs[i].isValid) continue;
                    Vector3 pc = phantomPairs[i].WebCenter;
                    float d = Vector3.Distance(myPos, pc);
                    candidates.Add((pc, d, 0f));
                }
            }

            // 3. 트랩 (정적 → heading 0f)
            if (envController != null && envController.activeTraps != null)
            {
                for (int i = 0; i < envController.activeTraps.Count; i++)
                {
                    var trap = envController.activeTraps[i];
                    float d = Vector3.Distance(myPos, trap.position);
                    candidates.Add((trap.position, d, 0f));
                }
            }

            // 거리순 정렬 → 가까운 10개까지
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            int count = Mathf.Min(candidates.Count, 10);
            for (int i = 0; i < count; i++)
            {
                var c = candidates[i];
                AppendAllyPairObs(myPos, myForward, c.center, myAngle, c.heading);
            }
        }

        /// <summary>아군쌍 1개를 allyBufferSensor에 추가 (3개: dist, bearing, hdg)</summary>
        private void AppendAllyPairObs(Vector3 myPos, Vector3 myForward, Vector3 center, float myAngle, float allyYaw)
        {
            Vector3 rel = center - myPos;
            float dist = rel.magnitude;
            float aDistS = (envController != null) ? envController.allyDistScale : allyDistScale;
            float aBrgS  = (envController != null) ? envController.allyBearingScale : allyBearingScale;
            float aHdgS  = (envController != null) ? envController.allyWebLengthScale : allyWebLengthScale;
            float normDist = NormalizePosition(dist, allyPairNormK) * aDistS;
            float brg = ComputeSignedBearing(myForward, rel) * aBrgS;
            // hdg: 내 헤딩과 아군 쌍 헤딩의 차이 (±180°=반대방향→0, 0°=동방향→±1)
            float hdg = NormalizeHeadingDiff(myAngle, allyYaw) * aHdgS;

            allyBufferSensor.AppendObservation(new float[] { normDist, brg, hdg });
            lastAllyBufferObs.Add(normDist); lastAllyBufferObs.Add(brg); lastAllyBufferObs.Add(hdg);
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
                Debug.LogWarning($"[{name}] OnAction BLOCKED: engine={_engine != null}, RB={_engine?.RB != null}, " +
                    $"ended={_episodeEnded}, neutral={_neutralized}, convoy={_convoyMode}, " +
                    $"kinematic={_engine?.RB?.isKinematic}, secondary={_engine?.isConvoySecondary}");
                return;
            }

            // Stage9: 직진 이탈 모드 — ML 정책 무시, 현재 헤딩으로 전속력 직진
            if (_straightMode)
            {
                _engine.Accelerate(maxThrottle);
                _engine.Turn(0f);
                _prevThrottle = maxThrottle;
                _prevSteering = 0f;
                return;
            }

            // Convoy-Deploy: 그물 전개 모드 — ML 정책 무시, 지정 방향으로 전속력 분리
            if (_deployMode)
            {
                _engine.Accelerate(maxThrottle);
                _engine.Turn(_deploySteerOverride);
                _prevThrottle = maxThrottle;
                _prevSteering = _deploySteerOverride;
                return;
            }

            // 쌍동선 모드 — 한 대의 배처럼 throttle+steering 제어 (Agent2는 kinematic 동기화)
            if (_convoyMode)
            {
                float tInput = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
                float sInput = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);

                // Throttle: [-1,+1] → [0, maxThrottle]
                float convoyThrottle = (tInput + 1f) * 0.5f * maxThrottle;
                // Steering: 일반 조향과 동일
                float convoySteering = Mathf.Clamp(sInput * steeringSensitivity, -1f, 1f);

                _engine.Accelerate(convoyThrottle);
                _engine.Turn(convoySteering);

                _prevThrottle = convoyThrottle;
                _prevSteering = convoySteering;

                if (enableDebugLog || CompletedEpisodes < 2)
                    Debug.Log($"[{name}] CONVOY: throttle={convoyThrottle:F2}, steer={convoySteering:F2}, " +
                        $"vel={_engine.RB?.velocity.magnitude:F1}, secondary={_engine.isConvoySecondary}");
                return;
            }

            float throttleInput = actions.ContinuousActions[0];
            float steeringInput = actions.ContinuousActions[1];

            // NaN 방지
            if (float.IsNaN(throttleInput) || float.IsInfinity(throttleInput)) throttleInput = 0f;
            if (float.IsNaN(steeringInput) || float.IsInfinity(steeringInput)) steeringInput = 0f;

            throttleInput = Mathf.Clamp(throttleInput, -1f, 1f);
            steeringInput = Mathf.Clamp(steeringInput, -1f, 1f);

            // Throttle Mapping: action [-1,+1] → throttle [0, maxThrottle]
            //   -1 → 0 (정지), 0 → 0.5×max, +1 → max (전진 최대)
            float throttle = (throttleInput + 1f) * 0.5f * maxThrottle;

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

            // 가속 보상: throttle 높을수록 보상 (적극적 기동 유도)
            if (envController != null && envController.rewardCalculator != null)
            {
                float throttleReward = envController.rewardCalculator.throttleRewardCoeff * throttle;
                AddReward(throttleReward);
            }

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

            // LOS 휴리스틱 모드 (데모 녹화 시에도 강제 활성)
            if (enableLOSHeuristic || enableDemoRecording)
            {
                ApplyLOSHeuristic(continuousActions);
                return;
            }

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

        /// <summary>
        /// LOS 가이던스 기반 자동 조종.
        /// ─ DEPLOY   : OnActionReceived가 덮어쓰므로 demo 일관성 유지용 값 출력
        /// ─ CONVOY   : 적 방향으로 접근 (차동 추력 = 정면 정렬)
        /// ─ SEPARATED: 적의 LOS 수직 방향 좌/우 목표점으로 이동 (그물 포위)
        /// </summary>
        private void ApplyLOSHeuristic(Unity.MLAgents.Actuators.ActionSegment<float> ca)
        {
            // ── DEPLOY: OnActionReceived에서 어차피 덮어쓰지만 demo 기록값 일관성 유지 ──
            if (_deployMode)
            {
                ca[0] = 1f;
                float rawSteer = steeringSensitivity > 0f
                    ? _deploySteerOverride / steeringSensitivity
                    : _deploySteerOverride;
                ca[1] = Mathf.Clamp(rawSteer, -1f, 1f);
                return;
            }

            // 타겟 적 (AutoAssign 배정 우선 → 캐시 → 직접 탐색)
            GameObject target = GetAssignedEnemy();
            if (target == null && _heuristicNearestEnemy != null && _heuristicNearestEnemy.activeInHierarchy)
                target = _heuristicNearestEnemy;
            if (target == null)
                target = FindNearestActiveEnemy();

            if (target == null) { ca[0] = 0f; ca[1] = 0f; return; }

            Vector3 myPos  = transform.position;
            Vector3 myFwd  = transform.forward; myFwd.y = 0f;
            Vector3 enemyPos = target.transform.position;

            // ── CONVOY: 적 방향으로 직진 ──
            if (_convoyMode)
            {
                Vector3 toEnemy = enemyPos - myPos; toEnemy.y = 0f;
                float brg = ComputeSignedBearing(myFwd, toEnemy);
                float convoyThrottle = Mathf.Clamp(1f + ComputeAllyAvoidanceThrottle(myPos, myFwd), -1f, 1f);
                ca[0] = convoyThrottle;
                ca[1] = Mathf.Clamp(-brg * losSteerGain, -1f, 1f);
                return;
            }

            // ── SEPARATED: LOS 수직 좌/우 위치로 이동 (그물 포위) ──

            // 1. 역할 결정: 파트너가 오른쪽(brg<0) → 나는 LEFT(+), 반대면 RIGHT(-)
            float mySide = -1f;
            if (partnerAgent != null)
            {
                Vector3 toPartner = partnerAgent.transform.position - myPos; toPartner.y = 0f;
                float partnerBrg = ComputeSignedBearing(myFwd, toPartner);
                mySide = partnerBrg < 0f ? 1f : -1f;
            }

            // 2. LOS 수직 방향 (적→모선 방향의 CCW 90°)
            Vector3 toMother = Vector3.back;
            if (motherShip != null)
                toMother = motherShip.transform.position - enemyPos;
            toMother.y = 0f;
            if (toMother.sqrMagnitude > 0.01f) toMother.Normalize();
            Vector3 perpDir = new Vector3(-toMother.z, 0f, toMother.x);

            // 3. 목표점
            Vector3 goalPos = enemyPos + perpDir * (mySide * losOffset);

            // 4. 목표점까지 PD 조향
            Vector3 toGoal = goalPos - myPos; toGoal.y = 0f;
            float goalDist = toGoal.magnitude;
            float goalBrg  = goalDist > 0.5f ? ComputeSignedBearing(myFwd, toGoal) : 0f;

            float throttleAction = goalDist > 30f ? 1f : goalDist > 10f ? 0f : -1f;
            throttleAction = Mathf.Clamp(throttleAction + ComputeAllyAvoidanceThrottle(myPos, myFwd), -1f, 1f);

            float bearingRate = goalBrg - _prevGoalBrg;
            _prevGoalBrg = goalBrg;
            float steerAction = Mathf.Clamp(-(goalBrg * losSteerGain + bearingRate * losDerivGain), -1f, 1f);

            ca[0] = throttleAction;
            ca[1] = steerAction;
        }

        /// <summary>
        /// 다른 아군 페어의 webCenter 기준으로 LOS 방향 상 전방/후방 감지
        /// → 전방 페어 있으면 감속(음수), 후방 페어 있으면 가속(양수)
        /// </summary>
        private float ComputeAllyAvoidanceThrottle(Vector3 myPos, Vector3 myFwd)
        {
            if (envController == null || envController.launchZoneManager == null) return 0f;
            var lzm = envController.launchZoneManager;
            if (!lzm.IsInitialized) return 0f;

            // LOS 방향: 배정 타깃→모선 방향 (없으면 myFwd 그대로)
            Vector3 losDir = myFwd;
            GameObject target = GetAssignedEnemy();
            if (target != null && motherShip != null)
            {
                Vector3 ld = motherShip.transform.position - target.transform.position; ld.y = 0f;
                if (ld.sqrMagnitude > 0.01f) losDir = ld.normalized;
            }

            float mod = 0f;
            int poolCount = lzm.GetCurrentPoolCount();
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = lzm.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                // 자신이 속한 쌍(파트너 포함) 스킵
                if (pair.agent1 == this || pair.agent2 == this ||
                    pair.agent1 == partnerAgent || pair.agent2 == partnerAgent) continue;

                // 다른 쌍의 webCenter 기준으로 1회 체크
                Vector3 a1Pos = pair.agent1 != null ? pair.agent1.transform.position : Vector3.zero;
                Vector3 a2Pos = pair.agent2 != null ? pair.agent2.transform.position : a1Pos;
                Vector3 otherCenter = (a1Pos + a2Pos) * 0.5f;
                if (otherCenter.y < -100f) continue; // HIDDEN_POS 제외

                Vector3 toOther = otherCenter - myPos; toOther.y = 0f;
                float dist = toOther.magnitude;
                if (dist < 0.5f || dist > allyAvoidanceDist) continue;

                // LOS 방향 기준 전방/후방 판정
                float losDot = Vector3.Dot(losDir, toOther.normalized);
                float lateralDist = Mathf.Sqrt(Mathf.Max(0f,
                    toOther.sqrMagnitude - Mathf.Pow(losDot * dist, 2f)));
                if (lateralDist > allyAvoidanceLateralMax) continue;

                float proximity = 1f - dist / allyAvoidanceDist; // 가까울수록 1
                if (losDot > 0.5f)       mod += proximity * allyAccelStrength;   // 전방 아군 → 내가 가속 (따라붙기)
                else if (losDot < -0.5f) mod -= proximity * allyBrakingStrength; // 후방 아군 → 내가 감속 (간격 유지)
            }
            return Mathf.Clamp(mod, -1f, 1f);
        }

        /// <summary>활성 적군 중 Web 중심에서 가장 가까운 것 반환 (Heuristic fallback용)</summary>
        private GameObject FindNearestActiveEnemy()
        {
            if (enemyShips == null) return null;

            Vector3 webCenter = transform.position;
            if (partnerAgent != null)
                webCenter = (transform.position + partnerAgent.transform.position) * 0.5f;

            GameObject nearest = null;
            float minDist = float.MaxValue;
            foreach (var e in enemyShips)
            {
                if (e == null || !e.activeInHierarchy) continue;
                if (envController != null && envController.IsEnemyNeutralized(e)) continue;
                float d = Vector3.Distance(webCenter, e.transform.position);
                if (d < minDist) { minDist = d; nearest = e; }
            }
            return nearest;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_episodeEnded || _neutralized || _straightMode)
                return;

            // 적군 선박 충돌 → 포획으로 처리
            if (collision.gameObject.CompareTag("attack_boat"))
            {
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                DefenseEnvController ctrl = envRoot.GetComponentInChildren<DefenseEnvController>();
                if (ctrl != null)
                {
                    // webObject의 DynamicWeb을 넘겨서 개별 보너스도 부여
                    DynamicWeb dw = webObject != null ? webObject.GetComponent<DynamicWeb>() : null;
                    ctrl.OnEnemyHitWeb(collision.gameObject, dw);
                }
                return;
            }

            var otherAgent = collision.gameObject.GetComponent<DefenseAgent>();
            bool isMotherShip = collision.gameObject.CompareTag("MotherShip");

            if (otherAgent != null || isMotherShip)
            {
                // 파트너 아군 충돌: 페널티만 (비활성화 없음)
                if (otherAgent != null && otherAgent == partnerAgent)
                {
                    Transform envRoot = transform.parent != null ? transform.parent : transform;
                    DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();
                    if (envController != null)
                        envController.OnPartnerCollision(this);
                    return;
                }

                // 다른 쌍/모선 충돌: 기존 처리 (쌍 비활성화)
                {
                    Transform envRoot = transform.parent != null ? transform.parent : transform;
                    DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();
                    if (envController != null)
                        envController.OnFriendlyCollision(this);
                }
            }
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;

            // 자신: 파란 구
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, 2f);

            // 파트너: 초록 선
            if (partnerAgent != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, partnerAgent.transform.position);
            }

            // 적군: 배정된 적=노란 굵은 선, 나머지=어두운 빨간 선
            if (enemyShips != null)
            {
                GameObject assignedEnemy = GetAssignedEnemy();
                Vector3 webCenter = partnerAgent != null
                    ? (transform.position + partnerAgent.transform.position) * 0.5f
                    : transform.position;

                foreach (var enemy in enemyShips)
                {
                    if (enemy == null || !enemy.activeInHierarchy) continue;
                    if (enemy == assignedEnemy)
                    {
                        // 배정 매칭 — 밝은 노란선 (Web 중심 → 적)
                        Gizmos.color = new Color(1f, 0.9f, 0.1f, 1f);
                        Gizmos.DrawLine(webCenter, enemy.transform.position);
                        Gizmos.DrawWireSphere(enemy.transform.position, 4f);
                    }
                    else
                    {
                        // 비배정 — 희미한 빨간선
                        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.25f);
                        Gizmos.DrawLine(webCenter, enemy.transform.position);
                    }
                }
            }

            // Web 오브젝트: 노란 구
            if (webObject != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(webObject.transform.position, 3f);
            }
        }
    }
}
