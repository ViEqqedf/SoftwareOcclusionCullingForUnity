using Unity.Mathematics;
using UnityEngine;

namespace ViE.SOC.Runtime.Components {
    public struct CullingItem {
        public float3 center;
        public float boundRadius;
        public float screenSize;
        public bool hasTransparentMat;
        public int index;
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
    }
}