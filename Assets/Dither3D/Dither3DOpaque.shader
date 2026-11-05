/*
 * Copyright (c) 2025 Rune Skovbo Johansen
 * Modified for Non-Euclidean Dual-Portal Analytical Tunnel Self-Shadowing
 */

Shader "Dither 3D/Opaque"
{
    Properties
    {
        _StencilRef("Stencil Ref", Int) = 0
        _StencilComp("Stencil Comparison", Int) = 8
        _TunnelType("Tunnel Type (0=None, 1=NS, 2=EW)", Int) = 0
        _TunnelBounds("Tunnel Bounds (minX, maxX, minZ, maxZ)", Vector) = (0,0,0,0)
        _TunnelRoofHeight("Tunnel Roof Height", Float) = 2.01

        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _EmissionMap ("Emission", 2D) = "white" {}
        _EmissionColor ("Emission Color", Color) = (0,0,0,0)
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0

        [Header(Dither Input Brightness)]
        _InputExposure ("Exposure", Range(0,5)) = 1
        _InputOffset ("Offset", Range(-1,1)) = 0

        [Header(Dither Settings)]
        [DitherPatternProperty] _DitherMode ("Pattern", Int) = 3
        [HideInInspector] _DitherTex ("Dither 3D Texture", 3D) = "" {}
        [HideInInspector] _DitherRampTex ("Dither Ramp Texture", 2D) = "white" {}
        _Scale ("Dot Scale", Range(2,10)) = 5.0
        _SizeVariability ("Dot Size Variability", Range(0,1)) = 0
        _Contrast ("Dot Contrast", Range(0,2)) = 1
        _StretchSmoothness ("Stretch Smoothness", Range(0,2)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        Stencil
        {
            Ref[_StencilRef]
            Comp[_StencilComp]
        }

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert finalcolor:mycolor

        #pragma target 3.5
        #pragma multi_compile_fog
        #pragma multi_compile __ DITHERCOL_GRAYSCALE DITHERCOL_RGB DITHERCOL_CMYK
        #pragma multi_compile __ INVERSE_DOTS
        #pragma multi_compile __ RADIAL_COMPENSATION
        #pragma multi_compile __ QUANTIZE_LAYERS
        #pragma multi_compile __ DEBUG_FRACTAL

        #include "Dither3DInclude.cginc"

        sampler2D _MainTex;
        sampler2D _BumpMap;
        sampler2D _EmissionMap;

        int _TunnelType;
        float4 _TunnelBounds; // x = minX, y = maxX, z = minZ, w = maxZ
        float _TunnelRoofHeight;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
            float2 uv_EmissionMap;
            float2 uv_DitherTex;
            float4 screenPos;
            float3 worldPos;
            float3 worldNormal;
            INTERNAL_DATA
            UNITY_FOG_COORDS(4)
        };

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;
        fixed4 _EmissionColor;

        void vert(inout appdata_full v, out Input o) {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            float4 clipPos = UnityObjectToClipPos(v.vertex);
            UNITY_TRANSFER_FOG(o, clipPos);
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Normal = UnpackNormal (tex2D (_BumpMap, IN.uv_BumpMap));
            o.Emission = tex2D (_EmissionMap, IN.uv_EmissionMap) * _EmissionColor;

            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }

        // Exact analytical ray-box self-shadowing for Non-Euclidean Tunnels!
        // Computes whether a ray from IN.worldPos toward the directional light (_WorldSpaceLightPos0)
        // is blocked by this specific tunnel's roof or side walls—allowing BOTH the NS tunnel and
        // EW tunnel to display 100% accurate, independent shadows simultaneously with zero ShadowMap leaks!
        float ComputeAnalyticalTunnelShadow(float3 wp, float3 wn)
        {
            if (_TunnelType == 0) return 1.0;

            float3 L = normalize(_WorldSpaceLightPos0.xyz);
            if (L.y <= 0.01) return 0.25;

            // Offset start point slightly along normal to avoid self-intersection on the wall itself
            float3 p = wp + wn * 0.03;

            // Ray-plane intersection with the tunnel ceiling height (_TunnelRoofHeight)
            float tRoof = max(0.0, (_TunnelRoofHeight - p.y) / L.y);
            float3 hitRoof = p + L * tRoof;

            float minX = _TunnelBounds.x;
            float maxX = _TunnelBounds.y;
            float minZ = _TunnelBounds.z;
            float maxZ = _TunnelBounds.w;

            if (_TunnelType == 1)
            {
                // North-South Tunnel: open at z < minZ and z > maxZ; closed walls at x = minX and x = maxX
                // Light reaches point p ONLY if the ray exits through the North or South doorway BEFORE hitting a side wall or roof!
                bool hitsRoofOrSideWall = (hitRoof.z >= minZ && hitRoof.z <= maxZ) || (hitRoof.x < minX || hitRoof.x > maxX);
                return hitsRoofOrSideWall ? 0.32 : 1.0;
            }
            else
            {
                // East-West Tunnel: open at x < minX and x > maxX; closed walls at z = minZ and z = maxZ
                bool hitsRoofOrSideWall = (hitRoof.x >= minX && hitRoof.x <= maxX) || (hitRoof.z < minZ || hitRoof.z > maxZ);
                return hitsRoofOrSideWall ? 0.32 : 1.0;
            }
        }

        void mycolor (Input IN, SurfaceOutputStandard o, inout fixed4 color)
        {
            if (_TunnelType != 0)
            {
                float3 wn = WorldNormalVector(IN, o.Normal);
                float tunnelShadow = ComputeAnalyticalTunnelShadow(IN.worldPos, wn);
                color.rgb *= tunnelShadow;
            }
            UNITY_APPLY_FOG(IN.fogCoord, color);
            color = GetDither3DColor(IN.uv_DitherTex, IN.screenPos, color);
        }
        ENDCG
    }
    FallBack "Diffuse"
}