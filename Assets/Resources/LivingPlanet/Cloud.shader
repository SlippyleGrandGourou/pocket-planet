Shader "LivingPlanet/Cloud"
{
    // Volumes de nuages instanciés. L'éclairage ne dépend que de la position sur la planète (centrée à l'origine)
    // et de la direction du soleil : il reste donc juste vu de l'espace, où la lumière ambiante de la scène est
    // réglée pour la caméra et non pour le nuage regardé.
    Properties { }
    SubShader
    {
        // Après l'atmosphère (Transparent) : vu du sol, le ciel est dessiné par elle et ne passe pas là où un
        // nuage a déjà écrit sa profondeur ; les bords fondus se mélangeraient alors à du noir.
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Cloud"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // puff : xyz normale de l'enveloppe arrondie du nuage (repère objet), w hauteur dans le nuage (0 base, 1 sommet).
            // col.r : 1 si le contour du nuage est fondu, 0 s'il reste net (tours d'orage).
            struct Attr { float4 pos : POSITION; float3 normal : NORMAL; float4 puff : TEXCOORD1; float4 col : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Vary
            {
                float4 cs : SV_POSITION; float3 ws : TEXCOORD0; float3 facet : TEXCOORD1; float3 puff : TEXCOORD2;
                float4 tint : TEXCOORD3; float3 hf : TEXCOORD4;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _LP_CloudFlash; // xyz position de l'éclair en cours, w son intensité (0 s'il n'y en a pas)
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _LP_CloudTint) // rgb teinte, a opacité
            UNITY_INSTANCING_BUFFER_END(Props)

            Vary vert(Attr i)
            {
                Vary o;
                UNITY_SETUP_INSTANCE_ID(i);
                o.ws = TransformObjectToWorld(i.pos.xyz);
                o.cs = TransformWorldToHClip(o.ws);
                o.facet = TransformObjectToWorldNormal(i.normal);
                o.puff = TransformObjectToWorldNormal(i.puff.xyz);
                o.tint = UNITY_ACCESS_INSTANCED_PROP(Props, _LP_CloudTint);
                o.hf = float3(i.puff.w, ComputeFogFactor(o.cs.z), i.col.r);
                return o;
            }

            half4 frag(Vary i) : SV_Target
            {
                float3 sun = normalize(_MainLightPosition.xyz);
                float3 up = normalize(i.ws);
                float3 view = normalize(_WorldSpaceCameraPos - i.ws);
                float3 puff = normalize(i.puff);
                // Les facettes restent lisibles (style du jeu) mais l'essentiel du modelé vient de l'enveloppe arrondie.
                float3 n = normalize(lerp(normalize(i.facet), puff, 0.7));

                float elev = dot(up, sun);                       // hauteur du soleil vue depuis le nuage
                float day = smoothstep(-0.12, 0.2, elev);
                float low = saturate(1.0 - abs(elev) / 0.32);
                float3 sunCol = lerp(float3(1.0, 0.97, 0.92), float3(1.0, 0.52, 0.26), pow(low, 1.3));
                float sunPower = min(1.0, max(_MainLightColor.r, max(_MainLightColor.g, _MainLightColor.b)));

                float lit = saturate(dot(n, sun) * 0.6 + 0.4);
                lit *= lit;
                float body = lerp(0.6, 1.0, smoothstep(0.0, 0.7, i.hf.x)); // base plus sombre que le sommet
                float3 ambient = lerp(float3(0.035, 0.045, 0.075), float3(0.5, 0.57, 0.7), day);
                // Un nuage de pluie est gris par sa base ; son sommet, vu d'en haut, reste clair. Les cendres
                // (teinte très sombre) ne s'éclaircissent pas.
                float lum = dot(i.tint.rgb, float3(0.3, 0.59, 0.11));
                float3 tint = lerp(i.tint.rgb, float3(1.0, 1.0, 1.0), 0.6 * i.hf.x * saturate(dot(n, up) + 0.3) * saturate(lum * 6.0 - 0.3));
                float3 col = tint * body * (ambient + sunCol * (0.6 * lit * day * sunPower));

                // liseré lumineux quand le soleil est derrière le nuage
                float edge = 1.0 - saturate(dot(puff, view));
                float back = saturate(dot(-view, sun));
                col += sunCol * (0.45 * edge * edge * back * back * back * day * sunPower);

                // Bords fondus. La normale à facettes limite le fondu au vrai contour : sans elle, un nuage plat
                // vu par la tranche (stratus) serait transparent sur toute sa surface. Une tour d'orage, haute et
                // étroite, n'a pas d'enveloppe arrondie fiable : ses flancs se troueraient, elle garde un contour net.
                float facing = max(dot(puff, view), dot(normalize(i.facet), view) - 0.08);
                float alpha = i.tint.a * lerp(1.0, smoothstep(0.0, 0.22, facing), i.hf.z);

                // Éclair : même loi que la lumière ponctuelle qui éclaire le sol (voir UpdateFlash), dont
                // l'intensité est réglée pour valoir 0,3 à la distance de la caméra.
                if (_LP_CloudFlash.w > 0.01)
                {
                    float3 toFlash = _LP_CloudFlash.xyz - i.ws;
                    float reach = max(30.0, distance(_LP_CloudFlash.xyz, _WorldSpaceCameraPos));
                    float r2 = max(dot(toFlash, toFlash), 1.0);
                    float fall = saturate(1.0 - r2 / (4.0 * reach * reach));
                    float wrap = saturate(dot(n, toFlash * rsqrt(r2)) * 0.5 + 0.5);
                    col += tint * float3(0.85, 0.9, 1.0) * (_LP_CloudFlash.w * 0.3 * min(4.0, reach * reach / r2) * fall * fall * wrap);
                }
                col = MixFog(col, i.hf.y);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
