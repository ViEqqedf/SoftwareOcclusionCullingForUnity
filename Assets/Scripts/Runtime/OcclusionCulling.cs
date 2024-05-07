using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using ViE.SOC.Runtime.Components;
using ViE.SOC.Runtime.Utils;

namespace ViE.SOC.Runtime {
    public class OcclusionCulling : MonoBehaviour {
        private const float minScreenRadiusForOccluder = 0.02f;

        private JobHandle dependency;

        private Plane[] tempPlanes;
        private NativeArray<FrustumPlane> frustumPlaneArr;
        private List<MeshFilter> mfList;
        private NativeList<CullingItem> cullingItemList;

        private NativeList<CullingItem> occluderList;
        private NativeList<CullingItem> occludeeList;

        private List<Vector3> cullingOccluderVertexList;
        private List<Vector3> cullingOccludeeVertexList;
        private CullingVertexInfo[] cullingOccluderVertexTempArr;
        private CullingVertexInfo[] cullingOccludeeVertexTempArr;
        private NativeArray<CullingVertexInfo> cullingOccluderVertexArr;
        private NativeArray<CullingVertexInfo> cullingOccludeeVertexArr;
        private NativeArray<float3> cullingOccluderProjVertexArr;
        private NativeArray<float3> cullingOccludeeProjVertexArr;
        private NativeList<float4x4> occluderModelMatrixList;
        private NativeList<float4x4> occludeeModelMatrixList;

        private void Start() {
            JobsUtility.JobWorkerCount = 4;

            tempPlanes = new Plane[6];
            frustumPlaneArr = new NativeArray<FrustumPlane>(6, Allocator.Persistent);
            mfList = new List<MeshFilter>();
            cullingItemList = new NativeList<CullingItem>(Allocator.Persistent);

            occluderList = new NativeList<CullingItem>(Allocator.Persistent);
            occludeeList = new NativeList<CullingItem>(Allocator.Persistent);

            cullingOccluderVertexList = new List<Vector3>();
            cullingOccludeeVertexList = new List<Vector3>();
            cullingOccluderVertexTempArr = new CullingVertexInfo[1048576];
            cullingOccludeeVertexTempArr = new CullingVertexInfo[1048576];
            cullingOccluderVertexArr = new NativeArray<CullingVertexInfo>(1048576, Allocator.Persistent);
            cullingOccludeeVertexArr = new NativeArray<CullingVertexInfo>(1048576, Allocator.Persistent);
            cullingOccluderProjVertexArr = new NativeArray<float3>(1048576, Allocator.Persistent);
            cullingOccludeeProjVertexArr = new NativeArray<float3>(1048576, Allocator.Persistent);
            occluderModelMatrixList = new NativeList<float4x4>(Allocator.Persistent);
            occludeeModelMatrixList = new NativeList<float4x4>(Allocator.Persistent);
        }

        private void OnDestroy() {
            dependency.Complete();

            frustumPlaneArr.Dispose();
            cullingItemList.Dispose();
            occluderList.Dispose();
            occludeeList.Dispose();
            cullingOccluderVertexArr.Dispose();
            cullingOccludeeVertexArr.Dispose();
            cullingOccluderProjVertexArr.Dispose();
            cullingOccludeeProjVertexArr.Dispose();
            occluderModelMatrixList.Dispose();
            occludeeModelMatrixList.Dispose();

            JobsUtility.ResetJobWorkerCount();
        }

        private void Update() {
            dependency.Complete();

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
                // TODO: 使用额外的容器来做修改，然后 CopyTo
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
            VerticesTransfer(camera);
        }

