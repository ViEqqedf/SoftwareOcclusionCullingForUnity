using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using ViE.SOC.Runtime.Utils;

namespace ViE.SOC.Runtime {
    public class OcclusionCulling : MonoBehaviour {
        private Plane[] tempPlanes;
        private NativeArray<FrustumPlane> frustumPlanes;

        private void Start() {
            tempPlanes = new Plane[6];
            frustumPlanes = new NativeArray<FrustumPlane>(6, Allocator.Persistent);
        }

        private void OnDestroy() {
            frustumPlanes.Dispose();
        }

        private void Update() {
            FrustumCulling();
            CollectCullingItem();
        }

        private void FrustumCulling() {
             Profiler.BeginSample("[ViE] FrustumCulling");

             Camera mainCamera = Camera.main;
             GeometryUtility.CalculateFrustumPlanes(mainCamera, tempPlanes);
             for (int i = 0; i < 6; i++) {
                 frustumPlanes[i] = new FrustumPlane() {
                     normal = tempPlanes[i].normal,
                     disToOrigin = tempPlanes[i].distance,
                 };
             }

             MeshRenderer[] mrs = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
             foreach (var mr in mrs) {
                 float radius = mr.bounds.extents.magnitude;
                 Profiler.BeginSample("[ViE] FrustumCalculate");
                 bool needCulling = MathUtils.FrustumCulling(frustumPlanes, mr.bounds.center, radius);
                 Profiler.EndSample();

                 if (!needCulling) {
                     mr.enabled = true;
                 } else {
                     mr.enabled = false;
                 }
             }

             Profiler.EndSample();
        }

        private void CollectCullingItem() {

        }
    }
}