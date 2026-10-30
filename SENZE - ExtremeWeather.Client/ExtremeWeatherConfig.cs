using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class TunedValue<T>
{
    private readonly Func<T> _getValue;

    internal TunedValue(T value)
        : this(() => value)
    {
    }

    internal TunedValue(Func<T> getValue)
    {
        _getValue = getValue;
    }

    internal T Value => _getValue();
}

internal sealed class ExtremeWeatherConfig
{
    internal ConfigEntry<bool> Enabled { get; }
    internal ConfigEntry<KeyboardShortcut> MenuToggleKey { get; }
    internal ConfigEntry<bool> AutomaticStormsEnabled { get; }
    internal ConfigEntry<float> AutomaticStormChance { get; }
    internal ConfigEntry<float> ExtraRainVisuals { get; }
    internal ConfigEntry<float> ExtraSnowVisuals { get; }
    internal ConfigEntry<int> StormWindVisualBoost { get; }
    internal ConfigEntry<int> MaximumStormBotVision { get; }
    internal ConfigEntry<int> LightningFrequency { get; }
    internal ConfigEntry<int> LightningBrightness { get; }
    internal ConfigEntry<bool> NearStrikeEnabled { get; }
    internal ConfigEntry<float> NearStrikeChance { get; }
    internal ConfigEntry<bool> NearStrikeVisionEnabled { get; }
    internal ConfigEntry<int> NearStrikeVisionStrength { get; }
    internal ConfigEntry<float> NearStrikeVisionDuration { get; }
    internal ConfigEntry<bool> NearStrikeTinnitusEnabled { get; }
    internal ConfigEntry<float> NearStrikeTinnitusDuration { get; }
    internal ConfigEntry<bool> ThundersnowEnabled { get; }
    internal ConfigEntry<int> ThundersnowFrequency { get; }
    internal ConfigEntry<int> RainVolume { get; }
    internal ConfigEntry<int> SnowVolume { get; }
    internal ConfigEntry<int> WindVolume { get; }
    internal ConfigEntry<int> ThunderVolume { get; }
    internal ConfigEntry<int> ThunderImpact { get; }
    internal ConfigEntry<bool> BodycamInterferenceEnabled { get; }
    internal ConfigEntry<float> BodycamInterferenceIntensity { get; }

    internal TunedValue<float> ExtraRainMaximumDensityMultiplier { get; }
    internal TunedValue<bool> LightningEnabled { get; }
    internal TunedValue<float> LightningCloudThreshold { get; }
    internal TunedValue<float> LightningIntervalMinimum { get; }
    internal TunedValue<float> LightningIntervalMaximum { get; }
    internal TunedValue<float> NormalFlashBrightness { get; }
    internal TunedValue<float> StrongFlashBrightness { get; }
    internal TunedValue<float> StrongFlashChance { get; }
    internal TunedValue<float> DoubleDischargeChance { get; }
    internal TunedValue<float> TripleDischargeChance { get; }
    internal TunedValue<float> BurstGapMinimum { get; }
    internal TunedValue<float> BurstGapMaximum { get; }
    internal TunedValue<float> IndoorFlashMultiplier { get; }
    internal TunedValue<float> NearStrikeDistanceMinimum { get; }
    internal TunedValue<float> NearStrikeDistanceMaximum { get; }
    internal TunedValue<float> NearStrikeCameraSafetyDistance { get; }
    internal TunedValue<bool> NearStrikeImpactEffects { get; }
    internal TunedValue<int> NearStrikeImpactStrength { get; }
    internal TunedValue<int> NearStrikeSmokeLifetime { get; }
    internal TunedValue<float> LoudThunderChance { get; }
    internal TunedValue<int> LoudThunderBoost { get; }
    internal TunedValue<float> SoftRumbleChance { get; }
    internal TunedValue<int> IndoorThunderVolume { get; }
    internal TunedValue<float> ThunderDelayMinimum { get; }
    internal TunedValue<float> ThunderDelayMaximum { get; }
    internal TunedValue<float> ThunderDurationMinimum { get; }
    internal TunedValue<float> ThunderDurationMaximum { get; }
    internal TunedValue<float> ThunderFadeOutMinimum { get; }
    internal TunedValue<float> ThunderFadeOutMaximum { get; }
    internal TunedValue<int> ThunderSpatialBlend { get; }
    internal TunedValue<float> NearStrikeInterferenceIntensity { get; }
    internal TunedValue<float> NearStrikeInterferenceDuration { get; }
    internal TunedValue<float> StrongLightningInterferenceChance { get; }
    internal TunedValue<float> StrongLightningInterferenceIntensity { get; }
    internal TunedValue<float> StrongLightningInterferenceDuration { get; }
    internal TunedValue<float> NearStrikeTinnitusDistance { get; }

