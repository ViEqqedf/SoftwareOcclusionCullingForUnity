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

    public struct CullingOccluder {
        public float screenSize;
        public MeshRenderer meshRenderer;
    }
}