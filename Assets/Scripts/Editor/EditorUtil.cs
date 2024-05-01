using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ViE.SOC.Editor {
    public static class EditorUtil {
        [MenuItem("ViE Tools/清理打开场景的无效组件")]
        public static void ClearMissingComponentOfGameObjectInScene() {
            Scene scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects()) {
                RemoveMissingComponentOfGameObject(root);
            }

            EditorSceneManager.MarkSceneDirty(scene);
        }

        [MenuItem("ViE Tools/清理预制无效组件")]
        public static void ClearPrefabMissingComponent() {
            Debug.Log($"[ViE] 清理开始");

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new string[] { "Assets" });
            for (int i = 0, count = guids.Length; i < count; i++) {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                GameObject prefabGo = AssetDatabase.LoadAssetAtPath(prefabPath, typeof(GameObject)) as GameObject;
                RemoveMissingComponentOfGameObject(prefabGo);

                PrefabUtility.SavePrefabAsset(prefabGo);
            }

            Debug.Log($"[ViE] 清理结束");
        }

        private static void RemoveMissingComponentOfGameObject(GameObject go) {
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);

            for (int i = 0, count = go.transform.childCount; i < count; i++) {
                RemoveMissingComponentOfGameObject(go.transform.GetChild(i).gameObject);
            }
        }
    }
}