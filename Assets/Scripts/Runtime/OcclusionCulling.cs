using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using ViE.SOC.Runtime.Components;
using ViE.SOC.Runtime.Utils;

namespace ViE.SOC.Runtime {
    public class OcclusionCulling : MonoBehaviour {
        private const int DEFAULT_CONTAINER_SIZE = 1048576;
        private const float MIN_SCREEN_RADIUS_FOR_OCCLUDER = 0.02f;
        private const int FRAMEBUFFER_WIDTH = 256;
        private const int FRAMEBUFFER_HEIGHT = 128;
        private static readonly float4x4 fbMatrix = new float4x4(
            new float4(FRAMEBUFFER_WIDTH * 0.5f, 0, 0, 0),
            new float4(0, FRAMEBUFFER_WIDTH * 0.5f, 0, FRAMEBUFFER_HEIGHT * 0.5f),
            new float4(0, 0, 1, 0),
            new float4(0, 0, 0, 1));

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
        private NativeArray<float4> cullingOccluderProjVertexArr;
        private NativeArray<float4> cullingOccludeeProjVertexArr;
        private List<int> tempTriList;
        private List<int4> cullingOccluderTriList;
        private NativeList<int4> cullingOccluderTriNativeList;
        private NativeList<float4x4> occluderModelMatrixList;
        private NativeList<float4x4> occludeeModelMatrixList;

        private NativeArray<TriangleInfo> occluderScreenTriArr;
        private NativeArray<TriangleInfo> occludeeScreenTriArr;

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
            cullingOccluderVertexTempArr = new CullingVertexInfo[DEFAULT_CONTAINER_SIZE];
            cullingOccludeeVertexTempArr = new CullingVertexInfo[DEFAULT_CONTAINER_SIZE];
            cullingOccluderVertexArr = new NativeArray<CullingVertexInfo>(DEFAULT_CONTAINER_SIZE, Allocator.Persistent);
            cullingOccludeeVertexArr = new NativeArray<CullingVertexInfo>(DEFAULT_CONTAINER_SIZE, Allocator.Persistent);
            cullingOccluderProjVertexArr = new NativeArray<float4>(DEFAULT_CONTAINER_SIZE, Allocator.Persistent);
            cullingOccludeeProjVertexArr = new NativeArray<float4>(DEFAULT_CONTAINER_SIZE, Allocator.Persistent);
            tempTriList = new List<int>();
            cullingOccluderTriList = new List<int4>();
            cullingOccluderTriNativeList = new NativeList<int4>(Allocator.Persistent);
            occluderModelMatrixList = new NativeList<float4x4>(Allocator.Persistent);
            occludeeModelMatrixList = new NativeList<float4x4>(Allocator.Persistent);

            occluderScreenTriArr = new NativeArray<TriangleInfo>(DEFAULT_CONTAINER_SIZE / 3, Allocator.Persistent);
            occludeeScreenTriArr = new NativeArray<TriangleInfo>(DEFAULT_CONTAINER_SIZE / 3, Allocator.Persistent);
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
            cullingOccluderTriNativeList.Dispose();
            occluderModelMatrixList.Dispose();
            occludeeModelMatrixList.Dispose();

            occluderScreenTriArr.Dispose();
            occludeeScreenTriArr.Dispose();

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

                if (!item.hasTransparentMat && itemScreenSize > MIN_SCREEN_RADIUS_FOR_OCCLUDER) {
                    occluderList.Add(item);
                    // mrList[item.index].enabled = true;
                    // Debug.Log($"[ViE] 屏占比达标");
                } else {
                    // mrList[item.index].enabled = false;
                    // Debug.Log($"[ViE] 屏占比不达标");
                }

                occludeeList.Add(item);
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

        private void ProcessGeometry(Camera camera) {
            VerticesTransfer(camera);
            TriangleHandle();
        }

