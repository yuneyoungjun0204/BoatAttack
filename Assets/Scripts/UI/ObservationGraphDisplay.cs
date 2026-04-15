using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 관측값 실시간 그래프 (MaskableGraphic 기반, Canvas UI)
    /// TacticalPageManager의 한 페이지로 동작
    /// 마지막 행이 부족할 경우 자동 중앙 정렬
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class ObservationGraphDisplay : MaskableGraphic
    {
        [Header("=== References ===")]
        public DefenseAgent targetAgent;

        [Tooltip("카메라 시점 전환 시 자동으로 대상 변경 (설정 시 targetAgent 자동 갱신)")]
        public DefenseFollowCamera followCamera;

        [Tooltip("카메라 없을 때 fallback (자동 탐색)")]
        public DefenseEnvController envController;

        [Header("=== Graph Settings ===")]
        public int historyLength = 300;
        public float lineWidth = 1.5f;

        [Header("=== Y-Axis Range ===")]
        [Tooltip("그래프 Y축 최소값")]
        public float graphYMin = -1f;
        [Tooltip("그래프 Y축 최대값")]
        public float graphYMax = 1f;

        [Header("=== Layout ===")]
        public int columns = 4;
        public float cellPadding = 4f;
        public float graphTopMargin = 32f;
        public float graphBottomMargin = 28f;

        [Header("=== Colors ===")]
        public Color cellBgColor = new Color(0.08f, 0.09f, 0.14f, 1f);
        public Color graphBgColor = new Color(0.04f, 0.05f, 0.09f, 1f);
        public Color zeroLineColor = new Color(0.3f, 0.3f, 0.4f, 0.8f);
        public Color guideLineColor = new Color(0.2f, 0.2f, 0.25f, 0.5f);
        public Color cellBorderColor = new Color(0.25f, 0.3f, 0.45f, 0.7f);
        public Color cornerBracketColor = new Color(0.35f, 0.7f, 1f, 0.75f);

        // 라벨 텍스트 (SetupScript에서 생성)
        [HideInInspector] public Text[] labelTexts;
        [HideInInspector] public Text[] valueTexts;
        [HideInInspector] public Text[] rangeTexts;

        private float[][] _history;
        private int _writeIndex;
        private int _sampleCount;
        private bool _initialized;
        private int _cachedColumns = -1;
        private DefenseAgent _prevAgent; // 타겟 변경 감지용
        private int _lastCallCount = -1; // collectObsCallCount 변화 추적
        private int _staleFrames = 0;    // 관측 갱신 없이 경과한 프레임 수

        // VectorSensor 3개: phaseFlag, 모선 거리, 모선 베어링
        private static readonly string[] Labels = { "Phase", "MthDst", "MthBrg" };

        // EnemyBuffer: 3개씩 (Dist, Brg, Hdg)
        private static readonly string[] EnemyObsSuffix = { "Dist", "Brg", "Hdg" };
        // AllyBuffer: 3개씩 (Dist, Brg, Hdg)
        private static readonly string[] AllyObsSuffix = { "Dist", "Brg", "Hdg" };

        private static readonly Color[] GraphColors =
        {
            new Color(0.9f, 0.9f, 0.4f, 1f),  // Phase - 노랑
            new Color(0.4f, 0.9f, 0.9f, 1f),  // MthDst - 청록
            new Color(0.9f, 0.5f, 0.9f, 1f),  // MthBrg - 보라
        };

        // P키 포커스 모드: -1=전체, 0~N=해당 인덱스만 확대
        private int _focusIndex = -1;
        private Text _focusTitleText; // 포커스 모드 전용 타이틀 (동적 생성)

        /// <summary>현재 표시 중인 관측 수 (targetAgent에서 동적 결정)</summary>
        private int _obsCount = Labels.Length;

        /// <summary>현재 관측 수 (런타임 동적)</summary>
        public int ObsCount => _obsCount;

        /// <summary>알려진 라벨 수 (에디터 셋업용, Labels 배열 크기)</summary>
        public static int DefaultObsCount => Labels.Length;

        /// <summary>columns와 ObsCount로 행 수 자동 계산</summary>
        public int ComputedRows => Mathf.CeilToInt((float)_obsCount / Mathf.Max(1, columns));

        protected override void Awake()
        {
            base.Awake();
            InitHistory();
        }

        void InitHistory()
        {
            InitHistory(_obsCount);
        }

        /// <summary>지정 크기로 히스토리 초기화 (최초 호출용)</summary>
        void InitHistory(int count)
        {
            _obsCount = Mathf.Max(1, count);
            _history = new float[_obsCount][];
            for (int i = 0; i < _obsCount; i++)
                _history[i] = new float[historyLength];
            _writeIndex = 0;
            _sampleCount = 0;
            _initialized = true;
        }

        /// <summary>관측 수 변경 시 히스토리 리사이즈 (기존 데이터 보존, 초기화 안 함)</summary>
        void ResizeHistory(int newCount)
        {
            newCount = Mathf.Max(1, newCount);
            if (newCount == _obsCount && _history != null) return;

            int oldCount = _obsCount;
            var oldHistory = _history;

            _obsCount = newCount;
            _history = new float[_obsCount][];
            for (int i = 0; i < _obsCount; i++)
            {
                _history[i] = new float[historyLength];
                // 기존 채널 데이터 복사 (인덱스 범위 내)
                if (oldHistory != null && i < oldCount && oldHistory[i] != null)
                    System.Array.Copy(oldHistory[i], _history[i], historyLength);
            }
            // writeIndex, sampleCount 유지 → 그래프 연속성 보존
            _initialized = true;
        }

        /// <summary>타겟 변경 시 히스토리 초기화</summary>
        private void ClearHistory()
        {
            if (_history == null) return;
            for (int i = 0; i < _obsCount; i++)
                System.Array.Clear(_history[i], 0, historyLength);
            _writeIndex = 0;
            _sampleCount = 0;
        }

        new void Start()
        {
            RepositionLabels();
            _cachedColumns = columns;

            // 자동 탐색
            if (followCamera == null)
                followCamera = FindObjectOfType<DefenseFollowCamera>();
            if (envController == null)
                envController = FindObjectOfType<DefenseEnvController>();

            // targetAgent 초기 설정 (Inspector에서 미설정 시)
            if (targetAgent == null)
                FindInitialTarget();

            // 포커스 모드 전용 타이틀 텍스트 생성
            CreateFocusTitleText();
        }

        /// <summary>포커스 모드 전용 타이틀 텍스트 동적 생성</summary>
        private void CreateFocusTitleText()
        {
            var go = new GameObject("FocusTitle");
            go.transform.SetParent(transform, false);

            _focusTitleText = go.AddComponent<Text>();
            _focusTitleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _focusTitleText.fontSize = 22;
            _focusTitleText.fontStyle = FontStyle.Bold;
            _focusTitleText.alignment = TextAnchor.MiddleCenter;
            _focusTitleText.color = Color.white;
            _focusTitleText.supportRichText = true;
            _focusTitleText.raycastTarget = false;
            _focusTitleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _focusTitleText.verticalOverflow = VerticalWrapMode.Overflow;

            var rt = _focusTitleText.rectTransform;
            rt.anchorMin = new Vector2(0.05f, 0.92f);
            rt.anchorMax = new Vector2(0.95f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            // 그래프 메시 위에 렌더링되도록: sortingOrder 오버라이드
            var canvas = go.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;

            go.transform.SetAsLastSibling();
            go.SetActive(false);
        }

        /// <summary>카메라 또는 envController에서 초기 타겟 탐색 (활성 에이전트만)</summary>
        private void FindInitialTarget()
        {
            // 카메라에서 먼저
            if (followCamera != null)
            {
                var camAgent = followCamera.CurrentDefenseAgent;
                if (IsAgentValid(camAgent)) { targetAgent = camAgent; return; }
            }
            // envController fallback: LaunchZoneManager에서 활성 쌍의 에이전트
            if (envController != null && envController.launchZoneManager != null)
            {
                var lzm = envController.launchZoneManager;
                int poolCount = lzm.GetCurrentPoolCount();
                for (int i = 0; i < poolCount; i++)
                {
                    var pair = lzm.GetPair(i);
                    if (pair != null && pair.isActive)
                    {
                        if (IsAgentValid(pair.agent1)) { targetAgent = pair.agent1; return; }
                        if (IsAgentValid(pair.agent2)) { targetAgent = pair.agent2; return; }
                    }
                }
            }
            // 최후 fallback: 레거시 defenseAgent1/2
            if (envController != null)
            {
                if (IsAgentValid(envController.defenseAgent1))
                    targetAgent = envController.defenseAgent1;
                else if (IsAgentValid(envController.defenseAgent2))
                    targetAgent = envController.defenseAgent2;
            }
        }

        /// <summary>에이전트가 유효하고 활성 상태인지 확인</summary>
        private bool IsAgentValid(DefenseAgent agent)
        {
            return agent != null && agent.gameObject.activeInHierarchy && agent.transform.position.y > -100f;
        }

        void Update()
        {
            if (!_initialized) InitHistory();

            // P키: 포커스 모드 토글 (한 관측값만 확대)
            if (Keyboard.current != null && Keyboard.current.pKey.wasPressedThisFrame)
            {
                _focusIndex++;
                if (_focusIndex >= _obsCount) _focusIndex = -1; // 전체로 복귀
                RepositionLabels();
            }
            // ESC키: 포커스 해제
            if (_focusIndex >= 0 && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                _focusIndex = -1;
                RepositionLabels();
            }

            // 카메라 시점 대상과 자동 연동 (활성 에이전트만)
            if (followCamera != null)
            {
                DefenseAgent camAgent = followCamera.CurrentDefenseAgent;
                if (IsAgentValid(camAgent))
                    targetAgent = camAgent;
            }

            // 타겟이 없거나 비활성이면 재탐색
            if (!IsAgentValid(targetAgent))
                FindInitialTarget();

            // 타겟 변경 시 히스토리 초기화
            if (targetAgent != _prevAgent)
            {
                _prevAgent = targetAgent;
                _lastCallCount = -1;
                _staleFrames = 0;
                ClearHistory();
            }

            if (targetAgent == null || targetAgent.lastObservations == null) return;

            // 관측 수 변경 감지 → 히스토리 리사이즈 (기존 데이터 보존)
            // lastObservationsCount 사용 (배열은 오직 커지기만 하므로 Length와 다를 수 있음)
            int agentObsCount = targetAgent.lastObservationsCount;
            if (agentObsCount <= 0) agentObsCount = targetAgent.lastObservations.Length;
            if (agentObsCount != _obsCount && agentObsCount > 0)
            {
                ResizeHistory(agentObsCount);
                RepositionLabels();
            }

            // collectObsCallCount가 변하지 않았으면 같은 값 중복 기록 방지
            int currentCallCount = targetAgent.collectObsCallCount;
            if (currentCallCount != _lastCallCount)
            {
                _lastCallCount = currentCallCount;
                _staleFrames = 0;

                int count = Mathf.Min(agentObsCount, _obsCount);
                for (int i = 0; i < count; i++)
                    _history[i][_writeIndex] = targetAgent.lastObservations[i];

                _writeIndex = (_writeIndex + 1) % historyLength;
                _sampleCount = Mathf.Min(_sampleCount + 1, historyLength);
            }
            else
            {
                _staleFrames++;
            }

            // 값 텍스트 업데이트
            if (valueTexts != null)
            {
                int prevIdx = (_writeIndex - 1 + historyLength) % historyLength;
                for (int i = 0; i < Mathf.Min(valueTexts.Length, _obsCount); i++)
                {
                    if (valueTexts[i] != null)
                    {
                        if (_focusIndex >= 0)
                        {
                            // 포커스 모드: 기존 값 텍스트 전부 숨김 (전용 타이틀에 값 포함)
                            valueTexts[i].gameObject.SetActive(false);
                        }
                        else
                        {
                            valueTexts[i].gameObject.SetActive(true);
                            valueTexts[i].text = _history[i][prevIdx].ToString("F3");
                        }
                    }
                }
            }

            // 라벨 텍스트 포커스 모드 처리
            if (labelTexts != null)
            {
                for (int i = 0; i < Mathf.Min(labelTexts.Length, _obsCount); i++)
                {
                    if (labelTexts[i] == null) continue;
                    if (_focusIndex >= 0)
                    {
                        // 포커스 모드: 기존 라벨 전부 숨김 (전용 타이틀 사용)
                        labelTexts[i].gameObject.SetActive(false);
                    }
                    else
                    {
                        labelTexts[i].gameObject.SetActive(true);
                        labelTexts[i].text = GetDynamicLabel(i);
                    }
                }
            }

            // 범위 텍스트 포커스 모드 처리
            if (rangeTexts != null)
            {
                for (int i = 0; i < Mathf.Min(rangeTexts.Length, _obsCount); i++)
                {
                    if (rangeTexts[i] != null)
                        rangeTexts[i].gameObject.SetActive(_focusIndex < 0 || i == _focusIndex);
                }
            }

            // 포커스 모드 전용 타이틀 업데이트
            if (_focusTitleText != null)
            {
                if (_focusIndex >= 0 && _focusIndex < _obsCount)
                {
                    _focusTitleText.gameObject.SetActive(true);
                    string label = GetDynamicLabel(_focusIndex);
                    Color labelCol = GetColor(_focusIndex);
                    string hex = ColorUtility.ToHtmlStringRGB(labelCol);
                    int prevIdx = (_writeIndex - 1 + historyLength) % historyLength;
                    float val = _history[_focusIndex][prevIdx];
                    _focusTitleText.text = $"<color=#{hex}>[{_focusIndex + 1}/{_obsCount}] {label}</color>  <color=#AAAAAA>{val:F4}</color>  <size=14><color=#666666>P:next  ESC:back</color></size>";
                }
                else
                {
                    // 전체 보기: 디버그 정보 (staleness는 위에서 이미 계산됨)
                    _focusTitleText.gameObject.SetActive(true);
                    string agentName = targetAgent != null ? targetAgent.name : "null";
                    int callCount = targetAgent != null ? targetAgent.collectObsCallCount : 0;

                    // 상태 표시: LIVE(녹색) / STALE(노란) / NO OBS(빨강)
                    string status;
                    if (_staleFrames > 30)
                        status = "<color=#FF0000>NO OBS</color>";
                    else if (_staleFrames > 5)
                        status = "<color=#FFAA00>STALE</color>";
                    else
                        status = "<color=#00FF00>LIVE</color>";

                    // 에이전트 위치 (HIDDEN_POS 감지용)
                    Vector3 pos = targetAgent != null ? targetAgent.transform.position : Vector3.zero;
                    bool active = targetAgent != null && targetAgent.gameObject.activeInHierarchy;

                    _focusTitleText.text = $"<size=14>{status} <color=#888888>{agentName}  obs:{_obsCount}  calls:{callCount}  {(active?"ON":"<color=#FF0000>OFF</color>")}  ({pos.x:F0},{pos.y:F0},{pos.z:F0})</color></size>";
                }
            }

            // columns 변경 시 라벨 재배치
            if (_cachedColumns != columns)
            {
                RepositionLabels();
                _cachedColumns = columns;
            }

            SetVerticesDirty();
        }

        /// <summary>
        /// 셀의 정규화 좌표(0~1) 계산. 마지막 행은 중앙 정렬.
        /// </summary>
        private void GetCellAnchors(int obsIndex, out float xMin, out float yMin,
            out float xMax, out float yMax)
        {
            int cols = Mathf.Max(1, columns);
            int actualRows = Mathf.CeilToInt((float)_obsCount / cols);

            int row = obsIndex / cols;
            int col = obsIndex % cols;

            float cellW = 1f / cols;
            float cellH = 1f / actualRows;

            // 마지막 행 중앙 정렬 오프셋
            int itemsInRow = (row < actualRows - 1) ? cols : (_obsCount - row * cols);
            float offsetX = (itemsInRow < cols) ? (cols - itemsInRow) * cellW * 0.5f : 0f;

            xMin = col * cellW + offsetX;
            xMax = xMin + cellW;
            yMax = 1f - row * cellH;
            yMin = yMax - cellH;
        }

        /// <summary>라벨/값/범위 텍스트를 현재 columns에 맞게 재배치</summary>
        private void RepositionLabels()
        {
            for (int i = 0; i < _obsCount; i++)
            {
                GetCellAnchors(i, out float xMin, out float yMin, out float xMax, out float yMax);

                Rect rect = ((RectTransform)transform).rect;
                float padX = cellPadding / Mathf.Max(1f, rect.width);
                float padY = cellPadding / Mathf.Max(1f, rect.height);

                // 포커스 모드: 포커스 대상만 전체 영역 사용
                if (_focusIndex >= 0 && i == _focusIndex)
                {
                    xMin = 0f; yMin = 0f; xMax = 1f; yMax = 1f;
                    padX = cellPadding / Mathf.Max(1f, rect.width);
                    padY = cellPadding / Mathf.Max(1f, rect.height);
                }

                // 라벨 (셀 상단) — 코드 Labels 배열로 텍스트 자동 설정
                if (labelTexts != null && i < labelTexts.Length && labelTexts[i] != null)
                {
                    labelTexts[i].text = GetDynamicLabel(i);
                    var rt = labelTexts[i].rectTransform;
                    rt.anchorMin = new Vector2(xMin + padX, yMax - padY - 0.04f);
                    rt.anchorMax = new Vector2(xMax - padX, yMax - padY);
                }

                float cellW = (_focusIndex >= 0 && i == _focusIndex) ? 1f : (1f / Mathf.Max(1, columns));

                // 현재 값 (셀 하단 좌측)
                if (valueTexts != null && i < valueTexts.Length && valueTexts[i] != null)
                {
                    var rt = valueTexts[i].rectTransform;
                    rt.anchorMin = new Vector2(xMin + padX, yMin + padY);
                    rt.anchorMax = new Vector2(xMin + padX + cellW * 0.5f, yMin + padY + 0.035f);
                }

                // 범위 표시 (셀 하단 우측)
                if (rangeTexts != null && i < rangeTexts.Length && rangeTexts[i] != null)
                {
                    var rt = rangeTexts[i].rectTransform;
                    rt.anchorMin = new Vector2(xMin + padX + cellW * 0.5f, yMin + padY);
                    rt.anchorMax = new Vector2(xMax - padX, yMin + padY + 0.035f);
                }
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            if (!_initialized || _history == null) return;

            Rect r = GetPixelAdjustedRect();

            // 포커스 모드: 1개만 전체 크기로 렌더
            if (_focusIndex >= 0 && _focusIndex < _obsCount)
            {
                DrawFocusedCell(vh, r, _focusIndex);
                return;
            }

            int cols = Mathf.Max(1, columns);
            int actualRows = ComputedRows;
            float cellW = r.width / cols;
            float cellH = r.height / actualRows;

            for (int i = 0; i < _obsCount; i++)
            {
                int row = i / cols;
                int col = i % cols;

                // 마지막 행 중앙 정렬
                int itemsInRow = (row < actualRows - 1) ? cols : (_obsCount - row * cols);
                float rowOffset = (itemsInRow < cols) ? (cols - itemsInRow) * cellW * 0.5f : 0f;

                float cx = r.x + col * cellW + cellPadding + rowOffset;
                float cy = r.y + (actualRows - 1 - row) * cellH + cellPadding;
                float cw = cellW - cellPadding * 2;
                float ch = cellH - cellPadding * 2;

                // 셀 배경
                AddRect(vh, cx, cy, cw, ch, cellBgColor);

                // 셀 테두리
                float bw = 1f;
                AddRect(vh, cx, cy, cw, bw, cellBorderColor); // bottom
                AddRect(vh, cx, cy + ch - bw, cw, bw, cellBorderColor); // top
                AddRect(vh, cx, cy, bw, ch, cellBorderColor); // left
                AddRect(vh, cx + cw - bw, cy, bw, ch, cellBorderColor); // right

                // 코너 브라켓 (군사 HUD 스타일 - 굵고 뚜렷하게)
                float bracketLen = Mathf.Min(cw, ch) * 0.18f;
                float bracketW = 2f;
                // 좌상단
                AddRect(vh, cx, cy + ch - bracketW, bracketLen, bracketW, cornerBracketColor);
                AddRect(vh, cx, cy + ch - bracketLen, bracketW, bracketLen, cornerBracketColor);
                // 우상단
                AddRect(vh, cx + cw - bracketLen, cy + ch - bracketW, bracketLen, bracketW, cornerBracketColor);
                AddRect(vh, cx + cw - bracketW, cy + ch - bracketLen, bracketW, bracketLen, cornerBracketColor);
                // 좌하단
                AddRect(vh, cx, cy, bracketLen, bracketW, cornerBracketColor);
                AddRect(vh, cx, cy, bracketW, bracketLen, cornerBracketColor);
                // 우하단
                AddRect(vh, cx + cw - bracketLen, cy, bracketLen, bracketW, cornerBracketColor);
                AddRect(vh, cx + cw - bracketW, cy, bracketW, bracketLen, cornerBracketColor);

                // 상단 라벨 영역 분리선
                float headerY = cy + ch - graphTopMargin;
                AddRect(vh, cx + 4f, headerY, cw - 8f, 0.5f,
                    new Color(cellBorderColor.r, cellBorderColor.g, cellBorderColor.b, 0.3f));

                // 컬러 인디케이터 바 (상단 좌측, 그래프 색상 - 더 굵게)
                Color indicatorCol = GetColor(i);
                AddRect(vh, cx + 3f, cy + ch - graphTopMargin + 4f, 4f, graphTopMargin - 8f, indicatorCol);
                // 인디케이터 글로우
                AddRect(vh, cx + 2f, cy + ch - graphTopMargin + 3f, 6f, graphTopMargin - 6f,
                    new Color(indicatorCol.r, indicatorCol.g, indicatorCol.b, 0.15f));

                // 그래프 영역
                float gx = cx + 2;
                float gy = cy + graphBottomMargin;
                float gw = cw - 4;
                float gh = ch - graphTopMargin - graphBottomMargin;

                // 그래프 배경
                AddRect(vh, gx, gy, gw, gh, graphBgColor);

                // 수직 격자 (5등분)
                for (int g = 1; g < 5; g++)
                {
                    float vx = gx + gw * g / 5f;
                    AddRect(vh, vx - 0.25f, gy, 0.5f, gh,
                        new Color(guideLineColor.r, guideLineColor.g, guideLineColor.b, 0.2f));
                }

                // 0 기준선 (중앙, 강조)
                float zeroY = gy + (0f - graphYMin) / (graphYMax - graphYMin) * gh;
                AddRect(vh, gx, zeroY - 0.5f, gw, 1.5f, zeroLineColor);

                // ±0.5 보조선
                AddRect(vh, gx, gy + gh * 0.75f - 0.25f, gw, 0.5f, guideLineColor);
                AddRect(vh, gx, gy + gh * 0.25f - 0.25f, gw, 0.5f, guideLineColor);

                // ±0.25, ±0.75 미세 보조선 (버텍스 절약을 위해 제거)

                // 그래프 라인 (버텍스 절약: 글로우 1단계 + 메인만)
                if (_sampleCount >= 2)
                {
                    // 버텍스 예산 체크: 남은 여유가 있을 때만 글로우
                    if (vh.currentVertCount < 40000)
                    {
                        Color glowCol = new Color(indicatorCol.r, indicatorCol.g, indicatorCol.b, 0.15f);
                        DrawGraphLine(vh, i, gx, gy, gw, gh, glowCol, lineWidth * 3f);
                    }
                    DrawGraphLine(vh, i, gx, gy, gw, gh, indicatorCol, lineWidth);
                }

                // 현재값 인디케이터 (그래프 우측 끝에 작은 마커)
                if (_sampleCount > 0)
                {
                    int lastIdx = (_writeIndex - 1 + historyLength) % historyLength;
                    float lastVal = Mathf.Clamp(_history[i][lastIdx], graphYMin, graphYMax);
                    float markerY = gy + (lastVal - graphYMin) / (graphYMax - graphYMin) * gh;
                    float markerX = gx + gw;
                    // 작은 삼각형 마커
                    Color mCol = indicatorCol;
                    int mi = vh.currentVertCount;
                    vh.AddVert(new Vector3(markerX, markerY), mCol, Vector2.zero);
                    vh.AddVert(new Vector3(markerX + 4f, markerY + 3f), mCol, Vector2.zero);
                    vh.AddVert(new Vector3(markerX + 4f, markerY - 3f), mCol, Vector2.zero);
                    vh.AddTriangle(mi, mi + 1, mi + 2);
                }
            }
        }

        /// <summary>포커스 모드: 1개 관측값을 전체 영역에 확대 렌더링</summary>
        private void DrawFocusedCell(VertexHelper vh, Rect r, int obsIdx)
        {
            float cx = r.x + cellPadding;
            float cy = r.y + cellPadding;
            float cw = r.width - cellPadding * 2;
            float ch = r.height - cellPadding * 2;

            // 확대 시 상하 여백 비율 조정
            float topMargin = 48f;
            float bottomMargin = 44f;

            // 셀 배경
            AddRect(vh, cx, cy, cw, ch, cellBgColor);

            // 셀 테두리
            float bw = 2f;
            AddRect(vh, cx, cy, cw, bw, cellBorderColor);
            AddRect(vh, cx, cy + ch - bw, cw, bw, cellBorderColor);
            AddRect(vh, cx, cy, bw, ch, cellBorderColor);
            AddRect(vh, cx + cw - bw, cy, bw, ch, cellBorderColor);

            // 코너 브라켓 (확대 시 더 크게)
            float bracketLen = Mathf.Min(cw, ch) * 0.08f;
            float bracketW = 3f;
            AddRect(vh, cx, cy + ch - bracketW, bracketLen, bracketW, cornerBracketColor);
            AddRect(vh, cx, cy + ch - bracketLen, bracketW, bracketLen, cornerBracketColor);
            AddRect(vh, cx + cw - bracketLen, cy + ch - bracketW, bracketLen, bracketW, cornerBracketColor);
            AddRect(vh, cx + cw - bracketW, cy + ch - bracketLen, bracketW, bracketLen, cornerBracketColor);
            AddRect(vh, cx, cy, bracketLen, bracketW, cornerBracketColor);
            AddRect(vh, cx, cy, bracketW, bracketLen, cornerBracketColor);
            AddRect(vh, cx + cw - bracketLen, cy, bracketLen, bracketW, cornerBracketColor);
            AddRect(vh, cx + cw - bracketW, cy, bracketW, bracketLen, cornerBracketColor);

            // 상단 헤더 분리선
            float headerY = cy + ch - topMargin;
            AddRect(vh, cx + 8f, headerY, cw - 16f, 1f,
                new Color(cellBorderColor.r, cellBorderColor.g, cellBorderColor.b, 0.5f));

            // 컬러 인디케이터 바 (좌측, 확대)
            Color indicatorCol = GetColor(obsIdx);
            AddRect(vh, cx + 6f, cy + ch - topMargin + 8f, 6f, topMargin - 16f, indicatorCol);
            AddRect(vh, cx + 4f, cy + ch - topMargin + 6f, 10f, topMargin - 12f,
                new Color(indicatorCol.r, indicatorCol.g, indicatorCol.b, 0.15f));

            // 포커스 인덱스 표시 (우상단 — 페이지 네비게이션)
            // "[3/28]" 스타일의 가이드는 텍스트로만 표시 (아래 라벨에서 반영)

            // 그래프 영역 (확대)
            float gx = cx + 8f;
            float gy2 = cy + bottomMargin;
            float gw = cw - 16f;
            float gh = ch - topMargin - bottomMargin;

            AddRect(vh, gx, gy2, gw, gh, graphBgColor);

            // 수직 격자 (10등분 — 확대 시 더 촘촘)
            for (int g = 1; g < 10; g++)
            {
                float vx = gx + gw * g / 10f;
                AddRect(vh, vx - 0.25f, gy2, 0.5f, gh,
                    new Color(guideLineColor.r, guideLineColor.g, guideLineColor.b, 0.2f));
            }

            // 0 기준선
            float zeroY = gy2 + (0f - graphYMin) / (graphYMax - graphYMin) * gh;
            AddRect(vh, gx, zeroY - 0.75f, gw, 2f, zeroLineColor);

            // ±0.25, ±0.5, ±0.75 보조선 (확대 시 더 세밀)
            float[] guides = { 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f };
            foreach (float g in guides)
            {
                if (Mathf.Approximately(g, 0.5f)) continue; // 0 기준선은 이미 그림
                AddRect(vh, gx, gy2 + gh * g - 0.25f, gw, 0.5f, guideLineColor);
            }

            // 그래프 라인 (글로우 + 메인, 두껍게)
            if (_sampleCount >= 2)
            {
                Color glowCol = new Color(indicatorCol.r, indicatorCol.g, indicatorCol.b, 0.2f);
                DrawGraphLine(vh, obsIdx, gx, gy2, gw, gh, glowCol, lineWidth * 4f);
                DrawGraphLine(vh, obsIdx, gx, gy2, gw, gh, indicatorCol, lineWidth * 2f);
            }

            // 현재값 마커 (확대 시 더 크게)
            if (_sampleCount > 0)
            {
                int lastIdx = (_writeIndex - 1 + historyLength) % historyLength;
                float lastVal = Mathf.Clamp(_history[obsIdx][lastIdx], graphYMin, graphYMax);
                float markerY = gy2 + (lastVal - graphYMin) / (graphYMax - graphYMin) * gh;
                float markerX = gx + gw;
                int mi = vh.currentVertCount;
                vh.AddVert(new Vector3(markerX, markerY), indicatorCol, Vector2.zero);
                vh.AddVert(new Vector3(markerX + 8f, markerY + 5f), indicatorCol, Vector2.zero);
                vh.AddVert(new Vector3(markerX + 8f, markerY - 5f), indicatorCol, Vector2.zero);
                vh.AddTriangle(mi, mi + 1, mi + 2);
            }
        }

        void DrawGraphLine(VertexHelper vh, int obsIdx, float gx, float gy, float gw, float gh, Color lineColor, float width = 0f)
        {
            if (width <= 0f) width = lineWidth;
            int count = Mathf.Min(_sampleCount, historyLength);
            float stepX = gw / (historyLength - 1);

            for (int s = 1; s < count; s++)
            {
                int idx0 = (_writeIndex - count + s - 1 + historyLength) % historyLength;
                int idx1 = (_writeIndex - count + s + historyLength) % historyLength;

                float v0 = Mathf.Clamp(_history[obsIdx][idx0], graphYMin, graphYMax);
                float v1 = Mathf.Clamp(_history[obsIdx][idx1], graphYMin, graphYMax);

                float x0 = gx + (s - 1) * stepX;
                float x1 = gx + s * stepX;
                float y0 = gy + (v0 - graphYMin) / (graphYMax - graphYMin) * gh;
                float y1 = gy + (v1 - graphYMin) / (graphYMax - graphYMin) * gh;

                AddLine(vh, x0, y0, x1, y1, width, lineColor);
            }
        }

        void AddRect(VertexHelper vh, float x, float y, float w, float h, Color c)
        {
            int idx = vh.currentVertCount;
            vh.AddVert(new Vector3(x, y), c, Vector2.zero);
            vh.AddVert(new Vector3(x, y + h), c, Vector2.up);
            vh.AddVert(new Vector3(x + w, y + h), c, Vector2.one);
            vh.AddVert(new Vector3(x + w, y), c, Vector2.right);
            vh.AddTriangle(idx, idx + 1, idx + 2);
            vh.AddTriangle(idx, idx + 2, idx + 3);
        }

        void AddLine(VertexHelper vh, float x0, float y0, float x1, float y1, float width, Color c)
        {
            float dx = x1 - x0;
            float dy = y1 - y0;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;

            float hw = width * 0.5f;
            float nx = -dy / len * hw;
            float ny = dx / len * hw;

            int idx = vh.currentVertCount;
            vh.AddVert(new Vector3(x0 + nx, y0 + ny), c, Vector2.zero);
            vh.AddVert(new Vector3(x1 + nx, y1 + ny), c, Vector2.up);
            vh.AddVert(new Vector3(x1 - nx, y1 - ny), c, Vector2.one);
            vh.AddVert(new Vector3(x0 - nx, y0 - ny), c, Vector2.right);
            vh.AddTriangle(idx, idx + 1, idx + 2);
            vh.AddTriangle(idx, idx + 2, idx + 3);
        }

        // 에디터 셋업용 static 버전 (VectorSensor 라벨만)
        public static string GetLabel(int index)
        {
            if (index >= 0 && index < Labels.Length) return Labels[index];
            return $"Obs {index}";
        }

        // 런타임용 instance 버전 (EnemyBuffer + AllyBuffer 포함 동적 라벨)
        private string GetDynamicLabel(int index)
        {
            if (index >= 0 && index < Labels.Length) return Labels[index];

            int bufferIdx = index - Labels.Length;

            // 적군 버퍼 영역 (3개씩: Dist, SignedBrg, Hdg)
            int enemyCount = (targetAgent != null) ? targetAgent.lastEnemyBufferObs.Count : 0;
            if (bufferIdx >= 0 && bufferIdx < enemyCount)
            {
                int enemyNum = bufferIdx / 3;
                int comp = bufferIdx % 3;
                return $"E{enemyNum} {EnemyObsSuffix[comp]}";
            }

            // 아군 버퍼 영역 (3개씩: Dist, Brg, WebLen)
            int allyIdx = bufferIdx - enemyCount;
            int allyCount = (targetAgent != null) ? targetAgent.lastAllyBufferObs.Count : 0;
            if (allyIdx >= 0 && allyIdx < allyCount)
            {
                int allyNum = allyIdx / 3;
                int comp = allyIdx % 3;
                return $"A{allyNum} {AllyObsSuffix[comp]}";
            }

            return $"Obs {index}";
        }

        public static Color GetColor(int index)
        {
            if (index >= 0 && index < GraphColors.Length) return GraphColors[index];
            // 골든 레이시오 기반 색상 자동 생성 (겹치지 않는 색)
            float hue = (index * 0.618034f) % 1f;
            return Color.HSVToRGB(hue, 0.7f, 0.9f);
        }
    }
}