    internal ExtremeWeatherConfig(ConfigFile config, Action refreshAudio)
    {
        Enabled = config.Bind("00 - General", "Enabled", true,
            "Enables Extreme Weather.");
        MenuToggleKey = config.Bind("00 - General", "Menu Toggle Key", new KeyboardShortcut(KeyCode.UpArrow),
            "Opens or closes the in-raid time and weather panel.");

        AutomaticStormsEnabled = config.Bind("10 - Storm", "Automatic Storms Enabled", true,
            "Allows natural or manually selected rain or snow to develop into an extreme storm automatically.");
        AutomaticStormChance = BindRange(config, "10 - Storm", "Automatic Storm Chance (%)", 35f, 0f, 100f,
            "Chance rolled once when natural or manually selected rain or snow is first detected during a raid.");
        ExtraRainVisuals = BindRange(config, "10 - Storm", "Extra Rain (%)", 80f, 0f, 200f,
            "Increases Tarkov's native rain density and ground splashes.");
        ExtraSnowVisuals = BindRange(config, "10 - Storm", "Extra Snow (%)", 200f, 0f, 200f,
            "Increases Tarkov's native snowstorm flakes, flake size and sideways movement.");
        StormWindVisualBoost = BindRange(config, "10 - Storm", "Storm Wind Visuals (%)", 160, 100, 200,
            "Scales rain angle, cloud movement and vegetation sway during an extreme storm.");
        MaximumStormBotVision = BindRange(config, "10 - Storm", "Maximum Storm Bot Vision (metres)", 70, 0, 150,
            "Caps bot vision once an extreme storm reaches maximum precipitation and fog. Set to 0 to disable.");
        LightningFrequency = BindRange(config, "10 - Storm", "Lightning Frequency (%)", 100, 50, 200,
            "Controls how often custom lightning occurs. 100% uses the tuned 8–40 second interval.");
        LightningBrightness = BindRange(config, "10 - Storm", "Lightning Brightness (%)", 130, 50, 200,
            "Scales normal and rare strong lightning flashes together.");
        NearStrikeEnabled = config.Bind("10 - Storm", "Near Strikes Enabled", true,
            "Allows rare open-sky lightning impacts near the player.");
        NearStrikeChance = BindRange(config, "10 - Storm", "Near Strike Chance (%)", 2.5f, 0f, 10f,
            "Chance that a lightning event becomes a near strike.");
        NearStrikeVisionEnabled = config.Bind("10 - Storm", "Near Strike Vision Effect", true,
            "Adds a short distance- and view-dependent vision washout after a near strike.");
        NearStrikeVisionStrength = BindRange(config, "10 - Storm", "Near Strike Vision Strength (%)", 170, 25, 200,
            "Scales the brightness of the near-strike vision washout.");
        NearStrikeVisionDuration = BindRange(config, "10 - Storm", "Near Strike Vision Duration (s)", 4f, 0.5f, 5f,
            "Controls how long the near-strike vision washout takes to fade.");
        NearStrikeTinnitusEnabled = config.Bind("10 - Storm", "Near Strike Tinnitus", true,
            "Uses Tarkov's native tinnitus for exceptionally close near strikes, including when an earpiece is equipped.");
        NearStrikeTinnitusDuration = BindRange(config, "10 - Storm", "Near Strike Tinnitus Duration (s)", 30f, 15f, 30f,
            "Native tinnitus duration after an exceptionally close near strike. Tarkov enforces a 15-second minimum.");
        ThundersnowEnabled = config.Bind("10 - Storm", "Thundersnow Enabled", true,
            "Allows rare lightning and thunder during extreme winter snowstorms.");
        ThundersnowFrequency = BindRange(config, "10 - Storm", "Thundersnow Frequency (%)", 25, 5, 100,
            "Scales winter lightning frequency relative to a rainstorm. 25% produces four times longer intervals.");

        RainVolume = BindRange(config, "20 - Audio", "Rain Volume (%)", 70, 0, 100,
            "Volume of Tarkov rain ambience.");
        SnowVolume = BindRange(config, "20 - Audio", "Snow Volume (%)", 70, 0, 100,
            "Volume of Tarkov snow ambience.");
        WindVolume = BindRange(config, "20 - Audio", "Wind Volume (%)", 60, 0, 100,
            "Volume of Tarkov wind ambience.");
        ThunderVolume = BindRange(config, "20 - Audio", "Thunder Volume (%)", 80, 0, 100,
            "Volume of Extreme Weather thunder.");
        ThunderImpact = BindRange(config, "20 - Audio", "Thunder Impact (%)", 150, 50, 200,
            "Strength of loud thunder peaks and their supporting rumble.");

        BodycamInterferenceEnabled = config.Bind("30 - Bodycam", "Electrical Interference Enabled", true,
            "Allows compatible Bodycam versions to react to near strikes and rare strong lightning.");
        BodycamInterferenceIntensity = BindRange(config, "30 - Bodycam", "Electrical Interference Intensity", 0.95f, 0f, 1f,
            "Maximum Bodycam interference strength. Distant strong lightning uses a reduced portion of this value.");

        ExtraRainMaximumDensityMultiplier = new TunedValue<float>(8f);
        LightningEnabled = new TunedValue<bool>(true);
        LightningCloudThreshold = new TunedValue<float>(0.5f);
        LightningIntervalMinimum = new TunedValue<float>(() => 800f / LightningFrequency.Value);
        LightningIntervalMaximum = new TunedValue<float>(() => 4000f / LightningFrequency.Value);
        NormalFlashBrightness = new TunedValue<float>(() => 1.15f * LightningBrightness.Value / 100f);
        StrongFlashBrightness = new TunedValue<float>(() => 2.2f * LightningBrightness.Value / 100f);
        StrongFlashChance = new TunedValue<float>(6f);
        DoubleDischargeChance = new TunedValue<float>(19f);
        TripleDischargeChance = new TunedValue<float>(3f);
        BurstGapMinimum = new TunedValue<float>(0.08f);
        BurstGapMaximum = new TunedValue<float>(0.28f);
        IndoorFlashMultiplier = new TunedValue<float>(0.1f);
        NearStrikeDistanceMinimum = new TunedValue<float>(65f);
        NearStrikeDistanceMaximum = new TunedValue<float>(80f);
        NearStrikeCameraSafetyDistance = new TunedValue<float>(65f);
        NearStrikeImpactEffects = new TunedValue<bool>(true);
        NearStrikeImpactStrength = new TunedValue<int>(300);
        NearStrikeSmokeLifetime = new TunedValue<int>(300);
        LoudThunderChance = new TunedValue<float>(4f);
        LoudThunderBoost = new TunedValue<int>(180);
        SoftRumbleChance = new TunedValue<float>(52f);
        IndoorThunderVolume = new TunedValue<int>(48);
        ThunderDelayMinimum = new TunedValue<float>(0.45f);
        ThunderDelayMaximum = new TunedValue<float>(4.5f);
        ThunderDurationMinimum = new TunedValue<float>(7f);
        ThunderDurationMaximum = new TunedValue<float>(16f);
        ThunderFadeOutMinimum = new TunedValue<float>(4f);
        ThunderFadeOutMaximum = new TunedValue<float>(9f);
        ThunderSpatialBlend = new TunedValue<int>(68);
        NearStrikeInterferenceIntensity = new TunedValue<float>(() => BodycamInterferenceIntensity.Value);
        NearStrikeInterferenceDuration = new TunedValue<float>(0.9f);
        StrongLightningInterferenceChance = new TunedValue<float>(35f);
        StrongLightningInterferenceIntensity = new TunedValue<float>(
            () => Mathf.Clamp01(BodycamInterferenceIntensity.Value * 0.68f));
        StrongLightningInterferenceDuration = new TunedValue<float>(0.55f);
        NearStrikeTinnitusDistance = new TunedValue<float>(70f);

        RainVolume.SettingChanged += (_, _) => refreshAudio();
        SnowVolume.SettingChanged += (_, _) => refreshAudio();
        WindVolume.SettingChanged += (_, _) => refreshAudio();
    }

    private static ConfigEntry<float> BindRange(ConfigFile config, string section, string name,
        float defaultValue, float minimum, float maximum, string description)
    {
        return config.Bind(section, name, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<float>(minimum, maximum)));
    }

    private static ConfigEntry<int> BindRange(ConfigFile config, string section, string name,
        int defaultValue, int minimum, int maximum, string description)
    {
        return config.Bind(section, name, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<int>(minimum, maximum)));
    }
}
