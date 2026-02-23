using UnityEngine;
using Unity.MLAgents;
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
        Stage3_Tactical     // 전술 기동 학습
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

        [Header("Enemy Pool")]
        [Tooltip("풀 최대 크기 (stage*EnemyCount 이상으로 설정)")]
        public int poolSize = 5;

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

        [Header("Multi-Environment")]
        [Tooltip("환경 루트 Transform (Island Level 등). 비어있으면 부모 또는 자기 자신 사용")]
        public Transform environmentRoot;

        [Header("Reward Monitor (Read Only)")]
        [SerializeField] private float _currentEpisodeReward = 0f;
        [SerializeField] private float _lastStepReward = 0f;
        [SerializeField] private int _currentStep = 0;
        [SerializeField] private int _totalCollisions = 0;

        private int _resetTimer = 0; // FixedUpdate 기반 타이머
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
        
        // 적군 선박 관리 → _enemyPool 배열로 통합 (위 Enemy Pool 섹션 참조)
        
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

            // SimpleMultiAgentGroup 초기화
            if (m_AgentGroup == null)
            {
                m_AgentGroup = new SimpleMultiAgentGroup();
            }
            
            // 에이전트 등록 (SimpleMultiAgentGroup이 있는 경우만)
            if (m_AgentGroup != null)
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

            // 아군 간 거리 체크 (최대/최소) - 유예기간 이후부터
            if (!inGracePeriod && defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 pos1 = defenseAgent1.transform.position;
                Vector3 pos2 = defenseAgent2.transform.position;
                float allyDist = Vector3.Distance(pos1, pos2);

                // 최대 거리 초과 체크
                if (maxAllyDistance > 0f && allyDist > maxAllyDistance)
                {
                    RestartEpisode($"AllyDistanceExceeded(dist={allyDist:F1},max={maxAllyDistance})", rewardCalculator.collisionPenalty);
                    return;
                }

                // 최소 거리 미달 체크
                if (minAllyDistance > 0f && allyDist < minAllyDistance)
                {
                    RestartEpisode($"AllyDistanceTooClose(dist={allyDist:F1},min={minAllyDistance})", rewardCalculator.collisionPenalty);
                    return;
                }

                // 위치 교차 체크 (왼쪽/오른쪽 위치가 바뀌면 페널티)
                // 동적 스폰: 적 접근 방향의 수직축 기준, 레거시: 전역 X축 기준
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
                if (agent1CurrentlyOnLeft != _agent1StartsOnLeft)
                {
                    RestartEpisode($"PositionSwapped(startLeft={_agent1StartsOnLeft},nowLeft={agent1CurrentlyOnLeft})", rewardCalculator.collisionPenalty);
                    return;
                }
            }

            // 방어선 돌파 체크: 적이 그물보다 모선에 임계값 이상 더 가까우면 에피소드 종료
            if (!inGracePeriod && motherShip != null && webObject != null && enemyBreachThreshold > 0f)
            {
                Vector3 motherPos = motherShip.transform.position;
                float webToMother = Vector3.Distance(webObject.transform.position, motherPos);

                foreach (var enemy in enemyShips)
                {
                    if (enemy == null || !enemy.activeInHierarchy) continue;
                    float enemyToMother = Vector3.Distance(enemy.transform.position, motherPos);

                    if (enemyToMother < webToMother - enemyBreachThreshold)
                    {
                        RestartEpisode($"EnemyBreach(enemy={enemy.name},enemyDist={enemyToMother:F0},webDist={webToMother:F0},gap={webToMother - enemyToMother:F0})", enemyBreachPenalty);
                        return;
                    }
                }
            }

            // 보상 계산 주기 확인
            if (_resetTimer % rewardCalculationInterval != 0)
                return;

            if (defenseAgent1 == null || defenseAgent2 == null || rewardCalculator == null)
                return;

            // 상태 수집 → 보상 계산 (대형 유지 + 적 접근 + 시간 페널티)
            var agent1State = rewardCalculator.GetAgentState(defenseAgent1);
            var agent2State = rewardCalculator.GetAgentState(defenseAgent2);
            float stepReward = rewardCalculator.CalculateStepReward(
                agent1State, agent2State, enemyShips, webObject);

            // Inspector 모니터링
            _lastStepReward = stepReward;
            _currentEpisodeReward += stepReward;
            _currentStep = _resetTimer;
            _totalCollisions = _totalCollisionCount;

            // 그룹 보상 부여 (대형 유지 + 적 접근 + 시간 페널티)
            if (Mathf.Abs(stepReward) > 0.0001f)
            {
                if (m_AgentGroup != null)
                {
                    m_AgentGroup.AddGroupReward(stepReward);
                }
                else
                {
                    if (defenseAgent1 != null)
                        defenseAgent1.AddReward(stepReward);
                    if (defenseAgent2 != null)
                        defenseAgent2.AddReward(stepReward);
                }
            }

            // 개별 보상: 헤딩 정렬 (에이전트가 적을 향하면 보상)
            float heading1 = rewardCalculator.CalculateIndividualHeadingReward(agent1State, enemyShips);
            float heading2 = rewardCalculator.CalculateIndividualHeadingReward(agent2State, enemyShips);
            if (heading1 > 0f && defenseAgent1 != null)
                defenseAgent1.AddReward(heading1);
            if (heading2 > 0f && defenseAgent2 != null)
                defenseAgent2.AddReward(heading2);
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
            if (defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 p1 = defenseAgent1.transform.position;
                Vector3 p2 = defenseAgent2.transform.position;
                float dist = Vector3.Distance(p1, p2);
                detail = $" | allyDist={dist:F1}m, pos1={p1}, pos2={p2}";
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
                    if (defenseAgent1 != null)
                        defenseAgent1.AddReward(finalReward.Value);
                    if (defenseAgent2 != null)
                        defenseAgent2.AddReward(finalReward.Value);
                }
            }
            
            // 3단계: 에이전트 에피소드 종료
            if (m_AgentGroup != null)
            {
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
            
            // 기존 리셋 코루틴 중지 및 플래그 초기화
            StopAllCoroutines();
            _isResettingPositions = false;
            _lastResetFrame = -1; // 프레임 체크 초기화

            // 에피소드 종료 플래그를 먼저 리셋하여 다음 FixedUpdate()에서 타이머가 증가하지 않도록 함
            _episodeEnding = false;

            // 에피소드는 코루틴 완료 후 활성화 (초기에는 false로 시작)
            _episodeActive = false;

            _resetTimer = 0;

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
                if (defenseAgent1 != null)
                    defenseAgent1.AddReward(reward);
                if (defenseAgent2 != null)
                    defenseAgent2.AddReward(reward);
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
                if (defenseAgent1 != null)
                    defenseAgent1.AddReward(penalty);
                if (defenseAgent2 != null)
                    defenseAgent2.AddReward(penalty);
            }

            ResetSingleAttackBoat(enemyBoat);
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

            // 비활성화 (풀에서 재사용 가능)
            attackBoat.SetActive(false);
            UpdateEnemyShipsArray();

            // 모든 활성 적군이 0이면 에피소드 종료
            if (endEpisodeOnAllEnemiesDestroyed && !_episodeEnding)
            {
                int activeCount = 0;
                if (_enemyPool != null)
                {
                    for (int i = 0; i < _enemyPool.Length; i++)
                    {
                        if (_enemyPool[i] != null && _enemyPool[i].activeSelf)
                            activeCount++;
                    }
                }

                if (activeCount == 0 && GetActiveEnemyCountForStage() > 0)
                {
                    RestartEpisode("AllEnemiesDestroyed");
                }
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

            // pool[0] = 템플릿 자체 재활용
            _enemyPool[0] = template;
            CachePoolComponents(0);

            // pool[1..poolSize-1] = 복제 (템플릿의 부모 하위에 생성)
            Transform poolParent = template.transform.parent != null ? template.transform.parent : GetEnvironmentRoot();
            for (int i = 1; i < poolSize; i++)
            {
                GameObject clone = Instantiate(template, poolParent);
                clone.name = $"attack_boat_pool_{i}";
                clone.tag = "attack_boat";
                clone.SetActive(false);
                _enemyPool[i] = clone;
                CachePoolComponents(i);
            }

            // 모든 풀 객체 비활성화 (ResetScene에서 활성화)
            for (int i = 0; i < poolSize; i++)
            {
                // SimpleExplosionOnCollision의 Destroy 방지
                if (_poolExplosions[i] != null)
                {
                    _poolExplosions[i].destroyAfterExplosion = false;
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

            // 3. Rigidbody 속도 초기화
            Rigidbody rb = _poolRigidbodies[index];
            if (rb != null)
            {
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

                // 3. 아군 스폰 (적이 오는 방향, 모선 앞쪽 defenseSpawnDistance 거리)
                // 적과 모선 사이에 배치 → 적을 맞이하는 형태
                Vector3 defenseDir = enemyDir; // 적이 있는 방향 (모선→적 방향)
                float spreadRad1 = -defenseSpawnSpread * Mathf.Deg2Rad;
                float spreadRad2 = defenseSpawnSpread * Mathf.Deg2Rad;

                // agent1: 왼쪽 (적 방향에서 -spread 회전)
                Vector3 dir1 = RotateXZ(defenseDir, spreadRad1);
                Vector3 spawnPos1 = motherPos + dir1 * defenseSpawnDistance;
                spawnPos1.y = _originalDefense1Pos.y;
                // 적이 오는 방향 바라봄 (= enemyDir, 적에서 모선으로의 방향이 아닌 모선에서 적으로)
                Quaternion rot1 = Quaternion.LookRotation(defenseDir, Vector3.up);

                // agent2: 오른쪽 (적 방향에서 +spread 회전)
                Vector3 dir2 = RotateXZ(defenseDir, spreadRad2);
                Vector3 spawnPos2 = motherPos + dir2 * defenseSpawnDistance;
                spawnPos2.y = _originalDefense2Pos.y;
                Quaternion rot2 = rot1; // 같은 방향 바라봄

                // 아군 위치/각도에 각각 독립 랜덤 추가
                if (enableRandomSpawn)
                {
                    float jitter1 = Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange);
                    float jitter2 = Random.Range(-defenseRandomAngleRange, defenseRandomAngleRange);
                    rot1 *= Quaternion.Euler(0f, jitter1, 0f);
                    rot2 *= Quaternion.Euler(0f, jitter2, 0f);
                }

                ResetDefenseAgentDirect(defenseAgent1, spawnPos1, rot1);
                ResetDefenseAgentDirect(defenseAgent2, spawnPos2, rot2);

                // 동적 스폰: 수직축 기반 위치 교차 판별 설정
                _currentPerpendicularDir = RotateXZ(defenseDir, Mathf.PI / 2f);
                float perpDot1 = Vector3.Dot(spawnPos1 - motherPos, _currentPerpendicularDir);
                float perpDot2 = Vector3.Dot(spawnPos2 - motherPos, _currentPerpendicularDir);
                _agent1StartsOnLeft = perpDot1 < perpDot2;

                Debug.Log($"[DefenseEnv] DynamicSpawn: enemyAngle={_currentEnemyApproachAngle:F0}°, " +
                    $"ally1={spawnPos1}, ally2={spawnPos2}, allyDist={Vector3.Distance(spawnPos1, spawnPos2):F1}m, " +
                    $"perpDot1={perpDot1:F1}, perpDot2={perpDot2:F1}, agent1Left={_agent1StartsOnLeft}");
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
            if (_enemyPool == null) return;

            int stageTargetCount = GetActiveEnemyCountForStage();

            for (int i = 0; i < _enemyPool.Length; i++)
            {
                if (_enemyPool[i] == null) continue;

                if (i < stageTargetCount)
                {
                    // 각 적군마다 약간의 위치 오프셋 (퍼짐)
                    Vector3 spreadOffset = new Vector3(
                        Random.Range(-enemySpawnSpread, enemySpawnSpread),
                        0f,
                        Random.Range(-enemySpawnSpread, enemySpawnSpread)
                    );

                    Vector3 spawnPos = motherPos + enemyDir * enemySpawnDistance + spreadOffset;
                    spawnPos.y = _poolTemplateY;

                    // 모선 정중앙을 바라보는 회전
                    Vector3 lookDir = motherPos - spawnPos;
                    lookDir.y = 0f;
                    Quaternion spawnRot = lookDir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(lookDir, Vector3.up)
                        : Quaternion.identity;

                    ResetPoolObject(i, spawnPos, spawnRot);
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

            // 풀 객체 간 충돌 무시
            IgnoreCollisionBetweenEnemies();

            Debug.Log($"[DefenseEnv] ResetAttackBoatsDynamic: target={stageTargetCount}, poolSize={_enemyPool.Length}");

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
                if (_enemyPool[i] != null && _enemyPool[i].activeSelf)
                    activeEnemies.Add(_enemyPool[i]);
            }

            enemyShips = activeEnemies.ToArray();

            // DefenseAgent에 적군 배열 전달
            if (defenseAgent1 != null) defenseAgent1.enemyShips = enemyShips;
            if (defenseAgent2 != null) defenseAgent2.enemyShips = enemyShips;
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
