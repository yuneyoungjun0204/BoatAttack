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
        public DefenseAgent partnerAgent;
        [Tooltip("One-Way Towing 앵커 Transform (LaunchZoneManager에서 자동 할당)")]
        [HideInInspector] public Transform anchorTransform;
        public GameObject motherShip;
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
        [Range(1f, 2000f)] public float enemyNormK = 500f;

        [Header("=== LOS Observation ===")]
        [Tooltip("true: 적→모선 LOS 기반 관측 (perp, along, hdg) / false: 기존 (dist, bearing, hdg)")]
        public bool useLOSObservation = true;

        [Tooltip("LOS 위 예측 지점까지 거리 (m). 적이 이 거리만큼 전진한 위치를 기준으로 차단 위치를 계산")]
        [Range(10f, 300f)] public float losLookAheadDist = 50f;

        [Header("Observation Scale (정규화 후 가중치)")]
        [Tooltip("베어링 유리함수 k값 (작을수록 정면 민감도↑, 0.5지점=k도)")]
        [Range(5f, 90f)] public float bearingNormK = 30f;

        [Header("Enemy Observation Scale (적군 관측 계수)")]
        [Tooltip("일반 포획 모드에서 EnemyBufferSensor에 넣을 최대 적 수 (거리순). SingleNet 모드는 배정 적 1개만 고정.")]
        public int maxEnemyObsNormal = 5;

        [Tooltip("LOS 모드: obs[0] = LOS 수직 편차(perp) 가중치 / 기존 모드: 거리 가중치")]
        [Range(1f, 10f)] public float enemyDistScale = 1f;
        [Tooltip("LOS 모드: obs[1] = LOS 전진 편차(along) 가중치 / 기존 모드: 베어링 가중치")]
        [Range(1f, 10f)] public float enemyBearingScale = 1f;
        [Range(1f, 10f)] public float enemyHeadingScale = 1f;

        [Header("Ally Observation Scale (아군 관측 계수)")]
        [Range(1f, 10f)] public float allyDistScale = 1f;
        [Range(1f, 10f)] public float allyBearingScale = 1f;
        [Range(1f, 10f)] public float allyHdgScale = 1f;

        [Header("Self State (자기 기동 상태)")]
        [Range(1f, 50f)] public float speedNormK = 10f;
        [Range(0f, 5f)] public float forwardSpeedScale = 1f;
        [Range(0f, 5f)] public float driftSpeedScale = 1f;

        [Header("Ally Pair NormK")]
        [Range(1f, 1000f)] public float allyPairNormK = 100f;

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

        // Split mode: 적 근접 시 강제 분리 조향
        private bool _splitMode = false;
        private float _splitSteer = 0f;
        private int _splitStepsRemaining = 0;
        private float _splitInitialLateralSign = 0f; // [C] split 시작 시 파트너의 내 right축 부호 스냅샷
        private SingleNetCapture _singleNetCapture;  // Disarm 후 소형 포획 존
        private bool _singleNetMode = false;          // SingleNet 포획 모드 (LOS 방향 반전)
        private bool _stopMode = false;               // Phase1: 트랩 설치 후 정지 (RL 액션 무시)
        private bool _towMode = false;                // ONE-WAY TOWING: LOS 수직 방향 직진
        private Vector3 _towWorldDir = Vector3.right; // 횡단 방향 (world)

        // PD LOS 가이던스 (배치 후 일정 시간 동안 클러스터 방향으로 직진)
        [Header("=== Formation Spread (LOS offset, 0=RL 대형학습) ===")]
        [Tooltip("LOS 기준선에서 좌/우로 벌리는 거리 (m). isLeftAgent에 따라 좌/우 바이어스. 권장: 50m")]
        [Range(0f, 200f)] public float losSpreadDistance = 50f;
        [Tooltip("LOS 타겟 방향으로부터 선수가 벗어날 수 있는 최대 각도 (°). 0=비활성화")]
        [Range(0f, 180f)] public float losHeadingClampDeg = 60f;
        [Tooltip("true=왼쪽 선박(agent1), false=오른쪽 선박(agent2). LaunchZoneManager에서 자동 설정")]
        public bool isLeftAgent = true;

        [Header("=== PD LOS Guidance ===")]
        [Tooltip("배치 후 PD 가이던스 유지 시간 (초). 0이면 즉시 RL 전환")]
        public float guidanceDuration = 0f;
        [Tooltip("PD 가이던스 조향 비례 게인 (Kp)")]
        [Range(0f, 10f)]
        public float guidanceKp = 1.5f;
        [Tooltip("PD 가이던스 조향 미분 게인 (Kd)")]
        [Range(0f, 10f)]
        public float guidanceKd = 0.3f;

        [Header("=== Residual RL (접근 구간 행동 결합) ===")]
        [Tooltip("RL 비중. 접근 구간 행동 = (1-s)*LOS추종 + s*RL.  0=순수 LOS 추종(scripted), 1=순수 RL, 0.5=반반. ablation용 연속 스위치")]
        [Range(0f, 1f)]
        public float residualScale = 0.5f;

        private bool _guidancePhase = false;
        private float _guidanceEndTime = 0f;
        private Vector3 _guidanceTarget = Vector3.zero;
        private float _prevBearingError = 0f;

        // OnEpisodeBegin에 의해 초기화되지 않는 클러스터 타겟 (residual policy용)
        private Vector3 _clusterTarget = Vector3.zero;

        // LOS 베이스라인 조향 캐시 (CollectObservations에서 관측용)
        private float _cachedLOSBaseline = 0f;
        private float _cachedLOSThrottleBaseline = 0f; // 선회 각도 기반 속도 정답지

        // LOS 가이던스 시각화
        private Vector3 _losFootPoint      = Vector3.zero;
        private Vector3 _losLookAheadPoint = Vector3.zero;

        [Header("=== LOS Visualization ===")]
        [Tooltip("게임 뷰에서 LOS 가이던스 라인/마커 표시")]
        public bool showLOSVisualization = true;

        private LineRenderer _losLine;      // 적→모선 전체 LOS
        private LineRenderer _losFootLine;  // 에이전트→foot 수선
        private LineRenderer _losAheadLine; // foot→P
        private LineRenderer _losSteerLine; // 에이전트→P (조향 방향)
        private Transform    _losMarker;    // P 위치 구체 마커

        [Header("=== Safety Layer (충돌 회피) ===")]
        [Tooltip("splitMode 중 타 페어 APF 척력 활성 반경 (m). 권장: 30m")]
        public float safetyRadius = 30f;
        [Tooltip("APF 척력 조향 혼합 강도. 0=비활성, 1=최대. 권장: 0.8")]
        [Range(0f, 2f)] public float safetyRepulsionScale = 0.8f;

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
            enemyBufferSensor.ObservableSize = 3;   // Dist, SignedBrg, ClusterSize (heading 제거, 클러스터 관측)
            enemyBufferSensor.MaxNumObservables = enemyMaxObservables;

            // 아군 쌍 BufferSensor
            var buffers = GetComponents<BufferSensorComponent>();
            allyBufferSensor = buffers.Length > 1 ? buffers[1] : null;
            if (allyBufferSensor == null)
                allyBufferSensor = gameObject.AddComponent<BufferSensorComponent>();
            allyBufferSensor.SensorName = "AllyBufferSensor";
            allyBufferSensor.ObservableSize = 4;   // dist, bearing, hdgCos, hdgSin
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

            // LOS 시각화 LineRenderer 생성
            _losLine      = CreateLineRenderer("_LOS_Full",  new Color(0.5f, 0.5f, 0.5f, 0.4f), 0.3f);
            _losFootLine  = CreateLineRenderer("_LOS_Foot",  new Color(1f,   1f,   1f,   0.7f), 0.4f);
            _losAheadLine = CreateLineRenderer("_LOS_Ahead", new Color(0f,   1f,   0.8f, 1f),   0.6f);
            _losSteerLine = CreateLineRenderer("_LOS_Steer", new Color(1f,   0.8f, 0f,   1f),   0.5f);

            // look-ahead 점 P 마커 (구체)
            var markerGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            markerGo.name = "_LOS_Marker";
            markerGo.transform.SetParent(transform.parent ?? transform, false);
            markerGo.transform.localScale = Vector3.one * 4f;
            Destroy(markerGo.GetComponent<Collider>());
            var mr = markerGo.GetComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.color = new Color(0f, 1f, 0.8f, 1f);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _losMarker = markerGo.transform;
            _losMarker.gameObject.SetActive(false);

            // SingleNetCapture: 없으면 자동 추가 (기본 비활성 상태)
            _singleNetCapture = GetComponent<SingleNetCapture>();
            if (_singleNetCapture == null)
                _singleNetCapture = gameObject.AddComponent<SingleNetCapture>();
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

        private void UpdateLOSVisualizers()
        {
            if (!showLOSVisualization || !Application.isPlaying)
            {
                SetLOSVisualizersActive(false);
                return;
            }

            bool hasLookAhead  = _losLookAheadPoint != Vector3.zero;
            bool isConvoyOrGuidance = _guidancePhase && _guidanceTarget != Vector3.zero;
            float h = 2f;

            if (hasLookAhead && isConvoyOrGuidance && motherShip != null)
            {
                // 정지트랩 설치: 클러스터→모선 기준선 (회색)
                SetLine(_losLine, _guidanceTarget + Vector3.up * h,
                                  motherShip.transform.position + Vector3.up * h);
                // 에이전트 → foot (흰색 수선)
                SetLine(_losFootLine, transform.position + Vector3.up * h,
                                      _losFootPoint       + Vector3.up * h);
                // foot → P (청록, look-ahead)
                SetLine(_losAheadLine, _losFootPoint + Vector3.up * h, _losLookAheadPoint + Vector3.up * h);
                // 에이전트 → P (노랑, 조향)
                SetLine(_losSteerLine, transform.position + Vector3.up * h, _losLookAheadPoint + Vector3.up * h);
                _losMarker.position = _losLookAheadPoint + Vector3.up * h;
                SetLOSVisualizersActive(true);
            }
            else if (hasLookAhead && _heuristicNearestEnemy != null)
            {
                // 일반 RL 구간: 에이전트 → 적군 (기준선 회색)
                SetLine(_losLine,      transform.position + Vector3.up * h,
                                       _heuristicNearestEnemy.transform.position + Vector3.up * h);
                if (_losFootLine) _losFootLine.enabled = false;
                SetLine(_losAheadLine, transform.position + Vector3.up * h, _losLookAheadPoint + Vector3.up * h);
                SetLine(_losSteerLine, transform.position + Vector3.up * h, _losLookAheadPoint + Vector3.up * h);
                _losMarker.position = _losLookAheadPoint + Vector3.up * h;
                SetLOSVisualizersActive(true);
            }
            else
            {
                SetLOSVisualizersActive(false);
            }
        }

        private void SetLine(LineRenderer lr, Vector3 from, Vector3 to)
        {
            if (lr == null) return;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.enabled = true;
        }

        private void SetLOSVisualizersActive(bool active)
        {
            if (_losLine)      _losLine.enabled      = active;
            if (_losFootLine)  _losFootLine.enabled  = active;
            if (_losAheadLine) _losAheadLine.enabled = active;
            if (_losSteerLine) _losSteerLine.enabled = active;
            if (_losMarker)    _losMarker.gameObject.SetActive(active);
        }

        protected override void OnEnable()
        {
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

        public bool IsSplitting => _splitMode;

        /// <summary>
        /// 적 근접 분리 모드: durationSteps 동안 steerOverride 방향으로 전속력 조향
        /// </summary>
        public void SetSplitMode(bool active, float steerOverride = 0f, int durationSteps = 80)
        {
            _splitMode = active;
            _splitSteer = steerOverride;
            _splitStepsRemaining = active ? durationSteps : 0;

            // [C] split 시작 시 파트너의 내 right축 부호 기록 (교차 감지 기준)
            if (active && partnerAgent != null)
            {
                Vector3 toPartner = partnerAgent.transform.position - transform.position;
                toPartner.y = 0f;
                float lateral = Vector3.Dot(toPartner, transform.right);
                _splitInitialLateralSign = lateral >= 0f ? 1f : -1f;
            }
        }

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

        /// <summary>Disarm 이후 소형 포획 존 활성화 + LOS 방향 반전</summary>
        public bool IsSingleNetMode => _singleNetMode;

        public void ActivateSingleNet()
        {
            if (_singleNetCapture == null) return;
            if (envController != null) _singleNetCapture.Init(envController);
            _singleNetCapture.Activate();
            _singleNetMode = true;
        }

        /// <summary>에피소드 리셋 시 소형 포획 존 비활성화</summary>
        public void DeactivateSingleNet()
        {
            if (_singleNetCapture != null) _singleNetCapture.Deactivate();
            _singleNetMode = false;
        }

        /// <summary>Phase1 정지 모드: true이면 RL 액션 무시 + Engine 물리 전면 차단</summary>
        public void SetStopMode(bool active)
        {
            _stopMode = active;
            if (_engine != null) _engine.hardStopped = active;
            if (active) _towMode = false;  // stop 진입 시 tow 해제
        }

        /// <summary>ONE-WAY TOWING: LOS 수직 world 방향으로 직진. false 전달 시 해제.</summary>
        public void SetTowMode(bool active, Vector3 towWorldDir = default)
        {
            _towMode = active;
            if (active)
            {
                _towWorldDir = towWorldDir.sqrMagnitude > 0.001f ? towWorldDir.normalized : Vector3.right;
                _splitMode = false;  // split 모드와 충돌 방지
            }
        }

        /// <summary>
        /// Stage9: 직진 이탈 중 엔진 구동 (UnregisterAgent 후 OnActionReceived가 호출되지 않으므로 직접 구동)
        /// </summary>
        private void LateUpdate()
        {
            UpdateMatchingLines();
            UpdateLOSVisualizers();
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
        /// 런타임 배치 시 에이전트 상태 리셋
        /// OnEpisodeBegin과 달리 ML-Agents 에피소드를 건드리지 않고 내부 플래그만 초기화
        /// </summary>
        public void ResetForDeployment()
        {
            _episodeEnded = false;
            _neutralized = false;
            _straightMode = false;
            _splitMode = false;
            _splitSteer = 0f;
            _splitStepsRemaining = 0;
            _towMode = false;
            SetStopMode(false);
            _guidancePhase = false;
            _guidanceEndTime = 0f;
            _guidanceTarget = Vector3.zero;
            _prevBearingError = 0f;
            assignedTargetIndex = -1;
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
            DeactivateSingleNet();
        }

        /// <summary>
        /// PD LOS 가이던스 시작 — LaunchZoneManager에서 배치 직후 호출
        /// </summary>
        public void StartGuidance(Vector3 worldTarget)
        {
            if (guidanceDuration <= 0f) return;
            _guidanceTarget   = worldTarget;
            _guidanceEndTime  = Time.time + guidanceDuration;
            _guidancePhase    = true;
            _prevBearingError = 0f;
        }

        public void StartGuidanceForSteps(Vector3 worldTarget, int steps)
        {
            if (steps <= 0) return;
            _guidanceTarget   = worldTarget;
            _guidanceEndTime  = Time.time + steps * Time.fixedDeltaTime;
            _guidancePhase    = true;
            _prevBearingError = 0f;
        }

        /// <summary>
        /// LOS 클러스터 타겟 설정. OnEpisodeBegin에서 Vector3.zero로 초기화됨.
        /// </summary>
        public void SetClusterTarget(Vector3 worldTarget)
        {
            _clusterTarget = worldTarget;
        }

        public override void OnEpisodeBegin()
        {
            _episodeEnded = false;
            _splitMode = false;
            _splitSteer = 0f;
            _splitStepsRemaining = 0;
            _splitInitialLateralSign = 0f;
            _towMode = false;
            SetStopMode(false);
            _guidancePhase = false;
            _guidanceEndTime = 0f;
            _guidanceTarget = Vector3.zero;
            _clusterTarget = Vector3.zero;
            DeactivateSingleNet();          // _singleNetMode = false + SingleNetCapture zone 비활성화
            assignedTargetIndex = -1;
            // _neutralized는 여기서 리셋하지 않음
            // SetNeutralized(false)로만 해제 (DeployPairs/ResetScene에서 호출)
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
            // chase 모드: Phase2 직접 학습 — SingleNet + 가이던스 복원
            // isLeftAgent는 OnEpisodeBegin에서 리셋되지 않으므로 그대로 사용
            if (envController != null && envController.chaseTrainingMode)
            {
                ActivateSingleNet();
                _prevThrottle = 0.5f;

                // isLeftAgent 기반으로 배정 복원 (agent1=left=idx0, agent2=right=idx1)
                int tgtIdx = isLeftAgent ? 0 : 1;
                if (!isLeftAgent && (enemyShips == null || enemyShips.Length < 2))
                    tgtIdx = 0;
                assignedTargetIndex = tgtIdx + 1;  // 1-based

                // LOS 보상 활성화: _clusterTarget이 non-zero여야 ComputeLOSBaseline이 실행됨
                // SingleNet 모드에서는 실제 계산 시 GetAssignedEnemy()로 대체되므로 값 자체는 무관
                if (enemyShips != null && tgtIdx < enemyShips.Length && enemyShips[tgtIdx] != null)
                    SetClusterTarget(enemyShips[tgtIdx].transform.position);

                // 적 방향으로 가이던스 시작
                int gSteps = envController.chaseGuidanceSteps;
                if (gSteps > 0 && enemyShips != null
                    && tgtIdx < enemyShips.Length
                    && enemyShips[tgtIdx] != null
                    && enemyShips[tgtIdx].activeInHierarchy)
                {
                    StartGuidanceForSteps(enemyShips[tgtIdx].transform.position, gSteps);
                }
            }
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
            return Mathf.Cos(delta * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>
        /// Residual Policy 베이스라인 조향 계산.
        /// 가이던스 중: _guidanceTarget 방향 PD
        /// RL 구간: 가장 가까운 적(_heuristicNearestEnemy) 기준 LOS look-ahead P 방향 P제어
        /// 반환: [-1, 1] 스티어링값
        /// </summary>
        private float ComputeLOSBaselineSteering()
        {
            Vector3 steerTarget = Vector3.zero;
            bool valid = false;

            if (_clusterTarget != Vector3.zero)
            {
                // SingleNet 모드: 배정된 적 위치를 centroid로 사용 (클러스터 centroid 무시)
                // 정지트랩 모드: 원래 클러스터 centroid 사용
                Vector3 resolvedCentroid = _clusterTarget;
                if (_singleNetMode)
                {
                    GameObject assignedEnemy = GetAssignedEnemy();
                    if (assignedEnemy != null && assignedEnemy.activeInHierarchy)
                        resolvedCentroid = assignedEnemy.transform.position;
                }

                if (motherShip != null)
                {
                    Vector3 cPos = resolvedCentroid;                 cPos.y = 0f;  // centroid
                    Vector3 mPos = motherShip.transform.position;    mPos.y = 0f;  // 모선
                    Vector3 aPos = transform.position;               aPos.y = 0f;

                    Vector3 losVec = mPos - cPos;   // 클러스터→모선
                    float losDist  = losVec.magnitude;
                    if (losDist > 0.1f)
                    {
                        Vector3 losDir = losVec / losDist;       // 클러스터→모선 단위벡터
                        // 정지트랩: 적 마중(-losDir), 포획트랩: 적과 같은 방향(+losDir)
                    Vector3 toClusterDir = _singleNetMode ? losDir : -losDir;

                        // 에이전트를 기준선에 투영 → foot F
                        float proj = Vector3.Dot(aPos - cPos, losDir);
                        proj = Mathf.Clamp(proj, 0f, losDist);
                        Vector3 foot = cPos + losDir * proj;

                        // F에서 클러스터 방향으로 look-ahead
                        float toClusterDist = Vector3.Distance(foot, cPos);
                        float ahead = Mathf.Min(losLookAheadDist, toClusterDist * 0.98f);
                        steerTarget = foot + toClusterDir * ahead;

                        // 좌/우 벌림 offset (ONE-attack 방식: 두 선박이 LOS 기준선 좌우로 분산)
                        if (losSpreadDistance > 0f && !_singleNetMode)
                        {
                            // perpRight = up × losDir (LOS 기준 오른쪽)
                            // isLeftAgent=true → side=-1 → steerTarget -= perpRight → 왼쪽
                            // isLeftAgent=false → side=+1 → steerTarget += perpRight → 오른쪽
                            Vector3 perpRight = new Vector3(losDir.z, 0f, -losDir.x);
                            float side = isLeftAgent ? -1f : 1f;
                            steerTarget -= perpRight * side * losSpreadDistance;
                        }

                        _losFootPoint      = foot;
                        _losLookAheadPoint = steerTarget;
                        valid = true;
                    }
                }

                // fallback: 모선 없으면 클러스터 직접 향함
                if (!valid)
                {
                    Vector3 toCluster = _clusterTarget - transform.position;
                    toCluster.y = 0f;
                    float clusterDist = toCluster.magnitude;
                    if (clusterDist > 0.1f)
                    {
                        Vector3 d = toCluster / clusterDist;
                        steerTarget = transform.position + d * Mathf.Min(losLookAheadDist, clusterDist * 0.98f);
                        _losFootPoint      = transform.position;
                        _losLookAheadPoint = steerTarget;
                        valid = true;
                    }
                    else
                    {
                        steerTarget = _clusterTarget;
                        _losLookAheadPoint = steerTarget;
                        valid = true;
                    }
                }
            }
            else
            {
                // 클러스터 미배정: 배정된 적군(없으면 최근접 적) 기준 LOS
                GameObject targetEnemy = GetAssignedEnemy();
                if (targetEnemy != null && targetEnemy.activeInHierarchy)
                {
                    if (_singleNetMode)
                    {
                        // 포획 트랩: 적의 forward 방향으로 이동 (적과 같은 방향으로 달림)
                        Vector3 enemyFwd = targetEnemy.transform.forward;
                        enemyFwd.y = 0f;
                        if (enemyFwd.sqrMagnitude > 0.01f)
                        {
                            steerTarget = transform.position + enemyFwd.normalized * losLookAheadDist;
                            _losFootPoint = transform.position;
                            _losLookAheadPoint = steerTarget;
                            valid = true;
                        }
                    }
                    else
                    {
                        // 정지 트랩: 적 위치를 클러스터 centroid로 삼아 LOS 기준선(적→모선) 추종
                        Vector3 cPos = targetEnemy.transform.position; cPos.y = 0f;
                        Vector3 aPos = transform.position;             aPos.y = 0f;

                        if (motherShip != null)
                        {
                            Vector3 mPos = motherShip.transform.position; mPos.y = 0f;
                            Vector3 losVec = mPos - cPos;
                            float losDist = losVec.magnitude;
                            if (losDist > 0.1f)
                            {
                                Vector3 losDir = losVec / losDist;
                                Vector3 toClusterDir = -losDir; // 적 마중 방향

                                float proj = Mathf.Clamp(Vector3.Dot(aPos - cPos, losDir), 0f, losDist);
                                Vector3 foot = cPos + losDir * proj;

                                float toClusterDist = Vector3.Distance(foot, cPos);
                                float ahead = Mathf.Min(losLookAheadDist, toClusterDist * 0.98f);
                                steerTarget = foot + toClusterDir * ahead;

                                // 좌/우 spread offset (클러스터 경로와 동일 부호)
                                if (losSpreadDistance > 0f)
                                {
                                    Vector3 perpRight = new Vector3(losDir.z, 0f, -losDir.x);
                                    float side = isLeftAgent ? -1f : 1f;
                                    steerTarget += perpRight * side * losSpreadDistance;
                                }

                                _losFootPoint      = foot;
                                _losLookAheadPoint = steerTarget;
                                valid = true;
                            }
                        }

                        // fallback: 모선 없으면 적 위치로 직접
                        if (!valid)
                        {
                            Vector3 toEnemy = cPos - aPos;
                            float dist = toEnemy.magnitude;
                            if (dist > 0.1f)
                            {
                                steerTarget = aPos + (toEnemy / dist) * Mathf.Min(losLookAheadDist, dist * 0.98f);
                                _losFootPoint = aPos;
                                _losLookAheadPoint = steerTarget;
                                valid = true;
                            }
                        }
                    }
                }
            }

            if (!valid) return 0f;

            Vector3 toTarget = steerTarget - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.1f) return 0f;

            float targetAngle = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            float bearingError = Mathf.DeltaAngle(transform.eulerAngles.y, targetAngle) / 180f; // [-1,1]

            float dError = (bearingError - _prevBearingError) / Mathf.Max(Time.fixedDeltaTime, 0.001f);
            float result = Mathf.Clamp(guidanceKp * bearingError + guidanceKd * dError, -1f, 1f);
            _prevBearingError = bearingError;
            return result;
        }

        /// <summary>
        /// LOS 타겟 방향 기반 속도 정답지: 선회 각도가 클수록 cos 감쇠로 감속
        /// ComputeLOSBaselineSteering() 호출 후 _losLookAheadPoint가 설정된 상태에서 호출할 것
        /// </summary>
        private float ComputeLOSBaselineThrottle()
        {
            Vector3 toTarget = _losLookAheadPoint - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.1f) return 0f;

            float targetAngle = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            float absBrg = Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, targetAngle)) / 180f; // [0, 1]
            return maxThrottle * Mathf.Cos(absBrg * Mathf.PI * 0.5f);
        }

        /// <summary>
        /// <summary>
        /// 타 페어 에이전트로부터의 APF 척력 조향값 [-1,1] 반환
        /// splitMode [A] Safety Layer에서 사용
        /// </summary>
        private float ComputeInterPairRepulsionSteering(float radius)
        {
            if (envController == null || envController.launchZoneManager == null) return 0f;

            Vector3 repulsion = Vector3.zero;
            Vector3 myPos = transform.position;

            int cap = envController.launchZoneManager.GetPoolCapacity();
            for (int i = 0; i < cap; i++)
            {
                var pair = envController.launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                if (pair.agent1 == this) continue;
                if (pair.agent1 == null) continue;

                Vector3 toOther = pair.agent1.transform.position - myPos;
                toOther.y = 0f;
                float d = toOther.magnitude;
                if (d >= 0.1f && d <= radius)
                    repulsion += -(toOther / (d * d));
            }

            if (repulsion.sqrMagnitude < 0.0001f) return 0f;
            return Mathf.Clamp(Vector3.Dot(repulsion.normalized, transform.right), -1f, 1f);
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
        /// 적→모선 LOS 기반 관측값 계산.
        /// LOS 선 위 look-ahead 지점 P 기준으로 에이전트의 수직/전진 편차를 반환.
        ///
        /// perpNorm : LOS에 수직인 방향 편차 (0 = LOS 선 위, ±1 = 크게 벗어남)
        ///            부호: LOS 오른쪽(+) / 왼쪽(-)  [Cross(LOSdir, up) 기준]
        /// alongNorm: look-ahead 포인트 P 기준 전진(+) / 후퇴(-) 편차
        ///            (+) = P를 지나 모선 쪽 / (-) = 아직 P에 못 미침
        ///
        /// 학습 목표: perpNorm≈0, alongNorm≈0 → 완벽한 예측 차단 위치
        /// </summary>
        private (float perpNorm, float alongNorm) ComputeLOSObs(
            Vector3 enemyPos, Vector3 observerPos, Vector3 motherPos, float lookAheadDist, float normK)
        {
            // y 평탄화
            enemyPos.y    = 0f;
            observerPos.y = 0f;
            motherPos.y   = 0f;

            Vector3 LOSvec = motherPos - enemyPos;
            float LOSdist  = LOSvec.magnitude;
            if (LOSdist < 0.1f) return (0f, 0f); // degenerate: 적이 모선 위에 있음

            Vector3 LOSdir  = LOSvec / LOSdist;

            // LOS 오른쪽 수직벡터 (Cross(LOSdir, up) → LOSdir 기준 오른쪽)
            Vector3 LOSperp = Vector3.Cross(LOSdir, Vector3.up).normalized;

            // look-ahead 포인트 P — 모선을 넘지 않도록 clamp
            float ahead = Mathf.Min(lookAheadDist, LOSdist * 0.9f);
            Vector3 P   = enemyPos + LOSdir * ahead;

            // 수직 편차: 관측자가 LOS 선에서 얼마나 옆으로 벗어났나
            float signedPerp = Vector3.Dot(observerPos - enemyPos, LOSperp);

            // 전진 편차: 관측자가 P 기준 얼마나 앞/뒤에 있나
            float alongLOS = Vector3.Dot(observerPos - P, LOSdir);

            float perpNorm  = signedPerp / (Mathf.Abs(signedPerp) + normK);
            float alongNorm = alongLOS   / (Mathf.Abs(alongLOS)   + normK);

            return (perpNorm, alongNorm);
        }

        /// <summary>
        /// 가장 가까운 활성 적군 반환
        /// </summary>
        public GameObject GetAssignedEnemy()
        {
            // 배정된 타겟 우선
            if (assignedTargetIndex > 0 && enemyShips != null)
            {
                int idx = assignedTargetIndex - 1;
                if (idx < enemyShips.Length && enemyShips[idx] != null && enemyShips[idx].activeInHierarchy)
                    return enemyShips[idx];
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
        /// 관측 수집 (VectorSensor 2개 + AllyBufferSensor + EnemyBufferSensor)
        /// VectorSensor(2): motherDistNorm, LOSBaseline
        /// AllyBufferSensor: 타 아군쌍+트랩, 각 4개 (dist, bearing, hdgCos, hdgSin)
        /// EnemyBufferSensor: 적 각도 클러스터(일반) 또는 배정 적 1대(SingleNet), 각 3개 (Dist, SignedBrg, ClusterSize)
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            collectObsCallCount++;

            // VectorSensor 6개:
            //  [0] isLeftAgent (0/1) — RL 대형 대칭 파괴용
            //  [1] motherDistNorm   — 모선 거리
            //  [2] partner dist     — 파트너 거리 정규화
            //  [3] partner hdg      — 파트너 헤딩차
            //  [4] partner bearing  — 파트너 베어링
            //  [5] LOSBaseline      — LOS 조향 명령 [-1,1]
            // VectorSensor 3개 (RL 정책이 실제 조종하는 '접근' 구간에 유효한 것만):
            //  [0] 모선 거리, [1] LOS 베이스라인 조향, [2] 의도 드리프트 방향(+1 우/-1 좌)
            // (tow모드/전개진행률/앵커베어링/그물⊥적 = 스크립트 구간 전용이라 제거)
            const int VECTOR_OBS_COUNT = 3;
            if (lastObservations == null || lastObservations.Length < VECTOR_OBS_COUNT)
                lastObservations = new float[VECTOR_OBS_COUNT];
            int oi = 0;

            if (_engine == null || _engine.RB == null)
            {
                for (int z = 0; z < VECTOR_OBS_COUNT; z++) sensor.AddObservation(0f);
                lastObservationsCount = 0;
                return;
            }

            // [0] 모선 거리
            float motherDistNorm = 0f;
            if (motherShip != null)
            {
                Vector3 toMother = motherShip.transform.position - transform.position;
                toMother.y = 0f;
                float _normK = (envController != null) ? envController.enemyNormK : enemyNormK;
                motherDistNorm = _normK / (toMother.magnitude + _normK);
            }
            sensor.AddObservation(motherDistNorm);
            lastObservations[0] = motherDistNorm;

            // [1] LOS 베이스라인 조향 명령 [-1, 1]
            sensor.AddObservation(_cachedLOSBaseline);
            lastObservations[1] = _cachedLOSBaseline;

            // [2] 의도 드리프트(그물 전개 스윕) 방향 부호 (+1 우 / -1 좌) — 접근 단계에서 사전 인지
            float driftSign = 0f;
            if (envController != null)
            {
                Vector3 fwdD = transform.forward; fwdD.y = 0f;
                if (fwdD.sqrMagnitude > 0.001f)
                    driftSign = envController.ComputeTowDirSign(transform.position, fwdD.normalized);
            }
            sensor.AddObservation(driftSign);
            lastObservations[2] = driftSign;

            oi = VECTOR_OBS_COUNT;

            // Web 중심 기준: 자기 쌍의 agent1+agent2 중점을 관측 원점으로 사용
            Vector3 partnerPos = (partnerAgent != null) ? partnerAgent.transform.position : transform.position;
            Vector3 webCenter = (transform.position + partnerPos) * 0.5f;
            Vector3 avgForward = (transform.forward + (partnerAgent != null ? partnerAgent.transform.forward : transform.forward)) * 0.5f;
            avgForward.y = 0f;  // pitch 영향 제거, XZ 평면(yaw)만 사용
            Vector3 webForward = avgForward.sqrMagnitude > 0.01f ? avgForward.normalized : transform.forward;
            float webAngle = Quaternion.LookRotation(webForward).eulerAngles.y;

            // 4. AllyBufferSensor: 가까운 아군 쌍 최대 3개
            CollectAllyPairBufferObs(webCenter, webForward);

            // 5. EnemyBufferSensor — 자신에게 할당된 클러스터(또는 배정 적 1대)만 관측
            //   1 엔티티 × 3필드: { 거리, 방위(signed bearing), 각도 스프레드(클러스터가 몇 도에 퍼졌나) }
            //   - SingleNet/배정 모드: 배정된 적 1대만 정밀 관측 (스프레드=0)
            //   - 일반(접근) 모드: 이 쌍에 배정된 클러스터(pair.clusterEnemyIndices)의 centroid + 각도폭
            //   각도 스프레드 = 멤버들의 모선 기준 방위각 (max−min), 매 스텝 현재 위치로 재계산 → /90 정규화
            //   할당 클러스터가 비었거나(전멸) 없으면 → 최근접 활성 적 1대로 폴백 (스프레드=0)
            lastEnemyBufferObs.Clear();
            if (enemyBufferSensor != null && enemyShips != null)
            {
                float normK  = (envController != null) ? envController.enemyNormK        : enemyNormK;
                float eDistS = (envController != null) ? envController.enemyDistScale    : enemyDistScale;
                float eBrgS  = (envController != null) ? envController.enemyBearingScale : enemyBearingScale;

                GameObject assigned = _singleNetMode ? GetAssignedEnemy() : null;

                if (assigned != null && assigned.activeInHierarchy)
                {
                    // ── 배정 적 1대 정밀 관측 (막판 SingleNet 포획) ──
                    Vector3 rel = assigned.transform.position - webCenter;
                    float obs0 = NormalizePosition(rel.magnitude, normK) * eDistS;
                    float obs1 = ComputeSignedBearing(webForward, rel) * eBrgS;
                    float obs2 = 0f;   // 단일 표적 → 스프레드 없음
                    AppendEnemyObs(obs0, obs1, obs2);
                    _heuristicNearestEnemy = assigned;
                }
                else
                {
                    // ── 자신에게 할당된 클러스터만 관측 ──
                    // 내 쌍의 clusterEnemyIndices(배치 시 배정) → 활성 멤버로 centroid·count 재계산
                    List<int> myCluster = null;
                    var lzm = (envController != null) ? envController.launchZoneManager : null;
                    if (lzm != null && lzm.IsInitialized)
                    {
                        int pi = lzm.FindPairIndex(this);
                        if (pi >= 0)
                        {
                            var p = lzm.GetPair(pi);
                            if (p != null) myCluster = p.clusterEnemyIndices;
                        }
                    }

                    // 각도 스프레드 계산용 모선 기준점
                    Vector3 motherPos = (motherShip != null) ? motherShip.transform.position : webCenter;

                    Vector3 sum = Vector3.zero;
                    int activeCount = 0;
                    GameObject nearest = null; float nearestD = float.MaxValue;
                    // 멤버들의 모선 기준 방위각 범위(스프레드)용: 첫 멤버 각도를 기준으로 DeltaAngle 누적
                    float refAngle = 0f; bool haveRef = false;
                    float minDelta = 0f, maxDelta = 0f;
                    if (myCluster != null)
                    {
                        for (int k = 0; k < myCluster.Count; k++)
                        {
                            int idx = myCluster[k];
                            if (idx < 0 || idx >= enemyShips.Length) continue;
                            var e = enemyShips[idx];
                            if (e == null || !e.activeInHierarchy) continue;
                            if (envController != null && envController.IsEnemyNeutralized(e)) continue;
                            sum += e.transform.position;
                            activeCount++;
                            float d = Vector3.Distance(webCenter, e.transform.position);
                            if (d < nearestD) { nearestD = d; nearest = e; }

                            // 모선 기준 방위각 → 스프레드 누적 (wraparound은 DeltaAngle로 처리)
                            Vector3 relM = e.transform.position - motherPos; relM.y = 0f;
                            float angM = Mathf.Atan2(relM.x, relM.z) * Mathf.Rad2Deg;
                            if (!haveRef) { refAngle = angM; haveRef = true; }
                            else
                            {
                                float dlt = Mathf.DeltaAngle(refAngle, angM);
                                if (dlt < minDelta) minDelta = dlt;
                                if (dlt > maxDelta) maxDelta = dlt;
                            }
                        }
                    }

                    // 폴백: 할당 클러스터가 비었거나 없음 → 최근접 활성 적 1대
                    if (activeCount == 0)
                    {
                        for (int i = 0; i < enemyShips.Length; i++)
                        {
                            var e = enemyShips[i];
                            if (e == null || !e.activeInHierarchy) continue;
                            if (envController != null && envController.IsEnemyNeutralized(e)) continue;
                            float d = Vector3.Distance(webCenter, e.transform.position);
                            if (d < nearestD) { nearestD = d; nearest = e; }
                        }
                        if (nearest != null)
                        {
                            sum = nearest.transform.position;
                            activeCount = 1;
                        }
                    }

                    if (activeCount > 0)
                    {
                        Vector3 centroid = sum / activeCount;
                        Vector3 rel = centroid - webCenter;
                        float obs0 = NormalizePosition(rel.magnitude, normK) * eDistS;
                        float obs1 = ComputeSignedBearing(webForward, rel) * eBrgS;
                        // 각도 스프레드(도) = max−min 방위각, /90 정규화. 단일 멤버면 0.
                        float spreadDeg = haveRef ? (maxDelta - minDelta) : 0f;
                        float obs2 = Mathf.Clamp01(spreadDeg / 90f);
                        AppendEnemyObs(obs0, obs1, obs2);
                    }
                    _heuristicNearestEnemy = nearest;
                }
            } // if (enemyBufferSensor != null && enemyShips != null)

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
        /// 각: dist, bearing, hdg = 3개
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
                    if (pair.agent1 == null) continue;

                    Vector3 a1Pos = pair.agent1.transform.position;
                    Vector3 anchorPos = pair.AnchorPos;
                    if (a1Pos.y < -100f) continue;

                    Vector3 otherCenter = (a1Pos + anchorPos) * 0.5f;
                    float d = Vector3.Distance(myPos, otherCenter);
                    float allyYaw = pair.agent1.transform.eulerAngles.y;

                    candidates.Add((otherCenter, d, allyYaw));
                }
            }

            // 2. 트랩 (정적 → heading 0f)
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
            float aHdgS  = (envController != null) ? envController.allyHdgScale : allyHdgScale;
            float normDist = NormalizePosition(dist, allyPairNormK) * aDistS;
            float brg = ComputeSignedBearing(myForward, rel) * aBrgS;
            // 헤딩차를 (cos, sin) 2개로 → 부호 포함 모든 각도 유일 표현
            float dHdgA = Mathf.DeltaAngle(myAngle, allyYaw) * Mathf.Deg2Rad;
            float hdgCos = Mathf.Cos(dHdgA) * aHdgS;
            float hdgSin = Mathf.Sin(dHdgA) * aHdgS;

            allyBufferSensor.AppendObservation(new float[] { normDist, brg, hdgCos, hdgSin });
            lastAllyBufferObs.Add(normDist); lastAllyBufferObs.Add(brg); lastAllyBufferObs.Add(hdgCos); lastAllyBufferObs.Add(hdgSin);
        }

        /// <summary>적군(클러스터) 1개를 enemyBufferSensor에 추가 (3개: dist, bearing, clusterSize)</summary>
        private void AppendEnemyObs(float obs0, float obs1, float obs2)
        {
            enemyBufferSensor.AppendObservation(new float[] { obs0, obs1, obs2 });
            lastEnemyBufferObs.Add(obs0); lastEnemyBufferObs.Add(obs1); lastEnemyBufferObs.Add(obs2);
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
                    $"ended={_episodeEnded}, neutral={_neutralized}, " +
                    $"kinematic={_engine?.RB?.isKinematic}");
                return;
            }

            // Split 분리 모드
            if (_splitMode)
            {
                if (_splitStepsRemaining > 0)
                    _splitStepsRemaining--;
                else
                    _splitMode = false;

                if (_splitMode)
                {
                    float splitSteering = Mathf.Clamp(_splitSteer * steeringSensitivity, -1f, 1f);

                    // [C] 좌우 교차 방지: 파트너 측면 부호가 역전되면 조향 반전
                    if (_splitInitialLateralSign != 0f && partnerAgent != null)
                    {
                        Vector3 toPartner = partnerAgent.transform.position - transform.position;
                        toPartner.y = 0f;
                        float dist = toPartner.magnitude;
                        if (dist < 15f) // 근거리에서만 감지 (멀면 정상적인 벌어짐)
                        {
                            float curLateral = Vector3.Dot(toPartner, transform.right);
                            float curSign = curLateral >= 0f ? 1f : -1f;
                            if (curSign != _splitInitialLateralSign)
                                splitSteering = -splitSteering; // 역전
                        }
                    }

                    // [A] Safety Layer: 타 페어 APF 척력 혼합
                    if (safetyRepulsionScale > 0f)
                    {
                        float repulsion = ComputeInterPairRepulsionSteering(safetyRadius);
                        splitSteering = Mathf.Clamp(splitSteering + repulsion * safetyRepulsionScale, -1f, 1f);
                    }

                    _engine.Accelerate(maxThrottle);
                    _engine.Turn(splitSteering);
                    _prevThrottle = maxThrottle;
                    _prevSteering = splitSteering;
                    return;
                }
            }

            // 직진 이탈 모드 (Stage9)
            if (_straightMode)
            {
                _engine.Accelerate(maxThrottle);
                _engine.Turn(0f);
                _prevThrottle = maxThrottle;
                _prevSteering = 0f;
                return;
            }

            // ONE-WAY TOWING: LOS 수직 world 방향으로 full throttle 직진
            if (_towMode)
            {
                float towYaw   = Mathf.Atan2(_towWorldDir.x, _towWorldDir.z) * Mathf.Rad2Deg;
                float myYaw    = transform.eulerAngles.y;
                float bearing  = Mathf.DeltaAngle(myYaw, towYaw);   // -180 ~ +180
                float steer    = Mathf.Clamp(bearing / 25f, -1f, 1f); // ±25° → full steer
                _engine.Accelerate(maxThrottle);
                _engine.Turn(steer);
                _prevThrottle = maxThrottle;
                _prevSteering = steer;
                return;
            }

            // Phase1 정지 모드: 트랩 설치 후 보상/액션 없이 정지
            if (_stopMode)
            {
                _engine.Accelerate(0f);
                _engine.Turn(0f);
                _prevThrottle = 0f;
                _prevSteering = 0f;
                return;
            }

            // RL 액션
            float throttleInput = actions.ContinuousActions[0];
            float steeringInput = actions.ContinuousActions[1];

            if (float.IsNaN(throttleInput) || float.IsInfinity(throttleInput)) throttleInput = 0f;
            if (float.IsNaN(steeringInput) || float.IsInfinity(steeringInput)) steeringInput = 0f;

            // LOS 베이스라인 캐싱 (관측 + 정렬 보상용)
            // 주의: ComputeLOSBaselineThrottle은 _losLookAheadPoint 의존 → steering 먼저 호출
            if (_clusterTarget != Vector3.zero)
            {
                _cachedLOSBaseline         = ComputeLOSBaselineSteering();
                _cachedLOSThrottleBaseline = ComputeLOSBaselineThrottle();
            }

            // Residual RL: 접근 구간 행동 = (1-s)*LOS추종 베이스라인 + s*RL
            //   s=residualScale. s=0→순수 LOS 추종, s=1→순수 RL, 0.5→반반 (ablation 스위치)
            //   _clusterTarget==0(표적 없음)이면 baseline 무효 → 순수 RL 폴백
            float rlThrottle = Mathf.Clamp(throttleInput + 0.4f, 0f, maxThrottle);
            float rlSteering = Mathf.Clamp(steeringInput, -1f, 1f);
            float throttle, steering;
            if (_clusterTarget != Vector3.zero && residualScale < 0.999f)
            {
                float s = Mathf.Clamp01(residualScale);
                steering = Mathf.Clamp((1f - s) * _cachedLOSBaseline         + s * rlSteering, -1f, 1f);
                throttle = Mathf.Clamp((1f - s) * _cachedLOSThrottleBaseline + s * rlThrottle, 0f, maxThrottle);
            }
            else
            {
                throttle = rlThrottle;
                steering = rlSteering;
            }

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

            // === LOS 정렬 보상: RL 액션이 LOS 베이스라인에 가까울수록 + (개별) ===
            if (_clusterTarget != Vector3.zero && envController != null && envController.rewardCalculator != null)
            {
                var rc = envController.rewardCalculator;

                // 조향 정렬 (steering, _cachedLOSBaseline 둘 다 [-1,1])
                if (rc.losAlignmentRewardCoeff > 0f)
                {
                    float steerErr = Mathf.Clamp01(Mathf.Abs(steering - _cachedLOSBaseline) * 0.5f);
                    AddReward(rc.losAlignmentRewardCoeff * (1f - steerErr));
                }

                // 속도 정렬 (RL throttle vs 선회감속 베이스라인, maxThrottle로 정규화)
                if (rc.losThrottleAlignmentCoeff > 0f && maxThrottle > 0.01f)
                {
                    float thrErr = Mathf.Clamp01(Mathf.Abs(throttle - _cachedLOSThrottleBaseline) / maxThrottle);
                    AddReward(rc.losThrottleAlignmentCoeff * (1f - thrErr));
                }
            }

            if (enableDebugLog)
                Debug.Log($"[{gameObject.name}] Throttle: {throttle:F2}, Steering: {steering:F2}");
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

            // 선회 각도 기반 감속: goalBrg 클수록 cos 감쇠 (직진=1.0, 90°=0.71, 180°=0)
            float turnFactor = Mathf.Cos(Mathf.Abs(goalBrg) * Mathf.PI * 0.5f);
            float throttleAction = goalDist > 30f ? maxThrottle * turnFactor
                                 : goalDist > 10f ? 0f
                                 : -1f;
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
                if (pair.agent1 == this || pair.agent1 == partnerAgent) continue;

                Vector3 a1Pos = pair.agent1 != null ? pair.agent1.transform.position : Vector3.zero;
                Vector3 otherCenter = (a1Pos + pair.AnchorPos) * 0.5f;
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
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();

                // 모선 충돌: 페널티 + 쌍 비활성화
                if (isMotherShip)
                {
                    if (envController != null)
                        envController.OnAllyHitMotherShip(this);
                    return;
                }

                // 파트너 아군 충돌: 페널티만 (비활성화 없음)
                if (otherAgent != null && otherAgent == partnerAgent)
                {
                    if (envController != null)
                        envController.OnPartnerCollision(this);
                    return;
                }

                // 다른 쌍 충돌: 기존 처리 (쌍 비활성화)
                if (envController != null)
                    envController.OnFriendlyCollision(this);
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

            // ★ 추종 목적지 (_clusterTarget): 밝은 초록 구 — "에이전트가 PD로 따라가는 점".
            //   Commander/웨이포인트 방식은 이 점만 바꾸면 됨 (시스템은 그대로 추종).
            if (_clusterTarget != Vector3.zero)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawSphere(_clusterTarget + Vector3.up * 2f, 5f);          // 목적지 본체
                Gizmos.color = new Color(0f, 1f, 0f, 0.9f);
                Gizmos.DrawLine(transform.position, _clusterTarget);              // 나 → 목적지
                Gizmos.color = new Color(0.2f, 1f, 0.2f, 0.3f);
                Gizmos.DrawWireSphere(_clusterTarget, 9f);                        // 강조 링
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

            // LOS 가이던스 시각화
            if (_losLookAheadPoint != Vector3.zero)
            {
                // 수선의 발 (foot): 흰 구
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(_losFootPoint, 3f);

                // look-ahead 타겟 P: 밝은 청록 구
                Gizmos.color = new Color(0f, 1f, 0.8f, 1f);
                Gizmos.DrawSphere(_losLookAheadPoint, 4f);

                // 에이전트 → foot (수선): 흰 점선 느낌의 선
                Gizmos.color = new Color(1f, 1f, 1f, 0.5f);
                Gizmos.DrawLine(transform.position, _losFootPoint);

                // foot → P (look-ahead 방향): 청록 선
                Gizmos.color = new Color(0f, 1f, 0.8f, 0.8f);
                Gizmos.DrawLine(_losFootPoint, _losLookAheadPoint);

                // 에이전트 → P (실제 조향 방향): 노란 선
                Gizmos.color = new Color(1f, 0.8f, 0f, 1f);
                Gizmos.DrawLine(transform.position, _losLookAheadPoint);
            }
            else if (_guidancePhase && _guidanceTarget != Vector3.zero)
            {
                // guidance 단계: 클러스터 타겟 직선
                Gizmos.color = new Color(0f, 1f, 0.8f, 1f);
                Gizmos.DrawSphere(_guidanceTarget, 4f);
                Gizmos.DrawLine(transform.position, _guidanceTarget);
            }
        }
    }
}
