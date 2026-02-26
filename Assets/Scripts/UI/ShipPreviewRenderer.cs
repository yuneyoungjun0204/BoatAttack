using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace BoatAttack
{
    /// <summary>
    /// 3D 선박 프리뷰 렌더러
    /// RenderTexture + Camera로 선박 프리팹을 UI에 실시간 3D 표시
    /// 마우스 드래그: 회전, 스크롤: 줌, 우클릭 드래그: 상하 각도
    /// </summary>
    public class ShipPreviewRenderer : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IScrollHandler
    {
        [Header("=== References ===")]
        public RawImage targetImage;
        public DefenseEnvController envController;

        [Header("=== Settings ===")]
        public bool isEnemy = false;
        public int textureSize = 512;
        public Color backgroundColor = new Color(0.04f, 0.06f, 0.1f, 1f);

        [Header("=== Camera ===")]
        public float cameraDistance = 15f;
        public float cameraHeight = 8f;
        public float minDistance = 5f;
        public float maxDistance = 40f;
        public float minHeight = 1f;
        public float maxHeight = 20f;

        [Header("=== Interaction ===")]
        [Tooltip("드래그 회전 감도")]
        public float dragRotateSpeed = 0.5f;
        [Tooltip("드래그 상하 감도")]
        public float dragHeightSpeed = 0.05f;
        [Tooltip("스크롤 줌 감도")]
        public float scrollZoomSpeed = 2f;
        [Tooltip("자동 회전 속도 (0이면 정지)")]
        public float autoRotateSpeed = 5f;

        Camera _previewCam;
        RenderTexture _renderTex;
        GameObject _shipClone;
        GameObject _previewLight;
        float _orbitAngle = 30f;
        bool _setupDone;
        bool _isDragging;
        int _retryCount = 0;
        const int MAX_RETRIES = 20;

        static int _instanceCounter = 0;
        int _instanceId;

        Vector3 PreviewCenter => new Vector3(_instanceId * 500f, 500f, 0f);

        void Awake()
        {
            _instanceId = _instanceCounter++;
        }

        void OnEnable()
        {
            if (!_setupDone)
            {
                _retryCount = 0;
                Invoke(nameof(SetupPreview), 0.5f);
            }
            else if (_previewCam != null)
                _previewCam.enabled = true;
        }

        void OnDisable()
        {
            CancelInvoke(nameof(SetupPreview));
            if (_previewCam != null)
                _previewCam.enabled = false;
        }

        void OnDestroy()
        {
            Cleanup();
            _instanceCounter = Mathf.Max(0, _instanceCounter - 1);
        }

        void SetupPreview()
        {
            if (_setupDone) return;

            GameObject source = GetSourceShip();
            if (source == null)
            {
                _retryCount++;
                if (_retryCount < MAX_RETRIES)
                {
                    Invoke(nameof(SetupPreview), 0.5f);
                    return;
                }
                Debug.LogWarning($"[ShipPreview] {(isEnemy ? "적군" : "아군")} 선박을 {MAX_RETRIES}회 시도 후에도 찾을 수 없습니다");
                return;
            }
            _retryCount = 0;

            // === RenderTexture ===
            _renderTex = new RenderTexture(textureSize, textureSize, 24);
            _renderTex.antiAliasing = 2;

            // === 프리뷰 카메라 ===
            var camObj = new GameObject($"PreviewCam_{(isEnemy ? "Enemy" : "Friendly")}");
            _previewCam = camObj.AddComponent<Camera>();
            _previewCam.targetTexture = _renderTex;
            _previewCam.clearFlags = CameraClearFlags.SolidColor;
            _previewCam.backgroundColor = backgroundColor;
            _previewCam.nearClipPlane = 0.5f;
            _previewCam.farClipPlane = 100f;
            _previewCam.fieldOfView = 30f;
            _previewCam.depth = -10;
            _previewCam.cullingMask = -1; // 모든 레이어 (Layer 11 포함)

            // URP: UniversalAdditionalCameraData 자동 추가 (URP 프로젝트)
            TryAddURPCameraData(camObj);

            // === 조명 생성 (프리뷰 전용) ===
            _previewLight = new GameObject($"PreviewLight_{(isEnemy ? "Enemy" : "Friendly")}");
            var light = _previewLight.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.85f, 0.9f, 1f, 1f);
            light.intensity = 1.5f;
            light.shadows = LightShadows.None;
            _previewLight.transform.position = PreviewCenter + Vector3.up * 20f;
            _previewLight.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

            // === 선박 복제 ===
            _shipClone = Instantiate(source, PreviewCenter, Quaternion.Euler(0, 30, 0));
            _shipClone.name = $"PreviewShip_{(isEnemy ? "Enemy" : "Friendly")}";
            // 소스가 비활성(템플릿)일 수 있으므로 명시적 활성화
            _shipClone.SetActive(true);
            // 자식 중 비활성 오브젝트 재활성화 (BoatHull 등)
            ActivateAllChildren(_shipClone);
            DisableNonVisual(_shipClone);

            if (targetImage != null)
                targetImage.texture = _renderTex;

            UpdateCameraOrbit();
            _setupDone = true;

            int rendererCount = _shipClone.GetComponentsInChildren<Renderer>(true).Length;
            Debug.Log($"[ShipPreview] {(isEnemy ? "적군" : "아군")} 프리뷰 설정 완료: {source.name}, renderers={rendererCount}");
        }

        /// <summary>
        /// URP 카메라 데이터 자동 추가 (리플렉션으로 URP 종속성 회피)
        /// </summary>
        void TryAddURPCameraData(GameObject camObj)
        {
            // UniversalAdditionalCameraData는 URP 패키지 타입
            var urpType = System.Type.GetType(
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (urpType != null)
            {
                var existing = camObj.GetComponent(urpType);
                if (existing == null)
                    camObj.AddComponent(urpType);
            }
        }

        /// <summary>
        /// 모든 자식 오브젝트를 활성화 (ParticleSystem 자식 제외)
        /// </summary>
        void ActivateAllChildren(GameObject obj)
        {
            foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
            {
                // ParticleSystem이 있는 자식은 건드리지 않음 (DisableNonVisual에서 처리)
                if (child.GetComponent<ParticleSystem>() != null) continue;
                child.gameObject.SetActive(true);
            }
        }

        GameObject GetSourceShip()
        {
            if (envController == null) return null;

            if (isEnemy)
            {
                if (envController.enemyShips != null)
                {
                    foreach (var e in envController.enemyShips)
                        if (e != null && e.activeInHierarchy) return e;
                    foreach (var e in envController.enemyShips)
                        if (e != null) return e;
                }
            }
            else
            {
                // 1순위: LaunchZoneManager의 프리팹 직접 사용 (가장 깨끗한 소스)
                var lzm = envController.GetComponentInChildren<LaunchZoneManager>();
                if (lzm != null && lzm.defenseBoatPrefab != null)
                    return lzm.defenseBoatPrefab;

                // 2순위: 활성 에이전트
                if (envController.defenseAgent1 != null && envController.defenseAgent1.gameObject.activeInHierarchy)
                    return envController.defenseAgent1.gameObject;

                // 3순위: LaunchZoneManager의 활성 에이전트
                if (lzm != null && lzm.IsInitialized)
                {
                    var agents = lzm.GetActiveAgents();
                    if (agents.Count > 0 && agents[0] != null)
                        return agents[0].gameObject;
                }

                // 4순위: 비활성 템플릿
                if (envController.defenseAgent1 != null)
                    return envController.defenseAgent1.gameObject;
            }
            return null;
        }

        void Update()
        {
            if (_shipClone == null || _previewCam == null) return;

            if (!_isDragging && autoRotateSpeed > 0)
                _orbitAngle += autoRotateSpeed * Time.unscaledDeltaTime;

            UpdateCameraOrbit();
        }

        void UpdateCameraOrbit()
        {
            if (_previewCam == null) return;
            float rad = _orbitAngle * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(
                Mathf.Sin(rad) * cameraDistance,
                cameraHeight,
                Mathf.Cos(rad) * cameraDistance);

            _previewCam.transform.position = PreviewCenter + offset;
            _previewCam.transform.LookAt(PreviewCenter + Vector3.up * 2f);
        }

        #region Pointer Events (마우스 조작)

        public void OnPointerDown(PointerEventData eventData)
        {
            _isDragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                _orbitAngle -= eventData.delta.x * dragRotateSpeed;
            }

            if (eventData.button == PointerEventData.InputButton.Right)
            {
                cameraHeight -= eventData.delta.y * dragHeightSpeed;
                cameraHeight = Mathf.Clamp(cameraHeight, minHeight, maxHeight);
            }

            if (eventData.button == PointerEventData.InputButton.Left)
            {
                cameraHeight -= eventData.delta.y * dragHeightSpeed * 0.5f;
                cameraHeight = Mathf.Clamp(cameraHeight, minHeight, maxHeight);
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            cameraDistance -= eventData.scrollDelta.y * scrollZoomSpeed;
            cameraDistance = Mathf.Clamp(cameraDistance, minDistance, maxDistance);
        }

        void LateUpdate()
        {
            if (_isDragging && !Input.GetMouseButton(0) && !Input.GetMouseButton(1))
                _isDragging = false;
        }

        #endregion

        void DisableNonVisual(GameObject obj)
        {
            // 물리 비활성화
            foreach (var rb in obj.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            foreach (var col in obj.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            // 스크립트 비활성화
            foreach (var mb in obj.GetComponentsInChildren<MonoBehaviour>(true))
                mb.enabled = false;

            // 클론 내 카메라 비활성화 (보트 프리팹에 카메라가 포함되어 있음)
            foreach (var cam in obj.GetComponentsInChildren<Camera>(true))
                cam.enabled = false;

            // 클론 내 조명 비활성화
            foreach (var light in obj.GetComponentsInChildren<Light>(true))
                light.enabled = false;

            // 클론 내 AudioListener 비활성화
            foreach (var listener in obj.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = false;

            // 파티클/오디오 비활성화
            foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>(true))
                ps.gameObject.SetActive(false);
            foreach (var audio in obj.GetComponentsInChildren<AudioSource>(true))
                audio.enabled = false;
        }

        void Cleanup()
        {
            if (_shipClone != null) { Destroy(_shipClone); _shipClone = null; }
            if (_previewCam != null) { Destroy(_previewCam.gameObject); _previewCam = null; }
            if (_previewLight != null) { Destroy(_previewLight); _previewLight = null; }
            if (_renderTex != null) { _renderTex.Release(); Destroy(_renderTex); _renderTex = null; }
            _setupDone = false;
        }
    }
}
