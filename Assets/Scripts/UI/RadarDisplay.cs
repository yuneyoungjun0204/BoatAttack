using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// CIC 스타일 원형 레이더 (MaskableGraphic 기반)
    /// - 삼각형 선박 마커 (방향 표시)
    /// - 섬 지형 렌더링 (Island 태그) - 공유 버텍스 그리드
    /// - 스위프 잔상(afterglow) 효과
    /// - 방위각 눈금 + 거리 링 라벨
    /// - 적 마커 깜빡임 경고
    /// - 65000 버텍스 제한 준수
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class RadarDisplay : MaskableGraphic
    {
        [Header("=== References ===")]
        public DefenseEnvController envController;

        [Header("=== Radar Settings ===")]
        [Tooltip("레이더 탐지 반경 (미터)")]
        public float radarRange = 1000f;

        [Tooltip("시작 시 radarRange에 곱할 배수 (1=그대로, 6=6배 확장). Inspector radarRange 값은 유지됨")]
        public float rangeMultiplier = 1f;

        [Tooltip("자동 범위 맞춤")]
        public bool autoFitRange = false;

        [Range(1.1f, 2.0f)]
        public float autoFitMargin = 1.3f;

        [Header("=== Marker Size ===")]
        public float friendlyMarkerSize = 16f;
        public float enemyMarkerSize = 14f;
        public float mothershipMarkerSize = 20f;

        [Header("=== Colors ===")]
        public Color friendlyColor = new Color(0.2f, 0.85f, 1f, 1f);
        public Color enemyColor = new Color(1f, 0.25f, 0.2f, 1f);
        public Color mothershipColor = new Color(0.85f, 0.85f, 1f, 1f);
        public Color webLineColor = new Color(0.3f, 1f, 0.5f, 0.6f);
        [Tooltip("아군-적군 매칭 라인 색상")]
        public Color matchingLineColor = new Color(1f, 0.9f, 0.3f, 0.5f);
        public Color ringColor = new Color(0.15f, 0.55f, 0.2f, 0.6f);
        public Color sweepColor = new Color(0.2f, 1f, 0.3f, 0.5f);
        public Color gridColor = new Color(0.1f, 0.3f, 0.12f, 0.4f);
        public Color bgCircleColor = new Color(0.01f, 0.03f, 0.01f, 0.97f);
        public Color islandColor = new Color(0.12f, 0.25f, 0.1f, 0.7f);
        public Color bearingTickColor = new Color(0.3f, 0.8f, 0.4f, 0.85f);
        public Color outerGlowColor = new Color(0.1f, 0.6f, 0.2f, 0.5f);

        [Header("=== Sweep ===")]
        public float sweepSpeed = 60f;
        [Tooltip("스위프 잔상 각도 (도)")]
        public float sweepTrailAngle = 60f;
        [Tooltip("잔상 세그먼트 수")]
        public int sweepTrailSegments = 8;

        [Header("=== Range Rings ===")]
        public int ringCount = 4;

        [Header("=== Island ===")]
        [Tooltip("씬에 배치한 섬 루트 오브젝트들을 직접 할당. 설정 시 Tag/Layer 탐색보다 우선")]
        public GameObject[] islandObjects;
        [Tooltip("섬 레이어 마스크. islandObjects 미설정 시 Layer로 탐색")]
        public LayerMask islandLayerMask = 0;
        [Tooltip("지형 샘플 해상도 (낮을수록 가벼움)")]
        public int terrainSamples = 6;
        [Tooltip("섬 메시 최대 삼각형 수 (개별)")]
        public int maxIslandTriangles = 15;
        [Tooltip("전체 섬 최대 버텍스 수")]
        public int maxTotalIslandVerts = 5000;

        // 공유 버텍스 기반 섬 데이터
        struct IslandMeshData
        {
            public Vector2[] vertices;  // XZ 월드좌표
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

        struct MatchingLineData
        {
            public Vector3 allyCenter;  // 쌍 중심 월드 좌표
            public Vector3 enemyPos;    // 타겟 적군 월드 좌표
        }

        List<IslandMeshData> _islandCache = new List<IslandMeshData>();
        List<ShipRenderData> _shipData = new List<ShipRenderData>();
        List<MatchingLineData> _matchingLines = new List<MatchingLineData>();
        int _totalIslandVerts;

        float _sweepAngle;
        Vector3 _radarWorldCenter;
        float _pixelRadius;
        bool _islandsCached;
        float _nextIslandRetryTime = 0f;
        bool _hasWebLine;
        Vector2 _webP1, _webP2;

        // 레이더 기본 요소 버텍스 예산 (원, 격자, 링, 스위프, 마커 등)
        const int RADAR_BASE_VERTS = 2000;

        protected override void Start()
        {
            base.Start();
            color = Color.white;
            raycastTarget = false;

            if (rangeMultiplier > 0f && !Mathf.Approximately(rangeMultiplier, 1f))
                radarRange *= rangeMultiplier;

            CacheIslands();
        }

        void LateUpdate()
        {
            _sweepAngle += sweepSpeed * Time.unscaledDeltaTime;
            if (_sweepAngle >= 360f) _sweepAngle -= 360f;

            if (envController == null)
            {
                SetVerticesDirty();
                return;
            }

            // 섬을 못 찾은 경우 2초마다 재시도 (씬 로드 타이밍 대응, 찾을 때까지 반복)
            if (!_islandsCached || (_islandCache.Count == 0 && Time.unscaledTime >= _nextIslandRetryTime))
                CacheIslands();
            CollectShipData();
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect rect = rectTransform.rect;
            _pixelRadius = Mathf.Min(rect.width, rect.height) * 0.5f;
            float cx = rect.center.x;
            float cy = rect.center.y;

            if (_pixelRadius < 1f) return;

            // 1. 외곽 글로우 링 (배경 뒤 발광)
            DrawFilledCircle(vh, cx, cy, _pixelRadius + 6f,
                new Color(outerGlowColor.r, outerGlowColor.g, outerGlowColor.b, outerGlowColor.a * 0.4f), 48);
            DrawFilledCircle(vh, cx, cy, _pixelRadius + 3f,
                new Color(outerGlowColor.r, outerGlowColor.g, outerGlowColor.b, outerGlowColor.a * 0.25f), 48);

            // 2. 배경 원
            DrawFilledCircle(vh, cx, cy, _pixelRadius, bgCircleColor, 48);

            // 3. 격자 (십자선) - 대시 스타일
            Color dimGrid = new Color(gridColor.r, gridColor.g, gridColor.b, gridColor.a * 0.5f);
            DrawDashedLine(vh, cx - _pixelRadius * 0.92f, cy, cx + _pixelRadius * 0.92f, cy, 1f, gridColor, dimGrid);
            DrawDashedLine(vh, cx, cy - _pixelRadius * 0.92f, cx, cy + _pixelRadius * 0.92f, 1f, gridColor, dimGrid);
            // 45도 대각선 (더 어둡게)
            float diag = _pixelRadius * 0.65f;
            DrawLine(vh, cx - diag, cy - diag, cx + diag, cy + diag, 0.5f, dimGrid);
            DrawLine(vh, cx - diag, cy + diag, cx + diag, cy - diag, 0.5f, dimGrid);

            // 4. 거리 링 + 라벨 위치 표시
            for (int i = 1; i <= ringCount; i++)
            {
                float frac = (float)i / (ringCount + 1);
                float ringR = _pixelRadius * frac;
                DrawCircleOutline(vh, cx, cy, ringR, 1f, ringColor, 32);
                // 거리 링 우측에 작은 눈금 표시
                float labelX = cx + ringR + 2f;
                float labelY = cy;
                DrawFilledRect(vh, labelX, labelY - 1f, labelX + 8f, labelY + 1f,
                    new Color(ringColor.r, ringColor.g, ringColor.b, ringColor.a * 0.8f));
            }
            // 외곽 링 (이중선 - 더 강조)
            DrawCircleOutline(vh, cx, cy, _pixelRadius - 1f, 2f, ringColor * 1.8f, 48);
            DrawCircleOutline(vh, cx, cy, _pixelRadius - 4f, 0.8f,
                new Color(ringColor.r, ringColor.g, ringColor.b, ringColor.a * 0.3f), 48);

            // 5. 방위각 눈금 (perimeter bearing marks)
            DrawBearingTicks(vh, cx, cy);

            // 6. 스위프 잔상 (afterglow) - 부채꼴 그라데이션
            DrawSweepTrail(vh, cx, cy);

            // 7. 스위프 라인 (메인)
            float sweepRad = _sweepAngle * Mathf.Deg2Rad;
            float sx = cx + Mathf.Sin(sweepRad) * (_pixelRadius - 5f);
            float sy = cy + Mathf.Cos(sweepRad) * (_pixelRadius - 5f);
            DrawLine(vh, cx, cy, sx, sy, 2.5f, sweepColor);
            // 스위프 끝점 밝은 점
            DrawFilledCircle(vh, sx, sy, 3f, sweepColor * 1.5f, 8);

            if (envController == null) return;

            if (envController.motherShip != null)
                _radarWorldCenter = envController.motherShip.transform.position;

            if (autoFitRange) CalculateAutoRange();

            // 8. 섬 지형
            DrawIslands(vh, cx, cy);

            // 9. 웹 라인 (글로우 효과)
            if (_hasWebLine)
            {
                float d1 = new Vector2(_webP1.x - cx, _webP1.y - cy).magnitude;
                float d2 = new Vector2(_webP2.x - cx, _webP2.y - cy).magnitude;
                if (d1 < _pixelRadius && d2 < _pixelRadius)
                {
                    // 넓은 글로우
                    DrawLine(vh, _webP1.x, _webP1.y, _webP2.x, _webP2.y, 10f,
                        new Color(webLineColor.r, webLineColor.g, webLineColor.b, 0.1f));
                    DrawLine(vh, _webP1.x, _webP1.y, _webP2.x, _webP2.y, 5f,
                        new Color(webLineColor.r, webLineColor.g, webLineColor.b, 0.25f));
                    DrawLine(vh, _webP1.x, _webP1.y, _webP2.x, _webP2.y, 2.5f, webLineColor);
                }
            }

            // 9.5. 아군-적군 매칭 라인 (대시 스타일)
            foreach (var ml in _matchingLines)
            {
                Vector2 ap = WorldToLocal(ml.allyCenter, cx, cy);
                Vector2 ep = WorldToLocal(ml.enemyPos, cx, cy);
                float dA = new Vector2(ap.x - cx, ap.y - cy).magnitude;
                float dE = new Vector2(ep.x - cx, ep.y - cy).magnitude;
                if (dA < _pixelRadius && dE < _pixelRadius)
                {
                    // 넓은 글로우
                    DrawLine(vh, ap.x, ap.y, ep.x, ep.y, 6f,
                        new Color(matchingLineColor.r, matchingLineColor.g, matchingLineColor.b, 0.08f));
                    // 대시 라인
                    DrawDashedLine(vh, ap.x, ap.y, ep.x, ep.y, 1.5f,
                        matchingLineColor,
                        new Color(matchingLineColor.r, matchingLineColor.g, matchingLineColor.b, 0.05f));
                }
            }

            // 10. 선박 마커 (글로우 + 깜빡임)
            float blinkAlpha = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f);
            foreach (var ship in _shipData)
            {
                Vector2 rp = WorldToLocal(ship.worldPos, cx, cy);
                float dist = new Vector2(rp.x - cx, rp.y - cy).magnitude;
                if (dist > _pixelRadius - 5f) continue;

                Color markerCol = ship.markerColor;
                // 적군 마커 깜빡임 효과
                if (!ship.isMothership && markerCol.r > 0.5f && markerCol.g < 0.5f)
                    markerCol.a *= blinkAlpha;

                // 마커 글로우 (2단계 - 더 강하게)
                Color glowCol1 = new Color(markerCol.r, markerCol.g, markerCol.b, 0.1f);
                Color glowCol2 = new Color(markerCol.r, markerCol.g, markerCol.b, 0.3f);
                DrawFilledCircle(vh, rp.x, rp.y, ship.size * 1.2f, glowCol1, 8);
                DrawFilledCircle(vh, rp.x, rp.y, ship.size * 0.7f, glowCol2, 8);

                if (ship.isMothership)
                {
                    DrawDiamond(vh, rp.x, rp.y, ship.size, markerCol);
                    // 모선 주위 보호 링 (이중)
                    DrawCircleOutline(vh, rp.x, rp.y, ship.size * 1.1f, 1.5f,
                        new Color(mothershipColor.r, mothershipColor.g, mothershipColor.b, 0.4f), 16);
                    DrawCircleOutline(vh, rp.x, rp.y, ship.size * 0.7f, 1f,
                        new Color(mothershipColor.r, mothershipColor.g, mothershipColor.b, 0.2f), 12);
                }
                else
                {
                    DrawTriangleMarker(vh, rp.x, rp.y, ship.heading, ship.size, markerCol);
                }
            }

            // 11. 중심 십자 (더 밝고 크게)
            float crossSize = 6f;
            Color crossColor = new Color(0.4f, 1f, 0.5f, 0.8f);
            DrawLine(vh, cx - crossSize, cy, cx + crossSize, cy, 2f, crossColor);
            DrawLine(vh, cx, cy - crossSize, cx, cy + crossSize, 2f, crossColor);
            // 중심 밝은 점
            DrawFilledCircle(vh, cx, cy, 2.5f, crossColor, 8);
        }

        #region Sweep & Bearing

        /// <summary>스위프 잔상 (부채꼴 그라데이션 - 강한 효과)</summary>
        void DrawSweepTrail(VertexHelper vh, float cx, float cy)
        {
            int segments = Mathf.Clamp(sweepTrailSegments, 2, 12);
            float trailR = _pixelRadius - 5f;

            for (int i = 0; i < segments; i++)
            {
                float t = (float)i / segments;
                float tNext = (float)(i + 1) / segments;
                // 강한 잔상 - 시작은 sweepColor.a, 끝은 0으로 선형 감소
                float alpha = (1f - t) * sweepColor.a * 0.85f;
                float alphaNext = (1f - tNext) * sweepColor.a * 0.85f;

                float angle1 = (_sweepAngle - sweepTrailAngle * t) * Mathf.Deg2Rad;
                float angle2 = (_sweepAngle - sweepTrailAngle * tNext) * Mathf.Deg2Rad;

                Color c1 = new Color(sweepColor.r, sweepColor.g, sweepColor.b, alpha);
                Color c2 = new Color(sweepColor.r, sweepColor.g, sweepColor.b, alphaNext);
                // 중심은 약간의 빛 (0이 아닌 낮은 알파)
                Color cCenter = new Color(sweepColor.r, sweepColor.g, sweepColor.b, alpha * 0.15f);

                int idx = vh.currentVertCount;
                AddVert(vh, cx, cy, cCenter);
                AddVert(vh, cx + Mathf.Sin(angle1) * trailR, cy + Mathf.Cos(angle1) * trailR, c1);
                AddVert(vh, cx + Mathf.Sin(angle2) * trailR, cy + Mathf.Cos(angle2) * trailR, c2);
                vh.AddTriangle(idx, idx + 1, idx + 2);
            }
        }

        /// <summary>방위각 눈금 (36개 = 10도 간격, 주방위 강조)</summary>
        void DrawBearingTicks(VertexHelper vh, float cx, float cy)
        {
            float outerR = _pixelRadius - 2f;

            for (int i = 0; i < 36; i++)
            {
                float angle = i * 10f * Mathf.Deg2Rad;
                bool isMajor = (i % 9 == 0); // 0, 90, 180, 270
                bool isMid = (i % 3 == 0) && !isMajor; // 30도 단위

                float innerR;
                float width;
                Color col;

                if (isMajor)
                {
                    innerR = outerR - _pixelRadius * 0.1f;
                    width = 2f;
                    col = bearingTickColor;
                }
                else if (isMid)
                {
                    innerR = outerR - _pixelRadius * 0.06f;
                    width = 1.2f;
                    col = new Color(bearingTickColor.r, bearingTickColor.g, bearingTickColor.b, bearingTickColor.a * 0.6f);
                }
                else
                {
                    innerR = outerR - _pixelRadius * 0.035f;
                    width = 0.8f;
                    col = new Color(bearingTickColor.r, bearingTickColor.g, bearingTickColor.b, bearingTickColor.a * 0.3f);
                }

                float sinA = Mathf.Sin(angle);
                float cosA = Mathf.Cos(angle);
                DrawLine(vh,
                    cx + sinA * innerR, cy + cosA * innerR,
                    cx + sinA * outerR, cy + cosA * outerR,
                    width, col);
            }
        }

        /// <summary>대시 라인 (세그먼트 교대 색상)</summary>
        void DrawDashedLine(VertexHelper vh, float x1, float y1, float x2, float y2,
            float w, Color solidColor, Color gapColor)
        {
            int dashCount = 16;
            float dx = (x2 - x1) / dashCount;
            float dy = (y2 - y1) / dashCount;
            for (int i = 0; i < dashCount; i++)
            {
                float sx = x1 + dx * i;
                float sy = y1 + dy * i;
                Color c = (i % 2 == 0) ? solidColor : gapColor;
                DrawLine(vh, sx, sy, sx + dx, sy + dy, w, c);
            }
        }

        #endregion

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

            // 아군 표시: LaunchZoneManager가 있으면 모든 활성 쌍, 없으면 원본 쌍만
            if (envController.launchZoneManager != null && envController.launchZoneManager.IsInitialized)
            {
                var activeAgents = envController.launchZoneManager.GetActiveAgents();
                foreach (var agent in activeAgents)
                {
                    AddShipAgent(agent, friendlyColor, friendlyMarkerSize);
                }
            }
            else
            {
                AddShipAgent(envController.defenseAgent1, friendlyColor, friendlyMarkerSize);
                AddShipAgent(envController.defenseAgent2, friendlyColor, friendlyMarkerSize);
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
                        AddShip(enemy, enemyColor, enemyMarkerSize);
                }
            }

            // 아군-적군 매칭 라인 수집
            CollectMatchingLines();
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

        void CollectMatchingLines()
        {
            _matchingLines.Clear();

            var lzm = envController.launchZoneManager;
            if (lzm == null || !lzm.IsInitialized) return;

            int poolCount = lzm.GetCurrentPoolCount();
            for (int i = 0; i < poolCount; i++)
            {
                DefensePair pair = lzm.GetPair(i);
                if (pair == null || !pair.isActive) continue;
                if (pair.agent1 == null || pair.agent2 == null) continue;

                int targetIdx = pair.agent1.assignedTargetIndex; // 1-indexed, -1=none
                if (targetIdx <= 0) continue;

                int enemyPoolIdx = targetIdx - 1; // 0-indexed
                GameObject enemy = envController.GetPooledEnemy(enemyPoolIdx);
                if (enemy == null || !enemy.activeSelf) continue;

                Vector3 center = (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f;
                _matchingLines.Add(new MatchingLineData
                {
                    allyCenter = center,
                    enemyPos = enemy.transform.position
                });
            }
        }

        #endregion

        #region Island Cache

        /// <summary>
        /// 우선순위: 1) islandObjects 직접 참조 → 2) islandLayerMask Layer 탐색 → 3) "Island" 태그
        /// </summary>
        GameObject[] FindIslandGameObjects()
        {
            // 1순위: Inspector에서 직접 할당한 오브젝트
            if (islandObjects != null && islandObjects.Length > 0)
            {
                var valid = new List<GameObject>();
                foreach (var go in islandObjects)
                    if (go != null) valid.Add(go);
                if (valid.Count > 0) return valid.ToArray();
            }

            // 2순위: Layer 기반
            if (islandLayerMask != 0)
            {
                var renderers = FindObjectsOfType<Renderer>();
                var roots = new System.Collections.Generic.HashSet<GameObject>();
                foreach (var r in renderers)
                    if (((1 << r.gameObject.layer) & (int)islandLayerMask) != 0)
                        roots.Add(r.transform.root.gameObject);
                var arr = new GameObject[roots.Count];
                roots.CopyTo(arr);
                return arr;
            }

            // 3순위: "Island" 태그 fallback
            try { return GameObject.FindGameObjectsWithTag("Island"); }
            catch (UnityException) { return null; }
        }

        void CacheIslands()
        {
            _islandCache.Clear();
            _islandsCached = true;
            _totalIslandVerts = 0;

            GameObject[] islands = FindIslandGameObjects();
            if (islands == null || islands.Length == 0)
            {
                Debug.LogWarning("[RadarDisplay] 섬 오브젝트 없음 — islandObjects 배열에 직접 할당하거나 islandLayerMask를 설정하세요.");
                _islandsCached = false;
                _nextIslandRetryTime = Time.unscaledTime + 2f;
                return;
            }

            foreach (var island in islands)
            {
                if (island == null || _totalIslandVerts >= maxTotalIslandVerts) break;

                var terrain = island.GetComponent<Terrain>();
                if (terrain != null)
                {
                    CacheTerrainIsland(terrain);
                    continue;
                }

                var meshFilters = island.GetComponentsInChildren<MeshFilter>();
                if (meshFilters.Length > 0)
                {
                    int cachedBefore = _islandCache.Count;
                    foreach (var mf in meshFilters)
                    {
                        if (_totalIslandVerts >= maxTotalIslandVerts) break;
                        if (mf.sharedMesh != null) CacheMeshIsland(mf);
                    }
                    // MeshFilter는 있는데 readable이 아니어서 모두 bounds fallback → 없으면 전체 bounds 사용
                    if (_islandCache.Count == cachedBefore)
                    {
                        var r = island.GetComponentInChildren<Renderer>();
                        if (r != null)
                        {
                            Bounds combined = r.bounds;
                            foreach (var mr in island.GetComponentsInChildren<Renderer>())
                                combined.Encapsulate(mr.bounds);
                            CacheBoundsIsland(combined);
                        }
                    }
                }
                else
                {
                    var renderer = island.GetComponentInChildren<Renderer>();
                    if (renderer != null)
                        CacheBoundsIsland(renderer.bounds);
                }
            }
            Debug.LogWarning($"[RadarDisplay] 섬 {_islandCache.Count}개 캐시됨 (오브젝트: {islands.Length}개, 총 버텍스: {_totalIslandVerts})");
        }

        void CacheMeshIsland(MeshFilter mf)
        {
            var mesh = mf.sharedMesh;
            if (mesh == null)
                return;

            // 메시가 읽기 불가능 → Renderer bounds로 사각형 폴백
            if (!mesh.isReadable)
            {
                var renderer = mf.GetComponent<Renderer>();
                if (renderer != null)
                    CacheBoundsIsland(renderer.bounds);
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

        /// <summary>
        /// 메시 읽기 불가능 시 Renderer bounds로 불규칙 타원 생성
        /// 자연스러운 섬 형태 (10 segments, sin 변조로 울퉁불퉁)
        /// </summary>
        void CacheBoundsIsland(Bounds b)
        {
            const int seg = 10;
            if (_totalIslandVerts + seg + 1 > maxTotalIslandVerts) return;

            float cx = (b.min.x + b.max.x) * 0.5f;
            float cz = (b.min.z + b.max.z) * 0.5f;
            float rx = (b.max.x - b.min.x) * 0.5f;
            float rz = (b.max.z - b.min.z) * 0.5f;

            // 너무 작은 바운드는 무시
            if (rx < 1f && rz < 1f) return;

            var verts = new Vector2[seg + 1];
            verts[0] = new Vector2(cx, cz); // 중심점

            // bounds 크기 기반 시드 → 섬마다 다른 형태
            float seed = (cx * 0.13f + cz * 0.07f) % 6.28f;

            for (int i = 0; i < seg; i++)
            {
                float angle = i * Mathf.PI * 2f / seg;
                // 불규칙 변조: 0.8 ~ 1.0 범위로 들쭉날쭉
                float wobble = 0.82f + 0.18f * Mathf.Sin(angle * 3f + seed)
                                      + 0.08f * Mathf.Cos(angle * 5f + seed * 1.7f);
                verts[i + 1] = new Vector2(
                    cx + Mathf.Cos(angle) * rx * wobble,
                    cz + Mathf.Sin(angle) * rz * wobble);
            }

            // Fan triangulation (중심 → 둘레)
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

        /// <summary>
        /// Terrain을 공유 버텍스 그리드로 캐시 (N*N 버텍스, 셀 단위 삼각형)
        /// </summary>
        void CacheTerrainIsland(Terrain terrain)
        {
            var td = terrain.terrainData;
            var pos = terrain.transform.position;
            int n = Mathf.Clamp(terrainSamples, 4, 20);

            // 버텍스 예산 체크
            if (_totalIslandVerts + n * n > maxTotalIslandVerts)
                n = Mathf.Max(4, (int)Mathf.Sqrt(maxTotalIslandVerts - _totalIslandVerts));

            // 높이 그리드 + 공유 버텍스 생성
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

            // 셀 단위 삼각형 생성 (공유 버텍스 참조)
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

        void DrawIslands(VertexHelper vh, float cx, float cy)
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

        #region Coordinate Conversion

        Vector2 WorldToLocal(Vector3 wp, float cx, float cy)
        {
            float scale = _pixelRadius / radarRange;
            return new Vector2(cx + (wp.x - _radarWorldCenter.x) * scale,
                               cy + (wp.z - _radarWorldCenter.z) * scale);
        }

        Vector2 XZToLocal(Vector2 xz, float cx, float cy)
        {
            float scale = _pixelRadius / radarRange;
            return new Vector2(cx + (xz.x - _radarWorldCenter.x) * scale,
                               cy + (xz.y - _radarWorldCenter.z) * scale);
        }

        #endregion

        #region Auto Range

        void CalculateAutoRange()
        {
            float maxDist = 100f;
            if (envController.defenseAgent1 != null)
                maxDist = Mathf.Max(maxDist, HDist(envController.defenseAgent1.transform.position));
            if (envController.defenseAgent2 != null)
                maxDist = Mathf.Max(maxDist, HDist(envController.defenseAgent2.transform.position));
            if (envController.enemyShips != null)
                foreach (var e in envController.enemyShips)
                    if (e != null && e.activeInHierarchy)
                        maxDist = Mathf.Max(maxDist, HDist(e.transform.position));
            radarRange = Mathf.Max(1000f, maxDist * autoFitMargin);
        }

        float HDist(Vector3 wp)
        {
            float dx = wp.x - _radarWorldCenter.x, dz = wp.z - _radarWorldCenter.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
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
