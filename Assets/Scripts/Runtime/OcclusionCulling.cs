using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using ViE.SOC.Runtime.Components;
using ViE.SOC.Runtime.Utils;

namespace ViE.SOC.Runtime {
    public class OcclusionCulling : MonoBehaviour {
        private const float minScreenRadiusForOccluder = 0.02f;

        private Plane[] tempPlanes;
        private NativeArray<FrustumPlane> frustumPlaneArr;
        private List<MeshRenderer> mrList;
        private NativeList<CullingItem> cullingItemList;

        private NativeList<CullingItem> occluderList;
        private NativeList<CullingItem> occludeeList;

        private void Start() {
            tempPlanes = new Plane[6];
            frustumPlaneArr = new NativeArray<FrustumPlane>(6, Allocator.Persistent);
            mrList = new List<MeshRenderer>();
            cullingItemList = new NativeList<CullingItem>(Allocator.Persistent);

            occluderList = new NativeList<CullingItem>(Allocator.Persistent);
            occludeeList = new NativeList<CullingItem>(Allocator.Persistent);
        }

        private void OnDestroy() {
            frustumPlaneArr.Dispose();
            cullingItemList.Dispose();
            occluderList.Dispose();
            occludeeList.Dispose();
        }

        private void Update() {
            Camera mainCamera = Camera.main;

            CullingReset();
            Profiler.BeginSample("[ViE] FrustumCulling");
            FrustumCulling(mainCamera);
            Profiler.EndSample();
            Profiler.BeginSample("[ViE] CollectCullingItem");
            CollectCullingItem(mainCamera);
            Profiler.EndSample();
            Profiler.BeginSample("[ViE] ProcessGeometry");
            ProcessGeometry(mainCamera);
            Profiler.EndSample();
        }

        private void CullingReset() {
            mrList.Clear();
            cullingItemList.Clear();
            occluderList.Clear();
            occludeeList.Clear();
        }

        #region FrustumCulling

        private void FrustumCulling(Camera camera) {
            GeometryUtility.CalculateFrustumPlanes(camera, tempPlanes);
            for (int i = 0; i < 6; i++) {
                frustumPlaneArr[i] = new FrustumPlane() {
                    normal = tempPlanes[i].normal,
                    disToOrigin = tempPlanes[i].distance,
                };
            }

            int cullingMRListLength = 0;

            MeshRenderer[] mrs = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var mr in mrs) {
                float3 center = mr.bounds.center;
                float radius = mr.bounds.extents.magnitude;
                Profiler.BeginSample("[ViE] FrustumCalculate");
                bool needCulling = CullingUtils.FrustumCulling(frustumPlaneArr, center, radius);
                Profiler.EndSample();

                if (needCulling) {
                    bool hasTransparentMat = false;
                    foreach (var mat in mr.materials) {
                        hasTransparentMat = CullingUtils.IsMaterialTransparent(mat);
                        if (hasTransparentMat) {
                            break;
                        }
                    }

                    mrList.Add(mr);
                    cullingItemList.Add(new CullingItem() {
                        center = center,
                        boundRadius = radius,
                        hasTransparentMat = hasTransparentMat,
                        index = cullingMRListLength++,
                    });
                }

                // if (needCulling) {
                //     Debug.Log($"[ViE] 在平截头内");
                //     mr.enabled = true;
                // } else {
                //     Debug.Log($"[ViE] 不在平截头内");
                //     mr.enabled = false;
                // }
            }
        }

        #endregion

        #region CollectCullingItem

        private void CollectCullingItem(Camera camera) {
            for(int i = 0, count = cullingItemList.Length; i < count; i++) {
                var item = cullingItemList[i];
                float itemScreenSize = CullingUtils.ComputeBoundsScreenSize(
                    camera.transform.position, item.center, item.boundRadius, camera.projectionMatrix);
                cullingItemList[i] = new CullingItem() {
                    center = item.center,
                    boundRadius = item.boundRadius,
                    screenSize = itemScreenSize,
                    hasTransparentMat = item.hasTransparentMat,
                    index = item.index,
                };
                item = cullingItemList[i];

                if (!item.hasTransparentMat && itemScreenSize > minScreenRadiusForOccluder) {
                    occluderList.Add(item);
                    // mrList[item.index].enabled = true;
                    // Debug.Log($"[ViE] 屏占比达标");
                } else {
                    occludeeList.Add(item);
                    // mrList[item.index].enabled = false;
                    // Debug.Log($"[ViE] 屏占比不达标");
                }
            }

            OccluderSorter occluderSorter = new OccluderSorter();
            occluderList.Sort(occluderSorter);
        }

        private struct OccluderSorter : IComparer<CullingItem> {
            public int Compare(CullingItem fst, CullingItem snd) {
                int screenSizeComparison = fst.screenSize.CompareTo(snd.screenSize);
                if (screenSizeComparison != 0) {
                    return -screenSizeComparison;
                }

                return fst.index.CompareTo(snd.index);
            }
        }

        #endregion

        #region ProcessGeometry

        private void ProcessGeometry(Camera camera) {
            var projMatrix = camera.projectionMatrix;
        }

        #endregion
    }
}