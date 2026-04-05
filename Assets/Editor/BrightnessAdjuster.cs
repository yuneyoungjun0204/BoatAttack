using UnityEngine;
using UnityEditor;

namespace BoatAttack.EditorTools
{
    public static class BrightnessAdjuster
    {
        [MenuItem("BoatAttack/Brightness/Apply Bright Settings")]
        public static void ApplyBrightSettings  ()
        {
            // 1. Directional Light 강도 올리기
            Light[] lights = Object.FindObjectsOfType<Light>();
            foreach (var light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    Undo.RecordObject(light, "Brighten Directional Light");
                    light.intensity = 2.2f;
                    light.color = new Color(1f, 0.97f, 0.9f); // 약간 따뜻한 흰색
                    EditorUtility.SetDirty(light);
                    Debug.Log($"[BrightnessAdjuster] Directional Light '{light.name}' intensity → 1.8");
                }
            }

            // 2. Ambient Light 밝게
            Undo.RecordObject(RenderSettings.skybox, "Brighten Ambient");
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.55f, 0.65f); // 밝은 하늘색 ambient
            RenderSettings.ambientIntensity = 1.2f;

            // 3. Fog 밝게 (있으면)
            if (RenderSettings.fog)
            {
                RenderSettings.fogColor = new Color(0.7f, 0.75f, 0.85f);
            }

            Debug.Log("[BrightnessAdjuster] Bright settings applied! Ctrl+Z to undo.");

            // 씬 변경 표시
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        }

        [MenuItem("BoatAttack/Brightness/Restore Default")]
        public static void RestoreDefault()
        {
            Light[] lights = Object.FindObjectsOfType<Light>();
            foreach (var light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    Undo.RecordObject(light, "Restore Directional Light");
                    light.intensity = 1.0f;
                    light.color = Color.white;
                    EditorUtility.SetDirty(light);
                }
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.0f;

            Debug.Log("[BrightnessAdjuster] Restored to default.");
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        }
    }
}
