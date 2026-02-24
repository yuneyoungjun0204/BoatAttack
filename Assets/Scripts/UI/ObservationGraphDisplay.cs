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

        [Header("=== Graph Settings ===")]
        public int historyLength = 300;
        public float lineWidth = 1.5f;

        [Header("=== Layout ===")]
        public int columns = 4;
        public float cellPadding = 4f;
        public float graphTopMargin = 32f;
        public float graphBottomMargin = 28f;

        [Header("=== Colors ===")]
        public Color cellBgColor = new Color(0.12f, 0.12f, 0.18f, 1f);
        public Color graphBgColor = new Color(0.08f, 0.08f, 0.12f, 1f);
        public Color zeroLineColor = new Color(0.3f, 0.3f, 0.4f, 0.8f);
        public Color guideLineColor = new Color(0.2f, 0.2f, 0.25f, 0.5f);

        // 라벨 텍스트 (SetupScript에서 생성)
        [HideInInspector] public Text[] labelTexts;
        [HideInInspector] public Text[] valueTexts;
        [HideInInspector] public Text[] rangeTexts;

        private float[][] _history;
        private int _writeIndex;
        private int _sampleCount;
        private bool _initialized;
        private int _cachedColumns = -1;

        private static readonly string[] Labels =
        {
            "Partner R", "Partner F", "Partner Dist", "Partner Hdg",
            "Target R", "Target F", "Target Dist", "Target Hdg",
            "Mother R", "Mother F", "Mother Dist"
        };

        private static readonly Color[] GraphColors =
        {
            new Color(1f, 0.8f, 0.2f),
            new Color(1f, 0.6f, 0.1f),
            new Color(0.85f, 0.5f, 0.1f),
            new Color(0.9f, 0.4f, 0.1f),
            new Color(1f, 0.3f, 0.3f),
            new Color(0.9f, 0.2f, 0.5f),
            new Color(0.8f, 0.2f, 0.6f),
            new Color(0.7f, 0.2f, 0.7f),
            new Color(0.5f, 0.5f, 1f),
            new Color(0.4f, 0.8f, 1f),
            new Color(0.3f, 0.7f, 0.9f),
        };

        public const int OBS_COUNT = 11;

        /// <summary>columns와 OBS_COUNT로 행 수 자동 계산</summary>
        public int ComputedRows => Mathf.CeilToInt((float)OBS_COUNT / Mathf.Max(1, columns));

        protected override void Awake()
        {
            base.Awake();
            InitHistory();
        }

        void Start()
        {
            RepositionLabels();
            _cachedColumns = columns;
        }

        void InitHistory()
        {
            if (_initialized) return;
            _history = new float[OBS_COUNT][];
            for (int i = 0; i < OBS_COUNT; i++)
                _history[i] = new float[historyLength];
            _writeIndex = 0;
            _sampleCount = 0;
            _initialized = true;
        }

        void Update()
        {
            if (!_initialized) InitHistory();
            if (targetAgent == null || targetAgent.lastObservations == null) return;

            int count = Mathf.Min(targetAgent.lastObservations.Length, OBS_COUNT);
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
            int actualRows = Mathf.CeilToInt((float)OBS_COUNT / cols);

            int row = obsIndex / cols;
            int col = obsIndex % cols;

            float cellW = 1f / cols;
            float cellH = 1f / actualRows;

            // 마지막 행 중앙 정렬 오프셋
            int itemsInRow = (row < actualRows - 1) ? cols : (OBS_COUNT - row * cols);
            float offsetX = (itemsInRow < cols) ? (cols - itemsInRow) * cellW * 0.5f : 0f;

            xMin = col * cellW + offsetX;
            xMax = xMin + cellW;
            yMax = 1f - row * cellH;
            yMin = yMax - cellH;
        }

        /// <summary>라벨/값/범위 텍스트를 현재 columns에 맞게 재배치</summary>
        private void RepositionLabels()
        {
            for (int i = 0; i < OBS_COUNT; i++)
            {
                GetCellAnchors(i, out float xMin, out float yMin, out float xMax, out float yMax);

                Rect rect = ((RectTransform)transform).rect;
                float padX = cellPadding / Mathf.Max(1f, rect.width);
                float padY = cellPadding / Mathf.Max(1f, rect.height);

                // 라벨 (셀 상단)
                if (labelTexts != null && i < labelTexts.Length && labelTexts[i] != null)
                {
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

            for (int i = 0; i < OBS_COUNT; i++)
            {
                int row = i / cols;
                int col = i % cols;

                // 마지막 행 중앙 정렬
                int itemsInRow = (row < actualRows - 1) ? cols : (OBS_COUNT - row * cols);
                float rowOffset = (itemsInRow < cols) ? (cols - itemsInRow) * cellW * 0.5f : 0f;

                float cx = r.x + col * cellW + cellPadding + rowOffset;
                float cy = r.y + (actualRows - 1 - row) * cellH + cellPadding;
                float cw = cellW - cellPadding * 2;
                float ch = cellH - cellPadding * 2;

                // 셀 배경
                AddRect(vh, cx, cy, cw, ch, cellBgColor);

                // 그래프 영역
                float gx = cx + 2;
                float gy = cy + graphBottomMargin;
                float gw = cw - 4;
                float gh = ch - graphTopMargin - graphBottomMargin;

                // 그래프 배경
                AddRect(vh, gx, gy, gw, gh, graphBgColor);

                // 0 기준선 (중앙)
                float zeroY = gy + gh * 0.5f;
                AddRect(vh, gx, zeroY - 0.5f, gw, 1f, zeroLineColor);

                // ±0.5 보조선
                AddRect(vh, gx, gy + gh * 0.75f - 0.5f, gw, 1f, guideLineColor);
                AddRect(vh, gx, gy + gh * 0.25f - 0.5f, gw, 1f, guideLineColor);

                // 그래프 라인
                if (_sampleCount >= 2)
                    DrawGraphLine(vh, i, gx, gy, gw, gh, GraphColors[i]);
            }
        }

        void DrawGraphLine(VertexHelper vh, int obsIdx, float gx, float gy, float gw, float gh, Color lineColor)
        {
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

                AddLine(vh, x0, y0, x1, y1, lineWidth, lineColor);
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

        // Setup 스크립트에서 사용
        public static string GetLabel(int index) => Labels[index];
        public static Color GetColor(int index) => GraphColors[index];
    }
}
