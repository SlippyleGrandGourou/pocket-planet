Shader "LivingPlanet/Ocean"
{
    Properties
    {
        _LP_WaterRoughness ("Water roughness", Range(0, 1)) = 0.08
        _LP_ShoreColor ("Shore water", Color) = (0.10, 0.44, 0.47, 1)
        _LP_ShallowColor ("Shallow water", Color) = (0.03, 0.24, 0.36, 1)
        _LP_DeepColor ("Deep water", Color) = (0.008, 0.065, 0.17, 1)
        [HideInInspector] _LP_WaterTime ("Water time", Float) = 0
    }

    SubShader
    {
        // L'eau est rendue après le terrain et mélangée à lui : le fond transparaît près des rives. Elle écrit
        // quand même sa profondeur, pour que l'atmosphère, la pluie et les rubans de rivière se placent bien.
        Tags { "RenderType"="Transparent" "Queue"="Transparent-100" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Ocean"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // water : x vitesse du courant, y charge en sédiments, z profondeur locale (négative sous la berge),
            //         w hauteur de l'onde de tsunami.
            // slope : pente de la surface de l'eau (vecteur tangent, en repère objet).
            struct Attr { float4 pos : POSITION; float3 normal : NORMAL; float4 col : COLOR; float4 water : TEXCOORD0; float3 slope : TEXCOORD1; };
            struct Vary
            {
                float4 cs : SV_POSITION; float3 ws : TEXCOORD0; float4 ai : TEXCOORD1; float fog : TEXCOORD2;
                float3 flow : TEXCOORD3; float4 water : TEXCOORD4; float3 slope : TEXCOORD5;
            };

            CBUFFER_START(UnityPerMaterial)
            float _LP_WaterRoughness;
            float _LP_WaterTime;
            float4 _LP_ShoreColor;
            float4 _LP_ShallowColor;
            float4 _LP_DeepColor;
            CBUFFER_END

            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + float3(0.71, 0.113, 0.419));
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            // Bruit de valeur 3D (sans texture), à interpolation quintique pour que sa dérivée reste lisse :
            // c'est elle qui sert de relief aux vaguelettes.
            float noise3(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
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

            // Pente d'un champ de vaguelettes de longueur d'onde 1/freq, qui dérive à la vitesse "drift".
            // Elle s'efface quand une vaguelette devient plus petite que deux pixels : sinon elle scintille et
            // dessine des moirés dès qu'on prend de l'altitude.
            float3 ripple(float3 p, float freq, float3 drift, float footprint)
            {
                float3 q = p * freq + drift;
                float e = 0.35;
                float c = noise3(q);
                float3 g = float3(noise3(q + float3(e, 0, 0)) - c, noise3(q + float3(0, e, 0)) - c, noise3(q + float3(0, 0, e)) - c) / e;
                return g * (1.0 - smoothstep(0.06, 0.18, footprint * freq));
            }

            Vary vert(Attr i)
            {
                Vary o;
                o.ws = TransformObjectToWorld(i.pos.xyz);
                o.cs = TransformWorldToHClip(o.ws);
                o.ai = i.col; // r : agitation, g : glace, b : ruban de rivière, a : turbulence (déferlement d'un tsunami)
                o.flow = TransformObjectToWorldDir(i.normal);
                o.slope = TransformObjectToWorldDir(i.slope, false);
                o.water = i.water;
                o.fog = ComputeFogFactor(o.cs.z);
                return o;
            }

            half4 frag(Vary i) : SV_Target
            {
                float3 p = i.ws;
                float3 n0 = normalize(p);
                float amp = i.ai.x;
                float ice = i.ai.y;
                float depth = i.water.z;
                float t = _LP_WaterTime;
                float footprint = length(fwidth(p));

                // Surface : la sphère, inclinée par la pente réelle de l'eau (houle de fond, onde de tsunami),
                // puis froissée par deux échelles de vaguelettes qui dérivent avec le courant.
                float3 flow = i.flow - n0 * dot(i.flow, n0);
                float flowLength = length(flow);
                float3 alongFlow = flowLength > 0.001 ? flow / flowLength : float3(0, 0, 0);
                float3 drift = alongFlow * t * (0.25 + i.water.x * 0.9);
                float3 grad = ripple(p, 0.33, float3(0.0, t * 0.11, 0.0) - drift * 0.33, footprint) * 0.9
                    + ripple(p, 1.15, float3(t * 0.23, 0.0, t * 0.17) - drift * 1.15, footprint) * 0.6;
                grad -= n0 * dot(grad, n0);
                float3 slope = i.slope - n0 * dot(i.slope, n0);
                float3 n = normalize(n0 - slope * 1.6 - grad * (0.035 + amp * 0.085));

                Light l = GetMainLight();
                float3 L = l.direction;
                float3 V = normalize(_WorldSpaceCameraPos - p);
                float day = saturate(dot(n0, L) * 2.0 + 0.3);
                float diff = saturate(dot(n, L));

                // Couleur selon la profondeur : turquoise sur les hauts-fonds, bleu, puis bleu nuit au large.
                float3 baseC = lerp(_LP_ShoreColor.rgb, _LP_ShallowColor.rgb, smoothstep(0.0, 9.0, depth));
                baseC = lerp(baseC, _LP_DeepColor.rgb, smoothstep(6.0, 70.0, depth));
                baseC = lerp(baseC, float3(0.33, 0.27, 0.16), saturate(i.water.y * 2.0) * 0.4);
                float3 col = baseC * (0.22 + 0.62 * diff) * (0.35 + 0.65 * day);

                float3 H = normalize(L + V);
                float roughness = saturate(0.22 + _LP_WaterRoughness + amp * 0.1);
                float specPower = lerp(140.0, 28.0, roughness);
                float fres = 0.02 + 0.98 * pow(1.0 - saturate(dot(n, V)), 5.0);
                // Vu de loin, un pixel couvre des milliers de vaguelettes : le reflet du soleil s'étale et pâlit.
                float distant = smoothstep(0.08, 1.2, footprint);
                float spec = pow(saturate(dot(n, H)), lerp(specPower, 36.0, distant)) * (2.2 - roughness) * day * lerp(1.0, 0.16, distant);
                float3 sky = GlossyEnvironmentReflection(reflect(-V, n), p, roughness, 1.0) * (0.1 + 0.9 * day);
                sky = max(sky, float3(0.20, 0.38, 0.62) * (0.1 + 0.9 * day));
                col = lerp(col, sky, fres * 0.85) + spec * l.color * 0.26;

                // Onde de tsunami : crête éclaircie, creux assombri, et écume là où le front est raide.
                float crest = smoothstep(0.15, 2.5, i.water.w);
                float trough = smoothstep(0.15, 2.5, -i.water.w);
                col *= 1.0 - 0.22 * trough;
                col += float3(0.05, 0.13, 0.15) * crest * day;
                float steep = length(slope);

                // Écume : liseré de vagues le long des rives (discret par temps calme), moutons par gros temps,
                // déferlement des tsunamis.
                float foamNoise = noise3(p * 0.55 + float3(t * 0.21, t * 0.13, -t * 0.17)) * 0.6
                    + noise3(p * 1.7 - float3(t * 0.3, 0.0, t * 0.26)) * 0.4;
                float surf = 0.5 + 0.5 * sin(depth * 7.0 - t * 1.4 + foamNoise * 6.0);
                // Le liseré ne se forme que là où le fond remonte franchement : pas sur une lame d'eau étale.
                float beach = smoothstep(0.015, 0.06, fwidth(depth) / max(footprint, 1e-4));
                float shore = (1.0 - smoothstep(0.1, 0.9, depth)) * smoothstep(-0.3, 0.1, depth) * beach * (1.0 - i.ai.z);
                float foam = shore * smoothstep(0.55, 0.95, foamNoise * 0.5 + surf * 0.6) * (0.2 + 0.8 * amp);
                foam += smoothstep(0.7, 0.95, foamNoise + amp * 0.3 - 0.2) * smoothstep(0.7, 1.0, amp) * 0.6;
                foam += saturate(i.ai.a * 1.6 + smoothstep(0.05, 0.22, steep) * crest) * smoothstep(0.3, 0.75, foamNoise + 0.25);
                foam = saturate(foam) * (1.0 - smoothstep(0.4, 1.6, footprint) * 0.7);
                col = lerp(col, float3(0.92, 0.96, 0.97) * (0.22 + 0.78 * day), foam * 0.75);

                col = lerp(col, float3(0.85, 0.9, 0.95) * (0.2 + 0.8 * day), ice);

                // Transparence : le fond se devine sous quelques unités d'eau, la rive est fondue. Les reflets
                // rasants, l'écume et la glace restent opaques.
                float alpha = lerp(0.55, 0.97, smoothstep(0.0, 8.0, depth));
                alpha = lerp(alpha, 1.0, saturate(fres * 0.9 + foam + ice));
                // Un ruban de rivière reste presque opaque, sinon ses tronçons superposés se voient aux confluents.
                alpha = lerp(alpha, max(alpha, 0.9), i.ai.z);
                alpha *= smoothstep(-0.3, 0.25, depth);
                clip(alpha - 0.01);

                col = MixFog(col, i.fog);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
