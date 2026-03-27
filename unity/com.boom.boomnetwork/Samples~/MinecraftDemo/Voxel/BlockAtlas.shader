// BoomNetwork MinecraftDemo — Block Atlas Shader (URP)
// Reads atlas tile coordinates from vertex color (R=col, G=row)
// UVs are tiled across greedy-merged quads using frac()

Shader "BoomNetwork/BlockAtlas"
{
    Properties
    {
        _MainTex ("Block Atlas", 2D) = "white" {}
        _AtlasSize ("Atlas Size (cols, rows)", Vector) = (8, 8, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float4 color       : COLOR;
                float3 worldNormal : TEXCOORD1;
                float  fogFactor   : TEXCOORD2;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _AtlasSize;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs posInputs = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS  = posInputs.positionCS;
                o.uv          = v.uv; // tiled UVs from mesh builder
                o.color       = v.color;
                o.worldNormal = TransformObjectToWorldNormal(v.normalOS);
                o.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float cols = _AtlasSize.x;
                float rows = _AtlasSize.y;

                // Decode atlas tile position from vertex color
                float atlasCol = round(i.color.r * 255.0);
                float atlasRow = round(i.color.g * 255.0);

                // Tile size in UV space
                float tileW = 1.0 / cols;
                float tileH = 1.0 / rows;

                // frac() tiles the texture across greedy-merged quads
                float2 tiledUV = frac(i.uv);

                // Offset into atlas
                float2 atlasUV = float2(
                    (atlasCol + tiledUV.x) * tileW,
                    1.0 - (atlasRow + 1.0 - tiledUV.y) * tileH
                );

                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, atlasUV);

                // Simple directional lighting (matches URP main light)
                Light mainLight = GetMainLight();
                float ndl = saturate(dot(normalize(i.worldNormal), mainLight.direction));
                float lighting = 0.55 + 0.45 * ndl;

                col.rgb *= lighting * mainLight.color;

                // Apply fog
                col.rgb = MixFog(col.rgb, i.fogFactor);

                return col;
            }
            ENDHLSL
        }

        // Shadow caster pass for receiving shadows
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            float3 _LightDirection;

            Varyings ShadowVert(Attributes v)
            {
                Varyings o;
                float3 worldPos = TransformObjectToWorld(v.positionOS.xyz);
                float3 worldNormal = TransformObjectToWorldNormal(v.normalOS);
                float4 clipPos = TransformWorldToHClip(ApplyShadowBias(worldPos, worldNormal, _LightDirection));

                #if UNITY_REVERSED_Z
                    clipPos.z = min(clipPos.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    clipPos.z = max(clipPos.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                o.positionCS = clipPos;
                return o;
            }

            half4 ShadowFrag(Varyings i) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // Depth only pass (for depth prepass)
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 DepthFrag(Varyings i) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    // Fallback for Built-in RP (in case someone uses it without URP)
    FallBack "Universal Render Pipeline/Lit"
}
