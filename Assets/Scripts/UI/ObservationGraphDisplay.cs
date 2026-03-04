using UnityEngine;
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

        private static readonly string[] Labels =
        {
            "Partner R", "Partner F", "Partner Dist", "Partner Hdg",
            "Target R", "Target F", "Target Dist", "Target Hdg",
            "Mother R", "Mother F", "Mother Dist",
            "L-Pair Dist", "L-Pair Fwd", "L-Pair Side", "L-Pair Hdg",
            "R-Pair Dist", "R-Pair Fwd", "R-Pair Side", "R-Pair Hdg"
        };

        private static readonly Color[] GraphColors =
        {
            new Color(1f, 0.8f, 0.2f),      // Partner R
            new Color(1f, 0.6f, 0.1f),      // Partner F
            new Color(0.85f, 0.5f, 0.1f),   // Partner Dist
            new Color(0.9f, 0.4f, 0.1f),    // Partner Hdg
            new Color(1f, 0.3f, 0.3f),      // Target R
            new Color(0.9f, 0.2f, 0.5f),    // Target F
            new Color(0.8f, 0.2f, 0.6f),    // Target Dist
            new Color(0.7f, 0.2f, 0.7f),    // Target Hdg
            new Color(0.5f, 0.5f, 1f),      // Mother R
            new Color(0.4f, 0.8f, 1f),      // Mother F
            new Color(0.3f, 0.7f, 0.9f),    // Mother Dist
            new Color(0.2f, 0.9f, 0.4f),    // L-Pair Dist
            new Color(0.3f, 0.8f, 0.3f),    // L-Pair Fwd
            new Color(0.4f, 0.7f, 0.2f),    // L-Pair Side
            new Color(0.5f, 0.6f, 0.2f),    // L-Pair Hdg
            new Color(0.2f, 0.6f, 0.9f),    // R-Pair Dist
            new Color(0.3f, 0.5f, 0.8f),    // R-Pair Fwd
            new Color(0.4f, 0.4f, 0.7f),    // R-Pair Side
            new Color(0.5f, 0.3f, 0.6f),    // R-Pair Hdg
        };

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

        /// <summary>지정 크기로 히스토리 초기화 (관측 수 변경 시 재호출)</summary>
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
        }

        /// <summary>카메라 또는 envController에서 초기 타겟 탐색</summary>
        private void FindInitialTarget()
        {
            // 카메라에서 먼저
            if (followCamera != null)
            {
                var camAgent = followCamera.CurrentDefenseAgent;
                if (camAgent != null) { targetAgent = camAgent; return; }
            }
            // envController fallback
            if (envController != null)
            {
                if (envController.defenseAgent1 != null)
                    targetAgent = envController.defenseAgent1;
                else if (envController.defenseAgent2 != null)
                    targetAgent = envController.defenseAgent2;
            }
        }

        void Update()
        {
            if (!_initialized) InitHistory();

            // 카메라 시점 대상과 자동 연동
            if (followCamera != null)
            {
                DefenseAgent camAgent = followCamera.CurrentDefenseAgent;
                if (camAgent != null)
                    targetAgent = camAgent;
            }

            // 타겟이 아직 없으면 재탐색
            if (targetAgent == null)
                FindInitialTarget();

            // 타겟 변경 시 히스토리 초기화
            if (targetAgent != _prevAgent)
            {
                _prevAgent = targetAgent;
                ClearHistory();
            }

            if (targetAgent == null || targetAgent.lastObservations == null) return;

            // 관측 수 변경 감지 → 히스토리 + 라벨 동적 리사이즈
            int agentObsCount = targetAgent.lastObservations.Length;
            if (agentObsCount != _obsCount)
            {
                InitHistory(agentObsCount);
                RepositionLabels();
            }

            int count = Mathf.Min(agentObsCount, _obsCount);
            for (int i = 0; i < count; i++)
                _history[i][_writeIndex] = targetAgent.lastObservations[i];

            _writeIndex = (_writeIndex + 1) % historyLength;
            _sampleCount = Mathf.Min(_sampleCount + 1, historyLength);

            // 값 텍스트 업데이트
            if (valueTexts != null)
            {
                int prevIdx = (_writeIndex - 1 + historyLength) % historyLength;
                for (int i = 0; i < Mathf.Min(valueTexts.Length, count); i++)
                {
                    if (valueTexts[i] != null)
                        valueTexts[i].text = _history[i][prevIdx].ToString("F3");
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

                // 라벨 (셀 상단) — 코드 Labels 배열로 텍스트 자동 설정
                if (labelTexts != null && i < labelTexts.Length && labelTexts[i] != null)
                {
                    labelTexts[i].text = GetLabel(i);
                    var rt = labelTexts[i].rectTransform;
                    rt.anchorMin = new Vector2(xMin + padX, yMax - padY - 0.04f);
                    rt.anchorMax = new Vector2(xMax - padX, yMax - padY);
                }

                float cellW = 1f / Mathf.Max(1, columns);

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
                float zeroY = gy + gh * 0.5f;
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
                    float lastVal = Mathf.Clamp(_history[i][lastIdx], -1f, 1f);
                    float markerY = gy + (lastVal + 1f) * 0.5f * gh;
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

        void DrawGraphLine(VertexHelper vh, int obsIdx, float gx, float gy, float gw, float gh, Color lineColor, float width = 0f)
        {
            if (width <= 0f) width = lineWidth;
            int count = Mathf.Min(_sampleCount, historyLength);
            float stepX = gw / (historyLength - 1);

            for (int s = 1; s < count; s++)
            {
                int idx0 = (_writeIndex - count + s - 1 + historyLength) % historyLength;
                int idx1 = (_writeIndex - count + s + historyLength) % historyLength;

                float v0 = Mathf.Clamp(_history[obsIdx][idx0], -1f, 1f);
                float v1 = Mathf.Clamp(_history[obsIdx][idx1], -1f, 1f);

                float x0 = gx + (s - 1) * stepX;
                float x1 = gx + s * stepX;
                float y0 = gy + (v0 + 1f) * 0.5f * gh;
                float y1 = gy + (v1 + 1f) * 0.5f * gh;

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

        // Setup 스크립트 및 런타임에서 사용 (범위 초과 시 자동 생성)
        public static string GetLabel(int index)
        {
            if (index >= 0 && index < Labels.Length) return Labels[index];
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
