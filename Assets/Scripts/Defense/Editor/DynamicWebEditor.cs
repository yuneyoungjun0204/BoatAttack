using UnityEngine;
using UnityEditor;
using Obi;
using System.Collections.Generic;

namespace BoatAttack.DefenseEditor
{
    [CustomEditor(typeof(DynamicWeb))]
    public class DynamicWebEditor : UnityEditor.Editor
    {
        // 메시 생성 파라미터
        private float meshWidth = 50f;
        private float meshHeight = 10f;
        private int segX = 10;
        private int segY = 5;

        private bool showObiSetup = false;

        public override void OnInspectorGUI()
        {
            // 기본 Inspector 그리기
            DrawDefaultInspector();

            var web = (DynamicWeb)target;

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            // Obi 셋업 토글
            showObiSetup = EditorGUILayout.Foldout(showObiSetup, "Obi Cloth 셋업 도구", true, EditorStyles.foldoutHeader);
            if (!showObiSetup) return;

            EditorGUILayout.Space(5);

            // 현재 상태 표시
            DrawObiStatus(web);

            EditorGUILayout.Space(10);

            // 자동 셋업 버튼
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("Obi 자동 셋업 (메시 + Blueprint + 그룹 + Solver)", GUILayout.Height(35)))
            {
                AutoSetupObi(web);
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(5);

            // 개별 단계 버튼들
            EditorGUILayout.LabelField("개별 단계:", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("1. 메시 생성"))
                GenerateAndAssignMesh(web);
            if (GUILayout.Button("2. Blueprint 생성"))
                CreateAndAssignBlueprint(web);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("3. 파티클 그룹"))
                CreateAndAssignGroups(web);
            if (GUILayout.Button("4. Solver 배치"))
                EnsureSolver(web);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // 메시 파라미터 (접이식)
            EditorGUILayout.LabelField("메시 파라미터:", EditorStyles.miniLabel);
            meshWidth = EditorGUILayout.FloatField("가로 (m)", meshWidth);
            meshHeight = EditorGUILayout.FloatField("세로 (m)", meshHeight);
            segX = EditorGUILayout.IntSlider("가로 세그먼트", segX, 2, 20);
            segY = EditorGUILayout.IntSlider("세로 세그먼트", segY, 2, 15);

            EditorGUILayout.Space(10);

