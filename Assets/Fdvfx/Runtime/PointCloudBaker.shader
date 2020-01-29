Shader "Hidden/Fdvfx/PointCloudBaker"
{
    CGINCLUDE

    #include "UnityCG.cginc"

    Buffer<float> _VertexArray;
    Buffer<float> _NormalArray;
    Buffer<float> _UVArray;

    float2 _TextureSize;
    uint _VertexCount;

    void Vertex(
        float4 vertex : POSITION,
        float2 uv : TEXCOORD0,
        out float4 outPosition : SV_Position,
        out float2 outUV : TEXCOORD0
    )
    {
        outPosition = UnityObjectToClipPos(vertex);
        outUV = uv;
    }

    void Fragment(
        float4 position : SV_Position,
        float2 uv : TEXCOORD0,
        out float4 outVertex : SV_Target0,
        out float4 outNormal : SV_Target1,
        out float4 outUV : SV_Target2
    )
    {
        uint2 uvi = uv * _TextureSize;
        uint index = uvi.x + uvi.y * _TextureSize.x;

        float px = _VertexArray[index * 3 + 0];
        float py = _VertexArray[index * 3 + 1];
        float pz = _VertexArray[index * 3 + 2];

        float nx = _NormalArray[index * 3 + 0];
        float ny = _NormalArray[index * 3 + 1];
        float nz = _NormalArray[index * 3 + 2];

        float u = _UVArray[index * 2 + 0];
        float v = _UVArray[index * 2 + 1];

        float alpha = index < _VertexCount;

        outVertex = float4(px, py, pz, alpha);
        outNormal = float4(nx, ny, nz, alpha);
        outUV = float4(u, v, 0, alpha);
    }

    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDCG
        }
    }
}
