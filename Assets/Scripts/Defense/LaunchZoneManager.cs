using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using Unity.Barracuda;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 인스펙터에서 직접 지정하는 진수구역 설정
    /// </summary>
    [System.Serializable]
    public class SpawnZoneConfig
    {
        [Tooltip("스폰 기준점 오브젝트 (위치만 사용)")]
        public Transform position;
    }

    /// <summary>
    /// 내부 진수구역 데이터 (GenerateLaunchZones에서 생성)
    /// </summary>
    [System.Serializable]
    public class LaunchZone
    {
        [Tooltip("모선 기준 방위각 (클러스터 매칭용)")]
        public float angleDeg = 0f;

        [Tooltip("모선으로부터 진수구역 거리 (m)")]
        public float distance = 200f;

        [Tooltip("에피소드마다 방위각 jitter (±도, 과적합 방지)")]
        public float angleJitter = 10f;

        [Tooltip("인스펙터 지정 절대 스폰 위치 (SpawnZoneConfig 사용 시)")]
        public Vector3 worldPos;

        [Tooltip("true면 worldPos 사용, false면 모선 기준 계산")]
        public bool hasWorldPos = false;
    }

    /// <summary>
    /// 아군 방어 쌍 (에이전트 2대 + Web)
    /// </summary>
    [System.Serializable]
    public class DefensePair
    {
        [Tooltip("에이전트 1 (단일 이동 선박)")]
        public DefenseAgent agent1;

        // One-Way Towing: agent2 없음 — null 고정. 외부 참조 호환성 유지용.
        [System.NonSerialized] public DefenseAgent agent2 = null;

        [Tooltip("앵커 오브젝트 (Empty GameObject — DynamicWeb 두 번째 앵커, 선박 없음)")]
        public GameObject anchorObject;

        [Tooltip("Web 오브젝트")]
        public GameObject webObject;

        /// <summary>앵커 위치 헬퍼. anchorObject 없으면 agent1 위치 반환</summary>
        public Vector3 AnchorPos => anchorObject != null
            ? anchorObject.transform.position
            : (agent1 != null ? agent1.transform.position : Vector3.zero);

        [HideInInspector] public int assignedZoneIndex = -1;
        [HideInInspector] public bool isActive = false;
        [HideInInspector] public bool isDisarmed = false;  // 직진 이탈 중 (Stage9)
        [HideInInspector] public bool isStandby = false;   // 예비 대기 중 (배치됨, Neutralized)
        [HideInInspector] public int disarmStep = -1;      // 직진 시작 스텝
        [HideInInspector] public int deployStep = -1; // 배치 시점 (FixedUpdate 스텝)
        [HideInInspector] public bool isSplitting = false; // 분리(그물 전개) 중
        [HideInInspector] public int splitStartStep = -1;  // 분리 시작 스텝

        [HideInInspector] public Vector3 deployLateralDir = Vector3.right;
        [HideInInspector] public Vector3 initialWebDir = Vector3.right;


        // 양동 방향 필터용: 진수 시 스폰 각도 (모선 기준, -1=미설정)
        [HideInInspector] public float launchAngleDeg = -1f;

        // 클러스터 배정 (Angular Binning)
        [HideInInspector] public int   assignedClusterIdx = -1;   // CurrentClusters 내 인덱스
        [HideInInspector] public Vector3 clusterCentroid;          // 클러스터 중심 세계 위치
        [HideInInspector] public List<int> clusterEnemyIndices;    // 클러스터 내 적군 인덱스 목록
    }

    /// <summary>
    /// 방향별 적군 클러스터 (Angular Binning 결과)
    /// </summary>
    public struct EnemyCluster
    {
        public float   centerAngleDeg;   // 클러스터 대표 방향 (모선 기준, 도)
        public Vector3 centroidWorld;    // 클러스터 내 활성 적군 평균 위치
        public int     representativeIdx;// centroid에 가장 가까운 적군 인덱스 (-1=없음)
        public List<int> enemyIndices;   // enemies[] 배열 인덱스 목록 (비활성 포함)
    }

    /// <summary>
    /// 진수구역 + 아군 쌍 풀 관리자
    /// 모함 8방위 진수구역에서 적 방향에 따라 아군 쌍 출격
    /// 적군 풀과 동일한 패턴: 프리팹 복제 → SetActive로 활성/비활성 관리
    /// </summary>
    public class LaunchZoneManager : MonoBehaviour
    {
        [Header("Launch Zones (인스펙터 직접 지정)")]
        [Tooltip("진수구역 설정 배열. 각 구역마다 위치 오브젝트와 선박 헤딩 각도를 지정. 비어있으면 후미 자동 생성 fallback.")]
        public SpawnZoneConfig[] spawnZones;

        [Tooltip("(레거시) 진수구역 위치 Transform 배열. spawnZones가 비어있을 때만 사용됨.")]
        public Transform[] zoneTransforms;

        [Tooltip("모선 후미 진수거리 (m) — zoneTransforms 미설정 시 fallback으로 사용")]
        public float rearDistance = 10f;

        [Tooltip("후미 좌/우 분산 각도 (°) — zoneTransforms 미설정 시 fallback으로 사용")]
        [Range(0f, 45f)]
        public float rearSpread = 20f;

        [Tooltip("에피소드마다 진수 방위 jitter (±도, 과적합 방지)")]
        public float angleJitter = 10f;

        [Tooltip("같은 구역에서 다수 쌍 생성 시 전후 간격 (m) — 겹침 방지")]
        public float pairDepthStagger = 20f;

        /// <summary>
        /// DefenseEnvController가 DeployPairs 직전에 설정하는 횡 오프셋.
        /// towDir 반대 방향으로 스폰 위치를 밀어 스윕 공간을 확보한다.
        /// </summary>
        [HideInInspector] public float additionalLateralOffset = 0f;
        [HideInInspector] public Vector3 additionalLateralDir   = Vector3.right;

        [HideInInspector]
        public LaunchZone[] launchZones;

        [Header("Ally Pool")]
        [Tooltip("최대 아군 쌍 수 (풀 상한)")]
        [Range(1, 10)]
        public int maxPairCount = 3;

        [Tooltip("에피소드 시작 시 자동 배치 쌍 수")]
        [Range(0, 10)]
        public int activePairCount = 3;

        [Tooltip("초기 출동 쌍 수 (나머지는 예비로 대기, 0=activePairCount 전부 출동)")]
        [Range(0, 10)]
        public int initialDeployCount = 0;

        [Tooltip("에피소드 당 최대 배치 쌍 수 (0=무제한). 초기+추가 배치 합산")]
        [Range(0, 9)]
        public int maxTotalPairsPerEpisode = 0;

        [Tooltip("같은 진수구역에서 연속 출동 최소 간격 (스텝)")]
        public int zoneDeployCooldown = 100;

        [Header("Enemy Clustering")]
        [Tooltip("적군 방향 클러스터링 빈 폭 (°). 이 범위 내 적군을 동일 클러스터로 묶음. 60° 권장.")]
        [Range(10f, 120f)]
        public float clusterBinWidthDeg = 60f;

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

        [Header("Zone Visual (원통)")]
        [Tooltip("진수구역 원통 비주얼 표시")]
        public bool showZoneCylinders = true;

        [Tooltip("원통 높이")]
        public float cylinderHeight = 12f;

        [Tooltip("원통 반경")]
        public float cylinderRadius = 6f;

        [Tooltip("원통 색상")]
        public Color cylinderColor = new Color(0.2f, 0.5f, 1f, 0.25f);

        [Header("Convoy Settings (쌍동선)")]
        [Tooltip("쌍동선 두 선박 간 간격 (m). 클수록 멀리 배치")]
        [Range(3f, 30f)]
        public float convoySpacing = 5f;

        [Header("Web Settings (DynamicWeb 생성 시 적용)")]
        [Tooltip("Web 높이")]
        public float webHeight = 15f;

        [Tooltip("Web 두께")]
        public float webThickness = 0.5f;

        [Tooltip("Web 색상")]
        public Color webColor = new Color(1f, 0.2f, 0.6f, 0.7f); // 핑크

        [Tooltip("Web 시각화 활성화")]
        public bool webShowVisual = true;

        [Header("Debug")]
        [SerializeField] private int _deployedPairCount = 0;
        [SerializeField] private string _lastDeploymentInfo = "";

        /// <summary>에피소드 동안 배치된 총 페어 수 (초기 배치 + 추가 배치 누적)</summary>
        [SerializeField] private int _totalPairsDeployed = 0;

        // 풀 리스트 (지연 생성: 필요할 때만 추가)
        private List<DefensePair> _pairPool;
        private bool _initialized = false;

        /// <summary>현재 에피소드의 적군 클러스터 목록 (DeployPairs 호출 시 갱신)</summary>
        public List<EnemyCluster> CurrentClusters { get; private set; } = new List<EnemyCluster>();

        // 진수구역별 마지막 출동 스텝 (쿨다운용)
        private int[] _zoneLastDeployStep;

        // 각 쌍의 원래 높이(y) 저장
        private float _templateAgent1Y;

        // 원통 비주얼
        private GameObject[] _zoneCylinders;

        // ── Sequential deploy state ──────────────────────────────────────────
        private struct SeqSpawnInfo
        {
            public Vector3 pairCenter, zoneDir;
            public int     zoneIdx;
            public float   spawnAngleDeg;
        }
        private List<SeqSpawnInfo>    _seqSpawnInfos = new List<SeqSpawnInfo>();
        private List<EnemyCluster>    _seqClusters;
        private HashSet<int>          _seqAssignedClusterIdx = new HashSet<int>();
        private HashSet<int>          _seqAssignedEnemyIdx   = new HashSet<int>();
        private int                   _seqDeployNext;
        private int                   _seqActiveCount = 0;  // 클러스터 수 (활성 페어 수), 나머지는 예비
        private SimpleMultiAgentGroup _seqAgentGroup;
        private GameObject[]          _seqEnemies;

        /// <summary>PrepareSequentialDeploy 후 배치 가능한 총 쌍 수</summary>
        public int SequentialPairCount => _seqSpawnInfos?.Count ?? 0;

        /// <summary>
        /// 풀 초기화 (DefenseEnvController.Start()에서 호출)
        /// </summary>
        public void InitializeAllyPool()
        {
            if (_initialized) return;

            // 진수구역 자동 생성 (zoneCount 기반 균등 분할)
            GenerateLaunchZones();

            // templatePair가 없으면 프리팹에서 자동 생성
            if (templatePair == null || templatePair.agent1 == null || templatePair.anchorObject == null)
            {
                if (defenseBoatPrefab != null)
                {
                    // Debug.Log("[LaunchZoneManager] templatePair 없음 → 프리팹에서 자동 생성");
                    CreateTemplateFromPrefab();
                }
                else
                {
                    // Debug.LogError("[LaunchZoneManager] InitializeAllyPool: templatePair과 defenseBoatPrefab 모두 없습니다!");
                    return;
                }
            }

            if (templatePair == null || templatePair.agent1 == null || templatePair.anchorObject == null)
            {
                // Debug.LogError("[LaunchZoneManager] InitializeAllyPool: 템플릿 생성 실패!");
                return;
            }

            _templateAgent1Y = 1f;

            // 풀 빈 상태로 시작 (버튼 클릭 시 프리팹에서 직접 생성)
            _pairPool = new List<DefensePair>();

            // 템플릿 오브젝트 비활성화 (더 이상 사용 안 함, 충돌 방지)
            if (templatePair.agent1 != null) templatePair.agent1.gameObject.SetActive(false);
            if (templatePair.anchorObject != null) templatePair.anchorObject.SetActive(false);
            if (templatePair.webObject != null) templatePair.webObject.SetActive(false);

            _initialized = true;

            // 진수구역 원통 비주얼 생성
            if (showZoneCylinders)
                CreateZoneCylinders();
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

            var da1 = agent1Obj.GetComponent<DefenseAgent>();
            if (da1 == null) { Destroy(agent1Obj); return; }

            // ONE-WAY TOWING: agent2 선박 없음 — empty anchor GameObject만 생성
            GameObject anchorObj = new GameObject("Anchor_template");
            anchorObj.transform.SetParent(poolParent);
            anchorObj.transform.position = HIDDEN_POS + Vector3.right * 50f;

            // Web 생성
            GameObject webObj = CreateWebObject(poolParent);

            da1.webObject = webObj;
            if (motherShip != null) da1.motherShip = motherShip;
            if (envController != null) da1.envController = envController;

            // DynamicWeb: ship1=agent1, ship2=anchor (empty Transform)
            var dw = webObj.GetComponent<DynamicWeb>();
            if (dw == null) dw = webObj.AddComponent<DynamicWeb>();
            dw.defenseShip1 = agent1Obj.transform;
            dw.defenseShip2 = anchorObj.transform;
            if (envController != null) dw.envController = envController;

            var wd = webObj.GetComponent<WebCollisionDetector>();
            if (wd == null) wd = webObj.AddComponent<WebCollisionDetector>();
            if (envController != null) wd.envController = envController;

            NormalizeBufferSensor(da1);

            if (templatePair == null) templatePair = new DefensePair();
            templatePair.agent1 = da1;
            templatePair.anchorObject = anchorObj;
            templatePair.webObject = webObj;
            da1.useArrowKeys = true;

            if (envController != null)
            {
                envController.defenseAgent1 = da1;
                envController.webObject = webObj;
            }

            // Debug.Log($"[LaunchZoneManager] 프리팹에서 템플릿 쌍 생성 완료: {defenseBoatPrefab.name}");
        }

        /// <summary>
        /// Web 오브젝트 생성 (프리팹 또는 기본 생성)
        /// </summary>
        private GameObject CreateWebObject(Transform parent)
        {
            if (webPrefab != null)
            {
                var obj = Instantiate(webPrefab, parent);
                obj.SetActive(false); // Start() 전에 ship 참조 설정 보장
                // Instantiate가 원본의 _initialized, _netContainer 등을 복사하므로 리셋
                var clonedDw = obj.GetComponent<DynamicWeb>();
                if (clonedDw != null) clonedDw.ResetCloneState();
                return obj;
            }

            // 기본 Web 오브젝트 생성
            var webObj = new GameObject("DynamicWeb_template");
            webObj.transform.SetParent(parent, false);

            var rb = webObj.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            var col = webObj.AddComponent<BoxCollider>();
            col.isTrigger = true;

            var dw = webObj.AddComponent<DynamicWeb>();
            dw.webHeight = webHeight;
            dw.webThickness = webThickness;
            dw.webColor = webColor;
            dw.isTrigger = true;
            dw.showVisual = webShowVisual;

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

            // Anchor: 선박 없음 — 빈 GameObject만 생성 (DynamicWeb 두 번째 앵커)
            GameObject anchorClone = new GameObject($"Anchor_pair{index}");
            anchorClone.transform.SetParent(poolParent);
            anchorClone.transform.position = HIDDEN_POS + Vector3.right * (index * 100f + 50f);
            pair.anchorObject = anchorClone;

            // Web 복제 (템플릿 web이 없으면 새로 생성 — 그물 누락 절대 방지)
            if (templatePair.webObject != null)
            {
                GameObject webClone = Instantiate(templatePair.webObject, poolParent);
                webClone.name = $"Web_pair{index}";

                // Instantiate 시 복제된 비주얼 자식 즉시 제거 (Start()에서 새로 생성됨)
                for (int ci = webClone.transform.childCount - 1; ci >= 0; ci--)
                {
                    GameObject child = webClone.transform.GetChild(ci).gameObject;
                    if (child.name == "WebVisual" || child.name == "FishingNetVisual")
                        Object.DestroyImmediate(child);
                }

                pair.webObject = webClone;
            }
            else
            {
                Debug.LogWarning($"[CreatePairClone] templatePair.webObject가 null → 새 Web 생성 (pair{index})");
                pair.webObject = CreateWebObject(poolParent);
                pair.webObject.name = $"Web_pair{index}";
            }

            // Engine.RB 안전 초기화 (Boat.Awake 타이밍 이슈 방지)
            EnsureEngineRB(agent1Clone);

            // BufferSensor 크기 보정
            NormalizeBufferSensor(pair.agent1);

            // Web 교차 참조 설정
            pair.agent1.partnerAgent = null;
            pair.agent1.webObject = pair.webObject;
            pair.agent1.anchorTransform = anchorClone.transform;  // One-Way Towing 앵커
            pair.agent1.useArrowKeys = true;

            // 모선 참조
            if (motherShip != null)
                pair.agent1.motherShip = motherShip;

            // envController 참조
            if (envController != null)
                pair.agent1.envController = envController;

            // WebCollisionDetector/DynamicWeb 설정
            if (pair.webObject != null)
            {
                var webDetector = pair.webObject.GetComponent<WebCollisionDetector>();
                if (webDetector == null)
                    webDetector = pair.webObject.AddComponent<WebCollisionDetector>();
                if (envController != null)
                    webDetector.envController = envController;

                var dynamicWeb = pair.webObject.GetComponent<DynamicWeb>();
                if (dynamicWeb == null)
                {
                    Debug.LogWarning($"[CreatePairClone] DynamicWeb 컴포넌트 없음 → 추가 (pair{index})");
                    dynamicWeb = pair.webObject.AddComponent<DynamicWeb>();
                }
                dynamicWeb.ResetCloneState();
                dynamicWeb.defenseShip1 = pair.agent1.transform;
                dynamicWeb.defenseShip2 = anchorClone.transform;  // empty anchor as second point
                if (envController != null)
                    dynamicWeb.envController = envController;

                webDetector.parentDynamicWeb = dynamicWeb;
            }

            Debug.LogWarning($"[CreatePairClone] index={index}, a1={agent1Clone.name}, anchor={anchorClone.name}, " +
                $"web={pair.webObject?.name}, a1Engine={pair.agent1?._engine != null}, " +
                $"a1RB={pair.agent1?._engine?.RB != null}");

            return pair;
        }

        /// <summary>
        /// BufferSensorComponent의 ObservableSize/MaxNumObservables 강제 보정
        /// Inspector 불일치 방지 (코드에서 AppendObservation 4개 값 사용)
        /// inactive 오브젝트에서도 동작 (Awake 전에 호출 가능)
        /// </summary>
        private void NormalizeBufferSensor(DefenseAgent agent)
        {
            if (agent == null) return;
            var buf = agent.enemyBufferSensor;
            if (buf == null)
                buf = agent.GetComponent<BufferSensorComponent>();
            if (buf == null)
                buf = agent.gameObject.AddComponent<BufferSensorComponent>();
            buf.ObservableSize = 3;   // Dist, SignedBrg, Hdg (DefenseAgent와 일치)
            buf.MaxNumObservables = agent.enemyMaxObservables;  // 인스펙터 값 사용
            agent.enemyBufferSensor = buf;
        }

        /// <summary>
        /// 클론된 선박의 Engine.RB가 null이면 수동 할당
        /// </summary>
        private void EnsureEngineRB(GameObject agentObj)
        {
            var boat = agentObj.GetComponent<Boat>();
            if (boat != null && boat.engine != null && boat.engine.RB == null)
            {
                boat.engine.RB = agentObj.GetComponent<Rigidbody>();
                Debug.LogWarning($"[EnsureEngineRB] {agentObj.name}: Engine.RB 수동 할당 완료 (RB={boat.engine.RB != null})");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Sequential deploy — 1.5초 간격으로 한 쌍씩 배치
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 1단계: 리셋 + 클러스터 계산 + 스폰 위치 사전 계산.
        /// 실제 쌍 배치는 DeployNextSequentialPair()로 하나씩 처리.
        /// </summary>
        public void PrepareSequentialDeploy(
            FormationType formationType, float approachAngleDeg,
            float[] diversionaryAngles, int requestedCount,
            Vector3 motherPos, SimpleMultiAgentGroup agentGroup)
        {
            if (!_initialized || _pairPool == null) return;

            GenerateLaunchZones();

            // 클러스터 계산
            _seqEnemies = envController != null ? envController.enemyShips : null;
            var clusters = ClusterEnemiesByAngle(motherPos, _seqEnemies, clusterBinWidthDeg);
            clusters = LimitClusters(clusters, maxPairCount, _seqEnemies);
            CurrentClusters = clusters;
            _seqClusters = clusters;

            int pairCount = clusters.Count > 0 ? clusters.Count : requestedCount;
            pairCount = Mathf.Clamp(pairCount, 1, maxPairCount);
            _seqActiveCount = pairCount;       // 클러스터 수 = 활성 페어 수
            _deployedPairCount = pairCount;

            // 스폰 위치는 항상 maxPairCount개 계산 (예비 페어 포함)
            int totalSpawnCount = maxPairCount;

            StopAllCoroutines();
            ResetZoneCooldowns();

            // 모든 쌍 비활성화
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i].isActive && agentGroup != null)
                {
                    if (_pairPool[i].agent1 != null) agentGroup.UnregisterAgent(_pairPool[i].agent1);
                }
                SetPairActive(i, false);
            }

            // 구역 배정 및 스폰 위치 사전 계산 (pair index 예약 없음)
            Dictionary<int, float> zoneDirAngles;
            Dictionary<int, float> pairDirAngles;
            var zoneAssignments = AssignPairsToZones(
                formationType, approachAngleDeg, diversionaryAngles,
                totalSpawnCount, out zoneDirAngles, out pairDirAngles);

            _seqSpawnInfos = new List<SeqSpawnInfo>();
            foreach (var kvp in zoneAssignments)
            {
                int zoneIdx       = kvp.Key;
                var pairIndices   = kvp.Value;
                LaunchZone zone   = launchZones[zoneIdx];

                float lateralSpacing = 175f;
                float totalWidth     = (pairIndices.Count - 1) * lateralSpacing;
                float startOffset    = -totalWidth * 0.5f;

                for (int j = 0; j < pairIndices.Count; j++)
                {
                    Vector3 basePos;
                    Vector3 seqHDir;
                    if (zone.hasWorldPos)
                    {
                        basePos  = zone.worldPos;
                        float hr = zone.angleDeg * Mathf.Deg2Rad;
                        seqHDir  = new Vector3(Mathf.Sin(hr), 0f, Mathf.Cos(hr));
                    }
                    else
                    {
                        float rearDeg = GetMotherShipRearAngleDeg();
                        float rearRad = rearDeg * Mathf.Deg2Rad;
                        seqHDir  = new Vector3(Mathf.Sin(rearRad), 0f, Mathf.Cos(rearRad));
                        basePos  = motherPos + seqHDir * zone.distance;
                    }

                    Vector3 seqLateral    = new Vector3(seqHDir.z, 0f, -seqHDir.x);
                    float effectiveStagger = pairDepthStagger > 0.1f ? pairDepthStagger : 20f;
                    float effectiveLateral = lateralSpacing > 0.1f ? lateralSpacing : 20f;
                    float lateralOffset    = startOffset + j * effectiveLateral;
                    float totalDepth       = (pairIndices.Count - 1) * effectiveStagger;
                    float depthOffset      = j * effectiveStagger - totalDepth * 0.5f;
                    Vector3 pairCenter     = basePos + seqHDir * depthOffset + seqLateral * lateralOffset;

                    // One-Way Towing: towDir 반대 방향으로 스폰 오프셋
                    if (Mathf.Abs(additionalLateralOffset) > 0.1f)
                        pairCenter += additionalLateralDir * additionalLateralOffset;

                    _seqSpawnInfos.Add(new SeqSpawnInfo
                    {
                        pairCenter    = pairCenter,
                        zoneDir       = seqHDir,
                        zoneIdx       = zoneIdx,
                        spawnAngleDeg = zone.angleDeg
                    });
                }
            }

            _seqAssignedClusterIdx = new HashSet<int>();
            _seqAssignedEnemyIdx   = new HashSet<int>();
            _seqDeployNext         = 0;
            _seqAgentGroup         = agentGroup;

            Debug.LogWarning($"[PrepareSequentialDeploy] clusters={clusters.Count}, totalPairs={_seqSpawnInfos.Count}");
        }

        /// <summary>
        /// 2단계: 준비된 스폰 위치 목록에서 다음 쌍 하나를 배치.
        /// </summary>
        /// <returns>배치 성공 여부 (더 이상 배치할 쌍이 없으면 false)</returns>
        public bool DeployNextSequentialPair()
        {
            if (_seqSpawnInfos == null || _seqDeployNext >= _seqSpawnInfos.Count) return false;

            var info = _seqSpawnInfos[_seqDeployNext++];
            bool asStandby = (_seqDeployNext - 1) >= _seqActiveCount;

            int pi = GetOrCreateInactivePair();
            if (pi < 0) return false;

            DefensePair pair = _pairPool[pi];
            pair.isActive         = true; // 즉시 예약
            pair.assignedZoneIndex = info.zoneIdx;
            pair.launchAngleDeg   = info.spawnAngleDeg;

            if (_seqEnemies != null)
            {
                if (pair.agent1 != null) pair.agent1.enemyShips = _seqEnemies;
            }

            // 클러스터 배정: pairCenter에서 가장 가까운 미배정 클러스터
            int clusterIdx = (_seqClusters != null && _seqClusters.Count > 0)
                ? FindBestClusterForPair(info.pairCenter, _seqClusters, _seqAssignedClusterIdx)
                : -1;
            if (clusterIdx >= 0) _seqAssignedClusterIdx.Add(clusterIdx);

            pair.assignedClusterIdx  = clusterIdx;
            pair.clusterCentroid     = clusterIdx >= 0 ? _seqClusters[clusterIdx].centroidWorld : Vector3.zero;
            pair.clusterEnemyIndices = clusterIdx >= 0 ? new List<int>(_seqClusters[clusterIdx].enemyIndices) : null;

            // 클러스터 representative를 초기 타겟으로 배정
            int repIdx = clusterIdx >= 0 ? _seqClusters[clusterIdx].representativeIdx : -1;
            int initTarget = repIdx >= 0 ? repIdx + 1 : -1;
            if (pair.agent1 != null) pair.agent1.assignedTargetIndex = initTarget;

            // Residual policy용 클러스터 타겟
            if (clusterIdx >= 0)
            {
                Vector3 centroid = pair.clusterCentroid;
                if (pair.agent1 != null) pair.agent1.SetClusterTarget(centroid);
            }

            // 스폰 방향 = 모선 후미 방향
            float seqRearDeg = GetMotherShipRearAngleDeg();
            Quaternion rot = Quaternion.Euler(0f, seqRearDeg, 0f);
            Vector3 perpRightSeq = new Vector3(info.zoneDir.z, 0f, -info.zoneDir.x);

            // 단일 선박 spawn: agent1만 배치, anchorObject는 agent1 위치에 고정
            Vector3 pos1 = info.pairCenter;
            pos1.y = _templateAgent1Y;

            ResetAgent(pair.agent1, pos1, rot);

            // anchorObject: agent1과 동일 위치 (그물 dist=0 → 자동 숨김)
            if (pair.anchorObject != null)
                pair.anchorObject.transform.position = pos1;

            pair.deployLateralDir = perpRightSeq;
            pair.initialWebDir = perpRightSeq;
            pair.isSplitting = false;
            pair.isDisarmed  = false;

            int currentStep = envController != null ? envController.CurrentStep : 0;
            pair.deployStep     = currentStep;
            pair.splitStartStep = -1;

            // 예비 페어: Neutralized 상태로 배치, MA-POCA 미등록
            if (asStandby)
            {
                pair.isStandby = true;
                if (pair.agent1 != null) pair.agent1.SetNeutralized(true);
                if (pair.agent1 != null) pair.agent1.isLeftAgent = true;
                SetPairActive(pi, true);
                _totalPairsDeployed++;
                Debug.LogWarning($"[DeployNextSequentialPair] #{_seqDeployNext-1}/{_seqSpawnInfos.Count} STANDBY, pi={pi}");
                return true;
            }

            pair.isStandby = false;
            SetPairActive(pi, true);
            _totalPairsDeployed++;

            if (_seqAgentGroup != null)
            {
                if (pair.agent1 != null) _seqAgentGroup.RegisterAgent(pair.agent1);
            }

            if (pair.agent1 != null) pair.agent1.isLeftAgent = true;

            Debug.LogWarning($"[DeployNextSequentialPair] #{_seqDeployNext-1}/{_seqSpawnInfos.Count}, pi={pi}, cluster={clusterIdx}");
            return true;
        }

        /// <summary>
        /// 쌍을 진수구역에 배정.
        /// 진수 위치는 항상 모선 후미 구역 (zone 0~2) 고정.
        /// 포메이션별 차이는 pairDirAngles(바라보는 방향)에만 반영.
        /// </summary>
        private Dictionary<int, List<int>> AssignPairsToZones(
            FormationType formationType, float approachAngleDeg,
            float[] diversionaryAngles, int pairCount,
            out Dictionary<int, float> zoneDirAngles, out Dictionary<int, float> pairDirAngles)
        {
            var assignments = new Dictionary<int, List<int>>();
            zoneDirAngles = new Dictionary<int, float>();
            pairDirAngles = new Dictionary<int, float>();

            int zoneCount = launchZones.Length; // 1~3

            // 쌍별 바라볼 방향 결정 (포메이션에 따라 다름, 진수 위치와 무관)
            float[] pairLookAngles = new float[pairCount];
            if (formationType == FormationType.Diversionary && diversionaryAngles != null && diversionaryAngles.Length > 1)
            {
                int dirCount = diversionaryAngles.Length;
                for (int i = 0; i < pairCount; i++)
                    pairLookAngles[i] = diversionaryAngles[i % dirCount] * Mathf.Rad2Deg;
            }
            else
            {
                for (int i = 0; i < pairCount; i++)
                    pairLookAngles[i] = approachAngleDeg;
            }

            // 후미 구역에 순서대로 배정 (구역 수 = maxPairCount, 쌍 수 ≤ 구역 수 보장)
            for (int i = 0; i < pairCount; i++)
            {
                int zoneIdx = i % zoneCount; // 0, 1, 2, 0, 1, ... (구역 순환)
                if (!assignments.ContainsKey(zoneIdx))
                    assignments[zoneIdx] = new List<int>();
                assignments[zoneIdx].Add(i);
                pairDirAngles[i] = pairLookAngles[i];
            }

            return assignments;
        }

        /// <summary>
        /// 활성 적군을 모선 기준 접근 방향으로 Angular Bin 클러스터링
        /// centroidWorld, representativeIdx 포함 계산
        /// </summary>
        public List<EnemyCluster> ClusterEnemiesByAngle(Vector3 motherPos, GameObject[] enemies, float binWidthDeg)
        {
            var result = new List<EnemyCluster>();
            if (enemies == null || enemies.Length == 0) return result;

            float halfBin = binWidthDeg * 0.5f;

            // 1. 활성 적군 → (인덱스, 접근 각도) 수집
            var angleList = new List<(int idx, float angleDeg)>();
            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] == null || !enemies[i].activeInHierarchy) continue;
                Vector3 toEnemy = enemies[i].transform.position - motherPos;
                toEnemy.y = 0f;
                float angle = Mathf.Atan2(toEnemy.x, toEnemy.z) * Mathf.Rad2Deg;
                if (angle < 0f) angle += 360f;
                angleList.Add((i, angle));
            }
            if (angleList.Count == 0) return result;

            // 2. 각도 오름차순 정렬
            angleList.Sort((a, b) => a.angleDeg.CompareTo(b.angleDeg));

            // 3. 순서대로 빈 클러스터에 할당
            var processed = new bool[angleList.Count];
            for (int i = 0; i < angleList.Count; i++)
            {
                if (processed[i]) continue;

                var indices = new List<int> { angleList[i].idx };
                processed[i] = true;

                for (int j = i + 1; j < angleList.Count; j++)
                {
                    if (processed[j]) continue;
                    if (Mathf.Abs(Mathf.DeltaAngle(angleList[i].angleDeg, angleList[j].angleDeg)) <= halfBin)
                    {
                        indices.Add(angleList[j].idx);
                        processed[j] = true;
                    }
                }

                result.Add(ComputeClusterMeta(angleList[i].angleDeg, indices, enemies));
            }

            return result;
        }

        /// <summary>
        /// 클러스터 메타(centroid, representative) 계산 헬퍼
        /// </summary>
        private EnemyCluster ComputeClusterMeta(float centerAngleDeg, List<int> indices, GameObject[] enemies)
        {
            Vector3 centroid = Vector3.zero;
            int validCount = 0;
            foreach (int idx in indices)
            {
                if (enemies != null && idx < enemies.Length && enemies[idx] != null && enemies[idx].activeInHierarchy)
                { centroid += enemies[idx].transform.position; validCount++; }
            }
            if (validCount > 0) centroid /= validCount;

            // centroid에 가장 가까운 활성 적군 = representative
            int repIdx = -1;
            float repDist2 = float.MaxValue;
            foreach (int idx in indices)
            {
                if (enemies == null || idx >= enemies.Length || enemies[idx] == null || !enemies[idx].activeInHierarchy) continue;
                float d2 = Vector3.SqrMagnitude(enemies[idx].transform.position - centroid);
                if (d2 < repDist2) { repDist2 = d2; repIdx = idx; }
            }

            return new EnemyCluster
            {
                centerAngleDeg   = centerAngleDeg,
                centroidWorld    = centroid,
                representativeIdx = repIdx,
                enemyIndices     = indices
            };
        }

        /// <summary>
        /// 클러스터 수가 maxK 초과하면 가장 가까운 두 클러스터를 반복 병합 (circular mean 사용)
        /// </summary>
        public List<EnemyCluster> LimitClusters(List<EnemyCluster> clusters, int maxK, GameObject[] enemies)
        {
            while (clusters.Count > maxK)
            {
                // 각도가 가장 가까운 두 클러스터 탐색
                int bestA = 0, bestB = 1;
                float bestDiff = float.MaxValue;
                for (int i = 0; i < clusters.Count - 1; i++)
                    for (int j = i + 1; j < clusters.Count; j++)
                    {
                        float diff = Mathf.Abs(Mathf.DeltaAngle(clusters[i].centerAngleDeg, clusters[j].centerAngleDeg));
                        if (diff < bestDiff) { bestDiff = diff; bestA = i; bestB = j; }
                    }

                // 병합 인덱스 목록
                var mergedIndices = new List<int>(clusters[bestA].enemyIndices);
                mergedIndices.AddRange(clusters[bestB].enemyIndices);

                // 가중 원형 평균으로 중심각 계산
                float wa = clusters[bestA].enemyIndices.Count;
                float wb = clusters[bestB].enemyIndices.Count;
                float sinSum = wa * Mathf.Sin(clusters[bestA].centerAngleDeg * Mathf.Deg2Rad)
                             + wb * Mathf.Sin(clusters[bestB].centerAngleDeg * Mathf.Deg2Rad);
                float cosSum = wa * Mathf.Cos(clusters[bestA].centerAngleDeg * Mathf.Deg2Rad)
                             + wb * Mathf.Cos(clusters[bestB].centerAngleDeg * Mathf.Deg2Rad);
                float mergedAngle = Mathf.Atan2(sinSum, cosSum) * Mathf.Rad2Deg;
                if (mergedAngle < 0f) mergedAngle += 360f;

                var merged = ComputeClusterMeta(mergedAngle, mergedIndices, enemies);

                var next = new List<EnemyCluster>(clusters.Count - 1);
                for (int i = 0; i < clusters.Count; i++)
                    if (i != bestA && i != bestB) next.Add(clusters[i]);
                next.Add(merged);
                clusters = next;
            }
            return clusters;
        }

        /// <summary>
        /// 클러스터 목록에서 쌍 방향(zoneDirAngleDeg)에 가장 가까운 클러스터 인덱스 반환
        /// assignedClusters: 이미 다른 쌍에 배정된 클러스터 인덱스 집합 (중복 방지)
        /// </summary>
        private int FindBestClusterForPair(Vector3 pairCenter,
            List<EnemyCluster> clusters, HashSet<int> assignedClusters)
        {
            // 미배정 클러스터 중 pairCenter에서 가장 가까운 것 선택
            int bestIdx = -1;
            float bestDist = float.MaxValue;
            for (int c = 0; c < clusters.Count; c++)
            {
                if (assignedClusters.Contains(c)) continue;
                float dist = Vector3.Distance(pairCenter, clusters[c].centroidWorld);
                if (dist < bestDist) { bestDist = dist; bestIdx = c; }
            }
            return bestIdx; // 미배정 클러스터 없으면 -1
        }

        /// <summary>
        /// 클러스터 내에서 가장 가까운 미배정 적군 인덱스 반환 (-1 = 없음)
        /// </summary>
        private int FindClosestUnassignedInCluster(Vector3 pairCenter,
            EnemyCluster cluster, GameObject[] enemies, HashSet<int> assignedEnemies)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            foreach (int ei in cluster.enemyIndices)
            {
                if (assignedEnemies.Contains(ei)) continue;
                if (enemies[ei] == null || !enemies[ei].activeInHierarchy) continue;
                float d = Vector3.SqrMagnitude(pairCenter - enemies[ei].transform.position);
                if (d < bestDist) { bestDist = d; best = ei; }
            }
            return best;
        }

        /// <summary>
        /// 주어진 각도에 가까운 순서로 진수구역 인덱스 정렬 반환
        /// </summary>
        private List<int> GetZonesSortedByAngle(float angleDeg)
        {
            var sorted = new List<int>();
            for (int i = 0; i < launchZones.Length; i++)
                sorted.Add(i);

            sorted.Sort((a, b) =>
            {
                float diffA = Mathf.Abs(Mathf.DeltaAngle(angleDeg, launchZones[a].angleDeg));
                float diffB = Mathf.Abs(Mathf.DeltaAngle(angleDeg, launchZones[b].angleDeg));
                int cmp = diffA.CompareTo(diffB);
                if (cmp != 0) return cmp;
                // 동점: 랜덤 → 시계/반시계 편향 제거
                return Random.value < 0.5f ? -1 : 1;
            });

            return sorted;
        }

        /// <summary>
        /// 주어진 각도에 가장 가까운 진수구역 인덱스 반환
        /// </summary>
        public int FindClosestZone(float angleDeg)
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
        /// 비활성 쌍을 풀에서 찾거나, 없으면 새로 생성하여 반환
        /// maxPairCount 초과 시 -1 반환 (생성 불가)
        /// </summary>
        private int GetOrCreateInactivePair()
        {
            // 1. 기존 비활성 쌍 찾기
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (!_pairPool[i].isActive)
                {
                    // Debug.LogWarning($"[GetOrCreate] 기존 비활성 쌍 반환: index={i}, " +
                    //     $"agent1={_pairPool[i].agent1?.name}, poolCount={_pairPool.Count}");
                    return i;
                }
            }

            // 2. 풀 상한 체크 (Neutralized 쌍은 제외 — 그물 유지 중이므로 풀 슬롯 차지 안 함)
            int effectiveCount = 0;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (!IsPairNeutralized(_pairPool[i])) effectiveCount++;
            }
            if (effectiveCount >= maxPairCount)
            {
                Debug.LogWarning($"[GetOrCreate] 풀 상한 도달: effective={effectiveCount}/{maxPairCount} (total={_pairPool.Count})");
                return -1;
            }

            // 3. 새 쌍 생성
            Debug.LogWarning($"[GetOrCreate] 새 쌍 생성 시도: index={_pairPool.Count}/{maxPairCount}");
            Transform poolParent = templatePair.agent1.transform.parent != null
                ? templatePair.agent1.transform.parent
                : transform;

            int newIndex = _pairPool.Count;
            DefensePair newPair;
            try
            {
                newPair = CreatePairClone(newIndex, poolParent);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GetOrCreate] CreatePairClone 예외: {e.Message}\n{e.StackTrace}");
                return -1;
            }
            newPair.isActive = false;
            _pairPool.Add(newPair);

            // 클론을 깨끗한 비활성 상태로 초기화 (HIDDEN_POS, isKinematic, web 비활성)
            SetPairActive(newIndex, false);

            // Debug.LogWarning($"[GetOrCreate] 새 쌍 생성: index={newIndex}, " +
            //     $"agent1={newPair.agent1?.name}, agent2={newPair.agent2?.name}, " +
            //     $"web={newPair.webObject?.name}, pool={_pairPool.Count}/{maxPairCount}");
            return newIndex;
        }

        /// <summary>
        /// 에이전트 위치/회전/물리 리셋
        /// rb.position/rb.rotation + rb.Sleep()으로 물리 안정성 확보
        /// </summary>
        private void ResetAgent(DefenseAgent agent, Vector3 position, Quaternion rotation)
        {
            if (agent == null) return;

            Vector3 beforePos = agent.transform.position;

            // 내부 플래그 리셋 (_episodeEnded, _neutralized 등)
            agent.ResetForDeployment();

            // Transform 먼저 설정
            agent.transform.position = position;
            agent.transform.rotation = rotation;

            if (agent.TryGetComponent<Rigidbody>(out var rb))
            {
                bool wasKinematic = rb.isKinematic;
                rb.isKinematic = false;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = position;
                rb.rotation = rotation;
                rb.WakeUp(); // Sleep() 대신 WakeUp()으로 확실히 물리 활성화

                // Debug.LogWarning($"[ResetAgent] {agent.name}: " +
                //     $"before={beforePos} → after={position}, " +
                //     $"kinematic={wasKinematic}→false, " +
                //     $"engine={agent._engine != null}, " +
                //     $"engineRB={agent._engine?.RB != null}, " +
                //     $"transform.pos={agent.transform.position}");
            }
            else
            {
                // Debug.LogError($"[ResetAgent] {agent.name}: Rigidbody를 찾을 수 없습니다!");
            }

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
            if (index >= _pairPool.Count) return;
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
                        rb1.isKinematic = true;
                    }
                }
                if (pair.anchorObject != null)
                    pair.anchorObject.transform.position = HIDDEN_POS + Vector3.right * (index * 100f + 50f);
            }
            else
            {
                // "활성화": agent1 kinematic 해제
                if (pair.agent1 != null)
                {
                    if (!pair.agent1.gameObject.activeSelf)
                        pair.agent1.gameObject.SetActive(true);
                    if (pair.agent1.TryGetComponent<Rigidbody>(out var rb1))
                        rb1.isKinematic = false;
                }
            }

            // Web만 SetActive 토글 — 활성화 시 web 누락이면 즉시 복구
            if (pair.webObject == null && active)
            {
                Debug.LogError($"[SetPairActive] pair{index} 활성화 시 webObject가 null! 즉시 복구 시도");
                EnsurePairWebIntegrity(pair, index);
            }
            if (pair.webObject != null)
            {
                // 비활성화 시: isDisarmed=true(정지 트랩 설치 완료)이면 web을 유지
                // → 선박은 HIDDEN_POS로 이동하더라도 설치된 트랩은 에피소드 내내 남아있어야 함
                // → ResetScene()에서 isDisarmed=false로 클리어된 후 다음 DeployPairs에서 정리됨
                bool hideWeb = active ? false : !pair.isDisarmed;
                if (active || hideWeb)
                {
                    pair.webObject.SetActive(active);
                    if (active)
                    {
                        var dw = pair.webObject.GetComponent<DynamicWeb>();
                        if (dw != null)
                        {
                            dw.UnfreezeWeb();         // 이전 에피소드 고정 상태 해제
                            dw.EnsureVisualExists();
                        }
                    }
                }
            }
            pair.isActive = active;

            // 활성화 시 Disarmed/Deploying 상태 리셋; 비활성화 시 isStandby 리셋
            if (active)
            {
                pair.isDisarmed = false;
                pair.disarmStep = -1;
            }
            else
            {
                pair.isStandby = false;
            }
        }

        /// <summary>
        /// 쌍의 그물(Web) 무결성 검증 및 복구
        /// webObject가 null이거나, DynamicWeb/WebCollisionDetector가 없거나,
        /// defenseShip 참조가 잘못된 경우 모두 수정
        /// </summary>
        private void EnsurePairWebIntegrity(DefensePair pair, int index)
        {
            // 1. webObject 자체가 null → 새로 생성
            if (pair.webObject == null)
            {
                Debug.LogWarning($"[EnsurePairWebIntegrity] pair{index} webObject null → 새 Web 생성");
                pair.webObject = CreateWebObject(transform);
                pair.webObject.name = $"Web_pair{index}_recovered";

                // Agent에도 참조 복구
                if (pair.agent1 != null) pair.agent1.webObject = pair.webObject;
            }

            // 2. DynamicWeb 컴포넌트 확인
            var dynamicWeb = pair.webObject.GetComponent<DynamicWeb>();
            if (dynamicWeb == null)
            {
                Debug.LogWarning($"[EnsurePairWebIntegrity] pair{index} DynamicWeb 없음 → 추가");
                dynamicWeb = pair.webObject.AddComponent<DynamicWeb>();
            }

            // 3. defenseShip 참조 확인 및 복구
            if (pair.agent1 != null && dynamicWeb.defenseShip1 != pair.agent1.transform)
                dynamicWeb.defenseShip1 = pair.agent1.transform;
            if (pair.anchorObject != null && dynamicWeb.defenseShip2 != pair.anchorObject.transform)
                dynamicWeb.defenseShip2 = pair.anchorObject.transform;

            // 4. envController 참조 확인
            if (envController != null && dynamicWeb.envController == null)
                dynamicWeb.envController = envController;

            // 5. WebCollisionDetector 확인
            var webDetector = pair.webObject.GetComponent<WebCollisionDetector>();
            if (webDetector == null)
            {
                Debug.LogWarning($"[EnsurePairWebIntegrity] pair{index} WebCollisionDetector 없음 → 추가");
                webDetector = pair.webObject.AddComponent<WebCollisionDetector>();
            }
            if (envController != null && webDetector.envController == null)
                webDetector.envController = envController;

            // 6. Rigidbody + BoxCollider 확인 (충돌 감지 필수)
            var rb = pair.webObject.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = pair.webObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            var col = pair.webObject.GetComponent<BoxCollider>();
            if (col == null)
            {
                col = pair.webObject.AddComponent<BoxCollider>();
                col.isTrigger = true;
            }

            // 7. Agent의 webObject 참조 일관성 보장
            if (pair.agent1 != null && pair.agent1.webObject != pair.webObject)
                pair.agent1.webObject = pair.webObject;
            // 8. 비주얼 무결성: DynamicWeb 내부 비주얼 오브젝트가 파괴되었으면 재초기화
            if (dynamicWeb != null && dynamicWeb.showVisual)
            {
                dynamicWeb.EnsureVisualExists();
            }
        }

        /// <summary>
        /// 모든 활성 쌍의 에이전트 목록 반환
        /// </summary>
        public List<DefenseAgent> GetActiveAgents()
        {
            var agents = new List<DefenseAgent>();
            if (_pairPool == null) return agents;

            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive)
                {
                    if (_pairPool[i].agent1 != null) agents.Add(_pairPool[i].agent1);
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
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive)
                {
                    // Neutralized 쌍 (Convoy EXIT 후 그물 유지)은 활성 카운트에서 제외
                    if (IsPairNeutralized(_pairPool[i])) continue;
                    count++;
                }
            }
            return count;
        }

        /// <summary>첫 번째 예비 페어 인덱스 반환 (-1=없음)</summary>
        public int GetFirstStandbyPairIndex()
        {
            if (_pairPool == null) return -1;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive && _pairPool[i].isStandby)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 예비 페어를 활성화: Neutralized 해제 + MA-POCA 등록 + 클러스터 배정 + 가이던스 시작
        /// </summary>
        public bool ActivateStandbyPair(int pairIdx, EnemyCluster? cluster, SimpleMultiAgentGroup agentGroup)
        {
            if (_pairPool == null || pairIdx < 0 || pairIdx >= _pairPool.Count) return false;
            DefensePair pair = _pairPool[pairIdx];
            if (pair == null || !pair.isActive || !pair.isStandby) return false;

            pair.isStandby = false;
            if (pair.agent1 != null) pair.agent1.SetNeutralized(false);

            // MA-POCA 등록
            if (agentGroup != null)
            {
                if (pair.agent1 != null) agentGroup.RegisterAgent(pair.agent1);
            }

            // 클러스터 배정 + 가이던스
            if (cluster.HasValue)
            {
                pair.clusterCentroid     = cluster.Value.centroidWorld;
                pair.clusterEnemyIndices = cluster.Value.enemyIndices != null
                    ? new System.Collections.Generic.List<int>(cluster.Value.enemyIndices) : null;
                int repIdx     = cluster.Value.representativeIdx;
                int initTarget = repIdx >= 0 ? repIdx + 1 : -1;
                if (pair.agent1 != null) pair.agent1.assignedTargetIndex = initTarget;

                Vector3 centroid = pair.clusterCentroid;
                pair.agent1?.SetClusterTarget(centroid);
            }

            _deployedPairCount++;
            Debug.LogWarning($"[LaunchZoneManager] Pair {pairIdx} 예비 → 활성화, cluster={cluster?.centerAngleDeg:F0}°");
            return true;
        }

        /// <summary>
        /// 쌍의 양쪽 에이전트가 모두 Neutralized인지 확인
        /// </summary>
        private bool IsPairNeutralized(DefensePair pair)
        {
            if (pair == null) return false;
            return pair.agent1 != null && pair.agent1.IsNeutralized;
        }

        /// <summary>
        /// 작전 중인 쌍 수 (활성 + Neutralized 그물 유지 중)
        /// 에피소드 종료 판단용 — 그물이 남아있으면 아직 작전 중
        /// </summary>
        public int GetOperationalPairCount()
        {
            if (_pairPool == null) return 0;
            int count = 0;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 이번 에피소드에서 배치된 쌍 수 (한 번이라도 배치된 적이 있는지 확인용)
        /// </summary>
        public int GetDeployedPairCount() => _deployedPairCount;

        /// <summary>총 배치 카운터 리셋 (에피소드 시작 시 호출)</summary>
        public void ResetTotalPairsDeployed() => _totalPairsDeployed = 0;

        /// <summary>
        /// 풀 초기화 여부
        /// </summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// 풀 전체 용량 반환 (최대 생성 가능 수)
        /// </summary>
        public int GetPoolCapacity() => maxPairCount;

        /// <summary>
        /// 풀 인덱스로 쌍 반환 (Commander 관측용)
        /// </summary>
        public DefensePair GetPair(int index)
        {
            if (_pairPool == null || index < 0 || index >= _pairPool.Count)
                return null;
            return _pairPool[index];
        }

        /// <summary>웹 오브젝트로 쌍 인덱스 조회 (-1=없음)</summary>
        public int GetPairIndexByWeb(GameObject webObj)
        {
            if (_pairPool == null || webObj == null) return -1;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].webObject == webObj)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 비활성 풀에서 아군 쌍 1개를 추가 배치 (런타임 버튼용)
        /// 모선 주변 랜덤 진수구역에 배치
        /// </summary>
        /// <returns>배치 성공 여부</returns>
        public bool DeploySinglePair(Vector3 motherPos, float approachAngleDeg, SimpleMultiAgentGroup agentGroup, int forceZoneIdx = -1)
        {
            if (!_initialized || _pairPool == null) return false;
            if (_pairPool.Count >= maxPairCount && GetInactivePairCount() == 0) return false;
            if (maxTotalPairsPerEpisode > 0 && _totalPairsDeployed >= maxTotalPairsPerEpisode) return false;

            // 1. 스폰 위치 계산 (쿨다운 중인 구역은 다음 가까운 구역으로)
            int currentStep = envController != null ? envController.CurrentStep : 0;
            int zoneIdx;
            if (forceZoneIdx >= 0)
            {
                zoneIdx = forceZoneIdx;
            }
            else
            {
                // 가장 가까운 구역부터 탐색 (±90° 이내 + 쿨다운 아닌 구역 + 활성 쌍 한도 미초과)
                var sorted = GetZonesSortedByAngle(approachAngleDeg);
                zoneIdx = -1;
                foreach (int zi in sorted)
                {
                    float angleDiff = Mathf.Abs(Mathf.DeltaAngle(approachAngleDeg, launchZones[zi].angleDeg));
                    if (angleDiff > 90f) break; // 정렬 순서상 이후 전부 90° 초과
                    if (IsZoneOnCooldown(zi, currentStep)) continue;
                    zoneIdx = zi;
                    break;
                }
                if (zoneIdx < 0) return false; // 적 방향 ±90° 내 사용 가능 구역 없음
            }
            if (zoneIdx >= launchZones.Length) zoneIdx = 0;
            LaunchZone zone = launchZones[zoneIdx];

            // 스폰 방향 강제 없음 — identity 회전, 좌우 배치는 zone 방향 기준
            Vector3 pairCenter;
            Vector3 singleHDir;
            if (zone.hasWorldPos)
            {
                pairCenter  = zone.worldPos;
                float hr    = zone.angleDeg * Mathf.Deg2Rad;
                singleHDir  = new Vector3(Mathf.Sin(hr), 0f, Mathf.Cos(hr));
            }
            else
            {
                float ang  = zone.angleDeg * Mathf.Deg2Rad;
                singleHDir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                pairCenter = motherPos + singleHDir * zone.distance;
            }

            Vector3 singlePerp = new Vector3(singleHDir.z, 0f, -singleHDir.x);
            Quaternion rot = Quaternion.identity;

            Vector3 pos1 = pairCenter;
            pos1.y = _templateAgent1Y;

            // 2. 기존 비활성 쌍 재사용 또는 프리팹에서 새로 생성
            int pairIdx;
            DefensePair pair;
            GameObject[] enemies = envController != null ? envController.enemyShips : null;

            int existingInactive = FindInactivePairIndex();
            if (existingInactive >= 0)
            {
                pairIdx = existingInactive;
                pair = _pairPool[pairIdx];
                EnsurePairWebIntegrity(pair, pairIdx);
            }
            else
            {
                if (defenseBoatPrefab == null)
                {
                    Debug.LogError("[DeploySingle] defenseBoatPrefab이 null! Inspector에서 할당하세요.");
                    return false;
                }

                pairIdx = _pairPool.Count;
                pair = SpawnPairFromPrefab(pairIdx, pos1, rot);
                if (pair == null) return false;
                _pairPool.Add(pair);
            }

            pair.assignedZoneIndex = zoneIdx;
            pair.launchAngleDeg = zone.angleDeg;
            pair.deployStep = envController != null ? envController.CurrentStep : 0;
            // 3. 적군 참조 설정
            if (enemies != null)
            {
                pair.agent1.enemyShips = enemies;
            }

            // ResetAgent (ResetForDeployment에서 assignedTargetIndex=-1)
            ResetAgent(pair.agent1, pos1, rot);

            pair.deployLateralDir = singlePerp;

            // 4. 활성화
            SetPairActive(pairIdx, true);
            _totalPairsDeployed++;

            // 5. MA-POCA 등록
            if (agentGroup != null)
            {
                agentGroup.RegisterAgent(pair.agent1);
            }

            _deployedPairCount++;

            if (pair.agent1 != null) pair.agent1.isLeftAgent = true;

            if (_zoneLastDeployStep != null && zoneIdx >= 0 && zoneIdx < _zoneLastDeployStep.Length)
                _zoneLastDeployStep[zoneIdx] = currentStep;

            Debug.LogWarning($"[DeploySingle] pair={pairIdx}, zone={zoneIdx}, " +
                $"a1={pair.agent1?.name} engine={pair.agent1?._engine != null} RB={pair.agent1?._engine?.RB != null}");
            return true;
        }

        /// <summary>
        /// 비활성 쌍 인덱스 검색 (없으면 -1)
        /// </summary>
        private int FindInactivePairIndex()
        {
            for (int i = 0; i < _pairPool.Count; i++)
                if (!_pairPool[i].isActive) return i;
            return -1;
        }

        /// <summary>
        /// 프리팹에서 직접 올바른 위치에 쌍 생성 (HIDDEN_POS 거치지 않음)
        /// </summary>
        private DefensePair SpawnPairFromPrefab(int index, Vector3 pos1, Quaternion rot)
        {
            Transform parent = transform;

            // Agent1: 프리팹에서 pos1 위치에 직접 생성 (단일 선박)
            GameObject a1Obj = Instantiate(defenseBoatPrefab, pos1, rot, parent);
            a1Obj.name = $"DefenseAgent1_pair{index}";
            var a1 = a1Obj.GetComponent<DefenseAgent>();

            if (a1 == null)
            {
                Debug.LogError($"[SpawnPairFromPrefab] DefenseAgent 컴포넌트 없음! prefab={defenseBoatPrefab.name}");
                Destroy(a1Obj);
                return null;
            }

            // Anchor: empty GameObject (DynamicWeb 두 번째 앵커, 선박 없음)
            GameObject anchorObj = new GameObject($"Anchor_pair{index}");
            anchorObj.transform.SetParent(parent);
            anchorObj.transform.position = pos1;  // 초기에는 agent1과 동일 위치

            // Web 생성
            GameObject webObj = CreateWebObject(parent);
            webObj.name = $"Web_pair{index}";
            webObj.transform.position = pos1;

            DefensePair pair = new DefensePair();
            pair.agent1 = a1;
            pair.anchorObject = anchorObj;
            pair.webObject = webObj;

            a1.partnerAgent = null;
            a1.webObject = webObj;
            a1.anchorTransform = anchorObj.transform;  // One-Way Towing 앵커
            a1.useArrowKeys = true;

            if (motherShip != null)
                a1.motherShip = motherShip;
            if (envController != null)
                a1.envController = envController;

            // Web 컴포넌트 설정
            {
                var webDetector = webObj.GetComponent<WebCollisionDetector>();
                if (webDetector == null) webDetector = webObj.AddComponent<WebCollisionDetector>();
                if (envController != null)
                    webDetector.envController = envController;

                var dynamicWeb = webObj.GetComponent<DynamicWeb>();
                if (dynamicWeb == null)
                {
                    Debug.LogWarning($"[SpawnPairFromPrefab] DynamicWeb 컴포넌트 없음 → 추가 (pair{index})");
                    dynamicWeb = webObj.AddComponent<DynamicWeb>();
                }
                dynamicWeb.ResetCloneState();
                dynamicWeb.defenseShip1 = a1.transform;
                dynamicWeb.defenseShip2 = anchorObj.transform;
                dynamicWeb.webAnchor1 = null;
                dynamicWeb.webAnchor2 = null;
                if (envController != null)
                    dynamicWeb.envController = envController;

                webDetector.parentDynamicWeb = dynamicWeb;
            }

            EnsureEngineRB(a1Obj);
            NormalizeBufferSensor(a1);

            pair.isActive = false; // SetPairActive에서 true로 변경됨
            return pair;
        }

        /// <summary>
        /// 모든 쌍 비활성화 + MA-POCA 해제 (activePairCount=0일 때 사용)
        /// </summary>
        public void DeactivateAllPairs(SimpleMultiAgentGroup agentGroup)
        {
            if (_pairPool == null) return;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i].isActive && agentGroup != null)
                {
                    if (_pairPool[i].agent1 != null)
                        agentGroup.UnregisterAgent(_pairPool[i].agent1);
                }
                SetPairActive(i, false);
            }
            _deployedPairCount = 0;
        }

        /// <summary>
        /// 배치 가능한 쌍 수 반환 (기존 비활성 + 아직 생성 가능한 수)
        /// </summary>
        public int GetInactivePairCount()
        {
            if (_pairPool == null) return 0;
            int existingInactive = 0;
            int effectiveCount = 0;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (!_pairPool[i].isActive) existingInactive++;
                if (!IsPairNeutralized(_pairPool[i])) effectiveCount++;
            }
            // 아직 생성되지 않은 쌍도 "사용 가능"에 포함 (Neutralized는 슬롯 차지 안 함)
            int canCreate = Mathf.Max(0, maxPairCount - effectiveCount);
            return existingInactive + canCreate;
        }

        /// <summary>
        /// 예비 쌍 배치 가능 여부 (총 예산 내에서 비활성 쌍이 있는지)
        /// totalBudget: 이 에피소드에서 사용 가능한 총 쌍 수 (activePairCount)
        /// </summary>
        public bool HasReservePairs()
        {
            int active = GetActivePairCount();
            int budget = Mathf.Min(activePairCount, maxPairCount);
            return active < budget && GetInactivePairCount() > 0;
        }

        public int GetCurrentPoolCount()
        {
            return _pairPool != null ? _pairPool.Count : 0;
        }

        /// <summary>
        /// 특정 쌍 비활성화 + MA-POCA 해제 (개별 무력화용)
        /// 에이전트는 현재 위치에서 정지 (HIDDEN_POS로 이동하지 않음)
        /// </summary>
        public void DisablePair(int pairIndex, SimpleMultiAgentGroup agentGroup)
        {
            if (_pairPool == null || pairIndex < 0 || pairIndex >= _pairPool.Count)
                return;
            DefensePair pair = _pairPool[pairIndex];
            if (!pair.isActive) return;

            if (agentGroup != null)
            {
                if (pair.agent1 != null) agentGroup.UnregisterAgent(pair.agent1);
            }

            // 에이전트를 현재 위치에서 정지 (이동하지 않음)
            FreezeAgent(pair.agent1);

            // ML-Agents 액션 처리 차단 (OnActionReceived에서 early return)
            if (pair.agent1 != null) pair.agent1.SetNeutralized(true);

            // Web 처리: 배치된 웹은 현재 위치에 고정하여 에피소드 끝까지 유지
            // (쌍이 비활성화돼도 설치된 그물은 그 자리에 남아 적 포획 계속 가능)
            if (pair.webObject != null && pair.webObject.activeSelf)
            {
                var dw = pair.webObject.GetComponent<DynamicWeb>();
                if (dw != null)
                {
                    if (!dw.IsFrozen)
                        dw.FreezeAtCurrentPositions();
                    // SetActive(false) 하지 않음 → 웹은 에피소드 끝까지 유지
                }
                else
                    pair.webObject.SetActive(false);
            }
            pair.isActive = false;

            // Debug.Log($"[LaunchZoneManager] Pair {pairIndex} 무력화 (현재 위치 정지)");
        }

        /// <summary>
        /// Stage9: 포획 후 직진 이탈 모드 (Disarm)
        /// MA-POCA 해제 + 그물 비활성화 + 에이전트 직진 모드 활성화
        /// 에이전트는 현재 헤딩 방향으로 전속력 직진하여 전장에서 자연스럽게 이탈
        /// </summary>
        public void DisarmPair(int pairIndex, SimpleMultiAgentGroup agentGroup, int currentStep)
        {
            if (_pairPool == null || pairIndex < 0 || pairIndex >= _pairPool.Count)
                return;
            DefensePair pair = _pairPool[pairIndex];
            if (!pair.isActive || pair.isDisarmed) return;

            // MA-POCA 해제 (Posthumous Credit 작동)
            if (agentGroup != null)
            {
                if (pair.agent1 != null) agentGroup.UnregisterAgent(pair.agent1);
            }

            // 그물 비활성화
            if (pair.webObject != null) pair.webObject.SetActive(false);

            // 직진 모드 활성화 (ML 정책 대신 하드코딩 직진)
            if (pair.agent1 != null) pair.agent1.SetStraightMode(true);

            // 소형 포획 존 활성화 (Disarm 이후 개별 Net Capture)
            if (pair.agent1 != null) pair.agent1.ActivateSingleNet();

            // 상태 전환: Active → Disarmed (isActive는 유지 — 관측에 보임)
            pair.isDisarmed = true;
            pair.disarmStep = currentStep;

            _deployedPairCount = Mathf.Max(0, _deployedPairCount - 1);

            Debug.Log($"[LaunchZoneManager] Pair {pairIndex} 직진 이탈 시작, step={currentStep}");
        }

        /// <summary>
        /// Stage9: 직진 이탈 완료 → 완전 비활성화 (HIDDEN_POS + 재활용 가능)
        /// </summary>
        public void DeactivateDisarmedPair(int pairIndex)
        {
            if (_pairPool == null || pairIndex < 0 || pairIndex >= _pairPool.Count)
                return;
            DefensePair pair = _pairPool[pairIndex];
            if (!pair.isDisarmed) return;

            // 직진 모드 해제 + neutralized 해제 (재활용 가능)
            if (pair.agent1 != null) { pair.agent1.SetStraightMode(false); pair.agent1.SetNeutralized(false); }

            // HIDDEN_POS로 이동 + 비활성화
            SetPairActive(pairIndex, false);
            pair.isDisarmed = false;
            pair.disarmStep = -1;
            pair.isSplitting = false;
            pair.splitStartStep = -1;
            pair.assignedZoneIndex = -1;
            pair.launchAngleDeg = -1f;
            pair.deployStep = -1;
            if (pair.agent1 != null) pair.agent1.assignedTargetIndex = -1;

            Debug.Log($"[LaunchZoneManager] Pair {pairIndex} 직진 이탈 완료 → 비활성화");
        }

        /// <summary>
        /// 포획 성공 시 쌍을 풀로 반환 (재활용 가능).
        /// DisablePair와 달리 HIDDEN_POS로 이동 + neutralized 해제하여 나중에 재배치 가능.
        /// Web은 즉시 비활성화하지 않고, delayWebDisable=true이면 0.5초 후 비활성화.
        /// </summary>
        /// <summary>
        /// 에피소드 리셋 시 모든 활성 쌍을 강제 비활성화 (chase 모드 전환 시 사용)
        /// pair.isDisarmed는 ResetScene()에서 이미 false로 초기화됐으므로 web도 함께 숨김
        /// </summary>
        public void DeactivateAllActivePairs(SimpleMultiAgentGroup agentGroup)
        {
            if (_pairPool == null) return;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                DefensePair pair = _pairPool[i];
                if (pair == null || !pair.isActive) continue;
                if (agentGroup != null)
                {
                    if (pair.agent1 != null) agentGroup.UnregisterAgent(pair.agent1);
                    }
                // isDisarmed는 이미 ResetScene에서 false → webObject도 정상 숨김
                SetPairActive(i, false);
            }
        }

        /// <summary>
        /// Chase 모드 전용: Phase1 완료 직후 형상으로 한 쌍을 즉시 활성화.
        /// 웹을 pos1/pos2 위치에 고정(isDisarmed=true)하고 SingleNet 포획 모드 진입.
        /// </summary>
        public void SpawnChaseReadyPair(
            Vector3 pos1, Vector3 pos2, Vector3 webCenter,
            GameObject[] enemies, Quaternion rotation,
            SimpleMultiAgentGroup agentGroup, int guidanceSteps,
            DefenseEnvController envCtrl)
        {
            if (!IsInitialized) { Debug.LogError("[SpawnChaseReadyPair] LZM not initialized"); return; }

            int pi = GetOrCreateInactivePair();
            if (pi < 0) { Debug.LogError("[SpawnChaseReadyPair] 사용 가능한 비활성 쌍 없음"); return; }

            DefensePair pair = _pairPool[pi];

            pos1.y = _templateAgent1Y;
            pos2.y = _templateAgent1Y;

            // 적군 배열 전달 (OnEpisodeBegin에서 guidance 타겟으로 사용)
            if (enemies != null)
            {
                if (pair.agent1 != null) pair.agent1.enemyShips = enemies;
            }

            // 에이전트 위치/회전 리셋
            ResetAgent(pair.agent1, pos1, rotation);

            // anchorObject: pos2 위치에 배치 (chase: 웹 초기 폭 설정)
            if (pair.anchorObject != null)
                pair.anchorObject.transform.position = pos2;

            // 상태 플래그
            pair.isDisarmed   = true;
            pair.isSplitting  = false;
            pair.deployStep   = envCtrl != null ? envCtrl.CurrentStep : 0;

            // 쌍 활성화
            SetPairActive(pi, true);

            // 웹 처리: chase 모드는 정지 트랩 없음(선박 사이 frozen web 제거)
            // 일반 모드는 물리 동기화 후 현재 위치에 고정
            bool isChaseMode = envCtrl != null && envCtrl.chaseTrainingMode;
            if (isChaseMode)
            {
                if (pair.webObject != null) pair.webObject.SetActive(false);
            }
            else
            {
                Physics.SyncTransforms();
                if (pair.webObject != null)
                {
                    var dw = pair.webObject.GetComponent<DynamicWeb>();
                    if (dw != null) dw.FreezeAtCurrentPositions();
                }
            }

            // MA-POCA 등록 → 이 시점에 OnEpisodeBegin 호출됨
            // OnEpisodeBegin chase 분기에서 ActivateSingleNet + assignedTargetIndex + guidance 설정
            if (agentGroup != null)
            {
                if (pair.agent1 != null) agentGroup.RegisterAgent(pair.agent1);
            }

            // OnEpisodeBegin 이후 확정: isLeftAgent (OnEpisodeBegin이 리셋 안 함)
            if (pair.agent1 != null) pair.agent1.isLeftAgent = true;

            // assignedTargetIndex 재확인 (OnEpisodeBegin 체이스 분기가 설정했을 것이나 안전하게 덮어씀)
            if (pair.agent1 != null) pair.agent1.assignedTargetIndex = 1;

            // SingleNet 재확인 (OnEpisodeBegin 분기가 호출했을 것이나 안전하게 덮어씀)
            if (pair.agent1 != null) pair.agent1.ActivateSingleNet();

            // One-Way Towing 앵커 할당
            if (pair.agent1 != null) pair.agent1.anchorTransform = pair.anchorObject?.transform;

            Debug.Log($"[SpawnChaseReadyPair] pi={pi} pos1={pos1:F1} pos2={pos2:F1} webCenter={webCenter:F1} guidanceSteps={guidanceSteps}");
        }

        public void ReturnPairToPool(int pairIndex, SimpleMultiAgentGroup agentGroup, bool delayWebDisable = true)
        {
            if (_pairPool == null || pairIndex < 0 || pairIndex >= _pairPool.Count)
                return;
            DefensePair pair = _pairPool[pairIndex];
            if (!pair.isActive) return;

            // MA-POCA 등록 해제 (Posthumous Credit 작동)
            if (agentGroup != null)
            {
                if (pair.agent1 != null) agentGroup.UnregisterAgent(pair.agent1);
            }

            // HIDDEN_POS로 이동 + isKinematic (재활용 가능 상태)
            SetPairActive(pairIndex, false);
            pair.isSplitting = false;
            pair.splitStartStep = -1;
            pair.assignedZoneIndex = -1;
            pair.launchAngleDeg = -1f;
            pair.deployStep = -1;

            // neutralized 해제 (재배치 시 다시 사용 가능하도록)
            if (pair.agent1 != null) { pair.agent1.SetNeutralized(false); pair.agent1.assignedTargetIndex = -1; }

            _deployedPairCount = Mathf.Max(0, _deployedPairCount - 1);

            // Web: 0.5초 뒤 비활성화 (포획 연출용)
            if (delayWebDisable && pair.webObject != null)
            {
                // SetPairActive(false)에서 이미 webObject.SetActive(false) 했으므로
                // delayWebDisable일 때는 잠시 다시 켜고 0.5초 후 끔
                pair.webObject.SetActive(true);
                var webObj = pair.webObject; // 클로저 캡처용
                StartCoroutine(DelayedWebDisable(webObj, 0.5f));
            }

            Debug.Log($"[LaunchZoneManager] Pair {pairIndex} 풀 반환 (재활용 가능)");
        }

        /// <summary>
        /// Web을 delay 초 후 비활성화하는 코루틴
        /// </summary>
        private System.Collections.IEnumerator DelayedWebDisable(GameObject webObj, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (webObj != null) webObj.SetActive(false);
        }

        /// <summary>
        /// 에이전트 엔진 정지 (물리/파도/부력은 유지)
        /// SetNeutralized → OnActionReceived에서 엔진 구동 차단
        /// </summary>
        private void FreezeAgent(DefenseAgent agent)
        {
            if (agent == null) return;

            // 현재 속도만 제거 (관성 제거), 이후 파도/부력은 자연스럽게 적용
            if (agent.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// DefenseAgent가 포함된 쌍의 인덱스 반환 (-1이면 없음)
        /// </summary>
        public int FindPairIndex(DefenseAgent agent)
        {
            if (_pairPool == null || agent == null) return -1;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] == null) continue;
                if (_pairPool[i].agent1 == agent)
                    return i;
            }
            return -1;
        }

        public int FindPairIndexByGameObject(GameObject agentObj)
        {
            if (_pairPool == null || agentObj == null) return -1;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] == null) continue;
                if (_pairPool[i].agent1 != null && _pairPool[i].agent1.gameObject == agentObj)
                    return i;
            }
            return -1;
        }

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

            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive)
                {
                    if (_pairPool[i].agent1 != null)
                        _pairPool[i].agent1.enemyShips = enemies;
                }
            }
        }

        /// <summary>
        /// 모선 후미 기준 진수구역 생성
        /// maxPairCount에 따라 후미 좌/중/우 구역을 자동 배치
        /// </summary>
        private void GenerateLaunchZones()
        {
            Vector3 motherPos = motherShip != null ? motherShip.transform.position : Vector3.zero;

            // === 1순위: SpawnZoneConfig 배열 (인스펙터 직접 지정) ===
            if (spawnZones != null && spawnZones.Length > 0)
            {
                var validZones = new System.Collections.Generic.List<LaunchZone>();
                foreach (var sz in spawnZones)
                {
                    if (sz == null || sz.position == null) continue;
                    Vector3 offset = sz.position.position - motherPos;
                    offset.y = 0f;
                    float angleDeg = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                    if (angleDeg < 0f) angleDeg += 360f;
                    float distance = Mathf.Max(offset.magnitude, 1f);
                    validZones.Add(new LaunchZone
                    {
                        angleDeg    = angleDeg,
                        distance    = distance,
                        angleJitter = 0f,
                        worldPos    = sz.position.position,
                        hasWorldPos = true
                    });
                }
                launchZones = validZones.ToArray();
                _zoneLastDeployStep = new int[launchZones.Length];
                ResetZoneCooldowns();
                return;
            }

            // === 2순위: 레거시 zoneTransforms ===
            if (zoneTransforms != null && zoneTransforms.Length > 0)
            {
                var validZones = new System.Collections.Generic.List<LaunchZone>();
                foreach (var t in zoneTransforms)
                {
                    if (t == null) continue;
                    Vector3 offset = t.position - motherPos;
                    offset.y = 0f;
                    float angleDeg = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                    if (angleDeg < 0f) angleDeg += 360f;
                    float distance = Mathf.Max(offset.magnitude, 1f);
                    validZones.Add(new LaunchZone
                    {
                        angleDeg    = angleDeg,
                        distance    = distance,
                        angleJitter = angleJitter,
                        worldPos    = t.position,
                        hasWorldPos = true
                    });
                }
                launchZones = validZones.ToArray();
                _zoneLastDeployStep = new int[launchZones.Length];
                ResetZoneCooldowns();
                return;
            }

            // === 3순위: Fallback — 모선 후미 자동 생성 ===
            float rearAngleDeg = GetMotherShipRearAngleDeg();
            int count = Mathf.Clamp(maxPairCount, 1, 10);
            launchZones = new LaunchZone[count];

            if (count == 1)
            {
                launchZones[0] = new LaunchZone { angleDeg = rearAngleDeg, distance = rearDistance, angleJitter = angleJitter };
            }
            else if (count == 2)
            {
                launchZones[0] = new LaunchZone { angleDeg = rearAngleDeg - rearSpread, distance = rearDistance, angleJitter = angleJitter };
                launchZones[1] = new LaunchZone { angleDeg = rearAngleDeg + rearSpread, distance = rearDistance, angleJitter = angleJitter };
            }
            else
            {
                launchZones[0] = new LaunchZone { angleDeg = rearAngleDeg - rearSpread, distance = rearDistance, angleJitter = angleJitter };
                launchZones[1] = new LaunchZone { angleDeg = rearAngleDeg,              distance = rearDistance, angleJitter = angleJitter };
                launchZones[2] = new LaunchZone { angleDeg = rearAngleDeg + rearSpread, distance = rearDistance, angleJitter = angleJitter };
            }

            _zoneLastDeployStep = new int[count];
            ResetZoneCooldowns();
        }

        /// <summary>
        /// 모선의 현재 진행 방향 반대(후미) 각도를 월드 기준으로 반환 (도)
        /// </summary>
        private float GetMotherShipRearAngleDeg()
        {
            if (motherShip != null)
            {
                float heading = motherShip.transform.eulerAngles.y; // 모선 선수 방향 (0=North)
                return (heading + 180f) % 360f;                     // 후미 방향
            }
            return 180f; // fallback: 정남쪽
        }

        /// <summary>
        /// 진수구역 쿨다운 전체 리셋 (에피소드 시작 시 호출)
        /// </summary>
        public void ResetZoneCooldowns()
        {
            if (_zoneLastDeployStep == null) return;
            for (int i = 0; i < _zoneLastDeployStep.Length; i++)
                _zoneLastDeployStep[i] = -9999;
        }

        /// <summary>
        /// 해당 진수구역이 쿨다운 중인지 확인
        /// </summary>
        public bool IsZoneOnCooldown(int zoneIdx, int currentStep)
        {
            if (_zoneLastDeployStep == null || zoneIdx < 0 || zoneIdx >= _zoneLastDeployStep.Length)
                return false;
            return (currentStep - _zoneLastDeployStep[zoneIdx]) < zoneDeployCooldown;
        }

        /// <summary>
        /// 특정 진수구역에 현재 배치된 활성 쌍 수 반환
        /// </summary>
        private int GetActivePairsInZone(int zoneIdx)
        {
            if (_pairPool == null) return 0;
            int count = 0;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                DefensePair p = _pairPool[i];
                if (p == null || !p.isActive) continue;
                if (p.assignedZoneIndex == zoneIdx) count++;
            }
            return count;
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
                float dist = rearDistance;
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

            // Debug.Log($"[LaunchZoneManager] {launchZones.Length}개 진수구역 원통 생성 완료");
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
                float dist = rearDistance;
                float rad = angle * Mathf.Deg2Rad;
                Vector3 point = center + new Vector3(Mathf.Sin(rad) * dist, 0f, Mathf.Cos(rad) * dist);
                if (s > 0) Gizmos.DrawLine(prevPoint, point);
                prevPoint = point;
            }

            // 진수구역 표시 (런타임 배열 또는 maxPairCount 기반)
            int count = (launchZones != null && launchZones.Length > 0) ? launchZones.Length : maxPairCount;
            float angleStep = 360f / count;

            for (int i = 0; i < count; i++)
            {
                float angleDeg = (launchZones != null && i < launchZones.Length)
                    ? launchZones[i].angleDeg
                    : i * angleStep;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector3 zoneDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                float dist = rearDistance;
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
