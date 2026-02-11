using UnityEngine;
using System.Linq;

/// <summary>
/// 런타임 자동 설정 - Play 버튼만 누르면 자동으로 TAG 할당!
/// </summary>
public class RuntimeAutoSetup : MonoBehaviour
{
    [Header("Ship Name Patterns (자동 인식)")]
    [Tooltip("이름에 이 단어가 포함되면 Friendly로 분류")]
    public string[] friendlyKeywords = { "국군", "아군", "Player", "Friendly" };
    
    [Tooltip("이름에 이 단어가 포함되면 attack_boat로 분류")]
    public string[] attackKeywords = { "적군", "Enemy", "attack" };
    
    [Tooltip("이름에 이 단어가 포함되면 MotherShip로 분류")]
    public string[] motherKeywords = { "모선", "Mother", "Carrier" };

    [Header("Debug")]
    public bool showDebugLogs = true;

    void Awake()
    {
        // Play 버튼 누르면 자동 실행!
        AutoAssignTags();
    }

    void AutoAssignTags()
    {
        if (showDebugLogs)
        {
            Debug.Log("=== RuntimeAutoSetup: Auto-assigning Tags ===");
        }

        // 씬의 모든 GameObject 검색
        GameObject[] allObjects = FindObjectsOfType<GameObject>();
        
        int friendlyCount = 0;
        int attackedCount = 0;
        int motherCount = 0;

        foreach (GameObject obj in allObjects)
        {
            string objName = obj.name.ToLower();
            
            // Rigidbody가 있는 것만 (선박일 가능성)
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb == null) continue;

            // Friendly 키워드 확인
            bool isFriendly = friendlyKeywords.Any(k => objName.Contains(k.ToLower()));
            if (isFriendly)
            {
                if (obj.tag != "Friendly")
                {
                    obj.tag = "Friendly";
                    friendlyCount++;
                    if (showDebugLogs)
                        Debug.Log($"✓ {obj.name} → Tag: Friendly");
                }
                continue;
            }

            // attack_boat 키워드 확인
            bool isAttack = attackKeywords.Any(k => objName.Contains(k.ToLower()));
            if (isAttack)
            {
                if (obj.tag != "attack_boat")
                {
                    obj.tag = "attack_boat";
                    attackedCount++;
                    if (showDebugLogs)
                        Debug.Log($"✓ {obj.name} → Tag: attack_boat");
                }
                continue;
            }

            // MotherShip 키워드 확인
            bool isMother = motherKeywords.Any(k => objName.Contains(k.ToLower()));
            if (isMother)
            {
                if (obj.tag != "MotherShip")
                {
                    obj.tag = "MotherShip";
                    motherCount++;
                    if (showDebugLogs)
                        Debug.Log($"✓ {obj.name} → Tag: MotherShip");
                }
                continue;
            }
        }

        if (showDebugLogs)
        {
            Debug.Log($"=== Auto-assigned: Friendly({friendlyCount}), attack_boat({attackedCount}), MotherShip({motherCount}) ===");
        }
    }
}
