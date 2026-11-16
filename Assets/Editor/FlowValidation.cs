using System;
using System.IO;
using System.Reflection;
using LivingPlanet;
using UnityEngine;

// Exécutable en batch Unity avec -executeMethod FlowValidation.Run.
public static class FlowValidation
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    sealed class CalvingRandom : System.Random
    {
        readonly int cell;
        public CalvingRandom(int cell) { this.cell = cell; }
        public override int Next(int maxValue) { return cell; }
        public override double NextDouble() { return 0d; }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static double Solid(PlanetSim s)
    {
        double geology, water, salt, weighted;
        s.MaterialIndices(out geology, out water, out salt, out weighted);
        return weighted;
    }

    static double Weighted(PlanetSim s, params float[][] fields)
    {
        double total = 0;
        for (int c = 0; c < s.M; c++)
            for (int i = 0; i < fields.Length; i++)
                total += fields[i][c] * (double)s.G.Area[c];
        return total;
    }

    static void Near(double before, double after, string message)
    {
        Check(Math.Abs(before - after) <= Math.Max(0.00001, Math.Abs(before) * 0.00001), message + ": " + before + " -> " + after);
    }

    static void AreaWeightedTransfers()
    {
        var g = new PlanetGrid(12, 56.25f);
        int source = g.CellIndex(0, 0, 2);

        var hydro = new PlanetSim(g);
        for (int c = 0; c < hydro.M; c++) hydro.rock[c] = 1f;
        hydro.rock[source] = 10f;
        hydro.water[source] = 4f;
        hydro.susp[source] = 2f;
        hydro.waterState.salt[source] = 0.1f;
        double water = Weighted(hydro, hydro.water, hydro.snow);
        double sediment = Weighted(hydro, hydro.sed, hydro.susp, hydro.rock);
        double salt = Weighted(hydro, hydro.waterState.salt, hydro.waterState.deposit);
        hydro.StepHydro();
        Near(water, Weighted(hydro, hydro.water, hydro.snow, hydro.evap), "Area-weighted surface water");
        Near(sediment, Weighted(hydro, hydro.sed, hydro.susp, hydro.rock), "Area-weighted hydro sediment");
        Near(salt, Weighted(hydro, hydro.waterState.salt, hydro.waterState.deposit), "Area-weighted salt");

        var groundwater = new PlanetSim(g);
        for (int c = 0; c < groundwater.M; c++) groundwater.rock[c] = 1f;
        groundwater.rock[source] = 10f;
        groundwater.gw[source] = 2f;
        water = Weighted(groundwater, groundwater.water, groundwater.gw);
        groundwater.StepGroundwater();
        Near(water, Weighted(groundwater, groundwater.water, groundwater.gw), "Area-weighted groundwater");

        var geology = new PlanetSim(g);
        geology.tectonics = false;
        geology.uplift = 0f;
        geology.weathering = 0f;
        for (int c = 0; c < geology.M; c++) geology.rock[c] = 1f;
        geology.rock[source] = 100f;
        geology.sed[source] = 1f;
        sediment = Weighted(geology, geology.rock, geology.sed);
        geology.StepGeology();
        Near(sediment, Weighted(geology, geology.rock, geology.sed), "Area-weighted slope transport");

        var wind = new PlanetSim(g);
        for (int c = 0; c < wind.M; c++) wind.rock[c] = 1f;
        wind.sed[source] = 2f;
        wind.mix[source * 4 + PlanetSim.Sand] = 1f;
        wind.wind[source] = g.Tan[source * 4 + 1];
        sediment = Weighted(wind, wind.sed);
        typeof(PlanetSim).GetMethod("StepWindErosion", Private).Invoke(wind, null);
        Check(wind.sed[source] < 2f, "Wind did not erode source");
        Near(sediment, Weighted(wind, wind.sed), "Area-weighted wind sediment");

        var slide = new PlanetSim(g);
        for (int c = 0; c < slide.M; c++) slide.rock[c] = 1f;
        slide.rock[source] = 100f;
        slide.sed[source] = 2f;
        sediment = Weighted(slide, slide.rock, slide.sed);
        slide.Landslide(source, 2f);
        Near(sediment, Weighted(slide, slide.rock, slide.sed), "Area-weighted landslide");

        var retreat = new PlanetSim(g);
        int sea = g.Nbr[source * 4 + 1];
        for (int c = 0; c < retreat.M; c++) retreat.rock[c] = 10f;
        retreat.rock[source] = 1f; retreat.water[source] = 3f; retreat.susp[source] = 1f;
        retreat.rock[sea] = -1f; retreat.water[sea] = 1f;
        sediment = Weighted(retreat, retreat.rock, retreat.sed, retreat.susp);
        retreat.StepHydro();
        Check(retreat.water[sea] > 1f && retreat.susp[sea] > 0f, "Retreating water did not carry sediment into the sea");
        Near(sediment, Weighted(retreat, retreat.rock, retreat.sed, retreat.susp), "Area-weighted retreat sediment");

        var volcano = new PlanetSim(g);
        volcano.tectonics = false;
        volcano.uplift = 0f;
        volcano.weathering = 0f;
        volcano.kThermal = 0f;
        for (int c = 0; c < volcano.M; c++) volcano.rock[c] = 20f;
        volcano.rock[source] = 30f;
        volcano.magma[source] = 1f;
        double beforeVolcano = Solid(volcano);
        volcano.StartVolcano(source, 1f);
        for (int i = 0; i < 20; i++) volcano.StepGeology();
        Near(beforeVolcano, Solid(volcano), "Area-weighted volcanic deposits and lava");

        var impact = new PlanetSim(g);
        for (int c = 0; c < impact.M; c++) impact.rock[c] = 20f;
        sediment = Weighted(impact, impact.rock, impact.sed);
        var asteroid = new Asteroid { size = 1f };
        typeof(PlanetSim).GetMethod("Impact", Private).Invoke(impact, new object[] { source, asteroid, new System.Random(123) });
        Near(sediment + 0.1 * g.Area[source], Weighted(impact, impact.rock, impact.sed), "Area-weighted impact excavation");

        var air = new PlanetSim(g);
        air.tick = 1;
        for (int c = 0; c < air.M; c++)
        {
            air.rock[c] = 1f;
            air.temp[c] = 10f;
            air.hum[c] = 0.1f + 0.001f * (c % 11);
            air.wind[c] = g.Tan[c * 4 + 1] * 0.8f;
        }

        water = Weighted(air, air.hum) * air.rainK;
        air.StepClimate();
        Near(water, Weighted(air, air.hum, air.rainNow) * air.rainK, "Area-weighted atmospheric water");
    }

    static void TsunamiRunup()
    {
        var g = new PlanetGrid(32, 150f);
        var s = new PlanetSim(g);
        for (int c = 0; c < s.M; c++) s.rock[c] = 30f;
        for (int j = 0; j < g.N; j++)
            for (int i = 0; i < g.N; i++)
            {
                int c = g.CellIndex(0, i, j);
                if (i < 16)
                {
                    s.rock[c] = -10f; s.water[c] = 10f;
                    s.waterState.salt[c] = 10f * PlanetSim.OceanSalinity;
                }
                else if (i != 19)
                {
                    s.rock[c] = 0.1f; s.sed[c] = 0.2f;
                    s.mix[c * 4 + PlanetSim.Sand] = 1f;
                }
            }
        int source = g.CellIndex(0, 10, 16);
        int coast = g.CellIndex(0, 16, 16);
        int behindWall = g.CellIndex(0, 20, 16);
        int isolated = g.CellIndex(1, 16, 16);
        s.water[isolated] = 2f; s.susp[isolated] = 0.4f;
        double water = Weighted(s, s.water);
        double salt = Weighted(s, s.waterState.salt);
        double sediment = Weighted(s, s.rock, s.sed, s.susp);
        double movableSediment = Weighted(s, s.sed, s.susp);
        s.SpawnTsunami(source, 7f);
        Check(s.tsunamis.Count == 1 && s.water[source] > 10f, "Tsunami did not lift the water at its source");
        Check(s.TsunamiElevation(source) > 0f && s.TsunamiElevation(isolated) == 0f,
            "Tsunami visualization did not follow the simulated water elevation");
        Near(water, Weighted(s, s.water), "Tsunami source water");
        Near(salt, Weighted(s, s.waterState.salt), "Tsunami source salt");
        float peak = 0f, lateMinimum = float.MaxValue, minSoil = s.sed[coast], maxSuspension = 0f;
        bool sedimentRetreated = false;
        for (int i = 0; i < 500; i++)
        {
            s.StepTsunamis(0.1f);
            peak = Mathf.Max(peak, s.water[coast]);
            minSoil = Mathf.Min(minSoil, s.sed[coast]);
            maxSuspension = Mathf.Max(maxSuspension, s.susp[coast]);
            int seaEdge = coast * 4;
            if (s.water[coast] < peak * 0.9f && s.susp[coast] > 0f &&
                Vector3.Dot(s.TsunamiFlow(coast), g.Tan[seaEdge]) > 0.01f) sedimentRetreated = true;
            if (i > 300) lateMinimum = Mathf.Min(lateMinimum, s.water[coast]);
        }
        Check(peak > 1f, "Tsunami did not produce a visible coastal runup: " + peak);
        Check(lateMinimum < peak * 0.8f, "Tsunami water did not retreat: " + peak + " -> " + lateMinimum);
        Check(s.water[behindWall] < 0.001f, "Tsunami crossed an impermeable ridge");
        Check(s.water[isolated] == 2f && s.susp[isolated] == 0.4f,
            "Tsunami disturbed an isolated lake");
        Check(minSoil < 0.2f && maxSuspension > 0f,
            "Tsunami did not erode and carry coastal sediment: " + minSoil + ", " + maxSuspension);
        Check(sedimentRetreated, "Retreating tsunami did not carry sediment toward the sea");
        Near(water, Weighted(s, s.water), "Area-weighted tsunami water");
        Near(salt, Weighted(s, s.waterState.salt), "Area-weighted tsunami salt");
        double sedimentError = Math.Abs(sediment - Weighted(s, s.rock, s.sed, s.susp));
        Check(sedimentError < Math.Max(0.001, movableSediment * 0.0001),
            "Tsunami sediment balance error: " + sedimentError);
        Debug.Log("Tsunami coastal peak " + peak + ", retreat " + lateMinimum);
        TsunamiSaveContinuation(s);

        var separated = new PlanetSim(g);
        for (int c = 0; c < separated.M; c++) separated.rock[c] = 30f;
        for (int j = 10; j <= 22; j++)
            for (int i = 3; i <= 10; i++)
            {
                int c = g.CellIndex(0, i, j);
                separated.rock[c] = -10f; separated.water[c] = 10f;
            }
        int isolatedSea = g.CellIndex(0, 12, 16);
        separated.rock[isolatedSea] = -10f; separated.water[isolatedSea] = 10f;
        Check(separated.SpawnTsunami(source, 7f), "Tsunami failed to start in a connected sea");
        Check(separated.water[isolatedSea] == 10f, "Tsunami source transferred water across land");
    }

    static void TsunamiSaveContinuation(PlanetSim s)
    {
        var save = typeof(PlanetSim).Assembly.GetType("LivingPlanet.PlanetSave");
        var write = save.GetMethod("WriteObject");
        var read = save.GetMethod("ReadObject");
        var copy = new PlanetSim(s.G);
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                write.Invoke(null, new object[] { writer, s });
                write.Invoke(null, new object[] { writer, s.waterState });
            }
            stream.Position = 0;
            using (var reader = new BinaryReader(stream))
            {
                read.Invoke(null, new object[] { reader, copy, s.M * 8 + 1024, null, null });
                read.Invoke(null, new object[] { reader, copy.waterState, s.M * 8 + 1024, null, null });
            }
        }
        Check(copy.tsunamis.Count == s.tsunamis.Count, "Active tsunami was not restored");
        for (int i = 0; i < 10; i++) { s.StepTsunamis(0.1f); copy.StepTsunamis(0.1f); }
        for (int c = 0; c < s.M; c++)
            Check(Mathf.Abs(s.water[c] - copy.water[c]) < 0.00001f &&
                Mathf.Abs(s.susp[c] - copy.susp[c]) < 0.00001f &&
                Mathf.Abs(s.waterState.salt[c] - copy.waterState.salt[c]) < 0.00001f,
                "Active tsunami diverged after saving at cell " + c);

        string[] missing = { "tsunamiBase", "tsunamiMomentum", "tsunamiActive", "tsunamiAccum" };
        var fields = (FieldInfo[])save.GetMethod("Fields", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { typeof(PlanetSim) });
        var writeValue = save.GetMethod("WriteValue", BindingFlags.Static | BindingFlags.NonPublic);
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(fields.Length - missing.Length);
                foreach (var field in fields)
                {
                    if (Array.IndexOf(missing, field.Name) >= 0) continue;
                    writer.Write(field.Name);
                    writeValue.Invoke(null, new object[] { writer, field.FieldType, field.GetValue(s) });
                }
            }
            stream.Position = 0;
            using (var reader = new BinaryReader(stream))
                read.Invoke(null, new object[] { reader, new PlanetSim(s.G), s.M * 8 + 1024, missing, null });
        }
    }

    static void TsunamiHydroStability()
    {
        var g = new PlanetGrid(64, 600f);
        var s = new PlanetSim(g);
        s.ApplyAge(4.5f);
        s.Generate(920332882);
        var random = new System.Random(123);
        for (int i = 0; i < 7 * PlanetSim.TicksPerYear; i++)
        {
            s.StepClimate(); s.StepOcean(1000f); s.StepHydro(); s.StepGroundwater();
            s.StepIce(random); s.StepGeology(); s.StepLithification();
            s.StepTectonics(1000f); s.StepCoastline(); s.tick++;
        }
        s.rainK = s.snowK = s.evapK = 0f;
        int source = -1;
        for (int c = 0; c < s.M; c++)
        {
            s.temp[c] = 0f;
            if (source < 0 && s.IsOcean(c) && s.water[c] > 30f) source = c;
        }
        double water = Weighted(s, s.water, s.snow);
        double salt = Weighted(s, s.waterState.salt, s.waterState.deposit);
        double sediment = Weighted(s, s.rock, s.sed, s.susp);
        Check(source >= 0 && s.SpawnTsunami(source, 7f), "Could not start stability-test tsunami");
        var excursions = new System.Collections.Generic.List<float>();
        for (int i = 0; i < 400; i++)
        {
            s.StepHydro(); s.StepTsunamis(0.23f); s.StepCoastline();
            if (i % 25 != 0) continue;
            excursions.Clear();
            for (int c = 0; c < s.M; c++)
                if (s.IsOcean(c)) excursions.Add(Mathf.Abs(s.Ground(c) + s.water[c] - s.SeaLevel));
            excursions.Sort();
            float p99 = excursions[(int)(excursions.Count * 0.99f)];
            float maximum = excursions[excursions.Count - 1];
            Check(p99 < 35f && maximum < 80f,
                "Tsunami/hydrology instability at tick " + i + ": p99=" + p99 + ", max=" + maximum);
        }
        Near(water, Weighted(s, s.water, s.snow), "Coupled tsunami water");
        Near(salt, Weighted(s, s.waterState.salt, s.waterState.deposit), "Coupled tsunami salt");
        Near(sediment, Weighted(s, s.rock, s.sed, s.susp), "Coupled tsunami sediment");
    }

    static void CoastlineWaterLevel()
    {
        var s = new PlanetSim(new PlanetGrid(12, 56.25f));
        for (int c = 0; c < s.M; c++) { s.rock[c] = -10f; s.water[c] = 12f; }
        int coast = s.G.CellIndex(0, 5, 5);
        s.rock[coast] = 1f; s.water[coast] = 0f;
        double water = Weighted(s, s.water);
        double salt = Weighted(s, s.waterState.salt);
        s.StepCoastline();
        Check(s.SeaLevel > 1.9f && !s.IsOcean(coast), "Sea level or dry coastal classification is incorrect");
        s.water[coast] = 1f;
        Check(s.IsOcean(coast), "Flooded coastal cell did not become ocean");
        s.water[coast] = 0f;
        s.StepCoastline();
        Near(water, Weighted(s, s.water), "Coastline water");
        Near(salt, Weighted(s, s.waterState.salt), "Coastline salt");
        for (int c = 0; c < s.M; c++) if (c != coast) s.water[c] = 8f;
        s.StepCoastline();
        Check(s.SeaLevel < -1.9f && !s.IsOcean(coast), "Falling sea level did not expose the coast");
    }

    static void SpeedIndependentPhysics()
    {
        var g = new PlanetGrid(12, 56.25f);
        var normal = new PlanetSim(g);
        var fast = new PlanetSim(g);
        normal.ApplyAge(4.5f); fast.ApplyAge(4.5f);
        normal.Generate(123); fast.Generate(123);
        for (int tick = 0; tick < 80; tick++)
        {
            normal.tick = fast.tick = tick;
            normal.StepOcean(1f); fast.StepOcean(1000f);
            normal.StepTectonics(1f); fast.StepTectonics(1000f);
        }

        for (int c = 0; c < normal.M; c++)
        {
            Check(normal.temp[c] == fast.temp[c] && normal.rock[c] == fast.rock[c] &&
                normal.sed[c] == fast.sed[c] && normal.current[c] == fast.current[c],
                "Simulation changed with selected speed at cell " + c);
        }
    }

    static void EvaporationBalance()
    {
        var g = new PlanetGrid(12, 56.25f);
        int c = g.CellIndex(0, 5, 5);
        var wetAir = new PlanetSim(g);
        for (int i = 0; i < wetAir.M; i++) wetAir.rock[i] = 1f;
        wetAir.kFlow = 0f;
        wetAir.water[c] = 10f; wetAir.gw[c] = 1f; wetAir.temp[c] = 25f;
        wetAir.hum[c] = PlanetSim.Sat(wetAir.temp[c]);
        wetAir.StepHydro(); wetAir.StepGroundwater();
        Check(wetAir.evap[c] == 0f, "Saturated air continued to evaporate water");

        var dryAir = new PlanetSim(g);
        for (int i = 0; i < dryAir.M; i++) dryAir.rock[i] = 1f;
        dryAir.kFlow = 0f;
        dryAir.water[c] = 10f; dryAir.gw[c] = 1f; dryAir.temp[c] = 25f;
        double before = Weighted(dryAir, dryAir.water, dryAir.gw);
        dryAir.StepHydro(); dryAir.StepGroundwater();
        Check(dryAir.evap[c] > 0f && dryAir.evap[c] < 0.02f, "Standing water evaporated too rapidly");
        Near(before, Weighted(dryAir, dryAir.water, dryAir.gw, dryAir.evap), "Surface and groundwater evaporation");

        var sea = new PlanetSim(g);
        for (int i = 0; i < sea.M; i++) sea.rock[i] = 1f;
        sea.rock[c] = -10f; sea.water[c] = 10f; sea.temp[c] = 25f; sea.kFlow = 0f;
        sea.StepHydro();
        Check(sea.evap[c] == 0f, "Ocean evaporated twice");
        sea.tick = 1; sea.advection = 0f;
        float water = sea.water[c];
        sea.StepClimate();
        Check(sea.water[c] < water, "Open ocean did not evaporate into dry air");
        sea.water[c] = water; sea.hum[c] = 0f; sea.seaIce[c] = 6f;
        sea.StepClimate();
        Check(sea.water[c] == water, "Sea ice failed to suppress ocean evaporation");
    }

    static void WindTrapping()
    {
        var g = new PlanetGrid(12, 56.25f);
        var s = new PlanetSim(g);
        int source = g.CellIndex(0, 3, 5);
        int trap = g.Nbr[source * 4 + 1];
        int beyond = g.Nbr[trap * 4 + 1];
        for (int c = 0; c < s.M; c++) s.wind[c] = g.Tan[c * 4 + 1];
        s.sed[source] = 2f; s.mix[source * 4 + PlanetSim.Sand] = 1f;
        s.cover[trap] = 0.9f;
        do
        {
            s.tick++;
        } while (Mathf.Repeat(Mathf.Sin((source + 1) * 31.7f + s.tick * 17.13f) * 15731.743f, 1f) < 0.2f);
        double before = Weighted(s, s.sed);
        typeof(PlanetSim).GetMethod("StepWindErosion", Private).Invoke(s, null);
        Check(s.sed[trap] > 0f && s.sed[beyond] == 0f, "Vegetation did not trap windblown sediment");
        Near(before, Weighted(s, s.sed), "Vegetation-trapped sediment");
        float remaining = s.sed[source];
        s.water[source] = 0.2f;
        typeof(PlanetSim).GetMethod("StepWindErosion", Private).Invoke(s, null);
        Check(s.sed[source] == remaining, "Wind eroded submerged soil");
    }

    static void OceanDepth()
    {
        var grid = new PlanetGrid(32, 150f);
        foreach (int seed in new[] { 123, 357, 987 })
        {
            var s = new PlanetSim(grid);
            s.ApplyAge(4.5f);
            s.Generate(seed);
            double area = 0d, depth = 0d;
            for (int c = 0; c < s.M; c++)
                if (s.Ground(c) < s.SeaLevel)
                {
                    area += grid.Area[c];
                    depth += (s.SeaLevel - s.Ground(c)) * grid.Area[c];
                }
            Check(area > 0d && depth / area > 45d, "Generated oceans are too shallow for seed " + seed);
        }
    }

    static void IcebergVolume()
    {
        var g = new PlanetGrid(12, 56.25f);
        var s = new PlanetSim(g);
        int source = g.CellIndex(0, 0, 2);
        int sea = g.Nbr[source * 4 + 1];
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 1f;
            s.wind[c] = Vector3.ProjectOnPlane(g.Dir[sea] - g.Dir[c], g.Dir[c]).normalized * 40f;
        }
        s.rock[source] = 100f; s.glacier[source] = 25f;
        s.rock[sea] = -10f; s.water[sea] = 10f;
        double geo, water, salt, geoArea, before, saltArea;
        s.MaterialIndices(out geo, out water, out salt, out geoArea, out before, out saltArea);
        s.StepIce(new CalvingRandom(source));
        Check(s.icebergs.Count == 1, "Glacier did not calve");
        for (int i = 0; i < 20; i++) s.StepIce(new System.Random(100 + i));
        Check(s.icebergs.Count > 0 && g.CellFromDir(s.icebergs[0].dir) != source, "Iceberg did not drift");
        double after;
        s.MaterialIndices(out geo, out water, out salt, out geoArea, out after, out saltArea);
        Near(before, after, "Area-weighted iceberg water");

        var old = new PlanetSim(g);
        old.icebergs.Add(new Iceberg { dir = g.Dir[source], size = 0.5f });
        typeof(PlanetSim).GetMethod("MigrateIcebergVolumes", Private).Invoke(old, null);
        double meanArea = 4d * Math.PI * g.Radius * g.Radius / old.M;
        Near(0.5 * g.Area[source] / meanArea, old.icebergs[0].size, "Legacy iceberg volume");
    }

    static PlanetWorld MakeWorld(int speed)
    {
        var go = new GameObject("Clock validation " + speed);
        var world = go.AddComponent<PlanetWorld>();
        world.Grid = new PlanetGrid(12, 56.25f);
        world.Sim = new PlanetSim(world.Grid);
        world.Sim.ApplyAge(4.5f);
        world.Sim.Generate(123);
        world.Eco = new Ecosystem(world.Sim, 123);
        world.Eco.PlantInitial();
        world.Fauna = new Fauna(world);
        world.Fauna.Init();
        world.Sim.onDisturb = world.Eco.Disturb;
        world.Sim.onLightning = c => { if (world.Sim.moist[c] < 0.6f) world.Eco.Ignite(c); };
        world.Sim.onBlastPlant = world.Eco.Blast;
        world.Sim.onBlastFauna = world.Fauna.KillNear;
        world.timeScale = speed;
        var random = typeof(PlanetWorld).Assembly.GetType("LivingPlanet.PlanetRandom");
        typeof(PlanetWorld).GetField("rnd", Private).SetValue(world, Activator.CreateInstance(random, new object[] { 123 }));
        return world;
    }

    static void WorldClock()
    {
        var normal = MakeWorld(1);
        var fast = MakeWorld(1000);
        try
        {
            var step = typeof(PlanetWorld).GetMethod("StepOnce", Private);
            for (int i = 0; i < 30; i++) { step.Invoke(normal, null); step.Invoke(fast, null); }
            Check(normal.Sim.tick == fast.Sim.tick, "Simulation clocks differ");
            for (int c = 0; c < normal.Sim.M; c++)
                Check(normal.Sim.temp[c] == fast.Sim.temp[c] && normal.Sim.water[c] == fast.Sim.water[c] &&
                    normal.Sim.rock[c] == fast.Sim.rock[c] && normal.Sim.animalBreath[c] == fast.Sim.animalBreath[c],
                    "World changed with selected speed at cell " + c);
            for (int sp = 0; sp < Fauna.Count; sp++)
            {
                var a = normal.Fauna.pops[sp]; var b = fast.Fauna.pops[sp];
                Check(a.n == b.n, "Fauna population changed with selected speed");
                for (int i = 0; i < a.n; i++)
                    Check(a.a[i].dir == b.a[i].dir && a.a[i].energy == b.a[i].energy && a.a[i].age == b.a[i].age,
                        "Fauna state changed with selected speed");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(normal.gameObject);
            UnityEngine.Object.DestroyImmediate(fast.gameObject);
        }
    }

    static void LocalPredatorHabitat()
    {
        var world = MakeWorld(1);
        try
        {
            var fauna = world.Fauna;
            var rabbits = fauna.pops[Fauna.Rabbit];
            fauna.pops[Fauna.Deer].n = 0;
            rabbits.n = 1;
            int near = world.Grid.CellIndex(0, 5, 5);
            int far = world.Grid.CellIndex(1, 5, 5);
            rabbits.a[0] = new Animal { dir = world.Grid.Dir[near], cell = near };
            fauna.F.carrion[near] = fauna.F.carrion[far] = 0f;
            var habitat = typeof(Fauna).GetMethod("Habitat", Private);
            Check((bool)habitat.Invoke(fauna, new object[] { Fauna.Wolf, near }), "Nearby prey did not support predators");
            Check(!(bool)habitat.Invoke(fauna, new object[] { Fauna.Wolf, far }), "Distant prey supported local predators");
            rabbits.a[0].dead = true;
            Check(!(bool)habitat.Invoke(fauna, new object[] { Fauna.Wolf, near }), "Dead prey supported local predators");
            fauna.pops[Fauna.Bird].n = 0;
            var fish = fauna.pops[Fauna.Fish];
            fish.n = 1;
            fish.a[0] = new Animal { dir = world.Grid.Dir[near], cell = near };
            world.Sim.SeaLevel = 20f;
            world.Sim.rock[near] = 15f; world.Sim.sed[near] = 0f;
            Check((bool)habitat.Invoke(fauna, new object[] { Fauna.Raptor, near }), "Shallow fish should support raptors");
            world.Sim.rock[near] = 10f;
            Check(!(bool)habitat.Invoke(fauna, new object[] { Fauna.Raptor, near }), "Deep fish supported raptors");
        }
        finally { UnityEngine.Object.DestroyImmediate(world.gameObject); }
    }

    static void SlopeSoilComposition()
    {
        var g = new PlanetGrid(12, 56.25f);
        var s = new PlanetSim(g);
        s.tectonics = false; s.uplift = 0f; s.weathering = 0f; s.kThermal = 0.001f;
        int source = g.CellIndex(0, 0, 2);
        int middle = g.Nbr[source * 4 + 1];
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = 1f; s.sed[c] = 10f;
            s.mix[c * 4 + PlanetSim.Dirt] = 1f;
        }
        s.rock[source] = 100f; s.rock[middle] = 50f;
        s.mix[source * 4 + PlanetSim.Dirt] = 0f;
        s.mix[source * 4 + PlanetSim.Sand] = 1f;
        s.mix[middle * 4 + PlanetSim.Dirt] = 0f;
        s.mix[middle * 4 + PlanetSim.Clay] = 1f;
        double[] before = new double[4];
        for (int c = 0; c < s.M; c++)
            for (int k = 0; k < 4; k++)
                before[k] += s.sed[c] * (double)s.mix[c * 4 + k] * g.Area[c];
        s.StepGeology();
        Check(s.mix[middle * 4 + PlanetSim.Sand] > 0f, "Incoming soil was not mixed into a cell with outflow");
        for (int k = 0; k < 4; k++)
        {
            double after = 0d;
            for (int c = 0; c < s.M; c++) after += s.sed[c] * (double)s.mix[c * 4 + k] * g.Area[c];
            Near(before[k], after, "Area-weighted soil component " + k);
        }
    }

    static void MantleRockTransfers()
    {
        var s = new PlanetSim(new PlanetGrid(12, 56.25f));
        s.tectonics = true; s.kThermal = 0f; s.weathering = 0f;
        // StepGeology en mode tectonique lit les tampons alloués par InitPlates (rockN, orogenyHeightVariance...).
        typeof(PlanetSim).GetMethod("InitPlates", Private).Invoke(s, new object[] { new System.Random(1) });
        s.conv = new float[s.M]; s.rift = new float[s.M];
        int c = s.G.CellIndex(0, 5, 5);
        s.rock[c] = 20f; s.rift[c] = 1f; s.riftSink = 0.00008f;
        double before = (double)s.rock[c] + s.mantleSolid[c] + s.mantleResidual[c];
        for (int i = 0; i < 1000; i++) s.StepGeology();
        double after = (double)s.rock[c] + s.mantleSolid[c] + s.mantleResidual[c];
        Check(s.rock[c] < 19.95f, "Rift failed to lower the crust");
        Check(Math.Abs(before - after) < 0.000001, "Rift transfers lost material: " + before + " -> " + after);
        s.rift[c] = 0f; s.conv[c] = 0.7f; s.tect[c] = 0.5f;
        float rockBefore = s.rock[c];
        for (int i = 0; i < 1000; i++) s.StepGeology();
        after = (double)s.rock[c] + s.mantleSolid[c] + s.mantleResidual[c];
        Check(s.rock[c] > rockBefore, "Convergence failed to uplift the crust");
        Check(Math.Abs(before - after) < 0.000001, "Convergence transfers lost material: " + before + " -> " + after);
    }

    static void FruitResources()
    {
        var world = MakeWorld(1);
        try
        {
            int c = world.Grid.CellIndex(0, 5, 5);
            world.Eco.cellPlant[c] = world.Eco.plants.Count;
            world.Eco.plants.Add(new Plant { cell = c, type = Ecosystem.Berry, size = 1f, health = 1f });
            var fields = world.Fauna.F;
            fields.insects[c] = 0f;
            fields.fruit[c] = 0f;
            var forage = typeof(Fauna).GetMethod("EatForager", Private);
            object[] args = { new Animal { dir = world.Grid.Dir[c], cell = c, energy = 0.1f }, true };
            for (int i = 0; i < 10; i++) forage.Invoke(world.Fauna, args);
            Check(((Animal)args[0]).energy == 0.1f, "Forager ate fruit from an empty stock");
            fields.Step(10f);
            Check(fields.fruit[c] > 0f && fields.fruit[c] <= 1f, "Fruit failed to regenerate within capacity");
            fields.fruit[c] = 0.05f;
            fields.insects[c] = 0f;
            float initialEnergy = ((Animal)args[0]).energy;
            for (int i = 0; i < 100 && fields.fruit[c] > 0f; i++) forage.Invoke(world.Fauna, args);
            Check(fields.fruit[c] == 0f, "Forager did not deplete the available fruit");
            Near(0.2 * 0.05 / 0.12, ((Animal)args[0]).energy - initialEnergy, "Fruit consumption energy");
            var plant = world.Eco.plants[world.Eco.cellPlant[c]];
            plant.state = 1;
            world.Eco.plants[world.Eco.cellPlant[c]] = plant;
            fields.Step(10f);
            Check(fields.fruit[c] == 0f, "Dead plant produced fruit");
        }
        finally { UnityEngine.Object.DestroyImmediate(world.gameObject); }
    }

    static void SedimentTransport(bool moving, bool coast)
    {
        var g = new PlanetGrid(12, 56.25f);
        var s = new PlanetSim(g);
        int c = g.CellIndex(0, 5, 5);
        int n = g.Nbr[c * 4 + 1];
        for (int i = 0; i < s.M; i++) s.rock[i] = 1f;
        s.rock[c] = -10f; s.water[c] = 10f; s.susp[c] = 5f;
        if (!coast) { s.rock[n] = -10f; s.water[n] = 10f; }
        s.current = new Vector3[s.M];
        if (moving) s.current[c] = g.Tan[c * 4 + 1] * 0.8f;
        double before = Solid(s);
        s.StepHydro();
        Check(Math.Abs(before - Solid(s)) < 0.001, "Sediment budget");
        Check(s.susp[c] >= 0f && s.susp[n] >= 0f, "Negative suspended sediment");
        if (moving && !coast) Check(s.susp[n] > 0.2f, "Current did not transport sediment");
        else Check(s.susp[n] < 0.0001f, "Unexpected sediment transport");
        Check(s.susp[c] > 0f, "Ocean sediment settled immediately");
    }

    static void Currents()
    {
        var g = new PlanetGrid(16, 75f);
        var s = new PlanetSim(g);
        Vector3 center = new Vector3(1f, 0.5f, 0f).normalized;
        for (int c = 0; c < s.M; c++)
        {
            s.rock[c] = -10f; s.water[c] = 10f;
            Vector3 radial = Vector3.ProjectOnPlane(center, g.Dir[c]);
            s.wind[c] = radial.sqrMagnitude > 1e-8f ? Vector3.Cross(g.Dir[c], radial).normalized * 0.6f : Vector3.zero;
        }
        for (int i = 0; i < 120; i++) { s.tick = i; s.StepOcean(); }
        double circulation = 0;
        int count = 0;
        for (int c = 0; c < s.M; c++)
        {
            Vector3 v = s.current[c];
            Check(!float.IsNaN(v.x) && !float.IsInfinity(v.x) && v.magnitude <= 1.001f, "Invalid ocean current");
            Check(Math.Abs(Vector3.Dot(v, g.Dir[c])) < 0.001f, "Current is not tangent");
            float distance = Vector3.Angle(center, g.Dir[c]);
            Vector3 radial = Vector3.ProjectOnPlane(center, g.Dir[c]);
            if (distance > 10f && distance < 45f && radial.sqrMagnitude > 1e-8f)
            {
                circulation += Vector3.Dot(v, Vector3.Cross(g.Dir[c], radial).normalized);
                count++;
            }
        }
        Check(circulation / count > 0.05, "Circular wind failed to generate an ocean gyre");
    }

    static void WindVortices()
    {
        var g = new PlanetGrid(16, 75f);
        var s = new PlanetSim(g);
        Vector3 center = new Vector3(1f, 0.5f, 0f).normalized;
        for (int c = 0; c < s.M; c++) s.thOut[c * 4] = Vector3.Dot(g.Dir[c], center);
        typeof(PlanetSim).GetMethod("WindEddies", Private).Invoke(s, null);
        double circulation = 0;
        int count = 0;
        for (int c = 0; c < s.M; c++)
        {
            Vector3 radial = Vector3.ProjectOnPlane(center, g.Dir[c]);
            if (radial.magnitude < 0.3f) continue;
            Vector3 tangent = Vector3.Cross(g.Dir[c], radial).normalized;
            circulation += Vector3.Dot(s.wind[c], tangent);
            Check(Math.Abs(Vector3.Dot(s.wind[c], g.Dir[c])) < 0.0001f, "Wind eddy is not tangent");
            count++;
        }
        Check(circulation / count > 0.02, "Wind potential failed to create circulation");
    }

    static void CloudAdvection()
    {
        var g = new PlanetGrid(12, 56.25f);
        var s = new PlanetSim(g);
        var go = new GameObject("Cloud validation");
        try
        {
            var world = go.AddComponent<PlanetWorld>();
            world.Grid = g; world.Sim = s;
            var view = new PlanetView(world, null);
            int c = g.CellIndex(0, 5, 5);
            Vector3 start = g.Dir[c];
            var type = typeof(PlanetView);
            type.GetField("siteDir", Private).SetValue(view, new[] { start });
            type.GetField("siteCell", Private).SetValue(view, new[] { c });
            type.GetField("siteYaw", Private).SetValue(view, new[] { 10f });
            type.GetField("siteSize", Private).SetValue(view, new[] { 0f });
            type.GetField("siteSt", Private).SetValue(view, new[] { 0f });
            type.GetField("sitePhase", Private).SetValue(view, new[] { 0.25f });
            type.GetField("siteK", Private).SetValue(view, 16);
            for (int i = 0; i < s.M; i++) s.windHigh[i] = Vector3.ProjectOnPlane(Vector3.up, g.Dir[i]).normalized;
            var update = type.GetMethod("UpdateClouds", Private);
            Vector3 camera = -start * (g.Radius + 100f);
            update.Invoke(view, new object[] { camera, 1f });
            var dirs = (Vector3[])type.GetField("siteDir", Private).GetValue(view);
            Check(Vector3.Distance(start, dirs[0]) > 0.001f, "Cloud site did not follow wind");
            Vector3 moving = dirs[0];
            for (int i = 0; i < s.M; i++) s.windHigh[i] = Vector3.ProjectOnPlane(Vector3.forward, g.Dir[i]).normalized;
            update.Invoke(view, new object[] { camera, 1f });
            Check(Vector3.Distance(moving, dirs[0]) > 0.001f, "Cloud site did not follow a changed wind");
            moving = dirs[0];
            view.TimeScale = 0f;
            update.Invoke(view, new object[] { camera, 1f });
            Check(Vector3.Distance(moving, dirs[0]) < 0.000001f, "Cloud moved while paused");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    public static void RunTsunami()
    {
        TsunamiRunup();
        TsunamiHydroStability();
        Debug.Log("TSUNAMI VALIDATION PASSED");
    }

    public static void Run()
    {
        SedimentTransport(true, false);
        SedimentTransport(false, false);
        SedimentTransport(true, true);
        AreaWeightedTransfers();
        TsunamiRunup();
        TsunamiHydroStability();
        CoastlineWaterLevel();
        SpeedIndependentPhysics();
        EvaporationBalance();
        WindTrapping();
        OceanDepth();
        IcebergVolume();
        WorldClock();
        LocalPredatorHabitat();
        SlopeSoilComposition();
        MantleRockTransfers();
        FruitResources();
        WindVortices();
        Currents();
        CloudAdvection();
        Debug.Log("FLOW VALIDATION PASSED");
    }
}
