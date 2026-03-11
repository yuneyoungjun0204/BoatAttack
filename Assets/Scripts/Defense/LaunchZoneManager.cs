using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using Unity.Barracuda;
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
        [HideInInspector] public int deployStep = -1; // 배치 시점 (FixedUpdate 스텝)

        // 좌/우 교차 체크용 (배치 시 기록)
        [HideInInspector] public bool agent1StartsOnLeft = true; // agent1이 lateralDir 기준 왼쪽인지
        [HideInInspector] public Vector3 deployLateralDir = Vector3.right; // 배치 시 횡방향 (좌/우 판별 축)
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
        [Range(1, 40)]
        public int zoneCount = 10;

        [Tooltip("쌍의 좌우 펼침 각도 (±도)")]
        public float pairSpreadDeg = 1.7f;

        [Tooltip("에피소드마다 방위각 jitter (±도, 과적합 방지)")]
        public float angleJitter = 10f;

        [HideInInspector]
        public LaunchZone[] launchZones;

        [Header("Ally Pool")]
        [Tooltip("최대 아군 쌍 수 (풀 크기)")]
        [Range(1, 20)]
        public int maxPairCount = 15;

        [Tooltip("에피소드 시작 시 자동 배치 쌍 수 (0=버튼으로만 배치)")]
        [Range(0, 20)]
        public int activePairCount = 0;

        [Tooltip("초기 출동 쌍 수 (나머지는 예비로 대기, 0=activePairCount 전부 출동)")]
        [Range(0, 20)]
        public int initialDeployCount = 0;

        [Tooltip("같은 진수구역에서 연속 출동 최소 간격 (스텝)")]
        public int zoneDeployCooldown = 100;

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

        // 진수구역별 마지막 출동 스텝 (쿨다운용)
        private int[] _zoneLastDeployStep;

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
                    // Debug.Log("[LaunchZoneManager] templatePair 없음 → 프리팹에서 자동 생성");
                    CreateTemplateFromPrefab();
                }
                else
                {
                    // Debug.LogError("[LaunchZoneManager] InitializeAllyPool: templatePair과 defenseBoatPrefab 모두 없습니다!");
                    return;
                }
            }

            if (templatePair == null || templatePair.agent1 == null || templatePair.agent2 == null)
            {
                // Debug.LogError("[LaunchZoneManager] InitializeAllyPool: 템플릿 생성 실패!");
                return;
            }

            // Y방향: 수면 위 여유를 두고 배치 (파도에 의해 수면이 0 이상일 수 있음)
            _templateAgent1Y = 1f;
            _templateAgent2Y = 1f;

            // 풀 빈 상태로 시작 (버튼 클릭 시 프리팹에서 직접 생성)
            _pairPool = new List<DefensePair>();

            // 템플릿 오브젝트 비활성화 (더 이상 사용 안 함, 충돌 방지)
            if (templatePair.agent1 != null) templatePair.agent1.gameObject.SetActive(false);
            if (templatePair.agent2 != null) templatePair.agent2.gameObject.SetActive(false);
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

            // Agent2
            GameObject agent2Obj = Instantiate(defenseBoatPrefab, poolParent);
            agent2Obj.name = "DefenseAgent2_template";
            agent2Obj.transform.position = HIDDEN_POS + Vector3.right * 50f;

            var da1 = agent1Obj.GetComponent<DefenseAgent>();
            var da2 = agent2Obj.GetComponent<DefenseAgent>();

            if (da1 == null || da2 == null)
            {
                // Debug.LogError("[LaunchZoneManager] defenseBoatPrefab에 DefenseAgent 컴포넌트가 없습니다!");
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

            // DynamicWeb 설정 (없으면 추가 — 그물 누락 절대 방지)
            var dw = webObj.GetComponent<DynamicWeb>();
            if (dw == null)
            {
                Debug.LogWarning("[CreateTemplateFromPrefab] DynamicWeb 컴포넌트 없음 → 추가");
                dw = webObj.AddComponent<DynamicWeb>();
            }
            dw.defenseShip1 = agent1Obj.transform;
            dw.defenseShip2 = agent2Obj.transform;
            if (envController != null)
                dw.envController = envController;

            var wd = webObj.GetComponent<WebCollisionDetector>();
            if (wd == null) wd = webObj.AddComponent<WebCollisionDetector>();
            if (envController != null) wd.envController = envController;

            // BufferSensor 크기 보정
            NormalizeBufferSensor(da1);
            NormalizeBufferSensor(da2);

            // templatePair 설정
            if (templatePair == null)
                templatePair = new DefensePair();
            templatePair.agent1 = da1;
            templatePair.agent2 = da2;
            templatePair.webObject = webObj;
            da1.useArrowKeys = true;
            da2.useArrowKeys = false;

            // DefenseEnvController에도 참조 설정
            if (envController != null)
            {
                envController.defenseAgent1 = da1;
                envController.defenseAgent2 = da2;
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

            // Agent2 복제
            GameObject agent2Clone = Instantiate(templatePair.agent2.gameObject, poolParent);
            agent2Clone.name = $"DefenseAgent2_pair{index}";
            pair.agent2 = agent2Clone.GetComponent<DefenseAgent>();

            // Web 복제 (템플릿 web이 없으면 새로 생성 — 그물 누락 절대 방지)
            if (templatePair.webObject != null)
            {
                GameObject webClone = Instantiate(templatePair.webObject, poolParent);
                webClone.name = $"Web_pair{index}";

                // Instantiate 시 복제된 WebVisual 자식 제거 (Start()에서 새로 생성됨)
                Transform orphanedVisual = webClone.transform.Find("WebVisual");
                if (orphanedVisual != null)
                    Object.Destroy(orphanedVisual.gameObject);

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
            EnsureEngineRB(agent2Clone);

            // BufferSensor 크기 보정 (inactive 클론은 Awake 미호출이므로 여기서 강제)
            NormalizeBufferSensor(pair.agent1);
            NormalizeBufferSensor(pair.agent2);

            // 파트너/Web 교차 참조 설정
            pair.agent1.partnerAgent = pair.agent2;
            pair.agent2.partnerAgent = pair.agent1;
            pair.agent1.webObject = pair.webObject;
            pair.agent2.webObject = pair.webObject;

            // Heuristic 키 분리: agent1=화살표, agent2=WASD
            pair.agent1.useArrowKeys = true;
            pair.agent2.useArrowKeys = false;

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

                // Stage4: OnEnable 전 InferenceOnly 사전 설정
                if (envController.IsCommanderStage() && envController.defenseOnnxModel != null)
                {
                    SetAgentInferenceOnly(pair.agent1, envController.defenseOnnxModel);
                    SetAgentInferenceOnly(pair.agent2, envController.defenseOnnxModel);
                }
            }

            // WebCollisionDetector/DynamicWeb 설정 (envController 유무와 무관하게 ship 참조는 반드시 설정)
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
                dynamicWeb.defenseShip1 = pair.agent1.transform;
                dynamicWeb.defenseShip2 = pair.agent2.transform;
                if (envController != null)
                    dynamicWeb.envController = envController;

                // 복제된 Web의 webAnchor가 원본 선박을 가리키므로 복제 선박의 자식으로 재할당
                RemapWebAnchor(dynamicWeb, templatePair, pair);
            }

            Debug.LogWarning($"[CreatePairClone] index={index}, a1={agent1Clone.name}, a2={agent2Clone.name}, " +
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
            buf.ObservableSize = 4;       // r, f, d, h
            buf.MaxNumObservables = 10;
            agent.enemyBufferSensor = buf;
        }

        /// <summary>
        /// DefenseAgent의 BehaviorType을 InferenceOnly로 설정하고 학습된 모델 장착
        /// </summary>
        private static void SetAgentInferenceOnly(DefenseAgent agent, NNModel model)
        {
            if (agent == null) return;
            var bp = agent.GetComponent<BehaviorParameters>();
            if (bp != null)
            {
                bp.BehaviorType = BehaviorType.InferenceOnly;
                bp.Model = model;
            }
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
                // Debug.LogWarning("[LaunchZoneManager] DeployPairs: 풀이 초기화되지 않았습니다.");
                return;
            }

            pairCount = Mathf.Min(pairCount, maxPairCount);
            _deployedPairCount = pairCount;

            // Debug.LogWarning($"[DeployPairs] 시작: type={formationType}, pairCount={pairCount}, " +
            //     $"activePairCount={activePairCount}, poolCount={_pairPool.Count}");

            // Debug.LogWarning($"[LaunchZoneManager] DeployPairs 시작: type={formationType}, " +
            //     $"pairCount={pairCount}, activePairCount={activePairCount}, " +
            //     $"maxPairCount={maxPairCount}, poolLen={_pairPool.Count}, " +
            //     $"motherPos={motherPos}, agentGroup={agentGroup != null}");

            // 0. 진수구역 쿨다운 리셋
            ResetZoneCooldowns();

            // 1. 모든 쌍 비활성화 + MA-POCA 해제
            // SetPairActive는 에이전트 GameObject를 SetActive(false)하지 않고 멀리 이동시킴
            for (int i = 0; i < _pairPool.Count; i++)
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

            // 3. 각 진수구역에 배정된 쌍 배치 (2단계: 위치 결정 → Voronoi 배정)
            GameObject[] enemies = envController != null ? envController.enemyShips : null;

            // 3-1. 1단계: 모든 쌍의 pairCenter + zoneDir 먼저 계산
            var spawnInfos = new List<(int poolIdx, Vector3 pairCenter, Vector3 zoneDir, float pairWidth, int zoneIdx)>();
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
                    int pi = GetOrCreateInactivePair();
                    if (pi < 0) break;

                    float lateralOffset = startOffset + j * lateralSpacing;
                    Vector3 pairCenter = motherPos + zoneDir * zoneDist + lateralDir * lateralOffset;

                    spawnInfos.Add((pi, pairCenter, zoneDir, pairWidth, zoneIdx));
                }
            }

            // 3-2. 2단계: 모든 pairCenter 확정 → Voronoi 배정 + 배치
            Vector3[] allCenters = new Vector3[spawnInfos.Count];
            for (int i = 0; i < spawnInfos.Count; i++)
                allCenters[i] = spawnInfos[i].pairCenter;

            for (int i = 0; i < spawnInfos.Count; i++)
            {
                var (pi, pairCenter, zoneDir, pairWidth, zoneIdx) = spawnInfos[i];
                DefensePair pair = _pairPool[pi];
                pair.assignedZoneIndex = zoneIdx;

                // 적군 배열 전달
                if (enemies != null)
                {
                    pair.agent1.enemyShips = enemies;
                    pair.agent2.enemyShips = enemies;
                }

                // Voronoi 배정: 이 쌍이 담당하는 적 중 가장 가까운 것
                int bestEnemyIdx = GetClosestResponsibleEnemyForSpawn(
                    pairCenter, allCenters, i, enemies);
                if (bestEnemyIdx >= 0)
                {
                    if (pair.agent1 != null) pair.agent1.assignedTargetIndex = bestEnemyIdx + 1;
                    if (pair.agent2 != null) pair.agent2.assignedTargetIndex = bestEnemyIdx + 1;
                }

                // Stage3/7: 모선 바깥 방향(zoneDir)으로 스폰, 기타: 배정된 적 방향
                Quaternion rot;
                if (envController != null && (envController.currentStage == TrainingStage.Stage3_Tactical || envController.currentStage == TrainingStage.Stage7_FleetManeuver))
                {
                    rot = Quaternion.LookRotation(zoneDir, Vector3.up);
                }
                else
                {
                    int targetIdx = bestEnemyIdx >= 0 ? bestEnemyIdx + 1 : -1;
                    rot = ComputeSpawnRotation(zoneDir, pairCenter, enemies, targetIdx);
                }

                // 2대 좌우 배치: 스폰 방향(rot)에 수직으로 배치 → 그물이 펴짐
                Vector3 spawnForward = rot * Vector3.forward;
                Vector3 webLateral = new Vector3(-spawnForward.z, 0f, spawnForward.x);

                Vector3 pos1 = pairCenter + webLateral * (-pairWidth * 0.5f);
                pos1.y = _templateAgent1Y;
                Vector3 pos2 = pairCenter + webLateral * (pairWidth * 0.5f);
                pos2.y = _templateAgent2Y;

                // 에이전트 위치/회전 설정
                ResetAgent(pair.agent1, pos1, rot);
                ResetAgent(pair.agent2, pos2, rot);

                // 좌/우 교차 체크용 초기값 기록
                pair.deployLateralDir = webLateral;
                float dot1 = Vector3.Dot(pos1 - pairCenter, webLateral);
                pair.agent1StartsOnLeft = dot1 < 0f;

                pair.deployStep = -1;

                // 활성화
                SetPairActive(pi, true);
                _totalPairsDeployed++;

                // MA-POCA 등록
                if (agentGroup != null)
                {
                    agentGroup.RegisterAgent(pair.agent1);
                    agentGroup.RegisterAgent(pair.agent2);
                }
            }

            _lastDeploymentInfo = $"{formationType}, pairs={pairCount}, zones={zoneAssignments.Count}";
            // Debug.Log($"[LaunchZoneManager] DeployPairs: {_lastDeploymentInfo}");
        }

        /// <summary>
        /// 포메이션에 따라 쌍을 진수구역에 배정
        /// </summary>
        private Dictionary<int, List<int>> AssignPairsToZones(
            FormationType formationType, float approachAngleDeg,
            float[] diversionaryAngles, int pairCount)
        {
            var assignments = new Dictionary<int, List<int>>();
            var usedZones = new HashSet<int>();

            bool isFleet = envController != null && envController.currentStage == TrainingStage.Stage7_FleetManeuver;

            if (formationType == FormationType.Diversionary && diversionaryAngles != null && diversionaryAngles.Length > 1)
            {
                // 양동: 각 방향별로 가까운 구역에 분산 배정
                int pairsPerDir = pairCount / diversionaryAngles.Length;
                int pairRemainder = pairCount % diversionaryAngles.Length;
                int pairIdx = 0;

                for (int d = 0; d < diversionaryAngles.Length; d++)
                {
                    float dirAngleDeg = diversionaryAngles[d] * Mathf.Rad2Deg;
                    int count = pairsPerDir + (d < pairRemainder ? 1 : 0);

                    // 이 방향 기준 가까운 순 정렬
                    var sorted = GetZonesSortedByAngle(dirAngleDeg);

                    // Stage7: 구역 재사용 허용 (1구역에 여러 쌍)
                    // 기타: 1구역 1쌍
                    int zoneSlot = 0;
                    for (int j = 0; j < count && pairIdx < pairCount; j++)
                    {
                        bool found = false;
                        while (zoneSlot < sorted.Count)
                        {
                            int zoneIdx = sorted[zoneSlot];
                            float angleDiff = Mathf.Abs(Mathf.DeltaAngle(dirAngleDeg, launchZones[zoneIdx].angleDeg));
                            if (angleDiff > 90f) break;

                            if (!isFleet && usedZones.Contains(zoneIdx))
                            {
                                zoneSlot++;
                                continue;
                            }
                            usedZones.Add(zoneIdx);
                            if (!assignments.ContainsKey(zoneIdx))
                                assignments[zoneIdx] = new List<int>();
                            assignments[zoneIdx].Add(pairIdx);
                            pairIdx++;
                            if (!isFleet) zoneSlot++; // 기존: 다음 구역으로
                            found = true;
                            break;
                        }
                        if (!found) break;
                    }
                }
            }
            else
            {
                // Concentrated / Wave: 적 접근 방향 ±90° 이내 구역 사용
                var sorted = GetZonesSortedByAngle(approachAngleDeg);

                int pairIdx = 0;
                int zoneSlot = 0;
                while (pairIdx < pairCount && zoneSlot < sorted.Count)
                {
                    int zoneIdx = sorted[zoneSlot];
                    float angleDiff = Mathf.Abs(Mathf.DeltaAngle(approachAngleDeg, launchZones[zoneIdx].angleDeg));
                    if (angleDiff > 90f) break;

                    if (!assignments.ContainsKey(zoneIdx))
                        assignments[zoneIdx] = new List<int>();
                    assignments[zoneIdx].Add(pairIdx);
                    pairIdx++;

                    if (!isFleet)
                        zoneSlot++; // 기존: 1구역 1쌍 → 다음 구역
                    else if (assignments[zoneIdx].Count >= 3)
                        zoneSlot++; // Stage7: 1구역 최대 3쌍 → 다음 구역으로 넘어감
                }
            }

            return assignments;
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
                return diffA.CompareTo(diffB);
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

            // 2. 풀 상한 체크
            if (_pairPool.Count >= maxPairCount)
            {
                Debug.LogWarning($"[GetOrCreate] 풀 상한 도달: {_pairPool.Count}/{maxPairCount}");
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
                // "활성화": GameObject 활성화 (inactive 클론 대응) + kinematic 해제
                if (pair.agent1 != null)
                {
                    if (!pair.agent1.gameObject.activeSelf)
                        pair.agent1.gameObject.SetActive(true);
                    if (pair.agent1.TryGetComponent<Rigidbody>(out var rb1))
                        rb1.isKinematic = false;
                }
                if (pair.agent2 != null)
                {
                    if (!pair.agent2.gameObject.activeSelf)
                        pair.agent2.gameObject.SetActive(true);
                    if (pair.agent2.TryGetComponent<Rigidbody>(out var rb2))
                        rb2.isKinematic = false;
                }
            }

            // Web만 SetActive 토글 — 활성화 시 web 누락이면 즉시 복구
            if (pair.webObject == null && active)
            {
                Debug.LogError($"[SetPairActive] pair{index} 활성화 시 webObject가 null! 즉시 복구 시도");
                EnsurePairWebIntegrity(pair, index);
            }
            if (pair.webObject != null) pair.webObject.SetActive(active);
            pair.isActive = active;
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
                if (pair.agent2 != null) pair.agent2.webObject = pair.webObject;
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
            if (pair.agent2 != null && dynamicWeb.defenseShip2 != pair.agent2.transform)
                dynamicWeb.defenseShip2 = pair.agent2.transform;

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
            if (pair.agent2 != null && pair.agent2.webObject != pair.webObject)
                pair.agent2.webObject = pair.webObject;
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

        /// <summary>에피소드 동안 배치된 총 페어 수 반환</summary>
        public int GetTotalPairsDeployed() => _totalPairsDeployed;

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

        /// <summary>
        /// 쌍의 타겟 설정 (양쪽 에이전트에 동시 적용)
        /// targetEnemyIndex: -1=auto, 1+=1-indexed enemy
        /// </summary>
        public void SetPairTarget(int pairIndex, int targetEnemyIndex)
        {
            if (_pairPool == null || pairIndex < 0 || pairIndex >= _pairPool.Count)
                return;
            DefensePair pair = _pairPool[pairIndex];
            if (!pair.isActive) return;
            if (pair.agent1 != null) pair.agent1.assignedTargetIndex = targetEnemyIndex;
            if (pair.agent2 != null) pair.agent2.assignedTargetIndex = targetEnemyIndex;
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

            // 1. 스폰 위치 계산 (쿨다운 중인 구역은 다음 가까운 구역으로)
            int currentStep = envController != null ? envController.CurrentStep : 0;
            int zoneIdx;
            if (forceZoneIdx >= 0)
            {
                zoneIdx = forceZoneIdx;
            }
            else
            {
                // 가장 가까운 구역부터 탐색 (±90° 이내 + 쿨다운 아닌 구역)
                var sorted = GetZonesSortedByAngle(approachAngleDeg);
                zoneIdx = -1;
                foreach (int zi in sorted)
                {
                    float angleDiff = Mathf.Abs(Mathf.DeltaAngle(approachAngleDeg, launchZones[zi].angleDeg));
                    if (angleDiff > 90f) break; // 정렬 순서상 이후 전부 90° 초과
                    if (!IsZoneOnCooldown(zi, currentStep))
                    {
                        zoneIdx = zi;
                        break;
                    }
                }
                if (zoneIdx < 0) return false; // 적 방향 ±90° 내 사용 가능 구역 없음
            }
            if (zoneIdx >= launchZones.Length) zoneIdx = 0;
            LaunchZone zone = launchZones[zoneIdx];

            float zoneAngleDeg = zone.angleDeg + Random.Range(-zone.angleJitter, zone.angleJitter);
            float zoneAngleRad = zoneAngleDeg * Mathf.Deg2Rad;
            Vector3 zoneDir = new Vector3(Mathf.Sin(zoneAngleRad), 0f, Mathf.Cos(zoneAngleRad));
            float zoneDist = GetEllipseDistance(zoneAngleDeg);

            float pairSpacing = 10f; // 쌍 내 2선박 간격 (m)
            Vector3 pairCenter = motherPos + zoneDir * zoneDist;

            // 2. 기존 비활성 쌍 재사용 또는 프리팹에서 새로 생성
            int pairIdx;
            DefensePair pair;
            GameObject[] enemies = envController != null ? envController.enemyShips : null;

            // 임시로 zoneDir 기본 회전 사용 (후에 적 방향으로 보정)
            Quaternion rot = Quaternion.LookRotation(zoneDir, Vector3.up);

            // 임시 위치 (회전 계산 후 재설정)
            Vector3 tempLateral = new Vector3(zoneDir.z, 0f, -zoneDir.x);
            Vector3 pos1 = pairCenter + tempLateral * (-pairSpacing * 0.5f);
            pos1.y = _templateAgent1Y;
            Vector3 pos2 = pairCenter + tempLateral * (pairSpacing * 0.5f);
            pos2.y = _templateAgent2Y;

            int existingInactive = FindInactivePairIndex();
            if (existingInactive >= 0)
            {
                pairIdx = existingInactive;
                pair = _pairPool[pairIdx];

                // 비활성 쌍 재사용 시 그물 무결성 검증 — 누락 시 즉시 복구
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
                pair = SpawnPairFromPrefab(pairIdx, pos1, pos2, rot);
                if (pair == null) return false;
                _pairPool.Add(pair);
            }

            pair.assignedZoneIndex = zoneIdx;
            pair.deployStep = envController != null ? envController.CurrentStep : 0;

            // 3. 적군 참조 설정
            if (enemies != null)
            {
                pair.agent1.enemyShips = enemies;
                pair.agent2.enemyShips = enemies;
            }

            // 미배정 적 Greedy 배정: 기존 배정 현황 수집 후 가장 가까운 미배정 적 선택
            var singleAssigned = new HashSet<int>();
            if (_pairPool != null)
            {
                for (int si = 0; si < _pairPool.Count; si++)
                {
                    if (!_pairPool[si].isActive || _pairPool[si].agent1 == null) continue;
                    int existIdx = _pairPool[si].agent1.assignedTargetIndex;
                    if (existIdx > 0) singleAssigned.Add(existIdx - 1);
                }
            }
            int bestEnemy = FindClosestUnassignedEnemy(
                pairCenter, enemies, singleAssigned,
                zoneDir, motherPos, filterByDirection: true);
            if (bestEnemy < 0)
                bestEnemy = FindClosestUnassignedEnemy(pairCenter, enemies, singleAssigned);
            if (bestEnemy >= 0)
            {
                if (pair.agent1 != null) pair.agent1.assignedTargetIndex = bestEnemy + 1;
                if (pair.agent2 != null) pair.agent2.assignedTargetIndex = bestEnemy + 1;
            }

            // Stage3: 모선 바깥 방향(zoneDir)으로 스폰, 기타: 배정된 적 방향
            if (envController != null && envController.currentStage == TrainingStage.Stage3_Tactical)
            {
                rot = Quaternion.LookRotation(zoneDir, Vector3.up);
            }
            else
            {
                int targetIdx = bestEnemy >= 0 ? bestEnemy + 1 : -1;
                rot = ComputeSpawnRotation(zoneDir, pairCenter, enemies, targetIdx);
            }

            // 스폰 방향에 수직으로 agent1/2 배치 → 그물이 펴짐
            Vector3 spawnFwd = rot * Vector3.forward;
            Vector3 lateralDir = new Vector3(-spawnFwd.z, 0f, spawnFwd.x);
            pos1 = pairCenter + lateralDir * (-pairSpacing * 0.5f);
            pos1.y = _templateAgent1Y;
            pos2 = pairCenter + lateralDir * (pairSpacing * 0.5f);
            pos2.y = _templateAgent2Y;

            ResetAgent(pair.agent1, pos1, rot);
            ResetAgent(pair.agent2, pos2, rot);

            // 좌/우 교차 체크용 초기값 기록 (실제 agent1→agent2 방향)
            pair.deployLateralDir = lateralDir;
            float dotSingle = Vector3.Dot(pos1 - pairCenter, lateralDir);
            pair.agent1StartsOnLeft = dotSingle < 0f;

            // 4. 활성화
            SetPairActive(pairIdx, true);
            _totalPairsDeployed++;

            // 5. MA-POCA 등록
            if (agentGroup != null)
            {
                agentGroup.RegisterAgent(pair.agent1);
                agentGroup.RegisterAgent(pair.agent2);
            }

            _deployedPairCount++;

            // 해당 진수구역 쿨다운 기록
            if (_zoneLastDeployStep != null && zoneIdx >= 0 && zoneIdx < _zoneLastDeployStep.Length)
                _zoneLastDeployStep[zoneIdx] = currentStep;

            Debug.LogWarning($"[DeploySingle] pair={pairIdx}, zone={zoneIdx}, cooldown={zoneDeployCooldown}, " +
                $"a1={pair.agent1?.name} engine={pair.agent1?._engine != null} RB={pair.agent1?._engine?.RB != null}, " +
                $"a2={pair.agent2?.name} engine={pair.agent2?._engine != null} RB={pair.agent2?._engine?.RB != null}");
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
        private DefensePair SpawnPairFromPrefab(int index, Vector3 pos1, Vector3 pos2, Quaternion rot)
        {
            Transform parent = transform;

            // Agent1: 프리팹에서 pos1 위치에 직접 생성
            GameObject a1Obj = Instantiate(defenseBoatPrefab, pos1, rot, parent);
            a1Obj.name = $"DefenseAgent1_pair{index}";
            var a1 = a1Obj.GetComponent<DefenseAgent>();

            // Agent2: 프리팹에서 pos2 위치에 직접 생성
            GameObject a2Obj = Instantiate(defenseBoatPrefab, pos2, rot, parent);
            a2Obj.name = $"DefenseAgent2_pair{index}";
            var a2 = a2Obj.GetComponent<DefenseAgent>();

            if (a1 == null || a2 == null)
            {
                Debug.LogError($"[SpawnPairFromPrefab] DefenseAgent 컴포넌트 없음! prefab={defenseBoatPrefab.name}");
                if (a1 == null) Destroy(a1Obj);
                if (a2 == null) Destroy(a2Obj);
                return null;
            }

            // Web 생성
            GameObject webObj = CreateWebObject(parent);
            webObj.name = $"Web_pair{index}";
            webObj.transform.position = (pos1 + pos2) * 0.5f;

            // 교차 참조 설정
            DefensePair pair = new DefensePair();
            pair.agent1 = a1;
            pair.agent2 = a2;
            pair.webObject = webObj;

            a1.partnerAgent = a2;
            a2.partnerAgent = a1;
            a1.webObject = webObj;
            a2.webObject = webObj;
            a1.useArrowKeys = true;
            a2.useArrowKeys = false;

            if (motherShip != null)
            {
                a1.motherShip = motherShip;
                a2.motherShip = motherShip;
            }
            if (envController != null)
            {
                a1.envController = envController;
                a2.envController = envController;
            }

            // Web 컴포넌트 설정 (envController 유무와 무관하게 ship 참조는 반드시 설정)
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
                dynamicWeb.defenseShip1 = a1.transform;
                dynamicWeb.defenseShip2 = a2.transform;
                dynamicWeb.webAnchor1 = null;
                dynamicWeb.webAnchor2 = null;
                if (envController != null)
                    dynamicWeb.envController = envController;
            }

            // Engine.RB 확인
            EnsureEngineRB(a1Obj);
            EnsureEngineRB(a2Obj);

            // BufferSensor 크기 보정 (프리팹 Inspector 불일치 방지)
            NormalizeBufferSensor(a1);
            NormalizeBufferSensor(a2);

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
                    if (_pairPool[i].agent2 != null)
                        agentGroup.UnregisterAgent(_pairPool[i].agent2);
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
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (!_pairPool[i].isActive) existingInactive++;
            }
            // 아직 생성되지 않은 쌍도 "사용 가능"에 포함
            int canCreate = Mathf.Max(0, maxPairCount - _pairPool.Count);
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

        /// <summary>
        /// 현재 활성 쌍이 총 예산(activePairCount) 기준으로 몇 개 예비인지
        /// </summary>
        public int GetReserveCount()
        {
            int active = GetActivePairCount();
            int budget = Mathf.Min(activePairCount, maxPairCount);
            return Mathf.Max(0, budget - active);
        }

        #region Commander 쿼리 메서드

        /// <summary>
        /// 특정 zone에 활성 쌍이 있는지 확인
        /// </summary>
        public bool IsZoneOccupied(int zoneIndex)
        {
            if (_pairPool == null) return false;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] != null && _pairPool[i].isActive && _pairPool[i].assignedZoneIndex == zoneIndex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 비어있는(활성 쌍이 없는) zone 수 반환
        /// </summary>
        public int GetAvailableZoneCount()
        {
            if (launchZones == null) return 0;
            int count = 0;
            for (int z = 0; z < launchZones.Length; z++)
            {
                if (!IsZoneOccupied(z))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// centerZone에서 가까운 순서로 빈 zone 인덱스 배열 반환 (Commander 배치용)
        /// 원형 거리 기준 정렬: zone 간 각도 차이가 작은 순
        /// </summary>
        public int[] GetNearestAvailableZones(int centerZone, int count)
        {
            if (launchZones == null || count <= 0) return new int[0];

            var candidates = new System.Collections.Generic.List<(int idx, float diff)>();
            for (int z = 0; z < launchZones.Length; z++)
            {
                if (IsZoneOccupied(z)) continue;
                float diff = Mathf.Abs(Mathf.DeltaAngle(
                    launchZones[centerZone >= 0 && centerZone < launchZones.Length ? centerZone : 0].angleDeg,
                    launchZones[z].angleDeg));
                candidates.Add((z, diff));
            }

            candidates.Sort((a, b) => a.diff.CompareTo(b.diff));

            int resultCount = Mathf.Min(count, candidates.Count);
            int[] result = new int[resultCount];
            for (int i = 0; i < resultCount; i++)
                result[i] = candidates[i].idx;
            return result;
        }

        /// <summary>
        /// 주어진 월드 위치에서 가장 가까운 zone과 그 거리 반환 (Commander 관측용)
        /// 모선 위치 기준으로 각 zone의 월드 좌표를 계산하여 비교
        /// </summary>
        public (int zoneIndex, float distance) GetClosestZoneToPosition(Vector3 position, Vector3 motherPos)
        {
            if (launchZones == null || launchZones.Length == 0)
                return (0, float.MaxValue);

            int bestIdx = 0;
            float bestDist = float.MaxValue;

            for (int z = 0; z < launchZones.Length; z++)
            {
                float angleRad = launchZones[z].angleDeg * Mathf.Deg2Rad;
                Vector3 zoneDir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                float zoneDist = GetEllipseDistance(launchZones[z].angleDeg);
                Vector3 zoneWorldPos = motherPos + zoneDir * zoneDist;

                float dist = Vector3.Distance(position, zoneWorldPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = z;
                }
            }

            return (bestIdx, bestDist);
        }

        /// <summary>
        /// 현재 풀에서 생성된 쌍 수 반환 (Commander 관측용, maxPairCount와 별개)
        /// </summary>
        public int GetCurrentPoolCount()
        {
            return _pairPool != null ? _pairPool.Count : 0;
        }

        #endregion

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
                if (pair.agent2 != null) agentGroup.UnregisterAgent(pair.agent2);
            }

            // 에이전트를 현재 위치에서 정지 (이동하지 않음)
            FreezeAgent(pair.agent1);
            FreezeAgent(pair.agent2);

            // ML-Agents 액션 처리 차단 (OnActionReceived에서 early return)
            if (pair.agent1 != null) pair.agent1.SetNeutralized(true);
            if (pair.agent2 != null) pair.agent2.SetNeutralized(true);

            // Web 비활성화 + 상태 플래그만 변경
            if (pair.webObject != null) pair.webObject.SetActive(false);
            pair.isActive = false;

            // Debug.Log($"[LaunchZoneManager] Pair {pairIndex} 무력화 (현재 위치 정지)");
        }

        /// <summary>
        /// 포획 성공 시 쌍을 풀로 반환 (재활용 가능).
        /// DisablePair와 달리 HIDDEN_POS로 이동 + neutralized 해제하여 나중에 재배치 가능.
        /// Web은 즉시 비활성화하지 않고, delayWebDisable=true이면 0.5초 후 비활성화.
        /// </summary>
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
                if (pair.agent2 != null) agentGroup.UnregisterAgent(pair.agent2);
            }

            // HIDDEN_POS로 이동 + isKinematic (재활용 가능 상태)
            SetPairActive(pairIndex, false);
            pair.assignedZoneIndex = -1;
            pair.deployStep = -1;

            // neutralized 해제 (재배치 시 다시 사용 가능하도록)
            if (pair.agent1 != null) pair.agent1.SetNeutralized(false);
            if (pair.agent2 != null) pair.agent2.SetNeutralized(false);
            if (pair.agent1 != null) pair.agent1.assignedTargetIndex = -1;
            if (pair.agent2 != null) pair.agent2.assignedTargetIndex = -1;

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
                if (_pairPool[i].agent1 == agent || _pairPool[i].agent2 == agent)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// GameObject로 쌍 인덱스 검색 (agent1 또는 agent2의 GameObject)
        /// </summary>
        public int FindPairIndexByGameObject(GameObject agentObj)
        {
            if (_pairPool == null || agentObj == null) return -1;
            for (int i = 0; i < _pairPool.Count; i++)
            {
                if (_pairPool[i] == null) continue;
                if ((_pairPool[i].agent1 != null && _pairPool[i].agent1.gameObject == agentObj) ||
                    (_pairPool[i].agent2 != null && _pairPool[i].agent2.gameObject == agentObj))
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

            for (int i = 0; i < _pairPool.Count; i++)
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
            // 진수구역별 쿨다운 배열 초기화
            _zoneLastDeployStep = new int[zoneCount];
            ResetZoneCooldowns();
            // Debug.Log($"[LaunchZoneManager] {zoneCount}개 진수구역 생성 (간격 {angleStep:F1}°)");
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
        /// 타원 극좌표 공식으로 해당 방위각의 거리 계산
        /// r(θ) = (a*b) / sqrt((b*cosθ)² + (a*sinθ)²)
        /// a = 전후(fore/aft) 반경, b = 좌우(beam) 반경
        /// θ=0°/180° → a (전후), θ=90°/270° → b (좌우)
        /// </summary>
        public float GetZoneAngleDeg(int zoneIndex)
        {
            if (launchZones == null || zoneIndex < 0 || zoneIndex >= launchZones.Length)
                return 0f;
            return launchZones[zoneIndex].angleDeg;
        }

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

        /// <summary>
        /// Voronoi 배정: 해당 쌍이 담당하는 적 중 가장 가까운 적의 인덱스를 반환.
        /// "담당" = 이 적에 대해 모든 활성 쌍 중 이 쌍의 Web 중심이 가장 가까운 경우.
        /// 담당 적이 없으면 -1 반환.
        /// </summary>
        /// <param name="pairIdx">쌍 인덱스 (-1이면 webCenter 직접 사용)</param>
        /// <param name="webCenter">pairIdx=-1일 때 사용할 Web 중심 위치</param>
        /// <param name="enemies">적군 배열</param>
        /// <returns>담당 적 중 가장 가까운 적의 인덱스 (0-based), 없으면 -1</returns>
        // Greedy 1:1 매칭 캐시 (매 스텝 1회만 계산)
        private int _lastMatchingStep = -1;
        private readonly System.Collections.Generic.Dictionary<int, int> _pairToEnemyAssignment
            = new System.Collections.Generic.Dictionary<int, int>();

        /// <summary>
        /// Greedy 1:1 매칭 기반 담당 적 반환.
        /// 비용 함수: interceptDist / max(alongDist, 1) × refDist
        /// alongDist ≤ 0 (뒤쪽)이면 직선거리 × 2 페널티.
        /// 매 스텝 첫 호출 시 전체 매칭 계산 후 캐시.
        /// </summary>
        public int GetClosestResponsibleEnemy(int pairIdx, Vector3 webCenter, GameObject[] enemies)
        {
            if (enemies == null || _pairPool == null) return -1;

            // 현재 스텝 확인 (envController에서 가져옴)
            int currentStep = envController != null ? envController.CurrentStep : -1;

            // 이번 스텝에 아직 매칭 안 했으면 전체 계산
            if (currentStep != _lastMatchingStep)
            {
                ComputeGreedyMatching(enemies);
                _lastMatchingStep = currentStep;
            }

            // 캐시에서 반환
            if (_pairToEnemyAssignment.TryGetValue(pairIdx, out int assignedEnemy))
                return assignedEnemy;

            return -1;
        }

        /// <summary>
        /// Greedy 1:1 매칭: 차단 비용이 낮은 (쌍, 적) 순으로 배정.
        /// 적 > 쌍이면 남은 미배정 적 중 가장 비용 낮은 적을 가장 여유있는 쌍에 추가 배정.
        /// </summary>
        private void ComputeGreedyMatching(GameObject[] enemies)
        {
            _pairToEnemyAssignment.Clear();

            const float interceptRefDist = 200f;
            const float behindPenaltyMul = 2f;

            // 활성 쌍 수집
            var activePairs = new System.Collections.Generic.List<int>();
            var webCenters = new System.Collections.Generic.Dictionary<int, Vector3>();
            for (int pi = 0; pi < _pairPool.Count; pi++)
            {
                var pair = _pairPool[pi];
                if (pair == null || !pair.isActive) continue;
                if (pair.agent1 == null || pair.agent2 == null) continue;
                activePairs.Add(pi);
                webCenters[pi] = (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f;
            }

            // 활성 적 수집
            var activeEnemies = new System.Collections.Generic.List<int>();
            for (int ei = 0; ei < enemies.Length; ei++)
            {
                if (enemies[ei] != null && enemies[ei].activeInHierarchy)
                    activeEnemies.Add(ei);
            }

            if (activePairs.Count == 0 || activeEnemies.Count == 0) return;

            // 모든 (쌍, 적) 비용 계산
            var costList = new System.Collections.Generic.List<(int pairIdx, int enemyIdx, float cost)>();
            foreach (int pi in activePairs)
            {
                Vector3 wc = webCenters[pi];
                foreach (int ei in activeEnemies)
                {
                    float cost = CalculateInterceptCost(wc, enemies[ei].transform, interceptRefDist, behindPenaltyMul);
                    costList.Add((pi, ei, cost));
                }
            }

            // 비용 오름차순 정렬
            costList.Sort((a, b) => a.cost.CompareTo(b.cost));

            // Greedy 매칭: 비용 낮은 순으로 1:1 배정
            var assignedPairs = new System.Collections.Generic.HashSet<int>();
            var assignedEnemies = new System.Collections.Generic.HashSet<int>();

            foreach (var (pi, ei, cost) in costList)
            {
                if (assignedPairs.Contains(pi) || assignedEnemies.Contains(ei)) continue;
                _pairToEnemyAssignment[pi] = ei;
                assignedPairs.Add(pi);
                assignedEnemies.Add(ei);

                // 모든 쌍이 배정되면 종료
                if (assignedPairs.Count >= activePairs.Count) break;
            }

            // 미배정 쌍이 있으면 (쌍 > 적): 남은 쌍에게 가장 비용 낮은 적 배정 (중복 허용)
            foreach (int pi in activePairs)
            {
                if (assignedPairs.Contains(pi)) continue;
                float bestCost = float.MaxValue;
                int bestEi = -1;
                foreach (int ei in activeEnemies)
                {
                    float cost = CalculateInterceptCost(webCenters[pi], enemies[ei].transform, interceptRefDist, behindPenaltyMul);
                    if (cost < bestCost) { bestCost = cost; bestEi = ei; }
                }
                if (bestEi >= 0)
                    _pairToEnemyAssignment[pi] = bestEi;
            }
        }

        /// <summary>
        /// 차단 비용 계산: interceptDist/alongDist 기반.
        /// 전방: interceptDist / max(alongDist,1) × refDist
        /// 후방(alongDist≤0): 직선거리 × behindMul
        /// </summary>
        private float CalculateInterceptCost(Vector3 webCenter, Transform enemy,
            float refDist, float behindMul)
        {
            Vector3 toWeb = webCenter - enemy.position;
            toWeb.y = 0f;
            Vector3 enemyDir = enemy.forward;
            enemyDir.y = 0f;

            float straightDist = toWeb.magnitude;

            if (enemyDir.sqrMagnitude < 0.001f)
                return straightDist; // forward 없으면 직선거리

            enemyDir.Normalize();
            float along = Vector3.Dot(toWeb, enemyDir);

            if (along <= 0f)
                return straightDist * behindMul; // 뒤쪽: 페널티

            Vector3 perp = toWeb - along * enemyDir;
            float interceptDist = perp.magnitude;

            // 비용 = 수직거리 / 남은 거리 × 기준거리
            return interceptDist / Mathf.Max(along, 1f) * refDist;
        }

        /// <summary>
        /// 스폰용 Voronoi: pairCenter 기준으로 담당 적 계산 (아직 활성화 전이라 pairCenters 배열 사용)
        /// </summary>
        public int GetClosestResponsibleEnemyForSpawn(Vector3 myCenter, Vector3[] allCenters, int myIndex, GameObject[] enemies)
        {
            if (enemies == null || allCenters == null) return -1;

            int bestIdx = -1;
            float bestDist = float.MaxValue;

            for (int ei = 0; ei < enemies.Length; ei++)
            {
                if (enemies[ei] == null || !enemies[ei].activeInHierarchy) continue;

                float myDist = Vector3.Distance(myCenter, enemies[ei].transform.position);

                // 다른 쌍 중심이 더 가까우면 담당 아님
                bool isResponsible = true;
                for (int pi = 0; pi < allCenters.Length; pi++)
                {
                    if (pi == myIndex) continue;
                    if (Vector3.Distance(allCenters[pi], enemies[ei].transform.position) < myDist)
                    {
                        isResponsible = false;
                        break;
                    }
                }

                if (!isResponsible) continue;

                if (myDist < bestDist)
                {
                    bestDist = myDist;
                    bestIdx = ei;
                }
            }

            return bestIdx;
        }

        /// <summary>
        /// 적군 방향을 직접 바라보는 스폰 회전 계산 (head-on 인터셉트).
        /// 적이 없으면 기본 zoneDir 사용.
        /// </summary>
        private Quaternion ComputeSpawnRotation(Vector3 zoneDir, Vector3 pairCenter,
            GameObject[] enemies, int assignedTargetIndex)
        {
            Vector3 targetDir = Vector3.zero;
            bool hasTarget = false;

            // 1. 배정된 타겟이 있으면 그 방향 사용 (1-indexed)
            if (assignedTargetIndex > 0 && enemies != null)
            {
                int idx = assignedTargetIndex - 1;
                if (idx < enemies.Length && enemies[idx] != null && enemies[idx].activeInHierarchy)
                {
                    targetDir = enemies[idx].transform.position - pairCenter;
                    targetDir.y = 0f;
                    hasTarget = targetDir.sqrMagnitude > 1f;
                }
            }

            // 2. 배정 타겟 없으면 가장 가까운 활성 적 사용
            if (!hasTarget && enemies != null)
            {
                float closestDist = float.MaxValue;
                for (int i = 0; i < enemies.Length; i++)
                {
                    if (enemies[i] == null || !enemies[i].activeInHierarchy) continue;
                    Vector3 toEnemy = enemies[i].transform.position - pairCenter;
                    toEnemy.y = 0f;
                    float dist = toEnemy.sqrMagnitude;
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        targetDir = toEnemy;
                        hasTarget = true;
                    }
                }
            }

            if (!hasTarget)
                return Quaternion.LookRotation(zoneDir, Vector3.up);

            // 적군 방향을 직접 바라봄 (클램프 없음 → head-on 인터셉트)
            targetDir.Normalize();
            return Quaternion.LookRotation(targetDir, Vector3.up);
        }

        /// <summary>
        /// 미배정 적 중 가장 가까운 적 인덱스 반환 (-1 = 없음)
        /// zoneDir이 주어지면 해당 방향 ±90° 이내의 적만 후보 (양동 대응)
        /// </summary>
        private int FindClosestUnassignedEnemy(Vector3 pairCenter, GameObject[] enemies,
            HashSet<int> assigned, Vector3 zoneDir = default, Vector3 motherPos = default, bool filterByDirection = false)
        {
            if (enemies == null) return -1;

            float closestDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < enemies.Length; i++)
            {
                if (assigned.Contains(i)) continue;
                if (enemies[i] == null || !enemies[i].activeInHierarchy) continue;

                // 방향 필터: 진수구역 방향과 적의 모선 기준 방향 비교
                if (filterByDirection)
                {
                    Vector3 enemyDir = enemies[i].transform.position - motherPos;
                    enemyDir.y = 0f;
                    if (enemyDir.sqrMagnitude > 1f)
                    {
                        float angle = Vector3.Angle(zoneDir, enemyDir.normalized);
                        if (angle > 90f) continue; // 이 진수구역 방향이 아닌 적은 제외
                    }
                }

                float dist = Vector3.Distance(pairCenter, enemies[i].transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    bestIdx = i;
                }
            }
            return bestIdx;
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

            // Debug.Log($"[LaunchZoneManager] {launchZones.Length}개 진수구역 원통 생성 완료");
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

            // Greedy 1:1 매칭 시각화 (Gizmos 버전은 제거, Debug.DrawLine 사용)
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

        /// <summary>
        /// Greedy 1:1 매칭 시각화 (Debug.DrawLine → Gizmos 불필요, Game/Scene 뷰 모두 표시)
        /// FixedUpdate에서 매 스텝 호출
        /// </summary>
        public void DrawMatchingDebugLines()
        {
            if (_pairPool == null || _pairToEnemyAssignment == null || _pairToEnemyAssignment.Count == 0) return;

            GameObject[] enemies = envController != null ? envController.enemyShips : null;
            if (enemies == null) return;

            Color[] pairColors = {
                Color.green, Color.cyan, Color.yellow,
                Color.magenta, new Color(1f, 0.5f, 0f), Color.white
            };

            float dt = Time.fixedDeltaTime * 2f; // 2프레임 유지

            foreach (var kvp in _pairToEnemyAssignment)
            {
                int pi = kvp.Key;
                int ei = kvp.Value;

                if (pi < 0 || pi >= _pairPool.Count) continue;
                var pair = _pairPool[pi];
                if (pair == null || !pair.isActive || pair.agent1 == null || pair.agent2 == null) continue;
                if (ei < 0 || ei >= enemies.Length || enemies[ei] == null || !enemies[ei].activeInHierarchy) continue;

                Vector3 webCenter = (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f;
                Vector3 enemyPos = enemies[ei].transform.position;
                Color c = pairColors[pi % pairColors.Length];

                // 매칭 라인 (Web 중심 → 적)
                Debug.DrawLine(webCenter, enemyPos, c, dt);

                // Web 중심 십자 표시
                float s = 3f;
                Debug.DrawLine(webCenter + Vector3.left * s, webCenter + Vector3.right * s, c, dt);
                Debug.DrawLine(webCenter + Vector3.forward * s, webCenter + Vector3.back * s, c, dt);

                // 적 위치 X 표시
                float ex = 5f;
                Color ce = new Color(c.r, c.g, c.b, 0.8f);
                Debug.DrawLine(enemyPos + new Vector3(-ex, 0, -ex), enemyPos + new Vector3(ex, 0, ex), ce, dt);
                Debug.DrawLine(enemyPos + new Vector3(-ex, 0, ex), enemyPos + new Vector3(ex, 0, -ex), ce, dt);
            }
        }

        #endregion
    }
}
