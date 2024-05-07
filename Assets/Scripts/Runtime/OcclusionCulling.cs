using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using ViE.SOC.Runtime.Components;
using ViE.SOC.Runtime.Utils;

namespace ViE.SOC.Runtime {
    public class OcclusionCulling : MonoBehaviour {
        private const float minScreenRadiusForOccluder = 0.02f;

        private JobHandle tickJobHandle;

        private Plane[] tempPlanes;
        private NativeArray<FrustumPlane> frustumPlaneArr;
        private List<MeshFilter> mfList;
        private NativeList<CullingItem> cullingItemList;

        private NativeList<CullingItem> occluderList;
        private NativeList<CullingItem> occludeeList;

        private NativeList<CullingVertexInfo> cullingOccluderVertexList;
        private NativeList<CullingVertexInfo> cullingOccludeeVertexList;
        private NativeList<float4x4> cullingItemModelMatrixList;

        private void Start() {
            tempPlanes = new Plane[6];
            frustumPlaneArr = new NativeArray<FrustumPlane>(6, Allocator.Persistent);
            mfList = new List<MeshFilter>();
            cullingItemList = new NativeList<CullingItem>(Allocator.Persistent);

            occluderList = new NativeList<CullingItem>(Allocator.Persistent);
            occludeeList = new NativeList<CullingItem>(Allocator.Persistent);
            cullingOccluderVertexList = new NativeList<CullingVertexInfo>(Allocator.Persistent);
            cullingOccludeeVertexList = new NativeList<CullingVertexInfo>(Allocator.Persistent);
            cullingItemModelMatrixList = new NativeList<float4x4>(Allocator.Persistent);
        }

        private void OnDestroy() {
            frustumPlaneArr.Dispose();
            cullingItemList.Dispose();
            occluderList.Dispose();
            occludeeList.Dispose();
            cullingOccluderVertexList.Dispose();
            cullingOccludeeVertexList.Dispose();
            cullingItemModelMatrixList.Dispose();
        }

        private void Update() {
            tickJobHandle.Complete();

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
        }

        #region FrustumCulling

        private void FrustumCulling(Camera camera) {
            mfList.Clear();
            cullingItemList.Clear();

            GeometryUtility.CalculateFrustumPlanes(camera, tempPlanes);
            for (int i = 0; i < 6; i++) {
                frustumPlaneArr[i] = new FrustumPlane() {
                    normal = tempPlanes[i].normal,
                    disToOrigin = tempPlanes[i].distance,
                };
            }

            int cullingMFListLength = 0;

            MeshRenderer[] mrs = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var mr in mrs) {
                var bounds = mr.bounds;
                float3 center = bounds.center;
                float radius = bounds.extents.magnitude;
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

                    mfList.Add(mr.transform.GetComponent<MeshFilter>());
                    cullingItemList.Add(new CullingItem() {
                        center = center,
                        boundRadius = radius,
                        hasTransparentMat = hasTransparentMat,
                        index = cullingMFListLength++,
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
            occluderList.Clear();
            occludeeList.Clear();

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
            cullingOccluderVertexList.Clear();
            cullingOccludeeVertexList.Clear();
            cullingItemModelMatrixList.Clear();

            #region Occluder Transfer
            int mMatrixIdx = 0;

            for (int i = 0, count = occluderList.Length; i < count; i++) {
                var occluder = occluderList[i];
                MeshFilter mf = mfList[occluder.index];

                Mesh mesh = mf.mesh;
                for (int j = 0, vertexCount = mesh.vertices.Length; j < vertexCount; j++) {
                    Vector3 vertex = mesh.vertices[j];

                    cullingOccluderVertexList.Add(new CullingVertexInfo() {
                        vertex = vertex,
                        modelMatrixIndex = mMatrixIdx,
                    });
                }

                float4x4 mMatrix = mf.transform.localToWorldMatrix;
                cullingItemModelMatrixList.Add(mMatrix);
                mMatrixIdx++;
            }
            #endregion

            #region Occludee Transfer
            for (int i = 0, count = occludeeList.Length; i < count; i++) {
                var occludee = occludeeList[i];
                MeshFilter mf = mfList[occludee.index];

                Bounds bounds = mf.mesh.bounds;
                Vector3 max = bounds.max;
                Vector3 min = bounds.min;
                #region Add Vertices
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = max,
                    modelMatrixIndex = mMatrixIdx,
                });
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = new float3(bounds.min.x, bounds.max.y, bounds.max.z),
                    modelMatrixIndex = mMatrixIdx,
                });
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = new float3(bounds.min.x, bounds.max.y, bounds.min.z),
                    modelMatrixIndex = mMatrixIdx,
                });
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = new float3(bounds.max.x, bounds.max.y, bounds.min.z),
                    modelMatrixIndex = mMatrixIdx,
                });
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = new float3(bounds.max.x, bounds.min.y, bounds.max.z),
                    modelMatrixIndex = mMatrixIdx,
                });
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = new float3(bounds.min.x, bounds.min.y, bounds.max.z),
                    modelMatrixIndex = mMatrixIdx,
                });
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = min,
                    modelMatrixIndex = mMatrixIdx,
                });
                cullingOccludeeVertexList.Add(new CullingVertexInfo() {
                    vertex = new float3(bounds.max.x, bounds.min.y, bounds.min.z),
                    modelMatrixIndex = mMatrixIdx,
                });
                #endregion

                float4x4 mMatrix = mf.transform.localToWorldMatrix;
                cullingItemModelMatrixList.Add(mMatrix);
                mMatrixIdx++;
            }
            #endregion

            var vMatrix = camera.worldToCameraMatrix;
            var pMatrix = camera.projectionMatrix;
            tickJobHandle = new VertexTransformJob() {
                vMatrix = vMatrix,
                pMatrix = pMatrix,
            }.Schedule(1000, 100);
        }

        private struct VertexTransformJob : IJobParallelFor {
            public float4x4 vMatrix;
            public float4x4 pMatrix;

            public void Execute(int index) {
            }
        }

        #endregion
    }
}