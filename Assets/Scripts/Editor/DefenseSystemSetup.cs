using UnityEngine;
using UnityEditor;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.Barracuda;
using Unity.MLAgents.Policies;

namespace BoatAttack
{
    /// <summary>
    /// 방어 시스템 자동 설정 에디터 도구
    /// BehaviorParameters SpaceSize=11, BufferSensor 추가, 참조 연결 등
    /// </summary>
    public static class DefenseSystemSetup
    {
        [MenuItem("BoatAttack/Setup Defense Agents", false, 95)]
        public static void SetupDefenseAgents()
        {
            // 씬에서 모든 DefenseAgent 찾기
            var agents = Object.FindObjectsOfType<DefenseAgent>(true);
            if (agents.Length == 0)
            {
                EditorUtility.DisplayDialog("Setup Defense Agents", "씬에 DefenseAgent가 없습니다.", "OK");
                return;
            }

            int modified = 0;
            foreach (var agent in agents)
            {
                bool changed = false;

                // 1. BehaviorParameters SpaceSize=11, StackedVectors=3
                var bp = agent.GetComponent<BehaviorParameters>();
                if (bp != null)
                {
                    var so = new SerializedObject(bp);
                    var brainParams = so.FindProperty("m_BrainParameters");
                    if (brainParams != null)
                    {
                        var vecObsSize = brainParams.FindPropertyRelative("VectorObservationSize");
                        var numStacked = brainParams.FindPropertyRelative("NumStackedVectorObservations");

                        if (vecObsSize != null && vecObsSize.intValue != 11)
                        {
                            vecObsSize.intValue = 11;
                            changed = true;
                        }
                        if (numStacked != null && numStacked.intValue != 3)
                        {
                            numStacked.intValue = 3;
                            changed = true;
                        }
                    }

                    // Model을 None으로 (이전 모델 호환 안 되므로)
                    var modelProp = so.FindProperty("m_Model");
                    if (modelProp != null && modelProp.objectReferenceValue != null)
                    {
                        modelProp.objectReferenceValue = null;
                        changed = true;
                    }

                    if (changed)
                        so.ApplyModifiedProperties();
                }

                // 2. BufferSensorComponent 추가 (없으면)
                var bufferSensor = agent.GetComponent<BufferSensorComponent>();
                if (bufferSensor == null)
                {
                    bufferSensor = Undo.AddComponent<BufferSensorComponent>(agent.gameObject);
                    changed = true;
                }

                // BufferSensor 설정: ObservableSize=4 (R,F,Dist,Hdg), MaxNumObservables=10
                {
                    var bso = new SerializedObject(bufferSensor);
                    var obsSize = bso.FindProperty("m_ObservableSize");
                    var maxObs = bso.FindProperty("m_MaxNumObservables");
                    var sensorName = bso.FindProperty("m_SensorName");

                    bool bsChanged = false;
                    if (obsSize != null && obsSize.intValue != 4)
                    {
                        obsSize.intValue = 4;
                        bsChanged = true;
                    }
                    if (maxObs != null && maxObs.intValue != 10)
                    {
                        maxObs.intValue = 10;
                        bsChanged = true;
                    }
                    if (sensorName != null && sensorName.stringValue != "EnemyBufferSensor")
                    {
                        sensorName.stringValue = "EnemyBufferSensor";
                        bsChanged = true;
                    }
                    if (bsChanged)
                    {
                        bso.ApplyModifiedProperties();
                        changed = true;
                    }
                }

                // 3. enemyBufferSensor 참조 연결
                if (agent.enemyBufferSensor == null || agent.enemyBufferSensor != bufferSensor)
                {
                    Undo.RecordObject(agent, "Set enemyBufferSensor");
                    agent.enemyBufferSensor = bufferSensor;
                    EditorUtility.SetDirty(agent);
                    changed = true;
                }

                if (changed)
                {
                    modified++;
                    Debug.Log($"[DefenseSystemSetup] '{agent.gameObject.name}' 설정 완료: SpaceSize=11, StackedVectors=3, BufferSensor(4×10)");
                }
            }

            // DefenseEnvController의 defensePairs 자동 설정
            var envCtrl = Object.FindObjectOfType<DefenseEnvController>(true);
            if (envCtrl != null && agents.Length >= 2 && envCtrl.defensePairs.Count == 0)
            {
                Undo.RecordObject(envCtrl, "Setup DefensePairs");
                // 2개씩 페어로 묶기
                for (int i = 0; i + 1 < agents.Length; i += 2)
                {
                    var pair = new DefensePair
                    {
                        agent1 = agents[i],
                        agent2 = agents[i + 1]
                    };
                    envCtrl.defensePairs.Add(pair);
                }
                EditorUtility.SetDirty(envCtrl);
                Debug.Log($"[DefenseSystemSetup] DefensePairs {envCtrl.defensePairs.Count}개 자동 생성");
            }

            EditorUtility.DisplayDialog("Setup Defense Agents",
                $"{modified}개 에이전트 설정 완료.\n" +
                "- SpaceSize: 11\n" +
                "- StackedVectors: 3\n" +
                "- BufferSensor: ObsSize=4, Max=10\n" +
                "- Model: None (새 학습 필요)",
                "OK");
        }
    }
}
