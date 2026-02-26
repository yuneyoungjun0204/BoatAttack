using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 스타크래프트 스타일 인터랙티브 전술맵 (MaskableGraphic 기반)
    /// - 마우스 휠: 줌 인/아웃 (커서 위치 기준)
    /// - 마우스 드래그: 팬 이동
    /// - 더블클릭: 뷰 리셋 (모선 중심, 기본 줌)
    /// - 레이더 커버리지 원 오버레이
    /// - 범위 밖 선박 → 경계 클램프 마커
    /// - 섬 지형 렌더링
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class TacticalMapDisplay : MaskableGraphic,
        IScrollHandler, IDragHandler, IBeginDragHandler, IPointerClickHandler
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

        [Header("=== Interactive Controls ===")]
        [Tooltip("마우스 휠 줌 감도")]
        public float zoomSpeed = 0.06f;
        [Tooltip("최소 줌 (축소 한계)")]
        public float minZoom = 0.5f;
        [Tooltip("최대 줌 (확대 한계)")]
        public float maxZoom = 8f;

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
        public Color cornerBracketColor = new Color(0.3f, 0.7f, 1f, 0.8f);
        public Color threatCircleColor = new Color(1f, 0.3f, 0.2f, 0.25f);

        [Header("=== Island ===")]
        public Color islandColor = new Color(0.08f, 0.18f, 0.12f, 0.7f);
        public int terrainSamples = 6;
        public int maxIslandTriangles = 15;
        public int maxTotalIslandVerts = 5000;

        [Header("=== Zoom UI ===")]
        [HideInInspector] public Text zoomText;

        [Header("=== Ship Trails ===")]
        [Tooltip("이동 궤적 표시")]
        public bool showTrails = true;
        [Tooltip("궤적 기록 간격 (초)")]
        public float trailInterval = 0.5f;
        [Tooltip("궤적 최대 포인트 수")]
        public int trailMaxPoints = 30;

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
            public int trailId; // 궤적 추적용 ID (-1 = 없음)
        }

        // 선박 궤적 데이터
        class ShipTrail
        {
            public Vector3[] positions;
            public int writeIndex;
            public int count;
            public Color trailColor;

            public ShipTrail(int maxPoints, Color color)
            {
                positions = new Vector3[maxPoints];
                writeIndex = 0;
                count = 0;
                trailColor = color;
            }

            public void AddPoint(Vector3 pos)
            {
                positions[writeIndex] = pos;
                writeIndex = (writeIndex + 1) % positions.Length;
                if (count < positions.Length) count++;
            }
        }

        List<IslandMeshData> _islandCache = new List<IslandMeshData>();
        List<ShipRenderData> _shipData = new List<ShipRenderData>();
        Dictionary<int, ShipTrail> _trails = new Dictionary<int, ShipTrail>();
        int _totalIslandVerts;
        float _lastTrailTime;
        int _nextTrailId;

        Vector3 _mapWorldCenter;
        float _halfPixel;       // rect 절반 크기 (px)
        float _effectiveRange;  // 기본 맵 범위 (m)
        bool _islandsCached;

        bool _hasWebLine;
        Vector2 _webP1, _webP2;

        // 줌/팬 상태
        float _zoomLevel = 1f;
        Vector2 _panOffset;     // 월드 XZ 오프셋 (미터)
        Vector2 _dragPrevLocal;

        protected override void Start()
        {
            base.Start();
            color = Color.white;
            raycastTarget = true;  // 마우스 이벤트 수신
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

            // 줌 텍스트 업데이트
            if (zoomText != null)
                zoomText.text = $"x{_zoomLevel:F1}";

            SetVerticesDirty();
        }

        #region Interactive Controls

        /// <summary>실제 표시 범위 (줌 적용)</summary>
        float ViewRange => _effectiveRange / Mathf.Max(_zoomLevel, 0.01f);

        /// <summary>월드→로컬 스케일 (줌 적용)</summary>
        float ViewScale => _halfPixel / Mathf.Max(ViewRange, 0.01f);

        /// <summary>뷰 중심 월드 좌표 (팬 적용)</summary>
        Vector3 ViewWorldCenter => new Vector3(
            _mapWorldCenter.x + _panOffset.x,
            _mapWorldCenter.y,
            _mapWorldCenter.z + _panOffset.y);

        public void OnScroll(PointerEventData eventData)
        {
            float scroll = eventData.scrollDelta.y;
            if (Mathf.Abs(scroll) < 0.001f) return;

            // 마우스 위치 → 로컬 좌표
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, eventData.position, eventData.pressEventCamera, out Vector2 localPoint);

            Rect rect = rectTransform.rect;
            float cx = rect.center.x;
            float cy = rect.center.y;

            // 줌 전: 마우스 위치의 월드 좌표
            float oldRange = ViewRange;
            float oldScale = _halfPixel / Mathf.Max(oldRange, 0.01f);
            float worldMouseX = (localPoint.x - cx) / oldScale + _mapWorldCenter.x + _panOffset.x;
            float worldMouseZ = (localPoint.y - cy) / oldScale + _mapWorldCenter.z + _panOffset.y;

            // 줌 변경
            float oldZoom = _zoomLevel;
            _zoomLevel *= (1f + scroll * zoomSpeed);
            _zoomLevel = Mathf.Clamp(_zoomLevel, minZoom, maxZoom);

            // 줌 후: 마우스 아래 월드 좌표가 동일하도록 팬 보정
            float newRange = ViewRange;
            float newScale = _halfPixel / Mathf.Max(newRange, 0.01f);
            float newPanX = worldMouseX - (localPoint.x - cx) / newScale - _mapWorldCenter.x;
            float newPanY = worldMouseZ - (localPoint.y - cy) / newScale - _mapWorldCenter.z;
            _panOffset = new Vector2(newPanX, newPanY);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, eventData.position, eventData.pressEventCamera, out _dragPrevLocal);
        }

        public void OnDrag(PointerEventData eventData)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, eventData.position, eventData.pressEventCamera, out Vector2 localPoint);

            Vector2 delta = localPoint - _dragPrevLocal;
            _dragPrevLocal = localPoint;

            // 픽셀 이동량 → 월드 오프셋
            float pixelToWorld = ViewRange / Mathf.Max(_halfPixel, 0.01f);
            _panOffset.x -= delta.x * pixelToWorld;
            _panOffset.y -= delta.y * pixelToWorld;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // 더블클릭: 뷰 리셋
            if (eventData.clickCount >= 2)
            {
                _zoomLevel = 1f;
                _panOffset = Vector2.zero;
            }
        }

        #endregion

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

            // 3. 테두리 (이중 선)
            DrawRectOutline(vh, cx - halfW + 1f, cy - halfH + 1f,
                            cx + halfW - 1f, cy + halfH - 1f, 2f, borderColor);
            DrawRectOutline(vh, cx - halfW + 4f, cy - halfH + 4f,
                            cx + halfW - 4f, cy + halfH - 4f, 0.8f,
                            new Color(borderColor.r, borderColor.g, borderColor.b, borderColor.a * 0.3f));

            // 4. 코너 브라켓 (HUD 스타일 - 크고 굵게)
            float bLen = Mathf.Min(halfW, halfH) * 0.2f;
            float bw = 3f;
            float l = cx - halfW + 2f, b = cy - halfH + 2f;
            float r = cx + halfW - 2f, t = cy + halfH - 2f;
            // 좌상단
            DrawLine(vh, l, t, l + bLen, t, bw, cornerBracketColor);
            DrawLine(vh, l, t, l, t - bLen, bw, cornerBracketColor);
            // 우상단
            DrawLine(vh, r, t, r - bLen, t, bw, cornerBracketColor);
            DrawLine(vh, r, t, r, t - bLen, bw, cornerBracketColor);
            // 좌하단
            DrawLine(vh, l, b, l + bLen, b, bw, cornerBracketColor);
            DrawLine(vh, l, b, l, b + bLen, bw, cornerBracketColor);
            // 우하단
            DrawLine(vh, r, b, r - bLen, b, bw, cornerBracketColor);
            DrawLine(vh, r, b, r, b + bLen, bw, cornerBracketColor);

            if (envController == null) return;

            if (envController.motherShip != null)
                _mapWorldCenter = envController.motherShip.transform.position;

            // 5. 섬 지형
            DrawIslands(vh, cx, cy, halfW, halfH);

            // 6. 레이더 커버리지 원
            if (showRadarCircle)
                DrawRadarCoverage(vh, cx, cy, halfW, halfH);

            // 7. 선박 궤적
            if (showTrails)
                DrawShipTrails(vh, cx, cy, halfW, halfH);

            // 8. 웹 라인 (3단 글로우 효과)
            if (_hasWebLine)
            {
                bool p1In = IsInRect(_webP1, cx, cy, halfW, halfH);
                bool p2In = IsInRect(_webP2, cx, cy, halfW, halfH);
                if (p1In && p2In)
                {
                    DrawLine(vh, _webP1.x, _webP1.y, _webP2.x, _webP2.y, 12f,
                        new Color(webLineColor.r, webLineColor.g, webLineColor.b, 0.06f));
                    DrawLine(vh, _webP1.x, _webP1.y, _webP2.x, _webP2.y, 5f,
                        new Color(webLineColor.r, webLineColor.g, webLineColor.b, 0.2f));
                    DrawLine(vh, _webP1.x, _webP1.y, _webP2.x, _webP2.y, 2.5f, webLineColor);
                }
            }

            // 9. 선박 마커 (글로우 + 깜빡임)
            float blinkAlpha = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 4f);
            foreach (var ship in _shipData)
            {
                Vector2 rp = WorldToLocal(ship.worldPos, cx, cy);

                if (IsInRect(rp, cx, cy, halfW - 4f, halfH - 4f))
                {
                    Color mc = ship.markerColor;
                    // 적군 깜빡임
                    if (!ship.isMothership && mc.r > 0.5f && mc.g < 0.5f)
                        mc.a *= blinkAlpha;

                    // 마커 글로우 (이중 - 더 강하게)
                    DrawFilledCircle(vh, rp.x, rp.y, ship.size * 1.2f,
                        new Color(mc.r, mc.g, mc.b, 0.08f), 8);
                    DrawFilledCircle(vh, rp.x, rp.y, ship.size * 0.7f,
                        new Color(mc.r, mc.g, mc.b, 0.25f), 8);

                    if (ship.isMothership)
                    {
                        DrawDiamond(vh, rp.x, rp.y, ship.size, mc);
                        DrawCircleOutline(vh, rp.x, rp.y, ship.size * 1.0f, 1.5f,
                            new Color(mc.r, mc.g, mc.b, 0.5f), 16);
                        DrawCircleOutline(vh, rp.x, rp.y, ship.size * 0.6f, 1f,
                            new Color(mc.r, mc.g, mc.b, 0.25f), 12);
                    }
                    else
                    {
                        DrawTriangleMarker(vh, rp.x, rp.y, ship.heading, ship.size, mc);
                    }
                }
                else
                {
                    Vector2 clamped = ClampToRect(rp, cx, cy, halfW - 6f, halfH - 6f);
                    DrawEdgeMarker(vh, clamped.x, clamped.y, ship.markerColor);
                }
            }

            // 10. 줌 레벨 표시
            if (_zoomLevel != 1f)
                DrawZoomIndicator(vh, cx + halfW - 60f, cy - halfH + 8f);

            // 11. 스케일 바 (좌하단)
            DrawScaleBar(vh, cx - halfW + 10f, cy - halfH + 10f);
        }

        #region Data Collection

        void CollectShipData()
        {
            _shipData.Clear();
            _hasWebLine = false;

            bool recordTrail = showTrails && (Time.unscaledTime - _lastTrailTime >= trailInterval);
            if (recordTrail) _lastTrailTime = Time.unscaledTime;

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
                    isMothership = true,
                    trailId = -1
                });
            }

            if (envController.launchZoneManager != null && envController.launchZoneManager.IsInitialized)
            {
                var activeAgents = envController.launchZoneManager.GetActiveAgents();
                foreach (var agent in activeAgents)
                    AddShipAgent(agent, friendlyColor, friendlyMarkerSize, recordTrail);
            }
            else
            {
                AddShipAgent(envController.defenseAgent1, friendlyColor, friendlyMarkerSize, recordTrail);
                AddShipAgent(envController.defenseAgent2, friendlyColor, friendlyMarkerSize, recordTrail);
            }

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
                        AddShip(enemy, enemyColor, enemyMarkerSize, recordTrail);
                }
            }
        }

        int GetOrCreateTrailId(Object obj, Color col)
        {
            int hash = obj.GetInstanceID();
            if (!_trails.ContainsKey(hash))
                _trails[hash] = new ShipTrail(trailMaxPoints, new Color(col.r, col.g, col.b, 0.4f));
            return hash;
        }

        void AddShipAgent(DefenseAgent agent, Color col, float size, bool recordTrail)
        {
            if (agent == null) return;
            int tid = showTrails ? GetOrCreateTrailId(agent, col) : -1;
            if (recordTrail && tid >= 0)
                _trails[tid].AddPoint(agent.transform.position);
            _shipData.Add(new ShipRenderData
            {
                worldPos = agent.transform.position,
                heading = agent.transform.eulerAngles.y,
                markerColor = col,
                size = size,
                isMothership = false,
                trailId = tid
            });
        }

        void AddShip(GameObject ship, Color col, float size, bool recordTrail)
        {
            if (ship == null) return;
            int tid = showTrails ? GetOrCreateTrailId(ship, col) : -1;
            if (recordTrail && tid >= 0)
                _trails[tid].AddPoint(ship.transform.position);
            _shipData.Add(new ShipRenderData
            {
                worldPos = ship.transform.position,
                heading = ship.transform.eulerAngles.y,
                markerColor = col,
                size = size,
                isMothership = false,
                trailId = tid
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
            float range = ViewRange;
            if (range <= 0f || gridSpacing <= 0f) return;
            float scale = ViewScale;

            // 뷰 중심의 월드 좌표
            float viewX = _mapWorldCenter.x + _panOffset.x;
            float viewZ = _mapWorldCenter.z + _panOffset.y;

            // 뷰 가장자리의 월드 좌표 범위
            float worldLeft = viewX - halfW / scale;
            float worldRight = viewX + halfW / scale;
            float worldBottom = viewZ - halfH / scale;
            float worldTop = viewZ + halfH / scale;

            // 줌에 따라 그리드 간격 자동 조정
            float adjustedSpacing = gridSpacing;
            while (adjustedSpacing * scale < 30f) adjustedSpacing *= 2f;  // 너무 촘촘하면 2배
            while (adjustedSpacing * scale > 250f) adjustedSpacing *= 0.5f; // 너무 넓으면 절반

            int startX = Mathf.FloorToInt(worldLeft / adjustedSpacing);
            int endX = Mathf.CeilToInt(worldRight / adjustedSpacing);
            int startZ = Mathf.FloorToInt(worldBottom / adjustedSpacing);
            int endZ = Mathf.CeilToInt(worldTop / adjustedSpacing);

            // 격자선 수 제한
            int maxLines = 60;
            if (endX - startX > maxLines) endX = startX + maxLines;
            if (endZ - startZ > maxLines) endZ = startZ + maxLines;

            // 수직선
            for (int i = startX; i <= endX; i++)
            {
                float worldX = i * adjustedSpacing;
                float px = cx + (worldX - viewX) * scale;
                if (px < cx - halfW || px > cx + halfW) continue;
                DrawLine(vh, px, cy - halfH, px, cy + halfH, 1f, gridColor);
            }

            // 수평선
            for (int i = startZ; i <= endZ; i++)
            {
                float worldZ = i * adjustedSpacing;
                float py = cy + (worldZ - viewZ) * scale;
                if (py < cy - halfH || py > cy + halfH) continue;
                DrawLine(vh, cx - halfW, py, cx + halfW, py, 1f, gridColor);
            }

            // 모선 위치 십자선 (강조)
            Color centerGrid = gridColor * 2f;
            centerGrid.a = Mathf.Min(centerGrid.a, 0.6f);
            float motherPx = cx + (_mapWorldCenter.x - viewX) * scale;
            float motherPy = cy + (_mapWorldCenter.z - viewZ) * scale;
            if (motherPx >= cx - halfW && motherPx <= cx + halfW)
                DrawLine(vh, motherPx, cy - halfH, motherPx, cy + halfH, 1.5f, centerGrid);
            if (motherPy >= cy - halfH && motherPy <= cy + halfH)
                DrawLine(vh, cx - halfW, motherPy, cx + halfW, motherPy, 1.5f, centerGrid);
        }

        #endregion

        #region Draw Radar Coverage

        void DrawRadarCoverage(VertexHelper vh, float cx, float cy, float halfW, float halfH)
        {
            float scale = ViewScale;
            float radarPx = radarRange * scale;

            // 레이더는 모선 중심 (뷰 중심이 아님)
            float viewX = _mapWorldCenter.x + _panOffset.x;
            float viewZ = _mapWorldCenter.z + _panOffset.y;
            float radarCx = cx + (_mapWorldCenter.x - viewX) * scale;
            float radarCy = cy + (_mapWorldCenter.z - viewZ) * scale;

            DrawFilledCircle(vh, radarCx, radarCy, radarPx, radarCircleColor, 32);
            DrawCircleOutline(vh, radarCx, radarCy, radarPx, 1.5f, radarRingColor, 32);
        }

        #endregion

        #region Zoom Indicator

        void DrawZoomIndicator(VertexHelper vh, float x, float y)
        {
            // 맵 우하단에 줌 바 표시
            float barW = 50f;
            float barH = 4f;

            // 배경
            DrawFilledRect(vh, x, y, x + barW, y + barH,
                new Color(0.05f, 0.05f, 0.1f, 0.7f));

            // 줌 레벨 바 (로그 스케일)
            float t = Mathf.InverseLerp(
                Mathf.Log(minZoom), Mathf.Log(maxZoom), Mathf.Log(_zoomLevel));
            float fillW = barW * t;
            DrawFilledRect(vh, x, y, x + fillW, y + barH,
                new Color(0.3f, 0.8f, 1f, 0.6f));

            // 1x 기준선
            float oneT = Mathf.InverseLerp(
                Mathf.Log(minZoom), Mathf.Log(maxZoom), 0f); // log(1) = 0
            float onePx = x + barW * oneT;
            DrawLine(vh, onePx, y - 1f, onePx, y + barH + 1f, 1.5f,
                new Color(1f, 1f, 1f, 0.5f));
        }

        #endregion

        #region Ship Trails

        void DrawShipTrails(VertexHelper vh, float cx, float cy, float halfW, float halfH)
        {
            foreach (var ship in _shipData)
            {
                if (ship.trailId < 0 || !_trails.ContainsKey(ship.trailId)) continue;
                var trail = _trails[ship.trailId];
                if (trail.count < 2) continue;

                int vertBudget = 64000 - vh.currentVertCount;
                if (vertBudget < trail.count * 4) continue;

                for (int s = 1; s < trail.count; s++)
                {
                    int idx0 = (trail.writeIndex - trail.count + s - 1 + trail.positions.Length) % trail.positions.Length;
                    int idx1 = (trail.writeIndex - trail.count + s + trail.positions.Length) % trail.positions.Length;

                    Vector2 p0 = WorldToLocal(trail.positions[idx0], cx, cy);
                    Vector2 p1 = WorldToLocal(trail.positions[idx1], cx, cy);

                    if (!IsInRect(p0, cx, cy, halfW, halfH) && !IsInRect(p1, cx, cy, halfW, halfH))
                        continue;

                    float t = (float)s / trail.count;
                    Color trailCol = trail.trailColor;
                    trailCol.a *= t * t; // 끝으로 갈수록 진해짐
                    float width = 0.5f + t * 1.5f;

                    DrawLine(vh, p0.x, p0.y, p1.x, p1.y, width, trailCol);
                }
            }
        }

        #endregion

        #region Scale Bar

        void DrawScaleBar(VertexHelper vh, float x, float y)
        {
            float range = ViewRange;
            float scale = ViewScale;

            // 적절한 단위 찾기
            float[] niceValues = { 50f, 100f, 200f, 500f, 1000f, 2000f, 5000f };
            float barWorldLen = range * 0.2f;
            float bestVal = niceValues[0];
            foreach (float v in niceValues)
            {
                if (v <= barWorldLen) bestVal = v;
                else break;
            }

            float barPx = bestVal * scale;
            if (barPx < 10f) return;

            Color barCol = new Color(0.5f, 0.7f, 0.9f, 0.5f);

            // 바 본체
            DrawFilledRect(vh, x, y, x + barPx, y + 3f, barCol);
            // 좌우 끝 세로선
            DrawLine(vh, x, y - 1f, x, y + 5f, 1f, barCol);
            DrawLine(vh, x + barPx, y - 1f, x + barPx, y + 5f, 1f, barCol);
        }

        #endregion

        #region Coordinate Conversion

        Vector2 WorldToLocal(Vector3 wp, float cx, float cy)
        {
            float scale = ViewScale;
            float viewX = _mapWorldCenter.x + _panOffset.x;
            float viewZ = _mapWorldCenter.z + _panOffset.y;
            return new Vector2(
                cx + (wp.x - viewX) * scale,
                cy + (wp.z - viewZ) * scale);
        }

        Vector2 XZToLocal(Vector2 xz, float cx, float cy)
        {
            float scale = ViewScale;
            float viewX = _mapWorldCenter.x + _panOffset.x;
            float viewZ = _mapWorldCenter.z + _panOffset.y;
            return new Vector2(
                cx + (xz.x - viewX) * scale,
                cy + (xz.y - viewZ) * scale);
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
            DrawLine(vh, l, b, r, b, w, col);
            DrawLine(vh, r, b, r, t, w, col);
            DrawLine(vh, r, t, l, t, w, col);
            DrawLine(vh, l, t, l, b, w, col);
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
