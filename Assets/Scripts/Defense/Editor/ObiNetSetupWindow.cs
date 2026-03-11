using UnityEngine;
using UnityEditor;
using Obi;
using System.Collections.Generic;

namespace BoatAttack.DefenseEditor
{
    /// <summary>
    /// Obi Cloth 그물 셋업 에디터 윈도우
    /// 메시 생성 → Blueprint 생성 → 파티클 그룹 → ObiSolver/ObiCollider 배치
    /// </summary>
    public class ObiNetSetupWindow : EditorWindow
    {
        [MenuItem("Window/Defense/Obi Net Setup")]
        public static void ShowWindow()
        {
            GetWindow<ObiNetSetupWindow>("Obi Net Setup");
        }

        // Step 1: 메시 파라미터
        private float meshWidth = 50f;
        private float meshHeight = 10f;
        private int segX = 10;
        private int segY = 5;
        private string meshSavePath = "Assets/Obi/GeneratedNetMesh.asset";

        // Step 2: Blueprint
        private Mesh generatedMesh;
        private ObiClothBlueprint generatedBlueprint;
        private string blueprintSavePath = "Assets/Obi/NetClothBlueprint.asset";

        // Step 3: 파티클 그룹
        private string groupSavePath = "Assets/Obi";

        // Step 4: 환경
        private GameObject environmentRoot;

        private Vector2 scrollPos;

        private void OnGUI()
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            GUILayout.Label("Obi Cloth 그물 셋업 도구", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            DrawStep1_MeshGeneration();
            EditorGUILayout.Space(10);
            DrawStep2_Blueprint();
            EditorGUILayout.Space(10);
            DrawStep3_ParticleGroups();
            EditorGUILayout.Space(10);
            DrawStep4_EnvironmentSetup();

            EditorGUILayout.EndScrollView();
        }

        #region Step 1: 그물 메시 생성

        private void DrawStep1_MeshGeneration()
        {
            GUILayout.Label("Step 1: 그물 메시 생성", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "선박 간 거리(가로)와 수면 위 높이(세로)로 직사각형 메시를 생성합니다.\n" +
                "Subdivision이 많을수록 천이 자연스럽지만 성능이 떨어집니다.",
                MessageType.Info);

            meshWidth = EditorGUILayout.FloatField("가로 (선박 간 거리, m)", meshWidth);
            meshHeight = EditorGUILayout.FloatField("세로 (수면 위 높이, m)", meshHeight);
            segX = EditorGUILayout.IntSlider("가로 세그먼트", segX, 2, 20);
            segY = EditorGUILayout.IntSlider("세로 세그먼트", segY, 2, 15);
            meshSavePath = EditorGUILayout.TextField("저장 경로", meshSavePath);

            int vertCount = (segX + 1) * (segY + 1);
            int triCount = segX * segY * 2;
            EditorGUILayout.LabelField($"버텍스: {vertCount}개, 삼각형: {triCount}개");

            if (GUILayout.Button("그물 메시 생성", GUILayout.Height(30)))
            {
                GenerateNetMesh();
            }

            generatedMesh = (Mesh)EditorGUILayout.ObjectField("생성된 메시", generatedMesh, typeof(Mesh), false);
        }

        private void GenerateNetMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = "NetMesh";

            int vertsX = segX + 1;
            int vertsY = segY + 1;
            int vertCount = vertsX * vertsY;

            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uv = new Vector2[vertCount];
            Vector3[] normals = new Vector3[vertCount];

            // 버텍스 생성: X=가로(선박간), Y=세로(높이), Z=0
            // 원점 중심 배치
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

            // 삼각형 인덱스
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

            // 에셋 저장
            string dir = System.IO.Path.GetDirectoryName(meshSavePath);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            AssetDatabase.CreateAsset(mesh, meshSavePath);
            AssetDatabase.SaveAssets();
            generatedMesh = mesh;

            // 버텍스 인덱스 정보 로그
            int vertsPerCol = vertsY;
            Debug.Log($"[ObiNetSetup] 그물 메시 생성 완료: {meshSavePath}");
            Debug.Log($"  버텍스: {vertCount}개 ({vertsX}×{vertsY})");
            Debug.Log($"  Left Edge (x=0): 인덱스 {string.Join(", ", GetLeftEdgeIndices(vertsX, vertsY))}");
            Debug.Log($"  Right Edge (x={segX}): 인덱스 {string.Join(", ", GetRightEdgeIndices(vertsX, vertsY))}");

            EditorUtility.DisplayDialog("완료",
                $"그물 메시 생성 완료!\n버텍스: {vertCount}개\n경로: {meshSavePath}", "OK");
        }

