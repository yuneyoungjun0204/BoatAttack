using UnityEngine;
using Unity.MLAgents;
using Obi;

namespace BoatAttack
{
    /// <summary>
    /// 2대의 방어 선박 사이에 동적으로 생성되는 Web (장막)
    /// 선박 간 거리에 따라 크기가 자동으로 조정됨
    /// Obi Cloth 모드: 물리 기반 그물 시각화 (시연용, 학습 시 OFF)
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
        public Color webColor = new Color(0f, 1f, 1f, 0.3f); // 반투명 청록색

        [Tooltip("충돌 판정용 두께 배율 (비주얼보다 두껍게)")]
        [Range(1f, 20f)]
        public float colliderThicknessMultiplier = 10f;

        [Header("Collision")]
        [Tooltip("Trigger 충돌 사용")]
        public bool isTrigger = true;

        [Header("Visual")]
        [Tooltip("Web 시각화 활성화")]
        public bool showVisual = true;

        [Header("=== Obi Cloth (시연용 그물) ===")]
        [Tooltip("Obi Cloth 그물 사용 (학습 시 false, 시연 시 true)")]
        public bool useObiCloth = false;

        [Tooltip("Obi Cloth Blueprint (에디터에서 미리 생성)")]
        public ObiClothBlueprint obiClothBlueprint;

        [Tooltip("Obi Solver (환경 내 공유, 비어있으면 자동 탐색)")]
        public ObiSolver obiSolver;

        [Tooltip("Ship1에 부착할 파티클 그룹 (Blueprint 내 Left Edge)")]
        public ObiParticleGroup obiGroupShip1;

        [Tooltip("Ship2에 부착할 파티클 그룹 (Blueprint 내 Right Edge)")]
        public ObiParticleGroup obiGroupShip2;

        // Obi 런타임 참조
        private GameObject _obiClothObj;      // ObiSolver 자식으로 생성되는 별도 오브젝트
        private ObiCloth _obiCloth;
        private ObiParticleAttachment _attachment1;
        private ObiParticleAttachment _attachment2;
        private bool _obiInitialized = false;

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

        private BoxCollider _collider;
        private MeshRenderer _renderer;
        private GameObject _visualObject;
        private Color _lastWebColor;

        private void Start()
        {
            Initialize();
        }

        private void OnEnable()
        {
            // SetActive(true) 시 WebVisual이 없으면 재생성 (풀 재활성화 대응)
            if (_initialized && showVisual && _visualObject == null)
            {
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

            // Obi Cloth 초기화 (useObiCloth=true일 때만)
            if (useObiCloth)
            {
                InitializeObiCloth();
            }

            _initialized = true;
        }

        private void Update()
        {
            if (defenseShip1 == null || defenseShip2 == null)
                return;

            // BoxCollider 위치/크기는 항상 업데이트 (충돌 판정용)
            UpdateWebTransform();

            // Obi Attachment 지연 생성: Start()에서 선박이 NULL이었으면 여기서 생성
            if (useObiCloth && _obiInitialized && _attachment1 == null && defenseShip1 != null)
            {
                CreateObiAttachments();
            }

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

            if (_visualObject != null)
            {
                // Obi 모드에서는 Cube 비주얼 숨김
                if (useObiCloth && _obiInitialized)
                {
                    if (_visualObject.activeSelf) _visualObject.SetActive(false);
                }
                else
                {
                    if (!_visualObject.activeSelf) _visualObject.SetActive(true);
                    // 비주얼은 원래 두께 유지
                    _visualObject.transform.localScale = new Vector3(webThickness, webHeight, distance);
                }
            }
        }

        /// <summary>
        /// Web 시각화 생성
        /// </summary>
        private void CreateVisual()
        {
            _visualObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _visualObject.name = "WebVisual";
            _visualObject.transform.SetParent(transform);
            _visualObject.transform.localPosition = Vector3.zero;
            _visualObject.transform.localRotation = Quaternion.identity;

            Destroy(_visualObject.GetComponent<BoxCollider>());

            _renderer = _visualObject.GetComponent<MeshRenderer>();
            if (_renderer != null)
            {
                Material mat = null;

                if (webMaterial != null)
                {
                    mat = new Material(webMaterial);
                    mat.color = webColor;
                }
                else
                {
                    // URP 호환: Standard 셰이더는 URP에서 null → 핑크 머티리얼 발생
                    Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null)
                        shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader == null)
                        shader = Shader.Find("Standard");

                    if (shader != null)
                    {
                        mat = new Material(shader);
                        mat.color = webColor;

                        // URP 반투명 설정
                        mat.SetFloat("_Surface", 1); // 0=Opaque, 1=Transparent
                        mat.SetFloat("_Blend", 0);   // 0=Alpha
                        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        mat.SetInt("_ZWrite", 0);
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                        mat.renderQueue = 3000;
                    }
                    else if (_renderer.sharedMaterial != null)
                    {
                        mat = new Material(_renderer.sharedMaterial);
                        mat.color = webColor;
                    }
                }

                if (mat != null)
                {
                    _renderer.material = mat;
                }
            }
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

