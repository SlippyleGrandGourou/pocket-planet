using System;
using System.IO;
using System.Reflection;
using LivingPlanet;
using UnityEngine;

// Exécutable en batch Unity avec -executeMethod TectonicValidation.Run.
public static class TectonicValidation
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    sealed class ZeroRandom : System.Random
    {
        public override double NextDouble() { return 0d; }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static PlanetSim MakePlanet()
    {
        var s = new PlanetSim(new PlanetGrid(16, 75f));
        s.ApplyAge(4.5f);
        s.Generate(123);
        s.plateSpeed = 0.016f;
        Array.Clear(s.plate, 0, s.plate.Length);
        Array.Clear(s.conv, 0, s.conv.Length);
        Array.Clear(s.rift, 0, s.rift.Length);
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        axes[0] = Vector3.up;
        rates[0] = 0.03f;
        return s;
    }

    static void Move(PlanetSim s, float dt)
    {
        typeof(PlanetSim).GetMethod("AdvectCrust", Private).Invoke(s, new object[] { dt });
    }

    static double Solid(PlanetSim s)
    {
        double result = 0;
        for (int c = 0; c < s.M; c++)
            result += (s.rock[c] - s.magmaInflation[c] + s.sed[c]) * (double)s.G.Area[c];
        return result;
    }

    static void StepWorld(PlanetSim s, System.Random random, bool geology)
    {
        s.StepClimate();
        s.StepOcean();
        s.StepHydro(); s.StepGroundwater();
        s.StepIce(random);
        if (geology) { s.StepGeology(); s.StepLithification(); s.StepTectonics(); }
        s.StepCoastline();
        s.tick++;
    }

    static void SeaState(PlanetSim s, out float oceanFraction, out float lowest)
    {
        double area = 0, ocean = 0;
        lowest = float.MaxValue;
        for (int c = 0; c < s.M; c++)
        {
            area += s.G.Area[c];
            if (s.IsOcean(c)) ocean += s.G.Area[c];
            lowest = Mathf.Min(lowest, s.Ground(c));
        }
        oceanFraction = (float)(ocean / area);
    }

    // Altitude au-dessus de la mer dépassée par le dixième le plus haut des terres : mesure la hauteur des reliefs.
    static float HighLand(PlanetSim s) { return LandHeight(s, 0.9f); }

    static float LandHeight(PlanetSim s, float quantile)
    {
        var heights = new System.Collections.Generic.List<float>();
        for (int c = 0; c < s.M; c++)
            if (s.Ground(c) > s.SeaLevel) heights.Add(s.Ground(c) - s.SeaLevel);
        if (heights.Count == 0) return 0f;
        heights.Sort();
        return heights[(int)(quantile * (heights.Count - 1))];
    }

    // Sur plusieurs décennies, la tectonique ne doit ni vider les océans ni creuser de fosse sans fond : les
    // frontières divergentes emportent la croûte, il faut qu'elle soit remplacée au même rythme.
    static void LongTermSeaLevel()
    {
        const int years = 40;
        var s = new PlanetSim(new PlanetGrid(48, 1200f));
        s.ApplyAge(4.5f);
        s.Generate(123);
        var random = new System.Random(7);
        for (int i = 0; i < 300; i++) StepWorld(s, random, false);
        float seaBefore = s.SeaLevel, oceanBefore, lowestBefore, highBefore = HighLand(s);
        double geologyBefore, waterBefore, saltBefore, geologyAfter, waterAfter, saltAfter;
        double unweighted;
        s.MaterialIndices(out unweighted, out waterBefore, out saltBefore, out geologyBefore);
        string peaksBefore = LandHeight(s, 0.5f) + "/" + LandHeight(s, 0.99f) + "/" + LandHeight(s, 1f);
        SeaState(s, out oceanBefore, out lowestBefore);
        for (int i = 0; i < years * PlanetSim.TicksPerYear; i++) StepWorld(s, random, true);
        float oceanAfter, lowestAfter;
        SeaState(s, out oceanAfter, out lowestAfter);
        string measured = "sea " + seaBefore + " -> " + s.SeaLevel + ", ocean " + oceanBefore + " -> " + oceanAfter +
            ", lowest " + lowestBefore + " -> " + lowestAfter;
        float highAfter = HighLand(s);
        measured += ", high land " + highBefore + " -> " + highAfter + ", median/p99/max " + peaksBefore + " -> " +
            LandHeight(s, 0.5f) + "/" + LandHeight(s, 0.99f) + "/" + LandHeight(s, 1f);
        s.MaterialIndices(out unweighted, out waterAfter, out saltAfter, out geologyAfter);
        measured += ", geology " + geologyBefore + " -> " + geologyAfter + ", water " + waterBefore + " -> " + waterAfter;
        double mantle = 0, area = 0;
        for (int c = 0; c < s.M; c++) { mantle += s.mantleSolid[c] * (double)s.G.Area[c]; area += s.G.Area[c]; }
        measured += ", mantle mean " + (mantle / area);
        Debug.Log("LONG TERM SEA LEVEL years=" + years + " " + measured);
        Check(Mathf.Abs(s.SeaLevel - seaBefore) < 5f, "Sea level drifted over " + years + " years: " + measured);
        Check(Mathf.Abs(oceanAfter - oceanBefore) < 0.06f, "Ocean area drifted over " + years + " years: " + measured);
        Check(Mathf.Abs(highAfter - highBefore) < highBefore * 0.15f, "Relief height drifted over " + years + " years: " + measured);
        Check(lowestAfter > lowestBefore - 30f, "Tectonics dug a bottomless trench over " + years + " years: " + measured);
    }

    static double Sediment(PlanetSim s, int material = -1)
    {
        double result = 0;
        for (int c = 0; c < s.M; c++)
            result += s.sed[c] * (double)s.G.Area[c] * (material < 0 ? 1f : s.mix[c * 4 + material]);
        return result;
    }

    static void FlatRotation()
    {
        var s = MakePlanet();
        double area = 0;
        for (int c = 0; c < s.M; c++)
        {
            area += s.G.Area[c];
            s.rock[c] = 40f; s.sed[c] = 2f; s.magmaInflation[c] = 0f;
        }
        double expected = 4.0 * Math.PI * s.G.Radius * s.G.Radius;
        Check(Math.Abs(area - expected) < expected * 1e-6, "Cell areas do not cover the sphere");
        for (int i = 0; i < 50; i++) Move(s, 0.1f);
        for (int c = 0; c < s.M; c++)
        {
            Check(Math.Abs(s.rock[c] - 40f) < 0.002f, "Rigid rotation deformed uniform crust");
            Check(Math.Abs(s.sed[c] - 2f) < 0.0001f, "Rigid rotation deformed uniform sediment");
        }
    }

    static void MaterialTransport()
    {
        var s = MakePlanet();
        double solid = Solid(s), sediment = Sediment(s);
        double geology, water, salt, weightedBefore;
        s.MaterialIndices(out geology, out water, out salt, out weightedBefore);
        double[] materials = new double[4];
        for (int k = 0; k < 4; k++) materials[k] = Sediment(s, k);
        var originalRock = (float[])s.rock.Clone();
        var originalWater = (float[])s.water.Clone();
        for (int i = 0; i < 200; i++) Move(s, 0.1f);
        double weightedAfter;
        s.MaterialIndices(out geology, out water, out salt, out weightedAfter);
        Check(Math.Abs(Solid(s) - solid) < Math.Abs(solid) * 0.00002, "Crust volume changed");
        Check(Math.Abs(weightedAfter - weightedBefore) < Math.Abs(weightedBefore) * 0.00002, "Area-weighted geological index changed");
        Check(Math.Abs(Sediment(s) - sediment) < sediment * 0.00002, "Sediment volume changed");
        for (int k = 0; k < 4; k++)
            Check(Math.Abs(Sediment(s, k) - materials[k]) < sediment * 0.00002, "Soil material changed");
        bool moved = false;
        for (int c = 0; c < s.M; c++)
        {
            Check(s.water[c] == originalWater[c], "Drift changed water");
            Check(s.sed[c] >= 0f && s.hard[c] >= 0f && s.hard[c] <= 1.0001f, "Invalid crust state");
            Check(!float.IsNaN(s.rock[c]) && !float.IsInfinity(s.rock[c]), "Invalid rock height");
            float mix = s.mix[c * 4] + s.mix[c * 4 + 1] + s.mix[c * 4 + 2] + s.mix[c * 4 + 3];
            if (s.sed[c] > 0.0001f) Check(Math.Abs(mix - 1f) < 0.0001f, "Invalid soil composition");
            if (Math.Abs(s.rock[c] - originalRock[c]) > 0.1f) moved = true;
        }
        Check(moved, "Crust did not move");
    }

    static void FaceAndPlateBoundary()
    {
        var s = MakePlanet();
        int c = -1, k = -1;
        for (int i = 0; i < s.M && c < 0; i++)
            for (int edge = 0; edge < 4; edge++)
            {
                int n = s.G.Nbr[i * 4 + edge];
                if (n >= 0 && i / (s.G.N * s.G.N) != n / (s.G.N * s.G.N) &&
                    Vector3.Dot(Vector3.up, s.G.EdgeFlux[i * 4 + edge]) > 0.001f)
                { c = i; k = edge; break; }
            }
        Check(c >= 0, "No outgoing cube-face boundary");
        int destination = s.G.Nbr[c * 4 + k];
        Array.Clear(s.sed, 0, s.M);
        s.sed[c] = 10f;
        double before = Sediment(s);
        Move(s, 1f);
        Check(s.sed[destination] > 0f, "Sediment did not cross cube face");
        Check(Math.Abs(Sediment(s) - before) < before * 0.00001, "Cross-face sediment volume changed");

        s = MakePlanet();
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        axes[1] = axes[0]; rates[1] = rates[0];
        Array.Clear(s.sed, 0, s.M);
        for (int i = 0; i < s.M; i++) { s.rock[i] = 20f; s.magmaInflation[i] = 0f; }
        s.sed[c] = 10f;
        s.plate[destination] = 1;
        before = Sediment(s);
        Move(s, 1f);
        Check(s.sed[destination] > 0f, "Moving crust was blocked by the plate boundary");
        Check(Math.Abs(Sediment(s) - before) < before * 0.00001, "Boundary transport changed sediment volume");
    }

    static void OldMountainRelaxation()
    {
        var s = MakePlanet();
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = 0f;
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 0f; s.magmaInflation[c] = 0f;
        }
        int peak = s.G.CellIndex(0, 8, 8);
        int neighbor = s.G.Nbr[peak * 4 + 1];
        s.rock[peak] = 120f;
        double before = Solid(s);
        for (int i = 0; i < 4000; i++) Move(s, 0.05f);
        Check(s.Ground(peak) < 80f && s.Ground(neighbor) > 25f,
            "An old steep mountain did not spread beyond an inactive fault");
        float maxSlope = 0f;
        for (int c = 0; c < s.M; c++)
            for (int k = 0; k < 4; k++)
            {
                int edge = c * 4 + k, n = s.G.Nbr[edge];
                if (n >= 0) maxSlope = Mathf.Max(maxSlope, Mathf.Abs(s.Ground(c) - s.Ground(n)) / s.G.Dist[edge]);
            }
        Debug.Log($"OLD MOUNTAIN maxSlope={maxSlope:F2}");
        Check(maxSlope < 1.3f, "An old mountain retained excessively steep slopes");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.00002, "Mountain spreading changed crust volume");
    }

    static void ExtremeCliffRelaxation()
    {
        var s = MakePlanet();
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = 0f;
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 0f; s.magmaInflation[c] = 0f;
        }
        int peak = s.G.CellIndex(0, 8, 8);
        s.rock[peak] = 120f;
        double before = Solid(s);
        Move(s, 0.05f);
        Check(s.Ground(peak) < 80f, "Extreme cliff did not relax promptly");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.000002, "Cliff collapse changed crust volume");
    }

    static void MountainDrift()
    {
        var s = MakePlanet();
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        float cellAngle = Mathf.PI * 0.5f / s.G.N;
        rates[0] = cellAngle * 0.008f;
        Vector3 center = s.G.Dir[s.G.CellIndex(0, 8, 8)];
        for (int c = 0; c < s.M; c++)
        {
            float distance = Vector3.Angle(center, s.G.Dir[c]) * Mathf.Deg2Rad / cellAngle;
            s.rock[c] = 20f + 25f * Mathf.Exp(-distance * distance / 8f);
            s.sed[c] = 0f; s.magmaInflation[c] = 0f;
        }
        double before = Solid(s);
        for (int i = 0; i < 1400; i++) Move(s, 0.5f);
        Vector3 centroid = Vector3.zero;
        float peak = 0f;
        for (int c = 0; c < s.M; c++)
        {
            float height = Mathf.Max(0f, s.Ground(c) - 20f);
            centroid += s.G.Dir[c] * (height * s.G.Area[c]);
            peak = Mathf.Max(peak, height);
        }
        Vector3 expected = Quaternion.AngleAxis(rates[0] * 700f * Mathf.Rad2Deg, Vector3.up) * center;
        float error = Vector3.Angle(centroid, expected) * Mathf.Deg2Rad / cellAngle;
        Debug.Log($"MOUNTAIN DRIFT errorCells={error:F2} remainingPeak={peak:F2}");
        Check(error < 1.25f && peak > 4f, "Mountain did not remain visible while moving with its plate");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.00002, "Mountain drift changed crust volume");
    }

    static void PlateLifecycle()
    {
        var s = MakePlanet();
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        for (int c = 0; c < s.M; c++)
        {
            s.plate[c] = (byte)(c / (s.G.N * s.G.N) % 3);
            s.rift[c] = 0f;
        }
        for (int p = 0; p < 3; p++) { axes[p] = Vector3.up; rates[p] = 0.0003f; }
        s.MigrateActivePlates();
        var active = (bool[])typeof(PlanetSim).GetField("activePlates", Private).GetValue(s);
        var evolve = typeof(PlanetSim).GetMethod("EvolvePlateBoundaries", Private);
        evolve.Invoke(s, new object[] { 1 });
        int count = 0;
        foreach (bool value in active) if (value) count++;
        Check(count == 2, "Adjacent plates moving together did not merge");
        typeof(PlanetSim).GetMethod("AssignPlates", Private).Invoke(s, null);
        for (int c = 0; c < s.M; c++)
        {
            Check(active[s.plate[c]], "A merged plate reappeared");
            s.rift[c] = 1f;
        }
        evolve.Invoke(s, new object[] { 2 });
        count = 0;
        foreach (bool value in active) if (value) count++;
        Check(count == 3, "An extended plate did not split");
        typeof(PlanetSim).GetMethod("AssignPlates", Private).Invoke(s, null);
        int[] sizes = new int[PlanetSim.PlateCount];
        foreach (byte p in s.plate) sizes[p]++;
        int populated = 0;
        for (int p = 0; p < sizes.Length; p++) if (active[p] && sizes[p] > 0) populated++;
        Check(populated == 3, "New plate did not acquire territory");
    }

    static void PlateSaveCompatibility()
    {
        var s = MakePlanet();
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        var accumulated = (float[])typeof(PlanetSim).GetField("acc", Private).GetValue(s);
        rates[0] = 0.0004f; accumulated[0] = 0.01f;
        s.MigratePlateDriftSpeed();
        Check(Mathf.Abs(rates[0] - 0.0008f) < 1e-8f && accumulated[0] == 0.01f,
            "Legacy plate speed migration changed the accumulated displacement");
        float speed = s.plateSpeed;
        s.MigrateVisiblePlateDrift();
        Check(Mathf.Abs(rates[0] - 0.0032f) < 1e-8f && Mathf.Abs(s.plateSpeed - speed * 4f) < 1e-7f && accumulated[0] == 0.01f,
            "Visible drift migration did not preserve accumulated displacement");
        s.MigrateActivePlates();
        var save = typeof(PlanetSim).Assembly.GetType("LivingPlanet.PlanetSave");
        var fields = (FieldInfo[])save.GetMethod("Fields", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { typeof(PlanetSim) });
        var writeValue = save.GetMethod("WriteValue", BindingFlags.Static | BindingFlags.NonPublic);
        var readObject = save.GetMethod("ReadObject", BindingFlags.Static | BindingFlags.Public);
        for (int legacy = 0; legacy <= 1; legacy++)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(fields.Length - legacy);
                    foreach (var field in fields)
                    {
                        if (legacy == 1 && field.Name == "activePlates") continue;
                        writer.Write(field.Name);
                        writeValue.Invoke(null, new object[] { writer, field.FieldType, field.GetValue(s) });
                    }
                }
                stream.Position = 0;
                var loaded = new PlanetSim(s.G);
                using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                    readObject.Invoke(null, new object[] { reader, loaded, s.M * 8 + 1024,
                        legacy == 1 ? new[] { "activePlates" } : null, null });
                if (legacy == 1) loaded.MigrateActivePlates();
                var active = (bool[])typeof(PlanetSim).GetField("activePlates", Private).GetValue(loaded);
                Check(active[0] && !active[1], "Active plate state was not restored or migrated");
                typeof(PlanetSim).GetMethod("AssignPlates", Private).Invoke(loaded, null);
                foreach (byte p in loaded.plate) Check(p == 0, "Inactive plate reappeared after loading");
            }
        }
    }

    static void MantleTransport()
    {
        var s = MakePlanet();
        for (int c = 0; c < s.M; c++)
        {
            s.conv[c] = 0.8f;
            s.rift[c] = 0.3f;
            s.magma[c] = (c % 5) * 0.2f;
        }
        double geology, water, salt, before, after;
        s.MaterialIndices(out geology, out water, out salt, out before);
        var move = typeof(PlanetSim).GetMethod("StepMagma", Private);
        for (int i = 0; i < 20; i++) move.Invoke(s, new object[] { 1f });
        s.MaterialIndices(out geology, out water, out salt, out after);
        Check(Math.Abs(after - before) < Math.Abs(before) * 0.000002, "Magma transport changed area-weighted volume");
    }

    static void CompressionRelief()
    {
        var s = MakePlanet();
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = 0f;
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 1f; s.magmaInflation[c] = 0f;
            s.conv[c] = 0.8f; s.rift[c] = 0f;
        }
        int peak = s.G.CellIndex(0, 8, 8);
        int neighbor = s.G.Nbr[peak * 4 + 1];
        s.rock[peak] = 100f;
        double before = Solid(s);
        for (int i = 0; i < 200; i++) Move(s, 0.1f);
        Check(s.rock[peak] < 65f && s.Ground(neighbor) > 25f, "Compression did not broaden the peak");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.00002, "Compression changed crust volume");

    }

    static void PlateShortening()
    {
        var compressed = MakePlanet();
        var neutral = MakePlanet();
        var divergent = MakePlanet();
        var ratesA = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(compressed);
        var ratesB = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(neutral);
        var ratesC = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(divergent);
        ratesA[0] = ratesB[0] = ratesC[0] = Mathf.PI * 0.5f / compressed.G.N * 0.016f;
        for (int c = 0; c < compressed.M; c++)
        {
            compressed.rock[c] = neutral.rock[c] = 20f;
            compressed.sed[c] = neutral.sed[c] = 0f;
            compressed.magmaInflation[c] = neutral.magmaInflation[c] = 0f;
            divergent.rock[c] = 20f; divergent.sed[c] = 0f; divergent.magmaInflation[c] = 0f;
            divergent.rift[c] = 1f;
        }
        for (int j = 0; j < compressed.G.N; j++)
            for (int i = 0; i < compressed.G.N; i++)
            {
                int c = compressed.G.CellIndex(0, i, j);
                compressed.conv[c] = divergent.conv[c] = 0.8f * Mathf.Exp(-Mathf.Abs(i - 8) * 0.45f);
            }
        double before = Solid(compressed);
        for (int i = 0; i < 300; i++) { Move(compressed, 0.5f); Move(neutral, 0.5f); Move(divergent, 0.5f); }
        float shoulder = 0f, interior = 0f;
        for (int j = 4; j < 12; j++)
        {
            int a = compressed.G.CellIndex(0, 6, j), b = compressed.G.CellIndex(0, 3, j);
            shoulder += compressed.Ground(a) - neutral.Ground(a);
            interior += compressed.Ground(b) - neutral.Ground(b);
        }
        shoulder /= 8f; interior /= 8f;
        Debug.Log($"PLATE SHORTENING shoulder={shoulder:F2} interior={interior:F2}");
        Check(shoulder > 2f && interior < -1f, "Convergence did not compress the plate into a broad shoulder");
        Check(Math.Abs(Solid(compressed) - before) < Math.Abs(before) * 0.00002, "Plate shortening changed crust volume");
        for (int c = 0; c < neutral.M; c++)
            Check(Math.Abs(divergent.Ground(c) - neutral.Ground(c)) < 0.001f, "Rift produced compressive plate shortening");
    }

    static void MountainForelandCompression()
    {
        var compressed = MakePlanet();
        var weaker = MakePlanet();
        var neutral = MakePlanet();
        var ratesA = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(compressed);
        var ratesW = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(weaker);
        var ratesB = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(neutral);
        ratesA[0] = ratesW[0] = ratesB[0] = Mathf.PI * 0.5f / compressed.G.N * 0.016f;
        for (int c = 0; c < compressed.M; c++)
        {
            compressed.rock[c] = weaker.rock[c] = neutral.rock[c] = 30f;
            compressed.sed[c] = weaker.sed[c] = neutral.sed[c] = 0f;
            compressed.magmaInflation[c] = weaker.magmaInflation[c] = neutral.magmaInflation[c] = 0f;
        }
        for (int j = 0; j < compressed.G.N; j++)
            for (int i = 0; i < compressed.G.N; i++)
            {
                int c = compressed.G.CellIndex(0, i, j);
                compressed.rock[c] = weaker.rock[c] = neutral.rock[c] = 30f + 25f * Mathf.Exp(-0.5f * (i - 8) * (i - 8));
                if (i == 8) { compressed.conv[c] = 0.8f; weaker.conv[c] = 0.4f; }
            }
        double before = Solid(compressed);
        for (int i = 0; i < 300; i++) { Move(compressed, 0.5f); Move(weaker, 0.5f); Move(neutral, 0.5f); }
        float[] profile = new float[4];
        float weakShoulder = 0f;
        for (int j = 4; j < 12; j++)
            for (int i = 4; i <= 7; i++)
            {
                int c = compressed.G.CellIndex(0, i, j);
                profile[i - 4] += (compressed.Ground(c) - neutral.Ground(c)) / 8f;
                if (i == 6) weakShoulder += (weaker.Ground(c) - neutral.Ground(c)) / 8f;
            }
        Debug.Log($"FORELAND COMPRESSION profile={profile[0]:F2},{profile[1]:F2},{profile[2]:F2},{profile[3]:F2} weaker={weakShoulder:F2}");
        Check(profile[1] > 1f && profile[2] > 1f,
            "Collision did not raise a broad mountain belt inside the plate");
        Check(profile[2] > weakShoulder + 0.5f, "Stronger convergence did not raise the foreland more");
        Check(Math.Abs(Solid(compressed) - before) < Math.Abs(before) * 0.00002,
            "Foreland compression changed crust volume");
    }

    static void ComputedCollisionForeland()
    {
        var collision = MakePlanet();
        var neutral = MakePlanet();
        var axesA = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(collision);
        var axesB = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(neutral);
        var ratesA = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(collision);
        var ratesB = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(neutral);
        axesA[0] = axesB[0] = Vector3.up;
        axesA[1] = axesB[1] = -Vector3.up;
        ratesA[0] = ratesA[1] = ratesB[0] = ratesB[1] = Mathf.PI * 0.5f / collision.G.N * 0.016f;
        typeof(PlanetSim).GetField("speedNorm", Private).SetValue(collision, 0.5f / ratesA[0]);
        for (int c = 0; c < collision.M; c++)
        {
            collision.plate[c] = neutral.plate[c] = (byte)(collision.G.Dir[c].z < 0f ? 1 : 0);
            collision.rock[c] = neutral.rock[c] = 30f;
            collision.sed[c] = neutral.sed[c] = 0f;
            collision.magmaInflation[c] = neutral.magmaInflation[c] = 0f;
        }
        typeof(PlanetSim).GetMethod("ComputeActivity", Private).Invoke(collision, null);
        int boundary = collision.G.CellIndex(0, 7, 8);
        Check(collision.conv[boundary] > 0.1f, "Test did not construct converging plates");
        double before = Solid(collision);
        for (int i = 0; i < 60; i++) { Move(collision, 0.5f); Move(neutral, 0.5f); }
        float rise = 0f;
        for (int j = 4; j < 12; j++)
        {
            int c = collision.G.CellIndex(0, 5, j);
            rise += (collision.Ground(c) - neutral.Ground(c)) / 8f;
        }
        Debug.Log($"COMPUTED COLLISION foreland={rise:F2}");
        Check(rise > 1f, "A computed plate collision did not raise its foreland");
        Check(Math.Abs(Solid(collision) - before) < Math.Abs(before) * 0.00002,
            "Computed collision changed crust volume");
    }

    static void VolcanicFootprint()
    {
        var s = MakePlanet();
        s.tectonics = false;
        Array.Clear(s.magma, 0, s.M);
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 0f; s.magmaInflation[c] = 0f;
        }
        int center = s.G.CellIndex(0, 8, 8);
        s.magma[center] = 1f;
        double geology, water, salt, before, after;
        s.MaterialIndices(out geology, out water, out salt, out before);
        s.StartVolcano(center, 0.2f);
        var step = typeof(PlanetSim).GetMethod("StepVolcanoes", Private);
        for (int i = 0; i < 200; i++) step.Invoke(s, null);
        s.MaterialIndices(out geology, out water, out salt, out after);
        int footprint = 0, distant = 0;
        double left = 0d, right = 0d, lower = 0d, upper = 0d;
        for (int c = 0; c < s.M; c++)
        {
            if (s.Ground(c) <= 20.0001f) continue;
            footprint++;
            float distance = Vector3.Angle(s.G.Dir[center], s.G.Dir[c]) * Mathf.Deg2Rad * s.G.Radius / s.G.CellSize;
            if (distance > 2f) distant++;
        }
        for (int j = 4; j <= 12; j++)
            for (int i = 4; i <= 12; i++)
            {
                int c = s.G.CellIndex(0, i, j);
                double volume = Mathf.Max(0f, s.Ground(c) - 20f) * s.G.Area[c];
                if (i < 8) left += volume; else if (i > 8) right += volume;
                if (j < 8) lower += volume; else if (j > 8) upper += volume;
            }
        Debug.Log($"VOLCANIC FOOTPRINT cells={footprint} distant={distant} summit={s.Ground(center) - 20f:F3} asymmetry={Math.Abs(left - right):F3},{Math.Abs(lower - upper):F3}");
        Check(footprint >= 20 && distant >= 8, "Volcanic deposits remained concentrated in a narrow cone");
        Check(Math.Abs(left - right) + Math.Abs(lower - upper) > (left + right + lower + upper) * 0.05,
            "Volcanic footprint is too symmetrical");
        Check(Math.Abs(after - before) < 0.001, "Volcanic footprint changed geological volume");
    }

    static void VolcanicFaceBoundary()
    {
        var s = MakePlanet();
        int center = s.G.CellIndex(0, 0, 8);
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 0f; s.magmaInflation[c] = 0f;
        }
        double before = Solid(s);
        typeof(PlanetSim).GetMethod("DepositVolcanicEdifice", Private).Invoke(s, new object[] { center, 20f, 0.5f, 1f });
        double expected = 20d * s.G.Area[center];
        Check(Math.Abs(Solid(s) - before - expected) < expected * 0.00002, "Volcanic deposit changed volume across cube faces");
        int otherFace = 0;
        for (int c = s.G.N * s.G.N; c < s.M; c++)
            if (s.rock[c] > 20.0001f) otherFace++;
        Check(otherFace > 0, "Volcanic footprint did not cross the cube-face boundary");
    }

    static void VolcanicFlanks()
    {
        var weight = typeof(PlanetSim).GetMethod("VolcanicFootprintWeight", BindingFlags.Static | BindingFlags.NonPublic);
        float low = float.MaxValue, high = 0f;
        const float radius = 4f;
        for (int i = 0; i < 72; i++)
        {
            float angle = i * Mathf.PI * 2f / 72f;
            float x = 1.2f * Mathf.Cos(angle), y = 0.8f * Mathf.Sin(angle);
            float distance = radius * 0.5f * Mathf.Sqrt(x * x + y * y);
            Vector3 direction = new Vector3(x, y, 1f).normalized;
            float w = (float)weight.Invoke(null, new object[] { direction, Vector3.right, Vector3.up, distance, radius, 0f });
            low = Mathf.Min(low, w); high = Mathf.Max(high, w);
        }
        Debug.Log($"VOLCANIC FLANKS low={low:F3} high={high:F3}");
        Check(high > low * 3f, "Volcanic flanks lack distinct ridges and valleys");
    }

    static void HotspotAndPlateDrift()
    {
        var s = MakePlanet();
        int center = s.G.CellIndex(0, 8, 8);
        Array.Clear(s.plumeInf, 0, s.M);
        Array.Clear(s.magma, 0, s.M);
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 0f; s.magmaInflation[c] = 0f;
        }
        s.plumeInf[center] = 1f;
        s.magma[center] = 1f;
        var find = typeof(PlanetSim).GetMethod("FindHotspotEruption", Private);
        Check((int)find.Invoke(s, new object[] { new System.Random(5) }) == center,
            "An isolated charged hotspot was not selected for an eruption");
        var deposit = typeof(PlanetSim).GetMethod("DepositVolcanicEdifice", Private);
        deposit.Invoke(s, new object[] { center, 20f, 0.2f, 1f });
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = Mathf.PI * 0.5f / s.G.N * 0.008f;
        double before = Solid(s);
        Vector3 start = s.G.Dir[center];
        for (int i = 0; i < 1000; i++) Move(s, 0.5f);
        Vector3 centroid = Vector3.zero;
        for (int c = 0; c < s.M; c++)
            centroid += s.G.Dir[c] * (Mathf.Max(0f, s.Ground(c) - 20f) * s.G.Area[c]);
        float drift = Vector3.Angle(start, centroid) * Mathf.Deg2Rad / (Mathf.PI * 0.5f / s.G.N);
        Check(drift > 2f, "Volcanic edifice did not drift with the plate");
        Check(s.plumeInf[center] == 1f && (int)find.Invoke(s, new object[] { new System.Random(5) }) == center,
            "Hotspot moved with the plate");
        float oldHeight = s.Ground(center);
        deposit.Invoke(s, new object[] { center, 20f, 0.2f, 1f });
        Check(s.Ground(center) > oldHeight, "A stationary hotspot could not build a new edifice");
        Check(Math.Abs(before - (Solid(s) - 20d * s.G.Area[center])) < Math.Abs(before) * 0.00002,
            "Drift and renewed hotspot deposit changed crust volume");
        double crustBeforeEruption = Solid(s);
        s.MaybeRandomEruption(new ZeroRandom());
        Check(s.volcanoes.Count == 1 && s.volcanoes[0].cell == center, "Automatic eruption missed the charged hotspot");
        var step = typeof(PlanetSim).GetMethod("StepVolcanoes", Private);
        for (int i = 0; i < 400; i++) step.Invoke(s, null);
        Check(Solid(s) > crustBeforeEruption + 0.05d * s.G.Area[center], "Hotspot eruption did not add volcanic crust");
        Debug.Log($"HOTSPOT DRIFT cells={drift:F2}");
    }

    static void MagmaWithoutPlates()
    {
        var s = new PlanetSim(new PlanetGrid(16, 75f));
        s.tectonics = false;
        s.ApplyAge(4.5f);
        s.Generate(123);
        int center = s.G.CellIndex(0, 8, 8);
        float initialPlume = 0f;
        for (int c = 0; c < s.M; c++) initialPlume = Mathf.Max(initialPlume, s.plumeInf[c]);
        Check(initialPlume > 0f, "Hotspots were not initialized without plates");
        Array.Clear(s.plumeInf, 0, s.M);
        Array.Clear(s.magma, 0, s.M);
        s.plumeInf[center] = 1f;
        double geology, water, salt, before, after;
        s.MaterialIndices(out geology, out water, out salt, out before);
        for (int i = 0; i < 80; i++) s.StepTectonics();
        s.MaterialIndices(out geology, out water, out salt, out after);
        Check(s.magma[center] > 0.005f, "Hotspot magma did not recharge without plates");
        Check(Math.Abs(after - before) < Math.Abs(before) * 0.000002, "Independent magma changed geological volume");
    }

    static void DistributedEarthquakes()
    {
        var s = MakePlanet();
        int source = s.G.CellIndex(0, 0, 8);
        var pick = typeof(PlanetSim).GetMethod("PickQuakeEpicenter", Private);
        var random = new System.Random(123);
        int displaced = 0, otherFace = 0;
        for (int i = 0; i < 300; i++)
        {
            int c = (int)pick.Invoke(s, new object[] { source, random });
            float distance = Vector3.Angle(s.G.Dir[source], s.G.Dir[c]) * Mathf.Deg2Rad * s.G.Radius / s.G.CellSize;
            Check(distance <= 2.001f, "Earthquake epicenter was too far from the stressed zone");
            if (c != source) displaced++;
            if (c / (s.G.N * s.G.N) != source / (s.G.N * s.G.N)) otherFace++;
        }
        Check(displaced > 150 && otherFace > 0, "Earthquakes remained fixed on the fault or failed to cross faces");

        int neighbor = s.G.Nbr[source * 4 + 1];
        s.plate[neighbor] = 1;
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        Vector3 direction = (s.G.Dir[neighbor] - s.G.Dir[source]).normalized;
        Vector3 along = Vector3.Cross(s.G.Dir[source], direction).normalized;
        axes[0] = Vector3.Cross(s.G.Dir[source], along).normalized;
        axes[1] = -axes[0]; rates[0] = rates[1] = 0.03f;
        Check(s.PlateBoundary(source, neighbor, out float strength) == PlanetSim.BoundaryKind.Transform && strength > 0f,
            "Test did not construct a transform boundary");
        Array.Clear(s.stress, 0, s.M);
        typeof(PlanetSim).GetMethod("AccumulateStress", Private).Invoke(s, new object[] { 150f });
        Check(s.stress[source] > 0f, "Transform boundary did not accumulate seismic stress");
    }

    static void EarthquakeRelief()
    {
        var s = MakePlanet();
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 0f; s.conv[c] = 0.8f;
            s.rift[c] = 0f; s.magmaInflation[c] = 0f;
        }
        int center = s.G.CellIndex(0, 8, 8);
        double geology, water, salt, before, after;
        s.MaterialIndices(out geology, out water, out salt, out before);
        s.Quake(center, 7.5f, new System.Random(42));
        s.MaterialIndices(out geology, out water, out salt, out after);
        int raised = 0, lowered = 0;
        float largestStep = 0f;
        for (int c = 0; c < s.M; c++)
        {
            if (s.rock[c] > 20.001f) raised++;
            if (s.rock[c] < 19.999f) lowered++;
            for (int k = 0; k < 4; k++)
            {
                int n = s.G.Nbr[c * 4 + k];
                if (n >= 0) largestStep = Mathf.Max(largestStep, Mathf.Abs(s.rock[c] - s.rock[n]));
            }
        }
        // Le séisme soulève un bloc, affaisse l'autre et ouvre une cassure nette sur la trace de la faille
        // (voir Quake) : le relief doit rester large, et la marche la plus haute ne doit pas dépasser la
        // profondeur de cette cassure, soit 0,22 par point de magnitude au-dessus de 3,5.
        float deepestCrack = 0.22f * (7.5f - 3.5f);
        Check(raised > 20 && lowered > 20 && largestStep <= deepestCrack,
            "Earthquake relief is not broad, or its fault scarp is too sharp: " + raised + " " + lowered + " " + largestStep);
        Check(Math.Abs(after - before) < Math.Abs(before) * 0.000002, "Earthquake changed geological volume");
    }

    static void CollisionRelief()
    {
        var s = MakePlanet();
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = 0.0003f;
        axes[1] = -Vector3.up; rates[1] = rates[0];
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 1f; s.magmaInflation[c] = 0f;
            s.conv[c] = 0.8f; s.rift[c] = 0f;
        }
        for (int j = 0; j < s.G.N; j++)
            for (int i = 8; i < s.G.N; i++)
                s.plate[s.G.CellIndex(0, i, j)] = 1;
        double before = Solid(s);
        for (int i = 0; i < 2000; i++) Move(s, 0.5f);
        float leftPeak = s.Ground(s.G.CellIndex(0, 7, 8));
        float rightPeak = s.Ground(s.G.CellIndex(0, 8, 8));
        float leftValley = s.Ground(s.G.CellIndex(0, 4, 8));
        float rightValley = s.Ground(s.G.CellIndex(0, 11, 8));
        float leftShoulder = s.Ground(s.G.CellIndex(0, 6, 8));
        float rightShoulder = s.Ground(s.G.CellIndex(0, 9, 8));
        Debug.Log($"COLLISION RELIEF peaks={leftPeak:F2},{rightPeak:F2} valleys={leftValley:F2},{rightValley:F2} shoulders={leftShoulder:F2},{rightShoulder:F2}");
        Check(leftPeak > leftValley + 1f && rightPeak > rightValley + 1f, "Collision did not form ridges and lower valleys");
        Check(leftShoulder > leftValley + 1f && rightShoulder > rightValley + 1f, "Collision ridges are too narrow");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.00002, "Collision changed crust volume");
    }

    static void ValleyBetweenCollisions()
    {
        var s = MakePlanet();
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = 0f;
        axes[1] = Vector3.up; rates[1] = 0.0003f;
        axes[2] = -Vector3.up; rates[2] = 0.0003f;
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 20f; s.sed[c] = 1f; s.magmaInflation[c] = 0f;
            s.conv[c] = 0.8f; s.rift[c] = 0f;
        }
        for (int j = 0; j < s.G.N; j++)
            for (int i = 0; i < s.G.N; i++)
                s.plate[s.G.CellIndex(0, i, j)] = (byte)(i < 5 ? 1 : (i >= 11 ? 2 : 0));
        double before = Solid(s);
        for (int i = 0; i < 2000; i++) Move(s, 0.5f);
        float left = s.Ground(s.G.CellIndex(0, 4, 8));
        float valley = s.Ground(s.G.CellIndex(0, 8, 8));
        float right = s.Ground(s.G.CellIndex(0, 11, 8));
        Debug.Log($"COLLISION VALLEY left={left:F2} valley={valley:F2} right={right:F2}");
        Check(left > valley + 1f && right > valley + 1f, "Converging plates did not form a valley between ridges");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.00002, "Valley formation changed crust volume");
    }

    static void SubductionRelief(bool bothOcean)
    {
        var s = MakePlanet();
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = 0.0003f; axes[1] = -Vector3.up; rates[1] = rates[0];
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = -20f; s.sed[c] = 1f; s.magmaInflation[c] = 0f;
            s.conv[c] = 0.8f; s.rift[c] = 0f;
        }
        for (int j = 0; j < s.G.N; j++)
            for (int i = 8; i < s.G.N; i++)
            {
                int c = s.G.CellIndex(0, i, j);
                s.plate[c] = 1;
                if (!bothOcean) s.rock[c] = 20f;
            }
        int left = s.G.CellIndex(0, 7, 8), right = s.G.CellIndex(0, 8, 8);
        float leftBefore = s.Ground(left), rightBefore = s.Ground(right);
        double before = Solid(s);
        for (int i = 0; i < 2000; i++) Move(s, 0.5f);
        Check(s.Ground(left) > leftBefore + 1f && s.Ground(right) > rightBefore + 1f,
            "Subduction did not build ridges on both plates");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.00002, "Subduction relief changed crust volume");
    }

    static void PlateDrift()
    {
        var s = new PlanetSim(new PlanetGrid(16, 75f));
        s.ApplyAge(4.5f);
        s.Generate(123);
        var seeds = (Vector3[])typeof(PlanetSim).GetField("seeds", Private).GetValue(s);
        var original = (Vector3[])seeds.Clone();
        var originalPlate = (byte[])s.plate.Clone();
        for (int tick = 0; tick < 20 * PlanetSim.TicksPerYear; tick++)
        {
            s.StepTectonics();
            s.tick++;
        }
        float distance = 0f;
        for (int i = 0; i < seeds.Length; i++)
            distance += Vector3.Angle(original[i], seeds[i]) * Mathf.Deg2Rad / (Mathf.PI * 0.5f / s.G.N);
        Check(distance / seeds.Length > 0.1f, "Plate boundaries barely moved over twenty simulated years");
        int changed = 0;
        for (int c = 0; c < s.M; c++) if (s.plate[c] != originalPlate[c]) changed++;
        Check(changed > 0, "Plate assignments did not change as the plates drifted");
    }

    static void VisibleDriftOver170Years()
    {
        var s = new PlanetSim(new PlanetGrid(32, 600f));
        s.ApplyAge(4.5f);
        s.Generate(123);
        var axes = (Vector3[])typeof(PlanetSim).GetField("axis", Private).GetValue(s);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        var seeds = (Vector3[])typeof(PlanetSim).GetField("seeds", Private).GetValue(s);
        var initialSeeds = (Vector3[])seeds.Clone();
        int center = -1;
        float fastest = 0f;
        for (int c = 0; c < s.M; c++)
        {
            bool interior = true;
            for (int k = 0; k < 4 && interior; k++)
            {
                int n = c;
                for (int step = 0; step < 4; step++)
                {
                    n = s.G.Nbr[n * 4 + k];
                    if (n < 0 || s.plate[n] != s.plate[c]) { interior = false; break; }
                }
            }
            if (!interior) continue;
            int p = s.plate[c];
            float speed = Vector3.Cross(axes[p] * rates[p], s.G.Dir[c]).magnitude;
            if (speed > fastest) { fastest = speed; center = c; }
        }
        Check(center >= 0, "No plate interior found for visible drift test");
        Vector3 start = s.G.Dir[center];
        float cellAngle = Mathf.PI * 0.5f / s.G.N;
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 30f; s.magmaInflation[c] = 0f;
            float distance = Vector3.Angle(start, s.G.Dir[c]) * Mathf.Deg2Rad / cellAngle;
            s.sed[c] = distance < 3f && s.plate[c] == s.plate[center] ? 4f * Mathf.Exp(-distance * distance / 4f) : 0f;
        }
        double before = Sediment(s);
        for (int tick = 0; tick < 170 * PlanetSim.TicksPerYear; tick++)
        {
            s.StepTectonics();
            s.tick++;
        }
        Vector3 centroid = Vector3.zero;
        for (int c = 0; c < s.M; c++) centroid += s.G.Dir[c] * (s.sed[c] * s.G.Area[c]);
        float cells = Vector3.Angle(start, centroid) * Mathf.Deg2Rad / cellAngle;
        float averagePlateDrift = 0f;
        for (int p = 0; p < seeds.Length; p++)
            averagePlateDrift += Vector3.Angle(initialSeeds[p], seeds[p]) * Mathf.Deg2Rad / cellAngle / seeds.Length;
        Debug.Log($"VISIBLE PLATE DRIFT years=170 cells={cells:F2} meanPlate={averagePlateDrift:F2}");
        Check(cells > 5f, "Plate terrain did not move visibly in 170 years");
        Check(averagePlateDrift > 3f, "Plate boundaries did not move sufficiently in 170 years");
        Check(Math.Abs(Sediment(s) - before) < before * 0.00005, "Visible plate drift changed sediment volume: " + before + " -> " + Sediment(s));
    }

    static void CollisionSpurs()
    {
        var s = new PlanetSim(new PlanetGrid(64, 600f));
        s.ApplyAge(4.5f);
        s.Generate(123);
        Array.Clear(s.plate, 0, s.M);
        Array.Clear(s.conv, 0, s.M);
        Array.Clear(s.rift, 0, s.M);
        var rates = (float[])typeof(PlanetSim).GetField("rate", Private).GetValue(s);
        rates[0] = 0f;
        s.plateSpeed = 0.016f;
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 30f; s.sed[c] = 0f; s.magmaInflation[c] = 0f;
        }
        for (int j = 0; j < s.G.N; j++)
            for (int i = 0; i < s.G.N; i++)
                s.conv[s.G.CellIndex(0, i, j)] = 0.8f * Mathf.Exp(-Mathf.Abs(i - 32) * 0.45f);
        double before = Solid(s);
        for (int i = 0; i < 300; i++) Move(s, 0.5f);
        float[] near = new float[40], far = new float[40];
        float nearMean = 0f, farMean = 0f;
        for (int j = 12; j < 52; j++)
        {
            near[j - 12] = s.Ground(s.G.CellIndex(0, 30, j));
            far[j - 12] = s.Ground(s.G.CellIndex(0, 28, j));
            nearMean += near[j - 12] / 40f;
            farMean += far[j - 12] / 40f;
        }
        float varianceNear = 0f, varianceFar = 0f, covariance = 0f;
        float minimum = float.MaxValue, maximum = float.MinValue;
        for (int i = 0; i < near.Length; i++)
        {
            float a = near[i] - nearMean, b = far[i] - farMean;
            varianceNear += a * a; varianceFar += b * b; covariance += a * b;
            minimum = Mathf.Min(minimum, near[i]); maximum = Mathf.Max(maximum, near[i]);
        }
        float correlation = covariance / Mathf.Sqrt(varianceNear * varianceFar);
        Debug.Log($"COLLISION SPURS range={maximum - minimum:F2} correlation={correlation:F2}");
        Check(maximum - minimum > 2f && correlation > 0.3f,
            "Collision did not form irregular ridges extending into the plate");
        Check(Math.Abs(Solid(s) - before) < Math.Abs(before) * 0.00002, "Collision spurs changed crust volume");
    }

    public static void Run()
    {
        FlatRotation();
        MaterialTransport();
        FaceAndPlateBoundary();
        OldMountainRelaxation();
        ExtremeCliffRelaxation();
        MountainDrift();
        PlateLifecycle();
        PlateSaveCompatibility();
        MantleTransport();
        CompressionRelief();
        PlateShortening();
        MountainForelandCompression();
        ComputedCollisionForeland();
        VolcanicFootprint();
        VolcanicFaceBoundary();
        VolcanicFlanks();
        HotspotAndPlateDrift();
        MagmaWithoutPlates();
        DistributedEarthquakes();
        EarthquakeRelief();
        CollisionRelief();
        ValleyBetweenCollisions();
        SubductionRelief(true);
        SubductionRelief(false);
        PlateDrift();
        VisibleDriftOver170Years();
        CollisionSpurs();
        LongTermSeaLevel();
        Debug.Log("TECTONIC VALIDATION PASSED");
    }
}
