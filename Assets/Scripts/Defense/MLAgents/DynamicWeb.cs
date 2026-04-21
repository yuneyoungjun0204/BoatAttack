using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 두 방어 선박 사이의 차단망.
    /// 시각화: 단순 Cube 하나 (비용 최소).
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
        public Color webColor = new Color(0.95f, 0.95f, 0.92f, 0.6f);

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

        // Convoy 연결 막대
        private GameObject _convoyBarObject;
        private bool _convoyBarActive;

        public bool IsFrozen => _isFrozen;

        // ── 생명주기 ──

        private void OnEnable()
        {
            _isFrozen = false;
            if (!_initialized)
            {
                Initialize();
                return;
            }
            EnsureVisualExists();
        }

        private void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (_initialized) return;

            // Rigidbody: 물리 영향 없음, Trigger 작동용
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            // BoxCollider
            _collider = GetComponent<BoxCollider>();
            if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            _collider.isTrigger = isTrigger;

            // 고아 비주얼 정리
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

            // Convoy 접힘: 일정 간격 미만이면 비활성
            bool webOpen = distance >= minWebActiveDist;
            if (webOpen != _wasWebOpen)
            {
                if (_collider != null) _collider.enabled = webOpen;
                if (_visualObject != null) _visualObject.SetActive(webOpen);
                _wasWebOpen = webOpen;
            }

            // 비주얼 크기 (Cube)
            if (webOpen && _visualObject != null)
                _visualObject.transform.localScale = new Vector3(webThickness, webHeight, distance);
        }

        // ── 비주얼 ──

        private void CreateVisual()
        {
            if (_visualObject != null) { _visualObject.SetActive(false); Destroy(_visualObject); }

            _visualObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _visualObject.name = "WebVisual";
            _visualObject.transform.SetParent(transform);
            _visualObject.transform.localPosition = Vector3.zero;
            _visualObject.transform.localRotation = Quaternion.identity;

            // 자체 콜라이더 제거 (부모 BoxCollider만 사용)
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
            Shader s = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (s == null) return null;
            var mat = new Material(s);
            // 반투명
            mat.SetFloat("_Surface", 1f);      // Transparent
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
            mat.color = webColor;
            return mat;
        }

        /// <summary>색상 변경 (외부 호출용)</summary>
        public void SetColor(Color color)
        {
            webColor = color;
            if (_renderer != null && _renderer.material != null)
                _renderer.material.color = color;
        }

        /// <summary>호환성 유지용 stub — 현재는 항상 단순 Cube 사용</summary>
        public void SetFishingNetVisual(bool enabled) { }

        /// <summary>
        /// Instantiate 후 복사된 내부 상태 초기화.
        /// Instantiate는 _initialized=true, _visualObject=원본참조 등을 그대로 복사하므로
        /// 클론 오브젝트에서 반드시 호출해야 한다.
        /// </summary>
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
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _renderer != null && _renderer.material != null)
                _renderer.material.color = webColor;
        }

        // ── 고정 (정지 트랩) ──

        public void FreezeAtCurrentPositions()
        {
            _frozenPos1 = (webAnchor1 != null) ? webAnchor1.position
                : (defenseShip1 != null ? defenseShip1.position : transform.position);
            _frozenPos2 = (webAnchor2 != null) ? webAnchor2.position
                : (defenseShip2 != null ? defenseShip2.position : transform.position);
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
                Shader s = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (s != null)
                {
                    var mat = new Material(s);
                    mat.color = convoyBarColor;
                    mat.SetFloat("_Smoothness", 0.75f);
                    mat.SetFloat("_Metallic", 0.85f);
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
            Vector3 dir = (p2 - p1).normalized;
            if (dir.sqrMagnitude > 0.001f)
                _convoyBarObject.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
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
            // Transform 비교 대신 DefenseAgent 레퍼런스 비교 (자식 콜라이더/앵커 참조 시 오판 방지)
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