        private void VerticesTransfer(Camera camera) {
            // cullingOccluderVertexArr.Clear();
            // cullingOccludeeVertexArr.Clear();
            cullingOccluderVertexList.Clear();
            cullingOccludeeVertexList.Clear();
            occluderModelMatrixList.Clear();
            occludeeModelMatrixList.Clear();

            var vMatrix = camera.worldToCameraMatrix;
            var pMatrix = camera.projectionMatrix;
            var vpMatrix = vMatrix * pMatrix;

            Profiler.BeginSample("[ViE] OccluderTransfer");
            #region Occluder Transfer
            int occluderVertexArrLength = 0;
            int mMatrixIdx = 0;

            for (int i = 0, count = occluderList.Length; i < count; i++) {
                var occluder = occluderList[i];
                MeshFilter mf = mfList[occluder.index];

                Mesh mesh = mf.mesh;
                mesh.GetVertices(cullingOccluderVertexList);

                for (int j = 0, vertexCount = cullingOccluderVertexList.Count; j < vertexCount; j++) {
                    Vector3 vertex = cullingOccluderVertexList[j];
                    cullingOccluderVertexTempArr[occluderVertexArrLength++] = new CullingVertexInfo() {
                        vertex = vertex,
                        modelMatrixIndex = mMatrixIdx,
                    };

                    // cullingOccluderVertexArr.Add(new CullingVertexInfo() {
                    //     vertex = vertex,
                    //     modelMatrixIndex = mMatrixIdx,
                    // });
                }

                float4x4 mMatrix = mf.transform.localToWorldMatrix;
                occluderModelMatrixList.Add(mMatrix);
                mMatrixIdx++;
            }

            cullingOccluderVertexArr.CopyFrom(cullingOccluderVertexTempArr);

            dependency = new CullingVerticesTransferJob() {
                vpMatrix = vpMatrix,
                vertices = cullingOccluderVertexArr,
                clipVerticesResult = cullingOccluderProjVertexArr,
            }.Schedule(occluderVertexArrLength, 10, dependency);
            #endregion
            Profiler.EndSample();

            Profiler.BeginSample("[ViE] OccludeeTransfer");
            #region Occludee Transfer
            int occludeeVertexArrLength = 0;

            for (int i = 0, count = occludeeList.Length; i < count; i++) {
                var occludee = occludeeList[i];
                MeshFilter mf = mfList[occludee.index];

                Bounds bounds = mf.mesh.bounds;
                Vector3 max = bounds.max;
                Vector3 min = bounds.min;
                #region Add Vertices
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = max,
                    modelMatrixIndex = mMatrixIdx,
                };
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = new float3(bounds.min.x, bounds.max.y, bounds.max.z),
                    modelMatrixIndex = mMatrixIdx,
                };
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = new float3(bounds.min.x, bounds.max.y, bounds.min.z),
                    modelMatrixIndex = mMatrixIdx,
                };
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = new float3(bounds.max.x, bounds.max.y, bounds.min.z),
                    modelMatrixIndex = mMatrixIdx,
                };
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = new float3(bounds.max.x, bounds.min.y, bounds.max.z),
                    modelMatrixIndex = mMatrixIdx,
                };
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = new float3(bounds.min.x, bounds.min.y, bounds.max.z),
                    modelMatrixIndex = mMatrixIdx,
                };
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = min,
                    modelMatrixIndex = mMatrixIdx,
                };
                cullingOccludeeVertexTempArr[occludeeVertexArrLength++] = new CullingVertexInfo() {
                    vertex = new float3(bounds.max.x, bounds.min.y, bounds.min.z),
                    modelMatrixIndex = mMatrixIdx,
                };
                #endregion

                float4x4 mMatrix = mf.transform.localToWorldMatrix;
                occludeeModelMatrixList.Add(mMatrix);
                mMatrixIdx++;
            }

            cullingOccludeeVertexArr.CopyFrom(cullingOccludeeVertexTempArr);

            dependency = new CullingVerticesTransferJob() {
                vpMatrix = vpMatrix,
                vertices = cullingOccludeeVertexArr,
                clipVerticesResult = cullingOccludeeProjVertexArr,
            }.Schedule(occludeeVertexArrLength, 10, dependency);
            #endregion
            Profiler.EndSample();
        }

        [BurstCompile]
        private struct CullingVerticesTransferJob : IJobParallelFor {
            public float4x4 vpMatrix;
            [ReadOnly] public NativeArray<CullingVertexInfo> vertices;
            public NativeArray<float3> clipVerticesResult;

            public void Execute(int index) {
                float3 vertex = vertices[index].vertex;
                float4 mulResult = math.mul(vpMatrix, new float4(vertex.x, vertex.y, vertex.z, 0));
                clipVerticesResult[index] = new float3(mulResult.x, mulResult.y, mulResult.z);
            }
        }

        #endregion
    }
}