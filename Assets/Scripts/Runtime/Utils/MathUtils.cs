using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ViE.SOC.Runtime.Utils {
    public struct FrustumPlane {
        public float3 normal;
        public float disToOrigin;
    }

    public static class MathUtils {
        public static bool FrustumCulling(NativeArray<FrustumPlane> planes, float3 center, float radius = 1f) {
            for (int i = 0; i < 6; i++) {
                var frustumPlane = planes[i];
                float dis = math.dot(frustumPlane.normal, center) + frustumPlane.disToOrigin;
                if (dis < -radius) {
                    return true;
                }
            }

            return false;
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
    }
}