        #region Obi Cloth

        /// <summary>
        /// Obi Cloth 초기화: ObiCloth + ObiParticleAttachment × 2 생성
        /// Blueprint와 ParticleGroup은 Inspector에서 미리 할당 필요
        /// </summary>
        private void InitializeObiCloth()
        {
            if (_obiInitialized) return;
            if (obiClothBlueprint == null)
            {
                Debug.LogWarning($"[DynamicWeb] Obi Cloth Blueprint이 할당되지 않음 — Obi 비활성화");
                useObiCloth = false;
                return;
            }

            // ObiSolver 자동 탐색
            if (obiSolver == null)
            {
                obiSolver = GetComponentInParent<ObiSolver>();
                if (obiSolver == null)
                {
                    Transform envRoot = transform.parent != null ? transform.parent : transform;
                    obiSolver = envRoot.GetComponentInChildren<ObiSolver>();
                }
            }

            if (obiSolver == null)
            {
                Debug.LogWarning($"[DynamicWeb] ObiSolver를 찾을 수 없음 — Obi 비활성화");
                useObiCloth = false;
                return;
            }

            // ★ 핵심: ObiSolver 자식으로 별도 GameObject 생성 (비활성 상태)
            // Obi는 ObiActor가 ObiSolver의 자식 계층에 있어야 동작함
            // AddComponent 시 OnEnable→AddToSolver가 호출되므로,
            // 모든 설정 완료 후 활성화해야 Blueprint가 등록됨
            _obiClothObj = new GameObject($"ObiCloth_{gameObject.name}");
            _obiClothObj.SetActive(false);  // ★ 비활성 상태로 시작
            _obiClothObj.transform.SetParent(obiSolver.transform);
            _obiClothObj.transform.localPosition = Vector3.zero;
            _obiClothObj.transform.localRotation = Quaternion.identity;

            // ObiCloth 컴포넌트 생성 (비활성이므로 OnEnable 호출 안 됨)
            _obiCloth = _obiClothObj.AddComponent<ObiCloth>();
            _obiCloth.clothBlueprint = obiClothBlueprint;

            // 천 물성 설정 (그물답게)
            _obiCloth.stretchCompliance = 0f;       // 늘어나지 않음
            _obiCloth.stretchingScale = 1f;
            _obiCloth.bendCompliance = 0.02f;       // 약간 구부러짐
            _obiCloth.maxBending = 0.05f;
            _obiCloth.drag = 0.05f;                 // 공기 저항 (바람에 펄럭임)
            _obiCloth.lift = 0.02f;

            // ObiClothRenderer 추가 (시각화)
            var clothRenderer = _obiClothObj.AddComponent<ObiClothRenderer>();
            clothRenderer.cloth = _obiCloth;

            // MeshFilter에 inputMesh 설정 (Renderer가 사용)
            var meshFilter = _obiClothObj.GetComponent<MeshFilter>();
            if (meshFilter != null && obiClothBlueprint.inputMesh != null)
                meshFilter.sharedMesh = obiClothBlueprint.inputMesh;

            // MeshRenderer에 Material 설정 (없으면 안 보임)
            var meshRenderer = _obiClothObj.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                Material mat = null;
                if (webMaterial != null)
                {
                    mat = new Material(webMaterial);
                }
                else
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) shader = Shader.Find("Standard");
                    if (shader != null)
                    {
                        mat = new Material(shader);
                        mat.color = webColor;
                        // 반투명 설정
                        mat.SetFloat("_Surface", 1);
                        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        mat.SetInt("_ZWrite", 0);
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        mat.renderQueue = 3000;
                    }
                }
                if (mat != null)
                    meshRenderer.material = mat;
            }

            // Attachment: 선박이 이미 할당되어 있으면 즉시 생성, 아니면 Update()에서 지연 생성
            if (defenseShip1 != null && defenseShip2 != null)
            {
                CreateObiAttachments();
            }

            // ★ 모든 설정 완료 후 활성화 → OnEnable → AddToSolver (Blueprint 포함)
            _obiClothObj.SetActive(true);

            _obiInitialized = true;
            Debug.Log($"[DynamicWeb] Obi 초기화 완료 (Particles: {obiClothBlueprint.particleCount}, Ships: {(defenseShip1 != null ? "OK" : "대기중")})");
        }

        /// <summary>
        /// Obi Attachment 생성 (선박 할당 후 호출)
        /// </summary>
        private void CreateObiAttachments()
        {
            if (_obiClothObj == null || _attachment1 != null) return;

            if (obiGroupShip1 != null && defenseShip1 != null)
            {
                _attachment1 = _obiClothObj.AddComponent<ObiParticleAttachment>();
                _attachment1.target = defenseShip1;
                _attachment1.particleGroup = obiGroupShip1;
                _attachment1.attachmentType = ObiParticleAttachment.AttachmentType.Static;
                _attachment1.constrainOrientation = false;
            }

            if (obiGroupShip2 != null && defenseShip2 != null)
            {
                _attachment2 = _obiClothObj.AddComponent<ObiParticleAttachment>();
                _attachment2.target = defenseShip2;
                _attachment2.particleGroup = obiGroupShip2;
                _attachment2.attachmentType = ObiParticleAttachment.AttachmentType.Static;
                _attachment2.constrainOrientation = false;
            }

            Debug.Log($"[DynamicWeb] Obi Attachment 생성 완료 (Ship1: {defenseShip1?.name}, Ship2: {defenseShip2?.name})");
        }

        /// <summary>
        /// Obi Cloth 활성화/비활성화 (풀 시스템에서 호출)
        /// </summary>
        public void SetObiClothActive(bool active)
        {
            if (!_obiInitialized || _obiClothObj == null) return;

            _obiClothObj.SetActive(active);
        }

        /// <summary>
        /// Obi Attachment 타겟 갱신 (풀에서 선박 참조가 바뀔 때)
        /// </summary>
        public void UpdateObiAttachmentTargets()
        {
            if (!_obiInitialized) return;

            if (_attachment1 != null && defenseShip1 != null)
                _attachment1.target = defenseShip1;

            if (_attachment2 != null && defenseShip2 != null)
                _attachment2.target = defenseShip2;
        }

        /// <summary>
        /// Obi Solver 참조 설정 (풀 복제 시 새 Solver 할당)
        /// </summary>
        public void SetObiSolver(ObiSolver solver)
        {
            obiSolver = solver;
            if (_obiInitialized)
            {
                // 기존 Obi 오브젝트 제거 후 재생성
                if (_obiClothObj != null)
                    Destroy(_obiClothObj);
                _obiInitialized = false;
                InitializeObiCloth();
            }
        }

        private void OnDestroy()
        {
            // DynamicWeb 파괴 시 Obi 오브젝트도 정리
            if (_obiClothObj != null)
                Destroy(_obiClothObj);
        }

        #endregion
    }
}
