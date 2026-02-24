using UnityEngine;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 닷지 게임 스타일 선박 스포너.
    /// 원통형 비주얼에서 선박을 주기적으로 생성하는 스포너.
    /// 아군(Defense)과 적군(Attack) 2종류로 사용.
    /// Defense 모드(spawnAsPair): 2대 선박 + DynamicWeb 세트로 스폰.
    /// </summary>
    public class ShipSpawner : MonoBehaviour
    {
        public enum SpawnerType { Defense, Attack }

        [Header("=== Spawner Identity ===")]
        public SpawnerType spawnerType = SpawnerType.Attack;
        [Tooltip("스포너 고유 이름 (디버그용)")]
        public string spawnerName = "";

        [Header("=== Prefab ===")]
        [Tooltip("생성할 선박 프리팹")]
        public GameObject shipPrefab;

        [Header("=== Pool Settings ===")]
        [Tooltip("오브젝트 풀 크기 (쌍 모드에서는 쌍 수)")]
        public int poolSize = 10;

        [Header("=== Spawn Settings ===")]
        [Tooltip("자동 스폰 활성화")]
        public bool autoSpawn = true;
        [Tooltip("스폰 간격 (초)")]
        public float spawnInterval = 10f;
        [Tooltip("동시 활성 최대 수 (쌍 모드에서는 쌍 수)")]
        public int maxActiveShips = 5;
        [Tooltip("스포너 주변 스폰 반경 (미터)")]
        public float spawnRadius = 30f;
        [Tooltip("스폰 시 타겟 방향을 바라봄")]
        public bool faceTarget = true;
        [Tooltip("스폰 대상 (적군→모선, 아군→적 방향 등)")]
        public Transform spawnTarget;
        [Tooltip("true: 타겟(모선) 주변에 스폰 / false: 스포너 주변에 스폰")]
        public bool spawnAtTarget = false;

        [Header("=== Defense Pair Settings ===")]
        [Tooltip("true: 2대 선박 + Web 세트로 스폰 (방어 전용)")]
        public bool spawnAsPair = false;
        [Tooltip("쌍 내 좌우 간격 (미터)")]
        public float pairSpread = 20f;
        [Tooltip("Web 프리팹 (비어있으면 자동 생성)")]
        public GameObject webPrefab;

        [Header("=== Visual (Cylinder) ===")]
        [Tooltip("원통 높이")]
        public float cylinderHeight = 12f;
        [Tooltip("원통 반경")]
        public float cylinderRadius = 6f;
        [Tooltip("스포너 색상 (비워두면 타입별 자동)")]
        public Color spawnerColor = Color.clear;
        [Tooltip("스폰 시 펄스 애니메이션")]
        public bool pulseOnSpawn = true;

        // ── Pool (단일 모드) ──
        private GameObject[] _pool;
        private bool[] _isActive;
        private int _activeCount;
        private float _spawnTimer;
        private bool _initialized;

        // ── Pool (쌍 모드 추가) ──
        private GameObject[] _ship2Pool;
        private GameObject[] _webPool;

        // ── Visual ──
        private GameObject _cylinderVisual;
        private MeshRenderer _cylinderRenderer;
        private MaterialPropertyBlock _propBlock;
        private float _pulseTimer;
        private static readonly int ColorID = Shader.PropertyToID("_Color");

        // ── Callbacks ──
        /// <summary>선박/쌍 스폰 시 호출. (spawnedShipOrPairRoot, poolIndex)</summary>
        public System.Action<GameObject, int> onShipSpawned;
        /// <summary>선박/쌍 회수 시 호출. (recalledShipOrPairRoot, poolIndex)</summary>
        public System.Action<GameObject, int> onShipRecalled;

        #region Public Properties

        public int ActiveCount => _activeCount;
        public int AvailableCount => poolSize - _activeCount;
        public int PoolSize => _pool != null ? _pool.Length : 0;
        public bool IsInitialized => _initialized;

        /// <summary>쌍 모드에서 특정 인덱스의 Ship1 가져오기</summary>
        public GameObject GetShip1(int poolIndex)
        {
            if (_pool == null || poolIndex < 0 || poolIndex >= poolSize) return null;
            return _pool[poolIndex];
        }

        /// <summary>쌍 모드에서 특정 인덱스의 Ship2 가져오기</summary>
        public GameObject GetShip2(int poolIndex)
        {
            if (!spawnAsPair || _ship2Pool == null || poolIndex < 0 || poolIndex >= poolSize) return null;
            return _ship2Pool[poolIndex];
        }

        /// <summary>쌍 모드에서 특정 인덱스의 Web 가져오기</summary>
        public GameObject GetWeb(int poolIndex)
        {
            if (!spawnAsPair || _webPool == null || poolIndex < 0 || poolIndex >= poolSize) return null;
            return _webPool[poolIndex];
        }

        #endregion

        #region Lifecycle

        void Start()
        {
            if (!_initialized)
                Initialize();
        }

        void FixedUpdate()
        {
            if (!_initialized || !autoSpawn) return;
            if (_activeCount >= maxActiveShips) return;

            _spawnTimer += Time.fixedDeltaTime;
            if (_spawnTimer >= spawnInterval)
            {
                _spawnTimer = 0f;
                SpawnShip();
            }
        }

        void Update()
        {
            // 펄스 애니메이션
            if (_pulseTimer > 0f)
            {
                _pulseTimer -= Time.deltaTime;
                float t = _pulseTimer / 0.3f; // 0.3초 펄스
                float scale = 1f + t * 0.3f;
                if (_cylinderVisual != null)
                    _cylinderVisual.transform.localScale = new Vector3(
                        cylinderRadius * 2f * scale,
                        cylinderHeight * 0.5f,
                        cylinderRadius * 2f * scale);
            }
            else if (_cylinderVisual != null)
            {
                _cylinderVisual.transform.localScale = new Vector3(
                    cylinderRadius * 2f,
                    cylinderHeight * 0.5f,
                    cylinderRadius * 2f);
            }
        }

        #endregion

        #region Initialization

        /// <summary>풀 초기화 + 원통 비주얼 생성</summary>
        public void Initialize()
        {
            if (_initialized) return;
            if (shipPrefab == null)
            {
                Debug.LogError($"[ShipSpawner] {spawnerName}: shipPrefab이 null입니다!");
                return;
            }

            if (spawnAsPair)
                CreatePairPool();
            else
                CreatePool();

            CreateCylinderVisual();
            _spawnTimer = 0f;
            _initialized = true;

            if (string.IsNullOrEmpty(spawnerName))
                spawnerName = $"{spawnerType}Spawner_{gameObject.GetInstanceID()}";

            Debug.Log($"[ShipSpawner] {spawnerName} 초기화 완료. pool={poolSize}, type={spawnerType}, pair={spawnAsPair}");
        }

        private void CreatePool()
        {
            _pool = new GameObject[poolSize];
            _isActive = new bool[poolSize];
            _activeCount = 0;

            for (int i = 0; i < poolSize; i++)
            {
                var ship = Instantiate(shipPrefab, transform);
                ship.name = $"{spawnerName}_{spawnerType}_{i}";
                ship.transform.position = GetHiddenPosition(i);
                ship.SetActive(false);
                _pool[i] = ship;
                _isActive[i] = false;
            }
        }

        /// <summary>쌍 모드 풀 생성: 각 슬롯에 Ship1 + Ship2 + Web</summary>
        private void CreatePairPool()
        {
            _pool = new GameObject[poolSize];       // ship1
            _ship2Pool = new GameObject[poolSize];  // ship2
            _webPool = new GameObject[poolSize];    // web
            _isActive = new bool[poolSize];
            _activeCount = 0;

            for (int i = 0; i < poolSize; i++)
            {
                // Ship 1
                var ship1 = Instantiate(shipPrefab, transform);
                ship1.name = $"{spawnerName}_Ship1_{i}";
                ship1.transform.position = GetHiddenPosition(i);
                ship1.SetActive(false);
                _pool[i] = ship1;

                // Ship 2
                var ship2 = Instantiate(shipPrefab, transform);
                ship2.name = $"{spawnerName}_Ship2_{i}";
                ship2.transform.position = GetHiddenPosition(i) + Vector3.right * 30f;
                ship2.SetActive(false);
                _ship2Pool[i] = ship2;

                // Web
                GameObject web;
                if (webPrefab != null)
                {
                    web = Instantiate(webPrefab, transform);
                }
                else
                {
                    web = CreateDefaultWeb();
                }
                web.name = $"{spawnerName}_Web_{i}";
                web.SetActive(false);
                _webPool[i] = web;

                // 교차 참조 설정
                SetupPairReferences(ship1, ship2, web);

                _isActive[i] = false;
            }
        }

        /// <summary>기본 Web 오브젝트 생성 (DynamicWeb + WebCollisionDetector)</summary>
        private GameObject CreateDefaultWeb()
        {
            var webObj = new GameObject("DynamicWeb");
            webObj.transform.SetParent(transform, false);
            webObj.tag = "Untagged";

            // Rigidbody (DynamicWeb RequireComponent)
            var rb = webObj.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            // BoxCollider (DynamicWeb RequireComponent)
            var col = webObj.AddComponent<BoxCollider>();
            col.isTrigger = true;

            // DynamicWeb
            var dw = webObj.AddComponent<DynamicWeb>();
            dw.webHeight = 5f;
            dw.webThickness = 0.5f;
            dw.webColor = new Color(0f, 1f, 1f, 0.3f);
            dw.isTrigger = true;
            dw.showVisual = true;

            // WebCollisionDetector
            webObj.AddComponent<WebCollisionDetector>();

            return webObj;
        }

        /// <summary>쌍의 교차 참조 설정 (partner, web, DynamicWeb)</summary>
        private void SetupPairReferences(GameObject ship1, GameObject ship2, GameObject web)
        {
            // DefenseAgent 파트너 + Web 참조
            var agent1 = ship1.GetComponent<DefenseAgent>();
            var agent2 = ship2.GetComponent<DefenseAgent>();

            if (agent1 != null && agent2 != null)
            {
                agent1.partnerAgent = agent2;
                agent2.partnerAgent = agent1;
                agent1.webObject = web;
                agent2.webObject = web;
            }

            // DynamicWeb 선박 참조
            var dynamicWeb = web.GetComponent<DynamicWeb>();
            if (dynamicWeb != null)
            {
                dynamicWeb.defenseShip1 = ship1.transform;
                dynamicWeb.defenseShip2 = ship2.transform;

                // webAnchor: 선박 자식 중 "WebAnchor" 찾기 (없으면 null → position fallback)
                dynamicWeb.webAnchor1 = FindChildRecursive(ship1.transform, "WebAnchor");
                dynamicWeb.webAnchor2 = FindChildRecursive(ship2.transform, "WebAnchor");
            }

            // 모선 참조
            if (spawnTarget != null)
            {
                if (agent1 != null) agent1.motherShip = spawnTarget.gameObject;
                if (agent2 != null) agent2.motherShip = spawnTarget.gameObject;
            }
        }

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

        private void CreateCylinderVisual()
        {
            _cylinderVisual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _cylinderVisual.name = $"SpawnerVisual_{spawnerType}";
            _cylinderVisual.transform.SetParent(transform, false);
            _cylinderVisual.transform.localPosition = new Vector3(0f, cylinderHeight * 0.5f, 0f);
            _cylinderVisual.transform.localScale = new Vector3(
                cylinderRadius * 2f, cylinderHeight * 0.5f, cylinderRadius * 2f);

            var col = _cylinderVisual.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var renderer = _cylinderVisual.GetComponent<MeshRenderer>();
            _cylinderRenderer = renderer;

            Color c = GetSpawnerColor();
            var mat = new Material(Shader.Find("Standard"));
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
            mat.color = c;
            renderer.material = mat;

            _propBlock = new MaterialPropertyBlock();
        }

        private Color GetSpawnerColor()
        {
            if (spawnerColor != Color.clear) return spawnerColor;
            return spawnerType == SpawnerType.Defense
                ? new Color(0.2f, 0.5f, 1f, 0.25f)
                : new Color(1f, 0.2f, 0.15f, 0.25f);
        }

        private Vector3 GetHiddenPosition(int index)
        {
            return new Vector3(transform.position.x + index * 50f, -500f, transform.position.z);
        }

        #endregion

        #region Spawn / Recall

        /// <summary>선박(또는 쌍) 1개 스폰. 성공 시 Ship1 반환, 실패 시 null.</summary>
        public GameObject SpawnShip()
        {
            if (!_initialized) return null;
            if (_activeCount >= maxActiveShips) return null;

            int index = -1;
            for (int i = 0; i < poolSize; i++)
            {
                if (!_isActive[i])
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) return null;

            return SpawnShipAt(index, GetSpawnPosition(), GetSpawnRotation());
        }

        /// <summary>특정 위치/방향으로 선박(또는 쌍) 스폰</summary>
        public GameObject SpawnShipAt(int poolIndex, Vector3 position, Quaternion rotation)
        {
            if (!_initialized || poolIndex < 0 || poolIndex >= poolSize) return null;
            if (_isActive[poolIndex]) return null;

            if (spawnAsPair)
                return SpawnPairAt(poolIndex, position, rotation);

            // === 단일 모드 ===
            GameObject ship = _pool[poolIndex];
            ship.transform.position = position;
            ship.transform.rotation = rotation;

            var rb = ship.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = position;
                rb.rotation = rotation;
            }

            ship.SetActive(true);
            if (rb != null) rb.Sleep();

            _isActive[poolIndex] = true;
            _activeCount++;

            if (pulseOnSpawn) _pulseTimer = 0.3f;
            onShipSpawned?.Invoke(ship, poolIndex);
            return ship;
        }

        /// <summary>쌍 스폰: Ship1 + Ship2 좌우 배치 + Web 활성화</summary>
        private GameObject SpawnPairAt(int poolIndex, Vector3 center, Quaternion rotation)
        {
            GameObject ship1 = _pool[poolIndex];
            GameObject ship2 = _ship2Pool[poolIndex];
            GameObject web = _webPool[poolIndex];

            // 좌우 오프셋 계산
            Vector3 right = rotation * Vector3.right;
            Vector3 pos1 = center - right * (pairSpread * 0.5f);
            Vector3 pos2 = center + right * (pairSpread * 0.5f);

            // Ship 1
            ship1.transform.position = pos1;
            ship1.transform.rotation = rotation;
            ResetShipPhysics(ship1, pos1, rotation);

            // Ship 2
            ship2.transform.position = pos2;
            ship2.transform.rotation = rotation;
            ResetShipPhysics(ship2, pos2, rotation);

            // 활성화
            ship1.SetActive(true);
            ship2.SetActive(true);
            web.SetActive(true);

            _isActive[poolIndex] = true;
            _activeCount++;

            if (pulseOnSpawn) _pulseTimer = 0.3f;
            onShipSpawned?.Invoke(ship1, poolIndex);

            Debug.Log($"[ShipSpawner] {spawnerName} 쌍 #{poolIndex} 스폰: pos1={pos1}, pos2={pos2}");
            return ship1;
        }

        private void ResetShipPhysics(GameObject ship, Vector3 pos, Quaternion rot)
        {
            var rb = ship.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = pos;
                rb.rotation = rot;
                rb.Sleep();
            }
        }

        /// <summary>특정 풀 인덱스의 선박(또는 쌍) 회수</summary>
        public void RecallShip(int poolIndex)
        {
            if (!_initialized || poolIndex < 0 || poolIndex >= poolSize) return;
            if (!_isActive[poolIndex]) return;

            // Ship 1
            GameObject ship = _pool[poolIndex];
            onShipRecalled?.Invoke(ship, poolIndex);
            DeactivateShip(ship, poolIndex);

            // Ship 2 + Web (쌍 모드)
            if (spawnAsPair)
            {
                if (_ship2Pool != null && _ship2Pool[poolIndex] != null)
                    DeactivateShip(_ship2Pool[poolIndex], poolIndex);

                if (_webPool != null && _webPool[poolIndex] != null)
                    _webPool[poolIndex].SetActive(false);
            }

            _isActive[poolIndex] = false;
            _activeCount--;
        }

        private void DeactivateShip(GameObject ship, int poolIndex)
        {
            var behaviours = ship.GetComponents<MonoBehaviour>();
            foreach (var b in behaviours)
                b.CancelInvoke();

            var rb = ship.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            ship.SetActive(false);
            ship.transform.position = GetHiddenPosition(poolIndex);
        }

        /// <summary>GameObject로 회수 (ship1 또는 ship2 모두 가능)</summary>
        public void RecallShip(GameObject ship)
        {
            int index = FindPoolIndex(ship);
            if (index >= 0) RecallShip(index);
        }

        /// <summary>모든 활성 선박/쌍 회수 (에피소드 리셋)</summary>
        public void RecallAll()
        {
            if (!_initialized) return;
            for (int i = 0; i < poolSize; i++)
            {
                if (_isActive[i]) RecallShip(i);
            }
            _spawnTimer = 0f;
        }

        #endregion

        #region Query

        /// <summary>풀 인덱스로 선박(ship1) 가져오기</summary>
        public GameObject GetShip(int poolIndex)
        {
            if (_pool == null || poolIndex < 0 || poolIndex >= poolSize) return null;
            return _pool[poolIndex];
        }

        /// <summary>선박의 풀 인덱스 검색 (ship1과 ship2 모두 검색, -1이면 없음)</summary>
        public int FindPoolIndex(GameObject ship)
        {
            if (_pool == null || ship == null) return -1;

            // ship1 풀 검색
            for (int i = 0; i < poolSize; i++)
            {
                if (_pool[i] == ship) return i;
            }

            // ship2 풀 검색 (쌍 모드)
            if (spawnAsPair && _ship2Pool != null)
            {
                for (int i = 0; i < poolSize; i++)
                {
                    if (_ship2Pool[i] == ship) return i;
                }
            }

            return -1;
        }

        /// <summary>특정 인덱스가 활성인지</summary>
        public bool IsActive(int poolIndex)
        {
            if (_isActive == null || poolIndex < 0 || poolIndex >= poolSize) return false;
            return _isActive[poolIndex];
        }

        /// <summary>활성 선박 리스트 반환 (쌍 모드: ship1만 반환)</summary>
        public List<GameObject> GetActiveShips()
        {
            var list = new List<GameObject>();
            if (_pool == null) return list;
            for (int i = 0; i < poolSize; i++)
            {
                if (_isActive[i] && _pool[i] != null)
                    list.Add(_pool[i]);
            }
            return list;
        }

        /// <summary>활성 선박 배열 (GC 최소화용)</summary>
        public void GetActiveShips(List<GameObject> result)
        {
            result.Clear();
            if (_pool == null) return;
            for (int i = 0; i < poolSize; i++)
            {
                if (_isActive[i] && _pool[i] != null)
                    result.Add(_pool[i]);
            }
        }

        /// <summary>활성 선박 전체 반환 (쌍 모드: ship1 + ship2 모두 포함)</summary>
        public List<GameObject> GetAllActiveShips()
        {
            var list = new List<GameObject>();
            if (_pool == null) return list;
            for (int i = 0; i < poolSize; i++)
            {
                if (!_isActive[i]) continue;
                if (_pool[i] != null) list.Add(_pool[i]);
                if (spawnAsPair && _ship2Pool != null && _ship2Pool[i] != null)
                    list.Add(_ship2Pool[i]);
            }
            return list;
        }

        #endregion

        #region Spawn Position Calculation

        private Vector3 SpawnOrigin =>
            (spawnAtTarget && spawnTarget != null) ? spawnTarget.position : transform.position;

        private Vector3 GetSpawnPosition()
        {
            Vector3 origin = SpawnOrigin;
            Vector2 rnd = Random.insideUnitCircle * spawnRadius;
            return new Vector3(origin.x + rnd.x, origin.y, origin.z + rnd.y);
        }

        private Quaternion GetSpawnRotation()
        {
            if (faceTarget && spawnTarget != null)
            {
                Vector3 dir;
                if (spawnAtTarget)
                    dir = spawnTarget.position - transform.position;
                else
                    dir = spawnTarget.position - transform.position;

                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    return Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
            return Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        }

        #endregion

        #region Gizmos

        void OnDrawGizmos()
        {
            Color c = GetSpawnerColor();
            c.a = 0.3f;
            Gizmos.color = c;

            Vector3 center = transform.position + Vector3.up * cylinderHeight * 0.5f;
            DrawWireCylinder(center, cylinderRadius, cylinderHeight);

            c.a = 0.15f;
            Gizmos.color = c;
            Vector3 spawnOrigin = (spawnAtTarget && spawnTarget != null) ? spawnTarget.position : transform.position;
            Gizmos.DrawWireSphere(spawnOrigin, spawnRadius);

            // 쌍 모드: 좌우 스폰 위치 표시
            if (spawnAsPair)
            {
                Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
                Vector3 right = transform.right;
                Gizmos.DrawWireSphere(spawnOrigin - right * pairSpread * 0.5f, 3f);
                Gizmos.DrawWireSphere(spawnOrigin + right * pairSpread * 0.5f, 3f);
                Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
                Gizmos.DrawLine(
                    spawnOrigin - right * pairSpread * 0.5f,
                    spawnOrigin + right * pairSpread * 0.5f);
            }

            if (faceTarget && spawnTarget != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(transform.position, spawnTarget.position);
            }

#if UNITY_EDITOR
            var style = new GUIStyle();
            style.normal.textColor = c;
            style.fontSize = 14;
            style.fontStyle = FontStyle.Bold;
            string label = string.IsNullOrEmpty(spawnerName)
                ? $"{spawnerType} Spawner"
                : spawnerName;
            if (spawnAsPair) label += " [PAIR]";
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * (cylinderHeight + 3f), label, style);
#endif
        }

        void OnDrawGizmosSelected()
        {
            Color c = GetSpawnerColor();
            c.a = 0.5f;
            Gizmos.color = c;

            Vector3 spawnOrigin = (spawnAtTarget && spawnTarget != null) ? spawnTarget.position : transform.position;
            Gizmos.DrawWireSphere(spawnOrigin, spawnRadius);

#if UNITY_EDITOR
            if (_initialized && _pool != null)
            {
                string info = spawnAsPair
                    ? $"Active Pairs: {_activeCount}/{poolSize}  Interval: {spawnInterval}s  Spread: {pairSpread}m"
                    : $"Active: {_activeCount}/{poolSize}  Interval: {spawnInterval}s";
                UnityEditor.Handles.Label(
                    transform.position + Vector3.up * (cylinderHeight + 6f), info);
            }
#endif
        }

        static void DrawWireCylinder(Vector3 center, float radius, float height)
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
