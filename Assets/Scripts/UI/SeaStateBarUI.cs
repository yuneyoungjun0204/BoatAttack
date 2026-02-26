using UnityEngine;
using UnityEngine.UI;

namespace BoatAttack
{
    /// <summary>
    /// 해상 상태 바 UI (MaskableGraphic 프로시저럴)
    /// 세그먼트 바 + 색상 그라데이션 (Calm→Moderate→Storm)
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class SeaStateBarUI : MaskableGraphic
    {
        [Header("=== Sea State Data ===")]
        [Range(0, 5)]
        public float value = 0f;
        public float maxValue = 5f;

        [Header("=== Colors ===")]
        public Color bgColor = new Color(0.08f, 0.08f, 0.12f, 0.8f);
        public Color calmColor = new Color(0.2f, 0.8f, 0.3f, 1f);
        public Color moderateColor = new Color(1f, 0.85f, 0.2f, 1f);
        public Color stormColor = new Color(1f, 0.2f, 0.15f, 1f);
        public Color borderColor = new Color(0.3f, 0.3f, 0.4f, 0.5f);

        [Header("=== Style ===")]
        public int segmentCount = 10;
        public float barPadding = 3f;

        protected override void Start()
        {
            base.Start();
            color = Color.white;
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect rect = rectTransform.rect;
            float left = rect.xMin, right = rect.xMax;
            float bottom = rect.yMin, top = rect.yMax;
            float w = right - left;
            float h = top - bottom;

            // 배경
            DrawRect(vh, left, bottom, right, top, bgColor);

            // 이중 테두리
            float bw = 1f;
            Color outerBorder = new Color(borderColor.r * 1.5f, borderColor.g * 1.5f, borderColor.b * 1.5f, borderColor.a);
            DrawRect(vh, left, bottom, right, bottom + bw, outerBorder);
            DrawRect(vh, left, top - bw, right, top, outerBorder);
            DrawRect(vh, left, bottom, left + bw, top, outerBorder);
            DrawRect(vh, right - bw, bottom, right, top, outerBorder);
            // 내부 테두리 (은은하게)
            float ibw = 0.5f;
            float ip = 2f;
            DrawRect(vh, left + ip, bottom + ip, right - ip, bottom + ip + ibw,
                new Color(borderColor.r, borderColor.g, borderColor.b, borderColor.a * 0.3f));
            DrawRect(vh, left + ip, top - ip - ibw, right - ip, top - ip,
                new Color(borderColor.r, borderColor.g, borderColor.b, borderColor.a * 0.3f));

            // 세그먼트 바
            float fill = Mathf.Clamp01(value / maxValue);
            float barL = left + barPadding;
            float barB = bottom + barPadding;
            float barT = top - barPadding;
            float totalW = w - barPadding * 2;
            float segW = totalW / segmentCount;
            float gap = 2f;
            int filledSegs = Mathf.CeilToInt(fill * segmentCount);

            // 빈 세그먼트 (비활성 슬롯 표시)
            for (int i = filledSegs; i < segmentCount; i++)
            {
                float sl = barL + i * segW + gap * 0.5f;
                float sr = barL + (i + 1) * segW - gap * 0.5f;
                if (sl >= sr) continue;
                DrawRect(vh, sl, barB, sr, barT,
                    new Color(borderColor.r * 0.3f, borderColor.g * 0.3f, borderColor.b * 0.3f, 0.2f));
            }

            // 활성 세그먼트 (글로우 + 메인)
            for (int i = 0; i < filledSegs; i++)
            {
                float t = (float)i / segmentCount;
                Color segColor = t < 0.4f
                    ? Color.Lerp(calmColor, moderateColor, t / 0.4f)
                    : Color.Lerp(moderateColor, stormColor, (t - 0.4f) / 0.6f);

                float sl = barL + i * segW + gap * 0.5f;
                float sr = barL + (i + 1) * segW - gap * 0.5f;
                sr = Mathf.Min(sr, barL + totalW * fill);
                if (sl >= sr) continue;

                // 글로우 (세그먼트보다 약간 크게 - 강하게)
                Color glowCol = new Color(segColor.r, segColor.g, segColor.b, 0.25f);
                DrawRect(vh, sl - 2f, barB - 2f, sr + 2f, barT + 2f, glowCol);

                // 메인 세그먼트
                DrawRect(vh, sl, barB, sr, barT, segColor);

                // 상단 하이라이트 (반사광 - 더 밝게)
                float hlH = (barT - barB) * 0.3f;
                Color hlCol = new Color(1f, 1f, 1f, 0.2f);
                DrawRect(vh, sl + 1f, barT - hlH, sr - 1f, barT - 1f, hlCol);
            }

            // 현재값 인디케이터 (삼각형 포인터)
            if (fill > 0f)
            {
                float ptrX = barL + totalW * fill;
                float ptrSize = h * 0.2f;
                Color ptrCol = fill < 0.4f ? calmColor :
                               fill < 0.7f ? moderateColor : stormColor;
                int pi = vh.currentVertCount;
                UIVertex pv = UIVertex.simpleVert;
                pv.color = ptrCol;
                pv.position = new Vector3(ptrX, top + 1f, 0); vh.AddVert(pv);
                pv.position = new Vector3(ptrX - ptrSize * 0.5f, top + ptrSize, 0); vh.AddVert(pv);
                pv.position = new Vector3(ptrX + ptrSize * 0.5f, top + ptrSize, 0); vh.AddVert(pv);
                vh.AddTriangle(pi, pi + 1, pi + 2);
            }
        }

        public void SetValue(float v)
        {
            value = v;
            SetVerticesDirty();
        }

        public string GetSeaStateLabel()
        {
            if (value < 0.5f) return "Calm";
            if (value < 1.0f) return "Light";
            if (value < 2.0f) return "Moderate";
            if (value < 3.0f) return "Rough";
            return "Storm";
        }

        void DrawRect(VertexHelper vh, float l, float b, float r, float t, Color col)
        {
            int idx = vh.currentVertCount;
            UIVertex vert = UIVertex.simpleVert;
            vert.color = col;
            vert.position = new Vector3(l, b, 0); vh.AddVert(vert);
            vert.position = new Vector3(r, b, 0); vh.AddVert(vert);
            vert.position = new Vector3(r, t, 0); vh.AddVert(vert);
            vert.position = new Vector3(l, t, 0); vh.AddVert(vert);
            vh.AddTriangle(idx, idx + 1, idx + 2);
            vh.AddTriangle(idx, idx + 2, idx + 3);
        }
    }
}
