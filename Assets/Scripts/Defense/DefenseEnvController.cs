using UnityEngine;
using Unity.MLAgents;
using Unity.Barracuda;
using Cinemachine;
using System.Linq;

namespace BoatAttack
{
    /// <summary>
    /// 커리큘럼 학습 단계
    /// </summary>
    public enum TrainingStage
    {
        Stage1_Formation,   // 대형 유지 학습
        Stage2_Capture,     // 포획 보상 학습
        Stage3_Tactical,    // 전술 기동 학습
        Stage4_Commander,   // Commander 기본 학습 (전략 에이전트 도입)
        Stage5_FullScale,   // 전체 복잡도 (Commander + DefenseAgent 동시 학습)
        Stage6_PhantomFormation, // 1 페어 + Phantom 가상 아군쌍 대형 학습
        Stage7_FleetManeuver,   // 다수 실제 아군 쌍(5~10) 군집 기동 학습
        Stage8_TacticalFullObs, // Stage3 + 모든 적 BufferSensor 관측 (타겟 배정 없음)
        Stage9_DisarmReform,    // Stage8 + 포획 후 직진 이탈 + 포획 보상 개별 분배
        Stage10_FlankCapture    // 정지 트랩 전개 후 생존 적군을 단독 측면 접근으로 포획
    }

    /// <summary>
    /// 방어 환경 컨트롤러 (중앙 허브 버전)
    /// - 모든 에피소드 재시작 로직을 중앙에서 관리
    /// - 그룹 보상 분배 및 환경 관리
    /// - 선박 위치 리셋 및 적군 추적
    /// - 에피소드 시작/종료 관리
    /// - 커리큘럼 학습 단계별 보상 제어
    /// </summary>
    [DefaultExecutionOrder(1000)] // LateUpdate가 다른 모든 스크립트보다 나중에 실행 → 카메라 최종 제어
    public class DefenseEnvController : MonoBehaviour
    {
        [Header("Training Stage")]
        [Tooltip("현재 학습 단계 (Stage1: 대형유지, Stage2: 포획, Stage3: 전술기동)")]
        public TrainingStage currentStage = TrainingStage.Stage1_Formation;

        [Tooltip("Stage1에서 적군 비활성화")]
        public bool disableEnemiesInStage1 = true;

        [Tooltip("Stage1에서 활성화할 적군 수 (disableEnemiesInStage1이 false일 때)")]
        [Range(0, 5)]
        public int stage1EnemyCount = 0;

        [Tooltip("Stage2에서 활성화할 적군 수")]
        [Range(0, 10)]
        public int stage2EnemyCount = 0;

        [Tooltip("Stage3에서 활성화할 적군 수")]
        [Range(0, 10)]
        public int stage3EnemyCount = 1;

        [Tooltip("Stage4(Commander)에서 활성화할 적군 수")]
        [Range(0, 10)]
        public int stage4EnemyCount = 5;

        [Tooltip("Stage5(FullScale)에서 활성화할 적군 수")]
        [Range(0, 10)]
        public int stage5EnemyCount = 10;

        [Tooltip("Stage6(PhantomFormation)에서 활성화할 적군 수")]
        [Range(0, 10)]
        public int stage6EnemyCount = 1;

        [Tooltip("Stage7(FleetManeuver) 아군 쌍 최소 수")]
        [Range(1, 10)]
        public int stage7AllyPairMin = 5;

        [Tooltip("Stage7(FleetManeuver) 아군 쌍 최대 수")]
        [Range(1, 10)]
        public int stage7AllyPairMax = 10;

        [Tooltip("Stage8(TacticalFullObs)에서 활성화할 적군 수")]
        [Range(0, 10)]
        public int stage8EnemyCount = 1;

        [Header("Stage9 (DisarmReform)")]
        [Tooltip("Stage9에서 활성화할 적군 수")]
        [Range(0, 10)]
        public int stage9EnemyCount = 3;

        [Tooltip("Stage10 적 수")]
        public int stage10EnemyCount = 4;

        [Tooltip("Stage9 트랩 그물 유지 스텝")]
        public int trapLifetimeSteps = 500;

        [Tooltip("Stage9 직진 이탈 유지 스텝 수 (이후 완전 비활성화)")]
        public int disarmDurationSteps = 50;

        [Header("=== Split & Separation ===")]
        [Tooltip("적이 이 거리 이하로 접근하면 앵커 드롭 시작 (m). 권장: 100~200m")]
        [Range(1000f, 10000f)]
        public float splitTriggerDistance = 1000f;
        [Tooltip("분리 시 좌/우 조향 강도 (0~3, IST: 3)")]
        [Range(0f, 3f)]
        public float splitSteerStrength = 3f;
        [Tooltip("선박-앵커 거리가 이 이상이면 그물이 화면에 등장 (m). DynamicWeb.minWebActiveDist를 에피소드마다 이 값으로 동기화. IST: 20m")]
        [Range(1f, 200f)]
        public float webAppearDist = 20f;
        [Tooltip("선박 간격이 이 이상이면 그물 고정 + 선박 정지 (m). IST: 100m")]
        public float webDeployedThreshold = 100f;
        [Tooltip("분리 시작 후 최대 허용 스텝. 타임아웃 시 강제 정지 — webDeployedThreshold에 도달하지 못했을 때만 발동하는 안전망. (5m/s × 2000step×0.02s = 200m)")]
        public int deployMaxSteps = 2000;
        [Tooltip("파트너 선박과 이 거리 이상 멀어지면 해당 페어 비활성화 (m). 분리 전에만 적용")]
        public float maxPairSeparation = 200f;
        [Tooltip("포획 후 그물+선박 유지 (다중 포획 허용). IST: true")]
        public bool keepWebAfterCapture = true;

        [Tooltip("양동(Diversionary) 방향 필터: 진수 각도와 적 접근 각도의 최대 허용 차이 (°). 0=필터 없음")]
        [Range(0f, 180f)]
        public float assignAngleTolerance = 60f;

        [Tooltip("가상 아군쌍 생성 간격 (방어선 좌/우, m)")]
        [Range(30f, 300f)]
        public float phantomSpacing = 80f;

        [Tooltip("가상 아군쌍 속도 배율 최소 (실제 아군 속도 × 배율)")]
        [Range(0.5f, 5f)]
        public float phantomSpeedMultMin = 0.8f;

        [Tooltip("가상 아군쌍 속도 배율 최대")]
        [Range(0.5f, 5f)]
        public float phantomSpeedMultMax = 1.2f;

        [Tooltip("아군 페어 소진 시 에피소드 종료 비활성화 (true=페어 소진해도 에피소드 유지)")]
        public bool disableNoPairsEndEpisode = false;

        [Header("=== Phase1: 정지 트랩 설치만 학습 ===")]
        [Tooltip("true: 트랩 설치 완료 후 아군 정지 + 전체 설치 시 에피소드 종료. 포획 기동 학습 전 1단계 전용.")]
        public bool phase1TrapOnlyMode = false;

        [Header("=== Chase Trap Training (Phase2 직접 학습) ===")]
        [Tooltip("true: Phase1 생략, 에피소드 시작 시 isDisarmed+singleNet 상태로 직접 스폰")]
        public bool chaseTrainingMode = false;
        [Tooltip("아군 쌍(webCenter) ~ 모선 최소 거리 (m)")]
        public float chaseWebCenterMinDist = 400f;
        [Tooltip("아군 쌍(webCenter) ~ 모선 최대 거리 (m)")]
        public float chaseWebCenterMaxDist = 700f;
        [Tooltip("아군 쌍 좌우 분리 최소 거리 (m)")]
        public float chaseShipSepMin      = 50f;
        [Tooltip("아군 쌍 좌우 분리 최대 거리 (m)")]
        public float chaseShipSepMax      = 100f;
        [Tooltip("적군 webCenter 기준 전방(적 방향) 최소 거리 (m)")]
        public float chaseEnemyMinBeyond  = 200f;
        [Tooltip("적군 webCenter 기준 전방(적 방향) 최대 거리 (m)")]
        public float chaseEnemyMaxBeyond  = 400f;
        [Tooltip("스폰 직후 물리 안정화 가이던스 스텝. 권장 20~30")]
        public int   chaseGuidanceSteps   = 25;

        [Header("Agents")]
        [Tooltip("방어 에이전트 1")]
        public DefenseAgent defenseAgent1;
        
        [Tooltip("방어 에이전트 2")]
        public DefenseAgent defenseAgent2;
        
        [Header("Components")]
        [Tooltip("SimpleMultiAgentGroup (선택사항 - 없으면 개별 보상으로 fallback)")]
        public SimpleMultiAgentGroup m_AgentGroup;
        
        [Tooltip("보상 계산기")]
        public DefenseRewardCalculator rewardCalculator;

        [Header("Trajectory Logging")]
        [Tooltip("경로 기록기 (선택)")]
        public TrajectoryLogger trajectoryLogger;

        [Header("Performance")]
        [Tooltip("경량 모드: 그물 메시 비활성화 (단순 Cube로 대체)")]
        public bool lightweightMode = true;

        [Header("Wind & Wave")]
        [Tooltip("바람 최소 속도 (m/s)")]
        public float windSpeedMin = 3f;
        [Tooltip("바람 최대 속도 (m/s)")]
        public float windSpeedMax = 10f;

        [Header("Settings")]
        [Tooltip("보상 계산 주기 (프레임 단위, 1 = 매 프레임)")]
        public int rewardCalculationInterval = 1;
        
        [Tooltip("최대 환경 스텝 수 (에피소드가 이 스텝 수에 도달하면 자동 종료, 0=무제한)")]
        public int maxEnvironmentSteps = 2500;
        
        [Tooltip("모선 참조")]
        public GameObject motherShip;
        
        [Tooltip("적군 선박들")]
        public GameObject[] enemyShips = new GameObject[5];
        
        [Tooltip("Web 오브젝트")]
        public GameObject webObject;
        
        [Header("Spawn Positions")]
        [Tooltip("방어 선박 1 초기 위치")]
        public Vector3 defense1SpawnPos = new Vector3(-115f, -8f, -10f);

        [Tooltip("방어 선박 1 초기 각도 (Euler 각도)")]
        public Vector3 defense1SpawnRot = new Vector3(0f, 0f, 0f);

        [Tooltip("방어 선박 2 초기 위치")]
        public Vector3 defense2SpawnPos = new Vector3(0f, -8f, -6f);

        [Tooltip("방어 선박 2 초기 각도 (Euler 각도)")]
        public Vector3 defense2SpawnRot = new Vector3(0f, 0f, 0f);

        [Tooltip("Web 오브젝트 위치 (2대 중간)")]
        public Vector3 webSpawnPos = new Vector3(0f, 0.8f, 0f);
        
        [Header("Episode End Conditions")]
        [Tooltip("Web/MotherShip 충돌 최대 허용 횟수 (이 횟수 이상 충돌 시 에피소드 종료)")]
        public int maxCollisionCount = 1;
        
        [Header("Random Spawn Settings")]
        [Tooltip("랜덤 스폰 범위 (기존 위치에서 반경 내 원형 영역)")]
        public float spawnRange = 30f;

        [Tooltip("랜덤 스폰 활성화 (에피소드 시작 시 랜덤 위치로 재생성)")]
        public bool enableRandomSpawn = true;

        [Tooltip("아군 선박 랜덤 시작 각도 범위 (±도)")]
        public float defenseRandomAngleRange = 30f;

        [Header("Dynamic Spawn (모선 기준)")]
        [Tooltip("동적 스폰 활성화 (모선 중심으로 적군/아군 배치)")]
        public bool useDynamicSpawn = true;

        [Tooltip("적군 스폰 거리 (모선으로부터)")]
        public float enemySpawnDistance = 1000f;

        [Tooltip("적군 스폰 위치 퍼짐 범위 (각 적군 사이 랜덤 오프셋)")]
        public float enemySpawnSpread = 50f;

        [Tooltip("아군 스폰 거리 (모선으로부터, 적 반대편)")]
        public float defenseSpawnDistance = 100f;

        [Tooltip("아군 2대 좌우 펼침 각도 (±도)")]
        public float defenseSpawnSpread = 1.7f;

        [Header("Enemy Path Randomization")]
        [Tooltip("적군 경로 랜덤화 활성화")]
        public bool enableEnemyPathRandomization = true;

        [Tooltip("적군 경로 랜덤 할당 활성화 (리셋 시 attack_track 중 랜덤 선택)")]
        public bool enableRandomPathAssignment = true;
        
        [Header("Explosion Settings")]
        [Tooltip("폭발 효과 Prefab (War FX)")]
        public GameObject explosionPrefab;
        
        [Tooltip("폭발 효과 크기 배율")]
        [Range(5f, 50f)]
        public float explosionScale = 23f;
        
        [Tooltip("폭발 효과 지속 시간 (초) - 이 시간 후 에피소드 재시작")]
        public float explosionDuration = 2.0f;
        
        [Header("Episode End Conditions")]
        [Tooltip("모든 적군 선박 파괴 시 에피소드 종료")]
        public bool endEpisodeOnAllEnemiesDestroyed = true;

        [Tooltip("적이 그물보다 모선에 이 거리 이상 더 가까우면 방어선 돌파 (m)")]
        public float enemyBreachThreshold = 20f;

        [Tooltip("에피소드 내 최대 아군 쌍 생성 수 (이 수 이상 생성 불가)")]
        public int maxAllyPairsPerEpisode = 3;

        [Header("Weather Randomization")]
        [Tooltip("에피소드 시작 시 날씨(파도/바람) 랜덤화 활성화")]
        public bool randomizeWeatherOnEpisode = false;

        [Tooltip("환경 컨트롤러 (날씨 랜덤화용)")]
        public EnvironmentController environmentController;

        [Header("Observation Scale (전 에이전트 공통)")]
        [Range(1f, 5000f)] public float enemyNormK = 500f;
        [Range(1f, 10f)] public float enemyDistScale = 1f;
        [Range(1f, 10f)] public float enemyBearingScale = 1f;
        [Range(1f, 10f)] public float enemyHeadingScale = 1f;
        [Range(1f, 10f)] public float allyDistScale = 1f;
        [Range(1f, 10f)] public float allyBearingScale = 1f;
        [Range(1f, 10f)] public float allyHdgScale = 1f;

        [Header("Enemy Rush Movement")]
        [Tooltip("동적 스폰 시 적군 자동 돌진 활성화")]
        public bool enableEnemyRush = true;

        [Tooltip("적군 돌진 스로틀 (기본값, 랜덤 범위의 중심)")]
        [Range(0.1f, 60.0f)]
        public float enemyRushThrottle = 1.0f;

        [Tooltip("에피소드마다 적군 속도 랜덤화 (±비율, 0=고정, 0.3=±30%)")]
        [Range(0f, 0.5f)]
        public float enemySpeedRandomRange = 0.3f;

        /// <summary>현재 에피소드의 실제 적군 스로틀 (매 에피소드 랜덤 설정)</summary>
        [HideInInspector] public float currentEnemyRushThrottle = 1.0f;

        [Tooltip("적군 조향 노이즈 크기")]
        [Range(0f, 0.5f)]
        public float enemySteeringNoise = 0.1f;

        [Tooltip("적군 노이즈 변화 속도")]
        public float enemyNoiseSpeed = 2f;

        [Tooltip("적군 조향 감도")]
        [Range(0.1f, 2.0f)]
        public float enemySteeringSensitivity = 0.3f;

        [Header("Enemy Pool")]
        [Tooltip("풀 최대 크기 (stage*EnemyCount 이상으로 설정)")]
        public int poolSize = 10;

        [Tooltip("적군 포메이션 스폰 관리자 (없으면 기존 단방향 스폰 사용)")]
        public EnemyFormationSpawner formationSpawner;

        [Tooltip("현재 에피소드의 포메이션 타입 (디버그 표시)")]
        [SerializeField] private FormationType _currentFormation;

        [Tooltip("아군 진수구역 관리자 (없으면 기존 1쌍 레거시 모드)")]
        public LaunchZoneManager launchZoneManager;

        [Header("Commander (계층적 RL)")]
        [Tooltip("전략 에이전트 (null이면 기존 레거시 모드 유지)")]
        public CommanderAgent commanderAgent;

        [Tooltip("방어 추적 카메라 (없으면 카메라 전환 비활성화)")]
        public DefenseFollowCamera followCamera;

        [Tooltip("아군 생성 버튼 UI (없으면 무시)")]
        public AllySpawnButtonUI spawnButtonUI;

        [Tooltip("Stage4: DefenseAgent 학습 완료 모델 (.onnx). InferenceOnly 전환용")]
        public NNModel defenseOnnxModel;

        // 풀 배열 (Start()에서 초기화, 인덱스 기반)
        private GameObject[] _enemyPool;
        private AttackAgent[] _poolAttackAgents;
        private Rigidbody[] _poolRigidbodies;
        private Engine[] _poolEngines;
        private AttackBoatDisabler[] _poolDisablers;
        private SimpleExplosionOnCollision[] _poolExplosions;
        private Cinemachine.CinemachineDollyCart[] _poolDollyCarts;
        private float[] _poolNoiseSeed;
        private float _poolTemplateY; // 템플릿 높이(y) 저장

        // Raycast 차단 시각화 (적→모선)
        private LineRenderer[] _enemyRayLines;
        // 이전 스텝 Raycast 결과: true = 해당 적이 어떤 Web에 차단됨
        private bool[] _enemyRayBlocked;

        [Header("Multi-Environment")]
        [Tooltip("환경 루트 Transform (Island Level 등). 비어있으면 부모 또는 자기 자신 사용")]
        public Transform environmentRoot;

        [Header("Reward Monitor (Read Only)")]
        [SerializeField] private float _currentEpisodeReward = 0f;
        [SerializeField] private float _lastStepReward = 0f;
        [SerializeField] private int _currentStep = 0;
        [SerializeField] private int _totalCollisions = 0;

        private int _resetTimer = 0; // FixedUpdate 기반 타이머
        public int CurrentStep => _resetTimer;
        private bool _episodeActive = false;
        private bool _episodeEnding = false; // 에피소드 종료 중 플래그 (중복 호출 방지)
        
        // 에피소드 추적
        private int _episodeNumber = 0; // 에피소드 번호 (재시작 확인용)
        
        // 충돌 횟수 추적 (Web + MotherShip 통합 카운트)
        private int _totalCollisionCount = 0; // 총 충돌 횟수 (Web + MotherShip 합산)
        private int _breachedEnemyCount = 0; // 모선 도달(돌파) 적군 수 (Commander 보상용)
        private int _capturedEnemyCount = 0; // 포획 성공 적군 수 (HUD 표시용)
        private int _allyPairsCreatedThisEpisode = 0; // 이번 에피소드에서 생성된 아군 쌍 수
        private float _prevAvgCoverage = float.MaxValue; // 커버리지 보상용 이전 평균 거리
        
        // 중복 충돌 방지 (같은 적군 선박이 짧은 시간 내 여러 번 충돌하는 것 방지)
        private float _collisionCooldown = 2.0f; // 충돌 쿨다운 시간 (초)
        private System.Collections.Generic.Dictionary<GameObject, float> _collisionCooldownTimes = new System.Collections.Generic.Dictionary<GameObject, float>();
        // 아군 Web 충돌 쿨다운 (OnTriggerStay 매프레임 중복 방지)
        private float _allyWebCollisionCooldown = 3.0f;
        private System.Collections.Generic.Dictionary<GameObject, float> _allyWebCollisionTimes = new System.Collections.Generic.Dictionary<GameObject, float>();

        // Stage9: 깔린 트랩 그물 관리
        private System.Collections.Generic.List<GameObject> _anchoredTraps = new System.Collections.Generic.List<GameObject>();

        /// <summary>활성 트랩 정보 (에이전트 관측용)</summary>
        public struct TrapInfo
        {
            public Vector3 position;
            public float webSize;
        }
        public System.Collections.Generic.List<TrapInfo> activeTraps = new System.Collections.Generic.List<TrapInfo>();
        
        // 위치 리셋 관련
        private int _lastResetFrame = -1; // 중복 리셋 방지용
        private bool _isResettingPositions = false; // 위치 리셋 코루틴 실행 중 플래그
        private WebCollisionDetector _webDetector;
        
        // 원래 위치 및 각도 저장 (랜덤 스폰용)
        private Vector3 _originalDefense1Pos;
        private Vector3 _originalDefense2Pos;
        private Quaternion _originalDefense1Rot;
        private Quaternion _originalDefense2Rot;

        // 씬의 모든 attack_track 경로 (랜덤 할당용)
        private CinemachineSmoothPath[] _availableAttackPaths;

        // 각 경로별 원본 웨이포인트 저장 (랜덤화용)
        private Vector3[][] _allOriginalWaypoints;

        // 아군 선박 좌/우 위치 추적 (위치 교차 감지용)
        // 에피소드 시작 시 agent1이 agent2의 왼쪽에 있으면 true
        private bool _agent1StartsOnLeft = true;

        // 동적 스폰: 현재 에피소드의 적군 접근 각도 (도)
        private float _currentEnemyApproachAngle = 0f;
        // 동적 스폰: 아군 위치 교차 판별용 수직축 (적 접근 방향의 90° 회전)
        private Vector3 _currentPerpendicularDir = Vector3.right;
        private System.Collections.Generic.Dictionary<GameObject, Vector3> _originalEnemyPositions = 
            new System.Collections.Generic.Dictionary<GameObject, Vector3>();
        private System.Collections.Generic.Dictionary<GameObject, Vector3> _originalBoatPositions = 
            new System.Collections.Generic.Dictionary<GameObject, Vector3>(); // 태그가 "boat"인 모든 선박의 초기 위치
        private System.Collections.Generic.Dictionary<GameObject, Quaternion> _originalBoatRotations = 
            new System.Collections.Generic.Dictionary<GameObject, Quaternion>(); // 태그가 "boat"인 모든 선박의 초기 각도
        
        // 무력화된 적군 추적 (SetActive(false) 대신 현재 위치 정지 방식)
        private System.Collections.Generic.HashSet<GameObject> _neutralizedEnemies =
            new System.Collections.Generic.HashSet<GameObject>();

        // 적군 선박 관리 → _enemyPool 배열로 통합 (위 Enemy Pool 섹션 참조)
        
        // FollowCam: C키로 [아군 페어 → 적군 개별] 순환
        private Camera _followCamCamera;
        private bool _followCamInitialized = false;
        private bool _followCamSnap = false;
        private enum FollowCamMode { None, Pair, Enemy, Agent } // Agent: Stage10 개별 선박 추적
        private FollowCamMode _camMode = FollowCamMode.None;
        private int _camTargetId = -1;
        private UnityEngine.InputSystem.InputAction _camSwitchAction;
        private bool _cKeyQueued = false;

        // Inspector에서 Stage 변경 시 자동 적용 (에디터 전용)
        private TrainingStage _lastStage;

        // Stage7: 에피소드별 아군 쌍 수 저장 (적군 수 동기화용)
        private int _stage7AllyCount = 0;


        #region Multi-Environment Helpers

        /// <summary>
        /// 환경 루트 Transform 반환 (멀티 환경 학습용)
        /// environmentRoot → 부모 → 자기 자신 순서로 fallback
        /// </summary>
        private Transform GetEnvironmentRoot()
        {
            if (environmentRoot != null) return environmentRoot;
            if (transform.parent != null) return transform.parent;
            return transform;
        }

        /// <summary>
        /// 현재 환경 내에서만 특정 태그의 GameObject를 찾기 (다른 환경의 오브젝트 제외)
        /// </summary>
        private GameObject[] FindGameObjectsWithTagInEnvironment(string tag)
        {
            var root = GetEnvironmentRoot();
            var allWithTag = GameObject.FindGameObjectsWithTag(tag);
            var result = new System.Collections.Generic.List<GameObject>();
            foreach (var obj in allWithTag)
            {
                if (obj != null && obj.transform.IsChildOf(root))
                    result.Add(obj);
            }
            return result.ToArray();
        }

        /// <summary>
        /// 현재 환경 내에서만 특정 태그의 단일 GameObject를 찾기
        /// </summary>
        private GameObject FindGameObjectWithTagInEnvironment(string tag)
        {
            var root = GetEnvironmentRoot();
            var allWithTag = GameObject.FindGameObjectsWithTag(tag);
            foreach (var obj in allWithTag)
            {
                if (obj != null && obj.transform.IsChildOf(root))
                    return obj;
            }
            return null;
        }

        /// <summary>
        /// 현재 환경 내에서만 컴포넌트 검색 (다른 환경의 컴포넌트 제외)
        /// </summary>
        private T[] FindComponentsInEnvironment<T>() where T : Component
        {
            return GetEnvironmentRoot().GetComponentsInChildren<T>(true);
        }

        #endregion

        private void OnValidate()
        {
            // Play 모드에서만 실행
            if (!Application.isPlaying)
                return;

            // Stage가 변경되었는지 확인
            if (_lastStage != currentStage)
            {
                _lastStage = currentStage;

                ApplyStageSettings();
            }
        }

        private void OnEnable()
        {
            _camSwitchAction = new UnityEngine.InputSystem.InputAction(
                "SwitchCam", UnityEngine.InputSystem.InputActionType.Button, "<Keyboard>/c");
            _camSwitchAction.performed += _ => _cKeyQueued = true;
            _camSwitchAction.Enable();
        }

        private void OnDisable()
        {
            if (_camSwitchAction != null)
            {
                _camSwitchAction.Disable();
                _camSwitchAction.Dispose();
                _camSwitchAction = null;
            }
        }

