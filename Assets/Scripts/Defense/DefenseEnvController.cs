using UnityEngine;
using Unity.MLAgents;
using Cinemachine;
using System.Linq;

namespace BoatAttack
{
    /// <summary>
    /// 방어 환경 컨트롤러 (중앙 허브 버전)
    /// - 모든 에피소드 재시작 로직을 중앙에서 관리
    /// - 그룹 보상 분배 및 환경 관리
    /// - 선박 위치 리셋 및 적군 추적
    /// - 에피소드 시작/종료 관리
    /// </summary>
    public class DefenseEnvController : MonoBehaviour
    {
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

        [Tooltip("방어 선박 2 초기 위치")]
        public Vector3 defense2SpawnPos = new Vector3(0f, -8f, -6f);

        [Tooltip("Web 오브젝트 위치 (2대 중간)")]
        public Vector3 webSpawnPos = new Vector3(0f, 0.8f, 0f);
        
        [Header("Random Spawn Settings")]
        [Tooltip("랜덤 스폰 범위 (기존 위치에서 ±range 범위로 랜덤 생성)")]
        public float spawnRange = 20f;
        
        [Tooltip("랜덤 스폰 활성화 (에피소드 시작 시 랜덤 위치로 재생성)")]
        public bool enableRandomSpawn = true;
        
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
        
        private int _resetTimer = 0; // FixedUpdate 기반 타이머
        private bool _episodeActive = false;
        private bool _episodeEnding = false; // 에피소드 종료 중 플래그 (중복 호출 방지)
        private bool _enemyEnteredDefenseZone = false;
        private bool _boundary2kmBreached = false;
        private bool _boundary1kmBreached = false;
        
        // 위치 리셋 관련
        private int _lastResetFrame = -1; // 중복 리셋 방지용
        private bool _isResettingPositions = false; // 위치 리셋 코루틴 실행 중 플래그
        private WebCollisionDetector _webDetector;
        
        // 원래 위치 및 각도 저장 (랜덤 스폰용)
        private Vector3 _originalDefense1Pos;
        private Vector3 _originalDefense2Pos;
        private Quaternion _originalDefense1Rot;
        private Quaternion _originalDefense2Rot;
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
        private System.Collections.Generic.HashSet<string> _destroyedAttackBoatNames = new System.Collections.Generic.HashSet<string>(); // 파괴된 attack_boat 이름 추적
        private int _initialAttackBoatCount = 0;
        
