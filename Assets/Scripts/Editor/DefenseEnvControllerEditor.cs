using UnityEditor;
using UnityEngine;

namespace BoatAttack
{
    [CustomEditor(typeof(DefenseEnvController))]
    public class DefenseEnvControllerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var env = (DefenseEnvController)target;

            // ============================================================
            // Pool Size 경고 + 자동 보정
            // ============================================================
            int maxNeeded = Mathf.Max(env.enemyCount, 1);
            if (env.poolSize < maxNeeded)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox(
                    $"Pool Size ({env.poolSize}) < 최대 적군 수 ({maxNeeded})!\n" +
                    $"적군이 {env.poolSize}대까지만 스폰됩니다.",
                    MessageType.Error);

                GUI.backgroundColor = new Color(1f, 0.4f, 0.3f);
                if (GUILayout.Button($"Fix: Pool Size → {maxNeeded}", GUILayout.Height(28)))
                {
                    Undo.RecordObject(env, "Fix Pool Size");
                    env.poolSize = maxNeeded;
                    EditorUtility.SetDirty(env);
                }
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("Quick Setup", EditorStyles.boldLabel);

            // ============================================================
            // 1. Formation Spawner 버튼
            // ============================================================
            EditorGUILayout.BeginHorizontal();
            if (env.formationSpawner != null)
            {
                EditorGUILayout.HelpBox("Formation Spawner: Connected", MessageType.Info);
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    Undo.RecordObject(env, "Remove Formation Spawner");
                    var spawner = env.formationSpawner;
                    env.formationSpawner = null;
                    EditorUtility.SetDirty(env);
                    Undo.DestroyObjectImmediate(spawner);
                }
            }
            else
            {
                if (GUILayout.Button("Add Formation Spawner", GUILayout.Height(30)))
                {
                    AddFormationSpawner(env);
                }
            }
            EditorGUILayout.EndHorizontal();

            // ============================================================
            // 2. Launch Zone Manager 버튼
            // ============================================================
            EditorGUILayout.BeginHorizontal();
            if (env.launchZoneManager != null)
            {
                EditorGUILayout.HelpBox("Launch Zone Manager: Connected", MessageType.Info);
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    Undo.RecordObject(env, "Remove Launch Zone Manager");
                    var lzm = env.launchZoneManager;
                    env.launchZoneManager = null;
                    EditorUtility.SetDirty(env);
                    Undo.DestroyObjectImmediate(lzm);
                }
            }
            else
            {
                if (GUILayout.Button("Add Launch Zone Manager", GUILayout.Height(30)))
                {
                    AddLaunchZoneManager(env);
                }
            }
            EditorGUILayout.EndHorizontal();

            // ============================================================
            // 3. 올인원 셋업 버튼
            // ============================================================
            EditorGUILayout.Space(5);
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.5f);
            if (GUILayout.Button("Setup All (Formation + LaunchZone)", GUILayout.Height(35)))
            {
                SetupAll(env);
            }
            GUI.backgroundColor = Color.white;

            // ============================================================
            // 4. 현재 상태 요약
            // ============================================================
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("System Status", EditorStyles.boldLabel);

            string formationStatus = env.formationSpawner != null
                ? $"Active (Type: {env.formationSpawner.formationType})"
                : "Not Connected (legacy single-direction spawn)";
            EditorGUILayout.LabelField("Formation:", formationStatus);

            string launchStatus = env.launchZoneManager != null
                ? $"Active (Max Pairs: {env.launchZoneManager.maxPairCount}, Active: {env.launchZoneManager.activePairCount})"
                : "Not Connected (legacy 1-pair mode)";
            EditorGUILayout.LabelField("Launch Zone:", launchStatus);

            // Pool Size 상태 (정상이면 녹색, 부족이면 빨강)
            var origColor = GUI.contentColor;
            GUI.contentColor = env.poolSize >= maxNeeded ? Color.green : Color.red;
            EditorGUILayout.LabelField("Pool Size:", $"{env.poolSize} (필요: {maxNeeded})");
            GUI.contentColor = origColor;

            EditorGUILayout.LabelField("Enemy Count:", env.enemyCount.ToString());
        }

        private void SetupAll(DefenseEnvController env)
        {
            Undo.RecordObject(env, "Setup All Formation System");

            // Pool Size 자동 보정
            int maxNeeded = Mathf.Max(env.enemyCount, 10);
            if (env.poolSize < maxNeeded)
            {
                env.poolSize = maxNeeded;
            }

            if (env.formationSpawner == null)
                AddFormationSpawner(env);
            if (env.launchZoneManager == null)
                AddLaunchZoneManager(env);

            EditorUtility.SetDirty(env);

            EditorUtility.DisplayDialog("Setup Complete",
                $"Formation + LaunchZone 세팅 완료!\n\n" +
                $"- Pool Size: {env.poolSize}\n" +
                $"- Formation: Random (집중/파상/양동)\n" +
                $"- LaunchZone: 4방위, 최대 6쌍\n\n" +
                $"Ctrl+S로 씬 저장하세요.", "OK");
        }

        private void AddFormationSpawner(DefenseEnvController env)
        {
            var spawner = env.gameObject.GetComponent<EnemyFormationSpawner>();
            if (spawner == null)
            {
                spawner = Undo.AddComponent<EnemyFormationSpawner>(env.gameObject);
            }

            spawner.formationType = FormationType.Random;
            spawner.concentratedAngleSpread = 15f;
            spawner.concentratedDistJitter = 300f;
            spawner.randomizeConcentratedSpread = true;
            spawner.waveCount = 3;
            spawner.waveGap = 400f;
            spawner.waveLateralSpread = 12f;
            spawner.randomizeWaveParams = true;
            spawner.diversionaryDirections = 3;
            spawner.diversionaryDirSpread = 20f;
            spawner.diversionaryClusterSpread = 10f;
            spawner.randomizeDiversionaryParams = true;

            env.formationSpawner = spawner;
            EditorUtility.SetDirty(env);
            EditorUtility.SetDirty(spawner);

            Debug.Log("[DefenseEnvEditor] EnemyFormationSpawner added and linked.");
        }

        private void AddLaunchZoneManager(DefenseEnvController env)
        {
            var lzm = env.gameObject.GetComponent<LaunchZoneManager>();
            if (lzm == null)
            {
                lzm = Undo.AddComponent<LaunchZoneManager>(env.gameObject);
            }

            lzm.launchZones = new LaunchZone[]
            {
                new LaunchZone { angleDeg = 0f,   distance = 100f, angleJitter = 10f },
                new LaunchZone { angleDeg = 90f,  distance = 100f, angleJitter = 10f },
                new LaunchZone { angleDeg = 180f, distance = 100f, angleJitter = 10f },
                new LaunchZone { angleDeg = 270f, distance = 100f, angleJitter = 10f },
            };

            lzm.maxPairCount = 6;
            lzm.activePairCount = 1;
            lzm.motherShip = env.motherShip;
            lzm.envController = env;

            if (env.defenseAgent1 != null)
            {
                lzm.templatePair = new DefensePair
                {
                    agent1 = env.defenseAgent1,
                    webObject = env.webObject
                };
            }

            env.launchZoneManager = lzm;
            EditorUtility.SetDirty(env);
            EditorUtility.SetDirty(lzm);

            Debug.Log("[DefenseEnvEditor] LaunchZoneManager added and linked " +
                $"(templatePair: {(lzm.templatePair?.agent1 != null ? "set" : "null")}).");
        }
    }
}