        private void Start()
        {
            // TrajectoryLogger 자동 탐색
            if (trajectoryLogger == null)
                trajectoryLogger = GetComponentInChildren<TrajectoryLogger>();

            // 초기 Stage 저장
            _lastStage = currentStage;

            // SimpleMultiAgentGroup 초기화
            if (m_AgentGroup == null)
            {
                m_AgentGroup = new SimpleMultiAgentGroup();
            }
            
            // 에이전트 등록 (LaunchZoneManager가 없을 때만 — 있으면 DeployPairs()에서 동적 등록)
            if (m_AgentGroup != null && launchZoneManager == null)
            {
                if (defenseAgent1 != null)
                    m_AgentGroup.RegisterAgent(defenseAgent1);
                if (defenseAgent2 != null)
                    m_AgentGroup.RegisterAgent(defenseAgent2);
            }
            
            // RewardCalculator 초기화
            if (rewardCalculator == null)
            {
                rewardCalculator = GetComponent<DefenseRewardCalculator>();
            }

            // Stage별 적군 활성화/비활성화
            ApplyStageSettings();
            
            // 모선 찾기 (환경 내에서만 검색)
            if (motherShip == null)
            {
                motherShip = FindGameObjectWithTagInEnvironment("MotherShip");
            }
            
            // MotherShipCollisionDetector에 envController 할당
            if (motherShip != null)
            {
                var motherShipDetector = motherShip.GetComponent<MotherShipCollisionDetector>();
                if (motherShipDetector != null)
                {
                    motherShipDetector.envController = this;
                }
            }

            // WebCollisionDetector 설정
            if (webObject != null)
            {
                _webDetector = webObject.GetComponent<WebCollisionDetector>();
                if (_webDetector == null)
                {
                    _webDetector = webObject.AddComponent<WebCollisionDetector>();
                }
                _webDetector.envController = this;

                // DynamicWeb에도 envController 할당
                var dynamicWeb = webObject.GetComponent<DynamicWeb>();
                if (dynamicWeb != null)
                {
                    dynamicWeb.envController = this;
                }
            }

            // 에이전트 페어링
            if (defenseAgent1 != null && defenseAgent2 != null)
            {
                defenseAgent1.partnerAgent = defenseAgent2;
                defenseAgent2.partnerAgent = defenseAgent1;

                defenseAgent1.webObject = webObject;
                defenseAgent2.webObject = webObject;

                // 모선 참조 할당 (멀티 환경에서 올바른 모선을 사용하도록)
                if (motherShip != null)
                {
                    defenseAgent1.motherShip = motherShip;
                    defenseAgent2.motherShip = motherShip;
                }
                
                // 적군 배열 설정
                if (enemyShips != null && enemyShips.Length > 0)
                {
                    defenseAgent1.enemyShips = enemyShips;
                    defenseAgent2.enemyShips = enemyShips;
                }
            }

            // 원점 = 실제 에이전트의 현재 위치 사용 (멀티 환경 호환)
            // 환경을 복제하면 에이전트도 함께 이동하므로, 실제 위치를 저장해야 올바른 리셋 좌표를 사용
            _originalDefense1Pos = (defenseAgent1 != null) ? defenseAgent1.transform.position : defense1SpawnPos;
            _originalDefense2Pos = (defenseAgent2 != null) ? defenseAgent2.transform.position : defense2SpawnPos;

            // 회전도 실제 에이전트 각도 사용 (fallback: 인스펙터 값)
            _originalDefense1Rot = (defenseAgent1 != null) ? defenseAgent1.transform.rotation : Quaternion.Euler(defense1SpawnRot);
            _originalDefense2Rot = (defenseAgent2 != null) ? defenseAgent2.transform.rotation : Quaternion.Euler(defense2SpawnRot);

            // 태그가 "boat"인 모든 GameObject의 초기 위치 및 각도 저장 (WAKE 제외, 환경 내에서만)
            _originalBoatPositions.Clear();
            _originalBoatRotations.Clear();
            GameObject[] allBoats = FindGameObjectsWithTagInEnvironment("boat");
            foreach (var boat in allBoats)
            {
                // WAKE 객체는 제외 (파도 효과 등)
                if (boat != null && !boat.name.Contains("WAKE") && !boat.name.Contains("Wake") && !_originalBoatPositions.ContainsKey(boat))
                {
                    _originalBoatPositions[boat] = boat.transform.position;
                    _originalBoatRotations[boat] = boat.transform.rotation;
                }
            }
            
            // 적군 원래 위치 저장
            if (enemyShips != null)
            {
                foreach (var enemy in enemyShips)
                {
                    if (enemy != null && !_originalEnemyPositions.ContainsKey(enemy))
                    {
                        _originalEnemyPositions[enemy] = enemy.transform.position;
                    }
                }
            }
            
            // 적군 오브젝트 풀 초기화 (프리팹 기반 고정 크기 풀)
            InitializeEnemyPool();

            // 아군 진수구역 풀 초기화 (LaunchZoneManager가 있으면)
            if (launchZoneManager != null)
            {
                if (launchZoneManager.motherShip == null)
                    launchZoneManager.motherShip = motherShip;
                if (launchZoneManager.envController == null)
                    launchZoneManager.envController = this;
                if (launchZoneManager.templatePair == null || launchZoneManager.templatePair.agent1 == null)
                {
                    // 기존 defenseAgent1/webObject를 템플릿으로 자동 설정 (단일 선박)
                    launchZoneManager.templatePair = new DefensePair
                    {
                        agent1 = defenseAgent1,
                        webObject = webObject
                    };
                }
                // anchorObject가 없으면 빈 GameObject로 자동 생성
                if (launchZoneManager.templatePair.anchorObject == null && launchZoneManager.templatePair.agent1 != null)
                {
                    var anchorObj = new GameObject("Anchor_template");
                    anchorObj.transform.SetParent(launchZoneManager.transform);
                    anchorObj.transform.position = launchZoneManager.templatePair.agent1.transform.position;
                    launchZoneManager.templatePair.anchorObject = anchorObj;

                    // DynamicWeb의 두 번째 참조를 앵커로 교정
                    if (webObject != null)
                    {
                        var dw = webObject.GetComponent<DynamicWeb>();
                        if (dw != null) dw.defenseShip2 = anchorObj.transform;
                    }
                }
                launchZoneManager.InitializeAllyPool();

                // 경량 모드: 모든 Web의 그물 메시 비활성화
                ApplyLightweightMode();

                // Stage별 activePairCount 설정
                // Commander Stage: 0 (Commander가 배치 결정)
                // 기타 Stage: 적군 수 기반 자동 계산
                launchZoneManager.activePairCount = IsCommanderStage()
                    ? 0
                    : GetAllyPairCountForStage();
            }

            // 아군 생성 버튼 UI 자동 생성 (없으면)
            if (spawnButtonUI == null && launchZoneManager != null)
            {
                GameObject btnObj = new GameObject("AllySpawnButtonUI");
                btnObj.transform.SetParent(transform);
                spawnButtonUI = btnObj.AddComponent<AllySpawnButtonUI>();
                spawnButtonUI.envController = this;
            }

            // 씬의 모든 attack_track 경로 수집 및 원본 웨이포인트 저장
            FindAllAttackTrackPaths();
            SaveOriginalWaypoints();

            // 첫 에피소드 시작 (PushBlockEnvController 패턴)
            ResetScene();
            
        }
        
        /// <summary>
        /// C키: [아군 페어들 → 적군 개별선박들] 순환
        /// 명시적 (mode, id)로 추적 대상 관리
        /// </summary>
        private void Update()
        {
            // 카메라 초기화 (1회): 기존 카메라 전부 끄고, 새 카메라 생성
            if (!_followCamInitialized)
            {
                _followCamInitialized = true;

                // 씬의 모든 기존 카메라/AudioListener 비활성화
                foreach (var cam in FindObjectsOfType<Camera>())
                    cam.enabled = false;
                foreach (var al in FindObjectsOfType<AudioListener>())
                    al.enabled = false;

                // 전용 카메라 새로 생성 (다른 스크립트가 절대 건드릴 수 없음)
                var camObj = new GameObject("_DefenseFollowCam");
                _followCamCamera = camObj.AddComponent<Camera>();
                camObj.AddComponent<AudioListener>();
                _followCamCamera.clearFlags = CameraClearFlags.Skybox;
                _followCamCamera.nearClipPlane = 0.3f;
                _followCamCamera.farClipPlane = 3000f;
                _followCamCamera.fieldOfView = 60f;
                Debug.LogWarning("[FollowCam] 전용 카메라 생성 완료");
            }

            // C키 입력 (InputAction 콜백 — 프레임 누락 없음)
            if (_cKeyQueued)
            {
                _cKeyQueued = false;
                AdvanceFollowCamTarget();
            }

            // 타겟 없으면 자동 선택 (최초 1회만)
            if (_camMode == FollowCamMode.None)
            {
                SelectFirstAvailableCamTarget();
            }
        }

        /// <summary>
        /// 화면 좌상단에 카메라 타겟 정보 HUD 표시
        /// </summary>
        // 클러스터 시각화용 색상 (최대 3개)
        private static readonly Color[] _clusterColors = { Color.cyan, new Color(0.4f, 1f, 0.4f), new Color(1f, 0.5f, 1f) };

        private void OnGUI()
        {
            string mode = _camMode.ToString();
            string target = "None";
            if (_camMode == FollowCamMode.Pair && launchZoneManager != null)
            {
                var pair = launchZoneManager.GetPair(_camTargetId);
                target = pair != null ? $"Pair#{_camTargetId} active={pair.isActive}" : $"Pair#{_camTargetId} NULL";
            }
            else if (_camMode == FollowCamMode.Enemy && enemyShips != null && _camTargetId >= 0 && _camTargetId < enemyShips.Length)
            {
                var e = enemyShips[_camTargetId];
                target = e != null ? e.name : "NULL";
            }

            string camName = _followCamCamera != null ? _followCamCamera.name : "NO CAM";
            GUI.Label(new Rect(10, 10, 500, 25), $"[CAM] {mode} | {target} | cam={camName} | [C]=switch",
                new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.yellow } });

