// Stencil mask for the selection outline.
//
// Drawn before the outline (lower render queue) from the unexpanded selected meshes, writing
// only to the stencil buffer. The outline pass then rejects anything already marked, so the rim
// appears around the SILHOUETTE OF THE WHOLE SELECTION rather than around every sub-mesh.
// Without this, a part made of several meshes gets an internal outline at each mesh boundary and
// small features disappear under their own rim.
Shader "USDRuntimeDemo/SelectionMask"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+90"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SelectionMask"

            Cull Back
            ZWrite Off
            ZTest LEqual
            ColorMask 0

            Stencil
            {
                Ref 2
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
