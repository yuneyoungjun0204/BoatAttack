using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 스타크래프트 스타일 사각형 전술맵 (MaskableGraphic 기반)
    /// - 레이더 범위의 N배 넓은 시야
    /// - 사각형 좌표계 + 격자
    /// - 레이더 커버리지 원 오버레이
    /// - 범위 밖 선박 → 경계 클램프 마커
    /// - 섬 지형 렌더링
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class TacticalMapDisplay : MaskableGraphic
    {
        [Header("=== References ===")]
        public DefenseEnvController envController;

        [Header("=== Map Settings ===")]
        [Tooltip("맵 표시 범위 (미터). 중심에서 가장자리까지의 거리")]
        public float mapRange = 3000f;

        [Tooltip("레이더 범위 배수로 자동 설정 (0이면 mapRange 직접 사용)")]
        public float mapScaleFromRadar = 3f;

        [Tooltip("레이더 커버리지 반경 (미터)")]
        public float radarRange = 1000f;

        [Tooltip("레이더 범위 원 표시")]
        public bool showRadarCircle = true;

        [Header("=== Grid ===")]
        [Tooltip("격자 간격 (미터)")]
        public float gridSpacing = 500f;

        [Header("=== Marker Size ===")]
        public float friendlyMarkerSize = 12f;
        public float enemyMarkerSize = 10f;
        public float mothershipMarkerSize = 16f;
        public float edgeMarkerSize = 6f;

        [Header("=== Colors ===")]
        public Color bgColor = new Color(0.01f, 0.02f, 0.06f, 0.95f);
        public Color gridColor = new Color(0.08f, 0.15f, 0.25f, 0.4f);
        public Color borderColor = new Color(0.15f, 0.35f, 0.5f, 0.8f);
        public Color radarCircleColor = new Color(0.1f, 0.4f, 0.15f, 0.25f);
        public Color radarRingColor = new Color(0.15f, 0.5f, 0.2f, 0.5f);
        public Color friendlyColor = new Color(0.2f, 0.6f, 1f, 1f);
        public Color enemyColor = new Color(1f, 0.25f, 0.2f, 1f);
        public Color mothershipColor = new Color(0.85f, 0.85f, 1f, 1f);
        public Color webLineColor = new Color(0.3f, 1f, 0.5f, 0.5f);
        public Color fogColor = new Color(0.0f, 0.0f, 0.02f, 0.4f);

        [Header("=== Island ===")]
        public Color islandColor = new Color(0.08f, 0.18f, 0.12f, 0.7f);
        public int terrainSamples = 6;
        public int maxIslandTriangles = 15;
        public int maxTotalIslandVerts = 5000;

        // ── Internal ──
        struct IslandMeshData
        {
            public Vector2[] vertices;
            public int[] triangles;
        }

        struct ShipRenderData
        {
            public Vector3 worldPos;
            public float heading;
            public Color markerColor;
            public float size;
            public bool isMothership;
        }

        List<IslandMeshData> _islandCache = new List<IslandMeshData>();
        List<ShipRenderData> _shipData = new List<ShipRenderData>();
        int _totalIslandVerts;

        Vector3 _mapWorldCenter;
        float _halfPixel;       // rect 절반 크기 (px)
        float _effectiveRange;  // 실제 맵 범위 (m)
        bool _islandsCached;

        bool _hasWebLine;
        Vector2 _webP1, _webP2;

        protected override void Start()
        {
            base.Start();
            color = Color.white;
            raycastTarget = false;
            CacheIslands();
        }

        void LateUpdate()
        {
            if (envController == null)
            {
                SetVerticesDirty();
                return;
            }

            // 레이더 범위 기반 자동 스케일
            if (mapScaleFromRadar > 0f)
                _effectiveRange = radarRange * mapScaleFromRadar;
            else
                _effectiveRange = mapRange;

            if (!_islandsCached) CacheIslands();
            CollectShipData();
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect rect = rectTransform.rect;
            _halfPixel = Mathf.Min(rect.width, rect.height) * 0.5f;
            float cx = rect.center.x;
            float cy = rect.center.y;
            float halfW = rect.width * 0.5f;
            float halfH = rect.height * 0.5f;

            if (_halfPixel < 1f) return;

            // 1. 배경 사각형
            DrawFilledRect(vh, cx - halfW, cy - halfH, cx + halfW, cy + halfH, bgColor);

            // 2. 격자선
            DrawGrid(vh, cx, cy, halfW, halfH);

            // 3. 테두리
            DrawRectOutline(vh, cx - halfW + 1f, cy - halfH + 1f,
                            cx + halfW - 1f, cy + halfH - 1f, 2f, borderColor);

            if (envController == null) return;

            if (envController.motherShip != null)
                _mapWorldCenter = envController.motherShip.transform.position;

            // 4. 섬 지형
            DrawIslands(vh, cx, cy, halfW, halfH);

            // 5. 레이더 커버리지 원
            if (showRadarCircle)
                DrawRadarCoverage(vh, cx, cy, halfW, halfH);

            // 6. 웹 라인
            if (_hasWebLine)
            {
                bool p1In = IsInRect(_webP1, cx, cy, halfW, halfH);
                bool p2In = IsInRect(_webP2, cx, cy, halfW, halfH);
                if (p1In && p2In)
                    DrawLine(vh, _webP1.x, _webP1.y, _webP2.x, _webP2.y, 2f, webLineColor);
            }

            // 7. 선박 마커
            foreach (var ship in _shipData)
            {
                Vector2 rp = WorldToLocal(ship.worldPos, cx, cy);

                if (IsInRect(rp, cx, cy, halfW - 4f, halfH - 4f))
                {
                    // 범위 내 → 일반 마커
                    if (ship.isMothership)
                        DrawDiamond(vh, rp.x, rp.y, ship.size, ship.markerColor);
                    else
                        DrawTriangleMarker(vh, rp.x, rp.y, ship.heading, ship.size, ship.markerColor);
                }
                else
                {
                    // 범위 밖 → 경계 클램프 작은 점
                    Vector2 clamped = ClampToRect(rp, cx, cy, halfW - 6f, halfH - 6f);
                    DrawEdgeMarker(vh, clamped.x, clamped.y, ship.markerColor);
                }
            }
        }

        #region Data Collection

        void CollectShipData()
        {
            _shipData.Clear();
            _hasWebLine = false;

            Rect rect = rectTransform.rect;
            float cx = rect.center.x;
            float cy = rect.center.y;

            if (envController.motherShip != null)
            {
                _shipData.Add(new ShipRenderData
                {
                    worldPos = envController.motherShip.transform.position,
                    heading = envController.motherShip.transform.eulerAngles.y,
                    markerColor = mothershipColor,
                    size = mothershipMarkerSize,
                    isMothership = true
                });
            }

            AddShipAgent(envController.defenseAgent1, friendlyColor, friendlyMarkerSize);
            AddShipAgent(envController.defenseAgent2, friendlyColor, friendlyMarkerSize);

            if (envController.defenseAgent1 != null && envController.defenseAgent2 != null)
            {
                _webP1 = WorldToLocal(envController.defenseAgent1.transform.position, cx, cy);
                _webP2 = WorldToLocal(envController.defenseAgent2.transform.position, cx, cy);
                _hasWebLine = true;
            }

            if (envController.enemyShips != null)
            {
                foreach (var enemy in envController.enemyShips)
                {
                    if (enemy != null && enemy.activeInHierarchy)
                        AddShip(enemy, enemyColor, enemyMarkerSize);
                }
            }
        }

        void AddShipAgent(DefenseAgent agent, Color col, float size)
        {
            if (agent == null) return;
            _shipData.Add(new ShipRenderData
            {
                worldPos = agent.transform.position,
                heading = agent.transform.eulerAngles.y,
                markerColor = col,
                size = size,
                isMothership = false
            });
        }

        void AddShip(GameObject ship, Color col, float size)
        {
            if (ship == null) return;
            _shipData.Add(new ShipRenderData
            {
                worldPos = ship.transform.position,
                heading = ship.transform.eulerAngles.y,
                markerColor = col,
                size = size,
                isMothership = false
            });
        }

        #endregion

        #region Island Cache (RadarDisplay 동일 패턴)

        void CacheIslands()
        {
            _islandCache.Clear();
            _islandsCached = true;
            _totalIslandVerts = 0;

            GameObject[] islands = null;
            try { islands = GameObject.FindGameObjectsWithTag("Island"); }
            catch (UnityException) { return; }
            if (islands == null || islands.Length == 0) return;

            foreach (var island in islands)
            {
                if (_totalIslandVerts >= maxTotalIslandVerts) break;

                var terrain = island.GetComponent<Terrain>();
                if (terrain != null)
                {
                    CacheTerrainIsland(terrain);
                    continue;
                }

                var meshFilters = island.GetComponentsInChildren<MeshFilter>();
                if (meshFilters.Length > 0)
                {
                    foreach (var mf in meshFilters)
                    {
                        if (_totalIslandVerts >= maxTotalIslandVerts) break;
                        if (mf.sharedMesh != null) CacheMeshIsland(mf);
                    }
                }
                else
                {
                    var renderer = island.GetComponentInChildren<Renderer>();
                    if (renderer != null)
                        CacheBoundsIsland(renderer.bounds);
                }
            }
        }

        void CacheMeshIsland(MeshFilter mf)
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) return;

            if (!mesh.isReadable)
            {
                var renderer = mf.GetComponent<Renderer>();
                if (renderer != null) CacheBoundsIsland(renderer.bounds);
                return;
            }

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            var tf = mf.transform;

            int totalTris = tris.Length / 3;
            int step = Mathf.Max(1, totalTris / maxIslandTriangles);

            var xzList = new List<Vector2>();
            var triList = new List<int>();
            var map = new Dictionary<int, int>();

            for (int t = 0; t < totalTris; t += step)
            {
                if (_totalIslandVerts + xzList.Count > maxTotalIslandVerts) break;
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                triList.Add(MapVert(i0, verts, tf, xzList, map));
                triList.Add(MapVert(i1, verts, tf, xzList, map));
                triList.Add(MapVert(i2, verts, tf, xzList, map));
            }

            if (xzList.Count > 0)
            {
                _islandCache.Add(new IslandMeshData { vertices = xzList.ToArray(), triangles = triList.ToArray() });
                _totalIslandVerts += xzList.Count;
            }
        }

        void CacheBoundsIsland(Bounds b)
        {
            const int seg = 10;
            if (_totalIslandVerts + seg + 1 > maxTotalIslandVerts) return;

            float bx = (b.min.x + b.max.x) * 0.5f;
            float bz = (b.min.z + b.max.z) * 0.5f;
            float rx = (b.max.x - b.min.x) * 0.5f;
            float rz = (b.max.z - b.min.z) * 0.5f;
            if (rx < 1f && rz < 1f) return;

            var verts = new Vector2[seg + 1];
            verts[0] = new Vector2(bx, bz);
            float seed = (bx * 0.13f + bz * 0.07f) % 6.28f;

            for (int i = 0; i < seg; i++)
            {
                float angle = i * Mathf.PI * 2f / seg;
                float wobble = 0.82f + 0.18f * Mathf.Sin(angle * 3f + seed)
                                      + 0.08f * Mathf.Cos(angle * 5f + seed * 1.7f);
                verts[i + 1] = new Vector2(
                    bx + Mathf.Cos(angle) * rx * wobble,
                    bz + Mathf.Sin(angle) * rz * wobble);
            }

            var tris = new int[seg * 3];
            for (int i = 0; i < seg; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = (i + 1) % seg + 1;
            }

            _islandCache.Add(new IslandMeshData { vertices = verts, triangles = tris });
            _totalIslandVerts += verts.Length;
        }

        int MapVert(int idx, Vector3[] verts, Transform tf, List<Vector2> list, Dictionary<int, int> map)
        {
            if (map.TryGetValue(idx, out int i)) return i;
            Vector3 wp = tf.TransformPoint(verts[idx]);
            int ni = list.Count;
            list.Add(new Vector2(wp.x, wp.z));
            map[idx] = ni;
            return ni;
        }

        void CacheTerrainIsland(Terrain terrain)
        {
            var td = terrain.terrainData;
            var pos = terrain.transform.position;
            int n = Mathf.Clamp(terrainSamples, 4, 20);

            if (_totalIslandVerts + n * n > maxTotalIslandVerts)
                n = Mathf.Max(4, (int)Mathf.Sqrt(maxTotalIslandVerts - _totalIslandVerts));

            bool[,] isLand = new bool[n, n];
            Vector2[] gridVerts = new Vector2[n * n];
            int landCount = 0;

            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    float nx = (float)x / (n - 1);
                    float nz = (float)z / (n - 1);
                    gridVerts[z * n + x] = new Vector2(
                        pos.x + nx * td.size.x,
                        pos.z + nz * td.size.z);
                    isLand[x, z] = td.GetInterpolatedHeight(nx, nz) > 0.5f;
                    if (isLand[x, z]) landCount++;
                }
            }
            if (landCount == 0) return;

            var tris = new List<int>();
            for (int z = 0; z < n - 1; z++)
            {
                for (int x = 0; x < n - 1; x++)
                {
                    if (!isLand[x, z] && !isLand[x + 1, z] &&
                        !isLand[x, z + 1] && !isLand[x + 1, z + 1])
                        continue;
                    int bl = z * n + x;
                    int br = z * n + x + 1;
                    int tl = (z + 1) * n + x;
                    int tr = (z + 1) * n + x + 1;
                    tris.Add(bl); tris.Add(tl); tris.Add(br);
                    tris.Add(br); tris.Add(tl); tris.Add(tr);
                }
            }

            if (tris.Count > 0)
            {
                _islandCache.Add(new IslandMeshData { vertices = gridVerts, triangles = tris.ToArray() });
                _totalIslandVerts += gridVerts.Length;
            }
        }

        #endregion

        #region Draw Islands

        void DrawIslands(VertexHelper vh, float cx, float cy, float halfW, float halfH)
        {
            int vertBudget = 64000 - vh.currentVertCount;

            foreach (var island in _islandCache)
            {
                if (island.vertices.Length > vertBudget) break;

                int baseIdx = vh.currentVertCount;
                foreach (var v in island.vertices)
                {
                    Vector2 rp = XZToLocal(v, cx, cy);
                    AddVert(vh, rp.x, rp.y, islandColor);
                }
                for (int i = 0; i < island.triangles.Length; i += 3)
                {
                    vh.AddTriangle(
                        baseIdx + island.triangles[i],
                        baseIdx + island.triangles[i + 1],
                        baseIdx + island.triangles[i + 2]);
                }
                vertBudget -= island.vertices.Length;
            }
        }

        #endregion

        #region Draw Grid

        void DrawGrid(VertexHelper vh, float cx, float cy, float halfW, float halfH)
        {
            if (_effectiveRange <= 0f || gridSpacing <= 0f) return;

            float scale = _halfPixel / _effectiveRange;
            int lines = Mathf.CeilToInt(_effectiveRange / gridSpacing);

            // 수직선
            for (int i = -lines; i <= lines; i++)
            {
                float px = cx + i * gridSpacing * scale;
                if (px < cx - halfW || px > cx + halfW) continue;
                DrawLine(vh, px, cy - halfH, px, cy + halfH, 1f, gridColor);
            }

            // 수평선
            for (int i = -lines; i <= lines; i++)
            {
                float py = cy + i * gridSpacing * scale;
                if (py < cy - halfH || py > cy + halfH) continue;
                DrawLine(vh, cx - halfW, py, cx + halfW, py, 1f, gridColor);
            }

            // 중심 십자선 (강조)
            Color centerGrid = gridColor * 2f;
            centerGrid.a = Mathf.Min(centerGrid.a, 0.6f);
            DrawLine(vh, cx - halfW, cy, cx + halfW, cy, 1.5f, centerGrid);
            DrawLine(vh, cx, cy - halfH, cx, cy + halfH, 1.5f, centerGrid);
        }

        #endregion

        #region Draw Radar Coverage

        void DrawRadarCoverage(VertexHelper vh, float cx, float cy, float halfW, float halfH)
        {
            float scale = _halfPixel / _effectiveRange;
            float radarPx = radarRange * scale;

            // 반투명 채우기 원 (레이더 커버 영역)
            DrawFilledCircle(vh, cx, cy, radarPx, radarCircleColor, 32);

            // 테두리 링
            DrawCircleOutline(vh, cx, cy, radarPx, 1.5f, radarRingColor, 32);
        }

        #endregion

        #region Coordinate Conversion

        Vector2 WorldToLocal(Vector3 wp, float cx, float cy)
        {
            float scale = _halfPixel / _effectiveRange;
            return new Vector2(
                cx + (wp.x - _mapWorldCenter.x) * scale,
                cy + (wp.z - _mapWorldCenter.z) * scale);
        }

        Vector2 XZToLocal(Vector2 xz, float cx, float cy)
        {
            float scale = _halfPixel / _effectiveRange;
            return new Vector2(
                cx + (xz.x - _mapWorldCenter.x) * scale,
                cy + (xz.y - _mapWorldCenter.z) * scale);
        }

        bool IsInRect(Vector2 p, float cx, float cy, float halfW, float halfH)
        {
            return p.x >= cx - halfW && p.x <= cx + halfW &&
                   p.y >= cy - halfH && p.y <= cy + halfH;
        }

        Vector2 ClampToRect(Vector2 p, float cx, float cy, float halfW, float halfH)
        {
            return new Vector2(
                Mathf.Clamp(p.x, cx - halfW, cx + halfW),
                Mathf.Clamp(p.y, cy - halfH, cy + halfH));
        }

        #endregion

        #region Drawing Primitives

        void DrawTriangleMarker(VertexHelper vh, float px, float py, float heading, float size, Color col)
        {
            float rad = -heading * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

            Vector2 tip = Rot(0, size * 0.6f, cos, sin);
            Vector2 left = Rot(-size * 0.35f, -size * 0.35f, cos, sin);
            Vector2 right = Rot(size * 0.35f, -size * 0.35f, cos, sin);

            int idx = vh.currentVertCount;
            AddVert(vh, px + tip.x, py + tip.y, col);
            AddVert(vh, px + left.x, py + left.y, col * 0.7f);
            AddVert(vh, px + right.x, py + right.y, col * 0.7f);
            vh.AddTriangle(idx, idx + 1, idx + 2);
        }

        void DrawDiamond(VertexHelper vh, float px, float py, float size, Color col)
        {
            float h = size * 0.5f;
            int idx = vh.currentVertCount;
            AddVert(vh, px, py + h, col);
            AddVert(vh, px + h, py, col);
            AddVert(vh, px, py - h, col);
            AddVert(vh, px - h, py, col);
            vh.AddTriangle(idx, idx + 1, idx + 2);
            vh.AddTriangle(idx, idx + 2, idx + 3);
        }

        void DrawEdgeMarker(VertexHelper vh, float px, float py, Color col)
        {
            // 경계에 작은 사각 점
            float s = edgeMarkerSize * 0.5f;
            Color dimCol = col * 0.6f;
            dimCol.a = 0.8f;
            DrawFilledRect(vh, px - s, py - s, px + s, py + s, dimCol);
        }

        Vector2 Rot(float x, float y, float cos, float sin)
        {
            return new Vector2(x * cos - y * sin, x * sin + y * cos);
        }

        void DrawFilledCircle(VertexHelper vh, float cx, float cy, float r, Color col, int seg)
        {
            int ci = AddVert(vh, cx, cy, col);
            int fi = vh.currentVertCount;
            for (int i = 0; i <= seg; i++)
            {
                float a = (float)i / seg * Mathf.PI * 2f;
                AddVert(vh, cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r, col);
                if (i > 0) vh.AddTriangle(ci, fi + i - 1, fi + i);
            }
        }

        void DrawCircleOutline(VertexHelper vh, float cx, float cy, float r, float w, Color col, int seg)
        {
            for (int i = 0; i < seg; i++)
            {
                float a1 = (float)i / seg * Mathf.PI * 2f;
                float a2 = (float)(i + 1) / seg * Mathf.PI * 2f;
                DrawLine(vh,
                    cx + Mathf.Cos(a1) * r, cy + Mathf.Sin(a1) * r,
                    cx + Mathf.Cos(a2) * r, cy + Mathf.Sin(a2) * r,
                    w, col);
            }
        }

        void DrawRectOutline(VertexHelper vh, float l, float b, float r, float t, float w, Color col)
        {
            DrawLine(vh, l, b, r, b, w, col); // 하단
            DrawLine(vh, r, b, r, t, w, col); // 우측
            DrawLine(vh, r, t, l, t, w, col); // 상단
            DrawLine(vh, l, t, l, b, w, col); // 좌측
        }

        void DrawLine(VertexHelper vh, float x1, float y1, float x2, float y2, float w, Color col)
        {
            float dx = x2 - x1, dy = y2 - y1;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;
            float nx = -dy / len * w * 0.5f, ny = dx / len * w * 0.5f;

            int idx = vh.currentVertCount;
            AddVert(vh, x1 + nx, y1 + ny, col);
            AddVert(vh, x1 - nx, y1 - ny, col);
            AddVert(vh, x2 - nx, y2 - ny, col);
            AddVert(vh, x2 + nx, y2 + ny, col);
            vh.AddTriangle(idx, idx + 1, idx + 2);
            vh.AddTriangle(idx, idx + 2, idx + 3);
        }

        void DrawFilledRect(VertexHelper vh, float l, float b, float r, float t, Color col)
        {
            int idx = vh.currentVertCount;
            AddVert(vh, l, b, col);
            AddVert(vh, r, b, col);
            AddVert(vh, r, t, col);
            AddVert(vh, l, t, col);
            vh.AddTriangle(idx, idx + 1, idx + 2);
            vh.AddTriangle(idx, idx + 2, idx + 3);
        }

        int AddVert(VertexHelper vh, float x, float y, Color col)
        {
            int idx = vh.currentVertCount;
            UIVertex v = UIVertex.simpleVert;
            v.position = new Vector3(x, y, 0);
            v.color = col;
            vh.AddVert(v);
            return idx;
        }

        #endregion
    }
}
