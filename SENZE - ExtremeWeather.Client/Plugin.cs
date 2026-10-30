using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Audio.AmbientSubsystem;
using BepInEx;
using Comfort.Common;
using EFT;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace SenzeExtremeWeather;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.SPT.core", "4.1.6")]
[BepInDependency("com.senze.bodycam", BepInDependency.DependencyFlags.SoftDependency)]
[BepInIncompatibility("com.samswat.timeweatherchanger")]
[BepInIncompatibility("com.killdg.returnofthunderstorms")]
[BepInProcess("EscapeFromTarkov.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string PluginGuid = "com.senze.extremeweather";
    internal const string PluginName = "Senze - Extreme Weather";
    internal const string PluginVersion = "1.3.8";

    internal static Plugin Instance { get; private set; }
    internal static ExtremeWeatherConfig Settings { get; private set; }

    private Harmony _harmony;
    private ThunderAudio _thunderAudio;
    private LightningEffects _lightning;
    private RainEnhancer _rainEnhancer;
    private SnowEnhancer _snowEnhancer;
    private WindEnhancer _windEnhancer;
    private SevereWeatherOverlay _weatherOverlay;
    private BodycamInterferenceBridge _bodycamInterference;
    private WeatherControlService _weatherControls;
    private WeatherControlMenu _weatherMenu;
    private bool _wasInRaid;
    private bool _extremeEffectsRequested;
    private bool _extremeActivationQueued;
    private bool _botVisionLimitLogged;

    internal static bool TryGetMaximumStormBotVision(out float maximumDistance)
    {
        maximumDistance = 0f;
        if (Instance == null || Settings == null || !Settings.Enabled.Value)
        {
            return false;
        }

        maximumDistance = Settings.MaximumStormBotVision.Value;
        if (maximumDistance <= 0f)
        {
            return false;
        }

        var controller = WeatherController.Instance;
        var debug = controller ? controller.WeatherDebug : null;
        if (debug == null || !debug.Enabled)
        {
            return false;
        }

        var maximumFog = debug.Fog >= 0.34f;
        var maximumPrecipitation = debug.Rain >= 0.99f
            && (Instance._extremeEffectsRequested
                || Instance._weatherControls?.MaximumStormIntensityActive == true);
        return maximumFog || maximumPrecipitation;
    }

    public void Awake()
    {
        Instance = this;
        if (!IsSupportedSptVersion(out var detectedVersion))
        {
            Logger.LogError($"{PluginName} supports SPT 4.1.6 up to, but not including, SPT 4.2.0. Detected: {detectedVersion}.");
            enabled = false;
            return;
        }

        Settings = new ExtremeWeatherConfig(Config, RefreshNativeWeatherAudio);
        _weatherControls = new WeatherControlService(Settings, Logger, ActivateExtremeEffectsNow);
        _thunderAudio = new ThunderAudio(Settings, Logger, this);
        _weatherOverlay = gameObject.AddComponent<SevereWeatherOverlay>();
        _weatherOverlay.Initialize(Settings, Logger);
        _bodycamInterference = new BodycamInterferenceBridge(Logger);
        _lightning = new LightningEffects(
            Settings,
            _thunderAudio,
            _weatherOverlay,
            _bodycamInterference,
            Logger);
        _weatherMenu = new WeatherControlMenu(_weatherControls, _lightning);
        _rainEnhancer = new RainEnhancer();
        _snowEnhancer = new SnowEnhancer();
        _windEnhancer = new WindEnhancer();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Plugin).Assembly);

        var audioDirectory = Path.Combine(Path.GetDirectoryName(Info.Location) ?? BepInEx.Paths.PluginPath, "Audio");
        var lightningTextureDirectory = Path.Combine(
            Path.GetDirectoryName(Info.Location) ?? BepInEx.Paths.PluginPath,
            "Lightning Bolt Textures");
        StartCoroutine(_thunderAudio.LoadClips(audioDirectory));
        StartCoroutine(_weatherOverlay.LoadLightningTextures(lightningTextureDirectory));
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
    }

    public void Update()
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        bool inRaid = gameWorld;
        if (!_wasInRaid && inRaid)
        {
            _weatherControls.BeginRaid();
            _botVisionLimitLogged = false;
            Logger.LogInfo("Raid detected; menu input is active.");
        }

        if (_wasInRaid && !inRaid)
        {
            _weatherMenu.Reset();
            _weatherControls.ResetRaidState();
            _lightning.Reset();
            _rainEnhancer.Restore();
            _snowEnhancer.Restore();
            _windEnhancer.Restore();
            _weatherOverlay.ResetEffects();
            _extremeEffectsRequested = false;
            _botVisionLimitLogged = false;
        }
        _wasInRaid = inRaid;

        if (inRaid)
        {
            _weatherControls.UpdateAutomaticStorm();

            var botVisionLimited = TryGetMaximumStormBotVision(out var botVisionDistance);
            if (botVisionLimited != _botVisionLimitLogged)
            {
                Logger.LogInfo(botVisionLimited
                    ? $"Maximum-storm bot vision cap activated at {botVisionDistance:0} metres."
                    : "Maximum-storm bot vision cap released.");
                _botVisionLimitLogged = botVisionLimited;
            }
        }

        var shortcut = Settings.MenuToggleKey.Value;
        if (shortcut.MainKey != KeyCode.None && shortcut.IsDown())
        {
            Logger.LogInfo($"Menu key detected: {shortcut.MainKey}.");
            if (!inRaid)
            {
                Logger.LogWarning("Menu was not opened because no raid is active.");
                return;
            }

            if (!WeatherController.Instance)
            {
                Logger.LogWarning("Menu was not opened because this location has no dynamic WeatherController.");
                return;
            }

            _weatherMenu.SetVisible(!_weatherMenu.IsVisible);
            Logger.LogInfo($"Menu visible: {_weatherMenu.IsVisible}.");
        }

        ProcessQueuedEffects();
        UpdateStormEffects();
    }

    public void OnGUI()
    {
        _weatherMenu?.Draw();
    }

    private void UpdateStormEffects()
    {
        var controller = WeatherController.Instance;
        if (!Settings.Enabled.Value || !controller || controller.WeatherDebug == null)
        {
            _lightning.Update(0f, false);
            _rainEnhancer.Restore();
            _snowEnhancer.Restore();
            _windEnhancer.Restore();
            return;
        }

        var debug = controller.WeatherDebug;
        var controlledWeather = debug.Enabled;
        if (_extremeEffectsRequested
            && (!controlledWeather || debug.Rain < 0.8f || debug.LightningThunderProbability < 0.01f))
        {
            _extremeEffectsRequested = false;
        }

        var rainStrength = controlledWeather ? Mathf.Clamp01(debug.Rain) : 0f;
        var winter = WeatherSeason.IsWinter;
        var automaticSequence = _weatherControls.AutomaticSequenceControlsEffects;
        var automaticEffectStrength = _weatherControls.AutomaticEffectStrength;
        var rainEnhancementStrength = automaticSequence
            ? automaticEffectStrength
            : rainStrength;
        var lightningStrength = automaticSequence
            ? automaticEffectStrength
            : controlledWeather
                ? Mathf.Max(Mathf.Clamp01(debug.LightningThunderProbability), rainStrength)
                : 0f;
        var lightningActive = controlledWeather
            && Settings.LightningEnabled.Value
            && (!winter || Settings.ThundersnowEnabled.Value)
            && (automaticSequence
                ? _weatherControls.AutomaticThunderActive
                : _extremeEffectsRequested
                    || (debug.CloudDensity >= Settings.LightningCloudThreshold.Value - 0.01f
                        && debug.LightningThunderProbability > 0.01f));

        if (controlledWeather && rainStrength > 0.01f && rainEnhancementStrength > 0.001f)
        {
            if (winter)
            {
                _rainEnhancer.Restore();
                _snowEnhancer.Apply(
                    controller,
                    rainEnhancementStrength,
                    Settings.ExtraSnowVisuals.Value,
                    Settings.StormWindVisualBoost.Value);
            }
            else
            {
                _snowEnhancer.Restore();
                _rainEnhancer.Apply(controller, rainEnhancementStrength, Settings.ExtraRainVisuals.Value,
                    Settings.ExtraRainMaximumDensityMultiplier.Value);
            }
        }
        else
        {
            _rainEnhancer.Restore();
            _snowEnhancer.Restore();
        }

        var windEnhancementStrength = automaticSequence
            ? automaticEffectStrength
            : _extremeEffectsRequested
                ? 1f
                : 0f;
        if (controlledWeather && windEnhancementStrength > 0.001f)
        {
            _windEnhancer.Apply(
                controller,
                windEnhancementStrength,
                Settings.StormWindVisualBoost.Value,
                winter);
        }
        else
        {
            _windEnhancer.Restore();
        }

        _lightning.Update(lightningStrength, lightningActive);
    }

    internal static void ActivateExtremeEffectsNow()
    {
        if (Instance == null)
        {
            return;
        }

        Instance._extremeActivationQueued = true;
    }

    internal void ReapplyEnhancedWind()
    {
        _windEnhancer?.ReapplyShaderWind();
    }

    private void ProcessQueuedEffects()
    {
        if (_extremeActivationQueued)
        {
            _extremeActivationQueued = false;
            _extremeEffectsRequested = true;

            var controller = WeatherController.Instance;
            if (WeatherSeason.IsWinter)
            {
                _snowEnhancer.Apply(
                    controller,
                    1f,
                    Settings.ExtraSnowVisuals.Value,
                    Settings.StormWindVisualBoost.Value);
            }
            else
            {
                _rainEnhancer.Apply(controller, 1f, Settings.ExtraRainVisuals.Value,
                    Settings.ExtraRainMaximumDensityMultiplier.Value);
            }
            if (!WeatherSeason.IsWinter || Settings.ThundersnowEnabled.Value)
            {
                _lightning.TriggerNow(1f);
            }

            if (WeatherSeason.IsWinter)
            {
                var snow = controller && controller.RainController != null
                    ? controller.RainController._snowFlakes
                    : null;
                var intensity = snow != null ? snow.Intensity.ToString("F2") : "unavailable";
                var stormFactor = snow != null ? snow.StormFactor.ToString("F2") : "unavailable";
                Logger.LogInfo(
                    $"Extreme effects processed in Update: extraSnow={Settings.ExtraSnowVisuals.Value:0}%, intensity={intensity}, stormFactor={stormFactor}.");
            }
            else
            {
                var rainDrops = controller && controller.RainController != null
                    ? controller.RainController._rainFallDrops
                    : null;
                var splashesReady = controller && controller.RainController != null
                    && controller.RainController._rainSplashController != null;
                var density = rainDrops != null ? rainDrops.MinMaxDensity.ToString("F2") : "unavailable";
                Logger.LogInfo(
                    $"Extreme effects processed in Update: extraRain={Settings.ExtraRainVisuals.Value:0}%, density={density}, splashes={splashesReady}.");
            }
        }

    }

    private void OnDestroy()
    {
        _weatherMenu?.Reset();
        _rainEnhancer?.Restore();
        _snowEnhancer?.Restore();
        _windEnhancer?.Restore();
        _lightning?.Dispose();
        _thunderAudio?.Dispose();
        _harmony?.UnpatchSelf();
        Settings = null;
        Instance = null;
    }

    private static bool IsSupportedSptVersion(out System.Version detectedVersion)
    {
        detectedVersion = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "spt-core", StringComparison.OrdinalIgnoreCase))
            ?.GetName().Version;

        if (detectedVersion == null)
        {
            try
            {
                var assemblyPath = Path.Combine(BepInEx.Paths.PluginPath, "spt", "spt-core.dll");
                detectedVersion = AssemblyName.GetAssemblyName(assemblyPath).Version;
            }
            catch
            {
                return false;
            }
        }

        return detectedVersion.CompareTo(new System.Version(4, 1, 6)) >= 0
            && detectedVersion.CompareTo(new System.Version(4, 2, 0)) < 0;
    }

    private static void RefreshNativeWeatherAudio()
    {
        foreach (var rainBlender in UnityEngine.Object.FindObjectsOfType<PrecipitationAmbientBlender>())
        {
            rainBlender.StartCrossfader(0.2f, 1f);
        }

        foreach (var windBlender in UnityEngine.Object.FindObjectsOfType<WindAmbientBlender>())
        {
            windBlender.UpdateWindSound(true);
        }
    }
}