        private void Start()
        {
            // SimpleMultiAgentGroup 초기화 (선택사항)
            if (m_AgentGroup == null)
            {
                m_AgentGroup = GetComponent<SimpleMultiAgentGroup>();
            }
            
            // 에이전트 등록 (SimpleMultiAgentGroup이 있는 경우만)
            if (m_AgentGroup != null)
            {
                if (defenseAgent1 != null)
                    m_AgentGroup.RegisterAgent(defenseAgent1);
                if (defenseAgent2 != null)
                    m_AgentGroup.RegisterAgent(defenseAgent2);
                
                if (enableDebugLog)
                {
                    Debug.Log("[DefenseEnvController] SimpleMultiAgentGroup 사용 - 그룹 보상 모드");
                }
            }
            else
            {
                if (enableDebugLog)
                {
                    Debug.LogWarning("[DefenseEnvController] SimpleMultiAgentGroup이 없습니다. 개별 보상 모드로 작동합니다.");
                }
            }
            
            // RewardCalculator 초기화
            if (rewardCalculator == null)
            {
                rewardCalculator = GetComponent<DefenseRewardCalculator>();
                if (rewardCalculator == null)
                {
                    Debug.LogError("[DefenseEnvController] DefenseRewardCalculator를 찾을 수 없습니다!");
                }
            }
            
            // 모선 찾기
            if (motherShip == null)
            {
                motherShip = GameObject.FindGameObjectWithTag("MotherShip");
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
            }

            // 에이전트 페어링
            if (defenseAgent1 != null && defenseAgent2 != null)
            {
                defenseAgent1.partnerAgent = defenseAgent2;
                defenseAgent2.partnerAgent = defenseAgent1;

                defenseAgent1.webObject = webObject;
                defenseAgent2.webObject = webObject;
                
                // 적군 배열 설정
                if (enemyShips != null && enemyShips.Length > 0)
                {
                    defenseAgent1.enemyShips = enemyShips;
                    defenseAgent2.enemyShips = enemyShips;
                }
            }

            // 원래 위치 및 각도 저장 (첫 시작 시)
            if (defenseAgent1 != null)
            {
                _originalDefense1Pos = defenseAgent1.transform.position;
                _originalDefense1Rot = defenseAgent1.transform.rotation;
                if (_originalDefense1Pos == Vector3.zero)
                {
                    _originalDefense1Pos = defense1SpawnPos;
                }
            }
            else
            {
                _originalDefense1Pos = defense1SpawnPos;
                _originalDefense1Rot = Quaternion.identity;
            }
            
            if (defenseAgent2 != null)
            {
                _originalDefense2Pos = defenseAgent2.transform.position;
                _originalDefense2Rot = defenseAgent2.transform.rotation;
                if (_originalDefense2Pos == Vector3.zero)
                {
                    _originalDefense2Pos = defense2SpawnPos;
                }
            }
            else
            {
                _originalDefense2Pos = defense2SpawnPos;
                _originalDefense2Rot = Quaternion.identity;
            }
            
            // 태그가 "boat"인 모든 GameObject의 초기 위치 및 각도 저장 (WAKE 제외)
            _originalBoatPositions.Clear();
            _originalBoatRotations.Clear();
            GameObject[] allBoats = GameObject.FindGameObjectsWithTag("boat");
            foreach (var boat in allBoats)
            {
                // WAKE 객체는 제외 (파도 효과 등)
                if (boat != null && !boat.name.Contains("WAKE") && !boat.name.Contains("Wake") && !_originalBoatPositions.ContainsKey(boat))
                {
                    _originalBoatPositions[boat] = boat.transform.position;
                    _originalBoatRotations[boat] = boat.transform.rotation;
                    if (enableDebugLog)
                    {
                        Debug.Log($"[DefenseEnvController] Boat 초기 위치 및 각도 저장: {boat.name} at {boat.transform.position}, rotation: {boat.transform.rotation.eulerAngles}");
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
            
            // attack_boat 태그를 가진 모든 적군 선박 찾기 및 초기 위치 저장
            FindAndSaveAttackBoats();
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 초기화 완료 - Defense1: {_originalDefense1Pos}, Defense2: {_originalDefense2Pos}, 총 Boat 수: {_originalBoatPositions.Count}, 적군 선박: {_initialAttackBoatCount}개");
            }
            
            // 첫 에피소드 시작 (PushBlockEnvController 패턴)
            ResetScene();
        }
        
        /// <summary>
        /// PushBlockEnvController 패턴: FixedUpdate에서 타이머 관리
        /// </summary>
        private void FixedUpdate()
        {
            // 에피소드가 비활성화되었거나 종료 중이면 타이머 증가하지 않음
            if (!_episodeActive || _episodeEnding)
                return;
            
            _resetTimer++;
            
            // 최대 스텝 수 체크 (PushBlockEnvController 패턴)
            if (_resetTimer >= maxEnvironmentSteps && maxEnvironmentSteps > 0)
            {
                Debug.LogError($"[DefenseEnvController] 최대 환경 스텝 수 도달 ({maxEnvironmentSteps}). 에피소드 종료.");
                
                // 통합 에피소드 재시작 메서드 호출
                RestartEpisode("MaxEnvironmentSteps");
                return;
            }
            
            // 보상 계산 주기 확인
            if (_resetTimer % rewardCalculationInterval != 0)
                return;
            
            if (defenseAgent1 == null || defenseAgent2 == null || rewardCalculator == null)
                return;
            
            // 1. 관측: 두 에이전트의 상태 수집
            var agent1State = rewardCalculator.GetAgentState(defenseAgent1);
            var agent2State = rewardCalculator.GetAgentState(defenseAgent2);
            
            // 2. 계산: 협동 기동 보상
            float cooperativeReward = rewardCalculator.CalculateCooperativeRewards(
                agent1State, agent2State);
            
            // 3. 계산: 전술 기동 보상
            float tacticalReward = rewardCalculator.CalculateTacticalRewards(
                agent1State, agent2State, enemyShips, webObject);
            
            // 4. 계산: 안전 및 제약 페널티
            float safetyPenalty = rewardCalculator.CalculateSafetyPenalties(
                agent1State, agent2State);
            
            // 5. 계산: 방어선 침범 체크
            float boundaryPenalty = CheckBoundaryBreach();
            
            // 6. 부여: 그룹 보상 또는 개별 보상
            float totalReward = cooperativeReward + tacticalReward + safetyPenalty + boundaryPenalty;
            if (Mathf.Abs(totalReward) > 0.0001f)
            {
                if (m_AgentGroup != null)
                {
                    // 그룹 보상 모드
                    m_AgentGroup.AddGroupReward(totalReward);
                }
                else
                {
                    // 개별 보상 모드 (fallback)
                    if (defenseAgent1 != null)
                        defenseAgent1.AddReward(totalReward);
                    if (defenseAgent2 != null)
                        defenseAgent2.AddReward(totalReward);
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
            Debug.LogError($"[DefenseEnvController] ===== RestartEpisode() 호출됨 (이유: {reason}) =====");
            
            // 중복 호출 방지
            if (_episodeEnding)
            {
                Debug.LogError($"[DefenseEnvController] 에피소드가 이미 종료 중입니다. RestartEpisode() 건너뜀.");
                return;
            }
            
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
            
            // 3단계: 모선 방어 성공 체크 (적이 방어 존에 진입하지 않았을 때)
            if (!_enemyEnteredDefenseZone && motherShip != null && reason != "MotherShipDefense")
            {
                if (m_AgentGroup != null)
                {
                    m_AgentGroup.AddGroupReward(motherShipDefenseReward);
                }
                else
                {
                    if (defenseAgent1 != null)
                        defenseAgent1.AddReward(motherShipDefenseReward);
                    if (defenseAgent2 != null)
                        defenseAgent2.AddReward(motherShipDefenseReward);
                }
                
                Debug.LogError($"[DefenseEnvController] 모선 방어 성공! 보상: {motherShipDefenseReward}");
            }
            
            // 4단계: 에이전트 에피소드 종료
            Debug.LogError("[DefenseEnvController] 에이전트 에피소드 종료 시작...");
            if (m_AgentGroup != null)
            {
                m_AgentGroup.EndGroupEpisode();
                Debug.LogError("[DefenseEnvController] 그룹 에피소드 종료 완료");
            }
            else
            {
                if (defenseAgent1 != null)
                {
                    defenseAgent1.EndEpisode();
                    Debug.LogError("[DefenseEnvController] defenseAgent1.EndEpisode() 호출 완료");
                }
                if (defenseAgent2 != null)
                {
                    defenseAgent2.EndEpisode();
                    Debug.LogError("[DefenseEnvController] defenseAgent2.EndEpisode() 호출 완료");
                }
            }
            
            // 5단계: 환경 리셋
            ResetScene();
            
            Debug.LogError($"[DefenseEnvController] ===== RestartEpisode() 완료 (이유: {reason}) =====");
        }
        
        /// <summary>
        /// PushBlockEnvController 패턴: 환경 리셋 (ML-Agents가 OnEpisodeBegin을 자동 호출)
        /// RestartEpisode()에서 호출됩니다.
        /// </summary>
        public void ResetScene()
        {
            Debug.LogError("[DefenseEnvController] ===== ResetScene() 호출됨 =====");
            
            // 위치 리셋이 이미 진행 중이면 건너뜀
            if (_isResettingPositions)
            {
                Debug.LogError("[DefenseEnvController] ResetScene() 건너뜀 (위치 리셋이 이미 진행 중입니다)");
                return;
            }
            
            // 에피소드 종료 플래그를 먼저 리셋하여 다음 FixedUpdate()에서 타이머가 증가하지 않도록 함
            _episodeEnding = false;
            
            // 에피소드는 코루틴 완료 후 활성화
            _episodeActive = false;
            
            _resetTimer = 0;
            _enemyEnteredDefenseZone = false;
            _boundary2kmBreached = false;
            _boundary1kmBreached = false;
            
            // 파괴된 적군 선박 목록 초기화 (에피소드 재시작 시)
            _destroyedAttackBoatNames.Clear();
            
            // RewardCalculator 리셋
            if (rewardCalculator != null)
            {
                rewardCalculator.Reset();
            }
            
            // 모든 선박 리셋 (비활성화 → 리셋 → 활성화)
            ResetPositionsOnly();
            
            Debug.LogError("[DefenseEnvController] ===== ResetScene() 완료 =====");
        }
        
        #endregion
        
        /// <summary>
        /// 에피소드 시작 (ML-Agents가 자동으로 호출, 환경 리셋은 ResetScene에서 처리)
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
        /// 모선 충돌 처리 (Game Over)
        /// </summary>
        public void OnMotherShipCollision(GameObject enemyBoat)
        {
            if (!_episodeActive)
                return;
            
            // 큰 패널티 부여 (그룹 또는 개별)
            if (m_AgentGroup != null)
            {
                m_AgentGroup.AddGroupReward(motherShipCollisionPenalty);
                m_AgentGroup.EndGroupEpisode();
            }
            else
            {
                if (defenseAgent1 != null)
                {
                    defenseAgent1.AddReward(motherShipCollisionPenalty);
                    defenseAgent1.EndEpisode();
                }
                if (defenseAgent2 != null)
                {
                    defenseAgent2.AddReward(motherShipCollisionPenalty);
                    defenseAgent2.EndEpisode();
                }
            }
            
            // PushBlockEnvController 패턴: 에피소드 종료 후 환경 리셋
            ResetScene();
            
            if (enableDebugLog)
            {
                Debug.Log($"[DefenseEnvController] 모선 충돌! Game Over. 패널티: {motherShipCollisionPenalty}");
            }
        }
        
        /// <summary>
        /// 아군 충돌 처리
        /// </summary>
        public void OnFriendlyCollision()
        {
            if (!_episodeActive)
                return;
            
            // 충돌 패널티 부여 (그룹 또는 개별)
            if (m_AgentGroup != null)
            {
                m_AgentGroup.AddGroupReward(collisionPenalty);
                m_AgentGroup.EndGroupEpisode();
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
        
        /// <summary>
        /// 방어선 침범 체크
        /// </summary>
        private float CheckBoundaryBreach()
        {
            if (motherShip == null || enemyShips == null)
                return 0f;
            
            float totalPenalty = 0f;
            
            foreach (var enemy in enemyShips)
            {
                if (enemy == null) continue;
                
                float distanceToMotherShip = Vector3.Distance(
                    enemy.transform.position, motherShip.transform.position);
                
                // 2km 경계선 체크
                if (!_boundary2kmBreached && distanceToMotherShip <= boundary2km)
                {
                    _boundary2kmBreached = true;
                    totalPenalty += boundaryBreachPenalty;
                }
                
                // 1km 경계선 체크
                if (!_boundary1kmBreached && distanceToMotherShip <= boundary1km)
                {
                    _boundary1kmBreached = true;
                    totalPenalty += boundaryBreachPenalty;
                }
                
                // 모선 방어 존 진입 체크
                if (distanceToMotherShip <= motherShipDefenseRadius)
                {
                    _enemyEnteredDefenseZone = true;
                }
            }
            
            return totalPenalty;
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
                Debug.LogWarning($"[DefenseEnvController] {boatName}은 이미 파괴 요청이 처리되었습니다.");
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
                Debug.LogWarning("[DefenseEnvController] attackBoat가 이미 파괴되었습니다!");
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
            
            Debug.LogError($"[DefenseEnvController] OnAttackBoatDestroyed 호출: {destroyedBoat.name} (이름: {boatName})");
            
            // 초기 적군 선박 수가 0이면 다시 찾기 (Start()에서 찾지 못했을 수 있음)
            if (_initialAttackBoatCount == 0)
            {
                Debug.LogError("[DefenseEnvController] 초기 적군 선박 수가 0입니다. 다시 찾는 중...");
                FindAndSaveAttackBoats();
                Debug.LogError($"[DefenseEnvController] 재검색 완료 - 초기 적군 수: {_initialAttackBoatCount}개");
            }
            
            // 파괴된 선박을 리스트에서 제거
            _attackBoats.Remove(destroyedBoat);
            
            // null이거나 파괴된 객체 제거
            _attackBoats.RemoveAll(boat => boat == null);
            
            // 현재 활성화된 적군 선박 수 확인 (씬에서 직접 찾기, 파괴된 선박 제외)
            GameObject[] allAttackBoatsInScene = GameObject.FindGameObjectsWithTag("attack_boat");
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
            
            Debug.LogError($"[DefenseEnvController] 적군 선박 파괴 후 상태 - 리스트: {_attackBoats.Count}개 (활성화: {activeInList}개), 씬의 활성화된 적군: {activeCount}개 (파괴된 선박 제외), 초기 적군 수: {_initialAttackBoatCount}개, 파괴된 선박: {_destroyedAttackBoatNames.Count}개");
            
            // 모든 적군 선박이 파괴되었는지 확인
            // 조건: 초기 적군 수가 0보다 크고, 씬에 활성화된 적군이 0개이고, 리스트에도 활성화된 선박이 0개
            if (endEpisodeOnAllEnemiesDestroyed && _initialAttackBoatCount > 0 && activeCount == 0 && activeInList == 0)
            {
                // 에피소드가 이미 종료 중인 경우 중복 호출 방지
                if (_episodeEnding)
                {
                    Debug.LogError($"[DefenseEnvController] 에피소드가 이미 종료 중입니다. (_episodeEnding: {_episodeEnding}) RestartEpisode() 호출 건너뜀.");
                    return;
                }
                
                Debug.LogError("[DefenseEnvController] ★★★ 모든 적군 선박 파괴됨! 에피소드 종료 ★★★");
                
                // 통합 에피소드 재시작 메서드 호출
                RestartEpisode("AllEnemiesDestroyed");
            }
            else
            {
                Debug.LogError($"[DefenseEnvController] 에피소드 종료 조건 미충족 - endEpisodeOnAllEnemiesDestroyed: {endEpisodeOnAllEnemiesDestroyed}, _initialAttackBoatCount: {_initialAttackBoatCount}, activeCount: {activeCount}, activeInList: {activeInList}");
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
            // 프리팹 딕셔너리는 초기화하지 않음 (에피소드 재시작 시 재사용)
            
            // 씬의 모든 attack_boat 태그를 가진 객체 찾기
            GameObject[] foundBoats = GameObject.FindGameObjectsWithTag("attack_boat");
            
            // enemyShips 배열 자동 동기화 (인스펙터에 할당된 것과 씬의 실제 객체를 동기화)
            if (enemyShips == null || enemyShips.Length == 0 || enemyShips.All(e => e == null))
            {
                // enemyShips 배열이 비어있으면 자동으로 채움
                enemyShips = new GameObject[foundBoats.Length];
                for (int i = 0; i < foundBoats.Length; i++)
                {
                    enemyShips[i] = foundBoats[i];
                }
                Debug.LogError($"[DefenseEnvController] enemyShips 배열이 비어있어 자동으로 {foundBoats.Length}개 채움");
            }
            else
            {
                // enemyShips 배열에 있는 객체도 _attackBoats에 추가 (중복 방지)
                foreach (var enemyShip in enemyShips)
                {
                    if (enemyShip != null && enemyShip.CompareTag("attack_boat") && !_attackBoats.Contains(enemyShip))
                    {
                        // enemyShips 배열에 있지만 씬에서 찾지 못한 경우도 있으므로 확인
                        bool foundInScene = foundBoats.Contains(enemyShip);
                        if (!foundInScene)
                        {
                            Debug.LogWarning($"[DefenseEnvController] enemyShips 배열의 '{enemyShip.name}'이 씬에서 찾을 수 없습니다.");
                        }
                    }
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
                    if (!_attackBoatPrefabs.ContainsKey(boatName))
                    {
                        // 원본 객체를 복제하여 프리팹으로 저장 (씬에 숨김)
                        GameObject prefabCopy = Instantiate(boat);
                        prefabCopy.name = boatName; // Clone 접미사 제거
                        prefabCopy.SetActive(false); // 비활성화하여 숨김
                        prefabCopy.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave; // Hierarchy에서 숨기고 저장하지 않음
                        _attackBoatPrefabs[boatName] = prefabCopy;
                        
                        if (enableDebugLog)
                        {
                            Debug.Log($"[DefenseEnvController] 적군 선박 프리팹 저장: {boatName}");
                        }
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
                    
                    if (enableDebugLog)
                    {
                        Debug.Log($"[DefenseEnvController] 적군 선박 초기 위치 저장: {boat.name} at {initialPos}");
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
            
            Debug.LogError($"[DefenseEnvController] 적군 선박 {_initialAttackBoatCount}개 발견 및 초기 위치 저장 완료 (enemyShips 배열: {enemyShipsCount}개)");
        }
        
        /// <summary>
        /// 위치만 리셋 (외부에서 호출 가능, ML-Agents 에피소드 재시작 시 사용)
        /// 모든 선박을 비활성화 → 위치 리셋 → 활성화
        /// </summary>
        public void ResetPositionsOnly()
        {
            Debug.LogError($"[DefenseEnvController] ===== ResetPositionsOnly() 호출됨 (Frame {Time.frameCount}) =====");
            
            // 코루틴이 이미 실행 중이면 중복 호출 방지
            if (_isResettingPositions)
            {
                Debug.LogError($"[DefenseEnvController] 위치 리셋 건너뜀 (코루틴이 이미 실행 중입니다: Frame {Time.frameCount})");
                return;
            }
            
            // 중복 호출 방지 (같은 프레임에서 여러 번 호출되는 것 방지)
            if (_lastResetFrame == Time.frameCount)
            {
                Debug.LogError($"[DefenseEnvController] 위치 리셋 건너뜀 (이미 이 프레임에서 리셋됨: Frame {Time.frameCount})");
                return;
            }
            
            _lastResetFrame = Time.frameCount;
            
            // 코루틴 실행 중 플래그 설정 (코루틴 시작 전에 설정하여 중복 실행 방지)
            _isResettingPositions = true;
            
            // _originalBoatPositions 업데이트 (새로 생성된 boat가 있을 수 있음)
            UpdateOriginalBoatPositions();
            
            Debug.LogError($"[DefenseEnvController] ResetPositionsWithDeactivation() 코루틴 시작... (Frame {Time.frameCount})");
            
            // 코루틴으로 비활성화 → 리셋 → 활성화 순서로 진행
            StartCoroutine(ResetPositionsWithDeactivation());
        }
        
        /// <summary>
        /// _originalBoatPositions 딕셔너리 업데이트 (새로 생성된 boat 추가)
        /// </summary>
        private void UpdateOriginalBoatPositions()
        {
            GameObject[] allBoats = GameObject.FindGameObjectsWithTag("boat");
            if (enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] UpdateOriginalBoatPositions(): 현재 씬에 'boat' 태그를 가진 GameObject: {allBoats.Length}개");
            }
            
            int addedCount = 0;
            foreach (var boat in allBoats)
            {
                // WAKE 객체는 제외 (파도 효과 등)
                if (boat != null && !boat.name.Contains("WAKE") && !boat.name.Contains("Wake") && !_originalBoatPositions.ContainsKey(boat))
                {
                    _originalBoatPositions[boat] = boat.transform.position;
                    _originalBoatRotations[boat] = boat.transform.rotation;
                    addedCount++;
                    if (enableDebugLog)
                    {
                        Debug.LogError($"[DefenseEnvController] 새로운 Boat 초기 위치 및 각도 저장: {boat.name} at {boat.transform.position}, rotation: {boat.transform.rotation.eulerAngles}");
                    }
                }
            }
            
            if (addedCount > 0 && enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] {addedCount}개의 새로운 Boat를 _originalBoatPositions에 추가했습니다. 총: {_originalBoatPositions.Count}개");
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
            if (keysToRemove.Count > 0 && enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] {keysToRemove.Count}개의 항목(null 또는 WAKE)을 _originalBoatPositions에서 제거했습니다.");
            }
        }
        
        /// <summary>
        /// 모든 선박을 비활성화 → 위치 리셋 → 활성화하는 코루틴
        /// </summary>
        private System.Collections.IEnumerator ResetPositionsWithDeactivation()
        {
            // 플래그는 ResetPositionsOnly()에서 이미 설정됨
            if (enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] ===== ResetPositionsWithDeactivation() 코루틴 시작 (Frame {Time.frameCount}) =====");
            }
            
            // 모든 WAKE 객체 제거 및 WakeGenerator 비활성화
            DestroyAllWakeObjects();
            
            // 비활성화 전에 모든 boat 리스트 저장 (비활성화 후에는 FindGameObjectsWithTag로 찾을 수 없음)
            // WAKE 객체는 제외 (파도 효과 등)
            System.Collections.Generic.List<GameObject> allBoatsList = new System.Collections.Generic.List<GameObject>();
            GameObject[] allBoatsArray = GameObject.FindGameObjectsWithTag("boat");
            if (enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] 'boat' 태그를 가진 GameObject 발견: {allBoatsArray.Length}개");
            }
            foreach (var boat in allBoatsArray)
            {
                // WAKE 객체는 제외 (파도 효과 등)
                if (boat != null && !boat.name.Contains("WAKE") && !boat.name.Contains("Wake"))
                {
                    allBoatsList.Add(boat);
                }
            }
            
            // 1. 모든 선박 비활성화
            if (defenseAgent1 != null)
            {
                defenseAgent1.gameObject.SetActive(false);
            }
            if (defenseAgent2 != null)
            {
                defenseAgent2.gameObject.SetActive(false);
            }
            
            // 태그가 "boat"인 모든 선박 비활성화 (안전하게 처리)
            foreach (var boat in allBoatsList)
            {
                if (boat != null && boat.activeSelf)
                {
                    try
                    {
                        // WAKE 객체는 제외
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
            
            // WebDetector 리셋
            if (_webDetector != null)
            {
                _webDetector.ResetDetector();
            }
            
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
                        // WAKE 객체는 제외
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
            
            if (enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] ===== ResetPositionsWithDeactivation() 코루틴 완료: 모든 선박이 원점에서 다시 활성화됨 (Frame {Time.frameCount}) =====");
            }
            
            // 코루틴 실행 완료 플래그 해제 및 에피소드 활성화
            _isResettingPositions = false;
            _episodeActive = true; // 코루틴 완료 후 에피소드 활성화
            
            Debug.LogError($"[DefenseEnvController] ===== ResetPositionsWithDeactivation() 코루틴 완료 및 에피소드 활성화 (Frame {Time.frameCount}) =====");
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
                    recreatedBoat.name = boatName; // 원본 이름 유지
                    recreatedBoat.tag = "attack_boat"; // 태그 설정
                    recreatedBoat.SetActive(true); // 활성화
                    
                    // 초기 위치 찾기 (원본 객체의 이름으로 찾기)
                    Vector3 initialPos = Vector3.zero;
                    bool foundInitialPos = false;
                    foreach (var posKvp in _attackBoatInitialPositions)
                    {
                        if (posKvp.Key != null)
                        {
                            string posBoatName = posKvp.Key.name.Replace("(Clone)", "");
                            if (posBoatName == boatName)
                            {
                                initialPos = posKvp.Value;
                                foundInitialPos = true;
                                break;
                            }
                        }
                    }
                    
                    if (!foundInitialPos)
                    {
                        // 초기 위치를 찾을 수 없으면 (0,0,0) 사용
                        Debug.LogWarning($"[DefenseEnvController] {boatName}의 초기 위치를 찾을 수 없습니다. (0,0,0) 사용");
                    }
                    
                    recreatedBoat.transform.position = initialPos;
                    recreatedBoat.transform.rotation = Quaternion.identity;
                    
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
                    if (dollyCart != null)
                    {
                        _attackBoatInitialPaths[recreatedBoat] = dollyCart.m_Path;
                    }
                    
                    recreatedCount++;
                    if (enableDebugLog)
                    {
                        Debug.LogError($"[DefenseEnvController] 적군 선박 재생성: {recreatedBoat.name} at {initialPos}");
                    }
                }
            }
            
            if (enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] ResetAttackBoatsToOrigin(): 씬에서 발견된 attack_boat: {allAttackBoatsInScene.Length}개, 재생성된 선박: {recreatedCount}개");
            }
            
            // 씬의 모든 attack_boat를 _attackBoats 리스트에 추가 (없는 경우만)
            foreach (var boat in allAttackBoatsInScene)
            {
                if (boat != null && !_attackBoats.Contains(boat))
                {
                    _attackBoats.Add(boat);
                    if (enableDebugLog)
                    {
                        Debug.LogError($"[DefenseEnvController] 새로운 적군 선박 발견 및 추가: {boat.name}");
                    }
                    
                    // 초기 위치/경로 저장 (아직 저장되지 않은 경우)
                    if (!_attackBoatInitialPositions.ContainsKey(boat))
                    {
                        _attackBoatInitialPositions[boat] = boat.transform.position;
                        
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
            
            // 초기 선박 수 업데이트 (씬에 있는 선박 수가 더 많으면)
            if (_attackBoats.Count > _initialAttackBoatCount)
            {
                _initialAttackBoatCount = _attackBoats.Count;
                if (enableDebugLog)
                {
                    Debug.LogError($"[DefenseEnvController] 초기 적군 선박 수 업데이트: {_initialAttackBoatCount}개");
                }
            }
            
            // 모든 선박을 원점(초기 위치)으로 리셋
            int resetCount = 0;
            foreach (var boat in _attackBoats)
            {
                if (boat == null) continue;
                
                // 비활성화된 선박도 활성화 (리셋을 위해, 안전하게 처리)
                bool wasActive = boat.activeSelf;
                if (!wasActive)
                {
                    try
                    {
                        boat.SetActive(true);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[DefenseEnvController] {boat.name} 활성화 중 예외 발생: {ex.Message}");
                        continue; // 활성화 실패 시 다음 객체로 넘어감
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
                    spawnPos = _attackBoatInitialPositions[boat];
                }
                else
                {
                    // 초기 위치가 없으면 현재 위치를 초기 위치로 저장
                    spawnPos = boat.transform.position;
                    _attackBoatInitialPositions[boat] = spawnPos;
                    if (enableDebugLog)
                    {
                        Debug.LogError($"[DefenseEnvController] {boat.name}의 초기 위치 저장: {spawnPos}");
                    }
                }
                
                boat.transform.position = spawnPos;
                boat.transform.rotation = Quaternion.identity;
                
                // Cinemachine Dolly Cart 리셋
                CinemachineDollyCart dollyCart = boat.GetComponent<CinemachineDollyCart>();
                if (dollyCart != null)
                {
                    if (_attackBoatInitialPaths.ContainsKey(boat) && _attackBoatInitialPaths[boat] != null)
                    {
                        dollyCart.m_Path = _attackBoatInitialPaths[boat];
                        dollyCart.m_Position = 0f; // Path 시작 위치로 리셋
                    }
                }
                
                resetCount++;
                if (enableDebugLog)
                {
                    Debug.LogError($"[DefenseEnvController] 적군 선박 리셋 ({boat.name}): {spawnPos}");
                }
            }
            
            if (enableDebugLog)
            {
                Debug.LogError($"[DefenseEnvController] ResetAttackBoatsToOrigin() 완료: 총 {resetCount}개의 적군 선박 리셋");
            }
        }
        
        /// <summary>
        /// 씬에 있는 모든 WAKE(Clone) 객체를 찾아서 파괴하고, WakeGenerator 컴포넌트를 완전히 비활성화
        /// </summary>
        private void DestroyAllWakeObjects()
        {
            int destroyedCount = 0;
            
            // 씬에 있는 모든 GameObject를 찾아서 "Wake(Clone)" 이름을 가진 것들을 파괴
            // FindObjectsOfType은 활성화된 객체만 찾지만, FindObjectsOfTypeAll은 비활성화된 객체도 찾습니다
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
        
        /// <summary>
        /// 위치 리셋 (랜덤 스폰 지원)
        /// 비활성화된 GameObject도 리셋 가능하도록 처리
        /// WAKE 객체 생성 비활성화 및 BOAT 객체의 위치/각도 초기화
        /// </summary>
        private void ResetPositions()
        {
            // 모든 WAKE(Clone) 객체 제거
            DestroyAllWakeObjects();
            
            // DefenseAgent1 리셋 (비활성화 상태에서도 가능)
            if (defenseAgent1 != null)
            {
                // Rigidbody는 활성화된 상태에서만 접근 가능
                bool wasActive = defenseAgent1.gameObject.activeSelf;
                if (!wasActive)
                {
                    defenseAgent1.gameObject.SetActive(true);
                }
                
                // WAKE 생성 비활성화 및 기존 WAKE 객체 제거
                if (defenseAgent1.TryGetComponent<WakeGenerator>(out var wakeGen1))
                {
                    // 모든 코루틴 중지
                    wakeGen1.StopAllCoroutines();
                    // 컴포넌트 비활성화
                    wakeGen1.enabled = false;
                }
                
                if (defenseAgent1.TryGetComponent<Rigidbody>(out var rb1))
                {
                    rb1.velocity = Vector3.zero;
                    rb1.angularVelocity = Vector3.zero;
                }
                
                // 원래 위치 사용 (초기화되지 않았으면 spawnPos 사용)
                Vector3 basePos = (_originalDefense1Pos != Vector3.zero) ? _originalDefense1Pos : defense1SpawnPos;
                Vector3 spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(basePos) : basePos;
                
                defenseAgent1.transform.position = spawnPos;
                defenseAgent1.transform.rotation = _originalDefense1Rot;
                
                // 다시 비활성화 (코루틴에서 나중에 활성화할 예정)
                if (!wasActive)
                {
                    defenseAgent1.gameObject.SetActive(false);
                }
            }

            // DefenseAgent2 리셋 (비활성화 상태에서도 가능)
            if (defenseAgent2 != null)
            {
                // Rigidbody는 활성화된 상태에서만 접근 가능
                bool wasActive = defenseAgent2.gameObject.activeSelf;
                if (!wasActive)
                {
                    defenseAgent2.gameObject.SetActive(true);
                }
                
                // WAKE 생성 비활성화 및 기존 WAKE 객체 제거
                if (defenseAgent2.TryGetComponent<WakeGenerator>(out var wakeGen2))
                {
                    // 모든 코루틴 중지
                    wakeGen2.StopAllCoroutines();
                    // 컴포넌트 비활성화
                    wakeGen2.enabled = false;
                }
                
                if (defenseAgent2.TryGetComponent<Rigidbody>(out var rb2))
                {
                    rb2.velocity = Vector3.zero;
                    rb2.angularVelocity = Vector3.zero;
                }
                
                // 원래 위치 사용 (초기화되지 않았으면 spawnPos 사용)
                Vector3 basePos = (_originalDefense2Pos != Vector3.zero) ? _originalDefense2Pos : defense2SpawnPos;
                Vector3 spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(basePos) : basePos;
                
                defenseAgent2.transform.position = spawnPos;
                defenseAgent2.transform.rotation = _originalDefense2Rot;
                
                // 다시 비활성화 (코루틴에서 나중에 활성화할 예정)
                if (!wasActive)
                {
                    defenseAgent2.gameObject.SetActive(false);
                }
            }
            
            // 태그가 "boat"인 모든 GameObject를 원점으로 리셋
            // 비활성화된 GameObject도 리셋하기 위해 _originalBoatPositions의 키 사용
            foreach (var boat in _originalBoatPositions.Keys)
            {
                if (boat == null)
                    continue;
                
                // WAKE 객체는 제외 (파도 효과 등)
                if (boat.name.Contains("WAKE") || boat.name.Contains("Wake"))
                    continue;
                
                // defenseAgent1, defenseAgent2는 이미 처리됨 (중복 방지)
                if (boat == defenseAgent1?.gameObject || boat == defenseAgent2?.gameObject)
                    continue;
                
                // 비활성화 상태에서도 리셋 가능하도록 처리
                bool wasActive = boat.activeSelf;
                if (!wasActive)
                {
                    boat.SetActive(true);
                }
                
                // WAKE 생성 비활성화 및 기존 WAKE 객체 제거
                if (boat.TryGetComponent<WakeGenerator>(out var wakeGenerator))
                {
                    // 모든 코루틴 중지
                    wakeGenerator.StopAllCoroutines();
                    // 컴포넌트 비활성화
                    wakeGenerator.enabled = false;
                }
                
                // Rigidbody 리셋
                if (boat.TryGetComponent<Rigidbody>(out var rb))
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                
                // 초기 위치로 리셋 (인스펙터에 지정된 초기 위치)
                Vector3 originalPos;
                if (_originalBoatPositions.ContainsKey(boat))
                {
                    originalPos = _originalBoatPositions[boat];
                }
                else
                {
                    // 초기 위치가 저장되지 않은 경우, 현재 위치를 저장하지 말고 로그만 출력
                    Debug.LogWarning($"[DefenseEnvController] Boat {boat.name}의 초기 위치가 저장되지 않았습니다. Start()에서 저장된 위치를 사용하세요.");
                    originalPos = boat.transform.position; // 임시로 현재 위치 사용
                    _originalBoatPositions[boat] = originalPos;
                }
                
                // 초기 각도로 리셋
                Quaternion originalRot;
                if (_originalBoatRotations.ContainsKey(boat))
                {
                    originalRot = _originalBoatRotations[boat];
                }
                else
                {
                    originalRot = boat.transform.rotation;
                    _originalBoatRotations[boat] = originalRot;
                }
                
                Vector3 spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(originalPos) : originalPos;
                
                boat.transform.position = spawnPos;
                boat.transform.rotation = originalRot;
                
                // 다시 비활성화 (코루틴에서 나중에 활성화할 예정)
                if (!wasActive)
                {
                    boat.SetActive(false);
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
                        
                        // 원래 위치에서 랜덤 스폰
                        Vector3 basePos;
                        if (_originalEnemyPositions.ContainsKey(enemy))
                        {
                            basePos = _originalEnemyPositions[enemy];
                        }
                        else
                        {
                            basePos = enemy.transform.position;
                            _originalEnemyPositions[enemy] = basePos;
                        }
                        
                        Vector3 spawnPos = enableRandomSpawn ? GetRandomSpawnPosition(basePos) : basePos;
                        enemy.transform.position = spawnPos;
                        enemy.transform.rotation = Quaternion.Euler(0, Random.Range(0f, 360f), 0);
                    }
                }
            }

            // Web 위치 설정 (2대 중간)
            if (webObject != null && defenseAgent1 != null && defenseAgent2 != null)
            {
                Vector3 webPos = (defenseAgent1.transform.position + defenseAgent2.transform.position) / 2f;
                webPos.y = webSpawnPos.y;  // Y 좌표는 고정
                webObject.transform.position = webPos;
            }
            else if (webObject != null)
            {
                webObject.transform.position = webSpawnPos;
            }
        }
        
        /// <summary>
        /// 기존 위치에서 랜덤 스폰 위치 생성
        /// </summary>
        private Vector3 GetRandomSpawnPosition(Vector3 originalPos)
        {
            float randomX = originalPos.x + Random.Range(-spawnRange, spawnRange);
            float randomZ = originalPos.z + Random.Range(-spawnRange, spawnRange);
            return new Vector3(randomX, originalPos.y, randomZ);
        }
        
        #endregion
    }
}
