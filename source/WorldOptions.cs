// Tiny Town Enhanced - the world settings on the World tab, saved with the world.
// The game's own values are read from the scene the first time they are needed, so "unchanged" always means exactly
// what the game does.
//   Presets:    Default, Sunrise, Sunset, Dusk, Night, Overcast; Time of Day (blends between looks through the day)
//               (night brings the stars out: Stars.cs)
//   Atmosphere: Sky Top / Horizon / Bottom colours, Sky Brightness, Sky Blend Top / Bottom,
//               Clouds, Cloud Amount, Cloud Size, Cloud Height, Cloud Color
//   Lighting:   Sun Direction, Sun Height, Sun Brightness, Sun Light Color, Sun Disc Color, Sun Size, Sun Glow,
//               Shadow Strength (0 = no shadows at all), Fill Light, Fill Light Color
//   (Reset, in the menu's header, puts all of them back: WorldTab.cs)
// Fill light (the light in the shade): untouched, it is the game's own, taken from its sky. Once the sky's colours
// are changed it follows them (top / horizon / bottom); once Fill Light Color is changed it is that one colour.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyTownEnhanced
{
    public class WorldOptions : IModule
    {
        public string Name { get { return "WorldOptions"; } }

        // the game's own values, read once ("captured"); "known" is that, and the scene's sun and sky found as well
        static bool known, captured; static int triedAt = -1;
        static Light sun; static Sky sky; static Material skyMaterial, cloudMaterial; static GameObject clouds;
        static float sunDirection, sunHeight, sunBrightness, shadowStrength, fillLight, skyBrightness, cloudHeight, blendTop, blendBottom, sunAlpha, sunBeta;
        static Color skyTop, skyHorizon, skyBottom, sunDisc, sunLight, cloudColor, ambientSky, ambientEquator, ambientGround;
        static int cloudCount; static bool cloudsHaveColor; static LightShadows shadows; static AmbientMode ambientMode;
        static readonly FieldInfo fClouds = AccessTools.Field(typeof(Sky), "clouds"), fCount = AccessTools.Field(typeof(Sky), "cloudCount"),
                                  fHeight = AccessTools.Field(typeof(Sky), "cloudHeight"), fMaterial = AccessTools.Field(typeof(Sky), "skyMaterial"),
                                  fCloudMaterial = AccessTools.Field(typeof(Sky), "cloudMaterial");

        static bool Known()
        {
            if (known) return true;
            // (while the scene is not up yet this is asked once for every setting in a row, and each try searches the
            //  scene: so one try a frame)
            if (Time.frameCount == triedAt) return false;
            triedAt = Time.frameCount;
            try
            {
                var lightObject = GameObject.Find("Directional Light");
                sun = lightObject != null ? lightObject.GetComponent<Light>() : null;
                foreach (var s in Resources.FindObjectsOfTypeAll<Sky>()) if (s.gameObject.scene.IsValid()) sky = s;
                if (sun == null || sky == null) return false;
                clouds = fClouds.GetValue(sky) as GameObject;
                if (clouds == null) return false;                               // (the sky has not started yet)
                skyMaterial = fMaterial.GetValue(sky) as Material;
                cloudMaterial = fCloudMaterial.GetValue(sky) as Material;
                if (skyMaterial == null) return false;
                if (!captured)
                {   // (only ever once: after this the mod itself changes the sun and the sky's material)
                    Vector3 angles = sun.transform.eulerAngles;
                    sunDirection = angles.y; sunHeight = angles.x;
                    sunBrightness = sun.intensity; shadowStrength = sun.shadowStrength; sunLight = sun.color; shadows = sun.shadows;
                    fillLight = RenderSettings.ambientIntensity; ambientMode = RenderSettings.ambientMode;
                    ambientSky = RenderSettings.ambientSkyColor; ambientEquator = RenderSettings.ambientEquatorColor; ambientGround = RenderSettings.ambientGroundColor;
                    skyBrightness = skyMaterial.GetFloat("_SkyIntensity");
                    skyTop = skyMaterial.GetColor("_SkyColor1"); skyHorizon = skyMaterial.GetColor("_SkyColor2"); skyBottom = skyMaterial.GetColor("_SkyColor3");
                    blendTop = skyMaterial.GetFloat("_SkyExponent1"); blendBottom = skyMaterial.GetFloat("_SkyExponent2");
                    sunDisc = skyMaterial.GetColor("_SunColor"); sunAlpha = skyMaterial.GetFloat("_SunAlpha"); sunBeta = skyMaterial.GetFloat("_SunBeta");
                    cloudsHaveColor = cloudMaterial != null && cloudMaterial.HasProperty("_Color");
                    cloudColor = cloudsHaveColor ? cloudMaterial.GetColor("_Color") : Color.white;
                    cloudCount = (int)fCount.GetValue(sky); cloudHeight = (float)fHeight.GetValue(sky);
                    captured = true;
                }
                known = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldOptions: " + e.Message); }
            return known;
        }

        static void Aim() { if (Known()) sun.transform.rotation = Quaternion.Euler(WorldFile.GetFloat("sunHeight"), WorldFile.GetFloat("sunDirection"), 0f); }

        static void Clouds()
        {
            if (!Known()) return;
            // (changing either of these rebuilds the cloud mesh, so only when it really is a change)
            float height = cloudHeight * WorldFile.GetFloat("cloudHeight") / 100f; int count = Mathf.RoundToInt(cloudCount * WorldFile.GetFloat("cloudAmount") / 100f);
            if (count != (int)fCount.GetValue(sky) || !Mathf.Approximately(height, (float)fHeight.GetValue(sky)) || !Mathf.Approximately(WorldFile.GetFloat("cloudSize"), CloudField.BuiltSize))
            {
                fHeight.SetValue(sky, height);
                sky.SetCloudCount(count);
            }
            var r = clouds.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = WorldFile.GetBool("clouds");
        }

        static void SkyColor(string property, Color c, bool lights = true)
        {
            if (!Known()) return;
            skyMaterial.SetColor(property, new Color(c.r, c.g, c.b, skyMaterial.GetColor(property).a));
            if (lights) Fill();                                     // (the sun disc's colour changes neither the fill light nor the reflections)
        }

        // the light in the shade (see the top of the file)
        // A preset or a Time of Day step sets a dozen settings in a row, and most of them end
        // here, and would redo the fill light and the reflection picture each time. While "holding" is on the work is
        // only noted, and done once when the row is finished (Settle).
        static bool holding, pending;
        static void Settle() { holding = false; if (pending) { pending = false; Fill(); } }

        static void Fill()
        {
            if (holding) { pending = true; return; }
            if (!Known()) return;
            float amount = WorldFile.GetFloat("fillLight");
            if (WorldFile.IsSet("fillColor"))
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = WorldFile.GetColor("fillColor") * amount;
            }
            else if (WorldFile.IsSet("skyTop") || WorldFile.IsSet("skyHorizon") || WorldFile.IsSet("skyBottom"))
            {
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = WorldFile.GetColor("skyTop") * amount;
                RenderSettings.ambientEquatorColor = WorldFile.GetColor("skyHorizon") * amount;
                RenderSettings.ambientGroundColor = WorldFile.GetColor("skyBottom") * amount;
            }
            else
            {   // the game's own: its mode, and its colours too (the two branches above overwrite them, and the game's
                // mode may be one that uses them)
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientSkyColor = ambientSky; RenderSettings.ambientEquatorColor = ambientEquator; RenderSettings.ambientGroundColor = ambientGround;
            }
            RenderSettings.ambientIntensity = amount;
            Reflect();
        }

        // What shiny and metallic surfaces reflect. The game's scene has a fixed picture of its own blue sky for that,
        // which a metallic model (Looks.cs) would go on showing under a sunset. With sky colours of the world's own,
        // a small picture of that sky (the three colours, top to bottom) is made and used instead.
        static Cubemap reflected; static bool reflectedOn; static bool reflectionKnown; static DefaultReflectionMode reflectionMode; static Cubemap reflectionWas;
        const int ReflectionSize = 16;
        static readonly Color[] pixels = new Color[ReflectionSize * ReflectionSize];
        static Color reflectedTop, reflectedHorizon, reflectedBottom; static float reflectedUpper, reflectedLower;      // what the picture was last made from
        static void Reflect()
        {
            if (!reflectionKnown) { reflectionKnown = true; reflectionMode = RenderSettings.defaultReflectionMode; reflectionWas = RenderSettings.customReflection; }
            if (!(WorldFile.IsSet("skyTop") || WorldFile.IsSet("skyHorizon") || WorldFile.IsSet("skyBottom") || WorldFile.IsSet("skyBrightness") || WorldFile.IsSet("skyBlendTop") || WorldFile.IsSet("skyBlendBottom")))
            {
                if (reflectedOn) { RenderSettings.defaultReflectionMode = reflectionMode; RenderSettings.customReflection = reflectionWas; reflectedOn = false; }
                return;
            }
            float bright = skyMaterial.GetFloat("_SkyIntensity"), upper = Mathf.Max(0.01f, skyMaterial.GetFloat("_SkyExponent1")), lower = Mathf.Max(0.01f, skyMaterial.GetFloat("_SkyExponent2"));
            Color top = skyMaterial.GetColor("_SkyColor1") * bright, horizon = skyMaterial.GetColor("_SkyColor2") * bright, bottom = skyMaterial.GetColor("_SkyColor3") * bright;
            if (reflectedOn && top == reflectedTop && horizon == reflectedHorizon && bottom == reflectedBottom && upper == reflectedUpper && lower == reflectedLower) return;
            if (reflected == null) reflected = new Cubemap(ReflectionSize, TextureFormat.RGBA32, true) { name = "TinyTownEnhanced sky reflection" };
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < ReflectionSize; y++)
                    for (int x = 0; x < ReflectionSize; x++)
                    {
                        float u = (x + 0.5f) / ReflectionSize * 2f - 1f, v = (y + 0.5f) / ReflectionSize * 2f - 1f;
                        // how far up this point of the face looks (faces: +x -x +y -y +z -z; rows run downwards)
                        float up = face == 2 ? 1f : face == 3 ? -1f : -v;
                        float along = face == 2 || face == 3 ? Mathf.Sqrt(u * u + v * v) : Mathf.Sqrt(1f + u * u);
                        float height = up / Mathf.Sqrt(up * up + along * along);
                        Color c = height >= 0f ? Color.Lerp(horizon, top, Mathf.Pow(height, 1f / upper)) : Color.Lerp(horizon, bottom, Mathf.Pow(-height, 1f / lower));
                        c.a = 1f; pixels[y * ReflectionSize + x] = c;
                    }
                reflected.SetPixels(pixels, (CubemapFace)face);
            }
            reflected.Apply(true);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom; RenderSettings.customReflection = reflected;
            reflectedOn = true; reflectedTop = top; reflectedHorizon = horizon; reflectedBottom = bottom; reflectedUpper = upper; reflectedLower = lower;
        }

        // ---- looks: a set of values for the settings a preset changes
        static readonly string[] PresetKeys = { "skyTop", "skyHorizon", "skyBottom", "skyBrightness", "sunDisc", "sunLight", "sunHeight", "sunBrightness", "shadowStrength", "fillLight", "fillColor", "timeOfDay", "stars", "starFade", "sunGlow" };
        // what the Default tile puts back to the game's own besides those: the rest of the sky and the sun
        static readonly string[] DefaultKeys = { "sunDirection", "sunSize", "skyBlendTop", "skyBlendBottom" };

        class Look
        {
            public Color Top, Horizon, Bottom, Disc, Light; public float Height, Brightness, Shadows, Fill;
            public float Stars;                                      // 0 none .. 1 fully out (Stars.cs)
            public Color? FillColor;                                 // the fill light's own colour; none: it follows the sky
            public float Glow = 100f;                                // Sun Glow, percent: 0 and the sun itself cannot be seen (its light is still there)
            public Look(Color top, Color horizon, Color bottom, Color disc, Color light, float height, float brightness, float shadows, float fill, float stars = 0f)
            { Top = top; Horizon = horizon; Bottom = bottom; Disc = disc; Light = light; Height = height; Brightness = brightness; Shadows = shadows; Fill = fill; Stars = stars; }

            public static Look Between(Look a, Look b, float t)
            {
                return new Look(Color.Lerp(a.Top, b.Top, t), Color.Lerp(a.Horizon, b.Horizon, t), Color.Lerp(a.Bottom, b.Bottom, t), Color.Lerp(a.Disc, b.Disc, t), Color.Lerp(a.Light, b.Light, t),
                                Mathf.Lerp(a.Height, b.Height, t), Mathf.Lerp(a.Brightness, b.Brightness, t), Mathf.Lerp(a.Shadows, b.Shadows, t), Mathf.Lerp(a.Fill, b.Fill, t), Mathf.Lerp(a.Stars, b.Stars, t))
                {   // (a look without a fill colour of its own counts as its horizon colour while blending with one that has)
                    FillColor = a.FillColor.HasValue || b.FillColor.HasValue ? Color.Lerp(a.FillColor ?? a.Horizon, b.FillColor ?? b.Horizon, t) : (Color?)null,
                    Glow = Mathf.Lerp(a.Glow, b.Glow, t)
                };
            }

            public void Apply()
            {
                holding = true;
                try { Set(); } finally { Settle(); }
            }

            void Set()
            {
                WorldFile.SetColor("skyTop", Top); WorldFile.SetColor("skyHorizon", Horizon); WorldFile.SetColor("skyBottom", Bottom);
                WorldFile.SetColor("sunDisc", Disc); WorldFile.SetColor("sunLight", Light);
                WorldFile.SetFloat("sunHeight", Mathf.Round(Height)); WorldFile.SetFloat("sunBrightness", Brightness);
                WorldFile.SetFloat("shadowStrength", Shadows); WorldFile.SetFloat("fillLight", Fill);
                WorldFile.SetFloat("sunGlow", Mathf.Round(Glow));
                // (set to the horizon colour, the fill colour counts as unchanged: it follows the sky again)
                WorldFile.SetColor("fillColor", FillColor ?? Horizon);
                if (Stars > 0.02f) { WorldFile.SetBool("stars", true); WorldFile.SetFloat("starFade", Stars); }
                else { WorldFile.SetBool("stars", false); WorldFile.SetFloat("starFade", 1f); }
            }
        }

        // Sun Height in these: 0 is one horizon, 90 straight overhead, 180 the opposite horizon - so through the day the
        // sun rises on one side (Sunrise, 15), crosses the top at noon and goes down on the other (Sunset, 170).
        static Color C(float r, float g, float b) { return new Color(r, g, b, 1f); }
        static readonly Look Sunrise  = new Look(C(0.35f, 0.55f, 0.85f), C(1f, 0.78f, 0.60f), C(0.60f, 0.50f, 0.50f), C(1f, 0.85f, 0.60f), C(1f, 0.85f, 0.70f), 15f, 0.9f, 0.8f, 1f);
        static readonly Look Sunset   = new Look(C(0.20f, 0.25f, 0.50f), C(0.95f, 0.55f, 0.35f), C(0.45f, 0.30f, 0.35f), C(1f, 0.60f, 0.30f), C(1f, 0.65f, 0.40f), 170f, 0.9f, 0.9f, 0.8f);
        static readonly Look Dusk     = new Look(C(0.08f, 0.08f, 0.25f), C(0.50f, 0.30f, 0.55f), C(0.15f, 0.12f, 0.25f), C(0.90f, 0.60f, 0.70f), C(0.75f, 0.60f, 0.85f), 175f, 0.5f, 0.7f, 0.6f)
                                                 { Glow = 0f };     // (the sun has set: its light lingers, the sun itself is not seen)
        static readonly Look Night    = new Look(C(0.01f, 0.02f, 0.06f), C(0.05f, 0.08f, 0.18f), C(0.02f, 0.03f, 0.07f), C(0.80f, 0.85f, 1f), C(0.55f, 0.65f, 1f), 45f, 0.6f, 1f, 1f, 1f)
                                                 { FillColor = Color.HSVToRGB(227f / 360f, 0.5f, 0.35f) };
        static readonly Look Overcast = new Look(C(0.55f, 0.58f, 0.62f), C(0.75f, 0.77f, 0.80f), C(0.50f, 0.52f, 0.55f), C(0.85f, 0.87f, 0.90f), C(0.85f, 0.88f, 0.90f), 60f, 0.6f, 0.4f, 1.3f);

        // the Night look with the moon at the given height
        static Look NightAt(float height) { var night = Look.Between(Night, Night, 0f); night.Height = height; return night; }

        // the game's own look, with the sun at the given height
        static Look Day(float height) { return new Look(skyTop, skyHorizon, skyBottom, sunDisc, sunLight, height, sunBrightness, shadowStrength, fillLight); }

        // a tile on the Presets tab; its swatch is the horizon colour
        // (the Time of Day slider moves to the preset's hour)
        static void Preset(string name, Look look, float hour)
        {
            WorldTab.AddTile("Presets", name, () => look.Horizon, delegate { WorldFile.Reset(PresetKeys); look.Apply(); WorldFile.SetFloat("timeOfDay", hour); });
        }

        // the look at an hour of the day: night, sunrise about 6, the game's own look through the day with the sun
        // crossing the sky from one horizon to the other (overhead at noon), sunset about 18, dusk, night.
        // At night the light is the moon's, which crosses back the other way so that it never swings about quickly.
        static void TimeOfDay(float hour)
        {
            if (!Known()) return;
            float[] hours = { 0f, 5f, 6.5f, 9f, 12f, 15f, 18f, 19.5f, 21f, 24f };
            Look[] looks = { NightAt(90f), NightAt(45f), Sunrise, Day(45f), Day(90f), Day(135f), Sunset, Dusk, NightAt(135f), NightAt(90f) };
            int i = 0;
            while (i < hours.Length - 2 && hour >= hours[i + 1]) i++;
            Look.Between(looks[i], looks[i + 1], Mathf.InverseLerp(hours[i], hours[i + 1], hour)).Apply();
        }

        public void Start(Plugin plugin)
        {
            Func<float, string> percent = v => v.ToString("0") + "%", degrees = v => v.ToString("0") + "°", number = v => v.ToString("0.00");
            Func<bool> cloudsOn = () => WorldFile.GetBool("clouds");

            // ---- Presets
            WorldTab.AddTile("Presets", "Default", () => Known() ? skyHorizon : Color.white, delegate
            {   // everything about the sky and the sun as the unmodified game has it
                var keys = new System.Collections.Generic.List<string>(PresetKeys); keys.AddRange(DefaultKeys);
                WorldFile.Reset(keys.ToArray());
            });
            Preset("Sunrise", Sunrise, 6.5f); Preset("Sunset", Sunset, 18f); Preset("Dusk", Dusk, 19.5f); Preset("Night", Night, 0f); Preset("Overcast", Overcast, 12f);
            // (the hour is saved only so the slider shows it again; what it sets are the ordinary settings, written when
            //  the slider is moved, so anything adjusted afterwards is kept)
            WorldFile.AddFloat("timeOfDay", () => 12f, delegate { });
            WorldTab.AddPlainSlider("Presets", "Time of Day", 0f, 24f, 0.25f, v => Mathf.FloorToInt(v).ToString("00") + ":" + Mathf.RoundToInt((v - Mathf.Floor(v)) * 60f).ToString("00"),
                                    () => WorldFile.GetFloat("timeOfDay"), delegate (float hour) { WorldFile.SetFloat("timeOfDay", hour); TimeOfDay(hour); });

            // ---- Atmosphere
            WorldTab.AddColor("Atmosphere", "skyTop", "Sky Top", () => Known() ? skyTop : Color.white, c => SkyColor("_SkyColor1", c));
            WorldTab.AddColor("Atmosphere", "skyHorizon", "Sky Horizon", () => Known() ? skyHorizon : Color.white, c => SkyColor("_SkyColor2", c));
            WorldTab.AddColor("Atmosphere", "skyBottom", "Sky Bottom", () => Known() ? skyBottom : Color.white, c => SkyColor("_SkyColor3", c));
            WorldTab.AddSlider("Atmosphere", "skyBrightness", "Sky Brightness", 0f, 2f, 0.05f, number, () => Known() ? skyBrightness : 1f,
                               delegate (float v) { if (Known()) { skyMaterial.SetFloat("_SkyIntensity", v); Fill(); } });
            WorldTab.AddSlider("Atmosphere", "skyBlendTop", "Sky Blend Top", 25f, 400f, 25f, percent, () => 100f, delegate (float v) { if (Known()) { skyMaterial.SetFloat("_SkyExponent1", blendTop * v / 100f); Fill(); } });
            WorldTab.AddSlider("Atmosphere", "skyBlendBottom", "Sky Blend Bottom", 25f, 400f, 25f, percent, () => 100f, delegate (float v) { if (Known()) { skyMaterial.SetFloat("_SkyExponent2", blendBottom * v / 100f); Fill(); } });
            WorldTab.AddCheckbox("Atmosphere", "clouds", "Clouds", "This world has clouds.", () => true, delegate { Clouds(); });
            // (above 100% there are more clouds than the game has, which costs: the label says so while it applies)
            WorldTab.AddSlider("Atmosphere", "cloudAmount", "Cloud Amount (Costs FPS)", 0f, 300f, 10f, percent, () => 100f, delegate { Clouds(); }, cloudsOn, () => WorldFile.GetFloat("cloudAmount") > 100f);
            WorldTab.AddSlider("Atmosphere", "cloudSize", "Cloud Size", 50f, 300f, 10f, percent, () => 100f, delegate { Clouds(); }, cloudsOn);
            WorldTab.AddSlider("Atmosphere", "cloudHeight", "Cloud Height", 50f, 200f, 10f, percent, () => 100f, delegate { Clouds(); }, cloudsOn);
            WorldTab.AddColor("Atmosphere", "cloudColor", "Cloud Color", () => Known() ? cloudColor : Color.white,
                              delegate (Color c) { if (Known() && cloudsHaveColor) cloudMaterial.SetColor("_Color", new Color(c.r, c.g, c.b, cloudColor.a)); },
                              () => cloudsOn() && Known() && cloudsHaveColor);

            // ---- Lighting
            WorldTab.AddSlider("Lighting", "sunDirection", "Sun Direction", 0f, 355f, 5f, degrees, () => Known() ? Mathf.Repeat(sunDirection, 360f) : 0f, delegate { Aim(); });
            WorldTab.AddSlider("Lighting", "sunHeight", "Sun Height", 5f, 175f, 5f, degrees, () => Known() ? sunHeight : 45f, delegate { Aim(); });
            WorldTab.AddSlider("Lighting", "sunBrightness", "Sun Brightness", 0f, 2f, 0.05f, number, () => Known() ? sunBrightness : 1f, delegate (float v) { if (Known()) sun.intensity = v; });
            WorldTab.AddColor("Lighting", "sunLight", "Sun Light Color", () => Known() ? sunLight : Color.white, delegate (Color c) { if (Known()) sun.color = c; });
            WorldTab.AddSlider("Lighting", "shadowStrength", "Shadow Strength", 0f, 1f, 0.05f, number, () => Known() ? shadowStrength : 1f,
                               delegate (float v) { if (!Known()) return; sun.shadowStrength = v; sun.shadows = v <= 0.001f ? LightShadows.None : shadows; });   // (0: no shadows are drawn at all)
            WorldTab.AddSlider("Lighting", "fillLight", "Fill Light", 0f, 2f, 0.05f, number, () => Known() ? fillLight : 1f, delegate { Fill(); });
            WorldTab.AddColor("Lighting", "fillColor", "Fill Light Color", () => WorldFile.GetColor("skyHorizon"), delegate { Fill(); });
            WorldTab.AddColor("Lighting", "sunDisc", "Sun Disc Color", () => Known() ? sunDisc : Color.white, c => SkyColor("_SunColor", c, false));
            WorldTab.AddSlider("Lighting", "sunSize", "Sun Size", 25f, 400f, 25f, percent, () => 100f, delegate (float v) { if (Known()) skyMaterial.SetFloat("_SunAlpha", sunAlpha * 100f / Mathf.Max(v, 1f)); });
            WorldTab.AddSlider("Lighting", "sunGlow", "Sun Glow", 0f, 300f, 10f, percent, () => 100f, delegate (float v) { if (Known()) skyMaterial.SetFloat("_SunBeta", sunBeta * v / 100f); });
        }
        // (the sun and the sky belong to the scene: found again after a scene change; the game's values are kept)
        public void SceneLoaded(string scene) { known = false; }
        public void Tick() { }
    }
}
