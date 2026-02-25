using UnityEngine;
using Unity.MLAgents;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 모함 진수구역 정의
    /// 모선 기준 방위각과 거리로 아군 쌍의 출격 위치를 결정
    /// </summary>
    [System.Serializable]
    public class LaunchZone
    {
        [Tooltip("모선 기준 방위각 (0=North/+Z, 90=East/+X, 180=South/-Z, 270=West/-X)")]
        public float angleDeg = 0f;

        [Tooltip("모선으로부터 진수구역 거리 (m)")]
        public float distance = 100f;

        [Tooltip("쌍의 좌우 펼침 각도 (±도)")]
        public float pairSpreadDeg = 1.7f;

        [Tooltip("에피소드마다 방위각 jitter (±도, 과적합 방지)")]
        public float angleJitter = 10f;
    }

    /// <summary>
    /// 아군 방어 쌍 (에이전트 2대 + Web)
    /// </summary>
    [System.Serializable]
    public class DefensePair
    {
        [Tooltip("에이전트 1")]
        public DefenseAgent agent1;

        [Tooltip("에이전트 2")]
        public DefenseAgent agent2;

        [Tooltip("Web 오브젝트")]
        public GameObject webObject;

        [HideInInspector] public int assignedZoneIndex = -1;
        [HideInInspector] public bool isActive = false;
    }

    /// <summary>
    /// 진수구역 + 아군 쌍 풀 관리자
    /// 모함 8방위 진수구역에서 적 방향에 따라 아군 쌍 출격
    /// 적군 풀과 동일한 패턴: 프리팹 복제 → SetActive로 활성/비활성 관리
    /// </summary>
    public class LaunchZoneManager : MonoBehaviour
    {
        [Header("Launch Zones (진수구역)")]
        [Tooltip("진수구역 개수 (360°를 균등 분할)")]
        [Range(1, 20)]
        public int zoneCount = 10;

        [Tooltip("쌍의 좌우 펼침 각도 (±도)")]
        public float pairSpreadDeg = 1.7f;

        [Tooltip("에피소드마다 방위각 jitter (±도, 과적합 방지)")]
        public float angleJitter = 10f;

        [HideInInspector]
        public LaunchZone[] launchZones;

        [Header("Ally Pool")]
        [Tooltip("최대 아군 쌍 수 (풀 크기)")]
        [Range(1, 10)]
        public int maxPairCount = 6;

        [Tooltip("현재 활성 쌍 수 (Stage/커리큘럼에서 제어)")]
        [Range(1, 10)]
        public int activePairCount = 1;

        [Header("Template")]
        [Tooltip("템플릿 쌍 (씬에 이미 배치된 기존 defenseAgent1/2/web). 비어있으면 프리팹에서 자동 생성")]
        public DefensePair templatePair;

        [Header("Prefab (templatePair 없을 때 사용)")]
        [Tooltip("방어 선박 프리팹 (DefenseAgent 포함)")]
        public GameObject defenseBoatPrefab;

        [Tooltip("Web 프리팹 (비어있으면 자동 생성)")]
        public GameObject webPrefab;

        [Header("References")]
        [Tooltip("모선 오브젝트")]
        public GameObject motherShip;

        [Tooltip("환경 컨트롤러")]
        public DefenseEnvController envController;

        [Header("Ellipse Shape (타원 배치)")]
        [Tooltip("타원 전후(Fore/Aft) 반경 - 선수/선미 방향 (0°/180°)")]
        public float ellipseForeAft = 150f;

        [Tooltip("타원 좌우(Beam) 반경 - 좌현/우현 방향 (90°/270°)")]
        public float ellipseBeam = 80f;

        [Header("Zone Visual (원통)")]
        [Tooltip("진수구역 원통 비주얼 표시")]
        public bool showZoneCylinders = true;

        [Tooltip("원통 높이")]
        public float cylinderHeight = 12f;

        [Tooltip("원통 반경")]
        public float cylinderRadius = 6f;

        [Tooltip("원통 색상")]
        public Color cylinderColor = new Color(0.2f, 0.5f, 1f, 0.25f);

        [Header("Debug")]
        [SerializeField] private int _deployedPairCount = 0;
        [SerializeField] private string _lastDeploymentInfo = "";

        // 풀 배열
        private DefensePair[] _pairPool;
        private bool _initialized = false;

        // 각 쌍의 원래 높이(y) 저장
        private float _templateAgent1Y;
        private float _templateAgent2Y;

        // 원통 비주얼
        private GameObject[] _zoneCylinders;

        /// <summary>
        /// 풀 초기화 (DefenseEnvController.Start()에서 호출)
        /// </summary>
        public void InitializeAllyPool()
        {
            if (_initialized) return;

            // 진수구역 자동 생성 (zoneCount 기반 균등 분할)
            GenerateLaunchZones();

            // templatePair가 없으면 프리팹에서 자동 생성
            if (templatePair == null || templatePair.agent1 == null || templatePair.agent2 == null)
            {
                if (defenseBoatPrefab != null)
                {
                    Debug.Log("[LaunchZoneManager] templatePair 없음 → 프리팹에서 자동 생성");
                    CreateTemplateFromPrefab();
                }
                else
                {
                    Debug.LogError("[LaunchZoneManager] InitializeAllyPool: templatePair과 defenseBoatPrefab 모두 없습니다!");
                    return;
                }
            }

            if (templatePair == null || templatePair.agent1 == null || templatePair.agent2 == null)
            {
                Debug.LogError("[LaunchZoneManager] InitializeAllyPool: 템플릿 생성 실패!");
                return;
            }

            // Y방향은 0으로 고정 (수면 높이)
            _templateAgent1Y = 0f;
            _templateAgent2Y = 0f;

            _pairPool = new DefensePair[maxPairCount];

            // pool[0] = 템플릿 쌍 자체 재활용
            _pairPool[0] = templatePair;
            _pairPool[0].isActive = false;

            // pool[1..N] = 복제
            Transform poolParent = templatePair.agent1.transform.parent != null
                ? templatePair.agent1.transform.parent
                : transform;

            for (int i = 1; i < maxPairCount; i++)
            {
                DefensePair pair = CreatePairClone(i, poolParent);
                pair.isActive = false;
                _pairPool[i] = pair;
            }

            // 모든 쌍 "비활성화" (에이전트는 멀리 이동 + isKinematic, Web만 SetActive(false))
            // ML-Agents 에이전트를 SetActive(false)하면 DecisionRequester가 복구 안 되므로
            // 에이전트 GameObject는 항상 active 유지
            for (int i = 0; i < maxPairCount; i++)
            {
                SetPairActive(i, false);
            }

            _initialized = true;

            // 진수구역 원통 비주얼 생성
            if (showZoneCylinders)
                CreateZoneCylinders();

            Debug.Log($"[LaunchZoneManager] InitializeAllyPool: maxPairs={maxPairCount}, template={templatePair.agent1.name}, zones={launchZones.Length}");
        }

        /// <summary>
        /// 프리팹에서 템플릿 쌍 생성 (templatePair가 비어있을 때)
        /// </summary>
        private void CreateTemplateFromPrefab()
        {
            Transform poolParent = transform;

            // Agent1
            GameObject agent1Obj = Instantiate(defenseBoatPrefab, poolParent);
            agent1Obj.name = "DefenseAgent1_template";
            agent1Obj.transform.position = HIDDEN_POS;

            // Agent2
            GameObject agent2Obj = Instantiate(defenseBoatPrefab, poolParent);
            agent2Obj.name = "DefenseAgent2_template";
            agent2Obj.transform.position = HIDDEN_POS + Vector3.right * 50f;

            var da1 = agent1Obj.GetComponent<DefenseAgent>();
            var da2 = agent2Obj.GetComponent<DefenseAgent>();

            if (da1 == null || da2 == null)
            {
                Debug.LogError("[LaunchZoneManager] defenseBoatPrefab에 DefenseAgent 컴포넌트가 없습니다!");
                if (da1 == null) Destroy(agent1Obj);
                if (da2 == null) Destroy(agent2Obj);
                return;
            }

            // Web 생성
            GameObject webObj = CreateWebObject(poolParent);

            // 교차 참조 설정
            da1.partnerAgent = da2;
            da2.partnerAgent = da1;
            da1.webObject = webObj;
            da2.webObject = webObj;

            if (motherShip != null)
            {
                da1.motherShip = motherShip;
                da2.motherShip = motherShip;
            }
            if (envController != null)
            {
                da1.envController = envController;
                da2.envController = envController;
            }

            // DynamicWeb 설정
            var dw = webObj.GetComponent<DynamicWeb>();
            if (dw != null)
            {
                dw.defenseShip1 = agent1Obj.transform;
                dw.defenseShip2 = agent2Obj.transform;
                if (envController != null)
                    dw.envController = envController;
            }

            var wd = webObj.GetComponent<WebCollisionDetector>();
            if (wd == null) wd = webObj.AddComponent<WebCollisionDetector>();
            if (envController != null) wd.envController = envController;

            // templatePair 설정
            if (templatePair == null)
                templatePair = new DefensePair();
            templatePair.agent1 = da1;
            templatePair.agent2 = da2;
            templatePair.webObject = webObj;

            // DefenseEnvController에도 참조 설정
            if (envController != null)
            {
                envController.defenseAgent1 = da1;
                envController.defenseAgent2 = da2;
                envController.webObject = webObj;
            }

            Debug.Log($"[LaunchZoneManager] 프리팹에서 템플릿 쌍 생성 완료: {defenseBoatPrefab.name}");
        }

        /// <summary>
        /// Web 오브젝트 생성 (프리팹 또는 기본 생성)
        /// </summary>
        private GameObject CreateWebObject(Transform parent)
        {
            if (webPrefab != null)
                return Instantiate(webPrefab, parent);

            // 기본 Web 오브젝트 생성
            var webObj = new GameObject("DynamicWeb_template");
            webObj.transform.SetParent(parent, false);

            var rb = webObj.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            var col = webObj.AddComponent<BoxCollider>();
            col.isTrigger = true;

            var dw = webObj.AddComponent<DynamicWeb>();
            dw.webHeight = 5f;
            dw.webThickness = 0.5f;
            dw.webColor = new Color(0f, 1f, 1f, 0.3f);
            dw.isTrigger = true;
            dw.showVisual = true;

            webObj.AddComponent<WebCollisionDetector>();
            webObj.SetActive(false);

            return webObj;
        }

        /// <summary>
        /// 템플릿으로부터 쌍 복제 (기존 로직 메서드화)
        /// </summary>
        private DefensePair CreatePairClone(int index, Transform poolParent)
        {
            DefensePair pair = new DefensePair();

            // Agent1 복제
            GameObject agent1Clone = Instantiate(templatePair.agent1.gameObject, poolParent);
            agent1Clone.name = $"DefenseAgent1_pair{index}";
            pair.agent1 = agent1Clone.GetComponent<DefenseAgent>();

            // Agent2 복제
            GameObject agent2Clone = Instantiate(templatePair.agent2.gameObject, poolParent);
            agent2Clone.name = $"DefenseAgent2_pair{index}";
            pair.agent2 = agent2Clone.GetComponent<DefenseAgent>();

            // Web 복제
            if (templatePair.webObject != null)
            {
                GameObject webClone = Instantiate(templatePair.webObject, poolParent);
                webClone.name = $"Web_pair{index}";
                pair.webObject = webClone;
            }

            // 파트너/Web 교차 참조 설정
            pair.agent1.partnerAgent = pair.agent2;
            pair.agent2.partnerAgent = pair.agent1;
            pair.agent1.webObject = pair.webObject;
            pair.agent2.webObject = pair.webObject;

            // 모선 참조
            if (motherShip != null)
            {
                pair.agent1.motherShip = motherShip;
                pair.agent2.motherShip = motherShip;
            }

            // envController 참조
            if (envController != null)
            {
                pair.agent1.envController = envController;
                pair.agent2.envController = envController;
            }

            // WebCollisionDetector/DynamicWeb 설정
            if (pair.webObject != null && envController != null)
            {
                var webDetector = pair.webObject.GetComponent<WebCollisionDetector>();
                if (webDetector == null)
                    webDetector = pair.webObject.AddComponent<WebCollisionDetector>();
                webDetector.envController = envController;

                var dynamicWeb = pair.webObject.GetComponent<DynamicWeb>();
                if (dynamicWeb != null)
                {
                    dynamicWeb.envController = envController;
                    dynamicWeb.defenseShip1 = pair.agent1.transform;
                    dynamicWeb.defenseShip2 = pair.agent2.transform;

                    // 복제된 Web의 webAnchor가 원본 선박을 가리키므로 복제 선박의 자식으로 재할당
                    RemapWebAnchor(dynamicWeb, templatePair, pair);
                }
            }

            return pair;
        }

        /// <summary>
        /// 포메이션에 따라 아군 쌍 배치
        /// </summary>
        /// <param name="formationType">적군 포메이션 유형</param>
        /// <param name="approachAngleDeg">적군 주 접근 방향 (도)</param>
        /// <param name="diversionaryAngles">양동 시 각 방향 접근 각도 (라디안), null이면 단일 방향</param>
        /// <param name="pairCount">배치할 쌍 수</param>
        /// <param name="motherPos">모선 위치</param>
        /// <param name="agentGroup">MA-POCA 그룹 (동적 등록용)</param>
        public void DeployPairs(FormationType formationType, float approachAngleDeg,
            float[] diversionaryAngles, int pairCount, Vector3 motherPos,
            SimpleMultiAgentGroup agentGroup)
        {
            if (!_initialized || _pairPool == null)
            {
                Debug.LogWarning("[LaunchZoneManager] DeployPairs: 풀이 초기화되지 않았습니다.");
                return;
            }

            pairCount = Mathf.Min(pairCount, maxPairCount);
            _deployedPairCount = pairCount;

            // 1. 모든 쌍 비활성화 + MA-POCA 해제
            // SetPairActive는 에이전트 GameObject를 SetActive(false)하지 않고 멀리 이동시킴
            for (int i = 0; i < _pairPool.Length; i++)
            {
                if (_pairPool[i].isActive && agentGroup != null)
                {
                    if (_pairPool[i].agent1 != null)
                        agentGroup.UnregisterAgent(_pairPool[i].agent1);
                    if (_pairPool[i].agent2 != null)
                        agentGroup.UnregisterAgent(_pairPool[i].agent2);
                }
                SetPairActive(i, false);
            }

            // 2. 진수구역별 쌍 배정
            Dictionary<int, List<int>> zoneAssignments = AssignPairsToZones(
                formationType, approachAngleDeg, diversionaryAngles, pairCount);

            // 3. 각 진수구역에 배정된 쌍 배치
            int pairIdx = 0;
            foreach (var kvp in zoneAssignments)
            {
                int zoneIdx = kvp.Key;
                List<int> pairIndices = kvp.Value;
                LaunchZone zone = launchZones[zoneIdx];

                // 진수구역 방위각 + jitter
                float zoneAngleDeg = zone.angleDeg + Random.Range(-zone.angleJitter, zone.angleJitter);
                float zoneAngleRad = zoneAngleDeg * Mathf.Deg2Rad;
                Vector3 zoneDir = new Vector3(Mathf.Sin(zoneAngleRad), 0f, Mathf.Cos(zoneAngleRad));

                // 타원 거리 계산 (jitter 적용된 각도 기준)
                float zoneDist = GetEllipseDistance(zoneAngleDeg);

                // 적이 오는 방향 (배치된 아군이 바라볼 방향)
                float faceAngleRad = approachAngleDeg * Mathf.Deg2Rad;
                Vector3 faceDir = new Vector3(Mathf.Sin(faceAngleRad), 0f, Mathf.Cos(faceAngleRad));

                // 적 접근 방향에 수직인 횡대열 축 계산
                Vector3 lateralDir = new Vector3(zoneDir.z, 0f, -zoneDir.x); // 90° 회전

                // 쌍 간 횡 간격 (쌍 내 2대 좌우폭 + 여유)
                float pairWidth = 2f * zoneDist * Mathf.Tan(zone.pairSpreadDeg * Mathf.Deg2Rad);
                float lateralSpacing = pairWidth + 10f; // 쌍 간 최소 10m 여유

                // 중심 기준 횡 오프셋 계산 (0이 중앙)
                float totalWidth = (pairIndices.Count - 1) * lateralSpacing;
                float startOffset = -totalWidth * 0.5f;

                for (int j = 0; j < pairIndices.Count; j++)
                {
                    int pi = pairIndices[j];
                    if (pi >= _pairPool.Length) continue;

                    DefensePair pair = _pairPool[pi];
                    pair.assignedZoneIndex = zoneIdx;

                    // 모선에서 타원 거리, 횡대열로 나란히 배치
                    float lateralOffset = startOffset + j * lateralSpacing;
                    Vector3 pairCenter = motherPos + zoneDir * zoneDist + lateralDir * lateralOffset;

                    // 2대 좌우 배치 (쌍 내)
                    float spreadRad = zone.pairSpreadDeg * Mathf.Deg2Rad;
                    Vector3 dir1 = RotateXZ(zoneDir, -spreadRad);
                    Vector3 dir2 = RotateXZ(zoneDir, spreadRad);

                    Vector3 pos1 = pairCenter + lateralDir * (pairWidth * 0.5f);
                    pos1.y = _templateAgent1Y;
                    Vector3 pos2 = pairCenter + lateralDir * (-pairWidth * 0.5f);
                    pos2.y = _templateAgent2Y;

                    // 적 방향 바라봄
                    Quaternion rot = faceDir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(faceDir, Vector3.up)
                        : Quaternion.identity;

                    // 에이전트 위치/회전 설정
                    ResetAgent(pair.agent1, pos1, rot);
                    ResetAgent(pair.agent2, pos2, rot);

                    // 적군 배열 전달
                    if (envController != null)
                    {
                        pair.agent1.enemyShips = envController.enemyShips;
                        pair.agent2.enemyShips = envController.enemyShips;
                    }

                    // 활성화 (에이전트 위치는 위의 ResetAgent에서 이미 설정됨)
                    SetPairActive(pi, true);

                    // MA-POCA 등록
                    if (agentGroup != null)
                    {
                        agentGroup.RegisterAgent(pair.agent1);
                        agentGroup.RegisterAgent(pair.agent2);
                    }

                    pairIdx++;
                }
            }

            _lastDeploymentInfo = $"{formationType}, pairs={pairCount}, zones={zoneAssignments.Count}";
            Debug.Log($"[LaunchZoneManager] DeployPairs: {_lastDeploymentInfo}");
        }

        /// <summary>
        /// 포메이션에 따라 쌍을 진수구역에 배정
        /// </summary>
        private Dictionary<int, List<int>> AssignPairsToZones(
            FormationType formationType, float approachAngleDeg,
            float[] diversionaryAngles, int pairCount)
        {
            var assignments = new Dictionary<int, List<int>>();

            if (formationType == FormationType.Diversionary && diversionaryAngles != null && diversionaryAngles.Length > 1)
            {
                // 양동: 각 방향의 가장 가까운 진수구역에 균등 분배
                int pairsPerDir = pairCount / diversionaryAngles.Length;
                int pairRemainder = pairCount % diversionaryAngles.Length;
                int pairIdx = 0;

                for (int d = 0; d < diversionaryAngles.Length; d++)
                {
                    float dirAngleDeg = diversionaryAngles[d] * Mathf.Rad2Deg;
                    int bestZone = FindClosestZone(dirAngleDeg);

                    if (!assignments.ContainsKey(bestZone))
                        assignments[bestZone] = new List<int>();

                    int count = pairsPerDir + (d < pairRemainder ? 1 : 0);
                    for (int i = 0; i < count && pairIdx < pairCount; i++)
                    {
                        assignments[bestZone].Add(pairIdx);
                        pairIdx++;
                    }
                }
            }
            else
            {
                // Concentrated / Wave: 적 접근 방향의 가장 가까운 진수구역에 모든 쌍 집중
                int bestZone = FindClosestZone(approachAngleDeg);

                assignments[bestZone] = new List<int>();
                for (int i = 0; i < pairCount; i++)
                {
                    assignments[bestZone].Add(i);
                }
            }

            return assignments;
        }

        /// <summary>
        /// 주어진 각도에 가장 가까운 진수구역 인덱스 반환
        /// </summary>
        private int FindClosestZone(float angleDeg)
        {
            int bestIdx = 0;
            float bestDiff = float.MaxValue;

            for (int i = 0; i < launchZones.Length; i++)
            {
                float diff = Mathf.Abs(Mathf.DeltaAngle(angleDeg, launchZones[i].angleDeg));
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }

            return bestIdx;
        }

        /// <summary>
        /// 에이전트 위치/회전/물리 리셋
        /// </summary>
        private void ResetAgent(DefenseAgent agent, Vector3 position, Quaternion rotation)
        {
            if (agent == null) return;

            if (agent.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.isKinematic = false; // SetPairActive에서 kinematic 설정되었을 수 있으므로 해제
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            agent.transform.position = position;
            agent.transform.rotation = rotation;

            if (agent._engine != null)
            {
                agent._engine.OnEpisodeReset();
            }
        }

        // 비활성 에이전트를 숨길 위치 (수면 아래 멀리)
        private static readonly Vector3 HIDDEN_POS = new Vector3(0f, -500f, 0f);

        /// <summary>
        /// 쌍 활성/비활성 설정
        /// ML-Agents 에이전트는 SetActive(false)하면 DecisionRequester가 복구 안 되므로,
        /// 에이전트 GameObject는 항상 active 유지하고 비활성 시 멀리 이동시킴
        /// Web만 SetActive로 토글
        /// </summary>
        private void SetPairActive(int index, bool active)
        {
            if (index >= _pairPool.Length) return;
            DefensePair pair = _pairPool[index];

            if (!active)
            {
                // "비활성화": 에이전트를 수면 아래로 이동 (GameObject는 활성 유지)
                if (pair.agent1 != null)
                {
                    pair.agent1.transform.position = HIDDEN_POS + Vector3.right * (index * 100f);
                    if (pair.agent1.TryGetComponent<Rigidbody>(out var rb1))
                    {
                        rb1.velocity = Vector3.zero;
                        rb1.angularVelocity = Vector3.zero;
                        rb1.isKinematic = true; // 물리 시뮬레이션 중단
                    }
                }
                if (pair.agent2 != null)
                {
                    pair.agent2.transform.position = HIDDEN_POS + Vector3.right * (index * 100f + 50f);
                    if (pair.agent2.TryGetComponent<Rigidbody>(out var rb2))
                    {
                        rb2.velocity = Vector3.zero;
                        rb2.angularVelocity = Vector3.zero;
                        rb2.isKinematic = true;
                    }
                }
            }
            else
            {
                // "활성화": kinematic 해제 (위치는 ResetAgent에서 설정됨)
                if (pair.agent1 != null && pair.agent1.TryGetComponent<Rigidbody>(out var rb1))
                    rb1.isKinematic = false;
                if (pair.agent2 != null && pair.agent2.TryGetComponent<Rigidbody>(out var rb2))
                    rb2.isKinematic = false;
            }

            // Web만 SetActive 토글
            if (pair.webObject != null) pair.webObject.SetActive(active);
            pair.isActive = active;
        }

        /// <summary>
        /// 모든 활성 쌍의 에이전트 목록 반환
        /// </summary>
        public List<DefenseAgent> GetActiveAgents()
        {
            var agents = new List<DefenseAgent>();
            if (_pairPool == null) return agents;

            for (int i = 0; i < _pairPool.Length; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive)
                {
                    if (_pairPool[i].agent1 != null) agents.Add(_pairPool[i].agent1);
                    if (_pairPool[i].agent2 != null) agents.Add(_pairPool[i].agent2);
                }
            }
            return agents;
        }

        /// <summary>
        /// 활성 쌍 수 반환
        /// </summary>
        public int GetActivePairCount()
        {
            if (_pairPool == null) return 0;
            int count = 0;
            for (int i = 0; i < _pairPool.Length; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 풀 초기화 여부
        /// </summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// 복제된 Web의 webAnchor1/2를 복제된 선박의 자식으로 재할당
        /// 원본 web 복제 시 anchor가 원본 선박의 자식을 가리키므로, 같은 이름의 자식을 복제 선박에서 찾아 재할당
        /// </summary>
        private void RemapWebAnchor(DynamicWeb dynamicWeb, DefensePair templatePair, DefensePair clonePair)
        {
            if (dynamicWeb.webAnchor1 != null && templatePair.agent1 != null)
            {
                Transform found = FindChildRecursive(clonePair.agent1.transform, dynamicWeb.webAnchor1.name);
                dynamicWeb.webAnchor1 = found; // null이면 defenseShip1.position fallback
            }

            if (dynamicWeb.webAnchor2 != null && templatePair.agent2 != null)
            {
                Transform found = FindChildRecursive(clonePair.agent2.transform, dynamicWeb.webAnchor2.name);
                dynamicWeb.webAnchor2 = found; // null이면 defenseShip2.position fallback
            }
        }

        /// <summary>
        /// 이름으로 자식 Transform을 재귀 검색
        /// </summary>
        private Transform FindChildRecursive(Transform parent, string childName)
        {
            foreach (Transform child in parent)
            {
                if (child.name == childName) return child;
                Transform found = FindChildRecursive(child, childName);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// 모든 활성 쌍의 에이전트에게 적군 배열 업데이트
        /// DefenseEnvController.UpdateEnemyShipsArray()에서 호출
        /// </summary>
        public void UpdateAllAgentEnemyShips(GameObject[] enemies)
        {
            if (_pairPool == null) return;

            for (int i = 0; i < _pairPool.Length; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive)
                {
                    if (_pairPool[i].agent1 != null)
                        _pairPool[i].agent1.enemyShips = enemies;
                    if (_pairPool[i].agent2 != null)
                        _pairPool[i].agent2.enemyShips = enemies;
                }
            }
        }

        /// <summary>
        /// zoneCount 기반으로 진수구역 배열 자동 생성 (360° 균등 분할)
        /// </summary>
        private void GenerateLaunchZones()
        {
            launchZones = new LaunchZone[zoneCount];
            float angleStep = 360f / zoneCount;
            for (int i = 0; i < zoneCount; i++)
            {
                launchZones[i] = new LaunchZone
                {
                    angleDeg = i * angleStep,
                    distance = GetEllipseDistance(i * angleStep),
                    pairSpreadDeg = this.pairSpreadDeg,
                    angleJitter = this.angleJitter,
                };
            }
            Debug.Log($"[LaunchZoneManager] {zoneCount}개 진수구역 생성 (간격 {angleStep:F1}°)");
        }

        /// <summary>
        /// 타원 극좌표 공식으로 해당 방위각의 거리 계산
        /// r(θ) = (a*b) / sqrt((b*cosθ)² + (a*sinθ)²)
        /// a = 전후(fore/aft) 반경, b = 좌우(beam) 반경
        /// θ=0°/180° → a (전후), θ=90°/270° → b (좌우)
        /// </summary>
        public float GetEllipseDistance(float angleDeg)
        {
            float angleRad = angleDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(angleRad);
            float sinA = Mathf.Sin(angleRad);
            float a = ellipseForeAft;
            float b = ellipseBeam;
            return (a * b) / Mathf.Sqrt(b * b * cosA * cosA + a * a * sinA * sinA);
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

        #region Zone Cylinder Visuals

        /// <summary>
        /// 각 진수구역 위치에 원통 비주얼 생성
        /// </summary>
        private void CreateZoneCylinders()
        {
            if (motherShip == null || launchZones == null) return;

            _zoneCylinders = new GameObject[launchZones.Length];
            Vector3 motherPos = motherShip.transform.position;

            for (int i = 0; i < launchZones.Length; i++)
            {
                LaunchZone zone = launchZones[i];
                float angleRad = zone.angleDeg * Mathf.Deg2Rad;
                Vector3 zoneDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                float dist = GetEllipseDistance(zone.angleDeg);
                Vector3 zonePos = motherPos + zoneDir * dist;

                // 원통 프리미티브 생성
                GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                cylinder.name = $"LaunchZone_{i}_{zone.angleDeg:F0}deg";
                cylinder.transform.SetParent(transform, true);
                cylinder.transform.position = new Vector3(zonePos.x, motherPos.y + cylinderHeight * 0.5f, zonePos.z);
                cylinder.transform.localScale = new Vector3(
                    cylinderRadius * 2f, cylinderHeight * 0.5f, cylinderRadius * 2f);

                // 콜라이더 제거 (비주얼 전용)
                var col = cylinder.GetComponent<Collider>();
                if (col != null) Object.Destroy(col);

                // 반투명 머티리얼 설정
                var renderer = cylinder.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    var mat = new Material(Shader.Find("Standard"));
                    mat.SetFloat("_Mode", 3); // Transparent
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = 3000;
                    mat.color = cylinderColor;
                    renderer.material = mat;
                }

                _zoneCylinders[i] = cylinder;
            }

            Debug.Log($"[LaunchZoneManager] {launchZones.Length}개 진수구역 원통 생성 완료");
        }

        /// <summary>
        /// 원통 위치를 모선 기준으로 갱신 (모선이 이동하는 경우 대비)
        /// </summary>
        public void UpdateZoneCylinderPositions()
        {
            if (_zoneCylinders == null || motherShip == null) return;

            Vector3 motherPos = motherShip.transform.position;
            for (int i = 0; i < launchZones.Length && i < _zoneCylinders.Length; i++)
            {
                if (_zoneCylinders[i] == null) continue;
                LaunchZone zone = launchZones[i];
                float angleRad = zone.angleDeg * Mathf.Deg2Rad;
                Vector3 zoneDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                float dist = GetEllipseDistance(zone.angleDeg);
                Vector3 zonePos = motherPos + zoneDir * dist;
                _zoneCylinders[i].transform.position = new Vector3(
                    zonePos.x, motherPos.y + cylinderHeight * 0.5f, zonePos.z);
            }
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmos()
        {
            // 모선 위치 (런타임이면 motherShip, 에디터에서는 transform 위치)
            Vector3 center = (motherShip != null) ? motherShip.transform.position : transform.position;

            // 타원 윤곽선 그리기
            Gizmos.color = new Color(cylinderColor.r, cylinderColor.g, cylinderColor.b, 0.3f);
            int ellipseSegments = 64;
            Vector3 prevPoint = Vector3.zero;
            for (int s = 0; s <= ellipseSegments; s++)
            {
                float angle = (float)s / ellipseSegments * 360f;
                float dist = GetEllipseDistance(angle);
                float rad = angle * Mathf.Deg2Rad;
                Vector3 point = center + new Vector3(Mathf.Sin(rad) * dist, 0f, Mathf.Cos(rad) * dist);
                if (s > 0) Gizmos.DrawLine(prevPoint, point);
                prevPoint = point;
            }

            // 진수구역 표시 (런타임 배열 또는 zoneCount 기반)
            int count = (launchZones != null && launchZones.Length > 0) ? launchZones.Length : zoneCount;
            float angleStep = 360f / count;

            for (int i = 0; i < count; i++)
            {
                float angleDeg = (launchZones != null && i < launchZones.Length)
                    ? launchZones[i].angleDeg
                    : i * angleStep;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector3 zoneDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                float dist = GetEllipseDistance(angleDeg);
                Vector3 zonePos = center + zoneDir * dist;

                // 진수구역 원통 표시
                Color c = cylinderColor;
                c.a = 0.4f;
                Gizmos.color = c;
                DrawWireCylinder(zonePos + Vector3.up * cylinderHeight * 0.5f, cylinderRadius, cylinderHeight);

                // 모선↔구역 연결선
                Gizmos.color = new Color(c.r, c.g, c.b, 0.2f);
                Gizmos.DrawLine(center, zonePos);

#if UNITY_EDITOR
                // 라벨 (거리 정보 포함)
                var style = new GUIStyle();
                style.normal.textColor = new Color(0.3f, 0.7f, 1f, 1f);
                style.fontSize = 12;
                style.fontStyle = FontStyle.Bold;
                UnityEditor.Handles.Label(
                    zonePos + Vector3.up * (cylinderHeight + 2f),
                    $"Zone {i} ({angleDeg:F0}° / {dist:F0}m)", style);
#endif
            }
        }

        private static void DrawWireCylinder(Vector3 center, float radius, float height)
        {
            float halfH = height * 0.5f;
            Vector3 top = center + Vector3.up * halfH;
            Vector3 bot = center - Vector3.up * halfH;

            int seg = 16;
            Vector3 prevTop = Vector3.zero, prevBot = Vector3.zero;
            for (int i = 0; i <= seg; i++)
            {
                float a = (float)i / seg * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * radius;
                float z = Mathf.Sin(a) * radius;
                Vector3 ct = top + new Vector3(x, 0, z);
                Vector3 cb = bot + new Vector3(x, 0, z);

                if (i > 0)
                {
                    Gizmos.DrawLine(prevTop, ct);
                    Gizmos.DrawLine(prevBot, cb);
                }
                if (i % 4 == 0) Gizmos.DrawLine(ct, cb);
                prevTop = ct;
                prevBot = cb;
            }
        }

        #endregion
    }
}
