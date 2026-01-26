using UnityEngine;
using Unity.MLAgents;
using Cinemachine;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 방어 환경 컨트롤러 (PushBlockEnvController 패턴 기반)
    /// 중앙 허브: 모든 에피소드 재시작 로직을 중앙에서 관리
    /// - 그룹 보상 분배 및 환경 관리
    /// - 선박 위치 리셋 및 적군 추적
    /// - 에피소드 시작/종료 관리
    /// </summary>
    public class DefenseEnvController : MonoBehaviour
    {
        [System.Serializable]
        public class DefenseAgentInfo
        {
            public Agent Agent; // DefenseAgent 또는 DefenseAgentCollab 모두 지원
            [HideInInspector]
            public Vector3 StartingPos;
            [HideInInspector]
            public Quaternion StartingRot;
            [HideInInspector]
            public Rigidbody Rb;
        }

        [System.Serializable]
        public class AttackBoatInfo
        {
            public GameObject Boat;
            [HideInInspector]
            public Vector3 StartingPos;
            [HideInInspector]
            public Quaternion StartingRot;
            [HideInInspector]
            public CinemachinePathBase StartingPath;
        }

        [Header("Agents")]
        [Tooltip("방어 에이전트 1 (DefenseAgent 또는 DefenseAgentCollab)")]
        public Agent defenseAgent1;
        
        [Tooltip("방어 에이전트 2 (DefenseAgent 또는 DefenseAgentCollab)")]
        public Agent defenseAgent2;
        
        [Header("Components")]
        [Tooltip("그룹 보상 사용 여부 (true면 SimpleMultiAgentGroup 사용, false면 개별 보상)")]
        public bool useGroupReward = true;
        
        // SimpleMultiAgentGroup은 MonoBehaviour가 아니므로 직접 인스턴스 생성
        private SimpleMultiAgentGroup m_AgentGroup;
        
        // [Tooltip("보상 계산기")]
        // public DefenseRewardCalculator rewardCalculator; // 클래스가 존재하지 않으므로 주석 처리
        
        [Header("Settings")]
        [Tooltip("보상 계산 주기 (프레임 단위, 1 = 매 프레임)")]
        public int rewardCalculationInterval = 1;
        
        [Tooltip("최대 환경 스텝 수 (에피소드가 이 스텝 수에 도달하면 자동 종료)")]
        public int MaxEnvironmentSteps = 5000;
        
        [Tooltip("모선 참조")]
        public GameObject motherShip;
        
        [Tooltip("적군 선박들 (인스펙터에서 할당, 비어있으면 자동으로 찾음)")]
        public GameObject[] enemyShips = new GameObject[5];
        
        [Tooltip("Web 오브젝트 (없으면 자동 생성)")]
        public GameObject webObject;
        
        [Header("Web Settings")]
        [Tooltip("Web 자동 생성 활성화")]
        public bool autoCreateWeb = true;
        
        [Tooltip("Web 높이")]
        public float webHeight = 5f;
        
        [Tooltip("Web 두께")]
        public float webThickness = 0.5f;
        
        [Tooltip("Web 색상")]
        public Color webColor = new Color(0f, 1f, 1f, 0.3f); // 반투명 청록색
        
        [Tooltip("Web 최소 거리 (두 선박이 이 거리보다 가까우면 Web 생성 안 함)")]
        public float webMinDistance = 5f;
        
        [Tooltip("Web 최대 거리 (두 선박이 이 거리보다 멀면 Web 생성 안 함)")]
        public float webMaxDistance = 500f;
        
        [Header("Explosion Settings")]
        [Tooltip("폭발 효과 Prefab (선택사항, 없으면 간단한 파티클 효과 사용)")]
        public GameObject explosionPrefab;
        
        [Tooltip("폭발 효과 크기 배율")]
        [Range(1f, 50f)]
        public float explosionScale = 15f;
        
        [Tooltip("폭발 효과 지속 시간 (초)")]
        public float explosionDuration = 2f;
        
        [Header("Spawn Positions")]
        [Tooltip("방어 선박 1 초기 위치")]
        public Vector3 defense1SpawnPos = new Vector3(-115f, -8f, -10f);

        [Tooltip("방어 선박 2 초기 위치")]
        public Vector3 defense2SpawnPos = new Vector3(0f, -8f, -6f);

        [Tooltip("Web 오브젝트 위치 (2대 중간)")]
        public Vector3 webSpawnPos = new Vector3(0f, 0.8f, 0f);
        
        [Header("Random Spawn Settings")]
        [Tooltip("랜덤 스폰 범위 (기존 위치에서 ±range 범위로 랜덤 생성)")]
        public float spawnRange = 20f;
        
        [Tooltip("랜덤 스폰 활성화 (에피소드 시작 시 랜덤 위치로 재생성)")]
        public bool enableRandomSpawn = true;
        
        [Tooltip("방어 에이전트 랜덤 위치 사용")]
        public bool UseRandomAgentPosition = true;
        
        [Tooltip("방어 에이전트 랜덤 각도 사용")]
        public bool UseRandomAgentRotation = true;
        
        [Header("Mission Rewards")]
        [Tooltip("포획 성공 보상")]
        public float captureReward = 1.0f;
        
        [Tooltip("포획 위치 보너스 (최대)")]
        public float captureCenterBonus = 0.3f;
        
        [Tooltip("포획 위치 보너스 최대 거리")]
        public float captureCenterMaxDistance = 30f;
        
        [Tooltip("모선 방어 성공 보상")]
        public float motherShipDefenseReward = 0.5f;
        
        [Tooltip("모선 방어 반경 (m)")]
        public float motherShipDefenseRadius = 1000f;
        
        [Tooltip("방어선 침범 패널티")]
        public float boundaryBreachPenalty = -0.1f;
        
        [Tooltip("2km 경계선")]
        public float boundary2km = 2000f;
        
        [Tooltip("1km 경계선")]
        public float boundary1km = 1000f;
        
        [Tooltip("모선 충돌 패널티 (Game Over)")]
        public float motherShipCollisionPenalty = -1.75f;
        
        [Header("Collision Detection")]
        [Tooltip("충돌 패널티 (아군-아군, 아군-모선)")]
        public float collisionPenalty = -1.0f;
        
        [Header("Episode End Conditions")]
        [Tooltip("모든 적군 선박 파괴 시 에피소드 종료")]
        public bool endEpisodeOnAllEnemiesDestroyed = true;
        
        [Header("Debug")]
        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = false;

        // PushBlockEnvController 패턴: 타이머 관리
        private int m_ResetTimer = 0;
        private bool _episodeActive = false;
        private bool _episodeEnding = false;
        private bool _isResettingPositions = false;
        
        // 상태 플래그
        private bool _enemyEnteredDefenseZone = false;
        private bool _boundary2kmBreached = false;
        private bool _boundary1kmBreached = false;
        
        // 방어 에이전트 정보
        private DefenseAgentInfo _agent1Info = new DefenseAgentInfo();
        private DefenseAgentInfo _agent2Info = new DefenseAgentInfo();
        
        // 적군 선박 (attack_boat 태그) 관리
        private List<GameObject> _attackBoats = new List<GameObject>();
        private Dictionary<GameObject, Vector3> _attackBoatInitialPositions = new Dictionary<GameObject, Vector3>();
        private Dictionary<GameObject, Quaternion> _attackBoatInitialRotations = new Dictionary<GameObject, Quaternion>();
        private Dictionary<GameObject, CinemachinePathBase> _attackBoatInitialPaths = new Dictionary<GameObject, CinemachinePathBase>();
        private Dictionary<string, GameObject> _attackBoatPrefabs = new Dictionary<string, GameObject>(); // 재생성용 프리팹
        private HashSet<string> _destroyedAttackBoatNames = new HashSet<string>(); // 파괴된 선박 이름 추적
        private int _initialAttackBoatCount = 0;
        
        // 일반 boat 태그 객체 관리
        private Dictionary<GameObject, Vector3> _originalBoatPositions = new Dictionary<GameObject, Vector3>();
        private Dictionary<GameObject, Quaternion> _originalBoatRotations = new Dictionary<GameObject, Quaternion>();
        
        // 적군 선박 (enemyShips 배열)
        private Dictionary<GameObject, Vector3> _originalEnemyPositions = new Dictionary<GameObject, Vector3>();
        
        // Web 관련
        private MeshRenderer _webRenderer;
        private MeshFilter _webMeshFilter;
        private BoxCollider _webCollider;
        private Material _webMaterial;
        
        // private WebCollisionDetector _webDetector; // 클래스가 존재하지 않으므로 주석 처리

        void Start()
        {
            // SimpleMultiAgentGroup 초기화 (MonoBehaviour가 아니므로 직접 생성)
            if (useGroupReward && m_AgentGroup == null)
            {
                m_AgentGroup = new SimpleMultiAgentGroup();
                
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] SimpleMultiAgentGroup 인스턴스 생성됨");
                }
            }
            
            // 에이전트 등록 및 초기 정보 저장
            if (defenseAgent1 != null)
            {
                _agent1Info.Agent = defenseAgent1;
                _agent1Info.StartingPos = defenseAgent1.transform.position;
                if (_agent1Info.StartingPos == Vector3.zero)
                {
                    _agent1Info.StartingPos = defense1SpawnPos;
                }
                _agent1Info.StartingRot = defenseAgent1.transform.rotation;
                _agent1Info.Rb = defenseAgent1.GetComponent<Rigidbody>();
                
                if (m_AgentGroup != null)
                {
                    try
                    {
                        m_AgentGroup.RegisterAgent(defenseAgent1);
                    }
                    catch (System.InvalidOperationException ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] defenseAgent1 등록 실패 (이미 등록되었을 수 있음): {ex.Message}");
                    }
                }
            }
            
            if (defenseAgent2 != null)
            {
                _agent2Info.Agent = defenseAgent2;
                _agent2Info.StartingPos = defenseAgent2.transform.position;
                if (_agent2Info.StartingPos == Vector3.zero)
                {
                    _agent2Info.StartingPos = defense2SpawnPos;
                }
                _agent2Info.StartingRot = defenseAgent2.transform.rotation;
                _agent2Info.Rb = defenseAgent2.GetComponent<Rigidbody>();
                
                if (m_AgentGroup != null)
                {
                    try
                    {
                        m_AgentGroup.RegisterAgent(defenseAgent2);
                    }
                    catch (System.InvalidOperationException ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] defenseAgent2 등록 실패 (이미 등록되었을 수 있음): {ex.Message}");
                    }
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
            
            // 태그가 "boat"인 모든 GameObject의 초기 위치 및 각도 저장 (WAKE 제외)
            GameObject[] allBoats = GameObject.FindGameObjectsWithTag("boat");
            foreach (var boat in allBoats)
            {
                if (boat != null && !boat.name.Contains("WAKE") && !boat.name.Contains("Wake") && !_originalBoatPositions.ContainsKey(boat))
                {
                    _originalBoatPositions[boat] = boat.transform.position;
                    _originalBoatRotations[boat] = boat.transform.rotation;
                }
            }
            
            // attack_boat 태그를 가진 모든 적군 선박 찾기 및 초기 위치 저장
            FindAndSaveAttackBoats();
            
            // WebCollisionDetector 찾기 (클래스가 존재하지 않으므로 주석 처리)
            // _webDetector = FindObjectOfType<WebCollisionDetector>();
            
            // Web 자동 생성 또는 초기화
            InitializeWeb();
            
            // MotherShip 충돌 감지 설정
            SetupMotherShipCollision();
            
            // 첫 에피소드 시작 (PushBlockEnvController 패턴)
            ResetScene();
        }
        
        /// <summary>
        /// MotherShip 충돌 감지 설정
        /// </summary>
        private void SetupMotherShipCollision()
        {
            if (motherShip == null)
            {
                if (enableDebugLog)
                {
                    Debug.LogWarning("[DefenseEnvController] MotherShip이 null입니다. 충돌 감지를 설정할 수 없습니다.");
                }
                return;
            }
            
            // MotherShipCollisionHandler 추가 또는 확인
            MotherShipCollisionHandler handler = motherShip.GetComponent<MotherShipCollisionHandler>();
            if (handler == null)
            {
                handler = motherShip.AddComponent<MotherShipCollisionHandler>();
                handler.envController = this;
                
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] MotherShipCollisionHandler 컴포넌트 추가됨");
                }
            }
            else
            {
                // 이미 있으면 envController 참조 업데이트
                handler.envController = this;
            }
            
            // Collider 확인 및 설정
            Collider existingCollider = motherShip.GetComponent<Collider>();
            if (existingCollider == null)
            {
                // Collider가 없으면 추가 (충돌 감지용)
                BoxCollider collider = motherShip.AddComponent<BoxCollider>();
                collider.isTrigger = true; // Trigger로 설정하여 OnTriggerEnter 사용
                
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] MotherShip에 BoxCollider 추가됨 (isTrigger=true)");
                }
            }
            else
            {
                // Collider가 이미 있으면 isTrigger 확인
                if (existingCollider is BoxCollider boxCollider)
                {
                    if (!boxCollider.isTrigger)
                    {
                        boxCollider.isTrigger = true;
                        if (enableDebugLog)
                        {
                            Debug.Log("[DefenseEnvController] MotherShip Collider를 isTrigger=true로 변경");
                        }
                    }
                }
            }
            
            // Rigidbody 확인 (Kinematic으로 설정)
            Rigidbody rb = motherShip.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = motherShip.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            else
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] MotherShip 충돌 감지 설정 완료 (Tag: {motherShip.tag})");
            }
        }

        /// <summary>
        /// PushBlockEnvController 패턴: FixedUpdate에서 타이머 관리
        /// </summary>
        void FixedUpdate()
        {
            // 에피소드가 비활성화되었거나 종료 중이면 타이머 증가하지 않음
            if (!_episodeActive || _episodeEnding)
            {
                // Web 비활성화 (에피소드가 비활성화된 경우)
                if (webObject != null && webObject.activeSelf)
                {
                    webObject.SetActive(false);
                }
                return;
            }
            
            // Web 업데이트 (매 프레임)
            UpdateWeb();
            
            m_ResetTimer++;
            
            // 최대 스텝 수 체크 (PushBlockEnvController 패턴)
            if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
            {
                if (enableDebugLog)
                {
                    Debug.Log($"[DefenseEnvController] 최대 환경 스텝 수 도달 ({MaxEnvironmentSteps}). 에피소드 종료.");
                }
                
                // 통합 에피소드 재시작 메서드 호출
                RestartEpisode("MaxEnvironmentSteps");
                return;
            }
            
            // Hurry Up Penalty (PushBlockEnvController 패턴)
            if (m_AgentGroup != null && _episodeActive)
            {
                try
                {
                    m_AgentGroup.AddGroupReward(-0.5f / MaxEnvironmentSteps);
                }
                catch (System.InvalidOperationException ex)
                {
                    // 에피소드가 종료된 상태에서 보상을 추가하려고 할 때 발생할 수 있음
                    if (enableDebugLog)
                    {
                        Debug.LogWarning($"[DefenseEnvController] 그룹 보상 추가 실패: {ex.Message}");
                    }
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
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] ===== RestartEpisode() 호출됨 (이유: {reason}) =====");
            }
            
            // 중복 호출 방지
            if (_episodeEnding)
            {
                if (enableDebugLog)
                {
                    Debug.Log($"[DefenseEnvController] 에피소드가 이미 종료 중입니다. RestartEpisode() 건너뜀.");
                }
                return;
            }
            
            // 1단계: 에피소드 종료 플래그 설정
            _episodeEnding = true;
            
            // 2단계: 최종 보상 부여 (있는 경우)
            if (finalReward.HasValue)
            {
                if (m_AgentGroup != null)
                {
                    try
                    {
                        m_AgentGroup.AddGroupReward(finalReward.Value);
                    }
                    catch (System.InvalidOperationException ex)
                    {
                        if (enableDebugLog)
                        {
                            Debug.LogWarning($"[DefenseEnvController] 그룹 보상 추가 실패: {ex.Message}");
                        }
                    }
                }
                else
                {
                    if (defenseAgent1 != null)
                        defenseAgent1.AddReward(finalReward.Value);
                    if (defenseAgent2 != null)
                        defenseAgent2.AddReward(finalReward.Value);
                }
            }
            
            // 3단계: 모선 방어 성공 체크 (적이 방어 존에 진입하지 않았을 때)
            if (!_enemyEnteredDefenseZone && motherShip != null && reason != "MotherShipDefense")
            {
                if (m_AgentGroup != null)
                {
                    try
                    {
                        m_AgentGroup.AddGroupReward(motherShipDefenseReward);
                    }
                    catch (System.InvalidOperationException ex)
                    {
                        if (enableDebugLog)
                        {
                            Debug.LogWarning($"[DefenseEnvController] 그룹 보상 추가 실패: {ex.Message}");
                        }
                    }
                }
                else
                {
                    if (defenseAgent1 != null)
                        defenseAgent1.AddReward(motherShipDefenseReward);
                    if (defenseAgent2 != null)
                        defenseAgent2.AddReward(motherShipDefenseReward);
                }
                
                if (enableDebugLog)
                {
                    Debug.Log($"[DefenseEnvController] 모선 방어 성공! 보상: {motherShipDefenseReward}");
                }
            }
            
            // 4단계: 에이전트 에피소드 종료
            if (m_AgentGroup != null)
            {
                try
                {
                    m_AgentGroup.EndGroupEpisode();
                }
                catch (System.InvalidOperationException ex)
                {
                    if (enableDebugLog)
                    {
                        Debug.LogWarning($"[DefenseEnvController] 그룹 에피소드 종료 실패: {ex.Message}");
                    }
                    // 개별 에이전트로 fallback
                    if (defenseAgent1 != null)
                        defenseAgent1.EndEpisode();
                    if (defenseAgent2 != null)
                        defenseAgent2.EndEpisode();
                }
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
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] ===== RestartEpisode() 완료 (이유: {reason}) =====");
            }
        }
        
        /// <summary>
        /// PushBlockEnvController 패턴: 환경 리셋
        /// RestartEpisode()에서 호출됩니다.
        /// </summary>
        public void ResetScene()
        {
            if (enableDebugLog)
            {
                Debug.Log("[DefenseEnvController] ===== ResetScene() 호출됨 =====");
            }
            
            // 위치 리셋이 이미 진행 중이면 건너뜀
            if (_isResettingPositions)
            {
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] ResetScene() 건너뜀 (위치 리셋이 이미 진행 중입니다)");
                }
                return;
            }
            
            // 에피소드 종료 플래그를 먼저 리셋하여 다음 FixedUpdate()에서 타이머가 증가하지 않도록 함
            _episodeEnding = false;
            
            // 에피소드는 코루틴 완료 후 활성화
            _episodeActive = false;
            
            m_ResetTimer = 0;
            _enemyEnteredDefenseZone = false;
            _boundary2kmBreached = false;
            _boundary1kmBreached = false;
            
            // 파괴된 적군 선박 목록 초기화 (에피소드 재시작 시)
            _destroyedAttackBoatNames.Clear();
            
            // RewardCalculator 리셋 (클래스가 존재하지 않으므로 주석 처리)
            // if (rewardCalculator != null)
            // {
            //     rewardCalculator.Reset();
            // }
            
            // 모든 선박 리셋 (비활성화 → 위치 리셋 → 활성화)
            ResetPositionsOnly();
            
            if (enableDebugLog)
            {
                Debug.Log("[DefenseEnvController] ===== ResetScene() 완료 =====");
            }
        }
        
        /// <summary>
        /// 에피소드 시작 (ML-Agents가 자동으로 호출)
        /// </summary>
        public void OnEpisodeBegin()
        {
            if (enableDebugLog)
            {
                Debug.Log("[DefenseEnvController] OnEpisodeBegin 호출됨 (ML-Agents 자동 호출)");
            }
            
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
        
        #endregion

        #region 적 포획 및 충돌 처리
        
        /// <summary>
        /// 적 포획 성공 처리 (중앙 허브로 리다이렉트)
        /// </summary>
        public void OnEnemyCaptured(Vector3 enemyPosition)
        {
            if (!_episodeActive)
                return;
            
            // 기본 포획 보상
            float totalReward = captureReward;
            
            // 위치 보너스 계산 (그물 중심에 가까울수록)
            if (defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 netCenter = (defenseAgent1.transform.position + 
                                   defenseAgent2.transform.position) / 2f;
                float distanceToCenter = Vector3.Distance(enemyPosition, netCenter);
                
                if (distanceToCenter <= captureCenterMaxDistance)
                {
                    float centerBonus = captureCenterBonus * 
                        (1f - distanceToCenter / captureCenterMaxDistance);
                    totalReward += centerBonus;
                }
            }
            
            // 통합 에피소드 재시작 메서드 호출
            RestartEpisode("EnemyCaptured", totalReward);
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 적 포획 성공! 보상: {totalReward:F3}");
            }
        }
        
        /// <summary>
        /// 모선 충돌 처리 (Game Over) (중앙 허브로 리다이렉트)
        /// </summary>
        public void OnMotherShipCollision(GameObject enemyBoat)
        {
            if (!_episodeActive)
                return;
            
            // 통합 에피소드 재시작 메서드 호출
            RestartEpisode("MotherShipCollision", motherShipCollisionPenalty);
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 모선 충돌! 패널티: {motherShipCollisionPenalty}");
            }
        }
        
        /// <summary>
        /// 아군 충돌 처리
        /// </summary>
        public void OnFriendlyCollision()
        {
            if (!_episodeActive)
                return;
            
            // 큰 패널티 부여 (그룹 또는 개별)
            if (m_AgentGroup != null)
            {
                try
                {
                    m_AgentGroup.AddGroupReward(collisionPenalty);
                    m_AgentGroup.EndGroupEpisode();
                }
                catch (System.InvalidOperationException ex)
                {
                    if (enableDebugLog)
                    {
                        Debug.LogWarning($"[DefenseEnvController] 그룹 보상/에피소드 종료 실패: {ex.Message}");
                    }
                    // 개별 에이전트로 fallback
                    if (defenseAgent1 != null)
                    {
                        defenseAgent1.AddReward(collisionPenalty);
                        defenseAgent1.EndEpisode();
                    }
                    if (defenseAgent2 != null)
                    {
                        defenseAgent2.AddReward(collisionPenalty);
                        defenseAgent2.EndEpisode();
                    }
                }
            }
            else
            {
                if (defenseAgent1 != null)
                {
                    defenseAgent1.AddReward(collisionPenalty);
                    defenseAgent1.EndEpisode();
                }
                if (defenseAgent2 != null)
                {
                    defenseAgent2.AddReward(collisionPenalty);
                    defenseAgent2.EndEpisode();
                }
            }
            
            // PushBlockEnvController 패턴: 에피소드 종료 후 환경 리셋
            ResetScene();
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 아군 충돌! 패널티: {collisionPenalty}");
            }
        }
        
        #endregion

        #region 적군 선박 파괴 관리 (중앙 허브)
        
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
                if (enableDebugLog)
                {
                    Debug.LogWarning($"[DefenseEnvController] {boatName}은 이미 파괴 요청이 처리되었습니다.");
                }
                return;
            }
            
            // 다음 프레임에 파괴 처리 (물리 콜백 제약 회피)
            StartCoroutine(DestroyAttackBoatNextFrame(attackBoat));
        }
        
        /// <summary>
        /// 다음 프레임에 attack_boat 파괴 처리 (물리 콜백 제약 회피)
        /// </summary>
        private IEnumerator DestroyAttackBoatNextFrame(GameObject attackBoat)
        {
            // 다음 프레임까지 대기 (물리 콜백이 끝난 후)
            yield return null;
            
            if (attackBoat == null)
            {
                if (enableDebugLog)
                {
                    Debug.LogWarning("[DefenseEnvController] attackBoat가 이미 파괴되었습니다!");
                }
                yield break;
            }
            
            // 파괴 처리
            OnAttackBoatDestroyed(attackBoat);
            
            // attack_boat 파괴
            Destroy(attackBoat);
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] {attackBoat.name} 파괴 완료");
            }
        }
        
        /// <summary>
        /// 적군 선박이 파괴되었을 때 호출 (내부에서만 호출)
        /// 중앙 허브: 모든 파괴 정보를 중앙에서 관리
        /// </summary>
        private void OnAttackBoatDestroyed(GameObject destroyedBoat)
        {
            if (destroyedBoat == null)
                return;
            
            // 파괴된 선박의 이름 저장 (파괴 후에도 추적 가능하도록)
            string boatName = destroyedBoat.name.Replace("(Clone)", "");
            _destroyedAttackBoatNames.Add(boatName);
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] OnAttackBoatDestroyed 호출: {destroyedBoat.name} (이름: {boatName})");
            }
            
            // 초기 적군 선박 수가 0이면 다시 찾기
            if (_initialAttackBoatCount == 0)
            {
                FindAndSaveAttackBoats();
            }
            
            // 파괴된 선박을 리스트에서 제거
            _attackBoats.Remove(destroyedBoat);
            _attackBoats.RemoveAll(boat => boat == null);
            
            // 현재 활성화된 적군 선박 수 확인
            GameObject[] allAttackBoatsInScene = GameObject.FindGameObjectsWithTag("attack_boat");
            int activeCount = allAttackBoatsInScene.Count(boat => 
                boat != null && 
                !_destroyedAttackBoatNames.Contains(boat.name.Replace("(Clone)", ""))
            );
            
            int activeInList = _attackBoats.Count(boat => 
                boat != null && 
                boat.activeSelf && 
                !_destroyedAttackBoatNames.Contains(boat.name.Replace("(Clone)", ""))
            );
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 적군 선박 파괴 후 상태 - 리스트: {_attackBoats.Count}개 (활성화: {activeInList}개), 씬의 활성화된 적군: {activeCount}개, 초기 적군 수: {_initialAttackBoatCount}개, 파괴된 선박: {_destroyedAttackBoatNames.Count}개");
            }
            
            // 모든 적군 선박이 파괴되었는지 확인
            if (endEpisodeOnAllEnemiesDestroyed && _initialAttackBoatCount > 0 && activeCount == 0 && activeInList == 0)
            {
                if (_episodeEnding)
                {
                    return;
                }
                
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] ★★★ 모든 적군 선박 파괴됨! 에피소드 종료 ★★★");
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
            _attackBoatInitialRotations.Clear();
            // 프리팹 딕셔너리는 초기화하지 않음 (에피소드 재시작 시 재사용)
            
            // 씬의 모든 attack_boat 태그를 가진 객체 찾기
            GameObject[] foundBoats = GameObject.FindGameObjectsWithTag("attack_boat");
            
            // enemyShips 배열 자동 동기화
            if (enemyShips == null || enemyShips.Length == 0 || enemyShips.All(e => e == null))
            {
                enemyShips = new GameObject[foundBoats.Length];
                for (int i = 0; i < foundBoats.Length; i++)
                {
                    enemyShips[i] = foundBoats[i];
                }
                if (enableDebugLog)
                {
                    Debug.Log($"[DefenseEnvController] enemyShips 배열이 비어있어 자동으로 {foundBoats.Length}개 채움");
                }
            }
            
            foreach (var boat in foundBoats)
            {
                if (boat != null && !_attackBoats.Contains(boat))
                {
                    _attackBoats.Add(boat);
                    Vector3 initialPos = boat.transform.position;
                    Quaternion initialRot = boat.transform.rotation;
                    _attackBoatInitialPositions[boat] = initialPos;
                    _attackBoatInitialRotations[boat] = initialRot;
                    
                    // 원본 객체를 프리팹으로 저장 (파괴 후 재생성용)
                    string boatName = boat.name.Replace("(Clone)", "");
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
                    }
                    else
                    {
                        _attackBoatInitialPaths[boat] = null;
                    }
                }
            }
            
            _initialAttackBoatCount = _attackBoats.Count;
            
            // enemyShips 배열과 실제 씬의 객체 수가 다른 경우 경고
            int enemyShipsCount = enemyShips != null ? enemyShips.Count(e => e != null) : 0;
            if (enemyShipsCount > 0 && enemyShipsCount != _initialAttackBoatCount)
            {
                Debug.LogWarning($"[DefenseEnvController] ⚠️ enemyShips 배열({enemyShipsCount}개)과 실제 씬의 attack_boat({_initialAttackBoatCount}개) 수가 다릅니다. " +
                    $"실제 씬의 객체 수({_initialAttackBoatCount}개)를 기준으로 추적합니다.");
            }
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 적군 선박 {_initialAttackBoatCount}개 발견 및 초기 위치 저장 완료 (enemyShips 배열: {enemyShipsCount}개)");
            }
        }
        
        #endregion

        #region 위치 리셋 로직
        
        /// <summary>
        /// 위치만 리셋 (외부에서 호출 가능, ML-Agents 에피소드 재시작 시 사용)
        /// 모든 선박을 비활성화 → 위치 리셋 → 활성화
        /// </summary>
        public void ResetPositionsOnly()
        {
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] ===== ResetPositionsOnly() 호출됨 (Frame {Time.frameCount}) =====");
            }
            
            // 코루틴이 이미 실행 중이면 중복 호출 방지
            if (_isResettingPositions)
            {
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] ResetPositionsOnly() 건너뜀 (위치 리셋이 이미 진행 중입니다)");
                }
                return;
            }
            
            // 플래그 설정 (코루틴 시작 전에 설정)
            _isResettingPositions = true;
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] ResetPositionsWithDeactivation() 코루틴 시작... (Frame {Time.frameCount})");
            }
            
            StartCoroutine(ResetPositionsWithDeactivation());
        }
        
        /// <summary>
        /// 모든 선박을 비활성화 → 위치 리셋 → 활성화하는 코루틴
        /// </summary>
        private IEnumerator ResetPositionsWithDeactivation()
        {
            if (enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] ===== ResetPositionsWithDeactivation() 코루틴 시작 (Frame {Time.frameCount}) =====");
            }
            
            // 모든 WAKE 객체 제거 및 WakeGenerator 비활성화
            DestroyAllWakeObjects();
            
            // 비활성화 전에 모든 boat 리스트 저장
            List<GameObject> allBoatsList = new List<GameObject>();
            GameObject[] allBoatsArray = GameObject.FindGameObjectsWithTag("boat");
            foreach (var boat in allBoatsArray)
            {
                if (boat != null && !boat.name.Contains("WAKE") && !boat.name.Contains("Wake"))
                {
                    allBoatsList.Add(boat);
                }
            }
            
            // 1. 모든 선박 비활성화
            if (defenseAgent1 != null)
            {
                try
                {
                    defenseAgent1.gameObject.SetActive(false);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[DefenseEnvController] defenseAgent1 비활성화 중 예외 발생: {ex.Message}");
                }
            }
            if (defenseAgent2 != null)
            {
                try
                {
                    defenseAgent2.gameObject.SetActive(false);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[DefenseEnvController] defenseAgent2 비활성화 중 예외 발생: {ex.Message}");
                }
            }
            
            // 태그가 "boat"인 모든 선박 비활성화 (안전하게 처리)
            foreach (var boat in allBoatsList)
            {
                if (boat != null && boat.activeSelf)
                {
                    try
                    {
                        if (boat.name.Contains("WAKE") || boat.name.Contains("Wake"))
                            continue;
                        boat.SetActive(false);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 비활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
            
            // 적군 선박들 (attack_boat 태그) 비활성화 (안전하게 처리)
            foreach (var boat in _attackBoats)
            {
                if (boat != null && boat.activeSelf)
                {
                    try
                    {
                        boat.SetActive(false);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 비활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
            
            // DefenseEnvController의 enemyShips 배열에 있는 적군들도 비활성화 (안전하게 처리)
            if (enemyShips != null)
            {
                foreach (var enemy in enemyShips)
                {
                    if (enemy != null && enemy.activeSelf)
                    {
                        try
                        {
                            enemy.SetActive(false);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[DefenseEnvController] {enemy?.name ?? "Unknown"} 비활성화 중 예외 발생: {ex.Message}");
                        }
                    }
                }
            }
            
            // 여러 프레임 대기 (비활성화가 완전히 적용되도록, NativeArray dispose 완료 대기)
            yield return null;
            yield return null;
            
            // 2. 위치 리셋
            ResetPositions();
            
            // 적군 선박 리셋 (attack_boat 태그)
            ResetAttackBoatsToOrigin();
            
            // WebDetector 리셋 (클래스가 존재하지 않으므로 주석 처리)
            // if (_webDetector != null)
            // {
            //     _webDetector.ResetDetector();
            // }
            
            // 한 프레임 대기 (위치 리셋이 완전히 적용되도록)
            yield return null;
            
            // 3. 모든 선박 활성화 (안전하게 처리)
            if (defenseAgent1 != null)
            {
                try
                {
                    defenseAgent1.gameObject.SetActive(true);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[DefenseEnvController] defenseAgent1 활성화 중 예외 발생: {ex.Message}");
                }
            }
            if (defenseAgent2 != null)
            {
                try
                {
                    defenseAgent2.gameObject.SetActive(true);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[DefenseEnvController] defenseAgent2 활성화 중 예외 발생: {ex.Message}");
                }
            }
            
            // 태그가 "boat"인 모든 선박 활성화 (리셋된 위치에서, 안전하게 처리)
            foreach (var boat in allBoatsList)
            {
                if (boat != null && !boat.activeSelf)
                {
                    try
                    {
                        if (boat.name.Contains("WAKE") || boat.name.Contains("Wake"))
                            continue;
                        boat.SetActive(true);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
            
            // 적군 선박들 활성화 (attack_boat 태그, 안전하게 처리)
            foreach (var boat in _attackBoats)
            {
                if (boat != null && !boat.activeSelf)
                {
                    try
                    {
                        boat.SetActive(true);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
            
            // DefenseEnvController의 enemyShips 배열에 있는 적군들도 활성화 (안전하게 처리)
            if (enemyShips != null)
            {
                foreach (var enemy in enemyShips)
                {
                    if (enemy != null && !enemy.activeSelf)
                    {
                        try
                        {
                            enemy.SetActive(true);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[DefenseEnvController] {enemy?.name ?? "Unknown"} 활성화 중 예외 발생: {ex.Message}");
                        }
                    }
                }
            }
            
            // 여러 프레임 대기 (활성화가 완전히 적용되도록, BuoyantObject 초기화 완료 대기)
            yield return null;
            yield return null;
            
            // Web 활성화 및 위치 설정 (2대 중간)
            if (webObject != null && defenseAgent1 != null && defenseAgent2 != null)
            {
                // Web 활성화
                if (!webObject.activeSelf)
                {
                    try
                    {
                        webObject.SetActive(true);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] webObject 활성화 중 예외 발생: {ex.Message}");
                    }
                }
                
                // Web 위치 설정 (2대 중간)
                Vector3 webPos = (defenseAgent1.transform.position + defenseAgent2.transform.position) / 2f;
                webPos.y = webSpawnPos.y;
                webObject.transform.position = webPos;
                
                if (enableDebugLog)
                {
                    Debug.Log($"[DefenseEnvController] Web 활성화 및 위치 설정 완료: {webPos}");
                }
            }
            else if (webObject != null)
            {
                // 에이전트가 없으면 Web 비활성화
                if (webObject.activeSelf)
                {
                    try
                    {
                        webObject.SetActive(false);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] webObject 비활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] ===== ResetPositionsWithDeactivation() 코루틴 완료: 모든 선박이 원점에서 다시 활성화됨 (Frame {Time.frameCount}) =====");
            }
            
            // 코루틴 실행 완료 플래그 해제 및 에피소드 활성화
            _isResettingPositions = false;
            _episodeActive = true; // 코루틴 완료 후 에피소드 활성화
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] ===== ResetPositionsWithDeactivation() 코루틴 완료 및 에피소드 활성화 (Frame {Time.frameCount}) =====");
            }
        }
        
        /// <summary>
        /// 위치 리셋 (PushBlockEnvController 패턴)
        /// </summary>
        private void ResetPositions()
        {
            // DefenseAgent1 리셋 (PushBlockEnvController 패턴)
            if (_agent1Info.Agent != null && _agent1Info.Rb != null)
            {
                Vector3 pos = UseRandomAgentPosition ? GetRandomSpawnPosition(_agent1Info.StartingPos) : _agent1Info.StartingPos;
                Quaternion rot = UseRandomAgentRotation ? GetRandomRot() : _agent1Info.StartingRot;
                
                _agent1Info.Agent.transform.SetPositionAndRotation(pos, rot);
                _agent1Info.Rb.velocity = Vector3.zero;
                _agent1Info.Rb.angularVelocity = Vector3.zero;
                
                // WAKE 생성 비활성화
                if (_agent1Info.Agent.TryGetComponent<WakeGenerator>(out var wakeGen1))
                {
                    wakeGen1.StopAllCoroutines();
                    wakeGen1.enabled = false;
                }
            }

            // DefenseAgent2 리셋 (PushBlockEnvController 패턴)
            if (_agent2Info.Agent != null && _agent2Info.Rb != null)
            {
                Vector3 pos = UseRandomAgentPosition ? GetRandomSpawnPosition(_agent2Info.StartingPos) : _agent2Info.StartingPos;
                Quaternion rot = UseRandomAgentRotation ? GetRandomRot() : _agent2Info.StartingRot;
                
                _agent2Info.Agent.transform.SetPositionAndRotation(pos, rot);
                _agent2Info.Rb.velocity = Vector3.zero;
                _agent2Info.Rb.angularVelocity = Vector3.zero;
                
                // WAKE 생성 비활성화
                if (_agent2Info.Agent.TryGetComponent<WakeGenerator>(out var wakeGen2))
                {
                    wakeGen2.StopAllCoroutines();
                    wakeGen2.enabled = false;
                }
            }
            
            // 태그가 "boat"인 모든 GameObject를 원점으로 리셋 (WAKE 제외)
            foreach (var boat in _originalBoatPositions.Keys)
            {
                if (boat == null)
                    continue;
                
                // WAKE 객체는 제외
                if (boat.name.Contains("WAKE") || boat.name.Contains("Wake"))
                    continue;
                
                // defenseAgent1, defenseAgent2는 이미 처리됨
                if (boat == defenseAgent1?.gameObject || boat == defenseAgent2?.gameObject)
                    continue;
                
                bool wasActive = boat.activeSelf;
                if (!wasActive)
                {
                    try
                    {
                        boat.SetActive(true);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 활성화 중 예외 발생: {ex.Message}");
                        continue;
                    }
                }
                
                // WAKE 생성 비활성화
                if (boat.TryGetComponent<WakeGenerator>(out var wakeGenerator))
                {
                    wakeGenerator.StopAllCoroutines();
                    wakeGenerator.enabled = false;
                }
                
                // Rigidbody 리셋
                if (boat.TryGetComponent<Rigidbody>(out var rb))
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                
                // 초기 위치로 리셋
                Vector3 originalPos = _originalBoatPositions.ContainsKey(boat) ? _originalBoatPositions[boat] : boat.transform.position;
                Quaternion originalRot = _originalBoatRotations.ContainsKey(boat) ? _originalBoatRotations[boat] : boat.transform.rotation;
                
                Vector3 spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(originalPos) : originalPos;
                Quaternion spawnRot = enableRandomSpawn ? GetRandomRot() : originalRot;
                
                boat.transform.SetPositionAndRotation(spawnPos, spawnRot);
                
                if (!wasActive)
                {
                    try
                    {
                        boat.SetActive(false);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 비활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
            
            // 적군 선박들 리셋 (DefenseEnvController의 enemyShips 배열)
            if (enemyShips != null)
            {
                foreach (var enemy in enemyShips)
                {
                    if (enemy == null) continue;
                    
                    if (enemy.TryGetComponent<Rigidbody>(out var rbEnemy))
                    {
                        rbEnemy.velocity = Vector3.zero;
                        rbEnemy.angularVelocity = Vector3.zero;
                        
                        Vector3 basePos = _originalEnemyPositions.ContainsKey(enemy) ? _originalEnemyPositions[enemy] : enemy.transform.position;
                        Vector3 spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(basePos) : basePos;
                        
                        enemy.transform.SetPositionAndRotation(spawnPos, GetRandomRot());
                    }
                }
            }

            // Web 위치 설정 및 활성화 (2대 중간)
            if (webObject != null && defenseAgent1 != null && defenseAgent2 != null)
            {
                // Web 활성화
                if (!webObject.activeSelf)
                {
                    try
                    {
                        webObject.SetActive(true);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] webObject 활성화 중 예외 발생: {ex.Message}");
                    }
                }
                
                // Web 위치 설정 (2대 중간)
                Vector3 webPos = (defenseAgent1.transform.position + defenseAgent2.transform.position) / 2f;
                webPos.y = webSpawnPos.y;
                webObject.transform.position = webPos;
            }
            else if (webObject != null && (defenseAgent1 == null || defenseAgent2 == null))
            {
                // 에이전트가 없으면 Web 비활성화
                if (webObject.activeSelf)
                {
                    try
                    {
                        webObject.SetActive(false);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] webObject 비활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
        }
        
        /// <summary>
        /// 모든 적군 선박 (attack_boat 태그)을 원점으로 리셋
        /// 에피소드 재시작 시 호출되며, 파괴된 적군 선박도 다시 생성
        /// </summary>
        private void ResetAttackBoatsToOrigin()
        {
            // null이거나 파괴된 객체 제거
            _attackBoats.RemoveAll(boat => boat == null);
            
            // 씬의 모든 attack_boat 다시 찾기
            GameObject[] allAttackBoatsInScene = GameObject.FindGameObjectsWithTag("attack_boat");
            
            // 파괴된 attack_boat 재생성
            int recreatedCount = 0;
            
            // 저장된 프리팹을 사용하여 파괴된 객체 재생성
            foreach (var kvp in _attackBoatPrefabs)
            {
                string boatName = kvp.Key;
                GameObject prefab = kvp.Value;
                
                if (prefab == null) continue;
                
                // 씬에서 같은 이름의 객체를 찾을 수 있는지 확인
                bool foundInScene = false;
                foreach (var boat in allAttackBoatsInScene)
                {
                    string sceneBoatName = boat.name.Replace("(Clone)", "");
                    if (sceneBoatName == boatName)
                    {
                        foundInScene = true;
                        break;
                    }
                }
                
                // 씬에 없으면 재생성 필요
                if (!foundInScene)
                {
                    // 프리팹에서 재생성
                    GameObject recreatedBoat = Instantiate(prefab);
                    recreatedBoat.name = boatName;
                    recreatedBoat.tag = "attack_boat";
                    recreatedBoat.SetActive(true);
                    
                    // 초기 위치 찾기
                    Vector3 initialPos = Vector3.zero;
                    Quaternion initialRot = Quaternion.identity;
                    bool foundInitialPos = false;
                    
                    foreach (var posKvp in _attackBoatInitialPositions)
                    {
                        if (posKvp.Key != null)
                        {
                            string posBoatName = posKvp.Key.name.Replace("(Clone)", "");
                            if (posBoatName == boatName)
                            {
                                initialPos = posKvp.Value;
                                if (_attackBoatInitialRotations.ContainsKey(posKvp.Key))
                                {
                                    initialRot = _attackBoatInitialRotations[posKvp.Key];
                                }
                                foundInitialPos = true;
                                break;
                            }
                        }
                    }
                    
                    if (!foundInitialPos)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boatName}의 초기 위치를 찾을 수 없습니다. (0,0,0) 사용");
                    }
                    
                    Vector3 spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(initialPos) : initialPos;
                    Quaternion spawnRot = enableRandomSpawn ? GetRandomRot() : initialRot;
                    
                    recreatedBoat.transform.SetPositionAndRotation(spawnPos, spawnRot);
                    
                    // Cinemachine Dolly Cart 리셋
                    CinemachineDollyCart dollyCart = recreatedBoat.GetComponent<CinemachineDollyCart>();
                    if (dollyCart != null)
                    {
                        // 원본 경로 찾기
                        CinemachinePathBase originalPath = null;
                        foreach (var pathKvp in _attackBoatInitialPaths)
                        {
                            if (pathKvp.Key != null)
                            {
                                string pathBoatName = pathKvp.Key.name.Replace("(Clone)", "");
                                if (pathBoatName == boatName)
                                {
                                    originalPath = pathKvp.Value;
                                    break;
                                }
                            }
                        }
                        
                        if (originalPath != null)
                        {
                            dollyCart.m_Path = originalPath;
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
                    _attackBoatInitialRotations[recreatedBoat] = initialRot;
                    if (dollyCart != null)
                    {
                        _attackBoatInitialPaths[recreatedBoat] = dollyCart.m_Path;
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
                        _attackBoatInitialPositions[boat] = boat.transform.position;
                        _attackBoatInitialRotations[boat] = boat.transform.rotation;
                        
                        // 원본 프리팹 저장
                        string boatName = boat.name.Replace("(Clone)", "");
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
                        }
                        else
                        {
                            _attackBoatInitialPaths[boat] = null;
                        }
                    }
                }
            }
            
            // 초기 선박 수 업데이트
            if (_attackBoats.Count > _initialAttackBoatCount)
            {
                _initialAttackBoatCount = _attackBoats.Count;
            }
            
            // 모든 선박을 원점(초기 위치)으로 리셋 (PushBlockEnvController 패턴)
            foreach (var boat in _attackBoats)
            {
                if (boat == null) continue;
                
                bool wasActive = boat.activeSelf;
                if (!wasActive)
                {
                    try
                    {
                        boat.SetActive(true);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 활성화 중 예외 발생: {ex.Message}");
                        continue;
                    }
                }
                
                // Rigidbody 리셋
                Rigidbody rb = boat.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                
                // 초기 위치로 리셋
                Vector3 spawnPos;
                if (_attackBoatInitialPositions.ContainsKey(boat))
                {
                    Vector3 basePos = _attackBoatInitialPositions[boat];
                    spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(basePos) : basePos;
                }
                else
                {
                    spawnPos = boat.transform.position;
                    _attackBoatInitialPositions[boat] = spawnPos;
                }
                
                Quaternion spawnRot;
                if (_attackBoatInitialRotations.ContainsKey(boat))
                {
                    Quaternion baseRot = _attackBoatInitialRotations[boat];
                    spawnRot = enableRandomSpawn ? GetRandomRot() : baseRot;
                }
                else
                {
                    spawnRot = boat.transform.rotation;
                    _attackBoatInitialRotations[boat] = spawnRot;
                }
                
                boat.transform.SetPositionAndRotation(spawnPos, spawnRot);
                
                // Cinemachine Dolly Cart 리셋
                CinemachineDollyCart dollyCart = boat.GetComponent<CinemachineDollyCart>();
                if (dollyCart != null)
                {
                    if (_attackBoatInitialPaths.ContainsKey(boat) && _attackBoatInitialPaths[boat] != null)
                    {
                        dollyCart.m_Path = _attackBoatInitialPaths[boat];
                        dollyCart.m_Position = 0f;
                    }
                }
                
                if (!wasActive)
                {
                    try
                    {
                        boat.SetActive(false);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat?.name ?? "Unknown"} 비활성화 중 예외 발생: {ex.Message}");
                    }
                }
            }
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] ResetAttackBoatsToOrigin() 완료: 총 {_attackBoats.Count}개의 적군 선박 리셋 (재생성: {recreatedCount}개)");
            }
        }
        
        /// <summary>
        /// PushBlockEnvController 패턴: 랜덤 각도 생성
        /// </summary>
        Quaternion GetRandomRot()
        {
            return Quaternion.Euler(0, Random.Range(0.0f, 360.0f), 0);
        }
        
        /// <summary>
        /// PushBlockEnvController 패턴: 랜덤 스폰 위치 생성
        /// </summary>
        private Vector3 GetRandomSpawnPosition(Vector3 originalPos)
        {
            float randomX = originalPos.x + Random.Range(-spawnRange, spawnRange);
            float randomZ = originalPos.z + Random.Range(-spawnRange, spawnRange);
            return new Vector3(randomX, originalPos.y, randomZ);
        }
        
        #endregion

        #region WAKE 객체 관리
        
        /// <summary>
        /// 씬에 있는 모든 WAKE(Clone) 객체를 찾아서 파괴하고, WakeGenerator 컴포넌트를 완전히 비활성화
        /// </summary>
        private void DestroyAllWakeObjects()
        {
            int destroyedCount = 0;
            
            // 씬에 있는 모든 GameObject를 찾아서 "Wake(Clone)" 이름을 가진 것들을 파괴
            GameObject[] allObjects = FindObjectsOfType<GameObject>(true); // true = 비활성화된 객체도 포함
            foreach (var obj in allObjects)
            {
                if (obj != null && obj.name.Contains("Wake") && (obj.name.Contains("Clone") || obj.name.Contains("(Clone)")))
                {
#if UNITY_EDITOR
                    DestroyImmediate(obj);
#else
                    Destroy(obj);
#endif
                    destroyedCount++;
                }
            }
            
            // 모든 WakeGenerator 컴포넌트를 찾아서 비활성화하고 코루틴 중지
            WakeGenerator[] allWakeGenerators = FindObjectsOfType<WakeGenerator>(true); // true = 비활성화된 객체도 포함
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
            
            if (destroyedCount > 0 || allWakeGenerators.Length > 0)
            {
                if (enableDebugLog)
                {
                    Debug.Log($"[DefenseEnvController] {destroyedCount}개의 WAKE(Clone) 객체를 파괴하고 {allWakeGenerators.Length}개의 WakeGenerator를 비활성화했습니다.");
                }
            }
        }
        
        #endregion

        #region Web 생성 및 관리
        
        /// <summary>
        /// Web 초기화 (자동 생성 또는 기존 Web 설정)
        /// </summary>
        private void InitializeWeb()
        {
            if (defenseAgent1 == null || defenseAgent2 == null)
            {
                if (enableDebugLog)
                {
                    Debug.LogWarning("[DefenseEnvController] 방어 에이전트가 없어 Web을 생성할 수 없습니다.");
                }
                return;
            }
            
            // Web이 없고 자동 생성이 활성화되어 있으면 생성
            if (webObject == null && autoCreateWeb)
            {
                CreateWeb();
            }
            // Web이 있으면 컴포넌트 참조 가져오기
            else if (webObject != null)
            {
                SetupWebComponents();
            }
            
            // Web 초기 위치 설정
            UpdateWeb();
        }
        
        /// <summary>
        /// Web GameObject 생성
        /// </summary>
        private void CreateWeb()
        {
            // Web GameObject 생성
            webObject = new GameObject("DefenseWeb");
            webObject.transform.SetParent(transform);
            
            // MeshFilter 추가
            _webMeshFilter = webObject.AddComponent<MeshFilter>();
            
            // MeshRenderer 추가
            _webRenderer = webObject.AddComponent<MeshRenderer>();
            
            // Material 생성 및 설정
            _webMaterial = new Material(Shader.Find("Standard"));
            _webMaterial.SetFloat("_Mode", 3); // Transparent 모드
            _webMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _webMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _webMaterial.SetInt("_ZWrite", 0);
            _webMaterial.DisableKeyword("_ALPHATEST_ON");
            _webMaterial.EnableKeyword("_ALPHABLEND_ON");
            _webMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            _webMaterial.renderQueue = 3000;
            _webMaterial.color = webColor;
            _webRenderer.material = _webMaterial;
            
            // BoxCollider 추가 (충돌 감지용)
            _webCollider = webObject.AddComponent<BoxCollider>();
            _webCollider.isTrigger = true;
            
            // Rigidbody 추가 (Kinematic, 충돌 감지를 위해 필요할 수 있음)
            Rigidbody webRb = webObject.AddComponent<Rigidbody>();
            webRb.isKinematic = true;
            webRb.useGravity = false;
            
            // Web 충돌 감지 컴포넌트 추가
            WebCollisionHandler webHandler = webObject.AddComponent<WebCollisionHandler>();
            webHandler.envController = this;
            
            // Layer 설정 (선택사항)
            webObject.layer = LayerMask.NameToLayer("Default");
            
            if (enableDebugLog)
            {
                Debug.Log("[DefenseEnvController] Web 충돌 감지 설정 완료 (WebCollisionHandler 추가됨)");
            }
            
            if (enableDebugLog)
            {
                Debug.Log("[DefenseEnvController] Web GameObject 자동 생성 완료");
            }
        }
        
        /// <summary>
        /// Web 컴포넌트 참조 설정
        /// </summary>
        private void SetupWebComponents()
        {
            if (webObject == null)
                return;
            
            _webMeshFilter = webObject.GetComponent<MeshFilter>();
            _webRenderer = webObject.GetComponent<MeshRenderer>();
            _webCollider = webObject.GetComponent<BoxCollider>();
            
            // MeshFilter가 없으면 추가
            if (_webMeshFilter == null)
            {
                _webMeshFilter = webObject.AddComponent<MeshFilter>();
            }
            
            // MeshRenderer가 없으면 추가
            if (_webRenderer == null)
            {
                _webRenderer = webObject.AddComponent<MeshRenderer>();
            }
            
            // Material 설정
            if (_webRenderer.material != null)
            {
                _webMaterial = _webRenderer.material;
                _webMaterial.color = webColor;
            }
            else
            {
                _webMaterial = new Material(Shader.Find("Standard"));
                _webMaterial.color = webColor;
                _webRenderer.material = _webMaterial;
            }
            
            // BoxCollider가 없으면 추가
            if (_webCollider == null)
            {
                _webCollider = webObject.AddComponent<BoxCollider>();
                _webCollider.isTrigger = true;
            }
            else
            {
                // Collider가 이미 있으면 isTrigger 확인
                if (!_webCollider.isTrigger)
                {
                    _webCollider.isTrigger = true;
                }
            }
            
            // Web 충돌 감지 컴포넌트 확인 및 추가
            WebCollisionHandler webHandler = webObject.GetComponent<WebCollisionHandler>();
            if (webHandler == null)
            {
                webHandler = webObject.AddComponent<WebCollisionHandler>();
                webHandler.envController = this;
                
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] WebCollisionHandler 컴포넌트 추가됨");
                }
            }
            else
            {
                // 이미 있으면 envController 참조 업데이트
                webHandler.envController = this;
            }
        }
        
        /// <summary>
        /// Web 업데이트 (위치, 크기, 회전)
        /// </summary>
        private void UpdateWeb()
        {
            if (webObject == null || defenseAgent1 == null || defenseAgent2 == null)
            {
                // Web 비활성화
                if (webObject != null && webObject.activeSelf)
                {
                    webObject.SetActive(false);
                }
                return;
            }
            
            // 두 선박 사이 거리 계산
            Vector3 pos1 = defenseAgent1.transform.position;
            Vector3 pos2 = defenseAgent2.transform.position;
            float distance = Vector3.Distance(pos1, pos2);
            
            // 거리 체크 (너무 가깝거나 멀면 Web 비활성화)
            if (distance < webMinDistance || distance > webMaxDistance)
            {
                if (webObject.activeSelf)
                {
                    webObject.SetActive(false);
                }
                return;
            }
            
            // Web 활성화
            if (!webObject.activeSelf)
            {
                webObject.SetActive(true);
            }
            
            // Web 중간 위치 계산
            Vector3 webPos = (pos1 + pos2) / 2f;
            webPos.y = Mathf.Max(pos1.y, pos2.y) + webSpawnPos.y;
            
            // Web 회전 계산 (두 선박을 연결하는 방향)
            Vector3 direction = (pos2 - pos1).normalized;
            if (direction != Vector3.zero)
            {
                webObject.transform.rotation = Quaternion.LookRotation(Vector3.Cross(direction, Vector3.up), Vector3.up);
            }
            
            // Web 위치 설정
            webObject.transform.position = webPos;
            
            // Web 크기 계산 및 설정
            float webWidth = distance; // 두 선박 사이 거리
            float webDepth = webThickness;
            
            // Mesh 생성 또는 업데이트
            CreateWebMesh(webWidth, webHeight, webDepth);
            
            // Collider 크기 설정
            if (_webCollider != null)
            {
                _webCollider.size = new Vector3(webWidth, webHeight, webDepth);
                _webCollider.center = Vector3.zero;
            }
        }
        
        /// <summary>
        /// Web Mesh 생성 (Quad 기반)
        /// </summary>
        private void CreateWebMesh(float width, float height, float depth)
        {
            if (_webMeshFilter == null)
                return;
            
            Mesh mesh = new Mesh();
            mesh.name = "DefenseWebMesh";
            
            // Vertices (중심을 기준으로)
            float halfWidth = width / 2f;
            float halfHeight = height / 2f;
            
            Vector3[] vertices = new Vector3[4]
            {
                new Vector3(-halfWidth, -halfHeight, 0f), // 왼쪽 아래
                new Vector3(halfWidth, -halfHeight, 0f),   // 오른쪽 아래
                new Vector3(-halfWidth, halfHeight, 0f),  // 왼쪽 위
                new Vector3(halfWidth, halfHeight, 0f)    // 오른쪽 위
            };
            
            // Triangles (두 개의 삼각형으로 Quad 구성)
            int[] triangles = new int[6]
            {
                0, 2, 1, // 첫 번째 삼각형
                2, 3, 1  // 두 번째 삼각형
            };
            
            // UVs (텍스처 좌표)
            Vector2[] uvs = new Vector2[4]
            {
                new Vector2(0, 0), // 왼쪽 아래
                new Vector2(1, 0), // 오른쪽 아래
                new Vector2(0, 1), // 왼쪽 위
                new Vector2(1, 1)  // 오른쪽 위
            };
            
            // Normals (앞면을 향하도록)
            Vector3[] normals = new Vector3[4]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.forward
            };
            
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uvs;
            mesh.normals = normals;
            
            _webMeshFilter.mesh = mesh;
        }
        
        /// <summary>
        /// 적군 선박이 Web과 충돌했을 때 호출
        /// </summary>
        public void OnEnemyHitWeb(GameObject enemyBoat)
        {
            if (!_episodeActive || enemyBoat == null)
                return;
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 적군 선박이 Web과 충돌: {enemyBoat.name}");
            }
            
            // 폭발 효과 발동
            CreateExplosionEffect(enemyBoat.transform.position);
            
            // 적군 선박 파괴 요청
            RequestAttackBoatDestruction(enemyBoat);
            
            // 에피소드 재시작 (적 포획 성공)
            RestartEpisode("EnemyHitWeb", captureReward);
        }
        
        /// <summary>
        /// 적군 선박이 MotherShip과 충돌했을 때 호출
        /// </summary>
        public void OnEnemyHitMotherShip(GameObject enemyBoat)
        {
            if (!_episodeActive || enemyBoat == null)
                return;
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 적군 선박이 MotherShip과 충돌: {enemyBoat.name}");
            }
            
            // 폭발 효과 발동
            CreateExplosionEffect(enemyBoat.transform.position);
            
            // 적군 선박 파괴 요청
            RequestAttackBoatDestruction(enemyBoat);
            
            // 에피소드 재시작 (모선 충돌 - 패널티)
            RestartEpisode("EnemyHitMotherShip", motherShipCollisionPenalty);
        }
        
        /// <summary>
        /// 폭발 효과 생성
        /// </summary>
        private void CreateExplosionEffect(Vector3 position)
        {
            if (explosionPrefab != null)
            {
                // Prefab에서 폭발 효과 생성
                GameObject explosion = Instantiate(explosionPrefab, position, Quaternion.identity);
                explosion.transform.localScale = Vector3.one * explosionScale;
                
                // 일정 시간 후 폭발 효과 제거
                Destroy(explosion, explosionDuration);
            }
            else
            {
                // Prefab이 없으면 간단한 파티클 효과 생성
                CreateSimpleExplosionEffect(position);
            }
        }
        
        /// <summary>
        /// 간단한 폭발 효과 생성 (Prefab이 없을 때)
        /// </summary>
        private void CreateSimpleExplosionEffect(Vector3 position)
        {
            // 간단한 파티클 효과 GameObject 생성
            GameObject explosion = new GameObject("ExplosionEffect");
            explosion.transform.position = position;
            explosion.transform.localScale = Vector3.one * explosionScale;
            
            // ParticleSystem 추가
            ParticleSystem ps = explosion.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = 1f;
            main.startSpeed = 20f;
            main.startSize = 2f;
            main.startColor = Color.red;
            main.maxParticles = 100;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            
            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0f, 100)
            });
            
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 1f;
            
            var velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = true;
            velocityOverLifetime.space = ParticleSystemSimulationSpace.Local;
            velocityOverLifetime.radial = new ParticleSystem.MinMaxCurve(10f);
            
            // 일정 시간 후 제거
            Destroy(explosion, explosionDuration);
        }
        
        #endregion
    }
    
    /// <summary>
    /// Web 충돌 감지 핸들러 (Web GameObject에 추가)
    /// </summary>
    public class WebCollisionHandler : MonoBehaviour
    {
        [HideInInspector]
        public DefenseEnvController envController;
        
        private void OnTriggerEnter(Collider other)
        {
            if (envController == null)
            {
                Debug.LogWarning("[WebCollisionHandler] envController가 null입니다!");
                return;
            }
            
            if (other == null)
                return;
            
            // 디버그 로그
            if (envController.enableDebugLog)
            {
                Debug.Log($"[WebCollisionHandler] OnTriggerEnter 호출됨: {other.name}, Tag: {other.tag}");
            }
            
            // attack_boat 태그를 가진 적군 선박인지 확인
            if (other.CompareTag("attack_boat"))
            {
                if (envController.enableDebugLog)
                {
                    Debug.Log($"[WebCollisionHandler] attack_boat 충돌 감지: {other.name}");
                }
                envController.OnEnemyHitWeb(other.gameObject);
            }
            else
            {
                if (envController.enableDebugLog)
                {
                    Debug.Log($"[WebCollisionHandler] attack_boat 태그가 아님: {other.name}, Tag: {other.tag}");
                }
            }
        }
    }
    
    /// <summary>
    /// MotherShip 충돌 감지 핸들러 (MotherShip GameObject에 추가)
    /// </summary>
    public class MotherShipCollisionHandler : MonoBehaviour
    {
        [HideInInspector]
        public DefenseEnvController envController;
        
        private void OnCollisionEnter(Collision collision)
        {
            if (envController == null)
            {
                Debug.LogWarning("[MotherShipCollisionHandler] envController가 null입니다!");
                return;
            }
            
            if (collision == null || collision.gameObject == null)
                return;
            
            // 디버그 로그
            if (envController.enableDebugLog)
            {
                Debug.Log($"[MotherShipCollisionHandler] OnCollisionEnter 호출됨: {collision.gameObject.name}, Tag: {collision.gameObject.tag}");
            }
            
            // attack_boat 태그를 가진 적군 선박인지 확인
            if (collision.gameObject.CompareTag("attack_boat"))
            {
                if (envController.enableDebugLog)
                {
                    Debug.Log($"[MotherShipCollisionHandler] attack_boat 충돌 감지: {collision.gameObject.name}");
                }
                envController.OnEnemyHitMotherShip(collision.gameObject);
            }
        }
        
        private void OnTriggerEnter(Collider other)
        {
            if (envController == null)
            {
                Debug.LogWarning("[MotherShipCollisionHandler] envController가 null입니다!");
                return;
            }
            
            if (other == null)
                return;
            
            // 디버그 로그
            if (envController.enableDebugLog)
            {
                Debug.Log($"[MotherShipCollisionHandler] OnTriggerEnter 호출됨: {other.name}, Tag: {other.tag}");
            }
            
            // attack_boat 태그를 가진 적군 선박인지 확인
            if (other.CompareTag("attack_boat"))
            {
                if (envController.enableDebugLog)
                {
                    Debug.Log($"[MotherShipCollisionHandler] attack_boat 충돌 감지: {other.name}");
                }
                envController.OnEnemyHitMotherShip(other.gameObject);
            }
        }
    }
}
