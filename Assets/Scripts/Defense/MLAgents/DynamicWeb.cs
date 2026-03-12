using UnityEngine;
using Unity.MLAgents;

namespace BoatAttack
{
    /// <summary>
    /// 2대의 방어 선박 사이에 동적으로 생성되는 Web (장막)
    /// 선박 간 거리에 따라 크기가 자동으로 조정됨
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]  // ML-Agents 빌드 호환성: 런타임 AddComponent 방지
    [RequireComponent(typeof(BoxCollider))]
    public class DynamicWeb : MonoBehaviour
    {
        [Header("Target Ships")]
        [Tooltip("방어 선박 1")]
        public Transform defenseShip1;

        [Tooltip("방어 선박 2")]
        public Transform defenseShip2;

        [Header("Web Anchor Points (Inspector에서 할당)")]
        [Tooltip("Web 시작점 (선박1에 부착된 자식 오브젝트)")]
        public Transform webAnchor1;

        [Tooltip("Web 끝점 (선박2에 부착된 자식 오브젝트)")]
        public Transform webAnchor2;

        [Header("Web Settings")]
        [Tooltip("Web 높이")]
        public float webHeight = 40f;

        [Tooltip("Web 두께")]
        public float webThickness = 0.5f;

        [Tooltip("Web 색상")]
        public Color webColor = new Color(0.95f, 0.95f, 0.92f, 0.9f); // 흰색 나일론

        [Tooltip("충돌 판정용 두께 배율 (비주얼보다 두껍게)")]
        [Range(1f, 20f)]
        public float colliderThicknessMultiplier = 10f;

        [Header("Collision")]
        [Tooltip("Trigger 충돌 사용")]
        public bool isTrigger = true;

        [Header("Visual")]
        [Tooltip("Web 시각화 활성화")]
        public bool showVisual = true;

        [Header("Collision Reward")]
        [Tooltip("공격 보트를 막았을 때 방어선에게 주는 보상")]
        public float defenseReward = 10f;

        // allyWebCollisionPenalty는 DefenseEnvController에서 관리

        [Header("Explosion Effect")]
        [Tooltip("공격 보트 폭발 효과 Prefab (War FX)")]
        public GameObject explosionPrefab;

        [Tooltip("폭발 효과 크기 배율")]
        [Range(5f, 50f)]
        public float explosionScale = 15f;

        [Header("Visual Material")]
        [Tooltip("Web 시각화용 Material (비어있으면 기본 생성)")]
        public Material webMaterial;

        [Header("Managers")]
        [Tooltip("환경 컨트롤러 (수동 할당 가능, 비어있으면 자동으로 찾음)")]
        public DefenseEnvController envController;

        [Header("Net Visual Settings")]
        [Tooltip("그물 세로줄 수 (선박 사이 분할)")]
        [Range(4, 40)] public int netVerticalLines = 24;

        [Tooltip("그물 가로줄 수 (깊이 방향)")]
        [Range(3, 16)] public int netHorizontalLines = 10;

        [Tooltip("메인 밧줄 두께 (테두리 밧줄)")]
        [Range(0.1f, 3f)] public float mainRopeWidth = 0.35f;

        [Tooltip("그물코 줄 두께")]
        [Range(0.02f, 0.5f)] public float netLineWidth = 0.08f;

        [Tooltip("그물 처짐 정도 (catenary)")]
        [Range(0f, 15f)] public float netSag = 3f;

        [Tooltip("수면 흔들림 강도")]
        [Range(0f, 2f)] public float waveAmplitude = 0.3f;

        [Tooltip("수면 흔들림 속도")]
        [Range(0f, 3f)] public float waveSpeed = 0.8f;

        [Tooltip("부표 크기")]
        [Range(0.5f, 5f)] public float floatSize = 1.2f;

        [Tooltip("부표 색상")]
        public Color floatColor = new Color(1f, 0.45f, 0f, 1f); // 주황색 부표

        [Tooltip("그물 줄 색상")]
        public Color ropeColor = new Color(0.95f, 0.93f, 0.88f, 1f); // 흰색 나일론 줄

        [Tooltip("어부 그물 스타일 시각화 사용")]
        public bool useFishingNetVisual = true;

        [Tooltip("부표 표시 여부")]
        public bool showFloats = true;

        private BoxCollider _collider;
        private MeshRenderer _renderer;
        private GameObject _visualObject;
        private Color _lastWebColor;

        // 어부 그물용
        private GameObject _netContainer;
        private LineRenderer[] _verticalLines;
        private LineRenderer[] _horizontalLines;
        private LineRenderer[] _diagonalLines;
        private LineRenderer _topRopeLine;     // 상단 메인 밧줄
        private LineRenderer _bottomRopeLine;  // 하단 메인 밧줄
        private GameObject[] _floatObjects;    // 부표들
        private GameObject[] _knotObjects;     // 매듭 (교차점)
        private Vector3[,] _cachedNodes;       // 노드 캐시

        private void Start()
        {
            Initialize();
        }

        private void OnEnable()
        {
            // SetActive(true) 시 비주얼이 없으면 재생성 (풀 재활성화 대응)
            if (_initialized && showVisual)
            {
                if (useFishingNetVisual && _netContainer == null)
                    CreateVisual();
                else if (!useFishingNetVisual && _visualObject == null)
                    CreateVisual();
            }
        }

        private bool _initialized = false;

        private void Initialize()
        {
            if (_initialized) return;

            // BoxCollider 설정
            _collider = gameObject.GetComponent<BoxCollider>();
            if (_collider == null)
            {
                _collider = gameObject.AddComponent<BoxCollider>();
            }
            _collider.isTrigger = isTrigger;

            // 시각화 오브젝트 생성
            if (showVisual)
            {
                CreateVisual();
            }

            // 초기 색상 저장
            _lastWebColor = webColor;

            // DefenseEnvController 찾기 및 캐싱 (멀티 환경 호환)
            if (envController == null)
            {
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                envController = envRoot.GetComponentInChildren<DefenseEnvController>();
            }

            _initialized = true;
        }

        private void Update()
        {
            if (defenseShip1 == null || defenseShip2 == null)
                return;

            UpdateWebTransform();

            // 색상 변경 감지 및 업데이트
            if (_lastWebColor != webColor)
            {
                SetColor(webColor);
                _lastWebColor = webColor;
            }
        }

        /// <summary>
        /// Web 위치 및 크기 업데이트
        /// </summary>
        private void UpdateWebTransform()
        {
            Vector3 pos1 = (webAnchor1 != null) ? webAnchor1.position : defenseShip1.position;
            Vector3 pos2 = (webAnchor2 != null) ? webAnchor2.position : defenseShip2.position;

            // Web 중심 위치
            Vector3 centerPos = (pos1 + pos2) / 2f;
            transform.position = centerPos;

            // Web 회전
            Vector3 direction = pos2 - pos1;
            direction.y = 0f;
            if (direction.magnitude > 0.01f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(direction);
                transform.rotation = targetRotation;
            }

            // Web 크기
            float distance = Vector3.Distance(pos1, pos2);

            if (_collider != null)
            {
                // 충돌 판정용: 두께를 넓혀 고속 적군 tunneling 방지
                _collider.size = new Vector3(webThickness * colliderThicknessMultiplier, webHeight, distance);
            }

            // 어부 그물 시각화 업데이트
            if (useFishingNetVisual && _netContainer != null)
            {
                UpdateFishingNetVisual(pos1, pos2, distance);
            }
            else if (_visualObject != null)
            {
                // 기존 Cube 비주얼
                _visualObject.transform.localScale = new Vector3(webThickness, webHeight, distance);
            }
        }

        /// <summary>
        /// 어부 그물 격자 업데이트: 수면 흔들림 + catenary 처짐 + 부표
        /// </summary>
        private void UpdateFishingNetVisual(Vector3 pos1, Vector3 pos2, float distance)
        {
            float baseY = (pos1.y + pos2.y) * 0.5f;
            float topY = baseY + webHeight * 0.15f;    // 수면 바로 위 (부표 라인)
            float bottomY = baseY - webHeight * 0.85f;  // 대부분 수면 아래

            float time = Time.time * waveSpeed;

            // 격자 노드 계산 [hIdx][vIdx]
            if (_cachedNodes == null || _cachedNodes.GetLength(0) != netHorizontalLines || _cachedNodes.GetLength(1) != netVerticalLines)
                _cachedNodes = new Vector3[netHorizontalLines, netVerticalLines];

            for (int vi = 0; vi < netVerticalLines; vi++)
            {
                float t = (float)vi / (netVerticalLines - 1);
                Vector3 basePos = Vector3.Lerp(pos1, pos2, t);

                // catenary 처짐 (양 끝 고정, 중앙 최대)
                float sagAmount = netSag * 4f * t * (1f - t);

                // 수면 파도 오프셋 (위치마다 다른 위상)
                float wavePhase = vi * 0.7f + time;
                float waveY = Mathf.Sin(wavePhase) * waveAmplitude;
                float waveZ = Mathf.Cos(wavePhase * 0.6f) * waveAmplitude * 0.3f;

                for (int hi = 0; hi < netHorizontalLines; hi++)
                {
                    float hT = (float)hi / (netHorizontalLines - 1);
                    float y = Mathf.Lerp(topY, bottomY, hT);

                    // 아래로 갈수록 처짐 + 파도 영향 감소
                    float sagForRow = sagAmount * (0.3f + 0.7f * hT);
                    float waveFade = 1f - hT * 0.7f; // 수면 근처만 흔들림

                    // 그물 방향에 수직인 방향으로 약간의 변위 (입체감)
                    Vector3 perpDir = Vector3.Cross((pos2 - pos1).normalized, Vector3.up);
                    float perpWave = Mathf.Sin(wavePhase * 1.3f + hi * 1.1f) * waveAmplitude * 0.2f * waveFade;

                    _cachedNodes[hi, vi] = new Vector3(
                        basePos.x + perpDir.x * perpWave,
                        y - sagForRow + waveY * waveFade,
                        basePos.z + perpDir.z * perpWave + waveZ * waveFade
                    );
                }
            }

            // 1. 상단 메인 밧줄 (부표 라인)
            if (_topRopeLine != null)
            {
                _topRopeLine.positionCount = netVerticalLines;
                for (int vi = 0; vi < netVerticalLines; vi++)
                    _topRopeLine.SetPosition(vi, _cachedNodes[0, vi]);
            }

            // 2. 하단 메인 밧줄 (추 라인)
            if (_bottomRopeLine != null)
            {
                _bottomRopeLine.positionCount = netVerticalLines;
                for (int vi = 0; vi < netVerticalLines; vi++)
                    _bottomRopeLine.SetPosition(vi, _cachedNodes[netHorizontalLines - 1, vi]);
            }

            // 3. 세로줄 (위→아래)
            if (_verticalLines != null)
            {
                for (int vi = 0; vi < netVerticalLines && vi < _verticalLines.Length; vi++)
                {
                    if (_verticalLines[vi] == null) continue;
                    _verticalLines[vi].positionCount = netHorizontalLines;
                    for (int hi = 0; hi < netHorizontalLines; hi++)
                        _verticalLines[vi].SetPosition(hi, _cachedNodes[hi, vi]);
                }
            }

            // 4. 가로줄 (pos1→pos2)
            if (_horizontalLines != null)
            {
                for (int hi = 0; hi < netHorizontalLines && hi < _horizontalLines.Length; hi++)
                {
                    if (_horizontalLines[hi] == null) continue;
                    _horizontalLines[hi].positionCount = netVerticalLines;
                    for (int vi = 0; vi < netVerticalLines; vi++)
                        _horizontalLines[hi].SetPosition(vi, _cachedNodes[hi, vi]);
                }
            }

            // 5. 대각선 (마름모 패턴)
            if (_diagonalLines != null)
            {
                int di = 0;
                for (int hi = 0; hi < netHorizontalLines - 1; hi++)
                {
                    for (int vi = 0; vi < netVerticalLines - 1; vi++)
                    {
                        if (di < _diagonalLines.Length && _diagonalLines[di] != null)
                        {
                            _diagonalLines[di].positionCount = 2;
                            _diagonalLines[di].SetPosition(0, _cachedNodes[hi, vi]);
                            _diagonalLines[di].SetPosition(1, _cachedNodes[hi + 1, vi + 1]);
                        }
                        di++;
                        if (di < _diagonalLines.Length && _diagonalLines[di] != null)
                        {
                            _diagonalLines[di].positionCount = 2;
                            _diagonalLines[di].SetPosition(0, _cachedNodes[hi, vi + 1]);
                            _diagonalLines[di].SetPosition(1, _cachedNodes[hi + 1, vi]);
                        }
                        di++;
                    }
                }
            }

            // 6. 부표 위치 업데이트
            if (_floatObjects != null && showFloats)
            {
                for (int vi = 0; vi < _floatObjects.Length && vi < netVerticalLines; vi++)
                {
                    if (_floatObjects[vi] != null)
                    {
                        Vector3 floatPos = _cachedNodes[0, vi];
                        floatPos.y += floatSize * 0.3f;
                        _floatObjects[vi].transform.position = floatPos;
                    }
                }
            }

            // 7. 매듭 위치 업데이트
            if (_knotObjects != null)
            {
                int ki = 0;
                for (int hi = 0; hi < netHorizontalLines; hi++)
                {
                    for (int vi = 0; vi < netVerticalLines; vi++)
                    {
                        if (ki < _knotObjects.Length && _knotObjects[ki] != null)
                            _knotObjects[ki].transform.position = _cachedNodes[hi, vi];
                        ki++;
                    }
                }
            }
        }

        /// <summary>
        /// Web 시각화 생성
        /// </summary>
        private void CreateVisual()
        {
            if (useFishingNetVisual)
            {
                CreateFishingNetVisual();
                return;
            }

            _visualObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _visualObject.name = "WebVisual";
            _visualObject.transform.SetParent(transform);
            _visualObject.transform.localPosition = Vector3.zero;
            _visualObject.transform.localRotation = Quaternion.identity;

            Destroy(_visualObject.GetComponent<BoxCollider>());

            _renderer = _visualObject.GetComponent<MeshRenderer>();
            if (_renderer != null)
            {
                Material mat = CreateNetMaterial();
                if (mat != null)
                    _renderer.material = mat;
            }
        }

        /// <summary>
        /// 어부 그물 스타일 시각화 생성
        /// </summary>
        private void CreateFishingNetVisual()
        {
            if (_netContainer != null)
                Destroy(_netContainer);

            _netContainer = new GameObject("FishingNetVisual");
            _netContainer.transform.SetParent(transform);
            _netContainer.transform.localPosition = Vector3.zero;
            _netContainer.transform.localRotation = Quaternion.identity;

            Material ropeMat = CreateRopeMaterial();
            Material netMat = CreateNetMaterial();

            // 상단 메인 밧줄 (두꺼움 — 부표 연결 라인)
            _topRopeLine = CreateLineRenderer("TopRope", ropeMat, mainRopeWidth);

            // 하단 메인 밧줄 (추 라인)
            _bottomRopeLine = CreateLineRenderer("BottomRope", ropeMat, mainRopeWidth * 0.8f);

            // 세로줄 (위→아래, 처짐)
            _verticalLines = new LineRenderer[netVerticalLines];
            for (int i = 0; i < netVerticalLines; i++)
            {
                _verticalLines[i] = CreateLineRenderer($"VLine_{i}", netMat, netLineWidth);
            }

            // 가로줄 (수평 — 상하단 메인 제외한 중간줄)
            int innerHLines = Mathf.Max(0, netHorizontalLines - 2);
            _horizontalLines = new LineRenderer[netHorizontalLines];
            for (int i = 0; i < netHorizontalLines; i++)
            {
                float w = (i == 0 || i == netHorizontalLines - 1) ? 0 : netLineWidth * 0.7f;
                _horizontalLines[i] = CreateLineRenderer($"HLine_{i}", netMat, Mathf.Max(w, netLineWidth * 0.5f));
            }

            // 대각선 (마름모 패턴 — 그물코)
            int diagCount = (netVerticalLines - 1) * (netHorizontalLines - 1) * 2;
            _diagonalLines = new LineRenderer[diagCount];
            for (int i = 0; i < diagCount; i++)
            {
                _diagonalLines[i] = CreateLineRenderer($"DLine_{i}", netMat, netLineWidth * 0.7f);
            }

            // 매듭 (교차점마다 작은 구체 — 나일론 매듭 표현)
            CreateKnots(ropeMat);

            // 부표 생성
            if (showFloats)
            {
                CreateFloats();
            }
        }

        /// <summary>
        /// 그물 교차점마다 매듭(작은 구체) 생성
        /// </summary>
        private void CreateKnots(Material knotMat)
        {
            int totalKnots = netHorizontalLines * netVerticalLines;
            _knotObjects = new GameObject[totalKnots];

            float knotSize = netLineWidth * 3f; // 줄 두께의 3배
            if (knotMat == null) knotMat = CreateRopeMaterial();

            int ki = 0;
            for (int hi = 0; hi < netHorizontalLines; hi++)
            {
                for (int vi = 0; vi < netVerticalLines; vi++)
                {
                    var knot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    knot.name = $"Knot_{hi}_{vi}";
                    knot.transform.SetParent(_netContainer.transform);
                    knot.transform.localScale = Vector3.one * knotSize;

                    var col = knot.GetComponent<Collider>();
                    if (col != null) Destroy(col);

                    var rend = knot.GetComponent<MeshRenderer>();
                    if (rend != null && knotMat != null)
                        rend.material = knotMat;

                    _knotObjects[ki] = knot;
                    ki++;
                }
            }
        }

        /// <summary>
        /// 상단 메인 밧줄을 따라 부표(플로트) 생성
        /// </summary>
        private void CreateFloats()
        {
            // 매 2~3번째 세로줄마다 부표 배치
            int floatInterval = Mathf.Max(1, netVerticalLines / 6);
            int floatCount = 0;
            for (int vi = 1; vi < netVerticalLines - 1; vi += floatInterval)
                floatCount++;

            _floatObjects = new GameObject[netVerticalLines];

            Material floatMat = CreateFloatMaterial();

            int idx = 0;
            for (int vi = 1; vi < netVerticalLines - 1; vi += floatInterval)
            {
                var floatObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                floatObj.name = $"Float_{idx}";
                floatObj.transform.SetParent(_netContainer.transform);
                floatObj.transform.localScale = Vector3.one * floatSize;

                // 콜라이더 제거 (비주얼 전용)
                var col = floatObj.GetComponent<Collider>();
                if (col != null) Destroy(col);

                var rend = floatObj.GetComponent<MeshRenderer>();
                if (rend != null && floatMat != null)
                    rend.material = floatMat;

                _floatObjects[vi] = floatObj;
                idx++;
            }
        }

        private LineRenderer CreateLineRenderer(string name, Material mat, float width)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(_netContainer.transform);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;

            LineRenderer lr = obj.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCapVertices = 3;
            lr.numCornerVertices = 3;
            lr.material = mat;
            lr.startColor = ropeColor;
            lr.endColor = ropeColor;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        /// <summary>
        /// 메인 밧줄용 불투명 머티리얼 (나일론 밧줄)
        /// </summary>
        private Material CreateRopeMaterial()
        {
            if (webMaterial != null)
            {
                Material mat = new Material(webMaterial);
                mat.color = ropeColor;
                return mat;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            if (shader != null)
            {
                Material mat = new Material(shader);
                mat.color = ropeColor;
                mat.SetFloat("_Smoothness", 0.25f); // 나일론 약간 광택
                mat.SetFloat("_Metallic", 0f);
                return mat;
            }
            return null;
        }

        /// <summary>
        /// 그물코용 머티리얼 (불투명 나일론 — 축구골대 느낌)
        /// </summary>
        private Material CreateNetMaterial()
        {
            if (webMaterial != null)
            {
                Material mat = new Material(webMaterial);
                mat.color = ropeColor;
                return mat;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            if (shader != null)
            {
                Material mat = new Material(shader);
                mat.color = ropeColor;
                mat.SetFloat("_Smoothness", 0.2f);
                mat.SetFloat("_Metallic", 0f);
                return mat;
            }
            return null;
        }

        /// <summary>
        /// 부표용 불투명 머티리얼 (주황색 플라스틱)
        /// </summary>
        private Material CreateFloatMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            if (shader != null)
            {
                Material mat = new Material(shader);
                mat.color = floatColor;
                mat.SetFloat("_Smoothness", 0.7f); // 플라스틱 광택
                mat.SetFloat("_Metallic", 0f);
                return mat;
            }
            return null;
        }

        /// <summary>
        /// Web 색상 변경
        /// </summary>
        public void SetColor(Color color)
        {
            webColor = color;
            if (_renderer != null && _renderer.material != null)
            {
                _renderer.material.color = color;
            }

            // 어부 그물 라인 색상 업데이트
            SetNetLineColors(_verticalLines, ropeColor);
            SetNetLineColors(_horizontalLines, ropeColor);
            SetNetLineColors(_diagonalLines, new Color(ropeColor.r, ropeColor.g, ropeColor.b, 0.7f));
            if (_topRopeLine != null) { _topRopeLine.startColor = ropeColor; _topRopeLine.endColor = ropeColor; }
            if (_bottomRopeLine != null) { _bottomRopeLine.startColor = ropeColor; _bottomRopeLine.endColor = ropeColor; }
        }

        private void SetNetLineColors(LineRenderer[] lines, Color color)
        {
            if (lines == null) return;
            foreach (var lr in lines)
            {
                if (lr == null) continue;
                lr.startColor = color;
                lr.endColor = color;
                if (lr.material != null)
                    lr.material.color = color;
            }
        }

        private void OnValidate()
        {
            if (Application.isPlaying && showVisual && _renderer != null && _renderer.material != null)
            {
                SetColor(webColor);
            }
        }

        /// <summary>
        /// Trigger 충돌 감지
        /// </summary>
        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("attack_boat"))
            {
                HandleAttackBoatCollision(other.gameObject);
            }
            else if (IsOtherPairDefenseShip(other.gameObject))
            {
                HandleAllyWebCollision(other.gameObject);
            }
        }

        /// <summary>
        /// Trigger 내부 머무름 (매 프레임 체크 — tunneling 보완)
        /// </summary>
        private void OnTriggerStay(Collider other)
        {
            if (other.CompareTag("attack_boat"))
            {
                HandleAttackBoatCollision(other.gameObject);
            }
        }

        /// <summary>
        /// 다른 페어의 아군 선박인지 확인 (자기 페어는 제외)
        /// </summary>
        private bool IsOtherPairDefenseShip(GameObject obj)
        {
            if (obj == null)
                return false;

            // 자기 페어의 에이전트는 무시
            if (defenseShip1 != null && obj.transform == defenseShip1)
                return false;
            if (defenseShip2 != null && obj.transform == defenseShip2)
                return false;

            // 다른 페어의 DefenseAgent만 감지
            if (obj.GetComponent<DefenseAgent>() != null)
                return true;

            return false;
        }

        /// <summary>
        /// 아군 선박 Web 충돌 처리 (자신의 Web 정보도 전달)
        /// </summary>
        private void HandleAllyWebCollision(GameObject allyShip)
        {
            if (allyShip == null)
                return;

            if (envController == null)
            {
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                envController = envRoot.GetComponentInChildren<DefenseEnvController>();
            }

            if (envController != null)
            {
                envController.OnAllyHitWeb(allyShip, this);
            }
        }

        /// <summary>
        /// 물리 충돌 감지
        /// </summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (collision.gameObject.CompareTag("attack_boat"))
            {
                HandleAttackBoatCollision(collision.gameObject);
            }
            else if (IsOtherPairDefenseShip(collision.gameObject))
            {
                HandleAllyWebCollision(collision.gameObject);
            }
        }

        /// <summary>
        /// attack_boat과의 충돌 처리
        /// </summary>
        private void HandleAttackBoatCollision(GameObject attackBoat)
        {
            if (attackBoat == null)
                return;

            if (envController == null)
            {
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                envController = envRoot.GetComponentInChildren<DefenseEnvController>();
                if (envController == null)
                    return;
            }

            envController.OnEnemyHitWeb(attackBoat, this);
        }

        /// <summary>
        /// 폭발 효과 생성
        /// </summary>
        private void CreateExplosion(Vector3 position)
        {
            if (explosionPrefab == null)
                return;

            Vector3 explosionPosition = position;
            explosionPosition.y += 0.5f;

            GameObject explosion = Instantiate(explosionPrefab, explosionPosition, Quaternion.identity);

            if (explosion != null)
            {
                explosion.SetActive(true);
                float scaleMultiplier = explosionScale;
                explosion.transform.localScale = Vector3.one * scaleMultiplier;

                ParticleSystem[] particleSystems = explosion.GetComponentsInChildren<ParticleSystem>();
                foreach (var ps in particleSystems)
                {
                    var main = ps.main;

                    if (main.startSize.mode == ParticleSystemCurveMode.Constant)
                    {
                        main.startSize = main.startSize.constant * scaleMultiplier;
                    }
                    else if (main.startSize.mode == ParticleSystemCurveMode.TwoConstants)
                    {
                        main.startSize = new ParticleSystem.MinMaxCurve(
                            main.startSize.constantMin * scaleMultiplier,
                            main.startSize.constantMax * scaleMultiplier
                        );
                    }

                    if (main.startSpeed.mode == ParticleSystemCurveMode.Constant)
                    {
                        main.startSpeed = main.startSpeed.constant * scaleMultiplier;
                    }
                    else if (main.startSpeed.mode == ParticleSystemCurveMode.TwoConstants)
                    {
                        main.startSpeed = new ParticleSystem.MinMaxCurve(
                            main.startSpeed.constantMin * scaleMultiplier,
                            main.startSpeed.constantMax * scaleMultiplier
                        );
                    }
                }
            }
        }

        /// <summary>
        /// 방어선에게 보상 부여
        /// </summary>
        private void RewardDefenseShips()
        {
            if (defenseShip1 != null)
            {
                var agent = defenseShip1.GetComponent<Unity.MLAgents.Agent>();
                if (agent != null)
                {
                    agent.AddReward(defenseReward);
                }
            }

            if (defenseShip2 != null)
            {
                var agent = defenseShip2.GetComponent<Unity.MLAgents.Agent>();
                if (agent != null)
                {
                    agent.AddReward(defenseReward);
                }
            }
        }

        /// <summary>
        /// attack_boat 리셋
        /// </summary>
        private void ResetAttackBoat(GameObject attackBoat)
        {
            if (attackBoat == null)
                return;

            if (envController == null)
            {
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                envController = envRoot.GetComponentInChildren<DefenseEnvController>();
            }

            if (envController != null)
            {
                envController.OnEnemyHitWeb(attackBoat);
            }
        }

        /// <summary>
        /// Gizmo 시각화
        /// </summary>
        private void OnDrawGizmos()
        {
            if (defenseShip1 == null || defenseShip2 == null)
                return;

            Vector3 pos1 = defenseShip1.position;
            Vector3 pos2 = defenseShip2.position;
            Vector3 centerPos = (pos1 + pos2) / 2f;

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(pos1, pos2);

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(centerPos, 1f);
        }
    }
}
