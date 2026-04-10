using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 선박 4면(좌/우/전/후) 포획 존.
    /// Flank Phase에서 적군이 각 오프셋 위치의 captureRadius 이내 진입 시 포획.
    /// 시각화: DynamicWeb과 동일한 어부 그물 스타일 (catenary + 파도 + 부표).
    /// </summary>
    public class SingleNetCapture : MonoBehaviour
    {
        [Header("Capture Zone")]
        [Tooltip("포획 구체 반경 (m)")]
        public float captureRadius = 30f;

        [Tooltip("선박 중심에서 좌/우 측면까지 오프셋 (m)")]
        public float sideOffset = 8f;

        [Tooltip("선박 중심에서 전/후 방향까지 오프셋 (m)")]
        public float fwdOffset = 8f;

        [Tooltip("포획 감지할 적군 레이어 마스크")]
        public LayerMask captureLayerMask = Physics.DefaultRaycastLayers;

        [Tooltip("포획 감지할 적군 태그")]
        public string enemyTag = "attack_boat";

        [Header("Cooldown")]
        [Tooltip("연속 포획 쿨다운 (초)")]
        public float captureCooldown = 1f;

        [Header("Net Size")]
        [Tooltip("좌우 그물의 전후 길이 (m)")]
        public float sideLateralLength = 40f;

        [Tooltip("전후 그물의 좌우 길이 (m)")]
        public float fwdLateralLength = 40f;

        [Tooltip("그물 높이 (m)")]
        public float webVisualHeight = 40f;

        [Header("Runtime Visual (DynamicWeb 동일 스타일)")]
        [Tooltip("Game 뷰 그물 시각화 활성화")]
        public bool showRuntimeVisual = true;

        [Tooltip("세로줄 수")]
        [Range(4, 24)] public int netVerticalLines = 12;

        [Tooltip("가로줄 수")]
        [Range(3, 12)] public int netHorizontalLines = 8;

        [Tooltip("메인 밧줄 두께 (m)")]
        [Range(0.1f, 3f)] public float mainRopeWidth = 0.35f;

        [Tooltip("그물코 줄 두께 (m)")]
        [Range(0.02f, 0.5f)] public float netLineWidth = 0.08f;

        [Tooltip("그물 처짐 (catenary)")]
        [Range(0f, 15f)] public float netSag = 3f;

        [Tooltip("수면 흔들림 강도")]
        [Range(0f, 2f)] public float waveAmplitude = 0.3f;

        [Tooltip("수면 흔들림 속도")]
        [Range(0f, 3f)] public float waveSpeed = 0.8f;

        [Tooltip("밧줄 색상 (흰색 나일론)")]
        public Color ropeColor = new Color(0.95f, 0.93f, 0.88f, 1f);

        [Tooltip("부표 색상 (주황)")]
        public Color floatColor = new Color(1f, 0.45f, 0f, 1f);

        [Tooltip("부표 크기 (m)")]
        [Range(0.5f, 5f)] public float floatSize = 1.2f;

        [Tooltip("부표 표시")]
        public bool showFloats = true;

        [Tooltip("머티리얼 (null이면 자동 생성)")]
        public Material netMaterial;

        // 참조
        private DefenseEnvController _envController;
        private DefenseAgent _agent;

        // 상태
        private bool _isActive = false;
        private float _lastCaptureTime = -999f;
        private GameObject _lastCapturedEnemy;

        // 4면 그물 시각화
        private NetVisual _leftNet;
        private NetVisual _rightNet;
        private NetVisual _frontNet;
        private NetVisual _backNet;
        private int _updateCounter;

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

        private void Awake()  { _agent = GetComponent<DefenseAgent>(); }

        private void Start()
        {
            if (_envController == null)
            {
                Transform root = transform;
                while (root.parent != null) root = root.parent;
                _envController = root.GetComponentInChildren<DefenseEnvController>();
            }
        }

        public void Init(DefenseEnvController envController) { _envController = envController; }

        public void Activate()
        {
            _isActive = true;
            _lastCapturedEnemy = null;
            _lastCaptureTime   = -999f;

            if (showRuntimeVisual)
            {
                if (_leftNet  == null) _leftNet  = CreateNet("LeftNet");
                if (_rightNet == null) _rightNet = CreateNet("RightNet");
                if (_frontNet == null) _frontNet = CreateNet("FrontNet");
                if (_backNet  == null) _backNet  = CreateNet("BackNet");

                _leftNet.container.SetActive(true);
                _rightNet.container.SetActive(true);
                _frontNet.container.SetActive(true);
                _backNet.container.SetActive(true);
            }
        }

        public void Deactivate()
        {
            _isActive = false;
            _lastCapturedEnemy = null;
            if (_leftNet  != null) _leftNet.container.SetActive(false);
            if (_rightNet != null) _rightNet.container.SetActive(false);
            if (_frontNet != null) _frontNet.container.SetActive(false);
            if (_backNet  != null) _backNet.container.SetActive(false);
        }

        public bool IsActive => _isActive;

        private void FixedUpdate()
        {
            if (!_isActive || _envController == null) return;
            if (Time.time - _lastCaptureTime < captureCooldown) return;

            // 4면 검사
            Vector3 leftCenter  = transform.position - transform.right   * sideOffset;
            Vector3 rightCenter = transform.position + transform.right   * sideOffset;
            Vector3 frontCenter = transform.position + transform.forward * fwdOffset;
            Vector3 backCenter  = transform.position - transform.forward * fwdOffset;

            if (TryCapture(Physics.OverlapSphere(leftCenter,  captureRadius, captureLayerMask))) return;
            if (TryCapture(Physics.OverlapSphere(rightCenter, captureRadius, captureLayerMask))) return;
            if (TryCapture(Physics.OverlapSphere(frontCenter, captureRadius, captureLayerMask))) return;
            TryCapture(Physics.OverlapSphere(backCenter, captureRadius, captureLayerMask));
        }

        private void Update()
        {
            if (!_isActive || !showRuntimeVisual) return;
            _updateCounter++;
            if (_updateCounter % 3 != 0) return;

            // 좌/우: 선박 forward 방향으로 뻗는 그물
            UpdateSideNet(_leftNet,  transform.position - transform.right   * sideOffset, transform.forward, sideLateralLength);
            UpdateSideNet(_rightNet, transform.position + transform.right   * sideOffset, transform.forward, sideLateralLength);
            // 전/후: 선박 right 방향으로 뻗는 그물 (수직)
            UpdateSideNet(_frontNet, transform.position + transform.forward * fwdOffset,  transform.right,   fwdLateralLength);
            UpdateSideNet(_backNet,  transform.position - transform.forward * fwdOffset,  transform.right,   fwdLateralLength);
        }

        private void UpdateSideNet(NetVisual net, Vector3 center, Vector3 spanDir, float length)
        {
            if (net == null || !net.container.activeSelf) return;
            Vector3 p1 = center - spanDir * length * 0.5f;
            Vector3 p2 = center + spanDir * length * 0.5f;
            UpdateNet(net, p1, p2);
        }

        private bool TryCapture(Collider[] hits)
        {
            foreach (var col in hits)
            {
                if (!col.CompareTag(enemyTag)) continue;
                GameObject enemy = col.gameObject;
                if (enemy == _lastCapturedEnemy) continue;
                if (_envController.IsEnemyNeutralized(enemy)) continue;

                _lastCapturedEnemy = enemy;
                _lastCaptureTime   = Time.time;
                _envController.OnEnemyHitSingleNet(enemy, _agent);
                return true;
            }
            return false;
        }

        // ── 그물 생성 ────────────────────────────────────────────────

        private NetVisual CreateNet(string containerName)
        {
            var net = new NetVisual();
            net.container = new GameObject(containerName);
            net.container.transform.SetParent(transform, false);

            Material ropeMat = CreateMat(ropeColor);
            Material lineMat = CreateMat(ropeColor);

            net.topRope    = MakeLR(net.container, "TopRope",    ropeMat, mainRopeWidth);
            net.bottomRope = MakeLR(net.container, "BottomRope", ropeMat, mainRopeWidth * 0.8f);

            net.verticals  = new LineRenderer[netVerticalLines];
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
            net.container.SetActive(false);
            return net;
        }

        private void CreateFloats(NetVisual net)
        {
            int count = Mathf.Max(1, netVerticalLines / 2);
            net.floats = new GameObject[count];
            Material fm = CreateMat(floatColor);
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

        // ── 그물 업데이트 (DynamicWeb.UpdateFishingNetVisual 동일 알고리즘) ──

        private void UpdateNet(NetVisual net, Vector3 pos1, Vector3 pos2)
        {
            int nV = netVerticalLines, nH = netHorizontalLines;
            float baseY   = (pos1.y + pos2.y) * 0.5f;
            float topY    = baseY + webVisualHeight * 0.15f;
            float bottomY = baseY - webVisualHeight * 0.85f;
            float time    = Time.time * waveSpeed;
            Vector3 perpDir = Vector3.Cross((pos2 - pos1).normalized, Vector3.up);

            for (int vi = 0; vi < nV; vi++)
            {
                float t       = (float)vi / (nV - 1);
                Vector3 bpos  = Vector3.Lerp(pos1, pos2, t);
                float sag     = netSag * 4f * t * (1f - t);
                float phase   = vi * 0.7f + time;
                float wY      = Mathf.Sin(phase) * waveAmplitude;
                float wZ      = Mathf.Cos(phase * 0.6f) * waveAmplitude * 0.3f;

                for (int hi = 0; hi < nH; hi++)
                {
                    float hT      = (float)hi / (nH - 1);
                    float y       = Mathf.Lerp(topY, bottomY, hT);
                    float sagRow  = sag * (0.3f + 0.7f * hT);
                    float fade    = 1f - hT * 0.7f;
                    float pWave   = Mathf.Sin(phase * 1.3f + hi * 1.1f) * waveAmplitude * 0.2f * fade;
                    net.nodes[hi, vi] = new Vector3(
                        bpos.x + perpDir.x * pWave,
                        y - sagRow + wY * fade,
                        bpos.z + perpDir.z * pWave + wZ * fade);
                }
            }

            SetRope(net.topRope,    nV, 0,      net.nodes);
            SetRope(net.bottomRope, nV, nH - 1, net.nodes);

            for (int vi = 0; vi < nV; vi++)
            {
                var lr = net.verticals[vi]; if (!lr) continue;
                lr.positionCount = nH;
                for (int hi = 0; hi < nH; hi++) lr.SetPosition(hi, net.nodes[hi, vi]);
            }
            for (int hi = 0; hi < nH; hi++)
            {
                var lr = net.horizontals[hi]; if (!lr) continue;
                lr.positionCount = nV;
                for (int vi = 0; vi < nV; vi++) lr.SetPosition(vi, net.nodes[hi, vi]);
            }

            int di = 0;
            for (int hi = 0; hi < nH - 1; hi++)
                for (int vi = 0; vi < nV - 1; vi++)
                {
                    SetDiag(net.diagonals, di++, net.nodes[hi, vi],     net.nodes[hi+1, vi+1]);
                    SetDiag(net.diagonals, di++, net.nodes[hi, vi+1],   net.nodes[hi+1, vi]);
                }

            if (net.floats != null && showFloats)
                for (int i = 0; i < net.floats.Length; i++)
                {
                    int vi = i * 2;
                    if (vi < nV && net.floats[i])
                        net.floats[i].transform.position = net.nodes[0, vi] + Vector3.up * floatSize * 0.3f;
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

        // ── 머티리얼 / LineRenderer ──────────────────────────────────

        private Material CreateMat(Color color)
        {
            if (netMaterial != null) { var m = new Material(netMaterial); m.color = color; return m; }
            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (sh == null) return new Material(Shader.Find("Hidden/InternalErrorShader"));
            var mat = new Material(sh); mat.color = color; return mat;
        }

        private LineRenderer MakeLR(GameObject parent, string n, Material mat, float width)
        {
            var go = new GameObject(n);
            go.transform.SetParent(parent.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace      = true;
            lr.startWidth         = width;
            lr.endWidth           = width;
            lr.numCapVertices     = 3;
            lr.numCornerVertices  = 3;
            lr.material           = mat;
            lr.startColor         = ropeColor;
            lr.endColor           = ropeColor;
            lr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows     = false;
            return lr;
        }

        private void OnDestroy()
        {
            if (_leftNet?.container)  Destroy(_leftNet.container);
            if (_rightNet?.container) Destroy(_rightNet.container);
            if (_frontNet?.container) Destroy(_frontNet.container);
            if (_backNet?.container)  Destroy(_backNet.container);
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = _isActive ? new Color(1f, 0.4f, 0f, 0.4f) : new Color(0.5f, 0.5f, 0.5f, 0.15f);
            Gizmos.DrawWireSphere(transform.position - transform.right   * sideOffset, captureRadius);
            Gizmos.DrawWireSphere(transform.position + transform.right   * sideOffset, captureRadius);
            Gizmos.DrawWireSphere(transform.position + transform.forward * fwdOffset,  captureRadius);
            Gizmos.DrawWireSphere(transform.position - transform.forward * fwdOffset,  captureRadius);
        }
    }
}
