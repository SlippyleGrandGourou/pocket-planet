Shader "LivingPlanet/CloudShell"
{
    // Couche nuageuse vue de loin : une sphère à l'altitude des nuages, dont chaque sommet porte la couverture
    // calculée par la simulation (couleur : r couverture, g orage, b cendres). Un bruit découpe cette couverture
    // en bancs aux bords nets, et un second échantillon décalé vers le soleil donne un relief en aplats.
    Properties { }
    SubShader
    {
        // Après l'atmosphère (Transparent), avant les volumes de nuages (Transparent+10).
        Tags { "RenderType"="Transparent" "Queue"="Transparent+9" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "CloudShell"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attr { float4 pos : POSITION; float4 col : COLOR; };
            struct Vary { float4 cs : SV_POSITION; float3 ws : TEXCOORD0; float4 col : TEXCOORD1; };

            CBUFFER_START(UnityPerMaterial)
                float _LP_ShellFade;    // 0 près du sol (les volumes suffisent), 1 vu de l'espace
                float _LP_ShellDrift;   // temps de dérive des bancs, arrêté en pause
                float4 _LP_LensOrigin;  // w : 1 si la lentille est active
                float4 _LP_LensDir;
            CBUFFER_END

            Vary vert(Attr i)
            {
                Vary o;
                o.ws = TransformObjectToWorld(i.pos.xyz);
                o.cs = TransformWorldToHClip(o.ws);
                o.col = i.col;
                return o;
            }

            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float vnoise(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(hash(i), hash(i + float3(1, 0, 0)), f.x), lerp(hash(i + float3(0, 1, 0)), hash(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(hash(i + float3(0, 0, 1)), hash(i + float3(1, 0, 1)), f.x), lerp(hash(i + float3(0, 1, 1)), hash(i + float3(1, 1, 1)), f.x), f.y),
                    f.z);
            }

            // Seuil de couverture au-dessus duquel il y a un nuage en ce point : il varie avec le bruit, si bien
            // qu'une couverture de 0,4 donne un ciel couvert à 40 % environ, et non un voile uniforme.
            float Threshold(float3 q)
            {
                float n = 0.42 * vnoise(q) + 0.33 * vnoise(q * 2.03 + 7.1) + 0.25 * vnoise(q * 4.1 + 13.7);
                return 0.06 + 0.8 * (1.0 - saturate((n - 0.25) * 2.0));
            }

            half4 frag(Vary i) : SV_Target
            {
                float3 dir = normalize(i.ws);
                float3 sun = normalize(_MainLightPosition.xyz);
                float cover = i.col.r, storm = i.col.g, ash = i.col.b;

                float3 q = dir * 13.0 + float3(_LP_ShellDrift, 0.0, 0.7 * _LP_ShellDrift);
                float thick = cover - Threshold(q);
                float alpha = smoothstep(0.0, 0.1, thick);
                if (alpha <= 0.001) discard;

                // relief : le nuage est plus clair là où il s'amincit vers le soleil (flanc éclairé)
                float3 toSun = sun - dir * dot(sun, dir);
                float towardSun = cover - Threshold(q + toSun * 0.35);
                float relief = saturate(0.5 + (thick - towardSun) * 3.0);

                float elev = dot(dir, sun);
                float day = smoothstep(-0.12, 0.2, elev);
                float low = saturate(1.0 - abs(elev) / 0.32);
                float3 sunCol = lerp(float3(1.0, 0.97, 0.92), float3(1.0, 0.52, 0.26), pow(low, 1.3));
                float sunPower = min(1.0, max(_MainLightColor.r, max(_MainLightColor.g, _MainLightColor.b)));
                float3 ambient = lerp(float3(0.035, 0.045, 0.075), float3(0.5, 0.57, 0.7), day);

                // teintes en espace linéaire, comme celles que le code donne aux volumes
                float3 tint = lerp(float3(1.0, 1.0, 1.0), float3(0.2, 0.21, 0.26), storm * 0.85);
                tint = lerp(tint, float3(0.033, 0.024, 0.02), saturate(ash));
                float3 col = tint * (ambient * (0.8 + 0.2 * relief) + sunCol * ((0.08 + 0.5 * relief) * day * sunPower));

                // lentille (touche X) : même ouverture que pour les volumes
                if (_LP_LensOrigin.w > 0.5)
                {
                    float3 v = i.ws - _LP_LensOrigin.xyz;
                    float t = dot(v, _LP_LensDir.xyz);
                    if (t > 1.0) alpha *= smoothstep(0.045, 0.09, length(v - _LP_LensDir.xyz * t) / t);
                }
                return half4(col, alpha * 0.94 * _LP_ShellFade);
            }
            ENDHLSL
        }
    }
}
