using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// 원클릭 자동 설정 - Menu에서 실행!
/// </summary>
public class EasySetup : MonoBehaviour
{
#if UNITY_EDITOR
    [MenuItem("Tools/RoonShot/Auto Setup Everything")]
    public static void AutoSetupEverything()
    {
        Debug.Log("========================================");
        Debug.Log("=== EASY SETUP START ===");
        Debug.Log("========================================\n");
        
        // 1. TAG 생성
        CreateTags();
        
        // 2. RLManager 생성
        CreateRLManager();
        
        // 3. 선박에 TAG 자동 할당
        AutoAssignShipTags();
        
        Debug.Log("\n========================================");
        Debug.Log("=== SETUP COMPLETE! ===");
        Debug.Log("========================================");
        Debug.Log("✅ 이제 Play 버튼을 누르세요!");
        
        // Scene 저장
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene()
        );
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
    }
    
    static void CreateTags()
    {
        Debug.Log("--- Creating Tags ---");
        
        string[] requiredTags = { "Friendly", "attack_boat", "MotherShip" };
        
        SerializedObject tagManager = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]
        );
        SerializedProperty tagsProp = tagManager.FindProperty("tags");
        
        foreach (string tag in requiredTags)
        {
            // 이미 존재하는지 확인
            bool found = false;
            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                SerializedProperty t = tagsProp.GetArrayElementAtIndex(i);
                if (t.stringValue.Equals(tag))
                {
                    found = true;
                    break;
                }
            }
            
            if (!found)
            {
                tagsProp.InsertArrayElementAtIndex(0);
                SerializedProperty newTag = tagsProp.GetArrayElementAtIndex(0);
                newTag.stringValue = tag;
                tagManager.ApplyModifiedProperties();
                Debug.Log($"✓ Created tag: {tag}");
            }
            else
            {
                Debug.Log($"✓ Tag already exists: {tag}");
            }
        }
    }
    
    static void CreateRLManager()
    {
        Debug.Log("\n--- Creating RLManager ---");
        
        GameObject rlManager = GameObject.Find("RLManager");
        
        if (rlManager == null)
        {
            rlManager = new GameObject("RLManager");
            Debug.Log("✓ Created RLManager GameObject");
        }
        else
        {
            Debug.Log("✓ RLManager already exists");
        }
        
        // MultiAgentServer 컴포넌트
        MultiAgentServer server = rlManager.GetComponent<MultiAgentServer>();
        if (server == null)
        {
            server = rlManager.AddComponent<MultiAgentServer>();
            Debug.Log("✓ Added MultiAgentServer component");
        }
        
        // 설정
        server.port = 9876;
        server.shipTags = new string[] { "Friendly", "attack_boat", "MotherShip" };
        server.sendInterval = 0.1f;
        
        Debug.Log("✓ Configured MultiAgentServer");
        
        EditorUtility.SetDirty(rlManager);
    }
    
    static void AutoAssignShipTags()
    {
        Debug.Log("\n--- Auto-Assigning Tags ---");
        
        // 씬의 모든 GameObject
        GameObject[] allObjects = Object.FindObjectsOfType<GameObject>();
        
        Dictionary<string, string> shipTags = new Dictionary<string, string>();
        int assignedCount = 0;
        
        foreach (GameObject obj in allObjects)
        {
            // Rigidbody가 있는 것만 (선박일 가능성)
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb == null) continue;
            
            string objName = obj.name.ToLower();
            string assignedTag = null;
            
            // 이름 기반 TAG 할당
            if (objName.Contains("국군") || objName.Contains("아군") || 
                objName.Contains("player") || objName.Contains("friendly"))
            {
                assignedTag = "Friendly";
            }
            else if (objName.Contains("적군") || objName.Contains("enemy") || 
                     objName.Contains("attack"))
            {
                assignedTag = "attack_boat";
            }
            else if (objName.Contains("모선") || objName.Contains("mother") || 
                     objName.Contains("carrier"))
            {
                assignedTag = "MotherShip";
            }
            
            if (assignedTag != null && obj.tag != assignedTag)
            {
                obj.tag = assignedTag;
                Debug.Log($"✓ {obj.name} → Tag: {assignedTag}");
                EditorUtility.SetDirty(obj);
                assignedCount++;
            }
        }
        
        if (assignedCount == 0)
        {
            Debug.LogWarning("⚠️ No ships found to assign tags!");
            Debug.LogWarning("   Please check ship GameObject names.");
        }
        else
        {
            Debug.Log($"\n✅ Assigned {assignedCount} tags!");
        }
    }
#endif
}
