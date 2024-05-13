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
        private NativeList<TriangleInfo> occluderScreenTriList;
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
        public NativeArray<bool> occludeeVisibilityResultArr;

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

            occluderScreenTriList = new NativeList<TriangleInfo>(DEFAULT_CONTAINER_SIZE / 3, Allocator.Persistent);
            occludeeScreenTriArr = new NativeArray<TriangleInfo>(DEFAULT_CONTAINER_SIZE / 3, Allocator.Persistent);

            frameBufferFstBin = new NativeArray<ulong>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            frameBufferSndBin = new NativeArray<ulong>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            frameBufferTrdBin = new NativeArray<ulong>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            frameBufferFthBin = new NativeArray<ulong>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            frameBufferFstTriBin = new NativeArray<TriangleInfo>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            frameBufferSndTriBin = new NativeArray<TriangleInfo>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            frameBufferTrdTriBin = new NativeArray<TriangleInfo>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            frameBufferFthTriBin = new NativeArray<TriangleInfo>(FRAMEBUFFER_HEIGHT, Allocator.Persistent);
            occludeeVisibilityResultArr = new NativeArray<bool>(DEFAULT_CONTAINER_SIZE / 3, Allocator.Persistent);
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

            occluderScreenTriList.Dispose();
            occludeeScreenTriArr.Dispose();

            frameBufferFstBin.Dispose();
            frameBufferSndBin.Dispose();
            frameBufferTrdBin.Dispose();
            frameBufferFthBin.Dispose();
            frameBufferFstTriBin.Dispose();
            frameBufferSndTriBin.Dispose();
            frameBufferTrdTriBin.Dispose();
            frameBufferFthTriBin.Dispose();
            occludeeVisibilityResultArr.Dispose();

            JobsUtility.ResetJobWorkerCount();
        }

        private void Update() {
            dependency.Complete();
            Profiler.BeginSample("[ViE] DrawRT");
            TestingDrawRT();
            Profiler.EndSample();

            Profiler.BeginSample("[ViE] CullingApply");
            CullingApply();
            Profiler.EndSample();

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
            Texture2D tex = new Texture2D(FRAMEBUFFER_WIDTH, FRAMEBUFFER_HEIGHT);
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

        private void CullingApply() {
            int occludeeCount = occludeeList.Length;
            for (int i = 0; i < occludeeCount; i++) {
                CullingItem item = occludeeList[i];
                if (mfList[item.index] != null) {
                    mfList[item.index].transform.GetComponent<MeshRenderer>().enabled = occludeeVisibilityResultArr[i];
                }
            }
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
                        isOccluder = mr.transform.tag.Contains("ViEOccluder"),
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
                    isOccluder = item.isOccluder,
                };
                item = cullingItemList[i];

                if (item.isOccluder && !item.hasTransparentMat && itemScreenSize > MIN_SCREEN_RADIUS_FOR_OCCLUDER) {
                    occluderList.Add(item);
                } else {
                    occludeeList.Add(item);
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
            int vertexStartIdx = 0;

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
                    cullingOccluderTriList.Add(new int4(tempTriList[j], tempTriList[j + 1], tempTriList[j + 2], vertexStartIdx));
                }

                Profiler.EndSample();

                Profiler.BeginSample("[ViE] OccluderTransfer-InnerLoop");
                int vertexCount = cullingOccluderVertexList.Count;
                for (int j = 0; j < vertexCount; j++) {
                    Vector3 vertex = cullingOccluderVertexList[j];
                    cullingOccluderVertexTempArr[occluderVertexArrLength++] = new CullingVertexInfo() {
                        vertex = vertex,
                        modelMatrixIndex = mMatrixIdx,
                    };
                }

                vertexStartIdx += vertexCount;
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
            occluderScreenTriList.Clear();

            occluderTriCount = cullingOccluderTriList.Count;
            for (int i = 0; i < occluderTriCount; i++) {
                cullingOccluderTriNativeList.Add(cullingOccluderTriList[i]);
            }

            dependency = new OccluderTriHandleJob() {
                triIdxArr = cullingOccluderTriNativeList.AsArray(),
                vertexArr = cullingOccluderProjVertexArr,
                screenTriList = occluderScreenTriList.AsParallelWriter(),
            }.Schedule(occluderTriCount, 10, dependency);

            occludeeTriCount = occludeeList.Length;
            dependency = new OccludeeTriHandleJob() {
                vertexArr = cullingOccludeeProjVertexArr,
                screenTriArr = occludeeScreenTriArr,
            }.Schedule(occludeeTriCount, 10, dependency);

            dependency = new ScreenTrisSortJob() {
                occluderScreenTriList = occluderScreenTriList,
            }.Schedule(dependency);
        }

        [BurstCompile]
        private struct OccluderTriHandleJob : IJobParallelFor {
            public NativeArray<int4> triIdxArr;
            [ReadOnly]
            public NativeArray<float4> vertexArr;
            [NativeDisableParallelForRestriction]
            public NativeList<TriangleInfo>.ParallelWriter screenTriList;

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

                    screenTriList.AddNoResize(new TriangleInfo() {
                        v0 = v0,
                        v1 = v1,
                        v2 = v2,
                        depth = farthestDepth,
                        midVertexIdx = midVertexIdx,
                    });

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

        [BurstCompile]
        private struct ScreenTrisSortJob : IJob {
            public NativeList<TriangleInfo> occluderScreenTriList;

            public unsafe void Execute() {
                NativeSortExtension.Sort((TriangleInfo*)occluderScreenTriList.GetUnsafePtr(), occluderScreenTriList.Length, new TriangleDepthSorter(false));
            }
        }

        #endregion

        #region Rasterization
        private unsafe void Rasterization(int occluderTriCount, int occludeeTriCount) {
            UnsafeUtility.MemClear(occludeeVisibilityResultArr.GetUnsafePtr(), DEFAULT_CONTAINER_SIZE / 3 * sizeof(bool));

            int size = FRAMEBUFFER_HEIGHT * sizeof(ulong);
            UnsafeUtility.MemClear(frameBufferFstBin.GetUnsafePtr(), size);
            UnsafeUtility.MemClear(frameBufferSndBin.GetUnsafePtr(), size);
            UnsafeUtility.MemClear(frameBufferTrdBin.GetUnsafePtr(), size);
            UnsafeUtility.MemClear(frameBufferFthBin.GetUnsafePtr(), size);

            dependency = new OccluderRasterizationJob() {
                screenTriCount = -1,
                occluderScreenTriList = occluderScreenTriList,
                fbBin0 = frameBufferFstBin,
                fbBin1 = frameBufferSndBin,
                fbBin2 = frameBufferTrdBin,
                fbBin3 = frameBufferFthBin,
            }.Schedule(occluderTriCount, 100, dependency);

            dependency = new OccludeeDepthTestJob() {
                occludeeScreenTriArr = occludeeScreenTriArr,
                fbBin0 = frameBufferFstBin,
                fbBin1 = frameBufferSndBin,
                fbBin2 = frameBufferTrdBin,
                fbBin3 = frameBufferFthBin,
                occludeeVisibilityResultArr = occludeeVisibilityResultArr,
            }.Schedule(occludeeTriCount, 100, dependency);
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
        private struct OccluderRasterizationJob : IJobParallelFor {
            public int screenTriCount;

            [ReadOnly]
            public NativeList<TriangleInfo> occluderScreenTriList;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin0;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin1;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin2;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin3;

            public void Execute(int index) {
                if (screenTriCount == -1) {
                    screenTriCount = occluderScreenTriList.Length;
                }

                if (index >= screenTriCount) {
                    return;
                }

                TriangleInfo tri = occluderScreenTriList[index];
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
                int middleRow = math.min((int)math.round(midVertex.y) + FRAMEBUFFER_HEIGHT / 2, FRAMEBUFFER_HEIGHT);
                float xLeft = lowestVertex.x + FRAMEBUFFER_WIDTH / 2;
                float xRight = lowestVertex.x + FRAMEBUFFER_WIDTH / 2;

                if (beginRowDiff > 0) {
                    xLeft += beginRowDiff * leftGradient;
                    xRight += beginRowDiff * rightGradient;
                }

                for (int row = beginRow; row < middleRow; row++, xLeft += leftGradient, xRight += rightGradient) {
                    SetBinRowMask(row, (int)math.floor(xLeft), (int)math.ceil(xRight));
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
                beginRowDiff = -beginRow;
                if (beginRowDiff > 0) {
                    xLeft += beginRowDiff * leftGradient;
                    xRight += beginRowDiff * rightGradient;
                }

                int maxRow = math.min((int)math.round(math.min(FRAMEBUFFER_HEIGHT, highestVertex.y + FRAMEBUFFER_HEIGHT / 2)), FRAMEBUFFER_HEIGHT);
                for (int row = beginRow; row < maxRow; row++, xLeft += leftGradient, xRight += rightGradient) {
                    SetBinRowMask(row, (int)math.floor(xLeft), (int)math.ceil(xRight));
                }
            }

            private void SetBinRowMask(int row, int xLeft, int xRight) {
                ulong fstFbMask = fbBin0[row];
                if (fstFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(0, xLeft, xRight);
                    if (rowMask != 0ul) {
                        fbBin0[row] = fstFbMask|rowMask;
                    }
                }

                ulong sndFbMask = fbBin1[row];
                if (sndFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(FRAMEBUFFER_BIN_WIDTH, xLeft, xRight);
                    if (rowMask != 0ul) {
                        fbBin1[row] = sndFbMask|rowMask;
                    }
                }

                ulong trdFbMask = fbBin2[row];
                if (trdFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(FRAMEBUFFER_BIN_WIDTH * 2, xLeft, xRight);
                    if (rowMask != 0ul) {
                        fbBin2[row] = trdFbMask|rowMask;
                    }
                }

                ulong fthFbMask = fbBin3[row];
                if (fthFbMask != ~0ul) {
                    ulong rowMask = ComputeBinRowMask(FRAMEBUFFER_BIN_WIDTH * 3, xLeft, xRight);
                    if (rowMask != 0ul) {
                        fbBin3[row] = fthFbMask|rowMask;
                    }
                }
            }

            private ulong ComputeBinRowMask(int binMinX, int fx0, int fx1) {
                int x0 = fx0 - binMinX;
                int x1 = fx1 - binMinX;
                x0 = math.max(0, x0);
                x1 = math.min(FRAMEBUFFER_BIN_WIDTH - 1, x1);
                var bitNum = (x1 - x0) + 1;

                if (bitNum > 0) {
                    var result = (bitNum == FRAMEBUFFER_BIN_WIDTH) ? ~0ul : ((1ul << bitNum) - 1) << x0;
                    return result;
                } else {
                    return 0ul;
                }
            }
        }

        private struct OccludeeDepthTestJob : IJobParallelFor {
            public NativeArray<TriangleInfo> occludeeScreenTriArr;
            public NativeArray<bool> occludeeVisibilityResultArr;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin0;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin1;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin2;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ulong> fbBin3;

            public void Execute(int index) {
                TriangleInfo tri = occludeeScreenTriArr[index];
                bool result = CheckVisibility(fbBin0, 0, tri) ||
                              CheckVisibility(fbBin1, FRAMEBUFFER_BIN_WIDTH, tri) ||
                              CheckVisibility(fbBin2, FRAMEBUFFER_BIN_WIDTH * 2, tri) ||
                              CheckVisibility(fbBin3, FRAMEBUFFER_BIN_WIDTH * 3, tri);
                occludeeVisibilityResultArr[index] |= result;
            }

            private bool CheckVisibility(NativeArray<ulong> frameBufBin, int binMinX, TriangleInfo triangle) {
                triangle.GetPosedVertex(out float4 lowestVertex, out float4 midVertex, out float4 highestVertex);
                lowestVertex += new float4(FRAMEBUFFER_WIDTH / 2, FRAMEBUFFER_HEIGHT / 2, 0, 0);
                midVertex += new float4(FRAMEBUFFER_WIDTH / 2, FRAMEBUFFER_HEIGHT / 2, 0, 0);
                highestVertex += new float4(FRAMEBUFFER_WIDTH / 2, FRAMEBUFFER_HEIGHT / 2, 0, 0);

                int yMin = (int)math.round(math.max(0, lowestVertex.y));
                int yMax = (int)math.round(math.min(FRAMEBUFFER_HEIGHT, highestVertex.y));

                int x0 = (int)math.round(math.max(lowestVertex.x - binMinX, 0));
                int x1 = (int)math.round(math.min(midVertex.x - binMinX, FRAMEBUFFER_BIN_WIDTH - 1));
                if (x0 > x1) {
                    return false;
                }

                int bitNum = (x1 - x0) + 1;
                ulong rowMask = (bitNum == FRAMEBUFFER_BIN_WIDTH) ? ~0ul : ((1ul << bitNum) - 1) << x0;
                for (int row = yMin; row <= yMax; ++row) {
                    ulong fbMask = frameBufBin[row];
                    if ((~fbMask & rowMask) > 0)
                        return true;
                }

                return false;
            }
        }
        #endregion
    }
}