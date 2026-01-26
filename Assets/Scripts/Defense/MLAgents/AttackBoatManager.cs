using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.MLAgents;
using Cinemachine;

namespace BoatAttack
{
    /// <summary>
    /// 공격 선박 매니저: 모든 attack_boat 태그를 가진 오브젝트를 추적하고,
    /// 모두 파괴되면 에피소드를 종료합니다. 에피소드 재시작 시 파괴된 객체를 다시 생성합니다.
    /// </summary>
    public class AttackBoatManager : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("추적할 공격 선박 태그")]
        public string attackBoatTag = "attack_boat";
        
        [Tooltip("체크 주기 (초)")]
        public float checkInterval = 0.5f;
        
        [Tooltip("에피소드 종료 전 대기 시간 (초)")]
        public float endEpisodeDelay = 2f;
        
        [Header("Respawn Settings")]
        [Tooltip("공격 선박 Prefab (에피소드 재시작 시 재생성용)")]
        public GameObject attackBoatPrefab;
        
        [Tooltip("재생성 시작 위치 (에피소드 재시작 시 이 위치에서 생성)")]
        public Vector3 spawnPosition = new Vector3(0f, 0.8f, 0f);
        
        [Tooltip("재생성 시 여러 위치에 배치할지 여부")]
        public bool useMultipleSpawnPositions = false;
        
        [Tooltip("재생성 위치 배열 (useMultipleSpawnPositions가 true일 때 사용)")]
        public Vector3[] spawnPositions = new Vector3[] { new Vector3(0f, 0.8f, 0f) };
        
        [Header("Debug")]
        [Tooltip("디버그 로그 출력")]
        public bool debugLog = true;
        
        [Tooltip("현재 활성화된 공격 선박 수")]
        [SerializeField] private int _activeBoatCount = 0;
        
        [Tooltip("총 공격 선박 수")]
        [SerializeField] private int _totalBoatCount = 0;

        private List<GameObject> _attackBoats = new List<GameObject>();
        private Dictionary<GameObject, Vector3> _initialPositions = new Dictionary<GameObject, Vector3>(); // 초기 위치 저장 (각 보트의 원래 위치)
        private List<Vector3> _spawnPositions = new List<Vector3>(); // 재생성 위치 리스트 (초기 위치 순서대로)
        private Dictionary<GameObject, CinemachinePathBase> _initialPaths = new Dictionary<GameObject, CinemachinePathBase>(); // 각 보트의 원래 Path 저장
        private List<CinemachinePathBase> _spawnPaths = new List<CinemachinePathBase>(); // 재생성 Path 리스트 (초기 Path 순서대로)
        private Coroutine _checkCoroutine;
        private Coroutine _autoReactivateCoroutine; // 자동 재생성 코루틴 추적
        private bool _isChecking = false;
        private bool _episodeEnded = false; // 에피소드가 종료되었는지 추적
        private HashSet<AttackAgent> _previousActiveAgents = new HashSet<AttackAgent>(); // 이전 프레임의 활성화된 에이전트
        private int _initialBoatCount = 0; // 초기 선박 개수 저장

        private void Start()
        {
            // 초기화
            FindAllAttackBoats();
            
            // 초기 위치 저장
            SaveInitialPositions();
            _initialBoatCount = _attackBoats.Count;
            
            // 체크 코루틴 시작
            if (_attackBoats.Count > 0)
            {
                _checkCoroutine = StartCoroutine(CheckAttackBoatsCoroutine());
                if (debugLog)
                {
                    Debug.Log($"[AttackBoatManager] 공격 선박 {_attackBoats.Count}개 추적 시작");
                }
            }
            else
            {
                Debug.LogWarning($"[AttackBoatManager] '{attackBoatTag}' 태그를 가진 공격 선박을 찾을 수 없습니다!");
            }
        }
        
        /// <summary>
        /// 초기 위치 및 Path 저장 (재생성 시 사용)
        /// </summary>
        private void SaveInitialPositions()
        {
            _initialPositions.Clear();
            _spawnPositions.Clear();
            _initialPaths.Clear();
            _spawnPaths.Clear();
            
            foreach (var boat in _attackBoats)
            {
                if (boat != null)
                {
                    Vector3 initialPos = boat.transform.position;
                    _initialPositions[boat] = initialPos;
                    _spawnPositions.Add(initialPos); // 재생성 위치 리스트에 추가
                    
                    // Cinemachine Dolly Cart의 Path 저장
                    CinemachineDollyCart dollyCart = boat.GetComponent<CinemachineDollyCart>();
                    if (dollyCart != null)
                    {
                        CinemachinePathBase path = dollyCart.m_Path;
                        _initialPaths[boat] = path;
                        _spawnPaths.Add(path); // 재생성 Path 리스트에 추가
                        
                        if (debugLog)
                        {
                            Debug.Log($"[AttackBoatManager] 초기 위치 및 Path 저장: {boat.name} at {initialPos}, Path: {(path != null ? path.name : "None")}");
                        }
                    }
                    else
                    {
                        _initialPaths[boat] = null;
                        _spawnPaths.Add(null);
                        
                        if (debugLog)
                        {
                            Debug.Log($"[AttackBoatManager] 초기 위치 저장: {boat.name} at {initialPos} (Dolly Cart 없음)");
                        }
                    }
                }
            }
        }

        private void OnEnable()
        {
            // 씬에 새로운 공격 선박이 추가될 수 있으므로 주기적으로 다시 찾기
            if (_checkCoroutine == null)
            {
                FindAllAttackBoats();
                if (_attackBoats.Count > 0)
                {
                    _checkCoroutine = StartCoroutine(CheckAttackBoatsCoroutine());
                }
            }
        }

        private void OnDisable()
        {
            if (_checkCoroutine != null)
            {
                StopCoroutine(_checkCoroutine);
                _checkCoroutine = null;
            }
        }

        /// <summary>
        /// 씬에서 모든 attack_boat 태그를 가진 오브젝트 찾기
        /// </summary>
        private void FindAllAttackBoats()
        {
            // null이거나 파괴된 객체 제거
            _attackBoats.RemoveAll(boat => boat == null);
            
            // 씬의 모든 GameObject에서 태그로 찾기
            GameObject[] foundBoats = GameObject.FindGameObjectsWithTag(attackBoatTag);
            
            foreach (var boat in foundBoats)
            {
                if (boat != null && !_attackBoats.Contains(boat))
                {
                    _attackBoats.Add(boat);
                    
                    // 초기 위치가 저장되지 않았다면 저장
                    if (!_initialPositions.ContainsKey(boat))
                    {
                        _initialPositions[boat] = boat.transform.position;
                    }
                }
            }
            
            _totalBoatCount = _attackBoats.Count;
            _activeBoatCount = _attackBoats.Count(boat => boat != null && boat.activeSelf);
            
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] 공격 선박 찾기 완료: 총 {_totalBoatCount}개, 활성화: {_activeBoatCount}개");
            }
        }

        /// <summary>
        /// 주기적으로 공격 선박 상태 체크
        /// </summary>
        private IEnumerator CheckAttackBoatsCoroutine()
        {
            _isChecking = true;
            
            while (_isChecking)
            {
                yield return new WaitForSeconds(checkInterval);
                
                // null이거나 파괴된 오브젝트 제거
                _attackBoats.RemoveAll(boat => boat == null);
                
                // 활성화된 공격 선박 수 계산
                _activeBoatCount = _attackBoats.Count(boat => boat.activeSelf);
                
                // 에피소드가 종료된 후, 새로운 에피소드가 시작되었는지 확인
                if (_episodeEnded)
                {
                    // 모든 공격 선박이 비활성화되어 있고, Academy가 새로운 에피소드를 시작하려고 할 때
                    // 먼저 모든 선박을 활성화해야 OnEpisodeBegin()이 호출될 수 있습니다.
                    
                    // Academy가 에피소드를 재시작하려고 하는지 확인
                    // Unity ML-Agents는 에이전트가 활성화되어 있을 때만 OnEpisodeBegin()을 호출합니다.
                    // 따라서 비활성화된 객체는 OnEpisodeBegin()이 호출되지 않습니다.
                    
                    // 해결책: 에피소드가 종료된 후 일정 시간이 지나면 자동으로 모든 선박을 다시 활성화
                    // 또는 Academy의 에피소드 재시작을 감지하여 선박을 활성화
                    
                    // AttackAgent가 다시 활성화되었는지 확인 (에피소드 재시작 감지)
                    AttackAgent[] currentAgents = FindObjectsOfType<AttackAgent>(true); // 비활성화된 객체도 포함
                    HashSet<AttackAgent> currentActiveAgents = new HashSet<AttackAgent>(
                        currentAgents.Where(agent => agent != null && agent.gameObject.activeSelf && agent.enabled)
                    );
                    
                    // 새로운 에이전트가 활성화되었거나, 이전에 없던 에이전트가 나타났는지 확인
                    bool newAgentActivated = currentActiveAgents.Any(agent => !_previousActiveAgents.Contains(agent));
                    bool anyAgentActive = currentActiveAgents.Count > 0;
                    
                    // 모든 선박이 파괴되었고, 에피소드가 종료된 상태라면 재생성 시도
                    if (_activeBoatCount == 0 && _episodeEnded)
                    {
                        // 에피소드 재시작 감지: 새로운 에이전트가 활성화되었거나, 
                        // 또는 일정 시간이 지났거나, 또는 Academy가 에피소드를 재시작하려고 할 때
                        if (newAgentActivated || (anyAgentActive && _previousActiveAgents.Count == 0))
                        {
                            // 에피소드가 재시작되었고, 모든 선박이 파괴되어 있으면 재생성
                            if (debugLog)
                            {
                                Debug.Log("[AttackBoatManager] 에피소드 재시작 감지! 모든 공격 선박 재생성...");
                            }
                            
                            ReactivateAllBoats();
                            _episodeEnded = false;
                        }
                        else if (!IsAutoReactivateRunning())
                        {
                            // 에피소드가 종료된 후 일정 시간이 지나면 자동으로 모든 선박을 재생성
                            StartCoroutine(AutoReactivateAfterDelay());
                        }
                    }
                    
                    // 현재 활성화된 에이전트 저장
                    _previousActiveAgents = currentActiveAgents;
                }
                else
                {
                    // 에피소드가 진행 중일 때도 현재 활성화된 에이전트 추적 (비활성화된 객체도 포함)
                    AttackAgent[] currentAgents = FindObjectsOfType<AttackAgent>(true);
                    _previousActiveAgents = new HashSet<AttackAgent>(
                        currentAgents.Where(agent => agent != null && agent.gameObject.activeSelf && agent.enabled)
                    );
                }
                
                if (debugLog && Time.frameCount % 60 == 0) // 1초마다 로그 출력 (60fps 가정)
                {
                    Debug.Log($"[AttackBoatManager] 활성화된 공격 선박: {_activeBoatCount}/{_totalBoatCount}");
                }
                
                // 모든 공격 선박이 파괴되었는지 확인
                if (_activeBoatCount == 0 && _initialBoatCount > 0 && !_episodeEnded)
                {
                    if (debugLog)
                    {
                        Debug.Log($"[AttackBoatManager] 모든 공격 선박이 파괴되었습니다! (초기: {_initialBoatCount}개, 현재: {_attackBoats.Count}개) 에피소드 종료 시작...");
                    }
                    
                    // 딜레이 후 에피소드 종료
                    yield return new WaitForSeconds(endEpisodeDelay);
                    
                    EndAllEpisodes();
                    _episodeEnded = true;
                    
                    // 에피소드 종료 직후 재생성 시도
                    if (debugLog)
                    {
                        Debug.Log("[AttackBoatManager] 에피소드 종료 완료. 재생성 준비...");
                    }
                    
                    // 즉시 재생성 시도 (에피소드가 바로 재시작될 수 있으므로)
                    StartCoroutine(ImmediateRespawnCheck());
                    
                    // 체크는 계속 진행 (에피소드 재시작 감지를 위해)
                }
                
                // 새로운 공격 선박이 추가되었을 수 있으므로 주기적으로 다시 찾기
                if (Time.frameCount % 300 == 0) // 5초마다 (300fps 가정)
                {
                    int previousCount = _attackBoats.Count;
                    FindAllAttackBoats();
                    
                    if (_attackBoats.Count != previousCount && debugLog)
                    {
                        Debug.Log($"[AttackBoatManager] 공격 선박 수 변경: {previousCount} -> {_attackBoats.Count}");
                    }
                }
            }
        }

        /// <summary>
        /// 모든 AttackAgent의 에피소드 종료
        /// </summary>
        private void EndAllEpisodes()
        {
            // 씬의 모든 AttackAgent 찾기
            AttackAgent[] agents = FindObjectsOfType<AttackAgent>();
            
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] {agents.Length}개의 AttackAgent 발견. 모든 에피소드 종료...");
            }
            
            foreach (var agent in agents)
            {
                if (agent != null && agent.gameObject.activeSelf)
                {
                    try
                    {
                        agent.EndEpisode();
                        if (debugLog)
                        {
                            Debug.Log($"[AttackBoatManager] {agent.gameObject.name}의 에피소드 종료");
                        }
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[AttackBoatManager] {agent.gameObject.name}의 에피소드 종료 실패: {e.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 공격 선박이 비활성화되었을 때 호출 (외부에서 호출 가능)
        /// </summary>
        public void OnAttackBoatDisabled(GameObject boat)
        {
            if (boat == null)
            {
                return;
            }
            
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] 공격 선박 비활성화 감지: {boat.name}");
            }
            
            // 즉시 체크 (코루틴이 다음 체크까지 기다리지 않도록)
            CheckAllBoatsDisabled();
        }

        /// <summary>
        /// 모든 공격 선박이 파괴되었는지 즉시 확인
        /// </summary>
        private void CheckAllBoatsDisabled()
        {
            // null이거나 파괴된 오브젝트 제거
            _attackBoats.RemoveAll(boat => boat == null);
            
            // 활성화된 공격 선박 수 계산
            _activeBoatCount = _attackBoats.Count(boat => boat != null && boat.activeSelf);
            
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] CheckAllBoatsDisabled: 활성화={_activeBoatCount}, 총={_attackBoats.Count}, 초기={_initialBoatCount}, 에피소드종료={_episodeEnded}");
            }
            
            if (_activeBoatCount == 0 && _initialBoatCount > 0 && !_episodeEnded)
            {
                if (debugLog)
                {
                    Debug.Log($"[AttackBoatManager] 모든 공격 선박이 파괴되었습니다! (초기: {_initialBoatCount}개, 현재: {_attackBoats.Count}개) 에피소드 종료 시작...");
                }
                
                // 코루틴 중지하지 않고 계속 체크 (에피소드 재시작 감지를 위해)
                // 딜레이 후 에피소드 종료
                StartCoroutine(EndEpisodesAfterDelay());
            }
        }

        /// <summary>
        /// 딜레이 후 에피소드 종료
        /// </summary>
        private IEnumerator EndEpisodesAfterDelay()
        {
            yield return new WaitForSeconds(endEpisodeDelay);
            EndAllEpisodes();
            _episodeEnded = true;
            
            // 에피소드 종료 직후 재생성 시도 (더 확실한 방법)
            if (debugLog)
            {
                Debug.Log("[AttackBoatManager] 에피소드 종료 완료. 재생성 준비...");
            }
            
            // 즉시 재생성 시도 (에피소드가 바로 재시작될 수 있으므로)
            StartCoroutine(ImmediateRespawnCheck());
        }
        
        /// <summary>
        /// 에피소드 종료 직후 재생성 체크 (여러 번 시도)
        /// </summary>
        private IEnumerator ImmediateRespawnCheck()
        {
            // 첫 번째 시도: 0.5초 후
            yield return new WaitForSeconds(0.5f);
            
            if (_episodeEnded && _activeBoatCount == 0 && _initialBoatCount > 0)
            {
                if (debugLog)
                {
                    Debug.Log("[AttackBoatManager] 즉시 재생성 시도 (1차)...");
                }
                
                ReactivateAllBoats();
                
                // 재생성 후 상태 확인
                yield return new WaitForSeconds(0.2f);
                
                if (_activeBoatCount > 0)
                {
                    _episodeEnded = false;
                    if (debugLog)
                    {
                        Debug.Log($"[AttackBoatManager] ✅ 재생성 성공! 활성화된 선박: {_activeBoatCount}개");
                    }
                    yield break;
                }
            }
            
            // 두 번째 시도: 1초 후
            yield return new WaitForSeconds(0.5f);
            
            if (_episodeEnded && _activeBoatCount == 0 && _initialBoatCount > 0)
            {
                if (debugLog)
                {
                    Debug.Log("[AttackBoatManager] 즉시 재생성 시도 (2차)...");
                }
                
                ReactivateAllBoats();
                
                // 재생성 후 상태 확인
                yield return new WaitForSeconds(0.2f);
                
                if (_activeBoatCount > 0)
                {
                    _episodeEnded = false;
                    if (debugLog)
                    {
                        Debug.Log($"[AttackBoatManager] ✅ 재생성 성공! 활성화된 선박: {_activeBoatCount}개");
                    }
                    yield break;
                }
            }
            
            // 세 번째 시도: 2초 후
            yield return new WaitForSeconds(1f);
            
            if (_episodeEnded && _activeBoatCount == 0 && _initialBoatCount > 0)
            {
                if (debugLog)
                {
                    Debug.Log("[AttackBoatManager] 즉시 재생성 시도 (3차 - 최종)...");
                }
                
                ReactivateAllBoats();
                _episodeEnded = false;
            }
        }

        /// <summary>
        /// 모든 공격 선박 다시 생성 (에피소드 재시작 시)
        /// </summary>
        public void ReactivateAllBoats()
        {
            // null이거나 파괴된 객체 제거
            _attackBoats.RemoveAll(boat => boat == null);
            
            int respawnedCount = 0;
            
            // 현재 활성화된 선박 수 확인
            int currentActiveCount = _attackBoats.Count(boat => boat != null && boat.activeSelf);
            
            // 파괴된 선박 개수 계산
            int destroyedCount = _initialBoatCount - currentActiveCount;
            
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] ========== ReactivateAllBoats 호출 ==========");
                Debug.Log($"  - 초기 선박 수: {_initialBoatCount}");
                Debug.Log($"  - 현재 활성화된 선박 수: {currentActiveCount}");
                Debug.Log($"  - 현재 리스트에 있는 선박 수: {_attackBoats.Count}");
                Debug.Log($"  - 파괴된 선박 수: {destroyedCount}");
            }
            
            if (destroyedCount <= 0)
            {
                if (debugLog)
                {
                    Debug.Log("[AttackBoatManager] 재생성할 선박이 없습니다. 모든 선박이 활성화되어 있습니다.");
                }
                return;
            }
            
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] {destroyedCount}개의 파괴된 선박 재생성 시작...");
            }
            
            // Prefab이 없으면 기존 객체를 다시 찾기만 함
            if (attackBoatPrefab == null)
            {
                Debug.LogError("[AttackBoatManager] ❌ attackBoatPrefab이 할당되지 않았습니다! Inspector에서 Prefab을 할당해주세요.");
                Debug.LogError("[AttackBoatManager] Prefab이 없으면 파괴된 객체를 재생성할 수 없습니다.");
                FindAllAttackBoats();
                return;
            }
            
            // 파괴된 선박 개수만큼 재생성 (각 보트의 원래 위치에서)
            int spawnIndex = 0;
            for (int i = 0; i < destroyedCount; i++)
            {
                // 각 보트의 원래 위치 사용 (순서대로)
                Vector3 spawnPos;
                if (spawnIndex < _spawnPositions.Count)
                {
                    // 저장된 초기 위치 사용
                    spawnPos = _spawnPositions[spawnIndex];
                }
                else
                {
                    // 초기 위치가 부족하면 기본 위치 사용
                    spawnPos = GetSpawnPosition(i);
                }
                spawnIndex++;
                
                if (debugLog)
                {
                    Debug.Log($"[AttackBoatManager] 선박 {i + 1}/{destroyedCount} 재생성 시도... 원래 위치: {spawnPos}");
                }
                
                GameObject newBoat = Instantiate(attackBoatPrefab, spawnPos, Quaternion.identity);
                
                if (newBoat != null)
                {
                    // 태그 설정
                    newBoat.tag = attackBoatTag;
                    
                    // Cinemachine Dolly Cart의 Path 복원
                    CinemachineDollyCart dollyCart = newBoat.GetComponent<CinemachineDollyCart>();
                    if (dollyCart != null && spawnIndex - 1 < _spawnPaths.Count)
                    {
                        CinemachinePathBase originalPath = _spawnPaths[spawnIndex - 1];
                        if (originalPath != null)
                        {
                            dollyCart.m_Path = originalPath;
                            dollyCart.m_Position = 0f; // Path 시작 위치로 리셋
                            
                            if (debugLog)
                            {
                                Debug.Log($"[AttackBoatManager] ✅ Dolly Cart Path 할당: {newBoat.name} -> {originalPath.name}");
                            }
                        }
                        else
                        {
                            if (debugLog)
                            {
                                Debug.LogWarning($"[AttackBoatManager] ⚠️ {newBoat.name}의 원래 Path가 null입니다.");
                            }
                        }
                    }
                    else if (dollyCart == null)
                    {
                        if (debugLog)
                        {
                            Debug.LogWarning($"[AttackBoatManager] ⚠️ {newBoat.name}에 CinemachineDollyCart 컴포넌트가 없습니다.");
                        }
                    }
                    
                    // 리스트에 추가
                    _attackBoats.Add(newBoat);
                    _initialPositions[newBoat] = spawnPos;
                    if (dollyCart != null && spawnIndex - 1 < _spawnPaths.Count)
                    {
                        _initialPaths[newBoat] = _spawnPaths[spawnIndex - 1];
                    }
                    
                    respawnedCount++;
                    
                    if (debugLog)
                    {
                        Debug.Log($"[AttackBoatManager] ✅ 선박 {i + 1}/{destroyedCount} 재생성 완료: {newBoat.name} at 원래 위치 {spawnPos}");
                    }
                }
                else
                {
                    Debug.LogError($"[AttackBoatManager] ❌ 선박 {i + 1} 재생성 실패! Instantiate가 null을 반환했습니다.");
                    Debug.LogError($"[AttackBoatManager] Prefab 확인: {attackBoatPrefab.name}, 위치: {spawnPos}");
                }
            }
            
            // 활성화된 공격 선박 수 업데이트
            _activeBoatCount = _attackBoats.Count(boat => boat != null && boat.activeSelf);
            _totalBoatCount = _attackBoats.Count;
            
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] ========== 재생성 완료 ==========");
                Debug.Log($"[AttackBoatManager] ✅ 공격 선박 {respawnedCount}개 재생성 완료. 총 활성화: {_activeBoatCount}/{_totalBoatCount}");
            }
            
            // 재생성 실패 시 경고
            if (respawnedCount == 0 && destroyedCount > 0)
            {
                Debug.LogError($"[AttackBoatManager] ❌ 재생성 실패! {destroyedCount}개 선박을 재생성하려고 했지만 0개만 생성되었습니다.");
                Debug.LogError($"[AttackBoatManager] Prefab이 올바르게 할당되었는지 확인하세요: {attackBoatPrefab != null}");
            }
        }
        
        /// <summary>
        /// 재생성 위치 가져오기
        /// </summary>
        private Vector3 GetSpawnPosition(int index)
        {
            if (useMultipleSpawnPositions && spawnPositions != null && spawnPositions.Length > 0)
            {
                return spawnPositions[index % spawnPositions.Length];
            }
            else
            {
                // 단일 위치에서 약간씩 오프셋을 주어 배치
                float offset = index * 2f; // 2미터 간격
                return spawnPosition + new Vector3(offset, 0f, 0f);
            }
        }

        /// <summary>
        /// 수동으로 공격 선박 다시 찾기 (에디터에서 테스트용)
        /// </summary>
        [ContextMenu("Find All Attack Boats")]
        public void RefreshAttackBoats()
        {
            FindAllAttackBoats();
            if (debugLog)
            {
                Debug.Log($"[AttackBoatManager] 공격 선박 새로고침 완료: 총 {_totalBoatCount}개, 활성화: {_activeBoatCount}개");
            }
        }

        /// <summary>
        /// 자동 재생성 코루틴이 실행 중인지 확인
        /// </summary>
        private bool IsAutoReactivateRunning()
        {
            return _autoReactivateCoroutine != null;
        }
        
        /// <summary>
        /// 에피소드 종료 후 일정 시간이 지나면 자동으로 모든 선박을 재생성
        /// </summary>
        private IEnumerator AutoReactivateAfterDelay()
        {
            _autoReactivateCoroutine = StartCoroutine(AutoReactivateCoroutine());
            yield return _autoReactivateCoroutine;
            _autoReactivateCoroutine = null;
        }
        
        private IEnumerator AutoReactivateCoroutine()
        {
            // 에피소드 종료 후 1초 대기 (에피소드 재시작 시간 확보)
            yield return new WaitForSeconds(1f);
            
            // 아직도 모든 선박이 파괴되어 있고, 에피소드가 종료된 상태라면
            if (_episodeEnded && _activeBoatCount == 0)
            {
                if (debugLog)
                {
                    Debug.Log("[AttackBoatManager] 자동 재생성: 모든 공격 선박 재생성...");
                }
                
                ReactivateAllBoats();
                _episodeEnded = false;
            }
        }

        /// <summary>
        /// 에디터에서 시각화
        /// </summary>
        private void OnDrawGizmos()
        {
            if (_attackBoats == null || _attackBoats.Count == 0)
            {
                return;
            }
            
            // 활성화된 공격 선박은 녹색, 비활성화된 것은 빨간색으로 표시
            foreach (var boat in _attackBoats)
            {
                if (boat == null)
                {
                    continue;
                }
                
                Gizmos.color = boat.activeSelf ? Color.green : Color.red;
                Gizmos.DrawWireSphere(boat.transform.position, 2f);
            }
        }
    }
}
