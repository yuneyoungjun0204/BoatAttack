using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor.SceneManagement;

namespace BoatAttack
{
    /// <summary>
    /// 전장 통합 뷰(게임 모드) 자동 세팅.
    /// Enter 게임 모드 시 표시되는 오버레이를 생성:
    ///   - 상단 가로 4창: 아군 · 적군 · 버드아이뷰 · 레이다(맨 오른쪽)
    ///   - 아래 전체: 따라가는 아군 1인칭 (메인 카메라가 비침)
    /// 기존 TacticalUI_Canvas + TacticalPageManager가 있어야 한다
    /// (없으면 먼저 BoatAttack/Setup Tactical System 실행).
    /// </summary>
    public static class BattleOverviewSetup
    {
        static readonly Color BG_DARK   = new Color(0.02f, 0.03f, 0.07f, 0.97f);
        static readonly Color WIN_BG    = new Color(0.03f, 0.05f, 0.09f, 1f);
        static readonly Color BORDER_ALLY = new Color(0.2f, 0.85f, 1f, 1f);
        static readonly Color BORDER_ENEMY = new Color(1f, 0.3f, 0.25f, 1f);
        static readonly Color BORDER_OVER = new Color(1f, 0.8f, 0.2f, 1f);
        static readonly Color ACCENT_GREEN = new Color(0.3f, 1f, 0.5f, 1f);
        static readonly Color TEXT_DIM  = new Color(0.6f, 0.6f, 0.7f, 1f);