            // 취소/제거 버튼
            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("Obi 설정 초기화 (참조 해제)", GUILayout.Height(25)))
            {
                ClearObiSetup(web);
            }
            GUI.backgroundColor = Color.white;
        }

        private void DrawObiStatus(DynamicWeb web)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("현재 상태:", EditorStyles.boldLabel);

            DrawStatusLine("useObiCloth", web.useObiCloth);
            DrawStatusLine("Blueprint", web.obiClothBlueprint != null);
            DrawStatusLine("Solver", web.obiSolver != null);
            DrawStatusLine("Group Ship1", web.obiGroupShip1 != null);
            DrawStatusLine("Group Ship2", web.obiGroupShip2 != null);

            if (web.obiClothBlueprint != null)
            {
                EditorGUILayout.LabelField($"  파티클 수: {web.obiClothBlueprint.particleCount}");
                if (web.obiClothBlueprint.particleCount == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Blueprint Generate가 필요합니다.\nBlueprint를 선택 → Inspector에서 Generate 클릭",
                        MessageType.Warning);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawStatusLine(string label, bool ok)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(ok ? "✓" : "✗", GUILayout.Width(20));
            EditorGUILayout.LabelField(label);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 전체 자동 셋업: 메시 → Blueprint → 그룹 → Solver → 할당
        /// </summary>
        private void AutoSetupObi(DynamicWeb web)
        {
            Undo.RecordObject(web, "Auto Setup Obi Cloth");

            // 1. 메시 생성
            Mesh mesh = GenerateNetMesh();
            string meshPath = GetAssetPath(web, "NetMesh.asset");
            SaveAsset(mesh, meshPath);

            // 2. Blueprint 생성
            var blueprint = ScriptableObject.CreateInstance<ObiClothBlueprint>();
            blueprint.inputMesh = mesh;
            string bpPath = GetAssetPath(web, "NetBlueprint.asset");
            SaveAsset(blueprint, bpPath);
            web.obiClothBlueprint = blueprint;

            // 3. Solver 확보
            EnsureSolver(web);

            // 4. useObiCloth 활성화
            web.useObiCloth = true;

            EditorUtility.SetDirty(web);
            AssetDatabase.SaveAssets();

            // Blueprint Generate 안내
            Selection.activeObject = blueprint;
            bool doGenerate = EditorUtility.DisplayDialog("자동 셋업 완료!",
                "메시, Blueprint, Solver가 생성되었습니다.\n\n" +
                "다음 단계:\n" +
                "1. 지금 열린 Blueprint Inspector에서 'Generate' 클릭\n" +
                "2. Generate 완료 후 DynamicWeb으로 돌아와서\n" +
                "   '3. 파티클 그룹' 버튼 클릭\n\n" +
                "Blueprint를 지금 선택하시겠습니까?",
                "Blueprint로 이동", "나중에");

            if (doGenerate)
                Selection.activeObject = blueprint;
        }

        /// <summary>
        /// 그물 메시 프로시저럴 생성 + 할당
        /// </summary>
        private void GenerateAndAssignMesh(DynamicWeb web)
        {
            Undo.RecordObject(web, "Generate Net Mesh");

            Mesh mesh = GenerateNetMesh();
            string path = GetAssetPath(web, "NetMesh.asset");
            SaveAsset(mesh, path);

            // Blueprint가 있으면 메시도 갱신
            if (web.obiClothBlueprint != null)
            {
                web.obiClothBlueprint.inputMesh = mesh;
                EditorUtility.SetDirty(web.obiClothBlueprint);
            }

            EditorUtility.SetDirty(web);
            Debug.Log($"[DynamicWebEditor] 그물 메시 생성: {path} ({(segX+1)*(segY+1)} vertices)");
        }

        private Mesh GenerateNetMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = "NetMesh";

            int vertsX = segX + 1;
            int vertsY = segY + 1;
            int vertCount = vertsX * vertsY;

            var vertices = new Vector3[vertCount];
            var uv = new Vector2[vertCount];
            var normals = new Vector3[vertCount];

            for (int y = 0; y < vertsY; y++)
            {
                for (int x = 0; x < vertsX; x++)
                {
                    int i = y * vertsX + x;
                    float px = ((float)x / segX - 0.5f) * meshWidth;
                    float py = ((float)y / segY) * meshHeight;
                    vertices[i] = new Vector3(px, py, 0f);
                    uv[i] = new Vector2((float)x / segX, (float)y / segY);
                    normals[i] = Vector3.back;
                }
            }

            int[] triangles = new int[segX * segY * 6];
            int ti = 0;
            for (int y = 0; y < segY; y++)
            {
                for (int x = 0; x < segX; x++)
                {
                    int bl = y * vertsX + x;
                    int br = bl + 1;
                    int tl = bl + vertsX;
                    int tr = tl + 1;
                    triangles[ti++] = bl;
                    triangles[ti++] = tl;
                    triangles[ti++] = br;
                    triangles[ti++] = br;
                    triangles[ti++] = tl;
                    triangles[ti++] = tr;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Blueprint 생성 + 할당
        /// </summary>
        private void CreateAndAssignBlueprint(DynamicWeb web)
        {
            Mesh mesh = web.obiClothBlueprint != null ? web.obiClothBlueprint.inputMesh : null;
            if (mesh == null)
            {
                EditorUtility.DisplayDialog("오류", "먼저 메시를 생성하세요 (1번 버튼).", "OK");
                return;
            }

            Undo.RecordObject(web, "Create Blueprint");

            var blueprint = ScriptableObject.CreateInstance<ObiClothBlueprint>();
            blueprint.inputMesh = mesh;
            string path = GetAssetPath(web, "NetBlueprint.asset");
            SaveAsset(blueprint, path);
            web.obiClothBlueprint = blueprint;

            EditorUtility.SetDirty(web);
            Selection.activeObject = blueprint;

            EditorUtility.DisplayDialog("Blueprint 생성 완료",
                "Inspector에서 Generate 버튼을 눌러주세요.\n" +
                "완료 후 DynamicWeb에서 '3. 파티클 그룹' 클릭.", "OK");
        }

        /// <summary>
        /// 파티클 그룹 생성 + 할당
        /// </summary>
        private void CreateAndAssignGroups(DynamicWeb web)
        {
            if (web.obiClothBlueprint == null)
            {
                EditorUtility.DisplayDialog("오류", "Blueprint가 없습니다.", "OK");
                return;
            }
            if (web.obiClothBlueprint.particleCount == 0)
            {
                EditorUtility.DisplayDialog("오류",
                    "Blueprint Generate를 먼저 완료하세요.\n" +
                    "Blueprint 선택 → Inspector → Generate", "OK");
                return;
            }

            Undo.RecordObject(web, "Create Particle Groups");

            int vertsX = segX + 1;
            int vertsY = segY + 1;

            // Left Edge (Ship1)
            var leftGroup = ScriptableObject.CreateInstance<ObiParticleGroup>();
            leftGroup.name = "LeftEdge_Ship1";
            leftGroup.SetSourceBlueprint(web.obiClothBlueprint);
            for (int y = 0; y < vertsY; y++)
                leftGroup.particleIndices.Add(y * vertsX);

            string leftPath = GetAssetPath(web, "NetGroup_Left.asset");
            SaveAsset(leftGroup, leftPath);

            // Right Edge (Ship2)
            var rightGroup = ScriptableObject.CreateInstance<ObiParticleGroup>();
            rightGroup.name = "RightEdge_Ship2";
            rightGroup.SetSourceBlueprint(web.obiClothBlueprint);
            for (int y = 0; y < vertsY; y++)
                rightGroup.particleIndices.Add(y * vertsX + segX);

            string rightPath = GetAssetPath(web, "NetGroup_Right.asset");
            SaveAsset(rightGroup, rightPath);

            // Blueprint groups에 등록
            if (!web.obiClothBlueprint.groups.Contains(leftGroup))
                web.obiClothBlueprint.groups.Add(leftGroup);
            if (!web.obiClothBlueprint.groups.Contains(rightGroup))
                web.obiClothBlueprint.groups.Add(rightGroup);
            EditorUtility.SetDirty(web.obiClothBlueprint);

            // DynamicWeb에 할당
            web.obiGroupShip1 = leftGroup;
            web.obiGroupShip2 = rightGroup;
            EditorUtility.SetDirty(web);
            AssetDatabase.SaveAssets();

            Debug.Log($"[DynamicWebEditor] 파티클 그룹 생성 완료: Left={leftGroup.Count}개, Right={rightGroup.Count}개");
            EditorUtility.DisplayDialog("완료",
                $"파티클 그룹 할당 완료!\n\n" +
                $"Ship1 (Left): {leftGroup.Count}개 파티클\n" +
                $"Ship2 (Right): {rightGroup.Count}개 파티클\n\n" +
                "이제 useObiCloth를 켜고 플레이하세요.",
                "OK");
        }

        /// <summary>
        /// ObiSolver 확보
        /// </summary>
        private void EnsureSolver(DynamicWeb web)
        {
            if (web.obiSolver != null) return;

            // 부모 계층에서 탐색
            var solver = web.GetComponentInParent<ObiSolver>();
            if (solver == null)
            {
                Transform envRoot = web.transform.parent != null ? web.transform.parent : web.transform;
                solver = envRoot.GetComponentInChildren<ObiSolver>();
            }

            if (solver == null)
            {
                // 환경 루트에 새로 생성
                Transform parent = web.transform.parent != null ? web.transform.parent : web.transform;
                var solverObj = new GameObject("ObiSolver");
                solverObj.transform.SetParent(parent);
                solverObj.transform.localPosition = Vector3.zero;
                solver = solverObj.AddComponent<ObiSolver>();
                solver.gravity = new Vector3(0, -9.81f, 0);
                solver.simulateWhenInvisible = false;
                Undo.RegisterCreatedObjectUndo(solverObj, "Add ObiSolver");
                Debug.Log($"[DynamicWebEditor] ObiSolver 생성: {solverObj.name}");
            }

            Undo.RecordObject(web, "Assign ObiSolver");
            web.obiSolver = solver;
            EditorUtility.SetDirty(web);
        }

        /// <summary>
        /// Obi 설정 초기화 (참조 해제)
        /// </summary>
        private void ClearObiSetup(DynamicWeb web)
        {
            if (!EditorUtility.DisplayDialog("Obi 설정 초기화",
                "DynamicWeb의 Obi 관련 참조를 모두 해제합니다.\n" +
                "(에셋 파일은 삭제되지 않습니다)\n\n진행하시겠습니까?",
                "초기화", "취소")) return;

            Undo.RecordObject(web, "Clear Obi Setup");
            web.useObiCloth = false;
            web.obiClothBlueprint = null;
            web.obiSolver = null;
            web.obiGroupShip1 = null;
            web.obiGroupShip2 = null;
            EditorUtility.SetDirty(web);
            Debug.Log("[DynamicWebEditor] Obi 설정 초기화 완료");
        }

        /// <summary>
        /// DynamicWeb 위치 기반 에셋 경로 생성
        /// </summary>
        private string GetAssetPath(DynamicWeb web, string filename)
        {
            return $"Assets/Obi/Generated/{filename}";
        }

        private void SaveAsset(Object asset, string path)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            // 기존 에셋이 있으면 덮어쓰기
            var existing = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (existing != null)
                AssetDatabase.DeleteAsset(path);

            AssetDatabase.CreateAsset(asset, path);
        }
    }
}
