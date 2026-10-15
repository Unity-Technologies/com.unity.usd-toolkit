// Screen-constant inverted-hull outline for the current selection.
//
// The hull is drawn from a copy of the mesh whose normals have been welded per position (see
// SelectionOutline.cs). Imported USD meshes are usually hard edged - a box arrives with split
// vertices and per-face normals - and extruding those raw would tear the outline open at every
// corner. Welding first is what makes this work on arbitrary CAD geometry.
Shader "USDRuntimeDemo/SelectionOutline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0.15, 0.85, 1.0, 1.0)
        _OutlineWidth ("Outline Width (pixels)", Float) = 4.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+100"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SelectionOutline"

            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            // Reject anything SelectionMask already marked, so only the outer rim survives.
            Stencil
            {
                Ref 2
                Comp NotEqual
            }

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float _OutlineWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;

                float4 positionCS = TransformObjectToHClip(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                // Push the silhouette out in clip space so the band stays the same number of
                // pixels wide however far away the part is. Multiplying by w undoes the
                // perspective divide that follows.
                float3 normalCS = mul((float3x3)UNITY_MATRIX_VP, normalWS);
                float2 offset = normalize(normalCS.xy + 1e-6);
                offset *= _OutlineWidth * 2.0 * positionCS.w / _ScreenParams.xy;

                positionCS.xy += offset;
                output.positionCS = positionCS;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                return half4(_OutlineColor.rgb, _OutlineColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
