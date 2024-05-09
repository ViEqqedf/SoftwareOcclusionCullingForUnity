using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViE.SOC.Runtime.Utils {
    public struct FrustumPlane {
        public float3 normal;
        public float disToOrigin;
    }

    public static class CullingUtils {
        public static bool FrustumCulling(NativeArray<FrustumPlane> planes, float3 center, float radius = 1f) {
            for (int i = 0; i < 6; i++) {
                var frustumPlane = planes[i];
                float dis = math.dot(frustumPlane.normal, center) + frustumPlane.disToOrigin;
                if (dis < -radius) {
                    return false;
                }
            }

            return true;
        }

        public static FrustumPlane GetPlane(float3 fstFarPlanePoint, float3 sndFarPlanePoint, float3 cameraPos) {
            float3 normal = math.cross(sndFarPlanePoint - fstFarPlanePoint, cameraPos - fstFarPlanePoint);
            normal = math.normalizesafe(normal);
            float dis = -math.dot(normal, fstFarPlanePoint);
            FrustumPlane frustumPlane = new FrustumPlane {
                normal = normal,
                disToOrigin = dis,
            };

            return frustumPlane;
        }

        public static float ComputeBoundsScreenSize(Vector3 viewPos, Vector3 modelPos, float sphereRadius, Matrix4x4 projMatrix) {
            float distance = Vector3.Distance(viewPos, modelPos);
            float screenScale = 0.5f * Mathf.Max(projMatrix.m00, projMatrix.m11);
            float screenRadius = screenScale * sphereRadius / Mathf.Max(1.0f, distance);
            return screenRadius * 2.0f;
        }

        public static bool IsMaterialTransparent(Material mat) {
            if (mat == null) {
                return false;
            }

            return mat.renderQueue == (int)RenderQueue.Transparent;
        }

        public static bool TriangleCulling(float4 fst, float4 snd, float4 trd) {
            var fstAbsW = math.abs(fst.w);
            var sndAbsW = math.abs(snd.w);
            var trdAbsW = math.abs(trd.w);

            for (int i = 0; i < 6; i++) {
                // left
                if (fst.x < -fstAbsW && snd.x < -sndAbsW && trd.x < -trdAbsW) {
                    return true;
                }

                // right
                if (fst.x > fstAbsW && snd.x > sndAbsW && trd.x > trdAbsW) {
                    return true;
                }

                // bottom
                if (fst.y < -fstAbsW && snd.y < -sndAbsW && trd.y < -trdAbsW) {
                    return true;
                }

                // up
                if (fst.y > fstAbsW && snd.y > sndAbsW && trd.y > trdAbsW) {
                    return true;
                }

                // near
                if (fst.z < -fstAbsW && snd.z < -sndAbsW && trd.z < -trdAbsW) {
                    return true;
                }

                // far
                if (fst.z > fstAbsW && snd.z > sndAbsW && trd.z > trdAbsW) {
                    return true;
                }
            }

            return false;
        }
    }
}