        #region VerticeTransfer
        private void VerticesTransfer(Camera camera) {
            // cullingOccluderVertexArr.Clear();
            // cullingOccludeeVertexArr.Clear();
            cullingOccluderVertexList.Clear();
            cullingOccludeeVertexList.Clear();
            occluderModelMatrixList.Clear();
            occludeeModelMatrixList.Clear();
            cullingOccluderTriList.Clear();

            var vMatrix = camera.worldToCameraMatrix;
            var pMatrix = camera.projectionMatrix;

            var vpMatrix = pMatrix * vMatrix;

            Profiler.BeginSample("[ViE] OccluderTransfer");
            #region Occluder Transfer
            int occluderVertexArrLength = 0;
            int mMatrixIdx = 0;
            int triCount = 0;

            for (int i = 0, count = occluderList.Length; i < count; i++) {
                var occluder = occluderList[i];
                MeshFilter mf = mfList[occluder.index];

                Mesh mesh = mf.mesh;
                Profiler.BeginSample("[ViE] OccluderTransfer-GetVertices");
                mesh.GetVertices(cullingOccluderVertexList);
                Profiler.EndSample();

                Profiler.BeginSample("[ViE] OccluderTransfer-GetTriangles");
                tempTriList.Clear();
                mesh.GetTriangles(tempTriList, 0);
                int tempTriCount = tempTriList.Count;
                for (int j = 0; j < tempTriCount; j += 3) {
                    cullingOccluderTriList.Add(new int4(tempTriList[j], tempTriList[j + 1], tempTriList[j + 2], triCount));
                }

                triCount += tempTriCount;
                Profiler.EndSample();

                Profiler.BeginSample("[ViE] OccluderTransfer-InnerLoop");
                for (int j = 0, vertexCount = cullingOccluderVertexList.Count; j < vertexCount; j++) {
                    Vector3 vertex = cullingOccluderVertexList[j];
                    cullingOccluderVertexTempArr[occluderVertexArrLength++] = new CullingVertexInfo() {
                        vertex = vertex,
                        modelMatrixIndex = mMatrixIdx,
                    };
                }
                Profiler.EndSample();

                Profiler.BeginSample("[ViE] OccluderTransfer-GetLocalToWorldMatrix");
                float4x4 mMatrix = mf.transform.localToWorldMatrix;
                Profiler.EndSample();
                occluderModelMatrixList.Add(mMatrix);
                mMatrixIdx++;
            }

            cullingOccluderVertexArr.CopyFrom(cullingOccluderVertexTempArr);

            dependency = new CullingVerticesTransferJob() {
                vpMatrix = vpMatrix,
                vertices = cullingOccluderVertexArr,
                modelMatrixList = occluderModelMatrixList,
                clipVerticesResult = cullingOccluderProjVertexArr,
            }.Schedule(occluderVertexArrLength, 10, dependency);
            #endregion
            Profiler.EndSample();

            Profiler.BeginSample("[ViE] OccludeeTransfer");
            #region Occludee Transfer
            int occludeeVertexArrLength = 0;
            mMatrixIdx = 0;

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
                modelMatrixList = occludeeModelMatrixList,
                clipVerticesResult = cullingOccludeeProjVertexArr,
            }.Schedule(occludeeVertexArrLength, 10, dependency);
            #endregion
            Profiler.EndSample();
        }

        [BurstCompile]
        private struct CullingVerticesTransferJob : IJobParallelFor {
            public float4x4 vpMatrix;
            [ReadOnly] public NativeArray<CullingVertexInfo> vertices;
            [ReadOnly] public NativeList<float4x4> modelMatrixList;
            public NativeArray<float4> clipVerticesResult;

            public void Execute(int index) {
                CullingVertexInfo vertexInfo = vertices[index];
                float3 vertex = vertexInfo.vertex;
                var mvp = math.mul(vpMatrix, modelMatrixList[vertexInfo.modelMatrixIndex]);
                float4 mulResult = math.mul(mvp, new float4(vertex.x, vertex.y, vertex.z, 1));
                clipVerticesResult[index] = mulResult;
            }
        }
        #endregion

        #region TriangleHandle

        private void TriangleHandle() {
            cullingOccluderTriNativeList.Clear();

            int occluderTriCount = cullingOccluderTriList.Count;
            for (int i = 0; i < occluderTriCount; i++) {
                cullingOccluderTriNativeList.Add(cullingOccluderTriList[i]);
            }

            dependency = new OccluderTriHandleJob() {
                triIdxArr = cullingOccluderTriNativeList.AsArray(),
                vertexArr = cullingOccluderProjVertexArr,
                screenTriArr = occluderScreenTriArr,
            }.Schedule(occluderTriCount, 10, dependency);

            dependency = new OccludeeTriHandleJob() {
                vertexArr = cullingOccludeeProjVertexArr,
                screenTriArr = occluderScreenTriArr,
            }.Schedule(occludeeList.Length, 10, dependency);
        }