        private List<int> GetLeftEdgeIndices(int vertsX, int vertsY)
        {
            var indices = new List<int>();
            for (int y = 0; y < vertsY; y++)
                indices.Add(y * vertsX); // x=0 열
            return indices;
        }

        private List<int> GetRightEdgeIndices(int vertsX, int vertsY)
        {
            var indices = new List<int>();
            for (int y = 0; y < vertsY; y++)
                indices.Add(y * vertsX + (vertsX - 1)); // x=last 열
            return indices;
        }

        #endregion

        #region Step 2: Blueprint

        private void DrawStep2_Blueprint()
        {
            GUILayout.Label("Step 2: ObiClothBlueprint 생성", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. 아래 버튼으로 Blueprint 에셋을 생성합니다.\n" +
                "2. 생성된 Blueprint를 더블클릭하여 Inspector에서 엽니다.\n" +
                "3. 'Generate' 버튼을 눌러 파티클을 생성합니다.\n" +
                "   ⚠️ vertexWeldDistance=0, minimumParticleSize=0.1로 설정하세요.",
                MessageType.Warning);

            blueprintSavePath = EditorGUILayout.TextField("저장 경로", blueprintSavePath);

            if (GUILayout.Button("Blueprint 에셋 생성", GUILayout.Height(25)))
            {
                CreateBlueprint();
            }

            generatedBlueprint = (ObiClothBlueprint)EditorGUILayout.ObjectField(
                "Blueprint", generatedBlueprint, typeof(ObiClothBlueprint), false);

            if (generatedBlueprint != null)
            {
                EditorGUILayout.LabelField($"파티클 수: {generatedBlueprint.particleCount}");

                if (generatedBlueprint.particleCount == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Blueprint이 아직 Generate되지 않았습니다.\n" +
                        "Blueprint를 선택하고 Inspector에서 Generate를 눌러주세요.",
                        MessageType.Error);
                }
            }
        }

        private void CreateBlueprint()
        {
            if (generatedMesh == null)
            {
                EditorUtility.DisplayDialog("오류", "먼저 Step 1에서 그물 메시를 생성하세요.", "OK");
                return;
            }

            var blueprint = ScriptableObject.CreateInstance<ObiClothBlueprint>();
            blueprint.inputMesh = generatedMesh;

            string dir = System.IO.Path.GetDirectoryName(blueprintSavePath);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            AssetDatabase.CreateAsset(blueprint, blueprintSavePath);
            AssetDatabase.SaveAssets();
            generatedBlueprint = blueprint;

            Selection.activeObject = blueprint;
            EditorUtility.DisplayDialog("완료",
                "Blueprint 에셋 생성 완료!\n\n" +
                "이제 Inspector에서:\n" +
                "1. vertexWeldDistance = 0\n" +
                "2. minimumParticleSize = 0.1\n" +
                "3. 'Generate' 버튼 클릭\n" +
                "4. 완료 후 Step 3으로 진행",
                "OK");
        }

        #endregion

        #region Step 3: 파티클 그룹

        private void DrawStep3_ParticleGroups()
        {
            GUILayout.Label("Step 3: 파티클 그룹 생성 (좌/우 가장자리)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Blueprint Generate 완료 후 실행하세요.\n" +
                "메시의 좌/우 가장자리 버텍스를 파티클 그룹으로 저장합니다.\n" +
                "이 그룹이 ObiParticleAttachment에서 선박에 고정됩니다.",
                MessageType.Info);

            groupSavePath = EditorGUILayout.TextField("저장 폴더", groupSavePath);

            bool canCreate = generatedBlueprint != null && generatedBlueprint.particleCount > 0;
            GUI.enabled = canCreate;

            if (GUILayout.Button("좌/우 파티클 그룹 생성", GUILayout.Height(25)))
            {
                CreateParticleGroups();
            }

            GUI.enabled = true;

            if (!canCreate && generatedBlueprint != null)
            {
                EditorGUILayout.HelpBox("Blueprint Generate를 먼저 완료하세요.", MessageType.Warning);
            }
        }

        private void CreateParticleGroups()
        {
            if (generatedBlueprint == null || generatedMesh == null) return;

            int vertsX = segX + 1;
            int vertsY = segY + 1;

            // Left Edge 그룹
            var leftGroup = ScriptableObject.CreateInstance<ObiParticleGroup>();
            leftGroup.name = "LeftEdge_Ship1";
            leftGroup.SetSourceBlueprint(generatedBlueprint);
            leftGroup.particleIndices = GetLeftEdgeIndices(vertsX, vertsY);

            string leftPath = $"{groupSavePath}/NetGroup_LeftEdge.asset";
            AssetDatabase.CreateAsset(leftGroup, leftPath);

            // Right Edge 그룹
            var rightGroup = ScriptableObject.CreateInstance<ObiParticleGroup>();
            rightGroup.name = "RightEdge_Ship2";
            rightGroup.SetSourceBlueprint(generatedBlueprint);
            rightGroup.particleIndices = GetRightEdgeIndices(vertsX, vertsY);

            string rightPath = $"{groupSavePath}/NetGroup_RightEdge.asset";
            AssetDatabase.CreateAsset(rightGroup, rightPath);

            // Blueprint의 groups 리스트에도 추가
            if (!generatedBlueprint.groups.Contains(leftGroup))
                generatedBlueprint.groups.Add(leftGroup);
            if (!generatedBlueprint.groups.Contains(rightGroup))
                generatedBlueprint.groups.Add(rightGroup);

            EditorUtility.SetDirty(generatedBlueprint);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ObiNetSetup] 파티클 그룹 생성 완료:");
            Debug.Log($"  LeftEdge (Ship1): {leftGroup.particleIndices.Count}개 파티클 → {leftPath}");
            Debug.Log($"  RightEdge (Ship2): {rightGroup.particleIndices.Count}개 파티클 → {rightPath}");

            EditorUtility.DisplayDialog("완료",
                $"파티클 그룹 생성 완료!\n\n" +
                $"LeftEdge (Ship1): {leftGroup.particleIndices.Count}개 파티클\n" +
                $"RightEdge (Ship2): {rightGroup.particleIndices.Count}개 파티클\n\n" +
                "DynamicWeb Inspector에서:\n" +
                "- obiGroupShip1 ← NetGroup_LeftEdge\n" +
                "- obiGroupShip2 ← NetGroup_RightEdge\n" +
                "를 할당하세요.",
                "OK");
        }

        #endregion

        #region Step 4: 환경 셋업

        private void DrawStep4_EnvironmentSetup()
        {
            GUILayout.Label("Step 4: 환경 셋업", EditorStyles.boldLabel);

            environmentRoot = (GameObject)EditorGUILayout.ObjectField(
                "환경 루트", environmentRoot, typeof(GameObject), true);

            EditorGUILayout.Space(5);

            // ObiSolver 추가
            if (GUILayout.Button("ObiSolver 추가 (환경 루트에)", GUILayout.Height(25)))
            {
                AddObiSolver();
            }

            EditorGUILayout.Space(5);

            // 적군 ObiCollider 추가
            if (GUILayout.Button("적군에 ObiCollider 추가 (attack_boat 태그)", GUILayout.Height(25)))
            {
                AddObiCollidersToEnemies();
            }
        }

        private void AddObiSolver()
        {
            if (environmentRoot == null)
            {
                EditorUtility.DisplayDialog("오류", "환경 루트 오브젝트를 할당하세요.", "OK");
                return;
            }

            var solver = environmentRoot.GetComponentInChildren<ObiSolver>();
            if (solver != null)
            {
                EditorUtility.DisplayDialog("정보",
                    $"이미 ObiSolver가 존재합니다: {solver.gameObject.name}", "OK");
                return;
            }

            var solverObj = new GameObject("ObiSolver");
            solverObj.transform.SetParent(environmentRoot.transform);
            solverObj.transform.localPosition = Vector3.zero;
            solver = solverObj.AddComponent<ObiSolver>();

            // 기본 설정
            solver.gravity = new Vector3(0, -9.81f, 0);
            solver.simulateWhenInvisible = false;

            Undo.RegisterCreatedObjectUndo(solverObj, "Add ObiSolver");

            EditorUtility.DisplayDialog("완료",
                "ObiSolver 추가 완료!\n\n" +
                "설정 권장값:\n" +
                "- Substeps: 2~4\n" +
                "- Synchronization: Asynchronous\n" +
                "- Simulate When Invisible: OFF",
                "OK");
        }

        private void AddObiCollidersToEnemies()
        {
            var enemies = GameObject.FindGameObjectsWithTag("attack_boat");
            if (enemies.Length == 0)
            {
                EditorUtility.DisplayDialog("정보",
                    "attack_boat 태그의 오브젝트가 없습니다.\n런타임에 생성되면 코드에서 자동 추가됩니다.",
                    "OK");
                return;
            }

            int added = 0;
            foreach (var enemy in enemies)
            {
                if (enemy.GetComponent<ObiCollider>() == null)
                {
                    var collider = enemy.GetComponent<Collider>();
                    if (collider != null)
                    {
                        Undo.AddComponent<ObiCollider>(enemy);
                        added++;
                    }
                }
            }

            EditorUtility.DisplayDialog("완료",
                $"ObiCollider 추가: {added}개 / 총 {enemies.Length}개 적군",
                "OK");
        }

        #endregion
    }
}
