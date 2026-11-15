Shader "LivingPlanet/Atmosphere"
{
    Properties { _Cull ("Cull", Float) = 1 }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Atmosphere"
            Tags { "LightMode"="UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Cull;
            CBUFFER_END
            float4 _LP_Planet; // x: rayon planète, y: rayon atmosphère, z: intensité
            float4 _LP_SunDir;
            float _LP_Cloud;   // couverture nuageuse moyenne au-dessus de la caméra (voile blanc, ciel moins saturé)

            struct Attr { float4 pos : POSITION; };
            struct Vary { float4 cs : SV_POSITION; float3 ws : TEXCOORD0; };

            Vary vert(Attr i)
            {
                Vary o;
                o.ws = TransformObjectToWorld(i.pos.xyz);
                o.cs = TransformWorldToHClip(o.ws);
                return o;
            }

            float2 Sph(float3 o, float3 d, float r)
            {
                float b = dot(o, d);
                float c = dot(o, o) - r * r;
                float h = b * b - c;
                if (h < 0.0) return float2(1.0, -1.0);
                h = sqrt(h);
                return float2(-b - h, -b + h);
            }

            half4 frag(Vary i) : SV_Target
            {
                float3 o = _WorldSpaceCameraPos;
                float3 d = normalize(i.ws - o);
                float Rp = _LP_Planet.x, Ra = _LP_Planet.y;
                float3 sun = _LP_SunDir.xyz;

                float2 ta = Sph(o, d, Ra);
                if (ta.y < 0.0 || ta.x > ta.y) return 0;
                float t0 = max(ta.x, 0.0), t1 = ta.y;
                float2 tp = Sph(o, d, Rp);
                if (tp.y > 0.0 && tp.x > 0.0) t1 = min(t1, tp.x);
                if (t1 <= t0) return 0;

                float H = Ra - Rp;
                float hr = H * 0.23, hm = H * 0.07;
                const float3 br = float3(0.0008, 0.0019, 0.0045);
                const float3 bo = float3(0.00009, 0.00027, 0.00001); // ozone : absorbe le vert/rouge, bleute le ciel au crépuscule
                float cl = saturate(_LP_Cloud);
                float bm = 0.003 * (1.0 + 3.0 * cl);

                const int VS = 16, LS = 6;
                float ds = (t1 - t0) / VS;
                float odR = 0, odM = 0, odO = 0;
                float3 sumR = 0, sumM = 0;

                for (int k = 0; k < VS; k++)
                {
                    float3 p = o + d * (t0 + (k + 0.5) * ds);
                    float h = max(length(p) - Rp, 0.0);
                    float dr = exp(-h / hr) * ds;
                    float dm = exp(-h / hm) * ds;
                    float dO = max(0.0, 1.0 - abs(h - 0.25 * H) / (0.15 * H)) * ds;
                    odR += dr; odM += dm; odO += dO;

                    float2 ts = Sph(p, sun, Rp);
                    if (ts.y > 0.0 && ts.x > 0.0) continue;
                    float len = Sph(p, sun, Ra).y;
                    float ls = len / LS;
                    float lR = 0, lM = 0, lO = 0;
                    for (int j = 0; j < LS; j++)
                    {
                        float3 q = p + sun * ((j + 0.5) * ls);
                        float hq = max(length(q) - Rp, 0.0);
                        lR += exp(-hq / hr) * ls;
                        lM += exp(-hq / hm) * ls;
                        lO += max(0.0, 1.0 - abs(hq - 0.25 * H) / (0.15 * H)) * ls;
                    }
                    float3 att = exp(-(br * (odR + lR) + bm * 1.1 * (odM + lM) + bo * (odO + lO)));
                    sumR += dr * att;
                    sumM += dm * att;
                }

                float mu = dot(d, sun);
                float g = lerp(0.76, 0.5, cl);
                float phR = 0.0597 * (1.0 + mu * mu);
                float phM = 0.119 * (1.0 - g * g) * (1.0 + mu * mu) / ((2.0 + g * g) * pow(1.0 + g * g - 2.0 * g * mu, 1.5));
                // diffusion multiple approchée : un peu de lumière isotrope qui adoucit les zones d'ombre du ciel
                float3 col = _LP_Planet.z * (sumR * br * (phR + 0.012) + sumM * bm * phM);
                float lum = dot(col, float3(0.3, 0.59, 0.11));
                col = lerp(col, lum.xxx * float3(1.0, 1.0, 1.02), cl * 0.45) * (1.0 - 0.25 * cl);
                col = 1.0 - exp(-col * 1.05);
                col = pow(col, 0.96);
                return half4(col, 0);
            }
            ENDHLSL
        }
    }
}