        [BurstCompile]
        private struct OccluderTriHandleJob : IJobParallelFor {
            public NativeArray<int4> triIdxArr;
            [ReadOnly]
            public NativeArray<float4> vertexArr;
            [NativeDisableParallelForRestriction]
            public NativeArray<TriangleInfo> screenTriArr;

            public void Execute(int index) {
                int4 curTri = triIdxArr[index];
                float4 fst = vertexArr[curTri.x + curTri.w];
                float4 snd = vertexArr[curTri.y + curTri.w];
                float4 trd = vertexArr[curTri.z + curTri.w];

                bool needCulling = CullingUtils.TriangleCulling(fst, snd, trd);
                if (!needCulling) {
                    // ndc
                    float4 ndcFst = fst / fst.w;
                    float4 ndcSnd = snd / snd.w;
                    float4 ndcTrd = trd / trd.w;

                    // screen
                    float4 screenFst = math.mul(fbMatrix, ndcFst);
                    float4 screenSnd = math.mul(fbMatrix, ndcSnd);
                    float4 screenTrd = math.mul(fbMatrix, ndcTrd);

                    // vertex sorting & save
                    float4 v0 = screenFst;
                    float4 v1 = default;
                    float4 v2 = default;

                    if (screenSnd.y < v0.y) {
                        v1 = v0;
                        v0 = screenSnd;
                    } else {
                        v1 = screenSnd;
                    }

                    if (screenTrd.y < v0.y) {
                        v2 = v0;
                        v0 = screenTrd;
                    } else {
                        v2 = screenTrd;
                    }

                    if (v2.x > v1.x) {
                        (v1, v2) = (v2, v1);
                    }

                    // mid vertex
                    short midVertexIdx = v1.y < v2.y ? (short)1 : (short)2;

                    // depth
                    float farthestDepth = v0.z;

                    // TODO: Reverse-Z 后重新确认深度方向
                    if (v1.z > farthestDepth) {
                        farthestDepth = v1.z;
                    }

                    if (v2.z > farthestDepth) {
                        farthestDepth = v2.z;
                    }

                    screenTriArr[index] = new TriangleInfo() {
                        v0 = v0,
                        v1 = v1,
                        v2 = v2,
                        depth = farthestDepth,
                        midVertexIdx = midVertexIdx,
                    };

                    Debug.Log($"[ViE] 三角形{index} 的 v0({v0}) 深度为 {v0.z}");
                    Debug.Log($"[ViE] 三角形{index} 的 v1({v1}) 深度为 {v1.z}");
                    Debug.Log($"[ViE] 三角形{index} 的 v2({v2}) 深度为 {v2.z}");
                }
            }
        }

        [BurstCompile]
        private struct OccludeeTriHandleJob : IJobParallelFor {
            [ReadOnly] public NativeArray<float4> vertexArr;
            [NativeDisableParallelForRestriction]
            public NativeArray<TriangleInfo> screenTriArr;

            public void Execute(int index) {
                int start = index * 8;
                int end = (index + 1) * 8 - 1;

                bool hasMin = false;
                float4 min = default;
                bool hasMax = false;
                float4 max = default;

                float minDepth = float.MaxValue;

                for (int i = start; i < end; i++) {
                    float4 vertex = vertexArr[i];
                    float4 ndcResult = vertex / vertex.w;
                    float4 screenResult = math.mul(fbMatrix, ndcResult);

                    if (!hasMin) {
                        min = screenResult;
                        hasMin = true;
                    }

                    if (!hasMax) {
                        max = screenResult;
                        hasMax = true;
                    }

                    if (screenResult.x < min.x && screenResult.y < min.y) {
                        min = screenResult;
                    }

                    if (screenResult.x > max.x && screenResult.y > max.y) {
                        max = screenResult;
                    }

                    if (minDepth > screenResult.z) {
                        minDepth = screenResult.z;
                    }
                }

                screenTriArr[index] = new TriangleInfo() {
                    v0 = min,
                    v1 = new float4(max.x, min.y, 0, 0),
                    v2 = max,
                    depth = minDepth,
                };
            }
        }

        #endregion
    }
}