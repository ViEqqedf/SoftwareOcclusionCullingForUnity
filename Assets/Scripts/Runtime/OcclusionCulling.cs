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
        public RenderTexture frameBufferRT;

        private const int DEFAULT_CONTAINER_SIZE = 1048576;
        private const float MIN_SCREEN_RADIUS_FOR_OCCLUDER = 0.02f;
        private const int FRAMEBUFFER_BIN_WIDTH = 64;
        private const int FRAMEBUFFER_WIDTH = FRAMEBUFFER_BIN_WIDTH * 4;
        private const int FRAMEBUFFER_HEIGHT = 128;
        private static readonly float4x4 fbMatrix = new float4x4(
            new float4(FRAMEBUFFER_WIDTH * 0.5f, 0, 0, FRAMEBUFFER_WIDTH * 0.5f),
            new float4(0, FRAMEBUFFER_HEIGHT * 0.5f, 0, FRAMEBUFFER_HEIGHT * 0.5f),
            new float4(0, 0, 1, 0),
            new float4(0, 0, 0, 1));

        private JobHandle dependency;

        // Frustum Culling
        private Plane[] tempPlanes;
        private NativeArray<FrustumPlane> frustumPlaneArr;
        private List<MeshFilter> mfList;
        private NativeList<CullingItem> cullingItemList;

        private NativeList<CullingItem> occluderList;
        private NativeList<CullingItem> occludeeList;

        // Geomerty Process
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

        // Triangle Process
        private NativeArray<TriangleInfo> occluderScreenTriArr;
        private NativeArray<TriangleInfo> occludeeScreenTriArr;

        // Rasterization
        private NativeArray<ulong> frameBufferFstBin;
        private NativeArray<ulong> frameBufferSndBin;
        private NativeArray<ulong> frameBufferTrdBin;
        private NativeArray<ulong> frameBufferFthBin;
        private NativeArray<TriangleInfo> frameBufferFstTriBin;
        private NativeArray<TriangleInfo> frameBufferSndTriBin;
        private NativeArray<TriangleInfo> frameBufferTrdTriBin;
        private NativeArray<TriangleInfo> frameBufferFthTriBin;

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

            frameBufferFstBin = new NativeArray<ulong>(128, Allocator.Persistent);
            frameBufferSndBin = new NativeArray<ulong>(128, Allocator.Persistent);
            frameBufferTrdBin = new NativeArray<ulong>(128, Allocator.Persistent);
            frameBufferFthBin = new NativeArray<ulong>(128, Allocator.Persistent);
            frameBufferFstTriBin = new NativeArray<TriangleInfo>(128, Allocator.Persistent);
            frameBufferSndTriBin = new NativeArray<TriangleInfo>(128, Allocator.Persistent);
            frameBufferTrdTriBin = new NativeArray<TriangleInfo>(128, Allocator.Persistent);
            frameBufferFthTriBin = new NativeArray<TriangleInfo>(128, Allocator.Persistent);
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

            frameBufferFstBin.Dispose();
            frameBufferSndBin.Dispose();
            frameBufferTrdBin.Dispose();
            frameBufferFthBin.Dispose();
            frameBufferFstTriBin.Dispose();
            frameBufferSndTriBin.Dispose();
            frameBufferTrdTriBin.Dispose();
            frameBufferFthTriBin.Dispose();

            JobsUtility.ResetJobWorkerCount();
        }

        private void Update() {
            dependency.Complete();
            TestingDrawRT();

            Camera mainCamera = Camera.main;

            CullingReset();
            Profiler.BeginSample("[ViE] FrustumCulling");
            FrustumCulling(mainCamera);
            Profiler.EndSample();
            Profiler.BeginSample("[ViE] CollectCullingItem");
            CollectCullingItem(mainCamera);
            Profiler.EndSample();
            Profiler.BeginSample("[ViE] ProcessGeometry");
            VerticesTransfer(mainCamera);
            TriangleHandle(out int occluderTriCount, out int occludeeTriCount);
            Profiler.EndSample();
            Profiler.BeginSample("[ViE] Rasterization");
            Rasterization(occluderTriCount, occludeeTriCount);
            Profiler.EndSample();
        }

        private void TestingDrawRT() {
            Texture2D tex = new Texture2D(256, 128);
            if (RenderTexture.active == null) {
                RenderTexture.active = frameBufferRT;
            }

            for (int row = 0; row < FRAMEBUFFER_HEIGHT; row++) {
                for (int col = 0; col < FRAMEBUFFER_WIDTH / 4; col++) {
                    var fstCol = frameBufferFstBin[row] & ((ulong)1 << col);
                    tex.SetPixel(col, row, fstCol != 0 ? Color.black : Color.white);

                    var sndCol = frameBufferSndBin[row] & ((ulong)1 << col);
                    tex.SetPixel(col + FRAMEBUFFER_WIDTH / 4, row, sndCol != 0 ? Color.black : Color.white);

                    var trdCol = frameBufferTrdBin[row] & ((ulong)1 << col);
                    tex.SetPixel(col + FRAMEBUFFER_WIDTH / 4 * 2, row, trdCol != 0 ? Color.black : Color.white);

                    var fthCol = frameBufferFthBin[row] & ((ulong)1 << col);
                    tex.SetPixel(col + FRAMEBUFFER_WIDTH / 4 * 3, row, fthCol != 0 ? Color.black : Color.white);
                }
            }

            tex.Apply();
            Graphics.Blit(tex, frameBufferRT);
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

        private void TriangleHandle(out int occluderTriCount, out int occludeeTriCount) {
            cullingOccluderTriNativeList.Clear();

            occluderTriCount = cullingOccluderTriList.Count;
            for (int i = 0; i < occluderTriCount; i++) {
                cullingOccluderTriNativeList.Add(cullingOccluderTriList[i]);
            }

            dependency = new OccluderTriHandleJob() {
                triIdxArr = cullingOccluderTriNativeList.AsArray(),
                vertexArr = cullingOccluderProjVertexArr,
                screenTriArr = occluderScreenTriArr,
            }.Schedule(occluderTriCount, 10, dependency);

            occludeeTriCount = occludeeList.Length;
            dependency = new OccludeeTriHandleJob() {
                vertexArr = cullingOccludeeProjVertexArr,
                screenTriArr = occludeeScreenTriArr,
            }.Schedule(occludeeTriCount, 10, dependency);

            dependency = new ScreenTrisSortJob() {
                occluderScreenTriArr = occluderScreenTriArr,
                occluderTriCount = occluderTriCount,
                occludeeScreenTriArr = occludeeScreenTriArr,
                occludeeTriCount = occludeeTriCount,
            }.Schedule(dependency);
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

                    // TODO: Reverse-Z 后重新确认深度方向，当前深度正方向屏幕朝里
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

                    // Debug.Log($"[ViE] 三角形{index} 的 v0({v0}) 深度为 {v0.z}");
                    // Debug.Log($"[ViE] 三角形{index} 的 v1({v1}) 深度为 {v1.z}");
                    // Debug.Log($"[ViE] 三角形{index} 的 v2({v2}) 深度为 {v2.z}");
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
                    midVertexIdx = Int16.MaxValue,
                };
            }
        }

        #endregion

        #region Rasterization
        private unsafe void Rasterization(int occluderTriCount, int occludeeTriCount) {
            int size = 128 * sizeof(ulong);
            UnsafeUtility.MemClear(frameBufferFstBin.GetUnsafePtr(), size);
            UnsafeUtility.MemClear(frameBufferSndBin.GetUnsafePtr(), size);
            UnsafeUtility.MemClear(frameBufferTrdBin.GetUnsafePtr(), size);
            UnsafeUtility.MemClear(frameBufferFthBin.GetUnsafePtr(), size);

            dependency = new OccluderRasterizationJob() {
                occluderScreenTriArr = occluderScreenTriArr,
                frameBufferFstBin = frameBufferFstBin,
                frameBufferSndBin = frameBufferSndBin,
                frameBufferTrdBin = frameBufferTrdBin,
                frameBufferFthBin = frameBufferFthBin,
            }.Schedule(occluderTriCount, 100, dependency);
        }

        private struct TriangleDepthSorter : IComparer<TriangleInfo> {
            public bool orderLargest;

            public TriangleDepthSorter(bool orderLargest) {
                this.orderLargest = orderLargest;
            }

            public int Compare(TriangleInfo x, TriangleInfo y) {
                int depthComparison = x.depth.CompareTo(y.depth);
                if (orderLargest) {
                    depthComparison = -depthComparison;
                }

                if (depthComparison != 0) {
                    return depthComparison;
                }

                return x.midVertexIdx.CompareTo(y.midVertexIdx);
            }
        }

        [BurstCompile]
        private struct ScreenTrisSortJob : IJob {
            public NativeArray<TriangleInfo> occluderScreenTriArr;
            public int occluderTriCount;
            public NativeArray<TriangleInfo> occludeeScreenTriArr;
            public int occludeeTriCount;

            public unsafe void Execute() {
                NativeSortExtension.Sort((TriangleInfo*)occluderScreenTriArr.GetUnsafePtr(), occluderTriCount, new TriangleDepthSorter(false));
                NativeSortExtension.Sort((TriangleInfo*)occludeeScreenTriArr.GetUnsafePtr(), occludeeTriCount, new TriangleDepthSorter(true));
            }
        }

        [BurstCompile]
        private struct OccluderRasterizationJob : IJobParallelFor {
            public NativeArray<TriangleInfo> occluderScreenTriArr;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> frameBufferFstBin;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> frameBufferSndBin;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> frameBufferTrdBin;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> frameBufferFthBin;

            public void Execute(int index) {
                TriangleInfo tri = occluderScreenTriArr[index];
                tri.GetPosedVertex(out float4 lowestVertex, out float4 midVertex, out float4 highestVertex);

                // 另一条边与中点齐平的点
                float xMiddleOtherSide = CullingUtils.GetXOnSameHorizontal(highestVertex, lowestVertex, midVertex.y);

                float leftGradient = 0;
                float rightGradient = 0;
                float midGradient = CullingUtils.CalculateSlope(lowestVertex, midVertex);
                float highestGradient = CullingUtils.CalculateSlope(lowestVertex, highestVertex);

                if (xMiddleOtherSide > midVertex.x) {
                    leftGradient = midGradient;
                    rightGradient = highestGradient;
                } else {
                    leftGradient = highestGradient;
                    rightGradient = midGradient;
                }

                int lowestRow = (int)math.round(lowestVertex.y) + FRAMEBUFFER_HEIGHT / 2;
                int beginRowDiff = -lowestRow;
                int beginRow = math.max(lowestRow, 0);
                int middleRow = math.min((int)math.round(midVertex.y) + FRAMEBUFFER_HEIGHT / 2, FRAMEBUFFER_HEIGHT - 1);
                float xLeft = lowestVertex.x + FRAMEBUFFER_WIDTH / 2;
                float xRight = lowestVertex.x + FRAMEBUFFER_WIDTH / 2;

                if (beginRowDiff > 0) {
                    xLeft += beginRowDiff * leftGradient;
                    xRight += beginRowDiff * rightGradient;
                }

                for (int row = beginRow; row < middleRow; row++, xLeft += leftGradient, xRight += rightGradient) {
                    SetBinRowMask(row, xLeft, xRight);
                }

                float4 leftVertex = default;
                float4 rightVertex = default;
                if (xMiddleOtherSide < midVertex.x) {
                    xLeft = xMiddleOtherSide;
                    xRight = midVertex.x;
                    leftVertex = new float4(xMiddleOtherSide, midVertex.y, 0, 0);
                    rightVertex = midVertex;
                } else {
                    xLeft = midVertex.x;
                    xRight = xMiddleOtherSide;
                    leftVertex = midVertex;
                    rightVertex = new float4(xMiddleOtherSide, midVertex.y, 0, 0);
                }

                leftGradient = CullingUtils.CalculateSlope(leftVertex, highestVertex);
                rightGradient = CullingUtils.CalculateSlope(rightVertex, highestVertex);
                xLeft += FRAMEBUFFER_WIDTH / 2;
                xRight += FRAMEBUFFER_WIDTH / 2;
                beginRow = math.max((int)math.round(midVertex.y) + FRAMEBUFFER_HEIGHT / 2, 0);
                int maxRow = math.min((int)math.round(math.min(FRAMEBUFFER_HEIGHT, highestVertex.y + FRAMEBUFFER_HEIGHT / 2)), FRAMEBUFFER_HEIGHT - 1);
                for (int row = beginRow; row < maxRow; row++, xLeft += leftGradient, xRight += rightGradient) {
                    SetBinRowMask(row, xLeft, xRight);
                }
            }

            private void SetBinRowMask(int row, float xLeft, float xRight) {
                ulong fstFbMask = frameBufferFstBin[row];
                if (fstFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(0, xLeft, xRight);
                    if (rowMask != 0ul) {
                        frameBufferFstBin[row] = fstFbMask|rowMask;
                    }
                }

                ulong sndFbMask = frameBufferSndBin[row];
                if (sndFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(FRAMEBUFFER_BIN_WIDTH, xLeft, xRight);
                    if (rowMask != 0ul) {
                        frameBufferSndBin[row] = sndFbMask|rowMask;
                    }
                }

                ulong trdFbMask = frameBufferTrdBin[row];
                if (trdFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(FRAMEBUFFER_BIN_WIDTH * 2, xLeft, xRight);
                    if (rowMask != 0ul) {
                        frameBufferTrdBin[row] = trdFbMask|rowMask;
                    }
                }

                ulong fthFbMask = frameBufferFthBin[row];
                if (fthFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(FRAMEBUFFER_BIN_WIDTH * 3, xLeft, xRight);
                    if (rowMask != 0ul) {
                        frameBufferFthBin[row] = fthFbMask|rowMask;
                    }
                }
            }

            private ulong ComputeBinRowMask(int binMinX, float fx0, float fx1) {
                int x0 = (int)math.round(fx0) - binMinX;
                int x1 = (int)math.round(fx1) - binMinX;
                x0 = math.max(0, x0);
                x1 = math.min(FRAMEBUFFER_BIN_WIDTH - 1, x1);
                var bitNum = (x1 - x0) + 1;

                if (bitNum > 0) {
                    var result = (bitNum == FRAMEBUFFER_BIN_WIDTH) ? ~0ul : ((1ul << bitNum) - 1) << x0;

                    if (result != 0ul) {
                        Debug.Log($"[ViE] ?");
                    }

                    return result;
                } else {
                    return 0ul;
                }
            }
        }
        #endregion
    }
}