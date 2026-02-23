using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 관측값 10개 실시간 그래프 (MaskableGraphic 기반, Canvas UI)
    /// TacticalPageManager의 한 페이지로 동작
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
        public int rows = 3;
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

        private float[][] _history;
        private int _writeIndex;
        private int _sampleCount;
        private bool _initialized;

        private static readonly string[] Labels =
        {
            "Partner R", "Partner F", "Partner Dist", "Partner Hdg",
            "Target R", "Target F", "Target Dist", "Target Hdg",
            "Mother R", "Mother F", "Mother Dist"
        };

        private static readonly Color[] GraphColors =
        {
            new Color(1f, 0.8f, 0.2f),      // Partner R
            new Color(1f, 0.6f, 0.1f),      // Partner F
            new Color(0.85f, 0.5f, 0.0f),   // Partner Dist
            new Color(0.9f, 0.4f, 0.1f),    // Partner Hdg
            new Color(1f, 0.3f, 0.3f),      // Enemy R
            new Color(0.9f, 0.2f, 0.5f),    // Enemy F
            new Color(0.8f, 0.15f, 0.35f),  // Enemy Dist
            new Color(0.7f, 0.2f, 0.7f),    // Enemy Hdg
            new Color(0.5f, 0.5f, 1f),      // Mother R
            new Color(0.4f, 0.8f, 1f),      // Mother F
            new Color(0.3f, 0.65f, 0.85f),  // Mother Dist
        };

        public const int OBS_COUNT = 11;

        protected override void Awake()
        {
            base.Awake();
            InitHistory();
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

            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            if (!_initialized || _history == null) return;

            Rect r = GetPixelAdjustedRect();
            float cellW = r.width / columns;
            float cellH = r.height / rows;

            for (int i = 0; i < OBS_COUNT; i++)
            {
                int col = i % columns;
                int row = i / columns;

                // 셀 좌상단 기준 (UI 좌표: 좌하단 원점)
                float cx = r.x + col * cellW + cellPadding;
                float cy = r.y + (rows - 1 - row) * cellH + cellPadding;
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
