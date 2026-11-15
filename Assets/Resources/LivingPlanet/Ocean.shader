Shader "LivingPlanet/Ocean"
{
    Properties
    {
        _LP_WaterRoughness ("Water roughness", Range(0, 1)) = 0.08
        _LP_ShallowColor ("Shallow water", Color) = (0.025, 0.22, 0.31, 1)
        _LP_DeepColor ("Deep water", Color) = (0.008, 0.065, 0.16, 1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Ocean"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attr { float4 pos : POSITION; float3 normal : NORMAL; float4 col : COLOR; float4 water : TEXCOORD0; };
            struct Vary { float4 cs : SV_POSITION; float3 ws : TEXCOORD0; float4 ai : TEXCOORD1; float fog : TEXCOORD2; float3 flow : TEXCOORD3; float4 water : TEXCOORD4; };

            float _LP_WaterRoughness;
            float _LP_WaterTime;
            float4 _LP_ShallowColor;
            float4 _LP_DeepColor;

            // Bruit de valeur 3D bon marché (sans texture, juste des hash) utilisé pour déformer les vagues
            // planes ci-dessous : sans lui, dot(p, direction_fixe) dessine des cercles concentriques parfaits
            // autour de chaque direction sur une sphère, ce qui donnait les ronds de vagues bien trop visibles.
            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + float3(0.71, 0.113, 0.419));
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float noise3(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash31(i + float3(0, 0, 0));
                float n100 = hash31(i + float3(1, 0, 0));
                float n010 = hash31(i + float3(0, 1, 0));
                float n110 = hash31(i + float3(1, 1, 0));
                float n001 = hash31(i + float3(0, 0, 1));
                float n101 = hash31(i + float3(1, 0, 1));
                float n011 = hash31(i + float3(0, 1, 1));
                float n111 = hash31(i + float3(1, 1, 1));
                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);
                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);
                return lerp(nxy0, nxy1, f.z);
            }

            Vary vert(Attr i)
            {
                Vary o;
                o.ws = TransformObjectToWorld(i.pos.xyz);
                o.cs = TransformWorldToHClip(o.ws);
                o.ai = i.col; // r : agitation, g : glace, a : turbulence côtière
                o.flow = TransformObjectToWorldDir(i.normal);
                o.water = i.water;
                o.fog = ComputeFogFactor(o.cs.z);
                return o;
            }

            half4 frag(Vary i) : SV_Target
            {
                float3 p = i.ws;
                float3 n0 = normalize(p);
                float3 surface = cross(ddy(p), ddx(p));
                float surfaceLength = dot(surface, surface);
                float3 surfaceNormal = surfaceLength > 1e-10 ? surface * rsqrt(surfaceLength) : n0;
                surfaceNormal *= dot(surfaceNormal, n0) < 0.0 ? -1.0 : 1.0;
                surfaceNormal = normalize(lerp(n0, surfaceNormal, 0.08));
                float amp = i.ai.x;
                float ice = i.ai.y;
                float depth = i.water.z;
                float t = _LP_WaterTime;

                float warp = noise3(p * 0.025 + float3(0.0, 0.0, t * 0.012)) - 0.5;
                float3 pw = p + warp * float3(2.4, 1.8, 2.1);

                float3 k1 = float3(0.82, 0.41, 0.40);
                float3 k2 = float3(-0.35, 0.62, 0.70);
                float3 k3 = float3(0.30, -0.80, 0.52);
                float3 flow = i.flow - n0 * dot(i.flow, n0);
                if (dot(flow, flow) > 0.0001)
                {
                    float3 along = normalize(flow);
                    float3 across = normalize(cross(n0, along));
                    k1 = normalize(k1 + along * 0.8);
                    k2 = normalize(k2 + across * 0.5 + along * 0.35);
                    k3 = normalize(k3 - across * 0.45 + along * 0.2);
                }
                float p1 = dot(pw, k1) * 0.18 + t * 0.18;
                float p2 = dot(pw, k2) * 0.28 + t * 0.25;
                float p3 = dot(pw, k3) * 0.38 - t * 0.30;
                float3 grad = k1 * cos(p1) * 0.4 * (1.0 - smoothstep(0.6, 1.5, fwidth(p1)))
                    + k2 * cos(p2) * 0.3 * (1.0 - smoothstep(0.6, 1.5, fwidth(p2)))
                    + k3 * cos(p3) * 0.2 * (1.0 - smoothstep(0.6, 1.5, fwidth(p3)));
                grad -= n0 * dot(grad, n0);
                float3 n = normalize(surfaceNormal - grad * (0.025 + amp * 0.055));

                Light l = GetMainLight();
                float3 L = l.direction;
                float3 V = normalize(_WorldSpaceCameraPos - p);
                float day = saturate(dot(n0, L) * 2.0 + 0.3);

                float diff = saturate(dot(n, L));
                // La couleur dépend de la profondeur, indépendamment de l'opacité du maillage.
                float absorption = smoothstep(0.0, 100.0, depth);
                float3 baseC = lerp(_LP_ShallowColor.rgb, _LP_DeepColor.rgb, absorption);
                baseC = lerp(baseC, float3(0.30, 0.24, 0.15), saturate(i.water.y * 2.0) * 0.65);
                float3 col = baseC * (0.12 + 0.55 * diff) * (0.35 + 0.65 * day);

                float3 H = normalize(L + V);
                float roughness = saturate(0.25 + _LP_WaterRoughness + amp * 0.08);
                float specPower = lerp(96.0, 24.0, roughness);
                float fres = 0.02 + 0.98 * pow(1.0 - saturate(dot(n, V)), 5.0);
                float spec = pow(saturate(dot(n, H)), specPower) * (2.0 - roughness) * day * (0.12 + 0.88 * fres);
                float3 sky = GlossyEnvironmentReflection(reflect(-V, n), p, roughness, 1.0) * (0.1 + 0.9 * day);
                sky = max(sky, float3(0.18, 0.36, 0.60) * (0.1 + 0.9 * day));
                col = lerp(col, sky, fres) + spec * l.color * 0.35;

                float flowLength = length(flow);
                float3 alongFlow = flowLength > 0.001 ? flow / flowLength : float3(0, 0, 0);
                float streak = noise3(p * 0.035 - alongFlow * t * (0.08 + i.water.x * 0.28));
                col += float3(0.018, 0.036, 0.045) * (streak - 0.5) * i.water.x * day;
                float crest = smoothstep(0.25, 4.0, i.water.w);
                float trough = smoothstep(0.25, 4.0, -i.water.w);
                col *= 1.0 - 0.12 * trough;
                col += float3(0.015, 0.07, 0.09) * crest * (0.65 + 0.35 * streak) * day;
                col = lerp(col, float3(0.32, 0.52, 0.55) * (0.25 + 0.75 * day), i.ai.a * 0.25);
                col = lerp(col, float3(0.85, 0.9, 0.95) * (0.2 + 0.8 * day), ice);
                col = MixFog(col, i.fog);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
