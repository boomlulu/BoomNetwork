// BoomNetwork MinecraftDemo — Block Atlas Shader
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
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float3 worldNormal : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _AtlasSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv; // tiled UVs from mesh builder
                o.color = v.color;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
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
                    1.0 - (atlasRow + 1.0 - tiledUV.y) * tileH  // flip Y for top-left origin
                );

                fixed4 col = tex2D(_MainTex, atlasUV);

                // Simple directional lighting
                float3 lightDir = normalize(float3(0.3, 1.0, 0.2));
                float ndl = saturate(dot(i.worldNormal, lightDir));
                float lighting = 0.55 + 0.45 * ndl; // ambient + directional

                col.rgb *= lighting;

                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