        [MenuItem("BoatAttack/Setup Battle Overview (Game Mode)", false, 101)]
        public static void SetupBattleOverview()
        {
            var pm = Object.FindObjectOfType<TacticalPageManager>();
            if (pm == null)
            {
                EditorUtility.DisplayDialog("Error",
                    "TacticalPageManager(TacticalUI_Canvas)를 찾을 수 없습니다.\n" +
                    "먼저 BoatAttack > Setup Tactical System (All) 을 실행하세요.", "OK");
                return;
            }
            var env = Object.FindObjectOfType<DefenseEnvController>();
            if (env == null)
            {
                EditorUtility.DisplayDialog("Error", "DefenseEnvController를 찾을 수 없습니다.", "OK");
                return;
            }

            var canvasTr = pm.transform;

            // 기존 오버레이 제거
            var prev = canvasTr.Find("BattleOverlay");
            if (prev != null) Undo.DestroyObjectImmediate(prev.gameObject);

            // === 루트 (풀스크린) ===
            var overlay = NewRect("BattleOverlay", canvasTr, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Undo.RegisterCreatedObjectUndo(overlay, "Create BattleOverlay");

            // === 상단 가로 4창 쫙: ALLY | ENEMY | BIRD'S-EYE | RADAR (레이다=맨 오른쪽) ===
            // 아래 영역은 투명 → 메인 카메라(아군 1인칭) 기본 화면이 비침
            const float yMin = 0.66f, yMax = 0.985f;

            // ① 아군 추적 (맨 왼쪽)
            var allyView = BuildCameraWindow(overlay.transform, env, "AllyWindow",
                new Vector2(0.005f, yMin), new Vector2(0.247f, yMax),
                BattleViewCamera.ViewMode.Ally, "ALLY", BORDER_ALLY, KeyCode.None, null);

            // ② 적군 추적 (아군 창이 보는 아군이 타겟한 적군)
            BuildCameraWindow(overlay.transform, env, "EnemyWindow",
                new Vector2(0.253f, yMin), new Vector2(0.495f, yMax),
                BattleViewCamera.ViewMode.Enemy, "ENEMY", BORDER_ENEMY, KeyCode.None, allyView);

            // ③ 버드아이뷰 (모선 최근접 선박 정수직 추적)
            BuildCameraWindow(overlay.transform, env, "BirdsEyeWindow",
                new Vector2(0.501f, yMin), new Vector2(0.743f, yMax),
                BattleViewCamera.ViewMode.BirdsEye, "BIRD'S-EYE", BORDER_OVER, KeyCode.None, null);

            // ④ 레이다/전술맵 (맨 오른쪽)
            BuildRadarWindow(overlay.transform, env,
                new Vector2(0.749f, yMin), new Vector2(0.995f, yMax));

            // === 힌트 (하단 중앙, 1인칭 화면 위) ===
            CreateLabel(overlay.transform, "Hint",
                "[Enter] UI 복귀   |   상단: 레이다 · 아군 · 적군 · 버드아이   |   아래: 아군 1인칭",
                new Vector2(0f, 0f), new Vector2(1f, 0.035f), 12, FontStyle.Normal, TEXT_DIM,
                TextAnchor.LowerCenter);

            // === PageManager 배선 ===
            Undo.RecordObject(pm, "Assign BattleOverlay");
            pm.battleOverlay = overlay;
            overlay.SetActive(false);
            EditorUtility.SetDirty(pm);

            EditorSceneManager.MarkSceneDirty(pm.gameObject.scene);

            EditorUtility.DisplayDialog("Battle Overview 완료",
                "전장 통합 뷰(게임 모드) 세팅 완료!\n\n" +
                "[Enter] 게임 모드 진입 →\n" +
                "  · 상단 가로 4창: 아군 · 적군 · 버드아이뷰 · 레이다\n" +
                "    - 적군 = 아군이 타겟한 적군\n" +
                "    - 버드아이뷰 = 모선 최근접 선박 정수직 추적\n" +
                "  · 아래 전체: 따라가는 아군 1인칭 (메인 카메라)\n" +
                "[Enter] 다시 → UI 페이지 복귀\n\n" +
                "창 위치/크기 = Inspector RectTransform,\n" +
                "추적 거리/높이 = BattleViewCamera, 부감 고도 = birdsEyeAltitude.\n\n" +
                "Ctrl+S로 씬 저장!", "OK");
        }

        [MenuItem("BoatAttack/Remove Battle Overview", false, 102)]
        public static void RemoveBattleOverview()
        {
            var pm = Object.FindObjectOfType<TacticalPageManager>();
            if (pm == null) return;
            var prev = pm.transform.Find("BattleOverlay");
            if (prev != null)
            {
                Undo.DestroyObjectImmediate(prev.gameObject);
                Undo.RecordObject(pm, "Clear BattleOverlay");
                pm.battleOverlay = null;
                EditorUtility.SetDirty(pm);
                EditorSceneManager.MarkSceneDirty(pm.gameObject.scene);
            }
        }

        // ================================================================
        // 카메라 창 (테두리 + 타이틀 + RawImage + BattleViewCamera)
        // ================================================================
        static BattleViewCamera BuildCameraWindow(Transform parent, DefenseEnvController env, string name,
            Vector2 aMin, Vector2 aMax, BattleViewCamera.ViewMode mode, string title, Color border, KeyCode cycle,
            BattleViewCamera linkedAlly)
        {
            // 테두리(살짝 큰 패널)
            var frame = AddPanel(parent, name, aMin, aMax, Vector2.zero, Vector2.zero, border);

            // 내부 배경
            var inner = AddPanel(frame.transform, "Inner",
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(3, 3), new Vector2(-3, -3), WIN_BG);

            // RawImage (라이브 카메라 출력)
            var rawObj = NewRect("LiveView", inner.transform,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(2, 2), new Vector2(-2, -22));
            var raw = rawObj.AddComponent<RawImage>();
            raw.color = Color.white;

            // 타이틀 바
            CreateLabel(inner.transform, "Title", title,
                new Vector2(0, 1), new Vector2(1, 1), 14, FontStyle.Bold, border, TextAnchor.MiddleLeft,
                new Vector2(8, -20), new Vector2(-8, -2));

            // BattleViewCamera (frame에 부착)
            var bvc = frame.AddComponent<BattleViewCamera>();
            bvc.targetImage = raw;
            bvc.envController = env;
            bvc.mode = mode;
            bvc.cycleKey = cycle;
            bvc.fov = 58.5f;       // 화각 ×1.3 (45 → 58.5)
            bvc.distance = 60f;    // 거리 ×1.5 (40 → 60)
            if (linkedAlly != null) bvc.linkedAllyView = linkedAlly;
            EditorUtility.SetDirty(bvc);
            return bvc;
        }

        // ================================================================
        // 레이다 창 (카메라 창과 동일한 프레임 + 전술맵)
        // ================================================================
        static void BuildRadarWindow(Transform parent, DefenseEnvController env, Vector2 aMin, Vector2 aMax)
        {
            var frame = AddPanel(parent, "RadarWindow", aMin, aMax, Vector2.zero, Vector2.zero, ACCENT_GREEN);
            var inner = AddPanel(frame.transform, "Inner",
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(3, 3), new Vector2(-3, -3),
                new Color(0.02f, 0.05f, 0.03f, 1f));

            CreateLabel(inner.transform, "Title", "RADAR",
                new Vector2(0, 1), new Vector2(1, 1), 14, FontStyle.Bold, ACCENT_GREEN, TextAnchor.MiddleLeft,
                new Vector2(8, -20), new Vector2(-8, -2));

            // 정사각 전술맵 (레이더 원 왜곡 방지) — inner 안에서 최대, 중앙
            var mapObj = NewRect("TacticalMap", inner.transform,
                new Vector2(0.02f, 0.02f), new Vector2(0.98f, 0.90f), Vector2.zero, Vector2.zero);
            var aspect = mapObj.AddComponent<AspectRatioFitter>();
            aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            aspect.aspectRatio = 1f;
            var map = mapObj.AddComponent<TacticalMapDisplay>();
            map.envController = env;
            map.radarRange = 1000f;
            map.mapScaleFromRadar = (3f / 1.3f) * 3f;   // 표시 범위 현재의 3배
            map.gridSpacing = 500f;
            map.showRadarCircle = true;
            map.friendlyMarkerSize = 20f;
            map.enemyMarkerSize = 18f;
            map.mothershipMarkerSize = 28f;
            map.edgeMarkerSize = 10f;
            EditorUtility.SetDirty(map);
        }

        // ================================================================
        // UI 헬퍼
        // ================================================================
        static GameObject NewRect(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            var rt = obj.AddComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = oMin; rt.offsetMax = oMax;
            return obj;
        }

        static GameObject AddPanel(Transform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax, Color col)
        {
            var obj = NewRect(name, parent, aMin, aMax, oMin, oMax);
            var img = obj.AddComponent<Image>();
            img.color = col;
            img.raycastTarget = false;
            return obj;
        }

        static GameObject CreateLabel(Transform parent, string name, string text,
            Vector2 aMin, Vector2 aMax, int size, FontStyle style, Color col, TextAnchor anchor,
            Vector2 oMin = default, Vector2 oMax = default)
        {
            var obj = NewRect(name, parent, aMin, aMax, oMin, oMax);
            var txt = obj.AddComponent<Text>();
            txt.text = text; txt.fontSize = size; txt.fontStyle = style; txt.color = col;
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.alignment = anchor;
            txt.raycastTarget = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            return obj;
        }
    }
}
