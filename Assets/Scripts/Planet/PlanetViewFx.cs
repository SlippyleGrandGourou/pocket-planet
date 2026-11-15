using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    // Effets : astéroïdes (roche, lueur, traînée, boule de feu, onde de choc) et flash lumineux partagé.
    public sealed partial class PlanetView
    {
        Bucket astRock, astGlow, astTrail, astBall, astRing, astPath, astZone;
        Light flashLight;
        Vector3 flashPos;
        float flashI;

        void BuildFxBuckets()
        {
            var sb = new MeshBuilder();
            sb.Sphere(Vector3.zero, 1f, 1f, 7, 10);
            var sm = sb.Build();
            var bb = new MeshBuilder();
            bb.Box(Vector3.zero, Vector3.one);
            var bm = bb.Build();
            astRock = new Bucket { mesh = sm, mat = MakeLit(new Color(0.22f, 0.2f, 0.19f), 0.1f, true) };
            astGlow = new Bucket { mesh = sm, mat = MakeUnlit(new Color(1f, 0.55f, 0.15f)) };
            astTrail = new Bucket { mesh = bm, mat = MakeUnlit(new Color(1f, 0.7f, 0.35f)) };
            astBall = new Bucket { mesh = sm, mat = MakeUnlit(new Color(1f, 0.6f, 0.2f)) };
            astRing = new Bucket { mesh = sm, mat = MakeUnlit(new Color(1f, 0.9f, 0.75f)) };
            astPath = new Bucket { mesh = bm, mat = MakeUnlit(new Color(1f, 0.35f, 0.25f)) };
            astZone = new Bucket { mesh = sm, mat = MakeUnlit(new Color(1f, 0.25f, 0.2f)) };
        }

        void Flash(Vector3 pos, float intensity)
        {
            if (intensity < flashI && (flashPos - lastCamPos).sqrMagnitude < (pos - lastCamPos).sqrMagnitude) return;
            flashPos = pos; flashI = intensity;
        }

        void UpdateFlash()
        {
            if (flashLight == null)
            {
                var go = new GameObject("FlashLight");
                go.transform.SetParent(root.transform, false);
                flashLight = go.AddComponent<Light>();
                flashLight.type = LightType.Point;
                flashLight.color = new Color(0.85f, 0.9f, 1f);
                flashLight.shadows = LightShadows.None;
                flashLight.enabled = false;
            }
            flashI = Mathf.MoveTowards(flashI, 0f, Time.deltaTime * 2.5f);
            if (flashI <= 0.01f) { flashLight.enabled = false; return; }
            float dist = Mathf.Max(30f, (flashPos - lastCamPos).magnitude);
            flashLight.enabled = true;
            flashLight.transform.position = flashPos;
            flashLight.range = dist * 2f;
            flashLight.intensity = flashI * 0.3f * dist * dist;
        }

        void DrawAsteroids(Bounds bounds)
        {
            if (astRock == null) BuildFxBuckets();
            UpdateFlash();
            astRock.n = astGlow.n = astTrail.n = astBall.n = astRing.n = astPath.n = astZone.n = 0;
            float t = Time.time;
            foreach (var a in S.asteroids)
            {
                // trajectoire estimée en pointillés et zone d'impact pulsante au sol
                int pc = a.path.Count;
                for (int i = 0; i + 1 < pc; i++)
                {
                    if (((i + (int)(t * 3f)) & 1) != 0) continue;
                    Vector3 p0 = a.path[i], p1 = a.path[i + 1];
                    Vector3 mid = (p0 + p1) * 0.5f;
                    float dd = (mid - lastCamPos).magnitude;
                    float w = Mathf.Clamp(dd * 0.0035f, 1.5f, 60f);
                    Vector3 seg = p1 - p0;
                    if (seg.sqrMagnitude < 1e-3f) continue;
                    astPath.Add(Matrix4x4.TRS(mid, Quaternion.LookRotation(seg), new Vector3(w, w, seg.magnitude * 0.8f)));
                }
                if (a.predCell >= 0)
                {
                    Vector3 pn = G.Dir[a.predCell];
                    Vector3 pp = pn * (R + Mathf.Max(0f, S.Ground(a.predCell)) + 1.5f);
                    float pulse = 1f + 0.12f * Mathf.Sin(t * 5f);
                    float rr = S.AstImpactRadius(a) * pulse;
                    astZone.Add(Matrix4x4.TRS(pp, Quaternion.FromToRotation(Vector3.up, pn), new Vector3(rr, 1.2f, rr)));
                }
                float alt = a.pos.magnitude - R;
                float atm = Mathf.Clamp01(1f - alt / 450f);
                float rs = Mathf.Max(7f + 10f * a.size, (a.pos - lastCamPos).magnitude * 0.004f);
                Vector3 vd = a.vel.normalized;
                var rot = Quaternion.LookRotation(vd) * Quaternion.Euler(a.age * 40f, a.age * 25f, 0f);
                astRock.Add(Matrix4x4.TRS(a.pos, rot, new Vector3(rs, rs * 0.8f, rs * 1.1f)));
                if (atm > 0.02f)
                {
                    float g = rs * (1.4f + 0.3f * Mathf.Sin(t * 20f)) * (0.6f + 0.6f * atm);
                    astGlow.Add(Matrix4x4.TRS(a.pos, Quaternion.identity, Vector3.one * g));
                    float tl = rs * (8f + 14f * atm);
                    float tw = rs * (0.5f + 0.6f * atm);
                    astTrail.Add(Matrix4x4.TRS(a.pos - vd * tl * 0.5f, Quaternion.LookRotation(vd), new Vector3(tw, tw, tl)));
                }
                else
                {
                    float tl = rs * 5f;
                    astTrail.Add(Matrix4x4.TRS(a.pos - vd * tl * 0.5f, Quaternion.LookRotation(vd), new Vector3(rs * 0.3f, rs * 0.3f, tl)));
                }
                Flash(a.pos, atm * 0.25f);
            }
            foreach (var f in S.astFx)
            {
                float k = f.age / f.life;
                if (f.kind == PlanetSim.AstBurn)
                {
                    float s = f.rw * (0.3f + k);
                    astBall.Add(Matrix4x4.TRS(f.pos, Quaternion.identity, Vector3.one * s * (1f - k)));
                    if (k < 0.15f) Flash(f.pos, 0.5f);
                    continue;
                }
                float grow = Mathf.Sin(Mathf.Clamp01(k * 1.4f) * Mathf.PI * 0.5f);
                float shrink = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f;
                // L'onde de choc d'une explosion volcanique (Blast()) n'a pas besoin de la boule de feu, juste
                // de l'anneau qui s'étend.
                if (f.kind != PlanetSim.AstBlast)
                    astBall.Add(Matrix4x4.TRS(f.pos, Quaternion.identity, Vector3.one * f.rw * 0.55f * grow * shrink));
                if (f.kind == PlanetSim.AstImpact || f.kind == PlanetSim.AstAir || f.kind == PlanetSim.AstBlast)
                {
                    float rr = f.rw * (0.3f + 3.5f * grow);
                    float th = Mathf.Max(0.1f, (1f - k) * f.rw * 0.05f);
                    var q = Quaternion.FromToRotation(Vector3.up, f.normal);
                    astRing.Add(Matrix4x4.TRS(f.pos, q, new Vector3(rr, th, rr)));
                }
                if (k < 0.25f) Flash(f.pos + f.normal * f.rw * 0.5f, 1f - k * 3f);
            }
            DrawBucket(astRock, bounds, true);
            DrawBucket(astGlow, bounds, false);
            DrawBucket(astTrail, bounds, false);
            DrawBucket(astBall, bounds, false);
            DrawBucket(astRing, bounds, false);
            DrawBucket(astPath, bounds, false);
            DrawBucket(astZone, bounds, false);
        }
    }
}
