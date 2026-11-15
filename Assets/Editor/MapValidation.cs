using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using LivingPlanet;
using UnityEngine;

public static class MapValidation
{
    public static void Run()
    {
        HistoryRoundTrip();
        RandomRoundTrip();
        SaveRoundTrip();
        var go = new GameObject("MapValidation");
        try
        {
            var world = go.AddComponent<PlanetWorld>();
            world.Grid = new PlanetGrid(16, 75f);
            world.Sim = new PlanetSim(world.Grid);
            world.Sim.ApplyAge(4.5f);
            world.Sim.Generate(123);
            var view = new PlanetView(world, null);
            typeof(PlanetWorld).GetField("view", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(world, view);
            var color = typeof(PlanetWorld).GetMethod("MapSurfaceColor", BindingFlags.Instance | BindingFlags.NonPublic);
            int c = world.Grid.CellIndex(0, 8, 8);
            var s = world.Sim;
            s.sed[c] = 0f; s.rockT[c] = PlanetSim.Basalt; s.ore[c] = 0;
            s.snow[c] = 0f; s.glacier[c] = 0f; s.lava[c] = 0f;
            s.temp[c] = 15f; s.burn[c] = 0f; s.scar[c] = 0f; s.flux[c] = 0f;

            s.rock[c] = -10f; s.water[c] = 10f; s.waterState.salt[c] = 10f * PlanetSim.OceanSalinity;
            var ocean = (Color)color.Invoke(world, new object[] { c });
            Check(ocean.b > ocean.r * 3f, "Submerged ocean lost its color");
            s.water[c] = 0f;
            var dry = (Color)color.Invoke(world, new object[] { c });
            Check(dry.r > dry.b, "Dry former sea floor is blue");
            s.rock[c] = 10f; s.water[c] = 10f;
            var emerged = (Color)color.Invoke(world, new object[] { c });
            Check(emerged.r > ocean.r * 3f && emerged.r >= emerged.b * 0.8f, "Emerging basalt remains dark ocean blue");

            Debug.Log("MAP VALIDATION PASSED");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static void SaveRoundTrip()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(PlanetWorld);
        var go = new GameObject("SaveValidation");
        var cameraObject = new GameObject("SaveValidationCamera");
        string path = Path.Combine(Path.GetTempPath(), "planet-validation-" + Guid.NewGuid().ToString("N") + ".save");
        try
        {
            var world = go.AddComponent<PlanetWorld>();
            world.resolution = 128; world.radius = 600f;
            world.Grid = new PlanetGrid(world.resolution, world.radius);
            world.Sim = new PlanetSim(world.Grid);
            world.Sim.ApplyAge(world.ageGyr);
            world.Sim.Generate(123);
            world.Eco = new Ecosystem(world.Sim, 123);
            world.Eco.PlantInitial();
            world.Fauna = new Fauna(world);
            world.Fauna.Init();
            Check(world.Fauna.Total > 0, "Save test has no active animals");
            int fruitCell = -1;
            for (int c = 0; c < world.Sim.M; c++)
                if (world.Eco.FruitType(c) >= 0) { fruitCell = c; break; }
            Check(fruitCell >= 0, "Save test has no fruiting plant");
            world.Fauna.F.fruit[fruitCell] = 0.073f;
            world.Sim.mantleResidual[fruitCell] = 0.000001f;
            world.Sim.onDisturb = world.Eco.Disturb;
            world.Sim.onLightning = c => { if (world.Sim.moist[c] < 0.6f) world.Eco.Ignite(c); };
            world.Sim.onBlastPlant = world.Eco.Blast;
            world.Sim.onBlastFauna = world.Fauna.KillNear;
            var camera = cameraObject.AddComponent<Camera>();
            type.GetField("cam", instance).SetValue(world, camera);
            type.GetField("view", instance).SetValue(world, new PlanetView(world, camera));
            type.GetField("curSeed", instance).SetValue(world, 123);
            type.GetField("ready", instance).SetValue(world, true);
            type.GetField("planetSavePath", instance).SetValue(world, path);
            var randomType = type.Assembly.GetType("LivingPlanet.PlanetRandom");
            var rng = (System.Random)Activator.CreateInstance(randomType, new object[] { 123 });
            type.GetField("rnd", instance).SetValue(world, rng);
            for (int i = 0; i < 10; i++) rng.NextDouble();
            typeof(PlanetSim).GetProperty("WeatherRandomState", instance).SetValue(world.Sim, 101);
            typeof(PlanetSim).GetProperty("VolcanoRandomState", instance).SetValue(world.Sim, 102);
            typeof(PlanetSim).GetProperty("FireRandomState", instance).SetValue(world.Sim, 103);
            typeof(Ecosystem).GetProperty("RandomState", instance).SetValue(world.Eco, 104);
            typeof(Fauna).GetProperty("RandomState", instance).SetValue(world.Fauna, 105);
            var record = type.GetMethod("RecordStats", instance);
            for (int i = 0; i < 50; i++) record.Invoke(world, null);
            Check((bool)type.GetMethod("SavePlanet", instance).Invoke(world, null), "Could not save test planet");
            int randomState = (int)randomType.GetProperty("State").GetValue(rng);
            int expected = rng.Next();
            randomType.GetProperty("State").SetValue(rng, randomState);
            var step = type.GetMethod("StepOnce", instance);
            for (int i = 0; i < 10; i++) step.Invoke(world, null);
            var expectedWater = (float[])world.Sim.water.Clone();
            var expectedRock = (float[])world.Sim.rock.Clone();
            var expectedBreath = (float[])world.Sim.animalBreath.Clone();
            var expectedFruit = (float[])world.Fauna.F.fruit.Clone();
            var expectedMantleResidual = (float[])world.Sim.mantleResidual.Clone();
            var expectedAnimals = new Animal[Fauna.Count][];
            Check(world.Fauna.Total > 0, "All animals died before the continuation check");
            for (int sp = 0; sp < Fauna.Count; sp++)
            {
                var pop = world.Fauna.pops[sp];
                expectedAnimals[sp] = new Animal[pop.n];
                Array.Copy(pop.a, expectedAnimals[sp], pop.n);
            }
            var load = (IEnumerator)type.GetMethod("LoadPlanet", instance).Invoke(world, new object[] { path });
            while (load.MoveNext()) { }
            Check((bool)type.GetField("ready", instance).GetValue(world), "Could not load test planet");
            Check(world.Fauna.F.fruit[fruitCell] == 0.073f, "Fruit stock was not restored");
            Check(world.Sim.mantleResidual[fruitCell] == 0.000001f, "Mantle residual was not restored");
            var resumed = (System.Random)type.GetField("rnd", instance).GetValue(world);
            Check(resumed.Next() == expected, "Planet random state was not restored");
            Check((int)typeof(PlanetSim).GetProperty("WeatherRandomState", instance).GetValue(world.Sim) == 101 &&
                (int)typeof(PlanetSim).GetProperty("VolcanoRandomState", instance).GetValue(world.Sim) == 102 &&
                (int)typeof(PlanetSim).GetProperty("FireRandomState", instance).GetValue(world.Sim) == 103 &&
                (int)typeof(Ecosystem).GetProperty("RandomState", instance).GetValue(world.Eco) == 104 &&
                (int)typeof(Fauna).GetProperty("RandomState", instance).GetValue(world.Fauna) == 105,
                "Subsystem random states were not restored");
            var climate = (Array)type.GetField("sClimate", instance).GetValue(world);
            var seriesType = type.GetNestedType("Series", BindingFlags.NonPublic);
            Check((int)seriesType.GetField("ln").GetValue(climate.GetValue(0)) == 1, "Saved long history was not restored");
            randomType.GetProperty("State").SetValue(resumed, randomState);
            for (int i = 0; i < 10; i++) step.Invoke(world, null);
            for (int c = 0; c < world.Sim.M; c++)
                Check(world.Sim.water[c] == expectedWater[c] && world.Sim.rock[c] == expectedRock[c] &&
                    world.Sim.animalBreath[c] == expectedBreath[c] && world.Fauna.F.fruit[c] == expectedFruit[c] &&
                    world.Sim.mantleResidual[c] == expectedMantleResidual[c],
                    "Simulation diverged after reload at cell " + c);
            for (int sp = 0; sp < Fauna.Count; sp++)
            {
                var pop = world.Fauna.pops[sp];
                Check(pop.n == expectedAnimals[sp].Length, "Fauna population diverged after reload");
                for (int i = 0; i < pop.n; i++)
                    Check(pop.a[i].Equals(expectedAnimals[sp][i]), "Animal state diverged after reload");
            }

            var rateField = typeof(PlanetSim).GetField("rate", instance);
            var normField = typeof(PlanetSim).GetField("speedNorm", instance);
            var accField = typeof(PlanetSim).GetField("acc", instance);
            var rates = (float[])rateField.GetValue(world.Sim);
            for (int p = 0; p < rates.Length; p++) rates[p] *= 0.25f;
            world.Sim.plateSpeed *= 0.25f;
            normField.SetValue(world.Sim, (float)normField.GetValue(world.Sim) * 4f);
            var acc = (float[])accField.GetValue(world.Sim);
            float oldRate = rates[0], oldSpeed = world.Sim.plateSpeed, oldAcc = acc[0];
            Check((bool)type.GetMethod("SavePlanet", instance).Invoke(world, null), "Could not save legacy test planet");
            byte[] data;
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var buffer = new MemoryStream())
            {
                gzip.CopyTo(buffer);
                data = buffer.ToArray();
            }
            int currentVersion = BitConverter.ToInt32(data, 4);
            int footer = BitConverter.ToInt32(data, data.Length - 4);
            Array.Copy(BitConverter.GetBytes(15), 0, data, 4, 4);
            Array.Copy(BitConverter.GetBytes(footer ^ currentVersion ^ 15), 0, data, data.Length - 4, 4);
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
                gzip.Write(data, 0, data.Length);
            load = (IEnumerator)type.GetMethod("LoadPlanet", instance).Invoke(world, new object[] { path });
            while (load.MoveNext()) { }
            Check((bool)type.GetField("ready", instance).GetValue(world), "Could not load version 15 planet");
            rates = (float[])rateField.GetValue(world.Sim);
            acc = (float[])accField.GetValue(world.Sim);
            Check(Mathf.Abs(world.Sim.plateSpeed - oldSpeed * 4f) < 1e-6f &&
                Mathf.Abs(rates[0] - oldRate * 4f) < 1e-7f && acc[0] == oldAcc,
                "Version 15 plate drift was not migrated correctly");
            Check((bool)type.GetMethod("SavePlanet", instance).Invoke(world, null), "Could not save migrated planet");
            load = (IEnumerator)type.GetMethod("LoadPlanet", instance).Invoke(world, new object[] { path });
            while (load.MoveNext()) { }
            Check((bool)type.GetField("ready", instance).GetValue(world), "Could not reload migrated planet");
            rates = (float[])rateField.GetValue(world.Sim);
            acc = (float[])accField.GetValue(world.Sim);
            Check(Mathf.Abs(world.Sim.plateSpeed - oldSpeed * 4f) < 1e-6f &&
                Mathf.Abs(rates[0] - oldRate * 4f) < 1e-7f && acc[0] == oldAcc,
                "Plate drift migration was applied again");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    static void RandomRoundTrip()
    {
        var type = typeof(PlanetWorld).Assembly.GetType("LivingPlanet.PlanetRandom");
        var first = (System.Random)Activator.CreateInstance(type, new object[] { 123 });
        for (int i = 0; i < 53; i++) first.NextDouble();
        int state = (int)type.GetProperty("State").GetValue(first);
        var restored = (System.Random)Activator.CreateInstance(type, new object[] { 456 });
        type.GetProperty("State").SetValue(restored, state);
        for (int i = 0; i < 100; i++)
            Check(first.Next(1000) == restored.Next(1000) && first.NextDouble() == restored.NextDouble(),
                "Random sequence did not resume from saved state");
    }

    static void HistoryRoundTrip()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var world = typeof(PlanetWorld);
        var seriesType = world.GetNestedType("Series", BindingFlags.NonPublic);
        var source = Array.CreateInstance(seriesType, 1);
        var item = Activator.CreateInstance(seriesType, true);
        seriesType.GetField("name").SetValue(item, "test");
        seriesType.GetField("unit").SetValue(item, "");
        var add = seriesType.GetMethod("AddLong");
        for (int i = 0; i < 2003; i++) add.Invoke(item, new object[] { (float)i });
        source.SetValue(item, 0);
        var write = world.GetMethod("WriteSeries", flags);
        var read = world.GetMethod("ReadSeries", flags);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        using (var reader = new BinaryReader(stream))
        {
            write.Invoke(null, new object[] { writer, source });
            stream.Position = 0;
            var loaded = (Array)read.Invoke(null, new object[] { reader, 12 });
            var restored = loaded.GetValue(0);
            Check((int)seriesType.GetField("ln").GetValue(restored) == 2000, "Long history count lost");
            var at = seriesType.GetMethod("LongAt");
            Check((float)at.Invoke(restored, new object[] { 0 }) == 3f &&
                (float)at.Invoke(restored, new object[] { 1999 }) == 2002f, "Wrapped long history lost");
        }
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        using (var reader = new BinaryReader(stream))
        {
            writer.Write(true); writer.Write(1); writer.Write("legacy"); writer.Write("");
            for (int i = 0; i < 4; i++) writer.Write(1f);
            writer.Write(1); writer.Write(0);
            for (int i = 0; i < 576; i++) writer.Write(i == 0 ? 42f : 0f);
            stream.Position = 0;
            var loaded = (Array)read.Invoke(null, new object[] { reader, 11 });
            var restored = loaded.GetValue(0);
            Check((int)seriesType.GetField("ln").GetValue(restored) == 0 &&
                ((float[])seriesType.GetField("v").GetValue(restored))[0] == 42f,
                "Legacy short history was not preserved");
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
