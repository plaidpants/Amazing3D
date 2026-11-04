Shader "Custom/NonEuclideanPortalMask"
{
    Properties
    {
        _StencilRef("Stencil Ref ID", Int) = 1
    }
    SubShader
    {
        // Render AFTER normal opaque walls (Geometry = 2000) so normal maze walls & arch pillars
        // write their Z-depth first! Then the portal only stamps visible doorway openings (ZTest LEqual),
        // and Cull Back ensures you NEVER stamp a portal when looking through the back of another doorway!
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+5" }
        ColorMask 0
        ZWrite Off
        ZTest LEqual
        Cull Back

        Stencil
        {
            Ref[_StencilRef]
            Comp Always
            Pass Replace
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}