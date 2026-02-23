using UnityEngine;
using Unity.MLAgents;
using Cinemachine;
using System.Linq;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 커리큘럼 학습 단계
    /// </summary>
    public enum TrainingStage
    {
        Stage1_Formation,   // 대형 유지 학습
        Stage2_Capture,     // 포획 보상 학습
        Stage3_Tactical     // 전술 기동 학습
    }

    /// <summary>
    /// 방어 페어: 2척 + Web = 1 포획 유닛 (기본 단위)
    /// </summary>
    [System.Serializable]
    public class DefensePair
    {
        public DefenseAgent agent1;
        public DefenseAgent agent2;
        public GameObject webObject;

        // 런타임 상태 (Commander Phase 2에서 사용)
        [HideInInspector] public bool isDeployed = true;
        [HideInInspector] public int spawnAttemptsLeft = 3;
        [HideInInspector] public int assignedTargetIndex = -1;

        // 원점 저장 (리셋용)
        [HideInInspector] public Vector3 originalPos1, originalPos2;
        [HideInInspector] public Quaternion originalRot1, originalRot2;
    }

    /// <summary>
    /// 방어 환경 컨트롤러 (중앙 허브 버전)
    /// - 모든 에피소드 재시작 로직을 중앙에서 관리
    /// - 그룹 보상 분배 및 환경 관리
    /// - 선박 위치 리셋 및 적군 추적
    /// - 에피소드 시작/종료 관리
    /// - 커리큘럼 학습 단계별 보상 제어
    /// </summary>
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
        [Range(1, 5)]
        public int stage2EnemyCount = 1;

        [Tooltip("Stage3에서 활성화할 적군 수")]
        [Range(1, 5)]
        public int stage3EnemyCount = 1;

        [Header("Defense Pairs")]
        [Tooltip("방어 페어 리스트 (2척+Web = 1 포획 유닛)")]
        public List<DefensePair> defensePairs = new List<DefensePair>();

        [Tooltip("최대 페어 수")]
        public int maxPairs = 4;

        /// <summary>
        /// 전체 방어 에이전트 순회용 편의 접근자
        /// </summary>
        public IEnumerable<DefenseAgent> AllDefenseAgents =>
            defensePairs.SelectMany(p => new[] { p.agent1, p.agent2 }).Where(a => a != null);

        [Header("Legacy Agents (자동 마이그레이션용)")]
        [Tooltip("방어 에이전트 1 (defensePairs가 비어있으면 자동으로 Pair 생성)")]
        public DefenseAgent defenseAgent1;

        [Tooltip("방어 에이전트 2")]
        public DefenseAgent defenseAgent2;
        
        [Header("Components")]
        [Tooltip("SimpleMultiAgentGroup (선택사항 - 없으면 개별 보상으로 fallback)")]
        public SimpleMultiAgentGroup m_AgentGroup;
        
        [Tooltip("보상 계산기")]
        public DefenseRewardCalculator rewardCalculator;

        [Header("Commander")]
        [Tooltip("Commander Agent (선택사항 - 없으면 자동 배정)")]
        public CommanderAgent commanderAgent;

        [Tooltip("스폰 포인트 (Inspector 수동 배치, Commander Phase 2)")]
        public Transform[] spawnPoints;

        [Header("Settings")]
        [Tooltip("보상 계산 주기 (프레임 단위, 1 = 매 프레임)")]
        public int rewardCalculationInterval = 1;
        
        [Tooltip("최대 환경 스텝 수 (에피소드가 이 스텝 수에 도달하면 자동 종료)")]
        public int maxEnvironmentSteps = 5000;
        
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

        [Tooltip("아군 간 최대 허용 거리 (이 거리 초과 시 에피소드 종료)")]
        public float maxAllyDistance = 120f;  // Stage1 최적거리(50m) + 대형붕괴거리(100m) 사이 여유

        [Tooltip("아군 간 최소 허용 거리 (이 거리 미만 시 에피소드 종료)")]
        public float minAllyDistance = 4f;

        [Tooltip("아군 선박이 Web과 충돌했을 때 페널티")]
        public float allyWebCollisionPenalty = -1.0f;

        [Tooltip("적이 그물보다 모선에 이 거리 이상 더 가까우면 방어선 돌파 (m)")]
        public float enemyBreachThreshold = 20f;

        [Tooltip("방어선 돌파 시 페널티")]
        public float enemyBreachPenalty = -1.1f;

        [Header("Weather Randomization")]
        [Tooltip("에피소드 시작 시 날씨(파도/바람) 랜덤화 활성화")]
        public bool randomizeWeatherOnEpisode = false;

        [Tooltip("환경 컨트롤러 (날씨 랜덤화용)")]
        public EnvironmentController environmentController;

        [Header("Enemy Formation")]
        [Tooltip("적군 포메이션 타입 (Random=에피소드마다 랜덤 선택)")]
        public EnemyFormation enemyFormation = EnemyFormation.Random;

        [Header("Enemy Rush Movement")]
        [Tooltip("동적 스폰 시 적군 자동 돌진 활성화")]
        public bool enableEnemyRush = true;

        [Tooltip("적군 돌진 스로틀")]
        [Range(0.1f, 60.0f)]
        public float enemyRushThrottle = 1.0f;

        [Tooltip("적군 조향 노이즈 크기")]
        [Range(0f, 0.5f)]
        public float enemySteeringNoise = 0.1f;

        [Tooltip("적군 노이즈 변화 속도")]
        public float enemyNoiseSpeed = 2f;

        [Tooltip("적군 조향 감도")]
        [Range(0.1f, 2.0f)]
        public float enemySteeringSensitivity = 0.3f;

        // 적군별 노이즈 시드 (Perlin 패턴 다르게)
        private System.Collections.Generic.Dictionary<GameObject, float> _enemyNoiseSeed =
            new System.Collections.Generic.Dictionary<GameObject, float>();

        [Header("Multi-Environment")]
        [Tooltip("환경 루트 Transform (Island Level 등). 비어있으면 부모 또는 자기 자신 사용")]
        public Transform environmentRoot;

        [Header("Reward Monitor (Read Only)")]
        [SerializeField] private float _currentEpisodeReward = 0f;
        [SerializeField] private float _lastStepReward = 0f;
        [SerializeField] private int _currentStep = 0;
        [SerializeField] private int _totalCollisions = 0;

        private int _resetTimer = 0; // FixedUpdate 기반 타이머
        public int CurrentStep => _resetTimer; // Commander 진행도 관측용
        private bool _episodeActive = false;
        private bool _episodeEnding = false; // 에피소드 종료 중 플래그 (중복 호출 방지)
        
        // 에피소드 추적
        private int _episodeNumber = 0; // 에피소드 번호 (재시작 확인용)
        
        // 충돌 횟수 추적 (Web + MotherShip 통합 카운트)
        private int _totalCollisionCount = 0; // 총 충돌 횟수 (Web + MotherShip 합산)
        
        // 중복 충돌 방지 (같은 적군 선박이 짧은 시간 내 여러 번 충돌하는 것 방지)
        private float _collisionCooldown = 2.0f; // 충돌 쿨다운 시간 (초)
        private System.Collections.Generic.Dictionary<GameObject, float> _collisionCooldownTimes = new System.Collections.Generic.Dictionary<GameObject, float>();
        
        // 위치 리셋 관련
        private int _lastResetFrame = -1; // 중복 리셋 방지용
        private bool _isResettingPositions = false; // 위치 리셋 코루틴 실행 중 플래그
        private WebCollisionDetector _webDetector;
        
        // 원래 위치 및 각도 저장 (레거시 호환 - defensePairs[0]에서 동기화)
        private Vector3 _originalDefense1Pos;
        private Vector3 _originalDefense2Pos;
        private Quaternion _originalDefense1Rot;
        private Quaternion _originalDefense2Rot;

        // 씬의 모든 attack_track 경로 (랜덤 할당용)
        private CinemachineSmoothPath[] _availableAttackPaths;

        // 각 경로별 원본 웨이포인트 저장 (랜덤화용)
        private Vector3[][] _allOriginalWaypoints;

        // 각 페어별 위치 교차 감지용 (에피소드 시작 시 agent1이 왼쪽이면 true)
        private List<bool> _pairAgent1StartsOnLeft = new List<bool>();
        // 레거시 호환 (_pairAgent1StartsOnLeft[0]의 별칭)
        private bool _agent1StartsOnLeft;

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
        
        // 적군 선박 (attack_boat 태그) 관리
        private System.Collections.Generic.List<GameObject> _attackBoats = new System.Collections.Generic.List<GameObject>();
        private System.Collections.Generic.Dictionary<GameObject, Vector3> _attackBoatInitialPositions = new System.Collections.Generic.Dictionary<GameObject, Vector3>();
        private System.Collections.Generic.Dictionary<GameObject, CinemachinePathBase> _attackBoatInitialPaths = new System.Collections.Generic.Dictionary<GameObject, CinemachinePathBase>();
        private System.Collections.Generic.Dictionary<string, GameObject> _attackBoatPrefabs = new System.Collections.Generic.Dictionary<string, GameObject>(); // 원본 attack_boat 프리팹 저장 (재생성용, 이름을 키로 사용)
        private System.Collections.Generic.Dictionary<string, Vector3> _attackBoatInitialPositionsByName = new System.Collections.Generic.Dictionary<string, Vector3>(); // 이름 기반 초기 위치 저장 (재생성용)
        private System.Collections.Generic.Dictionary<string, CinemachinePathBase> _attackBoatInitialPathsByName = new System.Collections.Generic.Dictionary<string, CinemachinePathBase>(); // 이름 기반 초기 경로 저장 (재생성용)
        private System.Collections.Generic.HashSet<string> _destroyedAttackBoatNames = new System.Collections.Generic.HashSet<string>(); // 파괴된 attack_boat 이름 추적
        private int _initialAttackBoatCount = 0;
        
        // Inspector에서 Stage 변경 시 자동 적용 (에디터 전용)
        private TrainingStage _lastStage;

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

        private void Start()
        {
            // 초기 Stage 저장
            _lastStage = currentStage;

            // ===== 레거시 → defensePairs 자동 마이그레이션 =====
            // defensePairs가 비어있고 legacy agent1/2가 있으면 자동으로 Pair[0] 생성
            if (defensePairs.Count == 0 && defenseAgent1 != null && defenseAgent2 != null)
            {
                var legacyPair = new DefensePair
                {
                    agent1 = defenseAgent1,
                    agent2 = defenseAgent2,
                    webObject = webObject
                };
                defensePairs.Add(legacyPair);
                Debug.Log("[DefenseEnv] Legacy agent1/2 → defensePairs[0] 자동 마이그레이션");
            }

            // defensePairs에서 legacy 필드 역방향 동기화 (Inspector 호환)
            if (defensePairs.Count > 0)
            {
                var p0 = defensePairs[0];
                if (defenseAgent1 == null) defenseAgent1 = p0.agent1;
                if (defenseAgent2 == null) defenseAgent2 = p0.agent2;
                if (webObject == null) webObject = p0.webObject;
            }

            // SimpleMultiAgentGroup 초기화
            if (m_AgentGroup == null)
            {
                m_AgentGroup = new SimpleMultiAgentGroup();
            }

            // 에이전트 등록: 모든 페어의 에이전트를 그룹에 등록
            if (m_AgentGroup != null)
            {
                foreach (var pair in defensePairs)
                {
                    if (pair.agent1 != null) m_AgentGroup.RegisterAgent(pair.agent1);
                    if (pair.agent2 != null) m_AgentGroup.RegisterAgent(pair.agent2);
                }
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

            // 각 페어별 Web/에이전트 설정
            foreach (var pair in defensePairs)
            {
                // WebCollisionDetector 설정
                if (pair.webObject != null)
                {
                    var detector = pair.webObject.GetComponent<WebCollisionDetector>();
                    if (detector == null)
                        detector = pair.webObject.AddComponent<WebCollisionDetector>();
                    detector.envController = this;

                    var dynamicWeb = pair.webObject.GetComponent<DynamicWeb>();
                    if (dynamicWeb != null)
                        dynamicWeb.envController = this;
                }

                // 페어 내 에이전트 페어링
                if (pair.agent1 != null && pair.agent2 != null)
                {
                    pair.agent1.partnerAgent = pair.agent2;
                    pair.agent2.partnerAgent = pair.agent1;

                    pair.agent1.webObject = pair.webObject;
                    pair.agent2.webObject = pair.webObject;

                    if (motherShip != null)
                    {
                        pair.agent1.motherShip = motherShip;
                        pair.agent2.motherShip = motherShip;
                    }

                    if (enemyShips != null && enemyShips.Length > 0)
                    {
                        pair.agent1.enemyShips = enemyShips;
                        pair.agent2.enemyShips = enemyShips;
                    }

                    pair.agent1.envController = this;
                    pair.agent2.envController = this;
                }

                // 페어 원점 저장 (실제 에이전트의 현재 위치)
                pair.originalPos1 = (pair.agent1 != null) ? pair.agent1.transform.position : Vector3.zero;
                pair.originalPos2 = (pair.agent2 != null) ? pair.agent2.transform.position : Vector3.zero;
                pair.originalRot1 = (pair.agent1 != null) ? pair.agent1.transform.rotation : Quaternion.identity;
                pair.originalRot2 = (pair.agent2 != null) ? pair.agent2.transform.rotation : Quaternion.identity;
            }

            // 레거시 WebCollisionDetector (webObject 필드가 직접 지정된 경우)
            if (webObject != null)
            {
                _webDetector = webObject.GetComponent<WebCollisionDetector>();
                if (_webDetector == null)
                    _webDetector = webObject.AddComponent<WebCollisionDetector>();
                _webDetector.envController = this;
            }

            // 레거시 원점 동기화 (defensePairs[0]에서)
            if (defensePairs.Count > 0)
            {
                _originalDefense1Pos = defensePairs[0].originalPos1;
                _originalDefense2Pos = defensePairs[0].originalPos2;
                _originalDefense1Rot = defensePairs[0].originalRot1;
                _originalDefense2Rot = defensePairs[0].originalRot2;
            }
            else
            {
                _originalDefense1Pos = defense1SpawnPos;
                _originalDefense2Pos = defense2SpawnPos;
                _originalDefense1Rot = Quaternion.Euler(defense1SpawnRot);
                _originalDefense2Rot = Quaternion.Euler(defense2SpawnRot);
            }

            // 페어별 위치 교차 리스트 초기화
            _pairAgent1StartsOnLeft.Clear();
            for (int i = 0; i < defensePairs.Count; i++)
                _pairAgent1StartsOnLeft.Add(false);

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
            
            // attack_boat 태그를 가진 모든 적군 선박 찾기 및 초기 위치 저장
            FindAndSaveAttackBoats();

            // 씬의 모든 attack_track 경로 수집 및 원본 웨이포인트 저장
            FindAllAttackTrackPaths();
            SaveOriginalWaypoints();

            // 첫 에피소드 시작 (PushBlockEnvController 패턴)
            ResetScene();
            
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

            // 각 페어별 거리/교차 체크 - 유예기간 이후부터
            if (!inGracePeriod)
            {
                for (int pi = 0; pi < defensePairs.Count; pi++)
                {
                    var pair = defensePairs[pi];
                    if (pair.agent1 == null || pair.agent2 == null) continue;

                    Vector3 pos1 = pair.agent1.transform.position;
                    Vector3 pos2 = pair.agent2.transform.position;
                    float allyDist = Vector3.Distance(pos1, pos2);

                    // 최대 거리 초과 체크
                    if (maxAllyDistance > 0f && allyDist > maxAllyDistance)
                    {
                        RestartEpisode($"AllyDistanceExceeded(pair={pi},dist={allyDist:F1},max={maxAllyDistance})", rewardCalculator.collisionPenalty);
                        return;
                    }

                    // 최소 거리 미달 체크
                    if (minAllyDistance > 0f && allyDist < minAllyDistance)
                    {
                        RestartEpisode($"AllyDistanceTooClose(pair={pi},dist={allyDist:F1},min={minAllyDistance})", rewardCalculator.collisionPenalty);
                        return;
                    }

                    // 위치 교차 체크
                    bool agent1CurrentlyOnLeft;
                    if (useDynamicSpawn && motherShip != null)
                    {
                        Vector3 center = motherShip.transform.position;
                        float dot1 = Vector3.Dot(pos1 - center, _currentPerpendicularDir);
                        float dot2 = Vector3.Dot(pos2 - center, _currentPerpendicularDir);
                        agent1CurrentlyOnLeft = dot1 < dot2;
                    }
                    else
                    {
                        agent1CurrentlyOnLeft = pos1.x < pos2.x;
                    }

                    bool startsOnLeft = (pi < _pairAgent1StartsOnLeft.Count) ? _pairAgent1StartsOnLeft[pi] : false;
                    if (agent1CurrentlyOnLeft != startsOnLeft)
                    {
                        RestartEpisode($"PositionSwapped(pair={pi},startLeft={startsOnLeft},nowLeft={agent1CurrentlyOnLeft})", rewardCalculator.collisionPenalty);
                        return;
                    }
                }
            }

            // 방어선 돌파 체크: 적이 그물보다 모선에 임계값 이상 더 가까우면 에피소드 종료
            if (!inGracePeriod && motherShip != null && enemyBreachThreshold > 0f)
            {
                Vector3 motherPos = motherShip.transform.position;

                // 모든 페어의 Web 중 가장 가까운 것 기준
                float closestWebToMother = float.MaxValue;
                foreach (var pair in defensePairs)
                {
                    if (pair.webObject != null)
                    {
                        float d = Vector3.Distance(pair.webObject.transform.position, motherPos);
                        if (d < closestWebToMother) closestWebToMother = d;
                    }
                }

                if (closestWebToMother < float.MaxValue)
                {
                    foreach (var enemy in enemyShips)
                    {
                        if (enemy == null || !enemy.activeInHierarchy) continue;
                        float enemyToMother = Vector3.Distance(enemy.transform.position, motherPos);

                        if (enemyToMother < closestWebToMother - enemyBreachThreshold)
                        {
                            RestartEpisode($"EnemyBreach(enemy={enemy.name},enemyDist={enemyToMother:F0},webDist={closestWebToMother:F0},gap={closestWebToMother - enemyToMother:F0})", enemyBreachPenalty);
                            return;
                        }
                    }
                }
            }

            // 보상 계산 주기 확인
            if (_resetTimer % rewardCalculationInterval != 0)
                return;

            if (rewardCalculator == null || defensePairs.Count == 0)
                return;

            // 각 페어별 보상 계산 (대형 유지 + 적 접근 + 시간 페널티)
            float totalStepReward = 0f;
            foreach (var pair in defensePairs)
            {
                if (pair.agent1 == null || pair.agent2 == null) continue;

                var state1 = rewardCalculator.GetAgentState(pair.agent1);
                var state2 = rewardCalculator.GetAgentState(pair.agent2);
                float pairReward = rewardCalculator.CalculateStepReward(
                    state1, state2, enemyShips, pair.webObject);
                totalStepReward += pairReward;

                // 개별 보상: 헤딩 정렬 (배정 타겟 기반)
                GameObject target1 = pair.agent1.GetAssignedEnemy();
                GameObject target2 = pair.agent2.GetAssignedEnemy();
                float heading1 = rewardCalculator.CalculateIndividualHeadingReward(state1, enemyShips, target1);
                float heading2 = rewardCalculator.CalculateIndividualHeadingReward(state2, enemyShips, target2);
                if (heading1 > 0f) pair.agent1.AddReward(heading1);
                if (heading2 > 0f) pair.agent2.AddReward(heading2);
            }

            // Inspector 모니터링
            _lastStepReward = totalStepReward;
            _currentEpisodeReward += totalStepReward;
            _currentStep = _resetTimer;
            _totalCollisions = _totalCollisionCount;

            // 그룹 보상 부여 (전 페어 합산)
            if (Mathf.Abs(totalStepReward) > 0.0001f)
            {
                if (m_AgentGroup != null)
                {
                    m_AgentGroup.AddGroupReward(totalStepReward);
                }
                else
                {
                    foreach (var agent in AllDefenseAgents)
                        agent.AddReward(totalStepReward / Mathf.Max(1, defensePairs.Count * 2));
                }
            }
        }

        #region 중앙 허브: 통합 에피소드 재시작 로직
        
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
            for (int pi = 0; pi < defensePairs.Count; pi++)
            {
                var pair = defensePairs[pi];
                if (pair.agent1 != null && pair.agent2 != null)
                {
                    float dist = Vector3.Distance(pair.agent1.transform.position, pair.agent2.transform.position);
                    detail += $" | pair{pi}Dist={dist:F1}m";
                }
            }
            Debug.LogWarning($"[DefenseEnv] ★ EPISODE END ★ reason={reason}, step={_resetTimer}, reward={finalReward}, ep={_episodeNumber}{detail}");

            // 충돌 횟수 초기화 (에피소드 종료 시 즉시 리셋)
            _totalCollisionCount = 0;
            _collisionCooldownTimes.Clear();

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
                    foreach (var agent in AllDefenseAgents)
                        agent.AddReward(finalReward.Value);
                }
            }

            // 3단계: Commander 보상 공유 + 에피소드 종료
            if (commanderAgent != null)
            {
                if (finalReward.HasValue)
                    commanderAgent.AddReward(finalReward.Value);
                commanderAgent.EndEpisode();
            }

            // 4단계: Executor 에피소드 종료
            if (m_AgentGroup != null)
            {
                m_AgentGroup.EndGroupEpisode();
            }
            else
            {
                foreach (var agent in AllDefenseAgents)
                    agent.EndEpisode();
            }

            // 5단계: 페어 런타임 상태 초기화
            foreach (var pair in defensePairs)
            {
                pair.assignedTargetIndex = -1;
                pair.spawnAttemptsLeft = 3;
            }

            // 6단계: 환경 리셋
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
            
            // 기존 리셋 코루틴 중지 및 플래그 초기화
            StopAllCoroutines();
            _isResettingPositions = false;
            _lastResetFrame = -1; // 프레임 체크 초기화

            // 에피소드 종료 플래그를 먼저 리셋하여 다음 FixedUpdate()에서 타이머가 증가하지 않도록 함
            _episodeEnding = false;

            // 에피소드는 코루틴 완료 후 활성화 (초기에는 false로 시작)
            _episodeActive = false;

            _resetTimer = 0;

            // 파괴된 적군 선박 목록 및 노이즈 시드 초기화 (에피소드 재시작 시)
            _destroyedAttackBoatNames.Clear();
            _enemyNoiseSeed.Clear();

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

            // 적군 경로 웨이포인트 랜덤화 (선박 리셋 전에 호출)
            RandomizeEnemyWaypoints();

            // attack_boat의 대기 중인 Invoke 취소 (폭발 등)
            CancelAttackBoatPendingActions();

            // 모든 선박 리셋
            ResetPositionsOnly();
            
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
            // 에피소드가 종료 중이면 무시
            if (_episodeEnding)
                return;

            RestartEpisode("EnemyCaptured", rewardCalculator.captureReward);
        }
        
        /// <summary>
        /// 적군이 Web에 충돌 시 처리 - 적군과 아군 모두 원점으로 리셋
        /// 충돌 횟수가 maxCollisionCount 이상이면 에피소드 종료
        /// </summary>
        public void OnEnemyHitWeb(GameObject enemyBoat)
        {
            // 에피소드가 종료 중이면 무시
            if (_episodeEnding)
                return;

            // 스폰 직후 유예기간에는 충돌 무시
            if (_resetTimer <= 10)
                return;

            if (enemyBoat == null)
                return;
            
            // 중복 충돌 방지: 같은 적군 선박이 쿨다운 시간 내에 다시 충돌하면 무시
            float currentTime = Time.time;
            if (_collisionCooldownTimes.ContainsKey(enemyBoat))
            {
                float lastCollisionTime = _collisionCooldownTimes[enemyBoat];
                if (currentTime - lastCollisionTime < _collisionCooldown)
                {
                    return;
                }
            }

            // 충돌 시간 기록
            _collisionCooldownTimes[enemyBoat] = currentTime;

            // 통합 충돌 횟수 증가 (Web + MotherShip 합산)
            _totalCollisionCount++;

            float reward = rewardCalculator.captureReward;

            // 통합 충돌 횟수가 maxCollisionCount 이상이면 에피소드 종료
            if (_totalCollisionCount >= maxCollisionCount)
            {
                RestartEpisode("WebCollisionLimit", reward);
                return;
            }

            // 충돌 횟수가 maxCollisionCount 미만이면 보상 부여 후 해당 적군만 리셋
            if (m_AgentGroup != null)
            {
                m_AgentGroup.AddGroupReward(reward);
            }
            else
            {
                foreach (var agent in AllDefenseAgents)
                    agent.AddReward(reward);
            }

            // 적군 선박을 원점으로 리셋 (모선 충돌과 동일한 메커니즘 사용)
            ResetSingleAttackBoat(enemyBoat);

            // 아군 선박 위치 리셋은 에피소드 종료 시에만 수행 (mid-episode 리셋 비활성화)
            // ResetDefenseAgentsToOrigin();
            
            // WebDetector 리셋
            if (_webDetector != null)
            {
                _webDetector.ResetDetector();
            }
        }

        /// <summary>
        /// 아군 선박이 Web과 충돌 시 처리 (페널티 + 에피소드 종료)
        /// </summary>
        public void OnAllyHitWeb(GameObject allyShip)
        {
            if (_episodeEnding)
                return;

            // 스폰 직후 유예기간에는 충돌 무시 (Web 재배치 전 겹침 방지)
            if (_resetTimer <= 10)
                return;

            // 페널티 부여 후 에피소드 종료 (인스펙터에서 조절 가능)
            RestartEpisode("AllyHitWeb", allyWebCollisionPenalty);
        }

        /// <summary>
        /// 아군 선박들을 원점으로 리셋 (모든 페어)
        /// </summary>
        private void ResetDefenseAgentsToOrigin()
        {
            foreach (var pair in defensePairs)
            {
                // 페어 내 두 선박이 동일한 랜덤 각도와 위치 오프셋 공유
                float sharedRandomAngle = enableRandomSpawn
                    ? Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange)
                    : 0f;
                Vector3 sharedOffset = GetSharedRandomOffset();

                ResetDefenseAgentPosition(pair.agent1, pair.originalPos1, pair.originalRot1, sharedRandomAngle, sharedOffset);
                ResetDefenseAgentPosition(pair.agent2, pair.originalPos2, pair.originalRot2, sharedRandomAngle, sharedOffset);
            }
        }

        /// <summary>
        /// 모선 충돌 처리 - 해당 공격선만 원점으로 리셋
        /// 충돌 횟수가 maxCollisionCount 이상이면 에피소드 종료
        /// </summary>
        public void OnMotherShipCollision(GameObject enemyBoat)
        {
            // 에피소드가 종료 중이면 무시
            if (_episodeEnding)
                return;

            // 스폰 직후 유예기간에는 충돌 무시
            if (_resetTimer <= 10)
                return;

            if (enemyBoat == null)
                return;

            // 중복 충돌 방지: 같은 적군 선박이 쿨다운 시간 내에 다시 충돌하면 무시
            float currentTime = Time.time;
            if (_collisionCooldownTimes.ContainsKey(enemyBoat))
            {
                float lastCollisionTime = _collisionCooldownTimes[enemyBoat];
                if (currentTime - lastCollisionTime < _collisionCooldown)
                {
                    return;
                }
            }

            // 충돌 시간 기록
            _collisionCooldownTimes[enemyBoat] = currentTime;

            // 통합 충돌 횟수 증가 (Web + MotherShip 합산)
            _totalCollisionCount++;

            float penalty = rewardCalculator.motherShipHitPenalty;

            // 통합 충돌 횟수가 maxCollisionCount 이상이면 에피소드 종료
            if (_totalCollisionCount >= maxCollisionCount)
            {
                RestartEpisode("MotherShipCollisionLimit", penalty);
                return;
            }

            // 충돌 횟수 미만이면 페널티 부여 + 해당 공격선만 리셋
            if (m_AgentGroup != null)
            {
                m_AgentGroup.AddGroupReward(penalty);
            }
            else
            {
                foreach (var agent in AllDefenseAgents)
                    agent.AddReward(penalty);
            }

            ResetSingleAttackBoat(enemyBoat);
        }

        /// <summary>
        /// 단일 공격선을 원점으로 리셋 (비활성화 없이 위치만 리셋 - Water System 호환)
        /// </summary>
        private void ResetSingleAttackBoat(GameObject attackBoat)
        {
            if (attackBoat == null)
                return;

            string boatName = attackBoat.name.Replace("(Clone)", "");

            // 1. Rigidbody 속도 초기화 + Sleep (비활성화 없이)
            Rigidbody rb = attackBoat.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.Sleep();  // 물리 시뮬레이션 일시 정지
            }

            // 2. 원점 위치로 이동
            Vector3 initialPos = Vector3.zero;
            if (_attackBoatInitialPositionsByName.ContainsKey(boatName))
            {
                initialPos = _attackBoatInitialPositionsByName[boatName];
            }
            else if (_attackBoatInitialPositions.ContainsKey(attackBoat))
            {
                initialPos = _attackBoatInitialPositions[attackBoat];
            }

            attackBoat.transform.position = initialPos;
            attackBoat.transform.rotation = Quaternion.identity;

            // Engine 리셋 (Gerstner 파도 안정화 전까지 _yHeight 조건 무시)
            var boat = attackBoat.GetComponent<Boat>();
            if (boat != null && boat.engine != null)
            {
                boat.engine.OnEpisodeReset();
            }

            // 3. Cinemachine Dolly Cart 리셋 (랜덤 경로 할당 또는 원래 경로 복원)
            Cinemachine.CinemachineDollyCart dollyCart = attackBoat.GetComponent<Cinemachine.CinemachineDollyCart>();
            if (dollyCart != null)
            {
                CinemachinePathBase assignedPath = null;

                if (enableRandomPathAssignment)
                {
                    assignedPath = GetRandomAttackPath();
                }

                // 랜덤 경로가 없으면 원래 경로 fallback
                if (assignedPath == null && _attackBoatInitialPathsByName.ContainsKey(boatName))
                {
                    assignedPath = _attackBoatInitialPathsByName[boatName];
                }

                if (assignedPath != null)
                {
                    dollyCart.m_Path = assignedPath;
                }
                dollyCart.m_Position = 0f;
            }
        }
        
        /// <summary>
        /// 아군 충돌 처리 (아군-아군 또는 아군-모선 충돌)
        /// 에피소드 종료 + 페널티 부여
        /// </summary>
        public void OnFriendlyCollision()
        {
            // 에피소드가 종료 중이면 무시
            if (_episodeEnding)
                return;

            // 스폰 직후 유예기간에는 충돌 무시 (물리 안정화 대기)
            if (_resetTimer <= 10)
                return;

            // RestartEpisode를 통해 일관된 방식으로 에피소드 종료
            // 패널티 부여 + EndGroupEpisode + ResetScene 모두 처리됨
            RestartEpisode("FriendlyCollision", rewardCalculator.collisionPenalty);
        }
        
        #region DefenseBoatManager 통합 기능
        
        /// <summary>
        /// 적군 선박 파괴 요청 (DynamicWeb, WebCollisionDetector에서 호출)
        /// 중앙 허브: 모든 파괴 로직을 중앙에서 관리
        /// </summary>
        public void RequestAttackBoatDestruction(GameObject attackBoat)
        {
            if (attackBoat == null)
                return;
            
            // 이미 파괴 요청이 처리되었는지 확인
            string boatName = attackBoat.name.Replace("(Clone)", "");
            if (_destroyedAttackBoatNames.Contains(boatName))
            {
                return;
            }
            
            // 다음 프레임에 파괴 처리 (물리 콜백 제약 회피)
            StartCoroutine(DestroyAttackBoatNextFrame(attackBoat));
        }
        
        /// <summary>
        /// 다음 프레임에 attack_boat 파괴 처리 (물리 콜백 제약 회피)
        /// </summary>
        private System.Collections.IEnumerator DestroyAttackBoatNextFrame(GameObject attackBoat)
        {
            // 다음 프레임까지 대기 (물리 콜백이 끝난 후)
            yield return null;
            
            if (attackBoat == null)
            {
                yield break;
            }
            
            // 파괴 처리
            OnAttackBoatDestroyed(attackBoat);

            // Destroy 대신 SetActive(false) 사용 (재활용 및 Water System 호환)
            attackBoat.SetActive(false);
        }
        
        /// <summary>
        /// 적군 선박이 파괴되었을 때 호출 (내부에서만 호출)
        /// 중앙 허브: 모든 파괴 정보를 중앙에서 관리
        /// </summary>
        private void OnAttackBoatDestroyed(GameObject destroyedBoat)
        {
            if (destroyedBoat == null)
                return;

            // 스폰 직후 유예기간에는 파괴 이벤트 무시
            if (_resetTimer <= 10)
            {
                Debug.Log($"[DefenseEnv] OnAttackBoatDestroyed 유예기간 무시: {destroyedBoat.name}, step={_resetTimer}");
                return;
            }

            Debug.Log($"[DefenseEnv] OnAttackBoatDestroyed: {destroyedBoat.name}, step={_resetTimer}");
            
            // 파괴된 선박의 이름 저장 (파괴 후에도 추적 가능하도록)
            string boatName = destroyedBoat.name.Replace("(Clone)", "");
            _destroyedAttackBoatNames.Add(boatName);
            
            // 초기 적군 선박 수가 0이면 다시 찾기 (Start()에서 찾지 못했을 수 있음)
            if (_initialAttackBoatCount == 0)
            {
                FindAndSaveAttackBoats();
            }
            
            // 파괴된 선박을 리스트에서 제거
            _attackBoats.Remove(destroyedBoat);
            
            // null이거나 파괴된 객체 제거
            _attackBoats.RemoveAll(boat => boat == null);
            
            // 현재 활성화된 적군 선박 수 확인 (환경 내에서 직접 찾기, 파괴된 선박 제외)
            GameObject[] allAttackBoatsInScene = FindGameObjectsWithTagInEnvironment("attack_boat");
            // 파괴된 선박 이름을 기준으로 제외
            int activeCount = allAttackBoatsInScene.Count(boat => 
                boat != null && 
                !_destroyedAttackBoatNames.Contains(boat.name.Replace("(Clone)", ""))
            );
            
            // _attackBoats 리스트에서 활성화된 선박 수 확인
            int activeInList = _attackBoats.Count(boat => 
                boat != null && 
                boat.activeSelf && 
                !_destroyedAttackBoatNames.Contains(boat.name.Replace("(Clone)", ""))
            );
            
            // 모든 적군 선박이 파괴되었는지 확인
            // 조건: 초기 적군 수가 0보다 크고, 씬에 활성화된 적군이 0개이고, 리스트에도 활성화된 선박이 0개
            if (endEpisodeOnAllEnemiesDestroyed && _initialAttackBoatCount > 0 && activeCount == 0 && activeInList == 0)
            {
                // 에피소드가 이미 종료 중인 경우 중복 호출 방지
                if (_episodeEnding)
                {
                    return;
                }
                
                // 통합 에피소드 재시작 메서드 호출
                RestartEpisode("AllEnemiesDestroyed");
            }
        }
        
        /// <summary>
        /// attack_boat 태그를 가진 모든 적군 선박 찾기 및 초기 위치 저장
        /// 중앙 허브: 모든 적군 선박 정보를 중앙에서 관리
        /// </summary>
        private void FindAndSaveAttackBoats()
        {
            _attackBoats.Clear();
            _attackBoatInitialPositions.Clear();
            _attackBoatInitialPaths.Clear();
            // 프리팹 딕셔너리와 이름 기반 딕셔너리는 초기화하지 않음 (에피소드 재시작 시 재사용)
            
            // 현재 환경 내의 attack_boat 태그를 가진 객체 찾기 (멀티 환경 호환)
            GameObject[] foundBoats = FindGameObjectsWithTagInEnvironment("attack_boat");
            
            // enemyShips 배열 자동 동기화 (인스펙터에 할당된 것과 씬의 실제 객체를 동기화)
            if (enemyShips == null || enemyShips.Length == 0 || enemyShips.All(e => e == null))
            {
                enemyShips = new GameObject[foundBoats.Length];
                for (int i = 0; i < foundBoats.Length; i++)
                {
                    enemyShips[i] = foundBoats[i];
                }
            }
            
            foreach (var boat in foundBoats)
            {
                if (boat != null && !_attackBoats.Contains(boat))
                {
                    _attackBoats.Add(boat);
                    Vector3 initialPos = boat.transform.position;
                    _attackBoatInitialPositions[boat] = initialPos;

                    // 원본 객체를 프리팹으로 저장 (파괴 후 재생성용)
                    // 이름을 키로 사용하여 같은 이름의 객체를 재생성할 수 있도록 함
                    string boatName = boat.name.Replace("(Clone)", ""); // Clone 접미사 제거
                    
                    // 이름 기반 초기 위치 저장 (재생성용)
                    if (!_attackBoatInitialPositionsByName.ContainsKey(boatName))
                    {
                        _attackBoatInitialPositionsByName[boatName] = initialPos;
                    }
                    
                    if (!_attackBoatPrefabs.ContainsKey(boatName))
                    {
                        // 원본 객체를 복제하여 프리팹으로 저장 (씬에 숨김)
                        GameObject prefabCopy = Instantiate(boat);
                        prefabCopy.name = boatName; // Clone 접미사 제거
                        prefabCopy.SetActive(false); // 비활성화하여 숨김
                        prefabCopy.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave; // Hierarchy에서 숨기고 저장하지 않음
                        _attackBoatPrefabs[boatName] = prefabCopy;
                    }
                    
                    // Cinemachine Dolly Cart의 Path 저장
                    CinemachineDollyCart dollyCart = boat.GetComponent<CinemachineDollyCart>();
                    if (dollyCart != null)
                    {
                        _attackBoatInitialPaths[boat] = dollyCart.m_Path;
                        // 이름 기반 경로 저장 (재생성용)
                        if (!_attackBoatInitialPathsByName.ContainsKey(boatName))
                        {
                            _attackBoatInitialPathsByName[boatName] = dollyCart.m_Path;
                        }
                    }
                    else
                    {
                        _attackBoatInitialPaths[boat] = null;
                        if (!_attackBoatInitialPathsByName.ContainsKey(boatName))
                        {
                            _attackBoatInitialPathsByName[boatName] = null;
                        }
                    }
                }
            }
            
            _initialAttackBoatCount = _attackBoats.Count;
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

            // 모든 WAKE 객체 제거 및 WakeGenerator 비활성화
            DestroyAllWakeObjects();

            if (useDynamicSpawn && motherShip != null)
            {
                // ========================================
                // 동적 스폰: 모선 기준 적군/아군 배치
                // ========================================
                Vector3 motherPos = motherShip.transform.position;

                // 1. 적군 접근 각도 랜덤 결정 (0~360)
                _currentEnemyApproachAngle = Random.Range(0f, 360f);
                float angleRad = _currentEnemyApproachAngle * Mathf.Deg2Rad;
                Vector3 enemyDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));

                // 2. 적군 스폰 (모선에서 enemySpawnDistance 거리, 모선 바라봄)
                ResetAttackBoatsDynamic(motherPos, enemyDir);

                // 3. 각 페어별 아군 스폰 (팬형 배치)
                Vector3 defenseDir = enemyDir;
                _currentPerpendicularDir = RotateXZ(defenseDir, Mathf.PI / 2f);

                for (int pi = 0; pi < defensePairs.Count; pi++)
                {
                    var pair = defensePairs[pi];
                    if (pair.agent1 == null || pair.agent2 == null) continue;

                    // 페어별 팬형 각도 분배 (페어가 1개면 0°, 여러 개면 균등 간격)
                    float pairAngleOffset = 0f;
                    if (defensePairs.Count > 1)
                    {
                        float totalFanAngle = defenseSpawnSpread * (defensePairs.Count - 1) * 2f;
                        pairAngleOffset = -totalFanAngle / 2f + pi * (totalFanAngle / (defensePairs.Count - 1));
                    }
                    float pairAngleRad = pairAngleOffset * Mathf.Deg2Rad;
                    Vector3 pairDir = RotateXZ(defenseDir, pairAngleRad);

                    // 페어 내 2척: 기존 spread 방식
                    float spreadRad1 = -defenseSpawnSpread * Mathf.Deg2Rad;
                    float spreadRad2 = defenseSpawnSpread * Mathf.Deg2Rad;

                    Vector3 dir1 = RotateXZ(pairDir, spreadRad1);
                    Vector3 spawnPos1 = motherPos + dir1 * defenseSpawnDistance;
                    spawnPos1.y = pair.originalPos1.y;
                    Quaternion rot1 = Quaternion.LookRotation(defenseDir, Vector3.up);

                    Vector3 dir2 = RotateXZ(pairDir, spreadRad2);
                    Vector3 spawnPos2 = motherPos + dir2 * defenseSpawnDistance;
                    spawnPos2.y = pair.originalPos2.y;
                    Quaternion rot2 = rot1;

                    if (enableRandomSpawn)
                    {
                        float jitter1 = Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange);
                        float jitter2 = Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange);
                        rot1 *= Quaternion.Euler(0f, jitter1, 0f);
                        rot2 *= Quaternion.Euler(0f, jitter2, 0f);
                    }

                    ResetDefenseAgentDirect(pair.agent1, spawnPos1, rot1);
                    ResetDefenseAgentDirect(pair.agent2, spawnPos2, rot2);

                    // 수직축 기반 위치 교차 판별
                    float perpDot1 = Vector3.Dot(spawnPos1 - motherPos, _currentPerpendicularDir);
                    float perpDot2 = Vector3.Dot(spawnPos2 - motherPos, _currentPerpendicularDir);
                    bool startsOnLeft = perpDot1 < perpDot2;
                    while (_pairAgent1StartsOnLeft.Count <= pi) _pairAgent1StartsOnLeft.Add(false);
                    _pairAgent1StartsOnLeft[pi] = startsOnLeft;

                    // 레거시 호환 (pair[0])
                    if (pi == 0) _agent1StartsOnLeft = startsOnLeft;
                }

                Debug.Log($"[DefenseEnv] DynamicSpawn: enemyAngle={_currentEnemyApproachAngle:F0}°, pairs={defensePairs.Count}");
            }
            else
            {
                // ========================================
                // 레거시 스폰: 기존 방식 (고정 위치 + 오프셋)
                // ========================================
                for (int pi = 0; pi < defensePairs.Count; pi++)
                {
                    var pair = defensePairs[pi];
                    float sharedRandomAngle = enableRandomSpawn
                        ? Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange)
                        : 0f;
                    Vector3 sharedOffset = GetSharedRandomOffset();

                    ResetDefenseAgentPosition(pair.agent1, pair.originalPos1, pair.originalRot1, sharedRandomAngle, sharedOffset);
                    ResetDefenseAgentPosition(pair.agent2, pair.originalPos2, pair.originalRot2, sharedRandomAngle, sharedOffset);
                }

                ResetAttackBoatsToOrigin();
            }

            // 아군 선박 좌/우 위치 기록 (위치 교차 감지용) - 레거시 스폰
            if (!useDynamicSpawn)
            {
                for (int pi = 0; pi < defensePairs.Count; pi++)
                {
                    var pair = defensePairs[pi];
                    if (pair.agent1 == null || pair.agent2 == null) continue;
                    Vector3 pos1 = pair.agent1.transform.position;
                    Vector3 pos2 = pair.agent2.transform.position;
                    bool startsOnLeft = pos1.x < pos2.x;
                    while (_pairAgent1StartsOnLeft.Count <= pi) _pairAgent1StartsOnLeft.Add(false);
                    _pairAgent1StartsOnLeft[pi] = startsOnLeft;
                    if (pi == 0) _agent1StartsOnLeft = startsOnLeft;
                }
            }

            // 모든 페어의 WebDetector 리셋
            foreach (var pair in defensePairs)
            {
                if (pair.webObject != null)
                {
                    var detector = pair.webObject.GetComponent<WebCollisionDetector>();
                    if (detector != null) detector.ResetDetector();
                }
            }
            if (_webDetector != null)
            {
                _webDetector.ResetDetector();
            }

            yield return null;

            // 코루틴 실행 완료 플래그 해제 및 에피소드 활성화
            _isResettingPositions = false;
            _episodeActive = true;
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
        /// 아군 에이전트를 지정 위치/각도로 직접 배치 (동적 스폰용)
        /// </summary>
        private void ResetDefenseAgentDirect(DefenseAgent agent, Vector3 position, Quaternion rotation)
        {
            if (agent == null) return;

            if (agent.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.Sleep();
            }

            agent.transform.position = position;
            agent.transform.rotation = rotation;

            if (agent._engine != null)
            {
                agent._engine.OnEpisodeReset();
            }
        }

        /// <summary>
        /// 적군 선박을 모선 기준 동적 위치로 스폰 (모선에서 enemySpawnDistance 거리, 모선 바라봄)
        /// </summary>
        private void ResetAttackBoatsDynamic(Vector3 motherPos, Vector3 enemyDir)
        {
            // null/파괴된 객체 제거
            _attackBoats.RemoveAll(boat => boat == null);

            // 파괴 기록 초기화 (새 에피소드이므로)
            _destroyedAttackBoatNames.Clear();

            int stageTargetCount = GetActiveEnemyCountForStage();
            if (stageTargetCount == 0)
            {
                // Stage1에서 적군 0대: 모든 적군 비활성화
                foreach (var boat in _attackBoats)
                {
                    if (boat != null) boat.SetActive(false);
                }
                UpdateEnemyShipsArray();
                return;
            }

            // 파괴된 적군 재생성 (기존 로직 재활용)
            EnsureAttackBoatCount(stageTargetCount);

            // 포메이션 기반 스폰 데이터 생성
            float baseY = (_attackBoats.Count > 0 && _attackBoats[0] != null)
                ? _attackBoats[0].transform.position.y : 0f;
            var formationData = EnemyFormationSpawner.Generate(
                enemyFormation, motherPos, stageTargetCount, enemySpawnDistance, baseY);

            // 활성 적군을 포메이션 위치로 배치
            int placedCount = 0;
            foreach (var boat in _attackBoats)
            {
                if (boat == null) continue;
                if (placedCount >= stageTargetCount)
                {
                    boat.SetActive(false);
                    continue;
                }

                // 먼저 모든 MonoBehaviour의 Invoke 취소 (이전 에피소드 잔여 Invoke 방지)
                var behaviours = boat.GetComponents<MonoBehaviour>();
                foreach (var mb in behaviours)
                {
                    if (mb != null) mb.CancelInvoke();
                }

                // 포메이션 데이터에서 위치/회전 가져오기
                Vector3 spawnPos;
                Quaternion spawnRot;
                if (placedCount < formationData.Count)
                {
                    spawnPos = formationData[placedCount].position;
                    spawnRot = formationData[placedCount].rotation;
                }
                else
                {
                    // fallback: 기존 방식 (spread offset)
                    Vector3 spreadOffset = new Vector3(
                        Random.Range(-enemySpawnSpread, enemySpawnSpread), 0f,
                        Random.Range(-enemySpawnSpread, enemySpawnSpread));
                    spawnPos = motherPos + enemyDir * enemySpawnDistance + spreadOffset;
                    spawnPos.y = boat.transform.position.y;
                    Vector3 lookDir = motherPos - spawnPos;
                    lookDir.y = 0f;
                    spawnRot = lookDir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(lookDir, Vector3.up)
                        : Quaternion.identity;
                }

                // 위치/회전을 먼저 설정 (SetActive 전에! 이전 위치에서 충돌 방지)
                boat.transform.position = spawnPos;
                boat.transform.rotation = spawnRot;

                // Rigidbody 리셋
                Rigidbody rb = boat.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // 위치 설정 후 활성화 (이전 위치에서의 충돌 방지)
                boat.SetActive(true);

                // 활성화 후 rb.Sleep() (Sleep은 활성 상태에서만 유효)
                if (rb != null)
                {
                    rb.Sleep();
                }

                // CinemachineDollyCart 비활성화 (동적 스폰에서는 경로 추적 안 함)
                CinemachineDollyCart dollyCart = boat.GetComponent<CinemachineDollyCart>();
                if (dollyCart != null)
                {
                    dollyCart.enabled = false;
                }

                // AttackAgent 모선 추적 모드 설정
                var attackAgent = boat.GetComponent<AttackAgent>();
                if (attackAgent != null)
                {
                    attackAgent.followWaypoints = false;
                    attackAgent.targetMotherShip = motherShip;
                }

                // AttackBoatDisabler 폭발 상태 초기화 (이전 에피소드 잔여 상태 제거)
                var disabler = boat.GetComponent<AttackBoatDisabler>();
                if (disabler != null)
                {
                    disabler.CancelInvoke(); // 대기 중인 DisableBoat Invoke 취소
                }

                // Engine 리셋 (Gerstner 파도 안정화)
                var boatComp = boat.GetComponent<Boat>();
                if (boatComp != null && boatComp.engine != null)
                {
                    boatComp.engine.OnEpisodeReset();
                }

                placedCount++;
            }

            Debug.Log($"[DefenseEnv] ResetAttackBoatsDynamic: placed={placedCount}/{stageTargetCount}, total={_attackBoats.Count}");

            // enemyShips 배열 업데이트 (DefenseAgent 관측용)
            UpdateEnemyShipsArray();
        }

        /// <summary>
        /// 적군 선박 수가 목표 수에 미달하면 재생성
        /// </summary>
        private void EnsureAttackBoatCount(int targetCount)
        {
            // 현재 활성 수
            int currentCount = 0;
            foreach (var boat in _attackBoats)
            {
                if (boat != null) currentCount++;
            }

            if (currentCount >= targetCount) return;

            // 부족한 만큼 재생성
            foreach (var kvp in _attackBoatPrefabs)
            {
                if (currentCount >= targetCount) break;

                string boatName = kvp.Key;
                GameObject prefab = kvp.Value;
                if (prefab == null) continue;

                // 이미 리스트에 있는지 확인
                bool exists = false;
                foreach (var boat in _attackBoats)
                {
                    if (boat != null && boat.name.Replace("(Clone)", "") == boatName)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    GameObject recreated = Instantiate(prefab, GetEnvironmentRoot());
                    recreated.name = boatName;
                    recreated.tag = "attack_boat";
                    recreated.SetActive(true);
                    _attackBoats.Add(recreated);
                    currentCount++;
                }
            }
        }

        /// <summary>
        /// enemyShips 배열을 현재 활성 attack_boat로 업데이트
        /// </summary>
        private void UpdateEnemyShipsArray()
        {
            var activeEnemies = new System.Collections.Generic.List<GameObject>();
            foreach (var boat in _attackBoats)
            {
                if (boat != null && boat.activeSelf)
                    activeEnemies.Add(boat);
            }

            enemyShips = activeEnemies.ToArray();

            // 모든 페어의 에이전트에 적군 배열 전달
            foreach (var agent in AllDefenseAgents)
                agent.enemyShips = enemyShips;
        }

        /// <summary>
        /// 모든 적군 선박 (attack_boat 태그)을 원점으로 리셋
        /// 에피소드 재시작 시 호출되며, 파괴된 적군 선박도 다시 생성
        /// Stage 설정에 따라 재생성 수 제한
        /// </summary>
        private void ResetAttackBoatsToOrigin()
        {
            // null이거나 파괴된 객체 제거
            _attackBoats.RemoveAll(boat => boat == null);

            // Stage에 따른 목표 적군 수 결정
            int stageTargetCount = GetActiveEnemyCountForStage();

            // Stage1에서 적군 0대면 재생성 스킵
            if (stageTargetCount == 0)
            {
                return;
            }

            // 현재 환경 내의 attack_boat 다시 찾기 (멀티 환경 호환)
            GameObject[] allAttackBoatsInScene = FindGameObjectsWithTagInEnvironment("attack_boat");

            // 파괴된 attack_boat 재생성
            int recreatedCount = 0;

            // Stage 설정에 맞는 적군 수만큼만 재생성
            int targetCount = Mathf.Min(
                stageTargetCount,
                _initialAttackBoatCount > 0 ? _initialAttackBoatCount : _attackBoatPrefabs.Count
            );
            
            foreach (var kvp in _attackBoatPrefabs)
            {
                string boatName = kvp.Key;
                GameObject prefab = kvp.Value;
                
                if (prefab == null) continue;
                
                // 씬에서 같은 이름의 객체를 찾을 수 있는지 확인
                bool foundInScene = false;
                GameObject existingBoat = null;
                foreach (var boat in allAttackBoatsInScene)
                {
                    if (boat == null) continue;
                    string sceneBoatName = boat.name.Replace("(Clone)", "");
                    if (sceneBoatName == boatName)
                    {
                        foundInScene = true;
                        existingBoat = boat;
                        break;
                    }
                }
                
                // 씬에 없으면 재생성 필요 (단, 목표 수 초과 시 스킵)
                if (!foundInScene && recreatedCount < targetCount)
                {
                    // 프리팹에서 재생성 (환경 루트 아래에 배치하여 멀티 환경 호환)
                    GameObject recreatedBoat = Instantiate(prefab, GetEnvironmentRoot());
                    recreatedBoat.name = boatName; // 원본 이름 유지
                    recreatedBoat.tag = "attack_boat"; // 태그 설정
                    recreatedBoat.SetActive(true); // 활성화

                    // 초기 위치 찾기 (이름 기반 Dictionary에서 찾기)
                    Vector3 initialPos = Vector3.zero;
                    if (_attackBoatInitialPositionsByName.ContainsKey(boatName))
                    {
                        initialPos = _attackBoatInitialPositionsByName[boatName];
                    }
                    
                    recreatedBoat.transform.position = initialPos;
                    recreatedBoat.transform.rotation = Quaternion.identity;
                    
                    // Cinemachine Dolly Cart 리셋 (랜덤 경로 할당)
                    CinemachineDollyCart dollyCart = recreatedBoat.GetComponent<CinemachineDollyCart>();
                    if (dollyCart != null)
                    {
                        CinemachinePathBase assignedPath = enableRandomPathAssignment ? GetRandomAttackPath() : null;

                        if (assignedPath == null && _attackBoatInitialPathsByName.ContainsKey(boatName))
                        {
                            assignedPath = _attackBoatInitialPathsByName[boatName];
                        }

                        if (assignedPath != null)
                        {
                            dollyCart.m_Path = assignedPath;
                            dollyCart.m_Position = 0f;
                        }
                    }
                    
                    // Rigidbody 리셋
                    Rigidbody rb = recreatedBoat.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    
                    // 리스트에 추가
                    _attackBoats.Add(recreatedBoat);
                    _attackBoatInitialPositions[recreatedBoat] = initialPos;
                    if (dollyCart != null)
                    {
                        _attackBoatInitialPaths[recreatedBoat] = dollyCart.m_Path;
                    }
                    
                    // 이름 기반 Dictionary도 업데이트 (재생성된 객체용)
                    if (!_attackBoatInitialPositionsByName.ContainsKey(boatName))
                    {
                        _attackBoatInitialPositionsByName[boatName] = initialPos;
                    }
                    if (dollyCart != null && !_attackBoatInitialPathsByName.ContainsKey(boatName))
                    {
                        _attackBoatInitialPathsByName[boatName] = dollyCart.m_Path;
                    }
                    
                    recreatedCount++;
                }
            }
            
            // 씬의 모든 attack_boat를 _attackBoats 리스트에 추가 (없는 경우만)
            foreach (var boat in allAttackBoatsInScene)
            {
                if (boat != null && !_attackBoats.Contains(boat))
                {
                    _attackBoats.Add(boat);
                    
                    // 초기 위치/경로 저장 (아직 저장되지 않은 경우)
                    if (!_attackBoatInitialPositions.ContainsKey(boat))
                    {
                        Vector3 initialPos = boat.transform.position;
                        _attackBoatInitialPositions[boat] = initialPos;
                        
                        // 원본 프리팹 저장
                        string boatName = boat.name.Replace("(Clone)", "");
                        
                        // 이름 기반 초기 위치 저장
                        if (!_attackBoatInitialPositionsByName.ContainsKey(boatName))
                        {
                            _attackBoatInitialPositionsByName[boatName] = initialPos;
                        }
                        
                        if (!_attackBoatPrefabs.ContainsKey(boatName))
                        {
                            GameObject prefabCopy = Instantiate(boat);
                            prefabCopy.name = boatName;
                            prefabCopy.SetActive(false);
                            prefabCopy.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                            _attackBoatPrefabs[boatName] = prefabCopy;
                        }
                        
                        // Cinemachine Dolly Cart의 Path 저장
                        CinemachineDollyCart dollyCart = boat.GetComponent<CinemachineDollyCart>();
                        if (dollyCart != null)
                        {
                            _attackBoatInitialPaths[boat] = dollyCart.m_Path;
                            // 이름 기반 경로 저장
                            if (!_attackBoatInitialPathsByName.ContainsKey(boatName))
                            {
                                _attackBoatInitialPathsByName[boatName] = dollyCart.m_Path;
                            }
                        }
                        else
                        {
                            _attackBoatInitialPaths[boat] = null;
                            if (!_attackBoatInitialPathsByName.ContainsKey(boatName))
                            {
                                _attackBoatInitialPathsByName[boatName] = null;
                            }
                        }
                    }
                }
            }
            
            // 초기 선박 수 확인 및 업데이트
            // 재생성 후 총 선박 수가 초기 수보다 적으면 문제
            if (_attackBoats.Count < _initialAttackBoatCount && _initialAttackBoatCount > 0)
            {
                // 부족한 만큼 더 재생성 시도
                int missingCount = _initialAttackBoatCount - _attackBoats.Count;
                int additionalRecreated = 0;
                
                foreach (var kvp in _attackBoatPrefabs)
                {
                    if (additionalRecreated >= missingCount) break;
                    
                    string boatName = kvp.Key;
                    GameObject prefab = kvp.Value;
                    
                    if (prefab == null) continue;
                    
                    // 이미 리스트에 있는지 확인
                    bool alreadyInList = false;
                    foreach (var boat in _attackBoats)
                    {
                        if (boat != null)
                        {
                            string listBoatName = boat.name.Replace("(Clone)", "");
                            if (listBoatName == boatName)
                            {
                                alreadyInList = true;
                                break;
                            }
                        }
                    }
                    
                    if (!alreadyInList)
                    {
                        // 재생성 (환경 루트 아래에 배치하여 멀티 환경 호환)
                        GameObject recreatedBoat = Instantiate(prefab, GetEnvironmentRoot());
                        recreatedBoat.name = boatName;
                        recreatedBoat.tag = "attack_boat";
                        recreatedBoat.SetActive(true);
                        
                        Vector3 initialPos = _attackBoatInitialPositionsByName.ContainsKey(boatName) 
                            ? _attackBoatInitialPositionsByName[boatName] 
                            : Vector3.zero;
                        
                        recreatedBoat.transform.position = initialPos;
                        recreatedBoat.transform.rotation = Quaternion.identity;
                        
                        CinemachineDollyCart dollyCart = recreatedBoat.GetComponent<CinemachineDollyCart>();
                        if (dollyCart != null)
                        {
                            CinemachinePathBase assignedPath = enableRandomPathAssignment ? GetRandomAttackPath() : null;

                            if (assignedPath == null && _attackBoatInitialPathsByName.ContainsKey(boatName))
                            {
                                assignedPath = _attackBoatInitialPathsByName[boatName];
                            }

                            if (assignedPath != null)
                            {
                                dollyCart.m_Path = assignedPath;
                                dollyCart.m_Position = 0f;
                            }
                        }
                        
                        Rigidbody rb = recreatedBoat.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                        }
                        
                        _attackBoats.Add(recreatedBoat);
                        _attackBoatInitialPositions[recreatedBoat] = initialPos;
                        if (dollyCart != null)
                        {
                            _attackBoatInitialPaths[recreatedBoat] = dollyCart.m_Path;
                        }
                        
                        additionalRecreated++;
                    }
                }
                
                recreatedCount += additionalRecreated;
            }
            
            // 초기 선박 수 업데이트 (씬에 있는 선박 수가 더 많으면)
            if (_attackBoats.Count > _initialAttackBoatCount)
            {
                _initialAttackBoatCount = _attackBoats.Count;
            }
            
            // 모든 선박을 원점(초기 위치)으로 리셋 (비활성화/활성화 없이 - Water System 호환)
            int resetCount = 0;
            foreach (var boat in _attackBoats)
            {
                if (boat == null) continue;

                // 비활성화된 선박은 건너뛰지 않고 위치만 리셋
                // (SetActive 호출하지 않음 - Water System Dictionary 충돌 방지)

                // Rigidbody 리셋 (활성화 상태에서만 동작)
                if (boat.activeSelf)
                {
                    Rigidbody rb = boat.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                }

                // 초기 위치로 리셋 (이름 기반으로 찾기)
                Vector3 spawnPos;
                string boatName = boat.name.Replace("(Clone)", "");

                if (_attackBoatInitialPositionsByName.ContainsKey(boatName))
                {
                    spawnPos = _attackBoatInitialPositionsByName[boatName];
                    if (!_attackBoatInitialPositions.ContainsKey(boat))
                    {
                        _attackBoatInitialPositions[boat] = spawnPos;
                    }
                }
                else if (_attackBoatInitialPositions.ContainsKey(boat))
                {
                    spawnPos = _attackBoatInitialPositions[boat];
                    _attackBoatInitialPositionsByName[boatName] = spawnPos;
                }
                else
                {
                    spawnPos = boat.transform.position;
                    _attackBoatInitialPositions[boat] = spawnPos;
                    _attackBoatInitialPositionsByName[boatName] = spawnPos;
                }

                // Cinemachine Dolly Cart 리셋 (랜덤 경로 할당 또는 원래 경로 복원)
                CinemachineDollyCart dollyCart = boat.GetComponent<CinemachineDollyCart>();
                if (dollyCart != null)
                {
                    CinemachinePathBase assignedPath = null;

                    if (enableRandomPathAssignment)
                    {
                        assignedPath = GetRandomAttackPath();
                    }

                    // 랜덤 경로가 없으면 원래 경로 fallback
                    if (assignedPath == null)
                    {
                        if (_attackBoatInitialPathsByName.ContainsKey(boatName))
                        {
                            assignedPath = _attackBoatInitialPathsByName[boatName];
                        }
                        else if (_attackBoatInitialPaths.ContainsKey(boat) && _attackBoatInitialPaths[boat] != null)
                        {
                            assignedPath = _attackBoatInitialPaths[boat];
                            _attackBoatInitialPathsByName[boatName] = assignedPath;
                        }
                    }

                    if (assignedPath != null)
                    {
                        dollyCart.m_Path = assignedPath;
                        dollyCart.m_Position = 0f;

                        if (dollyCart.m_Speed < 0)
                        {
                            dollyCart.m_Speed = Mathf.Abs(dollyCart.m_Speed);
                        }

                        Vector3 pathStartPos = assignedPath.EvaluatePositionAtUnit(0f, CinemachinePathBase.PositionUnits.PathUnits);
                        boat.transform.position = pathStartPos;
                    }
                    else
                    {
                        boat.transform.position = spawnPos;
                    }
                }
                else
                {
                    // Dolly Cart가 없으면 저장된 위치 사용
                    boat.transform.position = spawnPos;
                }

                boat.transform.rotation = Quaternion.identity;

                resetCount++;
            }
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
        /// 위치 리셋 - 아군 선박은 비활성화 없이 위치만 리셋 (모든 페어)
        /// </summary>
        private void ResetPositions()
        {
            DestroyAllWakeObjects();

            foreach (var pair in defensePairs)
            {
                float sharedAngle = enableRandomSpawn
                    ? Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange)
                    : 0f;
                Vector3 sharedOffset = GetSharedRandomOffset();

                ResetDefenseAgentPosition(pair.agent1, pair.originalPos1, pair.originalRot1, sharedAngle, sharedOffset);
                ResetDefenseAgentPosition(pair.agent2, pair.originalPos2, pair.originalRot2, sharedAngle, sharedOffset);

                // Web 위치 설정 (페어 2대 중간)
                if (pair.webObject != null && pair.agent1 != null && pair.agent2 != null)
                {
                    Vector3 webPos = (pair.agent1.transform.position + pair.agent2.transform.position) / 2f;
                    webPos.y = webSpawnPos.y;
                    pair.webObject.transform.position = webPos;
                }
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
            Vector3 motherPos = motherShip.transform.position;

            foreach (var boat in _attackBoats)
            {
                if (boat == null || !boat.activeSelf) continue;

                var boatComp = boat.GetComponent<Boat>();
                if (boatComp == null || boatComp.engine == null) continue;

                Engine engine = boatComp.engine;
                if (engine.RB == null) continue;

                // 노이즈 시드 (최초 한 번만 생성)
                if (!_enemyNoiseSeed.ContainsKey(boat))
                {
                    _enemyNoiseSeed[boat] = UnityEngine.Random.Range(0f, 1000f);
                }

                // 모선 방향 계산
                Vector3 toMother = motherPos - boat.transform.position;
                toMother.y = 0f;
                if (toMother.sqrMagnitude < 0.01f) continue;

                float angleToMother = Vector3.SignedAngle(boat.transform.forward, toMother.normalized, Vector3.up);
                float baseSteering = Mathf.Clamp(angleToMother / 45f, -1f, 1f);

                // Perlin 노이즈 (각 적군 다른 패턴)
                float seed = _enemyNoiseSeed[boat];
                float noise = (Mathf.PerlinNoise(seed, Time.time * enemyNoiseSpeed) - 0.5f) * 2f * enemySteeringNoise;

                float steering = Mathf.Clamp(baseSteering + noise, -1f, 1f);

                // Engine.Accelerate()는 0~1 클램프 → 적군은 직접 AddForce로 속도 배율 적용
                var forward = engine.RB.transform.forward;
                forward.y = 0f;
                forward.Normalize();
                if (float.IsNaN(forward.x)) forward = Vector3.forward;
                engine.RB.AddForce(engine.horsePower * enemyRushThrottle * forward, ForceMode.Acceleration);

                engine.Turn(steering * enemySteeringSensitivity);
            }
        }

        /// <summary>
        /// 모든 attack_boat의 대기 중인 Invoke/코루틴 취소 (에피소드 리셋 시)
        /// AttackBoatDisabler 등의 지연된 동작이 리셋 후에도 실행되는 것을 방지
        /// </summary>
        private void CancelAttackBoatPendingActions()
        {
            foreach (var boat in _attackBoats)
            {
                if (boat == null || !boat.activeSelf) continue;

                // AttackBoatDisabler의 Invoke 취소
                var disabler = boat.GetComponent<AttackBoatDisabler>();
                if (disabler != null)
                {
                    disabler.CancelInvoke();
                }

                // 모든 MonoBehaviour의 Invoke 취소
                var behaviours = boat.GetComponents<MonoBehaviour>();
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

                default:
                    return 0;
            }
        }

        /// <summary>
        /// 적군 선박 활성화/비활성화 적용
        /// </summary>
        private void ApplyEnemyActivation(int activeCount)
        {
            int activated = 0;

            // enemyShips 배열 처리
            if (enemyShips != null && enemyShips.Length > 0)
            {
                for (int i = 0; i < enemyShips.Length; i++)
                {
                    if (enemyShips[i] != null)
                    {
                        bool shouldBeActive = activated < activeCount;
                        enemyShips[i].SetActive(shouldBeActive);

                        if (shouldBeActive)
                        {
                            activated++;
                        }
                    }
                }
            }

            // _attackBoats 리스트도 처리 (enemyShips와 중복되지 않는 것들)
            foreach (var boat in _attackBoats)
            {
                if (boat == null) continue;

                // enemyShips에 이미 포함되어 있는지 확인
                bool alreadyProcessed = false;
                if (enemyShips != null)
                {
                    foreach (var enemy in enemyShips)
                    {
                        if (enemy == boat)
                        {
                            alreadyProcessed = true;
                            break;
                        }
                    }
                }

                if (!alreadyProcessed)
                {
                    bool shouldBeActive = activated < activeCount;
                    boat.SetActive(shouldBeActive);

                    if (shouldBeActive)
                    {
                        activated++;
                    }
                }
            }
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
        /// 포획 보상 활성화 여부 (Stage2, Stage3에서만 true)
        /// </summary>
        public bool IsCaptureRewardEnabled()
        {
            return currentStage == TrainingStage.Stage2_Capture ||
                   currentStage == TrainingStage.Stage3_Tactical;
        }

        /// <summary>
        /// 모선 충돌 페널티 활성화 여부 (Stage2, Stage3에서만 true)
        /// </summary>
        public bool IsMotherShipPenaltyEnabled()
        {
            return currentStage == TrainingStage.Stage2_Capture ||
                   currentStage == TrainingStage.Stage3_Tactical;
        }

        /// <summary>
        /// 전술 기동 보상 활성화 여부 (Stage3에서만 true)
        /// </summary>
        public bool IsTacticalRewardEnabled()
        {
            return currentStage == TrainingStage.Stage3_Tactical;
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

            // 아군 선박 스폰 범위 시각화 (Cyan) - 모든 페어
            if (enableRandomSpawn)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.3f);

                for (int pi = 0; pi < defensePairs.Count; pi++)
                {
                    var pair = defensePairs[pi];
                    Vector3 pos1 = pair.originalPos1 != Vector3.zero ? pair.originalPos1 : (pair.agent1 != null ? pair.agent1.transform.position : Vector3.zero);
                    Vector3 pos2 = pair.originalPos2 != Vector3.zero ? pair.originalPos2 : (pair.agent2 != null ? pair.agent2.transform.position : Vector3.zero);

                    if (pos1 != Vector3.zero)
                    {
                        DrawCircleGizmo(pos1, spawnRange, 32);
                        Gizmos.DrawWireSphere(pos1, 1f);
                    }
                    if (pos2 != Vector3.zero)
                    {
                        DrawCircleGizmo(pos2, spawnRange, 32);
                        Gizmos.DrawWireSphere(pos2, 1f);
                    }

                    #if UNITY_EDITOR
                    UnityEditor.Handles.color = Color.cyan;
                    if (pos1 != Vector3.zero)
                        UnityEditor.Handles.Label(pos1 + Vector3.up * 5f, $"Pair{pi}-A1\n반경: {spawnRange}m");
                    if (pos2 != Vector3.zero)
                        UnityEditor.Handles.Label(pos2 + Vector3.up * 5f, $"Pair{pi}-A2\n반경: {spawnRange}m");
                    #endif
                }
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