            // === 클러스터 시각화 ===
            if (launchZoneManager == null || launchZoneManager.CurrentClusters == null) return;
            var clusters = launchZoneManager.CurrentClusters;
            var labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            int y = 40;
            GUI.Label(new Rect(10, y, 350, 20),
                $"Clusters: {clusters.Count} / {launchZoneManager.maxPairCount}",
                new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = Color.white } });
            y += 22;
            for (int c = 0; c < clusters.Count; c++)
            {
                // 클러스터 내 현재 활성 적군 수 계산
                int activeCount = 0;
                if (clusters[c].enemyIndices != null && enemyShips != null)
                    foreach (int idx in clusters[c].enemyIndices)
                        if (idx < enemyShips.Length && enemyShips[idx] != null
                            && enemyShips[idx].activeInHierarchy && !IsEnemyNeutralized(enemyShips[idx]))
                            activeCount++;

                labelStyle.normal.textColor = _clusterColors[c % _clusterColors.Length];
                GUI.Label(new Rect(10, y, 420, 20),
                    $"  [C{c + 1}] dir={clusters[c].centerAngleDeg:F0}°  enemies={activeCount}  centroid=({clusters[c].centroidWorld.x:F0},{clusters[c].centroidWorld.z:F0})",
                    labelStyle);
                y += 20;
            }
        }

        /// <summary>
        /// 현재 카메라 타겟이 유효한지 확인
        /// </summary>
        private bool IsCurrentCamTargetValid()
        {
            if (_camMode == FollowCamMode.Pair && launchZoneManager != null)
            {
                var pair = launchZoneManager.GetPair(_camTargetId);
                return pair != null && pair.isActive;
            }
            if (_camMode == FollowCamMode.Enemy && enemyShips != null)
            {
                return _camTargetId >= 0 && _camTargetId < enemyShips.Length
                    && enemyShips[_camTargetId] != null
                    && !_neutralizedEnemies.Contains(enemyShips[_camTargetId]);
            }
            return false;
        }

        /// <summary>
        /// 다음 유효한 카메라 타겟으로 전진
        /// 순서: 현재 위치 → 같은 카테고리 내 다음 → 다른 카테고리 → 처음
        /// </summary>
        private void AdvanceFollowCamTarget()
        {
            // 전체 타겟 목록을 (mode, id) 순서대로 구축
            var targets = new System.Collections.Generic.List<(FollowCamMode mode, int id)>();

            // 활성 아군 페어들
            if (launchZoneManager != null)
            {
                int cap = launchZoneManager.GetPoolCapacity();
                for (int i = 0; i < cap; i++)
                {
                    var pair = launchZoneManager.GetPair(i);
                    if (pair != null && pair.isActive)
                        targets.Add((FollowCamMode.Pair, i));
                }
            }

            // 2. 활성 적군들
            if (enemyShips != null)
            {
                for (int i = 0; i < enemyShips.Length; i++)
                {
                    if (enemyShips[i] != null && !_neutralizedEnemies.Contains(enemyShips[i]))
                        targets.Add((FollowCamMode.Enemy, i));
                }
            }

            if (targets.Count == 0)
            {
                _camMode = FollowCamMode.None;
                _camTargetId = -1;
                Debug.LogWarning("[FollowCam] C키 → 타겟 없음");
                return;
            }

            // 현재 타겟의 위치 찾기
            int curIdx = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].mode == _camMode && targets[i].id == _camTargetId)
                {
                    curIdx = i;
                    break;
                }
            }

            // 다음으로 전진
            var prev = (_camMode, _camTargetId);
            int nextIdx = (curIdx + 1) % targets.Count;
            _camMode = targets[nextIdx].mode;
            _camTargetId = targets[nextIdx].id;
            _followCamSnap = true;

            // 상세 로그: 이전→이후, 전체 목록 크기
            int pairCnt = 0, enemyCnt = 0;
            foreach (var t in targets) { if (t.mode == FollowCamMode.Pair) pairCnt++; else enemyCnt++; }
            Debug.LogWarning($"[FollowCam] C키: {prev.Item1}[{prev.Item2}] → {_camMode}[{_camTargetId}] " +
                $"(curIdx={curIdx}→{nextIdx}, total={targets.Count}, pairs={pairCnt}, enemies={enemyCnt})");
        }

        /// <summary>
        /// 첫 유효 타겟 자동 선택 (타겟 없을 때)
        /// </summary>
        private void SelectFirstAvailableCamTarget()
        {
            // 적군 먼저 (아군 배치 전에도 볼 수 있도록)
            if (enemyShips != null)
            {
                for (int i = 0; i < enemyShips.Length; i++)
                {
                    if (enemyShips[i] != null && !_neutralizedEnemies.Contains(enemyShips[i]))
                    {
                        _camMode = FollowCamMode.Enemy;
                        _camTargetId = i;
                        _followCamSnap = true;
                        return;
                    }
                }
            }

            // 아군 페어
            if (launchZoneManager != null)
            {
                int cap = launchZoneManager.GetPoolCapacity();
                for (int i = 0; i < cap; i++)
                {
                    var pair = launchZoneManager.GetPair(i);
                    if (pair != null && pair.isActive)
                    {
                        _camMode = FollowCamMode.Pair;
                        _camTargetId = i;
                        _followCamSnap = true;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// LateUpdate: 카메라 추적
        /// Pair → 두 선박 중심점 (웹 + 양선박 한 화면)
        /// Enemy → 해당 선박 개별 추적
        /// </summary>
        private void LateUpdate()
        {
            if (_followCamCamera == null || _camMode == FollowCamMode.None) return;

            Vector3 targetPos;
            Vector3 camForward;
            float camHeight, camBack;

            if (_camMode == FollowCamMode.Pair)
            {
                var pair = launchZoneManager.GetPair(_camTargetId);
                if (pair == null || !pair.isActive) return;

                Vector3 p1 = pair.agent1 != null ? pair.agent1.transform.position : Vector3.zero;
                targetPos = p1;
                targetPos.y = 0f;

                Vector3 fwd1 = pair.agent1 != null ? pair.agent1.transform.forward : Vector3.forward;
                camForward = fwd1;
                camForward.y = 0f;
                if (camForward.sqrMagnitude < 0.01f) camForward = Vector3.forward;
                camForward.Normalize();

                float shipDist = 50f; // single ship — fixed camera distance
                camHeight = Mathf.Max(30f, shipDist * 0.8f);
                camBack = Mathf.Max(30f, shipDist * 0.6f);
            }
            else // Enemy
            {
                if (_camTargetId < 0 || _camTargetId >= enemyShips.Length || enemyShips[_camTargetId] == null) return;
                var enemy = enemyShips[_camTargetId];

                targetPos = enemy.transform.position;
                targetPos.y = 0f;

                camForward = enemy.transform.forward;
                camForward.y = 0f;
                if (camForward.sqrMagnitude < 0.01f) camForward = Vector3.forward;
                camForward.Normalize();

                camHeight = 40f;
                camBack = 40f;
            }

            Vector3 desiredPos = targetPos + Vector3.up * camHeight + camForward * -camBack;

            if (_followCamSnap)
            {
                Debug.LogWarning($"[FollowCam] SNAP! cam→{desiredPos:F0}, mode={_camMode}[{_camTargetId}]");
                _followCamCamera.transform.position = desiredPos;
                _followCamSnap = false;
            }
            else
            {
                _followCamCamera.transform.position = Vector3.Lerp(
                    _followCamCamera.transform.position, desiredPos, 5f * Time.unscaledDeltaTime);
            }
            _followCamCamera.transform.LookAt(targetPos + Vector3.up * 1f);
        }

        /// <summary>
        /// PushBlockEnvController 패턴: FixedUpdate에서 타이머 관리
        /// </summary>
        private void FixedUpdate()
        {
            // 에피소드가 종료 중이면 타이머 증가하지 않음
            if (_episodeEnding)
                return;
            
            // 에피소드가 활성화되지 않았으면 타이머만 증가하지 않음 (종료 조건은 체크 가능)
            if (!_episodeActive)
                return;
            
            _resetTimer++;

            // 경로 기록
            if (trajectoryLogger != null)
                trajectoryLogger.RecordStep(_resetTimer, launchZoneManager, _enemyPool, _neutralizedEnemies);

            // 적군 돌진 이동 (동적 스폰 + enableEnemyRush 활성 시)
            if (useDynamicSpawn && enableEnemyRush && motherShip != null)
            {
                DriveEnemiesForward();
            }

            // 스폰 직후 물리 안정화 유예기간 (첫 10스텝은 종료 조건 체크 안 함)
            bool inGracePeriod = _resetTimer <= 10;

            // 최대 스텝 수 체크 (PushBlockEnvController 패턴)
            if (_resetTimer >= maxEnvironmentSteps && maxEnvironmentSteps > 0)
            {
                RestartEpisode("MaxEnvironmentSteps");
                return;
            }

            // 아군 간 거리 체크 - 다중 쌍 지원
            if (!inGracePeriod)
            {
                if (launchZoneManager != null)
                {
                    // ONE-WAY TOWING: 협력기동(prtBrg 역전) 체크 제거 — agent2는 invisible anchor
                    // 모든 쌍 무력화 시 에피소드 종료 (한 번이라도 배치된 적이 있을 때만)
                    if (!disableNoPairsEndEpisode
                        && currentStage != TrainingStage.Stage9_DisarmReform
                        && currentStage != TrainingStage.Stage10_FlankCapture
                        && launchZoneManager.GetOperationalPairCount() == 0 && launchZoneManager.GetDeployedPairCount() > 0)
                    {
                        CheckEpisodeEndCondition();
                        return;
                    }
                }
                else if (defenseAgent1 != null && defenseAgent2 != null)
                {
                    // 레거시 단일 쌍: 위치 교차 체크만 유지
                    Vector3 pos1 = defenseAgent1.transform.position;
                    Vector3 pos2 = defenseAgent2.transform.position;

                    // 좌/우 교차 체크 (레거시 단일 쌍)
                    bool a1CurrentlyOnLeft = pos1.x < pos2.x;
                    if (a1CurrentlyOnLeft != _agent1StartsOnLeft)
                    {
                        RestartEpisode($"PositionSwapped(startLeft={_agent1StartsOnLeft},nowLeft={a1CurrentlyOnLeft})", rewardCalculator.collisionPenalty);
                        return;
                    }
                }
            }

            // ONE-WAY TOWING: 앵커 드롭 전 agent2가 agent1을 kinematic으로 추종
            UpdateKinematicAnchors();

            // 분리 트리거 + 파트너 과분리 비활성화
            if (!inGracePeriod)
                ProcessSplitAndSeparation();

            // 방어선 돌파 체크: 적이 모선에 너무 가까우면 해당 적만 무력화
            if (!inGracePeriod && motherShip != null && enemyBreachThreshold > 0f)
            {
                Vector3 motherPos = motherShip.transform.position;

                foreach (var enemy in enemyShips)
                {
                    if (enemy == null || !enemy.activeInHierarchy) continue;
                    float enemyToMother = Vector3.Distance(enemy.transform.position, motherPos);

                    if (enemyToMother < enemyBreachThreshold)
                    {
                        // 페널티 부여
                        if (m_AgentGroup != null)
                            m_AgentGroup.AddGroupReward(rewardCalculator.enemyBreachPenalty);

                        // Commander에도 돌파 페널티
                        if (commanderAgent != null && IsCommanderStage())
                            commanderAgent.AddReward(rewardCalculator.enemyBreachPenalty);

                        _breachedEnemyCount++;
                        DisableEnemy(enemy);
                        Debug.LogWarning($"[DefenseEnv] EnemyBreach → {enemy.name} 무력화, dist={enemyToMother:F0}m, step={_resetTimer}");
                    }
                }
                CheckEpisodeEndCondition();
                if (_episodeEnding) return;
            }

            // Phantom 독립 기동 업데이트 + 가상 Web 포획 (Stage6에서만)
            if (currentStage == TrainingStage.Stage6_PhantomFormation && motherShip != null && launchZoneManager != null)
            {
                Vector3 motherPosP = motherShip.transform.position;
                int poolCapP = launchZoneManager.GetPoolCapacity();
                float dt = Time.fixedDeltaTime;

                for (int pi = 0; pi < poolCapP; pi++)
                {
                    DefensePair pair = launchZoneManager.GetPair(pi);
                    if (pair == null || !pair.isActive || pair.isDisarmed || pair.agent1 == null) continue;

                    GameObject targetEnemy = null;
                    int tIdx = pair.agent1.assignedTargetIndex;
                    if (tIdx > 0) targetEnemy = GetPooledEnemy(tIdx - 1);
                    if (targetEnemy == null || !targetEnemy.activeSelf)
                        targetEnemy = pair.agent1.GetAssignedEnemy();
                    if (targetEnemy == null) continue;

                    Vector3 enemyPosP = targetEnemy.transform.position;

                    // Phantom 기동 업데이트 (가상 아군은 적에게 영향 없음 — 충돌 회피 학습용)
                    pair.agent1.phantomSpacing = phantomSpacing;
                    pair.agent1.UpdatePhantoms(dt, enemyPosP, motherPosP);
                    if (pair.agent2 != null)
                    {
                        pair.agent2.phantomSpacing = phantomSpacing;
                        pair.agent2.UpdatePhantoms(dt, enemyPosP, motherPosP);
                    }
                }
            }

            // 적 추월 체크: 배정된 적이 아군보다 모선에 가까워지면 페널티 + 비활성화
            if (!inGracePeriod && motherShip != null && launchZoneManager != null)
            {
                Vector3 motherPos2 = motherShip.transform.position;
                int poolCap2 = launchZoneManager.GetPoolCapacity();

                for (int pi = 0; pi < poolCap2; pi++)
                {
                    DefensePair pair = launchZoneManager.GetPair(pi);
                    if (pair == null || !pair.isActive || pair.isDisarmed) continue;
                    if (pair.agent1 == null) continue;
                    if (pair.deployStep >= 0 && (_resetTimer - pair.deployStep) < 50) continue;

                    // Neutralized(EXIT) 쌍 스킵
                    if (pair.agent1.IsNeutralized) continue;

                    Vector3 pairCenter = pair.agent1.transform.position;
                    float allyDist = Vector3.Distance(pairCenter, motherPos2);

                    float farthestEnemyDist = 0f;
                    bool hasActiveEnemy = false;

                    if (_enemyPool != null)
                    {
                        for (int ei = 0; ei < _enemyPool.Length; ei++)
                        {
                            if (_enemyPool[ei] == null || !_enemyPool[ei].activeSelf) continue;
                            if (IsEnemyNeutralized(_enemyPool[ei])) continue;

                            float eDist = Vector3.Distance(_enemyPool[ei].transform.position, motherPos2);
                            if (eDist > farthestEnemyDist)
                            {
                                farthestEnemyDist = eDist;
                                hasActiveEnemy = true;
                            }
                        }
                    }

                    if (!hasActiveEnemy) continue;

                    // 가장 먼 적조차 아군보다 모선에 가까움 = 완전 추월
                    if (farthestEnemyDist < allyDist)
                    {
                        pair.agent1.AddReward(rewardCalculator.collisionPenalty);
                        DisableOrDisarmPair(pi);
                        Debug.LogWarning($"[DefenseEnv] Pair {pi} 완전 추월: farthestEnemy={farthestEnemyDist:F0}m < ally={allyDist:F0}m, step={_resetTimer}");
                    }
                }

                if (!disableNoPairsEndEpisode
                    && currentStage != TrainingStage.Stage9_DisarmReform
                    && currentStage != TrainingStage.Stage10_FlankCapture
                    && launchZoneManager.GetOperationalPairCount() == 0 && launchZoneManager.GetDeployedPairCount() > 0)
                {
                    CheckEpisodeEndCondition();
                    if (_episodeEnding) return;
                }
            }

            // 유효 타겟 없는 아군 쌍 비활성화 (10스텝마다 체크)
            if (launchZoneManager != null && motherShip != null && _resetTimer % 10 == 0)
            {
                DeactivatePairsWithNoValidTarget();
            }

            // Stage8/9: 어떤 아군보다 모선에 가까운 적이 있으면 예비 쌍 출동 (100스텝마다)
            if ((currentStage == TrainingStage.Stage8_TacticalFullObs || currentStage == TrainingStage.Stage9_DisarmReform)
                && launchZoneManager != null && motherShip != null && _resetTimer % 100 == 0)
            {
                Stage8DeployForUncoveredEnemies();
            }

            // Stage9: 직진 이탈 중인 쌍 타이머 체크
            if (currentStage == TrainingStage.Stage9_DisarmReform && launchZoneManager != null)
            {
                ProcessDisarmedPairs();
            }

            // 타겟 사망 시에만 재배정 (100스텝마다 체크)
            if (_resetTimer % 100 == 0)
                RefreshDeadTargets();

            // 보상 계산 주기 확인
            if (_resetTimer % rewardCalculationInterval != 0)
                return;

            if (rewardCalculator == null)
                return;

            // 모든 활성 쌍에 대해 보상 계산 (Only-oneattack 방식)
            float totalStepReward = 0f;

            // 활성 적군 배열 준비
            GameObject[] activeEnemyArray = _enemyPool ?? enemyShips;

            if (launchZoneManager != null && launchZoneManager.IsInitialized)
            {
                int poolCount = launchZoneManager.GetCurrentPoolCount();
                for (int pi = 0; pi < poolCount; pi++)
                {
                    var pair = launchZoneManager.GetPair(pi);
                    if (pair == null || !pair.isActive || pair.isDisarmed) continue;
                    if (pair.agent1 == null) continue;
                    if (pair.agent1.IsNeutralized) continue;
                    if (pair.isSplitting) continue; // 분리 전개 중 — 보상 스킵

                    var a1State = rewardCalculator.GetAgentState(pair.agent1);

                    // 클러스터→모선 LOS 방향 계산 (대형 보상 수직 성분용)
                    Vector3 pairLosDir = Vector3.zero;
                    if (motherShip != null && pair.clusterCentroid != Vector3.zero)
                    {
                        Vector3 lv = motherShip.transform.position - pair.clusterCentroid;
                        lv.y = 0f;
                        if (lv.magnitude > 0.1f) pairLosDir = lv.normalized;
                    }

                    float pairReward = rewardCalculator.CalculateStepReward(
                        pi, a1State, a1State, activeEnemyArray, pair.webObject, pairLosDir);
                    if (Mathf.Abs(pairReward) > 0.0001f)
                        pair.agent1.AddReward(pairReward);
                    totalStepReward += pairReward;

                    // 개별 헤딩 정렬 보상 (그물 전개 중에는 스킵 — RL이 방향 제어 안 함)
                    if (rewardCalculator.headingAlignmentReward > 0f && activeEnemyArray != null && !pair.isSplitting)
                    {
                        float h1 = rewardCalculator.CalculateIndividualHeadingReward(a1State, activeEnemyArray);
                        if (h1 != 0f) pair.agent1.AddReward(h1);
                        totalStepReward += h1;
                    }

                    // 그물 전개 중 앵커 거리 증가 보상 (One-Way Towing)
                    if (pair.isSplitting && pair.anchorObject != null && pair.agent1 != null)
                    {
                        float anchorDist = Vector3.Distance(pair.agent1.transform.position, pair.anchorObject.transform.position);
                        float growthReward = rewardCalculator.CalculateWebGrowthReward(pi, anchorDist);
                        if (growthReward != 0f)
                        {
                            pair.agent1.AddReward(growthReward);
                            totalStepReward += growthReward;
                        }
                    }
                }
            }
            else if (defenseAgent1 != null && defenseAgent2 != null)
            {
                // 레거시 단일 쌍 fallback
                var a1State = rewardCalculator.GetAgentState(defenseAgent1);
                var a2State = rewardCalculator.GetAgentState(defenseAgent2);
                float stepReward = rewardCalculator.CalculateStepReward(0, a1State, a2State, activeEnemyArray, webObject);
                if (Mathf.Abs(stepReward) > 0.0001f)
                {
                    if (m_AgentGroup != null) m_AgentGroup.AddGroupReward(stepReward);
                    else { defenseAgent1.AddReward(stepReward); defenseAgent2.AddReward(stepReward); }
                }
                totalStepReward = stepReward;
            }

            // 시간 페널티는 CalculateStepReward 내부에 포함됨 (별도 그룹 부여 불필요)

            // 레이 시각화: 활성 Ray 라인 숨김 (보상 계산 없음)
            if (_enemyRayLines != null && _enemyPool != null)
            {
                for (int ei = 0; ei < Mathf.Min(_enemyPool.Length, _enemyRayLines.Length); ei++)
                {
                    if (_enemyRayLines[ei] != null)
                        _enemyRayLines[ei].enabled = false;
                }
            }

            // ── Raycast 차단 보상: 각 활성 적 → 모선 Ray가 Web에 닿으면 해당 쌍 보상 ──
            if (motherShip != null && _enemyPool != null && rewardCalculator.raycastInterceptReward > 0f)
            {
                // 스텝 시작 시 차단 상태 초기화
                if (_enemyRayBlocked != null && _enemyRayBlocked.Length == _enemyPool.Length)
                    System.Array.Clear(_enemyRayBlocked, 0, _enemyRayBlocked.Length);

                Vector3 motherPos = motherShip.transform.position;
                for (int ei = 0; ei < _enemyPool.Length; ei++)
                {
                    if (_enemyPool[ei] == null || !_enemyPool[ei].activeSelf) { HideRayLine(ei); continue; }
                    if (IsEnemyNeutralized(_enemyPool[ei]))                   { HideRayLine(ei); continue; }

                    Vector3 enemyPos    = _enemyPool[ei].transform.position;
                    Vector3 toMother    = motherPos - enemyPos; toMother.y = 0f;
                    float   distToMother = toMother.magnitude;
                    if (distToMother < 1f) continue;

                    Vector3 rayDir = toMother.normalized;

                    // 시각화
                    ShowRayLine(ei, enemyPos, enemyPos + rayDir * distToMother);

                    // Raycast (Trigger 콜라이더 포함)
                    RaycastHit[] hits = Physics.RaycastAll(
                        enemyPos + Vector3.up * 2f, rayDir, distToMother,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);

                    bool hitWeb = false;
                    foreach (var hit in hits)
                    {
                        var dw = hit.collider.GetComponent<DynamicWeb>()
                              ?? hit.collider.GetComponentInParent<DynamicWeb>();
                        if (dw == null) continue;

                        hitWeb = true;
                        if (_enemyRayBlocked != null && ei < _enemyRayBlocked.Length)
                            _enemyRayBlocked[ei] = true;

                        // 해당 Web의 쌍 찾기 → 보상
                        if (launchZoneManager != null)
                        {
                            int poolCap = launchZoneManager.GetPoolCapacity();
                            for (int pi2 = 0; pi2 < poolCap; pi2++)
                            {
                                DefensePair p = launchZoneManager.GetPair(pi2);
                                if (p == null || !p.isActive || p.webObject == null) continue;
                                var pDw = p.webObject.GetComponent<DynamicWeb>();
                                if (pDw != dw) continue;

                                float r = rewardCalculator.raycastInterceptReward;
                                if (p.agent1 != null) p.agent1.AddReward(r);
                                if (p.agent2 != null) p.agent2.AddReward(r);
                                totalStepReward += r;
                                break;
                            }
                        }
                        break; // 적 하나당 Web 하나만
                    }

                    SetRayLineColor(ei, hitWeb);
                }
            }


            // Inspector 모니터링
            _lastStepReward = totalStepReward;
            _currentEpisodeReward += totalStepReward;
            _currentStep = _resetTimer;
            _totalCollisions = _totalCollisionCount;

            // Commander 매 스텝 보상 (미교전 적 접근 페널티 + 시간 페널티)
            if (commanderAgent != null && IsCommanderStage())
                commanderAgent.CalculateStepReward();
        }

        /// <summary>
        /// 적의 진행 경로(Ray)에서 Web 중심까지의 차단 기하 계산
        /// interceptDist: Ray-Web 수직 거리 (적이 직진하면 불변 → 아군 이동만 반영)
        /// alongDist: 적 전방 기준 Web까지의 투영 거리 (양수=Web이 적 앞, 음수=적 뒤)
        /// </summary>
        private void CalculateInterceptGeometry(Vector3 webCenter, Transform enemy,
            out float interceptDist, out float alongDist)
        {
            Vector3 toWeb = webCenter - enemy.position;
            toWeb.y = 0f;
            Vector3 enemyDir = enemy.forward;
            enemyDir.y = 0f;

            if (enemyDir.sqrMagnitude < 0.001f)
            {
                // 적의 forward가 거의 0 (정지/수직) → fallback
                interceptDist = toWeb.magnitude;
                alongDist = 0f;
                return;
            }
            enemyDir.Normalize();

            alongDist = Vector3.Dot(toWeb, enemyDir);

            if (alongDist <= 0f)
            {
                // Web이 적 뒤에 있음 → 차단 불가
                interceptDist = float.MaxValue;
                alongDist = 0f;
                return;
            }

            // 수직 거리: toWeb에서 along 방향 성분을 빼면 수직 성분
            Vector3 perpVec = toWeb - alongDist * enemyDir;
            interceptDist = perpVec.magnitude;
        }

        #region 중앙 허브: 통합 에피소드 재시작 로직

        /// <summary>Phase1: 모든 활성 쌍이 isDisarmed 상태인지 확인</summary>
        private bool AreAllActivePairsDisarmed()
        {
            if (launchZoneManager == null) return false;
            int cap = launchZoneManager.GetPoolCapacity();
            int activeCount = 0;
            for (int i = 0; i < cap; i++)
            {
                var p = launchZoneManager.GetPair(i);
                if (p == null || !p.isActive) continue;
                activeCount++;
                if (!p.isDisarmed) return false;
            }
            return activeCount > 0;
        }

        /// <summary>
        /// 통합 에피소드 재시작 메서드 (중앙 허브)
        /// 모든 에피소드 재시작 로직을 여기서 처리합니다.
        /// </summary>
        /// <param name="reason">에피소드 종료 이유 (디버깅용)</param>
        /// <param name="finalReward">최종 보상 (선택사항)</param>
        public void RestartEpisode(string reason = "Unknown", float? finalReward = null)
        {
            // 중복 호출 방지
            if (_episodeEnding)
            {
                return;
            }

            // 상세 원인 로그 (Warning으로 Console에서 눈에 띄게)
            string detail = "";
            if (defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 p1 = defenseAgent1.transform.position;
                Vector3 p2 = defenseAgent2.transform.position;
                float dist = Vector3.Distance(p1, p2);
                detail = $" | allyDist={dist:F1}m, pos1={p1}, pos2={p2}";
            }
            Debug.LogWarning($"[DefenseEnv] ★ EPISODE END ★ reason={reason}, step={_resetTimer}, reward={finalReward}, ep={_episodeNumber}{detail}");

            // 경로 기록 저장
            if (trajectoryLogger != null)
                trajectoryLogger.SaveAndReset(reason, motherShip != null ? motherShip.transform.position : Vector3.zero);

            // 충돌 횟수 초기화 (에피소드 종료 시 즉시 리셋)
            _totalCollisionCount = 0;
            _collisionCooldownTimes.Clear();
            _allyWebCollisionTimes.Clear();

            // Inspector 모니터 초기화
            _currentEpisodeReward = 0f;
            _lastStepReward = 0f;
            _currentStep = 0;
            _totalCollisions = 0;

            // 에피소드 번호 증가 및 로그 출력
            _episodeNumber++;
            
            // 타이머도 여기서 명시적으로 리셋 (안전장치)
            _resetTimer = 0;

            // 1단계: 에피소드 종료 플래그 설정
            _episodeEnding = true;
            
            // 2단계: 최종 보상 부여 (있는 경우)
            if (finalReward.HasValue)
            {
                if (m_AgentGroup != null)
                {
                    m_AgentGroup.AddGroupReward(finalReward.Value);
                }
                else
                {
                    if (defenseAgent1 != null)
                        defenseAgent1.AddReward(finalReward.Value);
                    if (defenseAgent2 != null)
                        defenseAgent2.AddReward(finalReward.Value);
                }
            }
            
            // 2.5단계: Commander 에피소드 종료 보상 + EndEpisode
            if (commanderAgent != null && IsCommanderStage())
            {
                commanderAgent.CalculateEpisodeEndReward();
                commanderAgent.SetEpisodeEnded();
                commanderAgent.EndEpisode();
            }

            // 3단계: 에이전트 에피소드 종료
            if (m_AgentGroup != null)
            {
                // UnregisterAgent로 해제된 에이전트를 다시 등록 (그룹이 비어있으면 EndGroupEpisode 무시됨)
                if (launchZoneManager != null)
                {
                    int poolCount = launchZoneManager.GetCurrentPoolCount();
                    for (int i = 0; i < poolCount; i++)
                    {
                        DefensePair pair = launchZoneManager.GetPair(i);
                        if (pair == null) continue;
                        if (pair.agent1 != null && pair.agent1.gameObject.activeInHierarchy)
                            m_AgentGroup.RegisterAgent(pair.agent1);
                        if (pair.agent2 != null && pair.agent2.gameObject.activeInHierarchy)
                            m_AgentGroup.RegisterAgent(pair.agent2);
                    }
                }
                // 레거시 에이전트도 등록 (LaunchZoneManager 없을 때)
                if (defenseAgent1 != null && defenseAgent1.gameObject.activeInHierarchy)
                    m_AgentGroup.RegisterAgent(defenseAgent1);
                if (defenseAgent2 != null && defenseAgent2.gameObject.activeInHierarchy)
                    m_AgentGroup.RegisterAgent(defenseAgent2);

                m_AgentGroup.EndGroupEpisode();
            }
            else
            {
                if (defenseAgent1 != null)
                {
                    defenseAgent1.EndEpisode();
                }
                if (defenseAgent2 != null)
                {
                    defenseAgent2.EndEpisode();
                }
            }
            
            // 4.5단계: Self-Play 적군 에이전트 에피소드 종료
            if (_enemyPool != null)
            {
                for (int i = 0; i < _enemyPool.Length; i++)
                {
                    if (_enemyPool[i] == null) continue;
                    var attackAgent = _enemyPool[i].GetComponent<AttackAgent>();
                    if (attackAgent != null && attackAgent.selfPlayMode)
                        attackAgent.EndEpisode();
                }
            }

            // 5단계: 환경 리셋
            ResetScene();
        }
        
        /// <summary>
        /// PushBlockEnvController 패턴: 환경 리셋 (ML-Agents가 OnEpisodeBegin을 자동 호출)
        /// RestartEpisode()에서 호출됩니다.
        /// </summary>
        public void ResetScene()
        {
            
            // 충돌 횟수 리셋
            _totalCollisionCount = 0;
            _breachedEnemyCount = 0;
            _capturedEnemyCount = 0;
            _allyPairsCreatedThisEpisode = 0;
            _prevAvgCoverage = float.MaxValue;

            // 총 페어 사용 카운터 리셋
            if (launchZoneManager != null)
                launchZoneManager.ResetTotalPairsDeployed();

            // 무력화 적군 추적 초기화
            _neutralizedEnemies.Clear();
            
            // 기존 리셋 코루틴 중지 및 플래그 초기화
            StopAllCoroutines();
            _isResettingPositions = false;

            // Stage9: 트랩 그물 정리
            ClearAllTraps();
            _lastResetFrame = -1; // 프레임 체크 초기화

            // 이전 에피소드에서 비정상 상태로 남은 쌍들 초기화 (disarm + isSplitting)
            if (launchZoneManager != null)
            {
                for (int i = 0; i < launchZoneManager.GetPoolCapacity(); i++)
                {
                    var pair = launchZoneManager.GetPair(i);
                    if (pair == null) continue;

                    if (pair.isDisarmed)
                    {
                        if (pair.agent1 != null) pair.agent1.SetStraightMode(false);
                        if (pair.agent2 != null) pair.agent2.SetStraightMode(false);
                        pair.isDisarmed = false;
                        pair.disarmStep = -1;
                    }

                    if (pair.isSplitting)
                        pair.isSplitting = false;
                }
            }

            // 에피소드 종료 플래그를 먼저 리셋하여 다음 FixedUpdate()에서 타이머가 증가하지 않도록 함
            _episodeEnding = false;

            // 에피소드는 코루틴 완료 후 활성화 (초기에는 false로 시작)
            _episodeActive = false;

            _resetTimer = 0;

            // 적군 속도 랜덤화 (에피소드마다 다른 속도)
            if (enemySpeedRandomRange > 0f)
            {
                float minMul = 1f - enemySpeedRandomRange;
                float maxMul = 1f + enemySpeedRandomRange;
                currentEnemyRushThrottle = enemyRushThrottle * Random.Range(minMul, maxMul);
            }
            else
            {
                currentEnemyRushThrottle = enemyRushThrottle;
            }

            // 풀 노이즈 시드 초기화 (에피소드 재시작 시)
            if (_poolNoiseSeed != null)
            {
                for (int i = 0; i < _poolNoiseSeed.Length; i++)
                    _poolNoiseSeed[i] = Random.Range(0f, 1000f);
            }

            // RewardCalculator 리셋
            if (rewardCalculator != null)
            {
                rewardCalculator.Reset();
            }

            // Stage 설정 적용 (에피소드 시작 시마다)
            ApplyStageSettings();

            // 날씨 랜덤화 (에피소드 시작 시)
            if (randomizeWeatherOnEpisode && environmentController != null)
            {
                environmentController.RandomizeWeather();
            }

            // 바람 방향/세기 랜덤화 (도메인 랜덤화)
            WindzoneExtended.RandomizeWind(windSpeedMin, windSpeedMax);

            // 적군 경로 웨이포인트 랜덤화 (선박 리셋 전에 호출)
            RandomizeEnemyWaypoints();

            // attack_boat의 대기 중인 Invoke 취소 (폭발 등)
            CancelAttackBoatPendingActions();

            // Stage별 activePairCount 설정
            if (launchZoneManager != null)
                launchZoneManager.activePairCount = IsCommanderStage()
                    ? 0
                    : GetAllyPairCountForStage();

            // 모든 선박 리셋
            ResetPositionsOnly();

            // 카메라를 첫 번째 아군으로 초기화
            if (followCamera != null)
                followCamera.OnEpisodeReset();

            // 아군 생성 버튼 리셋
            if (spawnButtonUI != null)
                spawnButtonUI.OnEpisodeReset();

            // Commander 에피소드 리셋
            if (commanderAgent != null && IsCommanderStage())
                commanderAgent.OnEpisodeReset();
        }
        
        #endregion
        
        /// <summary>
        /// 에피소드 시작 (ML-Agents가 자동으로 호출, 환경 리셋은 ResetScene에서 처리)
        /// </summary>
        public void OnEpisodeBegin()
        {
            // 모든 WAKE 객체 제거 및 WakeGenerator 비활성화
            DestroyAllWakeObjects();
        }
        
        /// <summary>
        /// 에피소드 종료 (레거시 호환성 - RestartEpisode()로 리다이렉트)
        /// </summary>
        public void OnEpisodeEnd()
        {
            RestartEpisode("OnEpisodeEnd");
        }
        
        /// <summary>
        /// 적 포획 성공 처리 (중앙 허브로 리다이렉트)
        /// </summary>
        public void OnEnemyCaptured(Vector3 enemyPosition)
        {
            if (_episodeEnding) return;

            // OnEnemyHitWeb에서 이미 처리됨 (보상 + 무력화 + 종료조건 체크)
            // 레거시 호출 대비 fallback
            if (m_AgentGroup != null)
                m_AgentGroup.AddGroupReward(rewardCalculator.captureReward);
            CheckEpisodeEndCondition();
        }
        
        /// <summary>
        /// SingleNetCapture(개별 소형 포획 존)에 적 진입 시 처리.
        /// OnEnemyHitWeb과 동일한 보상/무력화 로직, capturingWeb 없이 단일 에이전트에 개별 보상.
        /// </summary>
        public void OnEnemyHitSingleNet(GameObject enemyBoat, DefenseAgent capturer)
        {
            if (_episodeEnding) return;
            if (_resetTimer <= 10) return;
            if (enemyBoat == null) return;

            enemyBoat = ResolveToPoolEntry(enemyBoat);

            // 이미 무력화된 적은 중복 카운트 방지
            if (IsEnemyNeutralized(enemyBoat)) return;

            float currentTime = Time.time;
            if (_collisionCooldownTimes.ContainsKey(enemyBoat))
            {
                if (currentTime - _collisionCooldownTimes[enemyBoat] < _collisionCooldown)
                    return;
            }
            _collisionCooldownTimes[enemyBoat] = currentTime;
            _totalCollisionCount++;
            _capturedEnemyCount++;

            float reward = rewardCalculator.captureReward;

            if (m_AgentGroup != null)
                m_AgentGroup.AddGroupReward(reward);
            else
            {
                if (capturer != null) capturer.AddReward(reward);
            }

            if (commanderAgent != null && IsCommanderStage())
            {
                commanderAgent.AddReward(reward);
                commanderAgent.OnEnemyCaptured();
            }

            // SingleNet 포획자에게 개별 보너스
            if (rewardCalculator.capturePairBonus > 0f && capturer != null)
                capturer.AddReward(rewardCalculator.capturePairBonus);

            var capturedAttack = enemyBoat.GetComponent<AttackAgent>();
            if (capturedAttack != null) capturedAttack.OnCapturedBySelfPlay();

            DisableEnemy(enemyBoat);
            CheckEpisodeEndCondition();
        }

        /// <summary>
        /// 적군이 Web에 충돌 시 처리:
        /// 1. 팀 보상 부여
        /// 2. 적군 무력화
        /// 3. 포획한 아군 쌍 풀 반환 (Web은 0.5초 후 비활성화)
        /// </summary>
        /// <summary>
        /// 적군이 웹 존 안에 머무는 동안 매 FixedUpdate 호출 (OnTriggerStay)
        /// </summary>
        public void OnEnemyInWebZone(GameObject enemyBoat, DynamicWeb web)
        {
            if (_episodeEnding || enemyBoat == null || web == null) return;
        }

        public void OnEnemyHitWeb(GameObject enemyBoat, DynamicWeb capturingWeb = null)
        {
            if (_episodeEnding) return;
            if (_resetTimer <= 10) return;
            if (enemyBoat == null) return;

            // 풀 엔트리로 해석 (자식 콜라이더 참조 문제 방지)
            enemyBoat = ResolveToPoolEntry(enemyBoat);

            // 이미 무력화된 적은 중복 카운트 방지
            if (IsEnemyNeutralized(enemyBoat)) return;

            // 중복 충돌 방지
            float currentTime = Time.time;
            if (_collisionCooldownTimes.ContainsKey(enemyBoat))
            {
                if (currentTime - _collisionCooldownTimes[enemyBoat] < _collisionCooldown)
                    return;
            }
            _collisionCooldownTimes[enemyBoat] = currentTime;
            _totalCollisionCount++;
            _capturedEnemyCount++;

            // 거리 보너스: 모선에서 멀리 잡을수록 보너스 (포획위치/스폰거리 비율)
            float captureDistance = motherShip != null
                ? Vector3.Distance(enemyBoat.transform.position, motherShip.transform.position)
                : 0f;
            float distRatio = enemySpawnDistance > 0f
                ? Mathf.Clamp01(captureDistance / enemySpawnDistance)
                : 0f;
            float reward = rewardCalculator.captureReward;

            // 그룹 보상: 팀 전체
            if (m_AgentGroup != null)
                m_AgentGroup.AddGroupReward(reward);
            else
            {
                if (defenseAgent1 != null) defenseAgent1.AddReward(reward);
                if (defenseAgent2 != null) defenseAgent2.AddReward(reward);
            }

            // 포획한 쌍에 개별 보너스 (MA-POCA 개인 채널 강화)
            if (rewardCalculator.capturePairBonus > 0f && capturingWeb != null && launchZoneManager != null)
            {
                int capPairIdx = launchZoneManager.GetPairIndexByWeb(capturingWeb.gameObject);
                if (capPairIdx >= 0)
                {
                    DefensePair capPair = launchZoneManager.GetPair(capPairIdx);
                    if (capPair?.agent1 != null) capPair.agent1.AddReward(rewardCalculator.capturePairBonus);
                    if (capPair?.agent2 != null) capPair.agent2.AddReward(rewardCalculator.capturePairBonus);
                }
            }

            // Commander에도 포획 보상 라우팅
            if (commanderAgent != null && IsCommanderStage())
            {
                commanderAgent.AddReward(reward);
                commanderAgent.OnEnemyCaptured();
            }

            // Self-Play: 포획당한 적군에 페널티
            var capturedAttack = enemyBoat.GetComponent<AttackAgent>();
            if (capturedAttack != null)
                capturedAttack.OnCapturedBySelfPlay();

            // 해당 적만 무력화 (비활성화)
            DisableEnemy(enemyBoat);

            if (_webDetector != null)
                _webDetector.ResetDetector();

            // 포획한 아군 쌍 현재 위치에서 정지 (Web은 0.5초 후 비활성화)
            FreezeCapturingPair(capturingWeb);

            // 전체 종료 조건 확인
            CheckEpisodeEndCondition();
        }

        /// <summary>
        /// 포획한 Web에서 아군 쌍을 식별하여 현재 위치에서 정지(freeze)
        /// HIDDEN_POS로 이동하지 않고, 에이전트는 그 자리에 멈춤
        /// Web만 0.5초 후 비활성화 (포획 연출)
        /// </summary>
        private void FreezeCapturingPair(DynamicWeb capturingWeb)
        {
            Debug.Log($"[FreezeCapturingPair] ENTER: stage={currentStage}, capturingWeb={capturingWeb != null}, launchZoneManager={launchZoneManager != null}");
            if (launchZoneManager == null) return;

            int pairIdx = -1;

            // 1차: capturingWeb에서 쌍 식별
            if (capturingWeb != null)
            {
                DefenseAgent agent = null;
                if (capturingWeb.defenseShip1 != null)
                    agent = capturingWeb.defenseShip1.GetComponent<DefenseAgent>();
                if (agent == null && capturingWeb.defenseShip2 != null)
                    agent = capturingWeb.defenseShip2.GetComponent<DefenseAgent>();
                if (agent != null)
                    pairIdx = launchZoneManager.FindPairIndex(agent);
                Debug.Log($"[FreezeCapturingPair] 1차 탐색: agent={agent?.name}, pairIdx={pairIdx}, ship1={capturingWeb.defenseShip1?.name}, ship2={capturingWeb.defenseShip2?.name}");
            }

            // 2차 fallback: capturingWeb이 null이면, 최근 포획 이벤트를 발생시킨 쌍 추정
            // (활성 쌍 중 webObject에 _hasTriggered=true인 WebCollisionDetector가 있는 쌍)
            if (pairIdx < 0)
            {
                for (int i = 0; i < launchZoneManager.GetPoolCapacity(); i++)
                {
                    var pair = launchZoneManager.GetPair(i);
                    if (pair == null || !pair.isActive || pair.isDisarmed) continue;
                    if (pair.webObject == null) continue;
                    var detector = pair.webObject.GetComponent<WebCollisionDetector>();
                    if (detector != null && detector.HasTriggered)
                    {
                        pairIdx = i;
                        // capturingWeb도 복구
                        if (capturingWeb == null)
                            capturingWeb = pair.webObject.GetComponent<DynamicWeb>();
                        Debug.Log($"[FreezeCapturingPair] fallback으로 Pair {i} 식별 (WebCollisionDetector.HasTriggered)");
                        break;
                    }
                }
            }

            if (pairIdx < 0)
            {
                Debug.LogWarning($"[FreezeCapturingPair] 포획 쌍을 찾을 수 없음! capturingWeb={capturingWeb}, stage={currentStage}");
                return;
            }

            if (pairIdx >= 0)
            {
                var pair = launchZoneManager.GetPair(pairIdx);

                if (keepWebAfterCapture)
                {
                    // 그물 유지 모드: 적만 제거, 쌍/Web 비활성화 없음 → 다중 포획 허용
                    // 포획한 쌍의 타겟을 클러스터 내 다음 생존 적으로 즉시 재배정
                    if (pair != null && currentStage != TrainingStage.Stage8_TacticalFullObs
                        && currentStage != TrainingStage.Stage9_DisarmReform)
                    {
                        // 현재 다른 쌍이 배정받은 적 인덱스를 먼저 수집 → 중복 방지
                        var taken = new System.Collections.Generic.HashSet<int>();
                        int poolCap = launchZoneManager.GetPoolCapacity();
                        for (int pi2 = 0; pi2 < poolCap; pi2++)
                        {
                            var other = launchZoneManager.GetPair(pi2);
                            if (other == null || !other.isActive || other == pair) continue;
                            if (other.agent1 != null && other.agent1.assignedTargetIndex > 0)
                                taken.Add(other.agent1.assignedTargetIndex - 1);
                        }
                        int nextEi = FindLiveEnemyInCluster(pair, taken);
                        if (pair.agent1 != null) pair.agent1.assignedTargetIndex = nextEi >= 0 ? nextEi + 1 : -1;
                        if (pair.agent2 != null) pair.agent2.assignedTargetIndex = nextEi >= 0 ? nextEi + 1 : -1;
                        Debug.Log($"[DefenseEnv] 포획 후 재배정 → Pair {pairIdx}, nextTarget={nextEi}, step={_resetTimer}");
                    }
                    Debug.Log($"[DefenseEnv] 포획 성공 (그물 유지) → Pair {pairIdx}, 누적={_capturedEnemyCount}, step={_resetTimer}");
                }
                else
                {
                    DisableOrDisarmPair(pairIdx);

                    // 비-Stage9: Web 0.5초간 유지 후 비활성화 (포획 연출)
                    if (currentStage != TrainingStage.Stage9_DisarmReform)
                    {
                        if (pair != null && pair.webObject != null)
                        {
                            pair.webObject.SetActive(true);
                            StartCoroutine(DelayedWebDisable(pair.webObject, 0.5f));
                        }
                    }

                    Debug.Log($"[DefenseEnv] 포획 성공 → Pair {pairIdx} 비활성화, step={_resetTimer}");
                }
            }
        }

        private System.Collections.IEnumerator DelayedWebDisable(GameObject webObj, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (webObj != null) webObj.SetActive(false);
        }

        /// <summary>
        /// 아군 선박이 다른 페어의 Web과 충돌 시 처리:
        /// 양쪽 쌍 모두 비활성화 (충돌한 쌍 + Web을 가진 쌍)
        /// </summary>
        public void OnAllyHitWeb(GameObject allyShip, DynamicWeb collidedWeb = null)
        {
            if (_episodeEnding || allyShip == null) return;
            if (_resetTimer <= 10) return;

            if (launchZoneManager == null || rewardCalculator == null) return;

            // 충돌 당한 쌍 (allyShip이 속한 쌍)
            int hitPairIdx = launchZoneManager.FindPairIndexByGameObject(allyShip);
            if (hitPairIdx < 0) return;

            DefensePair hitPair = launchZoneManager.GetPair(hitPairIdx);
            if (hitPair == null || !hitPair.isActive) return;

            // 정지 트랩 여부를 먼저 판별 — 이후 모든 가드의 예외 기준
            bool isFrozenTrap = currentStage == TrainingStage.Stage10_FlankCapture
                && collidedWeb != null && collidedWeb.IsFrozen;

            // 쿨다운: 정지 트랩은 1회만 처리하면 되므로 쿨다운 적용
            float now = Time.time;
            if (_allyWebCollisionTimes.TryGetValue(allyShip, out float lastTime)
                && now - lastTime < _allyWebCollisionCooldown)
                return;
            _allyWebCollisionTimes[allyShip] = now;

            // 배치 직후 유예 (50스텝) — 정지 트랩은 예외
            if (!isFrozenTrap && hitPair.deployStep >= 0 && (_resetTimer - hitPair.deployStep) < 50) return;

            // Disarmed(이탈/플랭크 중) 쌍은 일반 Web 충돌만 무시 — 정지 트랩은 예외
            if (!isFrozenTrap && hitPair.isDisarmed) return;

            float penalty = isFrozenTrap
                ? (rewardCalculator != null ? rewardCalculator.collisionPenalty : -0.5f)
                : (rewardCalculator != null ? rewardCalculator.collisionPenalty   : -0.5f);

            // 충돌 당한 쌍 벌점
            if (hitPair.agent1 != null) hitPair.agent1.AddReward(penalty);
            if (hitPair.agent2 != null) hitPair.agent2.AddReward(penalty);

            if (isFrozenTrap)
            {
                // 정지 트랩 충돌 → 강제 비활성화 (플랭크 쌍 보호 우회)
                DisableOrDisarmPair(hitPairIdx, forceDisable: true);
                NotifyCameraPairDisabled(hitPair);
                Debug.LogWarning($"[DefenseEnv] AllyHitFrozenTrap → Pair {hitPairIdx} 강제 비활성화 (penalty={penalty}), step={_resetTimer}");
                CheckEpisodeEndCondition();
                return;
            }

            // 일반 Web 충돌: Web을 가진 쌍(가해 쌍)에도 벌점
            if (collidedWeb != null && collidedWeb.defenseShip1 != null)
            {
                DefenseAgent webOwner = collidedWeb.defenseShip1.GetComponent<DefenseAgent>();
                if (webOwner != null)
                {
                    int webPairIdx = launchZoneManager.FindPairIndex(webOwner);
                    if (webPairIdx >= 0 && webPairIdx != hitPairIdx)
                    {
                        DefensePair webPair = launchZoneManager.GetPair(webPairIdx);
                        if (webPair?.agent1 != null) webPair.agent1.AddReward(rewardCalculator.collisionPenalty);
                        if (webPair?.agent2 != null) webPair.agent2.AddReward(rewardCalculator.collisionPenalty);
                    }
                }
            }

            // 걸린 아군 쌍 비활성화
            DisableOrDisarmPair(hitPairIdx);
            NotifyCameraPairDisabled(hitPair);
            Debug.LogWarning($"[DefenseEnv] AllyHitWeb → Pair {hitPairIdx} 비활성화 (penalty={penalty}), step={_resetTimer}");
            CheckEpisodeEndCondition();
        }

        /// <summary>
        /// 아군 선박들을 원점으로 리셋
        /// </summary>
        private void ResetDefenseAgentsToOrigin()
        {
            // 두 선박이 동일한 랜덤 각도를 바라보도록 한 번만 생성
            float sharedRandomAngle = enableRandomSpawn
                ? Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange)
                : 0f;

            // 두 선박이 동일한 위치 오프셋을 공유
            Vector3 sharedOffset = GetSharedRandomOffset();

            ResetDefenseAgentPosition(defenseAgent1, _originalDefense1Pos, _originalDefense1Rot, sharedRandomAngle, sharedOffset);
            ResetDefenseAgentPosition(defenseAgent2, _originalDefense2Pos, _originalDefense2Rot, sharedRandomAngle, sharedOffset);
        }

        /// <summary>
        /// 모선 충돌 처리 - 해당 공격선만 원점으로 리셋
        /// 충돌 횟수가 maxCollisionCount 이상이면 에피소드 종료
        /// </summary>
        public void OnMotherShipCollision(GameObject enemyBoat)
        {
            if (_episodeEnding) return;
            if (_resetTimer <= 10) return;
            if (enemyBoat == null) return;

            // 풀 엔트리로 해석 (자식 콜라이더 참조 문제 방지)
            enemyBoat = ResolveToPoolEntry(enemyBoat);

            // 중복 충돌 방지
            float currentTime = Time.time;
            if (_collisionCooldownTimes.ContainsKey(enemyBoat))
            {
                if (currentTime - _collisionCooldownTimes[enemyBoat] < _collisionCooldown)
                    return;
            }
            _collisionCooldownTimes[enemyBoat] = currentTime;
            _totalCollisionCount++;

            float penalty = rewardCalculator.motherShipHitPenalty;

            // 페널티 부여
            if (m_AgentGroup != null)
                m_AgentGroup.AddGroupReward(penalty);
            else
            {
                if (defenseAgent1 != null) defenseAgent1.AddReward(penalty);
                if (defenseAgent2 != null) defenseAgent2.AddReward(penalty);
            }

            // Self-Play: 모선 도달 적군에 보상
            var reachedAttack = enemyBoat.GetComponent<AttackAgent>();
            if (reachedAttack != null)
                reachedAttack.OnReachedMotherShipSelfPlay();

            // 해당 적만 무력화 (비활성화)
            DisableEnemy(enemyBoat);

            // 전체 종료 조건 확인
            CheckEpisodeEndCondition();
        }

        /// <summary>
        /// 단일 공격선을 새 위치로 리셋 (풀 인덱스 검색 → ResetPoolObject 호출)
        /// </summary>
        private void ResetSingleAttackBoat(GameObject attackBoat)
        {
            if (attackBoat == null || _enemyPool == null)
                return;

            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == attackBoat)
                {
                    // 동적 스폰이면 모선 기준 새 랜덤 위치, 아니면 원래 위치 유지
                    if (useDynamicSpawn && motherShip != null)
                    {
                        Vector3 motherPos = motherShip.transform.position;
                        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                        Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                        Vector3 spawnPos = motherPos + dir * enemySpawnDistance;
                        spawnPos.y = _poolTemplateY;

                        Vector3 lookDir = motherPos - spawnPos;
                        lookDir.y = 0f;
                        Quaternion spawnRot = lookDir.sqrMagnitude > 0.01f
                            ? Quaternion.LookRotation(lookDir, Vector3.up)
                            : Quaternion.identity;

                        ResetPoolObject(i, spawnPos, spawnRot);
                    }
                    else
                    {
                        ResetPoolObject(i, attackBoat.transform.position, Quaternion.identity);
                    }

                    UpdateEnemyShipsArray();
                    break;
                }
            }
        }

        /// <summary>
        /// 충돌 등으로 전달된 적군 GameObject를 _enemyPool 엔트리로 해석
        /// 자식 콜라이더의 gameObject가 전달될 수 있으므로, 풀 루트 오브젝트를 찾아 반환
        /// </summary>
        private GameObject ResolveToPoolEntry(GameObject obj)
        {
            if (obj == null || _enemyPool == null) return obj;

            // 1. 직접 매칭 (가장 흔한 경우)
            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == obj) return obj;
            }

            // 2. 부모 계층 탐색 (자식 콜라이더에서 전달된 경우)
            Transform t = obj.transform.parent;
            while (t != null)
            {
                for (int i = 0; i < _enemyPool.Length; i++)
                {
                    if (_enemyPool[i] != null && _enemyPool[i].transform == t)
                    {
                        Debug.LogWarning($"[DefenseEnv] ResolveToPoolEntry: {obj.name} → pool root {_enemyPool[i].name} (자식 콜라이더 감지)");
                        return _enemyPool[i];
                    }
                }
                t = t.parent;
            }

            // 3. attachedRigidbody fallback
            var rb = obj.GetComponentInParent<Rigidbody>();
            if (rb != null && rb.gameObject != obj)
            {
                for (int i = 0; i < _enemyPool.Length; i++)
                {
                    if (_enemyPool[i] == rb.gameObject)
                    {
                        Debug.LogWarning($"[DefenseEnv] ResolveToPoolEntry: {obj.name} → pool root {rb.gameObject.name} (Rigidbody fallback)");
                        return rb.gameObject;
                    }
                }
            }

            Debug.LogWarning($"[DefenseEnv] ResolveToPoolEntry: {obj.name} 풀에서 찾을 수 없음! 원본 반환");
            return obj;
        }

        /// <summary>
        /// 적군 선박 무력화 (비활성화, 리스폰 없음)
        /// 포획 또는 모선 충돌 시 해당 적만 제거
        /// </summary>
        private void DisableEnemy(GameObject enemyBoat)
        {
            if (enemyBoat == null) return;

            // 풀 엔트리로 해석 (자식 콜라이더 참조 문제 방지)
            enemyBoat = ResolveToPoolEntry(enemyBoat);

            // 이미 무력화된 경우 중복 처리 방지
            if (_neutralizedEnemies.Contains(enemyBoat))
            {
                Debug.Log($"[DefenseEnv] DisableEnemy: {enemyBoat.name} 이미 무력화됨, 스킵");
                return;
            }

            // AttackAgent 액션 차단 (_hasExploded → FixedUpdate/OnActionReceived 스킵)
            var attackAgent = enemyBoat.GetComponent<AttackAgent>();
            if (attackAgent != null)
                attackAgent.SetNeutralized();

            // 엔진 정지 (관성 제거)
            var rb = enemyBoat.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;  // 파도/부력 힘 차단 (GerstnerWaves 재등록 문제 방지를 위해 SetActive 사용 안 함)
            }

            // 활성 적군 목록에서 제거 (카메라/보상 계산에서 무시됨)
            MarkEnemyAsNeutralized(enemyBoat);

            // 카메라가 이 적을 추적 중이면 다른 선박으로 전환
            if (followCamera != null)
                followCamera.OnShipNeutralized(enemyBoat);

            Debug.Log($"[DefenseEnv] DisableEnemy: {enemyBoat.name} 무력화 완료, " +
                $"neutralizedCount={_neutralizedEnemies.Count}, step={_resetTimer}");
        }

        /// <summary>
        /// 무력화된 적군 등록 (enemyShips null 처리 + HashSet 추적 + _enemyPool 교차 등록)
        /// </summary>
        private void MarkEnemyAsNeutralized(GameObject enemyBoat)
        {
            _neutralizedEnemies.Add(enemyBoat);

            // _enemyPool에서도 같은 오브젝트를 확인하여 교차 등록
            if (_enemyPool != null)
            {
                for (int i = 0; i < _enemyPool.Length; i++)
                {
                    if (_enemyPool[i] != null && _enemyPool[i] == enemyBoat)
                    {
                        // 이미 추가됨 (동일 참조)
                        break;
                    }
                }
            }

            if (enemyShips == null) return;
            for (int i = 0; i < enemyShips.Length; i++)
            {
                if (enemyShips[i] == enemyBoat)
                {
                    enemyShips[i] = null;
                    break;
                }
            }
        }

        /// <summary>
        /// 적군이 무력화 상태인지 확인
        /// </summary>
        public bool IsEnemyNeutralized(GameObject enemy)
        {
            return enemy == null || _neutralizedEnemies.Contains(enemy);
        }

        #region Commander 쿼리 메서드

        /// <summary>
        /// 풀 인덱스로 적군 오브젝트 반환 (Commander 관측용)
        /// </summary>
        public GameObject GetPooledEnemy(int poolIndex)
        {
            if (_enemyPool == null || poolIndex < 0 || poolIndex >= _enemyPool.Length)
                return null;
            return _enemyPool[poolIndex];
        }

        /// <summary>
        /// 현재 포메이션 유형 반환 (Commander 관측용)
        /// </summary>
        public FormationType GetCurrentFormationType()
        {
            return _currentFormation;
        }

        /// <summary>
        /// 현재 적 접근 각도 반환 (Commander heuristic용)
        /// </summary>
        public float GetCurrentEnemyApproachAngle()
        {
            return _currentEnemyApproachAngle;
        }

        /// <summary>
        /// Commander Stage 여부 확인
        /// </summary>
        public bool IsCommanderStage()
        {
            return currentStage == TrainingStage.Stage4_Commander ||
                   currentStage == TrainingStage.Stage5_FullScale;
        }

        /// <summary>
        /// 모선 도달(돌파) 적군 수 반환 (Commander 에피소드 종료 보상용)
        /// </summary>
        public int GetBreachedEnemyCount()
        {
            return _breachedEnemyCount;
        }

        public int GetCapturedEnemyCount()
        {
            return _capturedEnemyCount;
        }

        /// <summary>
        /// 활성 적군 수 반환 (Commander 관측용)
        /// </summary>
        public int GetActiveEnemyCount()
        {
            if (_enemyPool == null) return 0;
            int count = 0;
            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] != null && _enemyPool[i].activeSelf && !_neutralizedEnemies.Contains(_enemyPool[i]))
                    count++;
            }
            return count;
        }

        #endregion

        /// <summary>
        /// 쌍 무력화 시 카메라에 양쪽 에이전트 알림
        /// </summary>
        private void NotifyCameraPairDisabled(DefensePair pair)
        {
            if (followCamera == null || pair == null) return;
            if (pair.agent1 != null) followCamera.OnShipNeutralized(pair.agent1.gameObject);
            if (pair.agent2 != null) followCamera.OnShipNeutralized(pair.agent2.gameObject);
        }

        private void HideRayLine(int ei)
        {
            if (_enemyRayLines == null || ei >= _enemyRayLines.Length || _enemyRayLines[ei] == null) return;
            _enemyRayLines[ei].enabled = false;
        }

        private void ShowRayLine(int ei, Vector3 from, Vector3 to)
        {
            if (_enemyRayLines == null || ei >= _enemyRayLines.Length || _enemyRayLines[ei] == null) return;
            _enemyRayLines[ei].enabled = true;
            _enemyRayLines[ei].SetPosition(0, from + Vector3.up * 2f);
            _enemyRayLines[ei].SetPosition(1, to   + Vector3.up * 2f);
        }

        private void SetRayLineColor(int ei, bool hit)
        {
            if (_enemyRayLines == null || ei >= _enemyRayLines.Length || _enemyRayLines[ei] == null) return;
            if (hit)
            {
                _enemyRayLines[ei].startColor = new Color(0f, 1f, 0f, 0.7f);
                _enemyRayLines[ei].endColor   = new Color(0f, 1f, 0f, 0.2f);
            }
            else
            {
                _enemyRayLines[ei].startColor = new Color(1f, 0f, 0f, 0.5f);
                _enemyRayLines[ei].endColor   = new Color(1f, 0f, 0f, 0.1f);
            }
        }

        /// <summary>
        /// 아군이 모든 적보다 모선에서 멀 때 (적이 아군을 지나침) 비활성화
        /// </summary>
        private void DeactivatePairsWithNoValidTarget()
        {
            Vector3 motherPos = motherShip.transform.position;
            int poolCount = launchZoneManager.GetCurrentPoolCount();

            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive || pair.isStandby) continue;
                if (pair.isDisarmed) continue;   // SingleNet 포획 모드: 트랩 너머 적은 항상 모선에 더 가까워 오탐 발생
                if (pair.isSplitting) continue;  // 분리 전개 중: 위치가 불안정해 유효 타겟 판정 오탐 발생
                if (pair.agent1 == null) continue;
                Vector3 pairCenter = pair.agent2 != null
                    ? (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f
                    : pair.agent1.transform.position;
                float pairDistToMother = Vector3.Distance(pairCenter, motherPos);

                // 아군보다 모선에서 멀거나 같은 적이 하나라도 있으면 유효 (아직 차단 가능)
                bool hasValidTarget = false;
                if (_enemyPool != null)
                {
                    for (int e = 0; e < _enemyPool.Length; e++)
                    {
                        if (_enemyPool[e] == null || !_enemyPool[e].activeSelf) continue;
                        if (IsEnemyNeutralized(_enemyPool[e])) continue;
                        float enemyDistToMother = Vector3.Distance(_enemyPool[e].transform.position, motherPos);
                        if (enemyDistToMother >= pairDistToMother)
                        {
                            hasValidTarget = true;
                            break;
                        }
                    }
                }

                if (!hasValidTarget)
                {
                    Debug.Log($"[DefenseEnv] Pair {i} 비활성화: 모든 적이 아군보다 모선에 가까움 (차단 불가)");
                    if (currentStage == TrainingStage.Stage6_PhantomFormation || currentStage == TrainingStage.Stage7_FleetManeuver)
                    {
                        // Stage6/7: 재배치 불가 → 페널티 + 영구 비활성화 → 에피소드 종료 유도
                        if (pair.agent1 != null) pair.agent1.AddReward(rewardCalculator.collisionPenalty);
                        if (pair.agent2 != null) pair.agent2.AddReward(rewardCalculator.collisionPenalty);
                        DisableOrDisarmPair(i);
                    }
                    else
                    {
                        launchZoneManager.ReturnPairToPool(i, m_AgentGroup);
                    }
                }
            }

            // Stage6/7: DisablePair 후 활성 쌍이 0이면 에피소드 종료
            if (!disableNoPairsEndEpisode
                && (currentStage == TrainingStage.Stage6_PhantomFormation || currentStage == TrainingStage.Stage7_FleetManeuver)
                && launchZoneManager.GetOperationalPairCount() == 0
                && launchZoneManager.GetDeployedPairCount() > 0)
            {
                CheckEpisodeEndCondition();
            }
        }


        /// <summary>
        /// Stage8: 해당 방향에 차단 가능한 아군이 없는 적 → 예비 쌍 출동 (배정 없이)
        /// 적 방향 ±coverAngle 범위 내에서 적보다 모선에 가까운 아군이 없으면 출동
        /// </summary>
        [Tooltip("Stage8 차단 판정 각도 (적 방향 ±이 값 내 아군만 체크)")]
        public float stage8CoverAngle = 60f;

        [Tooltip("Stage8 출동 불가 최소 거리 (적이 모선에서 이 거리 이내면 출동 안 함)")]
        public float stage8MinDeployDistance = 100f;

        [Tooltip("Stage8 한 번 호출 시 최대 출동 쌍 수 (0이면 무제한)")]
        public int stage8MaxDeployPerCall = 3;

        [Tooltip("레인 클러스터링 폭 (°). 이 각도 이내 적들은 1쌍이 커버 가능하다고 판단")]
        [Range(5f, 60f)]
        public float laneClusterWidth = 10f;

        /// <summary>
        /// 쌍의 중심에서 가장 가까운 활성 적까지의 거리
        /// </summary>
        /// <summary>
        /// 매 스텝: 쌍별 자동 분리(split) 트리거 + 파트너 과분리 비활성화
        /// </summary>
        private void ProcessSplitAndSeparation()
        {
            if (chaseTrainingMode) return;  // chase 모드: split 없음, isDisarmed에서 시작
            if (launchZoneManager == null) return;
            int poolCap = launchZoneManager.GetPoolCapacity();

            for (int pi = 0; pi < poolCap; pi++)
            {
                DefensePair pair = launchZoneManager.GetPair(pi);
                if (pair == null || !pair.isActive || pair.isDisarmed) continue;
                if (pair.agent1 == null || pair.anchorObject == null) continue;
                if (pair.agent1.IsNeutralized) continue;

                // 배치 직후 유예기간
                bool inGrace = pair.deployStep >= 0 && (_resetTimer - pair.deployStep) < 50;
                if (inGrace) continue;

                // 그물 폭 = agent1 ~ anchorObject 거리
                float dist = Vector3.Distance(pair.agent1.transform.position,
                                              pair.anchorObject.transform.position);

                // ── EXIT 체크: 분리 중 → 간격 충분하거나 타임아웃 → 정지 트랩 설치 ──
                if (pair.isSplitting)
                {
                    bool widthReached = dist >= webDeployedThreshold;
                    bool timeout = deployMaxSteps > 0
                        && (_resetTimer - pair.splitStartStep) > deployMaxSteps;

                    if (widthReached || timeout)
                    {
                        // 분리 조향 취소
                        pair.agent1.SetSplitMode(false);
                        pair.isSplitting = false;

                        // 선박 제동
                        Rigidbody rb1 = pair.agent1.GetComponent<Rigidbody>();
                        if (rb1 != null) { rb1.velocity = Vector3.zero; rb1.angularVelocity = Vector3.zero; }

                        // 그물 현재 위치에 고정 (정지 트랩으로 설치)
                        if (pair.webObject != null)
                        {
                            var dw = pair.webObject.GetComponent<DynamicWeb>();
                            if (dw != null) dw.FreezeAtCurrentPositions();
                        }

                        pair.isDisarmed = true;

                        Debug.LogWarning($"[Disarm] Pair {pi} phase1TrapOnlyMode={phase1TrapOnlyMode}  step={_resetTimer}");
                        if (phase1TrapOnlyMode)
                        {
                            pair.agent1.SetStopMode(true);

                            float deployBonus = rewardCalculator != null ? rewardCalculator.trapDeployBonus : 1.0f;
                            if (m_AgentGroup != null)
                                m_AgentGroup.AddGroupReward(deployBonus);
                            else
                                pair.agent1.AddReward(deployBonus);

                            Debug.Log($"[Phase1] Pair {pi} 트랩 설치 완료 → 정지. bonus={deployBonus:F2}, step={_resetTimer}");

                            if (AreAllActivePairsDisarmed())
                            {
                                float bonus = rewardCalculator != null ? rewardCalculator.allTrapsDeployedBonus : 2.0f;
                                RestartEpisode("AllTrapsDeployed", bonus);
                            }
                        }
                        else
                        {
                            pair.agent1.SetStopMode(true);

                            float deployBonus = rewardCalculator != null ? rewardCalculator.trapDeployBonus : 1.0f;
                            if (m_AgentGroup != null)
                                m_AgentGroup.AddGroupReward(deployBonus);
                            else
                                pair.agent1.AddReward(deployBonus);

                            Debug.Log($"[Trap] Pair {pi} 정지 트랩 설치 완료 → 정지. dist={dist:F1}m, timeout={timeout}, step={_resetTimer}");
                        }
                    }
                    continue;
                }

                // ── ONE-WAY TOWING: 적 근접 시 앵커 드롭 + 횡단 시작 (한 번만) ──
                {
                    float nearestEnemy = GetNearestActiveEnemyDistToPair(pair);
                    float effectiveTrigger = Mathf.Max(splitTriggerDistance, 1f);
                    if (nearestEnemy < effectiveTrigger)
                    {
                        // anchorObject를 현재 위치에 고정 (UpdateKinematicAnchors가 이미 동기화 중)
                        Vector3 anchorPos = pair.agent1.transform.position;
                        if (pair.anchorObject != null)
                        {
                            pair.anchorObject.transform.position = anchorPos;
                            Physics.SyncTransforms();
                        }

                        // towDir: agent1.forward 기준 오른쪽 수직 방향
                        Vector3 fwd = pair.agent1.transform.forward; fwd.y = 0f;
                        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; else fwd.Normalize();
                        Vector3 towDir = new Vector3(fwd.z, 0f, -fwd.x);

                        pair.agent1.SetTowMode(true, towDir);

                        pair.isSplitting = true;
                        pair.splitStartStep = _resetTimer;
                        Debug.Log($"[AnchorDrop] Pair {pi}: anchorPos={anchorPos:F0}, towDir={towDir:F2}, nearestEnemy={nearestEnemy:F1}m, step={_resetTimer}");
                    }
                }
            }
        }

        private float GetNearestActiveEnemyDistToPair(DefensePair pair)
        {
            if (_enemyPool == null || pair.agent1 == null) return float.MaxValue;
            Vector3 center = pair.agent1.transform.position;

            float minDist = float.MaxValue;
            for (int e = 0; e < _enemyPool.Length; e++)
            {
                if (_enemyPool[e] == null || !_enemyPool[e].activeInHierarchy) continue;
                if (IsEnemyNeutralized(_enemyPool[e])) continue;
                float d = Vector3.Distance(center, _enemyPool[e].transform.position);
                if (d < minDist) minDist = d;
            }
            return minDist;
        }

        /// <summary>
        /// ONE-WAY TOWING: 앵커 드롭 전(isSplitting=false)에 agent2를 agent1 위치로 kinematic 추종.
        /// dist=0 유지 → DynamicWeb 자동 숨김, maxPairSeparation 체크 미발동
        /// </summary>
        private void UpdateKinematicAnchors()
        {
            if (launchZoneManager == null) return;
            int poolCap = launchZoneManager.GetPoolCapacity();
            for (int pi = 0; pi < poolCap; pi++)
            {
                DefensePair pair = launchZoneManager.GetPair(pi);
                if (pair == null || !pair.isActive || pair.isDisarmed || pair.isSplitting) continue;
                if (pair.agent1 == null || pair.anchorObject == null) continue;

                // anchorObject를 agent1 현재 위치로 추종 → 그물 dist=0 유지 (자동 숨김)
                pair.anchorObject.transform.position = pair.agent1.transform.position;
            }
        }

        /// <summary>
        /// Stage9: 직진 이탈 중인 쌍들의 타이머를 체크하여
        /// 일정 스텝 경과 또는 모선에서 멀어지면 완전 비활성화
        /// </summary>
        private void ProcessDisarmedPairs()
        {
            if (launchZoneManager == null) return;
            int poolCap = launchZoneManager.GetPoolCapacity();

            for (int i = 0; i < poolCap; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isDisarmed) continue;

                // chase 모드: disarmed 쌍을 에피소드 내내 유지 (타임아웃 비활성화 없음)
                if (chaseTrainingMode) continue;

                // Stage10: isDisarmed = 플랭크 포획 모드 진입 의미 → 타임아웃 비활성화 없음
                if (currentStage == TrainingStage.Stage10_FlankCapture) continue;

                // disarmStep < 0: ProcessSplitAndSeparation Phase2 진입 쌍 (추격 모드)
                // DeactivateAndStraightenPair로 설정된 이탈 쌍이 아니므로 타임아웃 적용 안 함
                if (pair.disarmStep < 0) continue;

                int elapsed = _resetTimer - pair.disarmStep;

                // 최소 5스텝은 직진 유지 (즉시 비활성화 방지)
                if (elapsed < 5) continue;

                // 조건: 일정 스텝 경과 또는 모선에서 400m 이상
                bool timeout = elapsed >= disarmDurationSteps;
                bool farAway = false;
                float dist = 0f;
                if (motherShip != null && pair.agent1 != null)
                {
                    dist = Vector3.Distance(pair.agent1.transform.position, motherShip.transform.position);
                    farAway = dist > 400f;
                }

                if (timeout || farAway)
                {
                    Debug.Log($"[ProcessDisarmedPairs] Pair {i} 비활성화: elapsed={elapsed}, timeout={timeout}, farAway={farAway}, dist={dist:F0}, disarmStep={pair.disarmStep}, _resetTimer={_resetTimer}");
                    launchZoneManager.DeactivateDisarmedPair(i);
                }
            }
        }

        /// <summary>
        /// Stage9: 적군이 깔린 트랩 그물에 걸렸을 때 처리 (포획 보상 절반)
        /// </summary>
        public void OnEnemyHitTrap(GameObject enemyBoat)
        {
            if (_episodeEnding) return;
            if (enemyBoat == null) return;

            enemyBoat = ResolveToPoolEntry(enemyBoat);

            // 중복 충돌 방지
            float currentTime = Time.time;
            if (_collisionCooldownTimes.ContainsKey(enemyBoat))
            {
                if (currentTime - _collisionCooldownTimes[enemyBoat] < _collisionCooldown)
                    return;
            }
            _collisionCooldownTimes[enemyBoat] = currentTime;
            _totalCollisionCount++;
            _capturedEnemyCount++;

            float reward = rewardCalculator.captureReward * 0.5f;
            if (m_AgentGroup != null)
                m_AgentGroup.AddGroupReward(reward);

            DisableEnemy(enemyBoat);

            Debug.Log($"[DefenseEnv] 트랩 그물 포획! reward={reward:F2}, step={_resetTimer}");

            CheckEpisodeEndCondition();
        }

        /// <summary>
        /// Stage9: 아군이 깔린 트랩 그물에 걸렸을 때 처리
        /// 해당 쌍 비활성화 + allyWebCollisionPenalty 적용
        /// </summary>
        public void OnAllyHitTrap(DefenseAgent agent)
        {
            if (_episodeEnding) return;
            if (agent == null || launchZoneManager == null) return;

            int pairIdx = launchZoneManager.FindPairIndex(agent);
            if (pairIdx < 0) return;

            DefensePair pair = launchZoneManager.GetPair(pairIdx);
            if (pair == null || !pair.isActive) return;

            // 이미 비활성화/Disarm 처리된 쌍은 무시
            if (pair.isDisarmed) return;

            float trapPenalty = rewardCalculator != null ? rewardCalculator.collisionPenalty : -0.5f;
            if (pair.agent1 != null) pair.agent1.AddReward(trapPenalty);
            if (pair.agent2 != null) pair.agent2.AddReward(trapPenalty);

            // 비활성화 (Stage9이면 직진 이탈)
            DisableOrDisarmPair(pairIdx);
            NotifyCameraPairDisabled(pair);

            Debug.LogWarning($"[DefenseEnv] AllyHitTrap → Pair {pairIdx} 트랩 충돌 무력화 (penalty={trapPenalty}), step={_resetTimer}");

            CheckEpisodeEndCondition();
        }

        /// <summary>
        /// 수선의 발 기반 타겟 배정:
        /// Web에 차단되지 않은 적 중 Web 중심 → 적 Ray 수직거리가 가장 작은 적을 배정.
        /// </summary>
        /// <summary>
        /// 배정된 적이 사망(비활성/무력화)했을 때만 클러스터 내 다음 생존 적으로 재배정.
        /// 살아있으면 절대 변경하지 않음.
        /// </summary>
        private void RefreshDeadTargets()
        {
            if (launchZoneManager == null || enemyShips == null) return;
            // Stage8/9: 자율 판단 설계 — 타겟 강제 배정 금지
            if (currentStage == TrainingStage.Stage8_TacticalFullObs
             || currentStage == TrainingStage.Stage9_DisarmReform) return;
            int poolCap = launchZoneManager.GetPoolCapacity();
            var takenEnemies = new System.Collections.Generic.HashSet<int>();

            // 살아있는 배정 선점
            for (int pi = 0; pi < poolCap; pi++)
            {
                DefensePair pair = launchZoneManager.GetPair(pi);
                if (pair == null || !pair.isActive || pair.isDisarmed || pair.isStandby) continue;
                if (pair.agent1 == null) continue;
                int cur = pair.agent1.assignedTargetIndex;
                if (cur <= 0) continue;
                int ei = cur - 1;
                if (ei < enemyShips.Length && enemyShips[ei] != null
                    && enemyShips[ei].activeInHierarchy && !IsEnemyNeutralized(enemyShips[ei]))
                {
                    takenEnemies.Add(ei); // 살아있음 → 유지
                }
                else
                {
                    // 사망 → 초기화, 클러스터에서 재배정
                    pair.agent1.assignedTargetIndex = -1;
                    if (pair.agent2 != null) pair.agent2.assignedTargetIndex = -1;
                }
            }

            // 미배정 일반 페어: 클러스터 내 생존 적으로 재배정 (agent1/2 동일 타겟)
            for (int pi = 0; pi < poolCap; pi++)
            {
                DefensePair pair = launchZoneManager.GetPair(pi);
                if (pair == null || !pair.isActive || pair.isDisarmed || pair.isStandby) continue;
                if (pair.agent1 == null || pair.agent1.assignedTargetIndex > 0) continue;

                int newEi = FindLiveEnemyInCluster(pair, takenEnemies);
                if (newEi >= 0)
                {
                    pair.agent1.assignedTargetIndex = newEi + 1;
                    if (pair.agent2 != null) pair.agent2.assignedTargetIndex = newEi + 1;
                    takenEnemies.Add(newEi);
                }
            }

            // SingleNet(isDisarmed) 페어: agent1/agent2 각각 개별 타겟 갱신
            for (int pi = 0; pi < poolCap; pi++)
            {
                DefensePair pair = launchZoneManager.GetPair(pi);
                if (pair == null || !pair.isActive || !pair.isDisarmed || pair.isStandby) continue;

                // agent1
                bool agent1HasTarget = false;
                if (pair.agent1 != null)
                {
                    int cur1 = pair.agent1.assignedTargetIndex;
                    bool alive1 = cur1 > 0 && (cur1 - 1) < enemyShips.Length
                        && enemyShips[cur1 - 1] != null && enemyShips[cur1 - 1].activeInHierarchy
                        && !IsEnemyNeutralized(enemyShips[cur1 - 1]);
                    if (alive1) { takenEnemies.Add(cur1 - 1); agent1HasTarget = true; }
                    else
                    {
                        pair.agent1.assignedTargetIndex = -1;
                        int newEi = FindLiveEnemyInCluster(pair, takenEnemies, pair.agent1);
                        if (newEi >= 0) { pair.agent1.assignedTargetIndex = newEi + 1; takenEnemies.Add(newEi); agent1HasTarget = true; }
                    }
                }

                // agent2
                bool agent2HasTarget = false;
                if (pair.agent2 != null)
                {
                    int cur2 = pair.agent2.assignedTargetIndex;
                    bool alive2 = cur2 > 0 && (cur2 - 1) < enemyShips.Length
                        && enemyShips[cur2 - 1] != null && enemyShips[cur2 - 1].activeInHierarchy
                        && !IsEnemyNeutralized(enemyShips[cur2 - 1]);
                    if (alive2) { takenEnemies.Add(cur2 - 1); agent2HasTarget = true; }
                    else
                    {
                        pair.agent2.assignedTargetIndex = -1;
                        int newEi = FindLiveEnemyInCluster(pair, takenEnemies, pair.agent2);
                        if (newEi >= 0) { pair.agent2.assignedTargetIndex = newEi + 1; takenEnemies.Add(newEi); agent2HasTarget = true; }
                    }
                }

                // 양쪽 모두 배정 실패 → 배정 가능한 적이 없음 → 비활성화
                if (!agent1HasTarget && !agent2HasTarget)
                {
                    Debug.Log($"[DefenseEnv] SingleNet Pair {pi} 배정 가능 적 없음 → 비활성화, step={_resetTimer}");
                    launchZoneManager.ReturnPairToPool(pi, m_AgentGroup);
                }
            }
        }

        /// <summary>
        /// 포획 모드 배정:
        ///  1차 필터: 정지 트랩에 이미 차단된 적 제외
        ///  2차 매칭: 에이전트 위치 → 적→모선 LOS 선분 수직 거리 최솟값
        /// callerAgent=null 이면 pair.agent1 위치 사용
        /// </summary>
        private int FindLiveEnemyInCluster(DefensePair pair,
            System.Collections.Generic.HashSet<int> taken,
            DefenseAgent callerAgent = null)
        {
            if (enemyShips == null) return -1;

            Vector3 agentPos = (callerAgent != null ? callerAgent.transform.position
                                                    : pair.agent1.transform.position);
            agentPos.y = 0f;

            Vector3 motherPos3 = motherShip != null ? motherShip.transform.position : Vector3.zero;
            motherPos3.y = 0f;

            // 정지 트랩 BoxCollider 목록 수집
            var frozenCols = GetFrozenTrapColliders();

            // 후보 풀: 클러스터 내 우선, 없으면 전체
            var candidateIndices = new System.Collections.Generic.List<int>();
            if (pair.clusterEnemyIndices != null)
            {
                foreach (int ei in pair.clusterEnemyIndices)
                {
                    if (taken.Contains(ei)) continue;
                    if (ei >= enemyShips.Length || enemyShips[ei] == null) continue;
                    if (!enemyShips[ei].activeInHierarchy || IsEnemyNeutralized(enemyShips[ei])) continue;
                    candidateIndices.Add(ei);
                }
            }
            if (candidateIndices.Count == 0)
            {
                for (int ei = 0; ei < enemyShips.Length; ei++)
                {
                    if (taken.Contains(ei)) continue;
                    if (enemyShips[ei] == null || !enemyShips[ei].activeInHierarchy) continue;
                    if (IsEnemyNeutralized(enemyShips[ei])) continue;
                    candidateIndices.Add(ei);
                }
            }
            if (candidateIndices.Count == 0) return -1;

            // 1차 필터: 정지 트랩에 차단되지 않은 적만 추림
            var unblocked = new System.Collections.Generic.List<int>();
            foreach (int ei in candidateIndices)
            {
                Vector3 ePos = enemyShips[ei].transform.position; ePos.y = 0f;
                if (!IsSegmentBlockedByFrozenTrap(ePos, motherPos3, frozenCols))
                    unblocked.Add(ei);
            }
            // 모두 차단된 경우 필터 무시
            var pool = unblocked.Count > 0 ? unblocked : candidateIndices;

            // 2차 매칭: 에이전트 위치 → 적→모선 LOS 수직 거리 최솟값
            int best = -1;
            float bestPerp = float.MaxValue;
            foreach (int ei in pool)
            {
                Vector3 ePos = enemyShips[ei].transform.position; ePos.y = 0f;
                Vector3 seg  = motherPos3 - ePos;
                float   len  = seg.magnitude;
                if (len < 0.1f) { best = ei; break; }
                Vector3 dir  = seg / len;
                float   t    = Mathf.Clamp(Vector3.Dot(agentPos - ePos, dir), 0f, len);
                float   perp = Vector3.Distance(agentPos, ePos + dir * t);
                if (perp < bestPerp) { bestPerp = perp; best = ei; }
            }
            return best;
        }

        /// <summary>isDisarmed 페어의 frozen web BoxCollider 목록</summary>
        private System.Collections.Generic.List<BoxCollider> GetFrozenTrapColliders()
        {
            var result = new System.Collections.Generic.List<BoxCollider>();
            if (launchZoneManager == null) return result;
            int cap = launchZoneManager.GetPoolCapacity();
            for (int i = 0; i < cap; i++)
            {
                var p = launchZoneManager.GetPair(i);
                if (p == null || !p.isActive || !p.isDisarmed || p.webObject == null) continue;
                var dw = p.webObject.GetComponent<DynamicWeb>();
                if (dw == null || !dw.IsFrozen) continue;
                var col = p.webObject.GetComponent<BoxCollider>();
                if (col != null) result.Add(col);
            }
            return result;
        }

        /// <summary>선분 worldA→worldB 가 frozenColliders 중 하나라도 교차하면 true (슬래브법)</summary>
        private static bool IsSegmentBlockedByFrozenTrap(Vector3 worldA, Vector3 worldB,
            System.Collections.Generic.List<BoxCollider> cols)
        {
            foreach (var col in cols)
            {
                Vector3 lA = col.transform.InverseTransformPoint(worldA) - col.center;
                Vector3 lB = col.transform.InverseTransformPoint(worldB) - col.center;
                Vector3 half = col.size * 0.5f;
                if (SegmentIntersectsAABB(lA, lB, half)) return true;
            }
            return false;
        }

        private static bool SegmentIntersectsAABB(Vector3 a, Vector3 b, Vector3 half)
        {
            Vector3 d = b - a;
            float tMin = 0f, tMax = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float da = axis == 0 ? d.x : axis == 1 ? d.y : d.z;
                float aa = axis == 0 ? a.x : axis == 1 ? a.y : a.z;
                float h  = axis == 0 ? half.x : axis == 1 ? half.y : half.z;
                if (Mathf.Abs(da) < 1e-6f)
                {
                    if (aa < -h || aa > h) return false;
                }
                else
                {
                    float t1 = (-h - aa) / da;
                    float t2 = ( h - aa) / da;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Mathf.Max(tMin, t1);
                    tMax = Mathf.Min(tMax, t2);
                    if (tMin > tMax) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 쌍 비활성화 (정지). Stage9에서는 추가로 트랩 그물 생성.
        /// </summary>
        private void DisableOrDisarmPair(int pairIdx, bool forceDisable = false)
        {
            // Stage9 + 강제비활 아닐 때: 트랩 생성 후 DisarmPair (isActive 유지 → NoPairsLeft 방지)
            if (!forceDisable && currentStage == TrainingStage.Stage9_DisarmReform)
            {
                var pair = launchZoneManager.GetPair(pairIdx);
                if (pair != null && pair.isActive && !pair.isDisarmed)
                {
                    CreateTrapFromWeb(pair);
                    launchZoneManager.DisarmPair(pairIdx, m_AgentGroup, _resetTimer);
                }
                return;
            }

            launchZoneManager.DisablePair(pairIdx, m_AgentGroup);

            // 예비 페어 있으면 즉시 활성화 (같은 클러스터 커버)
            TryActivateStandbyPair(pairIdx);
        }

        private void TryActivateStandbyPair(int lostPairIdx)
        {
            if (launchZoneManager == null) return;
            // Stage9는 예비 활성화 안 함 (트랩 모드)
            if (currentStage == TrainingStage.Stage9_DisarmReform) return;

            int standbyIdx = launchZoneManager.GetFirstStandbyPairIndex();
            if (standbyIdx < 0) return;

            // 잃어버린 페어의 클러스터 정보 가져오기
            EnemyCluster? cluster = null;
            var lostPair = launchZoneManager.GetPair(lostPairIdx);
            if (lostPair != null && launchZoneManager.CurrentClusters != null
                && lostPair.assignedClusterIdx >= 0
                && lostPair.assignedClusterIdx < launchZoneManager.CurrentClusters.Count)
            {
                cluster = launchZoneManager.CurrentClusters[lostPair.assignedClusterIdx];
            }

            bool ok = launchZoneManager.ActivateStandbyPair(standbyIdx, cluster, m_AgentGroup);
            if (ok) _allyPairsCreatedThisEpisode++;
            Debug.LogWarning($"[DefenseEnv] 예비 페어 {standbyIdx} 활성화 → cluster={cluster?.centerAngleDeg:F0}°, ok={ok}");
        }

        /// <summary>
        /// Stage9: 포획 시 현재 Web 위치에 간이 트랩 생성
        /// </summary>
        private void CreateTrapFromWeb(DefensePair pair)
        {
            if (pair == null || pair.webObject == null) return;

            var srcCollider = pair.webObject.GetComponent<BoxCollider>();
            if (srcCollider == null) return;

            // 트랩 GameObject 생성 (환경 계층 내)
            var trap = new GameObject("TrapWeb");
            trap.transform.SetParent(transform);
            trap.transform.position = pair.webObject.transform.position;
            trap.transform.rotation = pair.webObject.transform.rotation;
            trap.transform.localScale = pair.webObject.transform.lossyScale;

            // 콜라이더 복사 (isTrigger)
            var col = trap.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.center = srcCollider.center;
            col.size = srcCollider.size;

            // Rigidbody 필요 (Trigger 작동 조건: 한쪽에 Rigidbody 필요)
            var rb = trap.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            // 디버그 시각화용 Cube (반투명 빨간색)
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "TrapVisual";
            visual.transform.SetParent(trap.transform, false);
            visual.transform.localPosition = col.center;
            visual.transform.localScale = col.size;
            var meshCollider = visual.GetComponent<Collider>();
            if (meshCollider != null) Destroy(meshCollider); // 시각화용이므로 콜라이더 제거
            var mat = visual.GetComponent<Renderer>().material;
            mat.color = new Color(1f, 0f, 0f, 0.3f);

            // 트랩 핸들러
            var handler = trap.AddComponent<TrapWebHandler>();
            handler.envController = this;
            handler.remainingSteps = trapLifetimeSteps;

            _anchoredTraps.Add(trap);

            // 관측용 트랩 정보 등록
            float webSize = col.size.x * trap.transform.localScale.x; // 월드 스케일 기준 그물 폭
            activeTraps.Add(new TrapInfo { position = trap.transform.position, webSize = webSize });

            Debug.Log($"[DefenseEnv] 트랩 그물 생성: pos={trap.transform.position}, size={col.size}, webSize={webSize:F1}, lifetime={trapLifetimeSteps}steps");
        }

        /// <summary>
        /// Stage9: 에피소드 리셋 시 모든 트랩 정리
        /// </summary>
        private void ClearAllTraps()
        {
            for (int i = _anchoredTraps.Count - 1; i >= 0; i--)
            {
                if (_anchoredTraps[i] != null)
                    Destroy(_anchoredTraps[i]);
            }
            _anchoredTraps.Clear();
            activeTraps.Clear();
        }

        private void Stage8DeployForUncoveredEnemies()
        {
            if (!launchZoneManager.HasReservePairs()) return;
            if (_enemyPool == null) return;

            Vector3 motherPos = motherShip.transform.position;
            int poolCount = launchZoneManager.GetCurrentPoolCount();

            // 1. 활성 적의 방위각/거리 수집
            var enemies = new System.Collections.Generic.List<(int idx, float angle, float dist)>();
            for (int e = 0; e < _enemyPool.Length; e++)
            {
                if (_enemyPool[e] == null || !_enemyPool[e].activeSelf) continue;
                if (IsEnemyNeutralized(_enemyPool[e])) continue;

                Vector3 rel = _enemyPool[e].transform.position - motherPos;
                float dist = rel.magnitude;
                if (dist < stage8MinDeployDistance) continue;

                float angle = Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg;
                enemies.Add((e, angle, dist));
            }
            if (enemies.Count == 0) return;

            // 2. 방위각 순 정렬
            enemies.Sort((a, b) => a.angle.CompareTo(b.angle));

            // 3. 레인 폭: Inspector에서 설정한 고정값 사용
            float laneWidth = laneClusterWidth;

            // 4. Greedy 클러스터링: laneWidth 내의 적들을 한 레인으로
            var lanes = new System.Collections.Generic.List<(float centerAngle, int enemyCount)>();
            int ci = 0;
            while (ci < enemies.Count)
            {
                float startAngle = enemies[ci].angle;
                float sumAngle = enemies[ci].angle;
                int count = 1;
                int cj = ci + 1;
                while (cj < enemies.Count && Mathf.Abs(Mathf.DeltaAngle(startAngle, enemies[cj].angle)) < laneWidth)
                {
                    sumAngle += enemies[cj].angle;
                    count++;
                    cj++;
                }
                float centerAngle = sumAngle / count;
                lanes.Add((centerAngle, count));
                ci = cj;
            }

            // 5. 미커버 레인에 페어 1개씩 배치
            int totalDeployedThisCall = 0;
            foreach (var lane in lanes)
            {
                if (!launchZoneManager.HasReservePairs()) break;
                if (stage8MaxDeployPerCall > 0 && totalDeployedThisCall >= stage8MaxDeployPerCall) break;

                // 이미 커버된 레인 스킵
                if (IsLaneCovered(lane.centerAngle, laneWidth, motherPos, poolCount)) continue;

                bool deployed = TryDeployPair(motherPos, lane.centerAngle, m_AgentGroup);
                if (deployed)
                {
                    totalDeployedThisCall++;
                    Debug.Log($"[DefenseEnv] Stage8 레인 출동: 방향 {lane.centerAngle:F0}°, " +
                        $"적수={lane.enemyCount}, laneWidth={laneWidth:F0}°");
                }
            }

            // 후발대 Web에도 경량 모드 적용
            if (lightweightMode) ApplyLightweightMode();
        }

        /// <summary>
        /// 해당 방향 ±laneWidth/2 범위에 이미 활성 페어가 있는지 확인
        /// </summary>
        private bool IsLaneCovered(float centerAngle, float laneWidth, Vector3 motherPos, int poolCount)
        {
            float halfLane = laneWidth * 0.5f;
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive || pair.isDisarmed || pair.agent1 == null) continue;

                Vector3 allyCenter = pair.agent2 != null
                    ? (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f
                    : pair.agent1.transform.position;
                Vector3 allyRel = allyCenter - motherPos;
                float allyAngle = Mathf.Atan2(allyRel.x, allyRel.z) * Mathf.Rad2Deg;

                if (Mathf.Abs(Mathf.DeltaAngle(centerAngle, allyAngle)) <= halfLane)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 가장 최근 배치된 활성 쌍의 인덱스 반환
        /// </summary>
        private int FindLastDeployedPairIndex()
        {
            if (launchZoneManager == null) return -1;
            int poolCount = launchZoneManager.GetCurrentPoolCount();
            int lastIdx = -1;
            int maxStep = -1;
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair != null && pair.isActive && pair.deployStep > maxStep)
                {
                    maxStep = pair.deployStep;
                    lastIdx = i;
                }
            }
            return lastIdx;
        }

        /// <summary>
        /// 에피소드 종료 조건 확인
        /// - 모든 적 무력화 → 성공 종료 (+보상)
        /// - 모든 아군 쌍 무력화 → 실패 종료 (-패널티)
        /// </summary>
        private void CheckEpisodeEndCondition()
        {
            if (_episodeEnding) return;

            // 활성 적군 수 확인 (무력화 HashSet 기준)
            int activeEnemies = 0;
            int poolActiveCount = 0;  // activeSelf=true인 풀 엔트리 수
            if (_enemyPool != null)
            {
                for (int i = 0; i < _enemyPool.Length; i++)
                {
                    if (_enemyPool[i] == null) continue;
                    bool isActive = _enemyPool[i].activeSelf;
                    bool isNeutralized = _neutralizedEnemies.Contains(_enemyPool[i]);
                    if (isActive) poolActiveCount++;
                    if (isActive && !isNeutralized)
                        activeEnemies++;
                }
            }

            // 진단 로그: 100스텝마다만 출력 (콘솔 과부하 방지)
            #if UNITY_EDITOR
            if (activeEnemies > 0 && _neutralizedEnemies.Count > 0 && _resetTimer % 100 == 0)
            {
                Debug.Log($"[DefenseEnv] CheckEnd: active={activeEnemies}, neutralized={_neutralizedEnemies.Count}, step={_resetTimer}");
            }
            #endif

            // 활성 쌍 수 확인
            int activePairs = 0;
            if (launchZoneManager != null)
                activePairs = launchZoneManager.GetActivePairCount();
            else if (defenseAgent1 != null && defenseAgent2 != null)
                activePairs = 1; // 레거시: 단일 쌍

            int totalEnemiesThisStage = GetActiveEnemyCountForStage();

            // 모든 적 무력화 → 성공 종료
            if (activeEnemies == 0 && totalEnemiesThisStage > 0)
            {
                float totalReward = rewardCalculator.allClearBonus;
                Debug.LogWarning($"[DefenseEnv] ★ ALL ENEMIES NEUTRALIZED ★ step={_resetTimer}, totalReward={totalReward:F2}");
                RestartEpisode("AllEnemiesNeutralized", totalReward);
                return;
            }

            // 아군 쌍이 전부 비활성화 → 실패 종료 (고정 -5 + 남은 적 수 비례 페널티)
            // Stage9/Stage10: 트랩 포획 모드 — NoPairsLeft 비활성화 (AllClear/MaxStep으로만 종료)
            bool skipNoPairs = disableNoPairsEndEpisode
                               || currentStage == TrainingStage.Stage9_DisarmReform
                               || currentStage == TrainingStage.Stage10_FlankCapture;
            if (!skipNoPairs && activePairs == 0 && activeEnemies > 0)
            {
                float totalPenalty = rewardCalculator.noPairsLeftPenalty
                                   + activeEnemies * rewardCalculator.remainingEnemyPenalty;
                Debug.LogWarning($"[DefenseEnv] ✖ NO PAIRS LEFT ✖ step={_resetTimer}, " +
                    $"activeEnemies={activeEnemies}, totalPenalty={totalPenalty:F2}");
                RestartEpisode("NoPairsLeft", totalPenalty);
                return;
            }
        }

        /// <summary>
        /// 아군 선박이 모선과 충돌: 페널티 + 쌍 비활성화
        /// </summary>
        public void OnAllyHitMotherShip(DefenseAgent collidedAgent)
        {
            if (_episodeEnding || collidedAgent == null) return;
            if (_resetTimer <= 10) return;
            if (launchZoneManager == null) return;

            int pairIdx = launchZoneManager.FindPairIndex(collidedAgent);
            if (pairIdx < 0) return;

            DefensePair pair = launchZoneManager.GetPair(pairIdx);
            if (pair == null) return;
            if (pair.deployStep >= 0 && (_resetTimer - pair.deployStep) < 50) return; // 배치 유예기간
            if (pair.isDisarmed) return; // SingleNet 포획 모드 중 모선 충돌은 무시

            float penalty = rewardCalculator.collisionPenalty;
            if (pair.agent1 != null) pair.agent1.AddReward(penalty);
            if (pair.agent2 != null) pair.agent2.AddReward(penalty);

            Debug.LogWarning($"[DefenseEnv] AllyHitMotherShip → Pair {pairIdx} 비활성화 (penalty={penalty:F2}), step={_resetTimer}");
            DisableOrDisarmPair(pairIdx);
            CheckEpisodeEndCondition();
        }

        /// <summary>
        /// 파트너 아군 충돌 처리: 비활성화 없이 페널티만 부여
        /// </summary>
        public void OnPartnerCollision(DefenseAgent collidedAgent)
        {
            if (_episodeEnding || collidedAgent == null) return;
            if (_resetTimer <= 10) return;

            float penalty = rewardCalculator.collisionPenalty * 0.3f; // 약한 페널티

            if (launchZoneManager != null)
            {
                int pairIdx = launchZoneManager.FindPairIndex(collidedAgent);
                if (pairIdx >= 0)
                {
                    DefensePair pair = launchZoneManager.GetPair(pairIdx);
                    if (pair != null && pair.deployStep >= 0 && (_resetTimer - pair.deployStep) < 50)
                        return; // 배치 유예기간
                    if (pair?.agent1 != null) pair.agent1.AddReward(penalty);
                    if (pair?.agent2 != null) pair.agent2.AddReward(penalty);
                    return;
                }
            }

            // 레거시 fallback
            if (defenseAgent1 != null) defenseAgent1.AddReward(penalty);
            if (defenseAgent2 != null) defenseAgent2.AddReward(penalty);
        }

        /// <summary>
        /// 아군 충돌 처리 (다른 쌍 또는 아군-모선 충돌)
        /// 해당 쌍만 무력화 (에피소드 유지)
        /// </summary>
        public void OnFriendlyCollision(DefenseAgent collidedAgent = null)
        {
            if (_episodeEnding) return;
            if (_resetTimer <= 10) return;

            float penalty = rewardCalculator.collisionPenalty;

            // LaunchZoneManager가 있으면 해당 쌍만 비활성화
            if (launchZoneManager != null && collidedAgent != null)
            {
                int pairIdx = launchZoneManager.FindPairIndex(collidedAgent);
                if (pairIdx >= 0)
                {
                    // 배치 후 유예기간 (50스텝) 동안 충돌 무시
                    DefensePair pair = launchZoneManager.GetPair(pairIdx);
                    if (pair != null && pair.deployStep >= 0 && (_resetTimer - pair.deployStep) < 50)
                        return;

                    // 해당 쌍에만 페널티
                    if (pair?.agent1 != null) pair.agent1.AddReward(penalty);
                    if (pair?.agent2 != null) pair.agent2.AddReward(penalty);

                    // isDisarmed(플랭크/EXIT 쌍)는 충돌 페널티만, 비활성화 없음
                    if (pair != null && pair.isDisarmed) return;

                    DisableOrDisarmPair(pairIdx);
                    NotifyCameraPairDisabled(pair);
                    Debug.LogWarning($"[DefenseEnv] FriendlyCollision → Pair {pairIdx} 무력화, step={_resetTimer}");

                    CheckEpisodeEndCondition();
                    return;
                }
            }

            // LaunchZoneManager 없거나 쌍 못찾으면 레거시: 전체 페널티 (에피소드 유지)
            if (m_AgentGroup != null)
                m_AgentGroup.AddGroupReward(penalty);
            else
            {
                if (defenseAgent1 != null) defenseAgent1.AddReward(penalty);
                if (defenseAgent2 != null) defenseAgent2.AddReward(penalty);
            }
            Debug.LogWarning($"[DefenseEnv] FriendlyCollision (레거시) → 페널티만 부여, 에피소드 유지, step={_resetTimer}");
        }
        
        #region DefenseBoatManager 통합 기능
        
        /// <summary>
        /// 적군 선박 파괴 요청 (DynamicWeb, WebCollisionDetector에서 호출)
        /// 풀 기반: 다음 프레임에 SetActive(false) + enemyShips 갱신
        /// </summary>
        public void RequestAttackBoatDestruction(GameObject attackBoat)
        {
            if (attackBoat == null || !attackBoat.activeSelf)
                return;

            // 다음 프레임에 비활성화 처리 (물리 콜백 제약 회피)
            StartCoroutine(DeactivateAttackBoatNextFrame(attackBoat));
        }

        private System.Collections.IEnumerator DeactivateAttackBoatNextFrame(GameObject attackBoat)
        {
            yield return null;

            if (attackBoat == null)
                yield break;

            // 스폰 직후 유예기간에는 파괴 이벤트 무시
            if (_resetTimer <= 10)
            {
                Debug.Log($"[DefenseEnv] DeactivateAttackBoat 유예기간 무시: {attackBoat.name}, step={_resetTimer}");
                yield break;
            }

            Debug.Log($"[DefenseEnv] DeactivateAttackBoat: {attackBoat.name}, step={_resetTimer}");

            // 현재 위치에서 무력화 (이동하지 않음)
            DisableEnemy(attackBoat);

            // 종료 조건 확인
            if (endEpisodeOnAllEnemiesDestroyed && !_episodeEnding)
            {
                CheckEpisodeEndCondition();
            }
        }
        
        /// <summary>
        /// 적군 오브젝트 풀 초기화 (Start()에서 1회 호출)
        /// 씬에 배치된 첫 attack_boat를 템플릿으로 사용, poolSize만큼 복제
        /// </summary>
        private void InitializeEnemyPool()
        {
            // 배열 할당
            _enemyPool = new GameObject[poolSize];
            _poolAttackAgents = new AttackAgent[poolSize];
            _poolRigidbodies = new Rigidbody[poolSize];
            _poolEngines = new Engine[poolSize];
            _poolDisablers = new AttackBoatDisabler[poolSize];
            _poolExplosions = new SimpleExplosionOnCollision[poolSize];
            _poolDollyCarts = new Cinemachine.CinemachineDollyCart[poolSize];
            _poolNoiseSeed = new float[poolSize];

            // Raycast 시각화용 LineRenderer 배열 + 차단 상태 배열
            _enemyRayLines   = new LineRenderer[poolSize];
            _enemyRayBlocked = new bool[poolSize];

            // 씬에서 템플릿 찾기 (3단계 fallback)
            // 1순위: 환경 루트 하위 태그 검색
            // 2순위: Inspector enemyShips 배열
            // 3순위: 글로벌 태그 검색 (환경 루트 무시)
            GameObject template = null;
            string templateSource = "";

            // 1순위: 환경 루트 하위에서 attack_boat 태그 검색
            GameObject[] foundBoats = FindGameObjectsWithTagInEnvironment("attack_boat");
            if (foundBoats.Length > 0)
            {
                template = foundBoats[0];
                templateSource = $"환경 내 태그 검색 ({foundBoats.Length}개 발견)";
            }

            // 2순위: Inspector의 enemyShips 배열
            if (template == null && enemyShips != null)
            {
                foreach (var ship in enemyShips)
                {
                    if (ship != null)
                    {
                        template = ship;
                        templateSource = "Inspector enemyShips 배열";
                        break;
                    }
                }
            }

            // 3순위: 글로벌 태그 검색 (환경 루트 제한 없이)
            if (template == null)
            {
                GameObject[] globalBoats = GameObject.FindGameObjectsWithTag("attack_boat");
                if (globalBoats.Length > 0)
                {
                    template = globalBoats[0];
                    templateSource = $"글로벌 태그 검색 ({globalBoats.Length}개 발견)";
                }
            }

            if (template == null)
            {
                Debug.LogError("[DefenseEnv] InitializeEnemyPool: attack_boat 템플릿을 찾을 수 없습니다! " +
                    "씬에 attack_boat 태그 오브젝트가 있거나, Inspector의 enemyShips에 참조를 넣어주세요.");
                return;
            }

            Debug.Log($"[DefenseEnv] InitializeEnemyPool: 템플릿 발견 - {template.name} (소스: {templateSource})");
            _poolTemplateY = template.transform.position.y;

            // 템플릿은 풀에 넣지 않고 비활성화 → 클론만 사용
            Transform poolParent = template.transform.parent != null ? template.transform.parent : GetEnvironmentRoot();
            for (int i = 0; i < poolSize; i++)
            {
                GameObject clone = Instantiate(template, poolParent);
                clone.name = $"attack_boat_pool_{i}";
                clone.tag = "attack_boat";
                clone.SetActive(false);
                _enemyPool[i] = clone;
                CachePoolComponents(i);

                // Raycast 시각화용 LineRenderer (적→모선)
                var rayLineObj = new GameObject($"RayLine_{i}");
                rayLineObj.transform.SetParent(clone.transform);
                var lr = rayLineObj.AddComponent<LineRenderer>();
                lr.positionCount = 2;
                lr.startWidth = 0.5f;
                lr.endWidth = 0.5f;
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = new Color(1f, 0f, 0f, 0.5f);
                lr.endColor = new Color(1f, 0f, 0f, 0.1f);
                lr.enabled = false;
                _enemyRayLines[i] = lr;
            }
            template.SetActive(false);
            template.name = $"{template.name}_template(unused)";

            // 모든 풀 객체 비활성화 (ResetScene에서 활성화)
            for (int i = 0; i < poolSize; i++)
            {
                // SimpleExplosionOnCollision 비활성화 (학습 시 폭발 이펙트 불필요)
                if (_poolExplosions[i] != null)
                {
                    _poolExplosions[i].destroyAfterExplosion = false;
                    _poolExplosions[i].enabled = false;
                }

                _enemyPool[i].SetActive(false);
                _poolNoiseSeed[i] = Random.Range(0f, 1000f);
            }

            // 풀 객체 간 충돌 무시
            IgnoreCollisionBetweenEnemies();

            // enemyShips 초기화
            UpdateEnemyShipsArray();

            Debug.Log($"[DefenseEnv] InitializeEnemyPool: poolSize={poolSize}, template={template.name}, foundInScene={foundBoats.Length}, stage={currentStage}, stageCount={GetActiveEnemyCountForStage()}");
        }

        /// <summary>
        /// 풀 인덱스의 컴포넌트를 캐시
        /// </summary>
        private void CachePoolComponents(int index)
        {
            GameObject obj = _enemyPool[index];
            _poolAttackAgents[index] = obj.GetComponent<AttackAgent>();
            _poolRigidbodies[index] = obj.GetComponent<Rigidbody>();
            _poolDisablers[index] = obj.GetComponent<AttackBoatDisabler>();
            _poolExplosions[index] = obj.GetComponent<SimpleExplosionOnCollision>();
            _poolDollyCarts[index] = obj.GetComponent<Cinemachine.CinemachineDollyCart>();

            var boat = obj.GetComponent<Boat>();
            _poolEngines[index] = (boat != null) ? boat.engine : null;
        }

        /// <summary>
        /// 풀 오브젝트를 지정 위치/회전으로 리셋 및 활성화
        /// </summary>
        private void ResetPoolObject(int index, Vector3 position, Quaternion rotation)
        {
            GameObject obj = _enemyPool[index];
            if (obj == null) return;

            // 1. 이전 에피소드 잔여 Invoke 취소
            var behaviours = obj.GetComponents<MonoBehaviour>();
            foreach (var mb in behaviours)
            {
                if (mb != null) mb.CancelInvoke();
            }

            // 2. 위치/회전 설정 (SetActive 전에!)
            obj.transform.position = position;
            obj.transform.rotation = rotation;

            // 3. Rigidbody 속도 초기화 + isKinematic 복원 (DisableEnemy에서 true로 설정됨)
            Rigidbody rb = _poolRigidbodies[index];
            if (rb != null)
            {
                rb.isKinematic = false;  // 물리 시뮬레이션 복원
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // 4. 활성화 → OnEnable 트리거 → _hasExploded=false 자동 리셋
            obj.SetActive(true);

            // 5. rb.Sleep() (활성 상태에서만 유효)
            if (rb != null)
            {
                rb.Sleep();
            }

            // 6. DollyCart 비활성화 (동적 스폰에서는 경로 추적 안 함)
            if (_poolDollyCarts[index] != null)
            {
                _poolDollyCarts[index].enabled = false;
            }

            // 7. AttackAgent 설정
            AttackAgent attackAgent = _poolAttackAgents[index];
            if (attackAgent != null)
            {
                attackAgent.followWaypoints = false;
                attackAgent.enableRush = false;  // DriveEnemiesForward(currentEnemyRushThrottle)에 위임
                attackAgent.targetMotherShip = motherShip;
            }

            // 8. Engine 리셋 (Gerstner 파도 안정화)
            if (_poolEngines[index] != null)
            {
                _poolEngines[index].OnEpisodeReset();
            }

            // 9. 노이즈 시드 갱신
            _poolNoiseSeed[index] = Random.Range(0f, 1000f);
        }
        
        /// <summary>
        /// 위치만 리셋 (외부에서 호출 가능, ML-Agents 에피소드 재시작 시 사용)
        /// 모든 선박을 비활성화 → 위치 리셋 → 활성화
        /// </summary>
        public void ResetPositionsOnly()
        {
            // 코루틴이 이미 실행 중이면 중복 호출 방지
            if (_isResettingPositions)
            {
                return;
            }
            
            // 중복 호출 방지 (같은 프레임에서 여러 번 호출되는 것 방지)
            if (_lastResetFrame == Time.frameCount)
            {
                return;
            }
            
            _lastResetFrame = Time.frameCount;
            
            // 코루틴 실행 중 플래그 설정 (코루틴 시작 전에 설정하여 중복 실행 방지)
            _isResettingPositions = true;
            
            // _originalBoatPositions 업데이트 (새로 생성된 boat가 있을 수 있음)
            UpdateOriginalBoatPositions();
            
            // 코루틴으로 비활성화 → 리셋 → 활성화 순서로 진행
            StartCoroutine(ResetPositionsWithDeactivation());
        }
        
        /// <summary>
        /// _originalBoatPositions 딕셔너리 업데이트 (새로 생성된 boat 추가)
        /// </summary>
        private void UpdateOriginalBoatPositions()
        {
            GameObject[] allBoats = FindGameObjectsWithTagInEnvironment("boat");
            
            int addedCount = 0;
            foreach (var boat in allBoats)
            {
                // WAKE 객체는 제외 (파도 효과 등)
                if (boat != null && !boat.name.Contains("WAKE") && !boat.name.Contains("Wake") && !_originalBoatPositions.ContainsKey(boat))
                {
                    _originalBoatPositions[boat] = boat.transform.position;
                    _originalBoatRotations[boat] = boat.transform.rotation;
                    addedCount++;
                }
            }
            
            // null이거나 WAKE인 항목 제거
            var keysToRemove = new System.Collections.Generic.List<GameObject>();
            foreach (var boat in _originalBoatPositions.Keys)
            {
                if (boat == null || boat.name.Contains("WAKE") || boat.name.Contains("Wake"))
                {
                    keysToRemove.Add(boat);
                }
            }
            foreach (var boat in keysToRemove)
            {
                _originalBoatPositions.Remove(boat);
                _originalBoatRotations.Remove(boat);
            }
        }
        
        /// <summary>
        /// 위치 리셋 코루틴 - 모든 선박 비활성화 없이 위치만 리셋 (Water System 호환)
        /// </summary>
        private System.Collections.IEnumerator ResetPositionsWithDeactivation()
        {
            // Debug.LogWarning($"[DefenseEnv] ResetPositionsWithDeactivation 시작: " +
            //     $"useDynamicSpawn={useDynamicSpawn}, motherShip={motherShip != null}, " +
            //     $"launchZoneManager={launchZoneManager != null}, " +
            //     $"lzmInitialized={launchZoneManager?.IsInitialized}");

            // chase 모드: Phase1 없이 Phase2 직접 스폰 (wake 객체 없으므로 DestroyAllWakeObjects 생략)
            if (chaseTrainingMode && motherShip != null && launchZoneManager != null)
            {
                Vector3 motherPos = motherShip.transform.position;
                yield return StartCoroutine(SpawnChaseTrainingLayout(motherPos, m_AgentGroup));
                _isResettingPositions = false;
                yield break;
            }

            // 모든 WAKE 객체 제거 및 WakeGenerator 비활성화
            DestroyAllWakeObjects();

            if (useDynamicSpawn && motherShip != null)
            {
                // ========================================
                // 동적 스폰: 모선 기준 적군/아군 배치
                // ========================================
                Vector3 motherPos = motherShip.transform.position;

                // 1. 적군 접근 각도 (formationSpawner가 있으면 내부에서 결정됨)
                if (formationSpawner == null)
                {
                    _currentEnemyApproachAngle = Random.Range(0f, 360f);
                }
                float angleRad = _currentEnemyApproachAngle * Mathf.Deg2Rad;
                Vector3 enemyDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));

                // 2. 적군 스폰 (formationSpawner가 있으면 포메이션별 패턴 적용)
                ResetAttackBoatsDynamic(motherPos, enemyDir);

                // 3. formationSpawner 사용 시 접근 각도가 업데이트되었으므로 enemyDir 재계산
                if (formationSpawner != null)
                {
                    angleRad = _currentEnemyApproachAngle * Mathf.Deg2Rad;
                    enemyDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                }

                // 4. 아군 스폰
                if (launchZoneManager != null)
                {
                    int totalPairBudget = launchZoneManager.IsInitialized
                        ? Mathf.Min(launchZoneManager.activePairCount, launchZoneManager.maxPairCount)
                        : 0;

                    // initialDeployCount > 0이면 초기 출동만, 나머지 예비 대기
                    int initialDeploy = launchZoneManager.initialDeployCount;
                    int pairCount = (initialDeploy > 0 && initialDeploy < totalPairBudget)
                        ? initialDeploy
                        : totalPairBudget;

                    Debug.Log($"[DefenseEnv] Stage={currentStage}, activePairCount={launchZoneManager.activePairCount}, " +
                        $"initialDeploy={initialDeploy}, pairCount={pairCount}, totalBudget={totalPairBudget}, " +
                        $"lzmInit={launchZoneManager.IsInitialized}, enemyCount={GetActiveEnemyCountForStage()}");

                    if (pairCount <= 0)
                    {
                        // activePairCount=0: 모든 쌍 비활성화 + defenseAgent1/2 숨김 (버튼으로만 배치)
                        if (launchZoneManager.IsInitialized)
                            launchZoneManager.DeactivateAllPairs(m_AgentGroup);
                        HideDefenseAgentsAtHiddenPos();
                    }
                    else
                    {
                        // activePairCount>0: 1.5초 간격으로 1대씩 순차 배치
                        float[] divAngles = (formationSpawner != null)
                            ? formationSpawner.GetLastDiversionaryAngles()
                            : null;

                        StartCoroutine(DeployPairsSequentially(
                            _currentFormation, _currentEnemyApproachAngle,
                            divAngles, pairCount, motherPos, m_AgentGroup));

                        // 경량 모드 적용 (배치 후 모든 Web에 적용)
                        ApplyLightweightMode();

                        // 위치 교차 판별
                        if (defenseAgent1 != null && defenseAgent2 != null)
                        {
                            Vector3 spawnPos1 = defenseAgent1.transform.position;
                            Vector3 spawnPos2 = defenseAgent2.transform.position;
                            _currentPerpendicularDir = RotateXZ(enemyDir, Mathf.PI / 2f);
                            float perpDot1 = Vector3.Dot(spawnPos1 - motherPos, _currentPerpendicularDir);
                            float perpDot2 = Vector3.Dot(spawnPos2 - motherPos, _currentPerpendicularDir);
                            _agent1StartsOnLeft = perpDot1 < perpDot2;
                        }

                        // Stage6: 에피소드마다 phantom 리셋 (개별 속도는 InitializePhantoms에서 랜덤 설정)
                        if (currentStage == TrainingStage.Stage6_PhantomFormation && launchZoneManager != null)
                        {
                            int poolCap = launchZoneManager.GetPoolCapacity();
                            for (int pi = 0; pi < poolCap; pi++)
                            {
                                DefensePair pair = launchZoneManager.GetPair(pi);
                                if (pair == null) continue;
                                if (pair.agent1 != null) pair.agent1.phantomsInitialized = false;
                                if (pair.agent2 != null) pair.agent2.phantomsInitialized = false;
                            }
                        }

                        // Stage7: 타겟 배정 없음 — 에이전트가 스스로 판단

                        // Stage8/9: 스폰 위치/수 결정에만 배정 사용 → 즉시 해제
                        if ((currentStage == TrainingStage.Stage8_TacticalFullObs || currentStage == TrainingStage.Stage9_DisarmReform) && launchZoneManager != null)
                        {
                            int poolCap = launchZoneManager.GetPoolCapacity();
                            for (int ci = 0; ci < poolCap; ci++)
                            {
                                DefensePair cp = launchZoneManager.GetPair(ci);
                                if (cp == null) continue;
                                if (cp.agent1 != null) cp.agent1.assignedTargetIndex = -1;
                                if (cp.agent2 != null) cp.agent2.assignedTargetIndex = -1;
                            }
                        }
                    }
                }
                else
                {
                    // 레거시: LaunchZoneManager 없을 때 기존 1쌍 직접 배치
                    Vector3 defenseDir = enemyDir;
                    float spreadRad1 = defenseSpawnSpread * Mathf.Deg2Rad;
                    float spreadRad2 = -defenseSpawnSpread * Mathf.Deg2Rad;

                    Vector3 dir1 = RotateXZ(defenseDir, spreadRad1);
                    Vector3 spawnPos1 = motherPos + dir1 * defenseSpawnDistance;
                    spawnPos1.y = _originalDefense1Pos.y;
                    Quaternion rot1 = Quaternion.LookRotation(defenseDir, Vector3.up);

                    Vector3 dir2 = RotateXZ(defenseDir, spreadRad2);
                    Vector3 spawnPos2 = motherPos + dir2 * defenseSpawnDistance;
                    spawnPos2.y = _originalDefense2Pos.y;
                    Quaternion rot2 = rot1;

                    if (enableRandomSpawn)
                    {
                        float jitter1 = Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange);
                        float jitter2 = Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange);
                        rot1 *= Quaternion.Euler(0f, jitter1, 0f);
                        rot2 *= Quaternion.Euler(0f, jitter2, 0f);
                    }

                    ResetDefenseAgentDirect(defenseAgent1, spawnPos1, rot1);
                    ResetDefenseAgentDirect(defenseAgent2, spawnPos2, rot2);

                    _currentPerpendicularDir = RotateXZ(defenseDir, Mathf.PI / 2f);
                    float perpDot1 = Vector3.Dot(spawnPos1 - motherPos, _currentPerpendicularDir);
                    float perpDot2 = Vector3.Dot(spawnPos2 - motherPos, _currentPerpendicularDir);
                    _agent1StartsOnLeft = perpDot1 < perpDot2;
                }
            }
            else
            {
                // ========================================
                // 레거시 스폰: 기존 방식 (고정 위치 + 오프셋)
                // ========================================
                float sharedRandomAngle = enableRandomSpawn
                    ? Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange)
                    : 0f;
                Vector3 sharedOffset = GetSharedRandomOffset();

                ResetDefenseAgentPosition(defenseAgent1, _originalDefense1Pos, _originalDefense1Rot, sharedRandomAngle, sharedOffset);
                ResetDefenseAgentPosition(defenseAgent2, _originalDefense2Pos, _originalDefense2Rot, sharedRandomAngle, sharedOffset);

                ResetAttackBoatsToOrigin();
            }

            // 아군 선박 좌/우 위치 기록 (위치 교차 감지용)
            // 동적 스폰은 위에서 수직축 기반으로 이미 설정됨, 레거시만 전역 X 기준
            if (!useDynamicSpawn && defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 pos1 = defenseAgent1.transform.position;
                Vector3 pos2 = defenseAgent2.transform.position;
                _agent1StartsOnLeft = pos1.x < pos2.x;
            }

            // WebDetector 리셋
            if (_webDetector != null)
            {
                _webDetector.ResetDetector();
            }

            yield return null;

            // 코루틴 실행 완료 플래그 해제 및 에피소드 활성화
            _isResettingPositions = false;
            _episodeActive = true;

            // 경로 기록 시작
            if (trajectoryLogger != null)
                trajectoryLogger.BeginEpisode(_episodeNumber, _currentFormation.ToString());

            // // 배치 결과 확인 로그 (비활성화)
            // if (launchZoneManager != null)
            // {
            //     int activePairs = launchZoneManager.GetActivePairCount();
            //     var agents = launchZoneManager.GetActiveAgents();
            //     Debug.LogWarning($"[DefenseEnv] 코루틴 완료: activePairs={activePairs}");
            // }
        }

        /// <summary>
        /// 아군 선박 위치만 리셋 (비활성화 없이)
        /// originalPos = Start() 시점의 실제 에이전트 위치 (멀티 환경 호환)
        /// sharedPositionOffset = 두 에이전트가 공유하는 위치 오프셋
        /// </summary>
        private void ResetDefenseAgentPosition(DefenseAgent agent, Vector3 originalPos, Quaternion originalRot, float sharedRandomAngle, Vector3 sharedPositionOffset)
        {
            if (agent == null)
                return;

            // 공유 오프셋 적용 (두 에이전트가 동일한 오프셋 사용)
            Vector3 targetPos = originalPos + sharedPositionOffset;

            // 랜덤 스폰: 각도만 적용 (위치는 이미 공유 오프셋으로 적용됨)
            Quaternion targetRot = originalRot;
            if (enableRandomSpawn)
            {
                targetRot = originalRot * Quaternion.Euler(0f, sharedRandomAngle, 0f);
            }

            // Rigidbody 속도 초기화 + Sleep
            if (agent.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.Sleep();  // 물리 시뮬레이션 일시 정지 (누적된 힘 제거)
            }

            // 위치와 회전 설정
            agent.transform.position = targetPos;
            agent.transform.rotation = targetRot;

            // Engine 리셋 (Gerstner 파도 안정화 전까지 _yHeight 조건 무시)
            if (agent._engine != null)
            {
                agent._engine.OnEpisodeReset();
            }
        }

        /// <summary>
        /// XZ 평면에서 방향 벡터를 라디안만큼 회전
        /// </summary>
        private Vector3 RotateXZ(Vector3 dir, float radians)
        {
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector3(
                dir.x * cos - dir.z * sin,
                dir.y,
                dir.x * sin + dir.z * cos
            );
        }

        /// <summary>
        /// 점 p에서 선분 a-b까지의 XZ 평면 최소 거리
        /// </summary>
        private static float PointToSegmentDistanceXZ(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a; ab.y = 0f;
            Vector3 ap = p - a; ap.y = 0f;
            float sqrLen = ab.sqrMagnitude;
            if (sqrLen < 0.001f) return ap.magnitude;
            float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / sqrLen);
            Vector3 closest = new Vector3(a.x + ab.x * t, 0f, a.z + ab.z * t);
            return new Vector3(p.x - closest.x, 0f, p.z - closest.z).magnitude;
        }

        /// <summary>
        /// defenseAgent1/2를 수면 아래 HIDDEN_POS로 이동 (activePairCount=0일 때)
        /// </summary>
        private void HideDefenseAgentsAtHiddenPos()
        {
            Vector3 hiddenPos = new Vector3(0f, -500f, 0f);
            HideAgentAtHiddenPos(defenseAgent1, hiddenPos);
            HideAgentAtHiddenPos(defenseAgent2, hiddenPos + Vector3.right * 50f);
            if (webObject != null) webObject.SetActive(false);
        }

        private void HideAgentAtHiddenPos(DefenseAgent agent, Vector3 pos)
        {
            if (agent == null) return;
            agent.transform.position = pos;
            if (agent.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }

        /// <summary>
        /// 아군 에이전트를 지정 위치/각도로 직접 배치 (동적 스폰용)
        /// </summary>
        private void ResetDefenseAgentDirect(DefenseAgent agent, Vector3 position, Quaternion rotation)
        {
            if (agent == null) return;

            if (agent.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.isKinematic = false; // HideAgentAtHiddenPos에서 true로 설정된 것을 복원
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = position;
                rb.rotation = rotation;
                rb.WakeUp(); // 물리 시뮬레이션 재활성화
            }

            agent.transform.position = position;
            agent.transform.rotation = rotation;

            if (agent._engine != null)
            {
                agent._engine.OnEpisodeReset();
            }
        }

        /// <summary>
        /// Phase2 chase 학습 전용: Phase1 완료 직후 형상을 직접 랜덤 스폰.
        /// 적군 2대(webCenter 기준 모선 방향, 좌우 분산)와 아군 쌍 1개(webCenter 좌우)를 배치.
        /// </summary>
        private System.Collections.IEnumerator SpawnChaseTrainingLayout(
            Vector3 motherPos, SimpleMultiAgentGroup agentGroup)
        {
            // 0. 이전 에피소드 활성 쌍 강제 비활성화
            launchZoneManager.DeactivateAllActivePairs(agentGroup);

            // 1. 적군 접근 각도 랜덤
            float theta = Random.Range(0f, 360f);
            _currentEnemyApproachAngle = theta;
            float rad = theta * Mathf.Deg2Rad;
            Vector3 approachDir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 perpDir     = new Vector3(approachDir.z, 0f, -approachDir.x);

            // 2. webCenter 위치
            float webDist   = Random.Range(chaseWebCenterMinDist, chaseWebCenterMaxDist);
            Vector3 webCenter = motherPos + approachDir * webDist;

            // 3. 아군 간격
            float sep = Random.Range(chaseShipSepMin, chaseShipSepMax);

            // 4. 적군 4대 스폰 — 각 독립 전방거리 + 독립 좌우 위치
            const int CHASE_ENEMY_COUNT = 4;
            float lateralMax = sep * 2.0f;  // 아군 간격의 2배까지 (내+외곽 폭넓게)
            float[] eLats = new float[CHASE_ENEMY_COUNT];
            Vector3[] ePos = new Vector3[CHASE_ENEMY_COUNT];

            for (int i = 0; i < CHASE_ENEMY_COUNT; i++)
            {
                float eDist = Random.Range(chaseEnemyMinBeyond, chaseEnemyMaxBeyond);
                eLats[i]   = Random.Range(-lateralMax, lateralMax);
                ePos[i]    = webCenter + approachDir * eDist + perpDir * eLats[i];
                ePos[i].y  = _poolTemplateY;
            }

            // 최외곽 배정: perpDir 기준 가장 왼쪽(최소 eLat) = agent1, 가장 오른쪽(최대 eLat) = agent2
            int leftmostIdx = 0, rightmostIdx = 0;
            for (int i = 1; i < CHASE_ENEMY_COUNT; i++)
            {
                if (eLats[i] < eLats[leftmostIdx])  leftmostIdx  = i;
                if (eLats[i] > eLats[rightmostIdx]) rightmostIdx = i;
            }

            // pool 순서: [0]=최외곽 왼쪽(→agent1), [1]=최외곽 오른쪽(→agent2), [2][3]=나머지
            int[] poolOrder = new int[CHASE_ENEMY_COUNT];
            poolOrder[0] = leftmostIdx;
            poolOrder[1] = rightmostIdx;
            int slot = 2;
            for (int i = 0; i < CHASE_ENEMY_COUNT; i++)
            {
                if (i != leftmostIdx && i != rightmostIdx)
                    poolOrder[slot++] = i;
            }

            // 적군 스폰
            if (_enemyPool != null)
            {
                for (int ei = 0; ei < _enemyPool.Length; ei++)
                {
                    if (_enemyPool[ei] == null) continue;
                    if (ei < CHASE_ENEMY_COUNT)
                    {
                        Vector3 p  = ePos[poolOrder[ei]];
                        Vector3 ld = motherPos - p; ld.y = 0f;
                        Quaternion rot = ld.sqrMagnitude > 0.01f
                            ? Quaternion.LookRotation(ld, Vector3.up) : Quaternion.identity;
                        ResetPoolObject(ei, p, rot);
                    }
                    else
                        _enemyPool[ei].SetActive(false);
                }
                IgnoreCollisionBetweenEnemies();
            }

            // 5. 아군 쌍 스폰 (webCenter 좌우)
            Vector3 pos1 = webCenter - perpDir * (sep * 0.5f);
            Vector3 pos2 = webCenter + perpDir * (sep * 0.5f);
            // Y 좌표는 SpawnChaseReadyPair 내부에서 _templateAgent1Y/2Y로 덮어씀

            // 방향 : 모선을 바라봄 (-approachDir = 적 → 모선 방향)
            Quaternion allyRot = Quaternion.LookRotation(-approachDir, Vector3.up);

            GameObject[] enemies = new GameObject[CHASE_ENEMY_COUNT];
            for (int i = 0; i < CHASE_ENEMY_COUNT && _enemyPool != null && i < _enemyPool.Length; i++)
                enemies[i] = _enemyPool[i];

            launchZoneManager.SpawnChaseReadyPair(
                pos1, pos2, webCenter,
                enemies, allyRot,
                agentGroup, chaseGuidanceSteps, this);

            // 5. 안정화 대기
            if (chaseGuidanceSteps > 0)
            {
                float endTime = Time.time + chaseGuidanceSteps * Time.fixedDeltaTime;
                while (Time.time < endTime)
                    yield return new WaitForFixedUpdate();
            }
            else
            {
                yield return null;
            }

            _episodeActive = true;
        }

        /// <summary>
        /// 적군 선박을 모선 기준 동적 위치로 스폰
        /// formationSpawner가 있으면 포메이션 패턴 사용, 없으면 기존 단방향 스폰
        /// </summary>
        private void ResetAttackBoatsDynamic(Vector3 motherPos, Vector3 enemyDir)
        {
            if (_enemyPool == null) return;

            int stageTargetCount = GetActiveEnemyCountForStage();

            if (formationSpawner != null)
            {
                // 포메이션 스폰: 3가지 패턴 중 선택하여 스폰 데이터 생성
                SpawnData[] spawns = formationSpawner.GenerateFormation(
                    motherPos, stageTargetCount, enemySpawnDistance, _poolTemplateY);
                _currentFormation = formationSpawner.GetLastFormationType();

                // 포메이션 주 접근 방향으로 _currentEnemyApproachAngle 업데이트
                _currentEnemyApproachAngle = formationSpawner.GetLastApproachAngleDeg();

                for (int i = 0; i < _enemyPool.Length; i++)
                {
                    if (_enemyPool[i] == null) continue;

                    if (i < stageTargetCount && i < spawns.Length)
                    {
                        ResetPoolObject(i, spawns[i].position, spawns[i].rotation);
                    }
                    else
                    {
                        var behaviours = _enemyPool[i].GetComponents<MonoBehaviour>();
                        foreach (var mb in behaviours)
                        {
                            if (mb != null) mb.CancelInvoke();
                        }
                        _enemyPool[i].SetActive(false);
                    }
                }

                Debug.Log($"[DefenseEnv] Formation: {_currentFormation}, enemies: {stageTargetCount}, " +
                    $"approachAngle: {_currentEnemyApproachAngle:F0}°, poolSize: {_enemyPool.Length}");
            }
            else
            {
                // 레거시 단방향 스폰 (fallback)
                for (int i = 0; i < _enemyPool.Length; i++)
                {
                    if (_enemyPool[i] == null) continue;

                    if (i < stageTargetCount)
                    {
                        Vector3 spreadOffset = new Vector3(
                            Random.Range(-enemySpawnSpread, enemySpawnSpread),
                            0f,
                            Random.Range(-enemySpawnSpread, enemySpawnSpread)
                        );

                        Vector3 spawnPos = motherPos + enemyDir * enemySpawnDistance + spreadOffset;
                        spawnPos.y = _poolTemplateY;

                        Vector3 lookDir = motherPos - spawnPos;
                        lookDir.y = 0f;
                        Quaternion spawnRot = lookDir.sqrMagnitude > 0.01f
                            ? Quaternion.LookRotation(lookDir, Vector3.up)
                            : Quaternion.identity;

                        ResetPoolObject(i, spawnPos, spawnRot);
                    }
                    else
                    {
                        var behaviours = _enemyPool[i].GetComponents<MonoBehaviour>();
                        foreach (var mb in behaviours)
                        {
                            if (mb != null) mb.CancelInvoke();
                        }
                        _enemyPool[i].SetActive(false);
                    }
                }

                Debug.Log($"[DefenseEnv] ResetAttackBoatsDynamic(legacy): target={stageTargetCount}, poolSize={_enemyPool.Length}");
            }

            // 풀 객체 간 충돌 무시
            IgnoreCollisionBetweenEnemies();

            UpdateEnemyShipsArray();
        }
        // EnsureAttackBoatCount 삭제됨 → 풀이 고정 크기이므로 동적 생성 불필요

        /// <summary>
        /// enemyShips 배열을 현재 활성 attack_boat로 업데이트
        /// </summary>
        private void UpdateEnemyShipsArray()
        {
            if (_enemyPool == null) return;

            var activeEnemies = new System.Collections.Generic.List<GameObject>();
            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] != null && _enemyPool[i].activeSelf && !_neutralizedEnemies.Contains(_enemyPool[i]))
                    activeEnemies.Add(_enemyPool[i]);
            }

            enemyShips = activeEnemies.ToArray();

            // DefenseAgent에 적군 배열 전달 (원본 쌍)
            if (defenseAgent1 != null) defenseAgent1.enemyShips = enemyShips;
            if (defenseAgent2 != null) defenseAgent2.enemyShips = enemyShips;

            // LaunchZoneManager의 모든 활성 쌍에도 적군 배열 전달
            if (launchZoneManager != null && launchZoneManager.IsInitialized)
            {
                launchZoneManager.UpdateAllAgentEnemyShips(enemyShips);
            }
        }

        /// <summary>
        /// 모든 적군 선박을 원점으로 리셋 (레거시 스폰, useDynamicSpawn=false)
        /// 풀 기반: Stage에 맞는 수만 활성화, 나머지는 비활성화
        /// </summary>
        private void ResetAttackBoatsToOrigin()
        {
            if (_enemyPool == null) return;

            int stageTargetCount = GetActiveEnemyCountForStage();

            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == null) continue;

                if (i < stageTargetCount)
                {
                    // 원점 위치로 리셋 (풀 인덱스 기반, 템플릿 높이 유지)
                    Vector3 spawnPos = _enemyPool[i].transform.position;
                    spawnPos.y = _poolTemplateY;
                    ResetPoolObject(i, spawnPos, Quaternion.identity);

                    // Cinemachine Dolly Cart 활성화 (레거시 스폰에서는 경로 추적 사용)
                    if (_poolDollyCarts[i] != null)
                    {
                        _poolDollyCarts[i].enabled = true;
                        if (enableRandomPathAssignment)
                        {
                            var randomPath = GetRandomAttackPath();
                            if (randomPath != null)
                            {
                                _poolDollyCarts[i].m_Path = randomPath;
                            }
                        }
                        _poolDollyCarts[i].m_Position = 0f;
                    }
                }
                else
                {
                    // 초과분 비활성화
                    var behaviours = _enemyPool[i].GetComponents<MonoBehaviour>();
                    foreach (var mb in behaviours)
                    {
                        if (mb != null) mb.CancelInvoke();
                    }
                    _enemyPool[i].SetActive(false);
                }
            }

            UpdateEnemyShipsArray();
        }
        
        /// <summary>
        /// 씬에 있는 모든 WAKE(Clone) 객체를 찾아서 파괴하고, WakeGenerator 컴포넌트를 완전히 비활성화
        /// </summary>
        private void DestroyAllWakeObjects()
        {
            int destroyedCount = 0;
            
            // 현재 환경 내의 Wake(Clone) 오브젝트만 찾아서 파괴 (멀티 환경 호환)
            var envRoot = GetEnvironmentRoot();
            Transform[] allTransforms = envRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                if (t != null && t.gameObject.name.Contains("Wake") && (t.gameObject.name.Contains("Clone") || t.gameObject.name.Contains("(Clone)")))
                {
                    Destroy(t.gameObject);
                    destroyedCount++;
                }
            }

            // 현재 환경 내의 WakeGenerator만 찾아서 비활성화하고 코루틴 중지 (멀티 환경 호환)
            WakeGenerator[] allWakeGenerators = envRoot.GetComponentsInChildren<WakeGenerator>(true);
            foreach (var wakeGen in allWakeGenerators)
            {
                if (wakeGen != null)
                {
                    // 모든 코루틴 중지
                    wakeGen.StopAllCoroutines();
                    // 컴포넌트 비활성화
                    wakeGen.enabled = false;
                }
            }
            
        }
        
        /// <summary>
        /// 위치 리셋 - 아군 선박은 비활성화 없이 위치만 리셋
        /// </summary>
        private void ResetPositions()
        {
            // 모든 WAKE(Clone) 객체 제거
            DestroyAllWakeObjects();

            // ========================================
            // 아군 선박(DefenseAgent)은 비활성화 없이 위치만 리셋
            // ========================================
            float sharedAngle = enableRandomSpawn
                ? Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange)
                : 0f;

            // 두 선박이 동일한 위치 오프셋을 공유
            Vector3 sharedOffset = GetSharedRandomOffset();

            ResetDefenseAgentPosition(defenseAgent1, _originalDefense1Pos, _originalDefense1Rot, sharedAngle, sharedOffset);
            ResetDefenseAgentPosition(defenseAgent2, _originalDefense2Pos, _originalDefense2Rot, sharedAngle, sharedOffset);

            // Web 위치 설정 (2대 중간)
            if (webObject != null && defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 webPos = (defenseAgent1.transform.position + defenseAgent2.transform.position) / 2f;
                webPos.y = webSpawnPos.y;
                webObject.transform.position = webPos;
            }
            else if (webObject != null)
            {
                webObject.transform.position = webSpawnPos;
            }
        }
        
        /// <summary>
        /// 아군 선박 2대가 공유하는 랜덤 오프셋 계산 (위치 동기화용)
        /// </summary>
        private Vector3 GetSharedRandomOffset()
        {
            if (!enableRandomSpawn)
                return Vector3.zero;

            // 원형 영역 내 균등 분포 (극좌표 사용)
            float randomAngle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float randomRadius = Mathf.Sqrt(Random.Range(0f, 1f)) * spawnRange;

            return new Vector3(randomRadius * Mathf.Cos(randomAngle), 0f, randomRadius * Mathf.Sin(randomAngle));
        }

        /// <summary>
        /// 기존 위치에서 랜덤 스폰 위치 생성 (적군 경로용)
        /// </summary>
        private Vector3 GetRandomSpawnPosition(Vector3 originalPos)
        {
            // 원형 영역 내 균등 분포 (극좌표 사용)
            float randomAngle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float randomRadius = Mathf.Sqrt(Random.Range(0f, 1f)) * spawnRange; // sqrt로 균등 분포

            float randomX = originalPos.x + randomRadius * Mathf.Cos(randomAngle);
            float randomZ = originalPos.z + randomRadius * Mathf.Sin(randomAngle);
            return new Vector3(randomX, originalPos.y, randomZ);
        }

        /// <summary>
        /// 적군 선박을 모선 방향으로 돌진시킴 (매 FixedUpdate 호출)
        /// AttackAgent 없이 Engine을 직접 제어
        /// </summary>
        private void DriveEnemiesForward()
        {
            if (_enemyPool == null) return;

            Vector3 motherPos = motherShip.transform.position;

            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == null || !_enemyPool[i].activeSelf) continue;

                // 무력화된 적은 구동하지 않음
                if (_neutralizedEnemies.Contains(_enemyPool[i])) continue;

                Engine engine = _poolEngines[i];
                if (engine == null || engine.RB == null) continue;

                // AttackAgent가 rush 모드로 실제 이동 처리 중일 때만 스킵 (이중 구동 방지)
                AttackAgent attackAgent = _poolAttackAgents[i];
                if (attackAgent != null && attackAgent.enabled &&
                    !attackAgent.followWaypoints && attackAgent.enableRush &&
                    attackAgent.targetMotherShip != null)
                    continue;

                // 모선 방향 계산
                Vector3 toMother = motherPos - _enemyPool[i].transform.position;
                toMother.y = 0f;
                if (toMother.sqrMagnitude < 0.01f) continue;

                float angleToMother = Vector3.SignedAngle(_enemyPool[i].transform.forward, toMother.normalized, Vector3.up);
                float baseSteering = Mathf.Clamp(angleToMother / 45f, -1f, 1f);

                // Perlin 노이즈 (각 적군 다른 패턴)
                float seed = _poolNoiseSeed[i];
                float noise = (Mathf.PerlinNoise(seed, Time.time * enemyNoiseSpeed) - 0.5f) * 2f * enemySteeringNoise;

                float steering = Mathf.Clamp(baseSteering + noise, -1f, 1f);

                // Engine.Accelerate()는 0~1 클램프 → 적군은 직접 AddForce로 속도 배율 적용
                var forward = engine.RB.transform.forward;
                forward.y = 0f;
                forward.Normalize();
                if (float.IsNaN(forward.x)) forward = Vector3.forward;
                engine.RB.AddForce(engine.horsePower * currentEnemyRushThrottle * forward, ForceMode.Acceleration);

                engine.Turn(steering * enemySteeringSensitivity);
            }
        }

        /// <summary>
        /// 모든 attack_boat의 대기 중인 Invoke/코루틴 취소 (에피소드 리셋 시)
        /// AttackBoatDisabler 등의 지연된 동작이 리셋 후에도 실행되는 것을 방지
        /// </summary>
        private void CancelAttackBoatPendingActions()
        {
            if (_enemyPool == null) return;

            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == null) continue;

                // 캐시된 Disabler CancelInvoke
                if (_poolDisablers[i] != null)
                {
                    _poolDisablers[i].CancelInvoke();
                }

                // 모든 MonoBehaviour의 Invoke 취소
                var behaviours = _enemyPool[i].GetComponents<MonoBehaviour>();
                foreach (var mb in behaviours)
                {
                    if (mb != null && mb.enabled)
                    {
                        mb.CancelInvoke();
                    }
                }
            }
        }

        /// <summary>
        /// 풀 내 활성 적군 간 물리 충돌 무시
        /// </summary>
        private void IgnoreCollisionBetweenEnemies()
        {
            if (_enemyPool == null) return;

            var activeBoats = new System.Collections.Generic.List<GameObject>();
            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] != null && _enemyPool[i].activeSelf)
                    activeBoats.Add(_enemyPool[i]);
            }

            for (int i = 0; i < activeBoats.Count; i++)
            {
                var collidersA = activeBoats[i].GetComponentsInChildren<Collider>();
                for (int j = i + 1; j < activeBoats.Count; j++)
                {
                    var collidersB = activeBoats[j].GetComponentsInChildren<Collider>();
                    foreach (var ca in collidersA)
                        foreach (var cb in collidersB)
                            if (ca != null && cb != null)
                                Physics.IgnoreCollision(ca, cb, true);
                }
            }
        }

        /// <summary>
        /// 씬에서 "attack_track"을 이름에 포함하는 모든 오브젝트의 CinemachineSmoothPath를 수집
        /// </summary>
        private void FindAllAttackTrackPaths()
        {
            var paths = new System.Collections.Generic.List<CinemachineSmoothPath>();

            // 현재 환경 내의 CinemachineSmoothPath 중 이름에 "attacktrack"이 포함된 것을 수집 (멀티 환경 호환)
            var allPaths = FindComponentsInEnvironment<CinemachineSmoothPath>();
            foreach (var path in allPaths)
            {
                string lowerName = path.gameObject.name.ToLower();
                if (lowerName.Contains("attacktrack") || lowerName.Contains("attack_track"))
                {
                    paths.Add(path);
                }
            }

            _availableAttackPaths = paths.ToArray();
        }

        /// <summary>
        /// 랜덤 attack_track 경로 반환
        /// </summary>
        private CinemachinePathBase GetRandomAttackPath()
        {
            if (_availableAttackPaths == null || _availableAttackPaths.Length == 0)
                return null;

            int index = Random.Range(0, _availableAttackPaths.Length);
            return _availableAttackPaths[index];
        }

        /// <summary>
        /// 모든 attack_track 경로의 원본 웨이포인트 저장 (Start()에서 호출)
        /// </summary>
        private void SaveOriginalWaypoints()
        {
            if (_availableAttackPaths == null || _availableAttackPaths.Length == 0)
                return;

            _allOriginalWaypoints = new Vector3[_availableAttackPaths.Length][];

            for (int p = 0; p < _availableAttackPaths.Length; p++)
            {
                var path = _availableAttackPaths[p];
                if (path == null || path.m_Waypoints == null)
                {
                    _allOriginalWaypoints[p] = new Vector3[0];
                    continue;
                }

                int waypointCount = path.m_Waypoints.Length;
                _allOriginalWaypoints[p] = new Vector3[waypointCount];

                for (int i = 0; i < waypointCount; i++)
                {
                    _allOriginalWaypoints[p][i] = path.m_Waypoints[i].position;
                }
            }
        }

        /// <summary>
        /// 모든 attack_track 경로의 웨이포인트 0, 1, 2번을 랜덤화 (에피소드 시작 시 호출)
        /// </summary>
        private void RandomizeEnemyWaypoints()
        {
            if (!enableEnemyPathRandomization)
                return;

            if (_availableAttackPaths == null || _allOriginalWaypoints == null)
                return;

            for (int p = 0; p < _availableAttackPaths.Length; p++)
            {
                var path = _availableAttackPaths[p];
                if (path == null || path.m_Waypoints == null)
                    continue;

                var origWaypoints = _allOriginalWaypoints[p];
                if (origWaypoints == null || origWaypoints.Length < 3)
                    continue;

                // 웨이포인트 0, 1, 2번만 랜덤화
                for (int i = 0; i < 3 && i < path.m_Waypoints.Length; i++)
                {
                    Vector3 originalPos = origWaypoints[i];
                    Vector3 randomizedPos = GetRandomSpawnPosition(originalPos);

                    if (path.transform.parent != null)
                    {
                        randomizedPos = path.transform.InverseTransformPoint(
                            path.transform.TransformPoint(originalPos) +
                            (randomizedPos - originalPos)
                        );
                    }
                    else
                    {
                        randomizedPos = originalPos + (randomizedPos - originalPos);
                    }

                    path.m_Waypoints[i].position = randomizedPos;
                }

                path.InvalidateDistanceCache();
            }
        }

        #endregion

        #region Stage Management

        /// <summary>
        /// 현재 Stage에 맞는 설정 적용
        /// - Stage1: 적군 비활성화, 대형 유지만 학습
        /// - Stage2: 적군 1~2대 활성화, 포획 보상 학습
        /// - Stage3: 적군 3~5대 활성화, 전술 기동 학습
        /// </summary>
        private void ApplyStageSettings()
        {
            int activeEnemyCount = GetActiveEnemyCountForStage();


            // 적군 활성화/비활성화
            ApplyEnemyActivation(activeEnemyCount);

            // Stage별 추가 설정
            switch (currentStage)
            {
                case TrainingStage.Stage1_Formation:
                    // Stage1: 대형 유지에 집중
                    // 포획/모선 관련 이벤트는 발생해도 보상 없음 (적군이 비활성화되므로 발생 안함)
                    break;

                case TrainingStage.Stage2_Capture:
                    // Stage2: 포획 보상 활성화
                    break;

                case TrainingStage.Stage3_Tactical:
                    // Stage3: 전술 기동 보상 활성화
                    break;

                case TrainingStage.Stage6_PhantomFormation:
                    // Stage6: 1 페어 + Phantom 가상 아군쌍 대형 학습
                    break;

                case TrainingStage.Stage7_FleetManeuver:
                    // Stage7: 다수 실제 아군 쌍 군집 기동 학습 (phantom 없음)
                    break;

                case TrainingStage.Stage8_TacticalFullObs:
                    // Stage8: Stage3 동작 + 모든 적 BufferSensor 관측
                    break;

                case TrainingStage.Stage9_DisarmReform:
                    // Stage9: Stage8 + 포획 후 직진 이탈 + 포획 보상 개별 분배
                    break;

                case TrainingStage.Stage10_FlankCapture:
                    break;
            }
        }

        /// <summary>
        /// 현재 Stage에서 활성화할 적군 수 반환
        /// </summary>
        private int GetActiveEnemyCountForStage()
        {
            switch (currentStage)
            {
                case TrainingStage.Stage1_Formation:
                    return disableEnemiesInStage1 ? 0 : stage1EnemyCount;

                case TrainingStage.Stage2_Capture:
                    return stage2EnemyCount;

                case TrainingStage.Stage3_Tactical:
                    return stage3EnemyCount;

                case TrainingStage.Stage4_Commander:
                    return stage4EnemyCount;

                case TrainingStage.Stage5_FullScale:
                    return stage5EnemyCount;

                case TrainingStage.Stage6_PhantomFormation:
                    return Mathf.Max(stage6EnemyCount, 1); // 최소 1대 보장

                case TrainingStage.Stage7_FleetManeuver:
                    return Mathf.Max(_stage7AllyCount, 1); // 아군 쌍 수와 동일

                case TrainingStage.Stage8_TacticalFullObs:
                    return stage8EnemyCount;

                case TrainingStage.Stage9_DisarmReform:
                    return stage9EnemyCount;

                case TrainingStage.Stage10_FlankCapture:
                    return stage10EnemyCount;

                default:
                    return 0;
            }
        }

        /// <summary>
        /// 경량 모드 적용: 모든 Web의 그물 메시를 단순 Cube로 전환
        /// </summary>
        private void ApplyLightweightMode()
        {
            if (launchZoneManager == null) return;
            for (int i = 0; i < launchZoneManager.GetCurrentPoolCount(); i++)
            {
                var pair = launchZoneManager.GetPair(i);
                if (pair?.webObject == null) continue;
                var dw = pair.webObject.GetComponent<DynamicWeb>();
                if (dw != null)
                {
                    dw.SetFishingNetVisual(!lightweightMode);
                    dw.minWebActiveDist = webAppearDist;  // Inspector 값 → 그물 등장 거리 동기화
                }
            }
            // 단독 웹 오브젝트도 처리
            if (webObject != null)
            {
                var dw = webObject.GetComponent<DynamicWeb>();
                if (dw != null)
                {
                    dw.SetFishingNetVisual(!lightweightMode);
                    dw.minWebActiveDist = webAppearDist;
                }
            }
        }

        /// <summary>
        /// non-Commander Stage에서 자동 배치할 아군 쌍 수 계산
        /// Stage3: 1:1 (적 1대당 쌍 1개), 기타: 적 3대당 1쌍
        /// </summary>
        private int GetAllyPairCountForStage()
        {
            int enemyCount = GetActiveEnemyCountForStage();
            if (enemyCount <= 0) return 1;
            int maxPairs = launchZoneManager != null ? launchZoneManager.maxPairCount : 6;

            // Stage7 (군집 기동): 5~10쌍 랜덤, 적군도 동일 수
            if (currentStage == TrainingStage.Stage7_FleetManeuver)
            {
                _stage7AllyCount = Random.Range(stage7AllyPairMin, stage7AllyPairMax + 1);
                return Mathf.Clamp(_stage7AllyCount, 1, maxPairs);
            }

            // Stage3/Stage6/Stage8/Stage9 (기동 학습): 1:1 — 적 1대당 아군 쌍 1개
            if (currentStage == TrainingStage.Stage3_Tactical || currentStage == TrainingStage.Stage6_PhantomFormation
                || currentStage == TrainingStage.Stage8_TacticalFullObs || currentStage == TrainingStage.Stage9_DisarmReform)
                return Mathf.Clamp(enemyCount, 1, maxPairs);

            return Mathf.Clamp(Mathf.CeilToInt(enemyCount / 3f), 1, maxPairs);
        }

        /// <summary>
        /// 적군 선박 활성화/비활성화 적용
        /// </summary>
        private void ApplyEnemyActivation(int activeCount)
        {
            if (_enemyPool == null)
            {
                Debug.LogWarning("[DefenseEnv] ApplyEnemyActivation: _enemyPool이 null입니다.");
                return;
            }

            int activated = 0;
            int nullCount = 0;
            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == null) { nullCount++; continue; }

                bool shouldBeActive = activated < activeCount;
                _enemyPool[i].SetActive(shouldBeActive);

                if (shouldBeActive)
                    activated++;
            }

            if (nullCount > 0)
            {
                Debug.LogWarning($"[DefenseEnv] ApplyEnemyActivation: 풀에 null 엔트리 {nullCount}개 (풀 초기화 실패 가능성)");
            }

            UpdateEnemyShipsArray();
            Debug.Log($"[DefenseEnv] ApplyEnemyActivation: 요청={activeCount}, 실제활성={activated}, poolSize={_enemyPool.Length}");
        }

        /// <summary>
        /// Stage 변경 (Inspector 또는 코드에서 호출)
        /// </summary>
        public void SetTrainingStage(TrainingStage newStage)
        {
            if (currentStage != newStage)
            {
                currentStage = newStage;

                ApplyStageSettings();
            }
        }

        /// <summary>
        /// 비활성/무력화된 적군 풀 오브젝트 수 반환 (스폰 가능한 수)
        /// </summary>
        public int GetInactiveEnemyCount()
        {
            if (_enemyPool == null) return 0;
            int count = 0;
            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == null) continue;
                if (!_enemyPool[i].activeSelf || _neutralizedEnemies.Contains(_enemyPool[i]))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 비활성 풀에서 적군을 추가 스폰 (런타임 버튼용)
        /// 모선 주변 랜덤 위치에 배치, 모선을 향해 돌진
        /// </summary>
        /// <returns>실제 스폰된 수</returns>
        public int SpawnAdditionalEnemies(int count)
        {
            if (_enemyPool == null || motherShip == null) return 0;

            Vector3 motherPos = motherShip.transform.position;
            int spawned = 0;

            for (int i = 0; i < _enemyPool.Length && spawned < count; i++)
            {
                if (_enemyPool[i] == null) continue;

                // 이미 활성 + 무력화 안 된 적은 스킵
                if (_enemyPool[i].activeSelf && !_neutralizedEnemies.Contains(_enemyPool[i]))
                    continue;

                // 무력화된 적이면 해제
                _neutralizedEnemies.Remove(_enemyPool[i]);

                // 랜덤 방향에서 모선을 향해 스폰
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                Vector3 spawnPos = motherPos + dir * enemySpawnDistance;
                spawnPos.y = _poolTemplateY;

                Vector3 lookDir = motherPos - spawnPos;
                lookDir.y = 0f;
                Quaternion spawnRot = lookDir.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(lookDir, Vector3.up)
                    : Quaternion.identity;

                ResetPoolObject(i, spawnPos, spawnRot);
                spawned++;
            }

            if (spawned > 0)
            {
                UpdateEnemyShipsArray();
                Debug.Log($"[DefenseEnv] SpawnAdditionalEnemies: 요청={count}, 스폰={spawned}");
            }

            return spawned;
        }

        /// <summary>에피소드 내 아군 쌍 추가 생성 가능 여부</summary>
        public bool CanDeployMorePairs()
        {
            return _allyPairsCreatedThisEpisode < maxAllyPairsPerEpisode;
        }

        /// <summary>
        /// 페어를 모두 동시에 배치하는 코루틴 (딜레이 없음)
        /// </summary>
        private System.Collections.IEnumerator DeployPairsSequentially(
            FormationType formation, float approachAngle, float[] divAngles,
            int totalCount, Vector3 motherPos, SimpleMultiAgentGroup group)
        {
            launchZoneManager.PrepareSequentialDeploy(
                formation, approachAngle, divAngles, totalCount, motherPos, group);

            // 활성 페어 + 예비 페어 모두 즉시 배치
            int totalCount2 = launchZoneManager.maxPairCount;
            int actualCount = launchZoneManager.SequentialPairCount; // 활성 클러스터 수
            Debug.LogWarning($"[DeploySeq] active={actualCount}, total(+standby)={totalCount2}, maxPair={launchZoneManager.maxPairCount}");
            for (int n = 0; n < totalCount2; n++)
            {
                if (_episodeEnding) yield break;
                bool ok = launchZoneManager.DeployNextSequentialPair();
                Debug.LogWarning($"[DeploySeq] n={n}/{totalCount2} → result={ok}, active={launchZoneManager.GetActivePairCount()}");
            }
            ApplyLightweightMode();
            yield break;
        }

        /// <summary>
        /// DeploySinglePair 래퍼: 에피소드 제한 체크 + 카운터 증가
        /// </summary>
        private bool TryDeployPair(Vector3 motherPos, float angleDeg, SimpleMultiAgentGroup group, int forceZoneIdx = -1)
        {
            if (!CanDeployMorePairs()) return false;
            bool result = launchZoneManager.DeploySinglePair(motherPos, angleDeg, group, forceZoneIdx);
            if (result) _allyPairsCreatedThisEpisode++;
            return result;
        }

        /// <summary>
        /// 비활성 풀에서 아군 쌍을 추가 배치 (런타임 버튼용)
        /// </summary>
        /// <returns>배치 성공 여부</returns>
        public bool SpawnAllyPair(int forceZoneIdx = -1)
        {
            if (launchZoneManager == null || motherShip == null)
                return false;

            int before = launchZoneManager.GetActivePairCount();
            Vector3 motherPos = motherShip.transform.position;
            bool result = TryDeployPair(motherPos, _currentEnemyApproachAngle, m_AgentGroup, forceZoneIdx);
            int after = launchZoneManager.GetActivePairCount();

            Debug.LogWarning($"[SpawnAllyPair] zone={forceZoneIdx}, result={result}, active {before}→{after}, " +
                $"pairs={_allyPairsCreatedThisEpisode}/{maxAllyPairsPerEpisode}, " +
                $"inactive={launchZoneManager.GetInactivePairCount()}");
            return result;
        }

        /// <summary>
        /// 배치 가능한 비활성 아군 쌍 수 반환
        /// </summary>
        public int GetAvailableAllyPairCount()
        {
            if (launchZoneManager == null) return 0;
            return launchZoneManager.GetInactivePairCount();
        }

        #endregion

        #region Assignment Line Visualization

        private Material _lineMaterial;

        private void EnsureLineMaterial()
        {
            if (_lineMaterial != null) return;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _lineMaterial = new Material(shader);
            _lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            _lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        /// <summary>
        /// 게임 뷰에서 아군-적군 배정선 표시 (GL)
        /// </summary>
        private void OnRenderObject()
        {
            if (launchZoneManager == null || !launchZoneManager.IsInitialized) return;
            if (enemyShips == null) return;

            EnsureLineMaterial();
            _lineMaterial.SetPass(0);

            GL.PushMatrix();
            GL.Begin(GL.LINES);

            int poolCount = launchZoneManager.GetCurrentPoolCount();
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = launchZoneManager.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                if (pair.agent1 == null) continue;

                int targetIdx = pair.agent1.assignedTargetIndex;
                if (targetIdx <= 0) continue;

                int enemyIdx = targetIdx - 1;
                GameObject enemy = GetPooledEnemy(enemyIdx);
                if (enemy == null || !enemy.activeSelf) continue;

                Vector3 allyCenter = pair.agent2 != null
                    ? (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f
                    : pair.agent1.transform.position;
                allyCenter.y += 3f;

                Vector3 enemyPos = enemy.transform.position;
                enemyPos.y += 3f;

                // 아군→적군: 녹색 실선
                GL.Color(new Color(0f, 1f, 0.3f, 0.8f));
                GL.Vertex(allyCenter);
                GL.Vertex(enemyPos);

                // Phantom 6쌍 시각화 (Stage6에서만)
                if (currentStage == TrainingStage.Stage6_PhantomFormation && pair.agent1.lastPhantomValid && pair.agent1.phantomPairs != null)
                {
                    float s = 8f;
                    for (int phi = 0; phi < pair.agent1.phantomCount; phi++)
                    {
                        var pp = pair.agent1.phantomPairs[phi];
                        if (!pp.isValid) continue;

                        Vector3 a1 = pp.agent1Pos; a1.y += 3f;
                        Vector3 a2 = pp.agent2Pos; a2.y += 3f;

                        if (pp.isStopped)
                        {
                            // 정지: 빨간색
                            GL.Color(new Color(1f, 0.3f, 0.3f, 0.5f));
                        }
                        else if (pp.hasWeb)
                        {
                            // 이동+그물: 노란색
                            GL.Color(new Color(1f, 0.9f, 0f, 0.7f));
                        }
                        else
                        {
                            // 이동+그물없음: 회색
                            GL.Color(new Color(0.6f, 0.6f, 0.6f, 0.4f));
                        }
                        GL.Vertex(a1);
                        GL.Vertex(a2);

                        // WebCenter 마커
                        Color markerColor = pp.isStopped ? new Color(1f, 0.3f, 0.3f, 0.5f)
                            : pp.hasWeb ? new Color(0f, 1f, 1f, 0.6f)
                            : new Color(0.5f, 0.5f, 0.5f, 0.4f);
                        GL.Color(markerColor);
                        Vector3 wc = pp.WebCenter; wc.y += 3f;
                        GL.Vertex(wc + new Vector3(-s, 0f, -s));
                        GL.Vertex(wc + new Vector3(s, 0f, s));
                        GL.Vertex(wc + new Vector3(-s, 0f, s));
                        GL.Vertex(wc + new Vector3(s, 0f, -s));

                        // phantom ↔ 아군 중심 연결 (반투명)
                        GL.Color(new Color(0f, 1f, 1f, 0.15f));
                        GL.Vertex(allyCenter);
                        GL.Vertex(wc);
                    }
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        #endregion

        #region Gizmo Visualization

        /// <summary>
        /// 에디터에서 스폰 범위 시각화
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!enableRandomSpawn && !enableEnemyPathRandomization)
                return;

            // 아군 선박 스폰 범위 시각화 (Cyan)
            if (enableRandomSpawn)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.3f); // 반투명 시안

                // Defense 1 스폰 범위
                Vector3 def1Pos = _originalDefense1Pos != Vector3.zero ? _originalDefense1Pos : defense1SpawnPos;
                if (def1Pos != Vector3.zero)
                {
                    DrawCircleGizmo(def1Pos, spawnRange, 32);
                    Gizmos.DrawWireSphere(def1Pos, 1f); // 중심점 표시
                }

                // Defense 2 스폰 범위
                Vector3 def2Pos = _originalDefense2Pos != Vector3.zero ? _originalDefense2Pos : defense2SpawnPos;
                if (def2Pos != Vector3.zero)
                {
                    DrawCircleGizmo(def2Pos, spawnRange, 32);
                    Gizmos.DrawWireSphere(def2Pos, 1f); // 중심점 표시
                }

                // 라벨 표시
                #if UNITY_EDITOR
                UnityEditor.Handles.color = Color.cyan;
                if (def1Pos != Vector3.zero)
                    UnityEditor.Handles.Label(def1Pos + Vector3.up * 5f, $"Defense1\n반경: {spawnRange}m");
                if (def2Pos != Vector3.zero)
                    UnityEditor.Handles.Label(def2Pos + Vector3.up * 5f, $"Defense2\n반경: {spawnRange}m");
                #endif
            }

            // 적군 웨이포인트 스폰 범위 시각화 (Red) - 모든 attack_track 경로
            if (enableEnemyPathRandomization && _availableAttackPaths != null)
            {
                Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.3f); // 반투명 빨강

                for (int p = 0; p < _availableAttackPaths.Length; p++)
                {
                    var path = _availableAttackPaths[p];
                    if (path == null || path.m_Waypoints == null) continue;

                    for (int i = 0; i < 3 && i < path.m_Waypoints.Length; i++)
                    {
                        Vector3 waypointPos;
                        if (_allOriginalWaypoints != null && p < _allOriginalWaypoints.Length && i < _allOriginalWaypoints[p].Length)
                        {
                            waypointPos = _allOriginalWaypoints[p][i];
                        }
                        else
                        {
                            waypointPos = path.m_Waypoints[i].position;
                        }

                        if (path.transform != null)
                        {
                            waypointPos = path.transform.TransformPoint(waypointPos);
                        }

                        DrawCircleGizmo(waypointPos, spawnRange, 32);
                        Gizmos.DrawWireSphere(waypointPos, 2f);

                        #if UNITY_EDITOR
                        UnityEditor.Handles.color = Color.red;
                        UnityEditor.Handles.Label(waypointPos + Vector3.up * 5f, $"{path.gameObject.name} WP{i}\n반경: {spawnRange}m");
                        #endif
                    }
                }
            }
        }

        /// <summary>
        /// XZ 평면에 원 그리기 (Gizmo용)
        /// </summary>
        private void DrawCircleGizmo(Vector3 center, float radius, int segments)
        {
            float angleStep = 360f / segments;
            Vector3 prevPoint = center + new Vector3(radius, 0f, 0f);

            for (int i = 1; i <= segments; i++)
            {
                float angle = i * angleStep * Mathf.Deg2Rad;
                Vector3 newPoint = center + new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius
                );
                Gizmos.DrawLine(prevPoint, newPoint);
                prevPoint = newPoint;
            }
        }

        #endregion
    }
}
