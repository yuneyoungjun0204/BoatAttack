using UnityEngine;
using UnityEngine.UI;
using System.Collections;

namespace BoatAttack
{
    /// <summary>
    /// 게임 화면 우하단에 아군 선박 생성 버튼 표시
    /// - 쿨타임: 3초
    /// - 최대 호출: 3회
    /// - 1회 클릭 시 최대 5쌍 시간차 배치
    /// </summary>
    public class AllySpawnButtonUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("DefenseEnvController (미설정 시 자동 탐색)")]
        public DefenseEnvController envController;

        [Header("Settings")]
        [Tooltip("버튼 쿨타임 (초)")]
        public float cooldownTime = 3f;

        [Tooltip("최대 호출 횟수")]
        public int maxSpawnCount = 3;

        [Tooltip("한 번에 생성 쌍 수 (4쌍=8척)")]
        public int maxPairsPerSpawn = 4;

        [Tooltip("쌍 간 생성 간격 (초) - 시간차 배치")]
        public float spawnInterval = 0.5f;

        // UI 요소
        private Canvas _canvas;
        private Button _spawnButton;
        private Text _buttonText;
        private Image _cooldownOverlay;
        private Image _buttonImage;

        // 상태
        private int _spawnUsed = 0;
        private float _cooldownTimer = 0f;
        private bool _onCooldown = false;
        private bool _isSpawning = false; // 시간차 생성 중 플래그
        private int _spawnedThisPress = 0; // 현재 클릭에서 생성된 수
        private int _totalToSpawnThisPress = 0; // 현재 클릭에서 생성할 총 수

        private void Start()
        {
            if (envController == null)
                envController = FindObjectOfType<DefenseEnvController>();

            CreateUI();
        }

        private void Update()
        {
            if (_onCooldown)
            {
                _cooldownTimer -= Time.unscaledDeltaTime;
                if (_cooldownTimer <= 0f)
                {
                    _onCooldown = false;
                    _cooldownTimer = 0f;
                }
            }

            UpdateButtonState();
        }

        private void CreateUI()
        {
            // Canvas 생성
            GameObject canvasObj = new GameObject("AllySpawnCanvas");
            canvasObj.transform.SetParent(transform);
            _canvas = canvasObj.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();

            // 버튼 오브젝트
            GameObject btnObj = new GameObject("SpawnButton");
            btnObj.transform.SetParent(canvasObj.transform, false);

            // RectTransform: 우하단
            RectTransform btnRect = btnObj.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1f, 0f);
            btnRect.anchorMax = new Vector2(1f, 0f);
            btnRect.pivot = new Vector2(1f, 0f);
            btnRect.anchoredPosition = new Vector2(-20f, 20f);
            btnRect.sizeDelta = new Vector2(200f, 60f);

            // 버튼 배경
            _buttonImage = btnObj.AddComponent<Image>();
            _buttonImage.color = new Color(0.2f, 0.5f, 0.8f, 0.9f);
            _spawnButton = btnObj.AddComponent<Button>();
            _spawnButton.targetGraphic = _buttonImage;

            // 쿨타임 오버레이 (Fill 방식)
            GameObject overlayObj = new GameObject("CooldownOverlay");
            overlayObj.transform.SetParent(btnObj.transform, false);
            RectTransform overlayRect = overlayObj.AddComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.sizeDelta = Vector2.zero;
            _cooldownOverlay = overlayObj.AddComponent<Image>();
            _cooldownOverlay.color = new Color(0f, 0f, 0f, 0.5f);
            _cooldownOverlay.type = Image.Type.Filled;
            _cooldownOverlay.fillMethod = Image.FillMethod.Horizontal;
            _cooldownOverlay.fillOrigin = 0;
            _cooldownOverlay.raycastTarget = false;

            // 텍스트
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(btnObj.transform, false);
            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            _buttonText = textObj.AddComponent<Text>();
            _buttonText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_buttonText.font == null)
                _buttonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _buttonText.fontSize = 16;
            _buttonText.alignment = TextAnchor.MiddleCenter;
            _buttonText.color = Color.white;
            _buttonText.raycastTarget = false;

            _spawnButton.onClick.AddListener(OnSpawnClicked);
        }

        private void OnSpawnClicked()
        {
            if (_isSpawning || envController == null)
                return;

            // [테스트] zone 0,1,2,3에 각 1쌍씩 총 4쌍 즉시 생성
            for (int z = 0; z < 4; z++)
                envController.SpawnAllyPair(z);
        }

        /// <summary>
        /// 쌍을 시간차로 하나씩 생성하는 코루틴
        /// </summary>
        private IEnumerator SpawnPairsSequentially(int count)
        {
            _isSpawning = true;
            _spawnedThisPress = 0;
            _totalToSpawnThisPress = count;

            for (int i = 0; i < count; i++)
            {
                if (envController == null) break;

                if (envController.SpawnAllyPair())
                {
                    _spawnedThisPress++;
                    // Debug.Log($"[AllySpawnUI] 쌍 {_spawnedThisPress}/{count} 배치 완료");
                }

                // 마지막 쌍이 아니면 대기
                if (i < count - 1)
                {
                    yield return new WaitForSeconds(spawnInterval);
                }
            }

            _isSpawning = false;
            _spawnedThisPress = 0;
            _totalToSpawnThisPress = 0;

            // Debug.Log($"[AllySpawnUI] 시간차 배치 완료 (남은 횟수: {maxSpawnCount - _spawnUsed})");
        }

        private void UpdateButtonState()
        {
            if (_spawnButton == null) return;

            // [테스트] 항상 활성화, 쿨타임/횟수 무시
            int available = envController != null ? envController.GetAvailableAllyPairCount() : 0;
            _spawnButton.interactable = !_isSpawning && available > 0;

            if (_buttonImage != null)
                _buttonImage.color = new Color(0.2f, 0.5f, 0.8f, 0.9f);

            if (_cooldownOverlay != null)
                _cooldownOverlay.gameObject.SetActive(false);

            if (_buttonText != null)
                _buttonText.text = $"Deploy +1\n(avail: {available})";
        }

        /// <summary>
        /// 에피소드 리셋 시 호출 (사용 횟수 초기화)
        /// </summary>
        public void OnEpisodeReset()
        {
            // 진행 중인 코루틴 정리
            StopAllCoroutines();
            _isSpawning = false;
            _spawnedThisPress = 0;
            _totalToSpawnThisPress = 0;

            _spawnUsed = 0;
            _onCooldown = false;
            _cooldownTimer = 0f;
        }
    }
}
