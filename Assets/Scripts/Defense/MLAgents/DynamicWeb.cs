using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 두 방어 선박 사이의 차단망.
    /// 시각화: catenary + 파도 + 부표 + 대각선 그물코 (SingleNetCapture 동일 스타일).
    /// 충돌: BoxCollider (isTrigger) — 적군 포획 + 아군 충돌 감지.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(BoxCollider))]
    public class DynamicWeb : MonoBehaviour
    {
        [Header("Target Ships")]
        public Transform defenseShip1;
        public Transform defenseShip2;

        [Header("Web Anchor Points")]
        public Transform webAnchor1;
        public Transform webAnchor2;

        [Header("Web Settings")]
        public float webHeight = 40f;
        public float webThickness = 0.5f;
        public Color webColor = new Color(1f, 0.85f, 0f, 0.9f);

        [Tooltip("충돌 판정용 두께 배율")]
        [Range(1f, 20f)]
        public float colliderThicknessMultiplier = 10f;

        [Tooltip("Trigger 충돌 사용")]
        public bool isTrigger = true;

        [Tooltip("Web 시각화 활성화")]
        public bool showVisual = true;

        [Header("Collision Reward")]
        public float defenseReward = 10f;

        [Header("Explosion Effect")]
        public GameObject explosionPrefab;
        [Range(5f, 50f)]
        public float explosionScale = 15f;

        [Header("Visual Material")]
        public Material webMaterial;

        [Header("Managers")]
        public DefenseEnvController envController;

        [Header("Convoy Mode")]
        [Tooltip("선박 간격이 이 거리(m) 미만이면 그물 비활성 (접힘 상태)")]
        public float minWebActiveDist = 20f;

        [Header("Convoy Bar")]
        public bool showConvoyBar = true;
        [Range(0.1f, 20f)]
        public float convoyBarThickness = 1.5f;
        public Color convoyBarColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        [Range(1f, 10f)]
        public float barSinkTime = 4f;

        [Header("Fishing Net Visual")]
        [Tooltip("세로줄 수 (선박 연결선 방향)")]
        [Range(4, 24)] public int netVerticalLines = 12;
        [Tooltip("가로줄 수 (수심 방향)")]
        [Range(3, 12)] public int netHorizontalLines = 8;
        [Tooltip("메인 밧줄 두께 (m)")]
        [Range(0.1f, 3f)] public float mainRopeWidth = 0.35f;
        [Tooltip("그물코 줄 두께 (m)")]
        [Range(0.02f, 0.5f)] public float netLineWidth = 0.08f;
        [Tooltip("그물 처짐 (catenary sag)")]
        [Range(0f, 15f)] public float netSag = 3f;
        [Tooltip("수면 흔들림 강도")]
        [Range(0f, 2f)] public float waveAmplitude = 0.3f;
        [Tooltip("수면 흔들림 속도")]
        [Range(0f, 3f)] public float waveSpeed = 0.8f;
        [Tooltip("그물 수면 위 높이 오프셋 (m). 수면에 가려지지 않도록 위로 올림")]
        [Range(0f, 5f)] public float netYOffset = 1.5f;
        [Tooltip("밧줄 색상")]
        public Color ropeColor = new Color(0.9f, 0.15f, 0.05f, 1f);
        [Tooltip("부표 색상")]
        public Color floatColor = new Color(1f, 0.15f, 0f, 1f);
        [Tooltip("부표 크기 (m)")]
        [Range(0.5f, 5f)] public float floatSize = 1.2f;
        [Tooltip("부표 표시")]
        public bool showFloats = true;
        [Tooltip("머티리얼 (null이면 자동 생성)")]
        public Material netMaterial;

        // ── 내부 상태 ──
        private BoxCollider _collider;
        private GameObject _visualObject;
        private MeshRenderer _renderer;
        private bool _wasWebOpen = true;
        private bool _initialized;

        // Stage10: 정지 트랩 고정
        private bool _isFrozen;
        private Vector3 _frozenPos1;
        private Vector3 _frozenPos2;
        private float _freezeTime = -1f;  // 고정된 시각 (3초 면역용)

        // Convoy 연결 막대
        private GameObject _convoyBarObject;
        private bool _convoyBarActive;

        // Fishing net visual
        private NetVisual _fishingNet;
        private bool _fishingNetMode = false;
        private int _netUpdateCounter;

        public bool IsFrozen => _isFrozen;
        /// <summary>고정 후 경과 시간(초). 고정 안 됐으면 float.MaxValue 반환</summary>
        public float FreezeElapsed => (_isFrozen && _freezeTime >= 0f) ? (Time.time - _freezeTime) : float.MaxValue;

        // ── NetVisual 내부 클래스 ──

        private class NetVisual
        {
            public GameObject container;
            public LineRenderer topRope;
            public LineRenderer bottomRope;
            public LineRenderer[] verticals;
            public LineRenderer[] horizontals;
            public LineRenderer[] diagonals;
            public GameObject[] floats;
            public Vector3[,] nodes;
        }

        // ── 생명주기 ──

        private void OnEnable()
        {
            _isFrozen = false;
            _freezeTime = -1f;
            if (!_initialized)
            {
                Initialize();
                return;
            }
            if (_fishingNetMode)
            {
                if (_fishingNet == null) BuildFishingNet();
            }
            else
                EnsureVisualExists();
        }

        private void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (_initialized) return;

            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            _collider = GetComponent<BoxCollider>();
            if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            _collider.isTrigger = isTrigger;

            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "WebVisual")
                {
                    child.gameObject.SetActive(false);
                    Destroy(child.gameObject);
                }
            }
            _visualObject = null;

            if (showVisual) CreateVisual();

            _initialized = true;
        }

        private void Update()
        {
            if (!_isFrozen && (defenseShip1 == null || defenseShip2 == null)) return;
            UpdateWebTransform();
            UpdateConvoyBar();
        }

        // ── 위치/크기 갱신 ──

        private void UpdateWebTransform()
        {
            Vector3 pos1 = _isFrozen ? _frozenPos1
                : ((webAnchor1 != null) ? webAnchor1.position : defenseShip1.position);
            Vector3 pos2 = _isFrozen ? _frozenPos2
                : ((webAnchor2 != null) ? webAnchor2.position : defenseShip2.position);

            Vector3 center = (pos1 + pos2) * 0.5f;
            transform.position = center;

            Vector3 dir = pos2 - pos1;
            dir.y = 0f;
            if (dir.magnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir);

            float distance = Vector3.Distance(pos1, pos2);

            if (_collider != null)
                _collider.size = new Vector3(webThickness * colliderThicknessMultiplier, webHeight, distance);

            bool webOpen = distance >= minWebActiveDist;
            if (webOpen != _wasWebOpen)
            {
                if (_collider != null) _collider.enabled = webOpen;
                if (_fishingNetMode)
                {
                    if (_fishingNet != null) _fishingNet.container.SetActive(webOpen);
                }
                else
                {
                    if (_visualObject != null) _visualObject.SetActive(webOpen);
                }
                _wasWebOpen = webOpen;
            }

            if (webOpen)
            {
                if (_fishingNetMode)
                {
                    _netUpdateCounter++;
                    if (_netUpdateCounter % 3 == 0)
                        UpdateFishingNet(pos1, pos2);
                }
                else if (_visualObject != null)
                    _visualObject.transform.localScale = new Vector3(webThickness, webHeight, distance);
            }
        }

        // ── Cube 비주얼 ──

        private void CreateVisual()
        {
            if (_visualObject != null) { _visualObject.SetActive(false); Destroy(_visualObject); }

            _visualObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _visualObject.name = "WebVisual";
            _visualObject.transform.SetParent(transform);
            _visualObject.transform.localPosition = Vector3.zero;
            _visualObject.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

            var col = _visualObject.GetComponent<BoxCollider>();
            if (col != null) Destroy(col);

            _renderer = _visualObject.GetComponent<MeshRenderer>();
            if (_renderer != null)
            {
                Material mat = webMaterial != null
                    ? new Material(webMaterial)
                    : CreateSimpleMaterial();
                if (mat != null)
                {
                    mat.color = webColor;
                    _renderer.material = mat;
                }
                _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
            }
        }

        public void EnsureVisualExists()
        {
            if (!showVisual) return;
            bool missing = _visualObject == null || !_visualObject.activeSelf;
            if (missing) CreateVisual();
        }

        private Material CreateSimpleMaterial()
        {
            Shader s = Shader.Find("Universal Render Pipeline/Unlit")
                    ?? Shader.Find("Sprites/Default")
                    ?? Shader.Find("Unlit/Color")
                    ?? Shader.Find("Standard");
            if (s == null) return null;
            var mat = new Material(s);
            SetMatColor(mat, webColor);
            return mat;
        }

        public void SetColor(Color color)
        {
            webColor = color;
            if (_fishingNetMode)
            {
                if (_fishingNet?.container == null) return;
                foreach (var lr in _fishingNet.container.GetComponentsInChildren<LineRenderer>())
                    if (lr != null && lr.material != null) SetMatColor(lr.material, color);
            }
            else if (_renderer != null && _renderer.material != null)
                SetMatColor(_renderer.material, color);
        }

        // ── 어부 그물 시각화 ──

        public void SetFishingNetVisual(bool enabled)
        {
            _fishingNetMode = enabled;
            if (enabled)
            {
                if (_visualObject != null) _visualObject.SetActive(false);
                if (_fishingNet == null) BuildFishingNet();
                if (_fishingNet != null) _fishingNet.container.SetActive(_wasWebOpen);
            }
            else
            {
                ClearFishingNet();
                if (_visualObject != null && _wasWebOpen) _visualObject.SetActive(true);
            }
        }

        private void BuildFishingNet()
        {
            ClearFishingNet();
            _fishingNet = CreateNet("FishingNet");
            _fishingNet.container.SetActive(_wasWebOpen);
        }

        private void ClearFishingNet()
        {
            if (_fishingNet?.container != null)
            {
                Destroy(_fishingNet.container);
            }
            _fishingNet = null;
        }

        private NetVisual CreateNet(string containerName)
        {
            var net = new NetVisual();
            net.container = new GameObject(containerName);
            net.container.transform.SetParent(transform, false);

            Material ropeMat = CreateNetMat(ropeColor);
            Material lineMat = CreateNetMat(ropeColor);

            net.topRope    = MakeLR(net.container, "TopRope",    ropeMat, mainRopeWidth);
            net.bottomRope = MakeLR(net.container, "BottomRope", ropeMat, mainRopeWidth * 0.8f);

            net.verticals = new LineRenderer[netVerticalLines];
            for (int i = 0; i < netVerticalLines; i++)
                net.verticals[i] = MakeLR(net.container, $"V{i}", lineMat, netLineWidth);

            net.horizontals = new LineRenderer[netHorizontalLines];
            for (int i = 0; i < netHorizontalLines; i++)
                net.horizontals[i] = MakeLR(net.container, $"H{i}", lineMat, netLineWidth * 0.7f);

            int diagCount = (netVerticalLines - 1) * (netHorizontalLines - 1) * 2;
            net.diagonals = new LineRenderer[diagCount];
            for (int i = 0; i < diagCount; i++)
                net.diagonals[i] = MakeLR(net.container, $"D{i}", lineMat, netLineWidth * 0.6f);

            if (showFloats) CreateFloats(net);

            net.nodes = new Vector3[netHorizontalLines, netVerticalLines];
            return net;
        }

        private void CreateFloats(NetVisual net)
        {
            int count = Mathf.Max(1, netVerticalLines / 2);
            net.floats = new GameObject[count];
            Material fm = CreateNetMat(floatColor);
            for (int i = 0; i < count; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(net.container.transform);
                go.transform.localScale = Vector3.one * floatSize;
                var r = go.GetComponent<MeshRenderer>(); if (r) r.material = fm;
                var c = go.GetComponent<Collider>();      if (c) c.enabled  = false;
                net.floats[i] = go;
            }
        }

        private void UpdateFishingNet(Vector3 pos1, Vector3 pos2)
        {
            if (_fishingNet == null) return;
            int nV = netVerticalLines, nH = netHorizontalLines;
            float baseY   = (pos1.y + pos2.y) * 0.5f + netYOffset;
            float time    = Time.time * waveSpeed;

            // vi 축: pos1→pos2 (선박 연결 방향)
            // hi 축: XZ 평면 내 수직 방향 → 그물이 수면에 평행하게 펼쳐짐
            Vector3 spanDir = (pos2 - pos1).normalized;
            Vector3 perpDir = new Vector3(spanDir.z, 0f, -spanDir.x);
            float halfWidth = webHeight * 0.5f;  // 수직 방향 반폭

            for (int vi = 0; vi < nV; vi++)
            {
                float t      = (float)vi / (nV - 1);
                Vector3 bpos = Vector3.Lerp(pos1, pos2, t);
                // 선박 연결 방향 catenary sag (중간 지점이 수면 아래 가장 낮음)
                float sagV  = netSag * 4f * t * (1f - t);
                float phase = vi * 0.7f + time;
                float wY    = Mathf.Sin(phase) * waveAmplitude;

                for (int hi = 0; hi < nH; hi++)
                {
                    float hT         = (float)hi / (nH - 1);
                    // 수직 방향 오프셋: -halfWidth ~ +halfWidth (XZ 평면)
                    float perpOffset = Mathf.Lerp(-halfWidth, halfWidth, hT);
                    // 폭 방향 catenary sag: 가장자리→중심 약간 아래
                    float sagH       = netSag * 4f * hT * (1f - hT) * 0.3f;
                    float pWave      = Mathf.Sin(phase * 1.3f + hi * 1.1f) * waveAmplitude * 0.3f;

                    _fishingNet.nodes[hi, vi] = new Vector3(
                        bpos.x + perpDir.x * perpOffset,
                        baseY - sagV - sagH + wY + pWave,
                        bpos.z + perpDir.z * perpOffset);
                }
            }

            // topRope/bottomRope: 수직 방향 양 끝 가장자리
            SetRope(_fishingNet.topRope,    nV, 0,      _fishingNet.nodes);
            SetRope(_fishingNet.bottomRope, nV, nH - 1, _fishingNet.nodes);

            for (int vi = 0; vi < nV; vi++)
            {
                var lr = _fishingNet.verticals[vi]; if (!lr) continue;
                lr.positionCount = nH;
                for (int hi = 0; hi < nH; hi++) lr.SetPosition(hi, _fishingNet.nodes[hi, vi]);
            }
            for (int hi = 0; hi < nH; hi++)
            {
                var lr = _fishingNet.horizontals[hi]; if (!lr) continue;
                lr.positionCount = nV;
                for (int vi = 0; vi < nV; vi++) lr.SetPosition(vi, _fishingNet.nodes[hi, vi]);
            }

            int di = 0;
            for (int hi = 0; hi < nH - 1; hi++)
                for (int vi = 0; vi < nV - 1; vi++)
                {
                    SetDiag(_fishingNet.diagonals, di++, _fishingNet.nodes[hi, vi],   _fishingNet.nodes[hi+1, vi+1]);
                    SetDiag(_fishingNet.diagonals, di++, _fishingNet.nodes[hi, vi+1], _fishingNet.nodes[hi+1, vi]);
                }

            // 부표: 양쪽 가장자리에 번갈아 배치
            if (_fishingNet.floats != null && showFloats)
                for (int i = 0; i < _fishingNet.floats.Length; i++)
                {
                    int vi     = i * 2;
                    int hiEdge = (i % 2 == 0) ? 0 : nH - 1;
                    if (vi < nV && _fishingNet.floats[i] != null)
                        _fishingNet.floats[i].transform.position =
                            _fishingNet.nodes[hiEdge, vi] + Vector3.up * floatSize * 0.5f;
                }
        }

        private void SetRope(LineRenderer lr, int nV, int hi, Vector3[,] nodes)
        {
            if (!lr) return;
            lr.positionCount = nV;
            for (int vi = 0; vi < nV; vi++) lr.SetPosition(vi, nodes[hi, vi]);
        }

        private void SetDiag(LineRenderer[] arr, int idx, Vector3 a, Vector3 b)
        {
            if (idx >= arr.Length || !arr[idx]) return;
            arr[idx].positionCount = 2;
            arr[idx].SetPosition(0, a);
            arr[idx].SetPosition(1, b);
        }

        private Material CreateNetMat(Color color)
        {
            if (netMaterial != null) { var m = new Material(netMaterial); SetMatColor(m, color); return m; }
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Sprites/Default")
                     ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Standard");
            if (sh == null) return new Material(Shader.Find("Hidden/InternalErrorShader"));
            var mat = new Material(sh);
            SetMatColor(mat, color);
            return mat;
        }

        private static void SetMatColor(Material mat, Color color)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))     mat.SetColor("_Color", color);
            mat.color = color;
        }

        private LineRenderer MakeLR(GameObject parent, string n, Material mat, float width)
        {
            var go = new GameObject(n);
            go.transform.SetParent(parent.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace     = true;
            lr.startWidth        = width;
            lr.endWidth          = width;
            lr.numCapVertices    = 3;
            lr.numCornerVertices = 3;
            lr.material          = mat;
            // Unlit 셰이더는 vertexColorMode를 지원하므로 startColor/endColor도 동기화
            lr.startColor        = mat.color;
            lr.endColor          = mat.color;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;
            return lr;
        }

        public void ResetCloneState()
        {
            _initialized = false;
            _isFrozen = false;
            _wasWebOpen = true;
            _convoyBarActive = false;
            _visualObject = null;
            _renderer = null;
            _collider = null;
            _convoyBarObject = null;
            _fishingNetMode = false;
            _fishingNet = null;
            _netUpdateCounter = 0;
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _renderer != null && _renderer.material != null)
                SetMatColor(_renderer.material, webColor);
        }

        // ── 고정 (정지 트랩) ──

        public void FreezeAtCurrentPositions()
        {
            _frozenPos1 = (webAnchor1 != null) ? webAnchor1.position
                : (defenseShip1 != null ? defenseShip1.position : transform.position);
            _frozenPos2 = (webAnchor2 != null) ? webAnchor2.position
                : (defenseShip2 != null ? defenseShip2.position : transform.position);
            _freezeTime = Time.time;
            _isFrozen = true;
        }

        public void UnfreezeWeb() { _isFrozen = false; }

        // ── Convoy Bar ──

        public void CreateConvoyBar()
        {
            if (!showConvoyBar) return;
            DestroyConvoyBar();

            _convoyBarObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _convoyBarObject.name = "ConvoyBar";
            _convoyBarObject.transform.SetParent(transform);

            var defaultCol = _convoyBarObject.GetComponent<Collider>();
            if (defaultCol != null) Destroy(defaultCol);

            var boxCol = _convoyBarObject.AddComponent<BoxCollider>();
            boxCol.isTrigger = true;
            boxCol.size = new Vector3(40f, 2f, 40f);

            var rend = _convoyBarObject.GetComponent<MeshRenderer>();
            if (rend != null)
            {
                Shader s = Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Sprites/Default")
                        ?? Shader.Find("Unlit/Color")
                        ?? Shader.Find("Standard");
                if (s != null)
                {
                    var mat = new Material(s);
                    SetMatColor(mat, convoyBarColor);
                    rend.material = mat;
                }
            }
            _convoyBarActive = true;
        }

        private void UpdateConvoyBar()
        {
            if (_convoyBarObject == null || !_convoyBarActive) return;
            if (defenseShip1 == null || defenseShip2 == null) return;

            Vector3 p1 = defenseShip1.position;
            Vector3 p2 = defenseShip2.position;
            float dist = Vector3.Distance(p1, p2);

            _convoyBarObject.transform.position = (p1 + p2) * 0.5f;
            Vector3 d = (p2 - p1).normalized;
            if (d.sqrMagnitude > 0.001f)
                _convoyBarObject.transform.rotation = Quaternion.FromToRotation(Vector3.up, d);
            _convoyBarObject.transform.localScale = new Vector3(convoyBarThickness, dist * 0.5f, convoyBarThickness);
        }

        public void DetachConvoyBar()
        {
            if (_convoyBarObject == null) return;
            _convoyBarActive = false;
            _convoyBarObject.transform.SetParent(null);

            var barRB = _convoyBarObject.AddComponent<Rigidbody>();
            barRB.mass = 50f;
            barRB.drag = 0.5f;
            barRB.angularDrag = 0.3f;
            barRB.useGravity = true;
            barRB.AddTorque(Random.insideUnitSphere * 30f, ForceMode.Impulse);
            barRB.AddForce(Vector3.down * 20f, ForceMode.Impulse);

            Destroy(_convoyBarObject, barSinkTime);
            _convoyBarObject = null;
        }

        public void DestroyConvoyBar()
        {
            if (_convoyBarObject != null)
            {
                _convoyBarObject.SetActive(false);
                Destroy(_convoyBarObject);
                _convoyBarObject = null;
            }
            _convoyBarActive = false;
        }

        // ── 충돌 감지 ──

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("attack_boat"))
                HandleAttackBoatCollision(other.gameObject);
            else if (IsOtherPairDefenseShip(other.gameObject))
                HandleAllyWebCollision(other.gameObject);
        }

        private void OnTriggerStay(Collider other)
        {
            if (other.CompareTag("attack_boat"))
                HandleAttackBoatCollision(other.gameObject);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.gameObject.CompareTag("attack_boat"))
                HandleAttackBoatCollision(collision.gameObject);
            else if (IsOtherPairDefenseShip(collision.gameObject))
                HandleAllyWebCollision(collision.gameObject);
        }

        private bool IsOtherPairDefenseShip(GameObject obj)
        {
            if (obj == null) return false;
            DefenseAgent agent = obj.GetComponentInParent<DefenseAgent>();
            if (agent == null) return false;
            if (defenseShip1 != null && defenseShip1.GetComponentInParent<DefenseAgent>() == agent) return false;
            if (defenseShip2 != null && defenseShip2.GetComponentInParent<DefenseAgent>() == agent) return false;
            return true;
        }

        private void HandleAllyWebCollision(GameObject allyShip)
        {
            if (allyShip == null) return;
            DefenseAgent agent = allyShip.GetComponentInParent<DefenseAgent>();
            if (agent != null) allyShip = agent.gameObject;

            FindEnvController();
            envController?.OnAllyHitWeb(allyShip, this);
        }

        private void HandleAttackBoatCollision(GameObject attackBoat)
        {
            if (attackBoat == null) return;
            FindEnvController();
            envController?.OnEnemyHitWeb(attackBoat, this);
        }

        private void FindEnvController()
        {
            if (envController != null) return;
            Transform root = transform.parent != null ? transform.parent : transform;
            envController = root.GetComponentInChildren<DefenseEnvController>();
        }

        // ── 폭발 효과 ──

        private void CreateExplosion(Vector3 position)
        {
            if (explosionPrefab == null) return;
            position.y += 0.5f;
            var go = Instantiate(explosionPrefab, position, Quaternion.identity);
            if (go == null) return;
            go.SetActive(true);
            go.transform.localScale = Vector3.one * explosionScale;
            Destroy(go, 3f);
        }

        // ── Gizmo ──

        private void OnDrawGizmos()
        {
            if (defenseShip1 == null || defenseShip2 == null) return;
            Gizmos.color = _isFrozen ? Color.red : Color.cyan;
            Gizmos.DrawLine(defenseShip1.position, defenseShip2.position);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere((defenseShip1.position + defenseShip2.position) * 0.5f, 1f);
        }
    }
}
