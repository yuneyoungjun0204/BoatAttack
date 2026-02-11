using UnityEngine;

/// <summary>
/// TAG 디버깅 도구 - 씬의 모든 선박과 TAG 확인
/// </summary>
public class DebugTagChecker : MonoBehaviour
{
    [Header("Check Tags")]
    public string[] tagsToCheck = { "Friendly", "attack_boat", "MotherShip" };
    
    [Header("Debug Settings")]
    public bool logOnStart = true;
    public bool logEveryFrame = false;
    public float logInterval = 5f;  // 초
    
    private float nextLogTime = 0f;

    void Start()
    {
        if (logOnStart)
        {
            LogAllTags();
        }
    }

    void Update()
    {
        if (logEveryFrame)
        {
            LogAllTags();
        }
        else if (Time.time >= nextLogTime)
        {
            LogAllTags();
            nextLogTime = Time.time + logInterval;
        }
    }

    [ContextMenu("Log All Tags")]
    public void LogAllTags()
    {
        Debug.Log("========================================");
        Debug.Log("=== TAG CHECK START ===");
        Debug.Log("========================================");

        // 모든 GameObject 수집
        GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();
        Debug.Log($"Total GameObjects in scene: {allObjects.Length}");

        // 각 TAG별 검색
        foreach (string tagName in tagsToCheck)
        {
            try
            {
                GameObject[] tagged = GameObject.FindGameObjectsWithTag(tagName);
                Debug.Log($"\n--- TAG: '{tagName}' ---");
                Debug.Log($"Count: {tagged.Length}");

                if (tagged.Length > 0)
                {
                    foreach (GameObject obj in tagged)
                    {
                        Debug.Log($"  ✓ {obj.name} (Tag: {obj.tag})");
                        
                        // Rigidbody 확인
                        Rigidbody rb = obj.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            Debug.Log($"    → Rigidbody: ✓ (Velocity: {rb.velocity.magnitude:F2} m/s)");
                        }
                        else
                        {
                            Debug.Log($"    → Rigidbody: ✗ (속도 정보 수집 불가)");
                        }
                    }
                }
                else
                {
                    Debug.LogWarning($"  ⚠ No objects found with tag '{tagName}'");
                }
            }
            catch (UnityException e)
            {
                Debug.LogError($"  ✗ Tag '{tagName}' does not exist in Tag Manager!");
                Debug.LogError($"    Error: {e.Message}");
                Debug.LogError($"    → Go to: Edit > Project Settings > Tags and Layers");
            }
        }

        Debug.Log("\n========================================");
        Debug.Log("=== TAG CHECK END ===");
        Debug.Log("========================================\n");
    }

    /// <summary>
    /// 특정 TAG가 존재하는지 확인
    /// </summary>
    public bool TagExists(string tagName)
    {
        try
        {
            GameObject.FindGameObjectsWithTag(tagName);
            return true;
        }
        catch (UnityException)
        {
            return false;
        }
    }
}
