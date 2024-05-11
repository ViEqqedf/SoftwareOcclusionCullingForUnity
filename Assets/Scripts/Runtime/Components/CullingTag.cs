using System;
using Unity.Mathematics;
using UnityEngine;

namespace ViE.SOC.Runtime.Components {
    public struct CullingItem {
        public float3 center;
        public float boundRadius;
        public float screenSize;
        public bool hasTransparentMat;
        public int index;
        public bool isOccluder;
    }

    public struct CullingVertexInfo {
        public float3 vertex;
        public int modelMatrixIndex;
    }

    public struct TriangleInfo {
        public float4 v0;
        public float4 v1;
        public float4 v2;
        public float depth;
        public short midVertexIdx;

        public void GetPosedVertex(out float4 lowestVertex, out float4 midVertex, out float4 highestVertex) {
            lowestVertex = v0;
            midVertex = v1;
            highestVertex = v2;

            if (midVertexIdx == 1) {
                midVertex = v1;
                highestVertex = v2;
            } else if (midVertexIdx == 2) {
                midVertex = v2;
                highestVertex = v1;
            } else if(midVertexIdx == 0) {
                throw new Exception($"Wrong MidVertex {midVertexIdx}");
            }
        }
    }
}