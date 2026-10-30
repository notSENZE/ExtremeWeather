using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Comfort.Common;
using EFT.Ballistics;
using EFT.Weather;
using Systems.Effects;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace SenzeExtremeWeather;

internal sealed class SevereWeatherOverlay : MonoBehaviour
{
    private sealed class LightningTexture
    {
        internal Texture2D Texture;
        internal Vector2 EndpointUv;
    }

    private sealed class NativeParticleState
    {
        internal ParticleSystemAdapter Adapter;
        internal float SizeMinimum;
        internal float SizeRange;
        internal float LifetimeMinimum;
        internal float LifetimeRange;
        internal bool HasSize;
        internal bool HasLifetime;
    }

    private sealed class NativeParticleSystemState
    {
        internal ParticleSystem ParticleSystem;
        internal float SizeMultiplier;
        internal float LifetimeMultiplier;
        internal bool HasLifetime;
    }

    private const int MaximumLightningTextureHeight = 1024;
    private const string NativeLauncherEffect = "smallgrenade_expl";
    private static readonly HashSet<string> NearStrikeTextureNames = new HashSet<string>(
        System.StringComparer.OrdinalIgnoreCase)
    {
        "35", "36", "37", "60", "65", "66",
        "67", "70", "71", "72", "73", "74",
    };

    private ExtremeWeatherConfig _settings;
    private ManualLogSource _logger;
    private Canvas _canvas;
    private Image _flashImage;
    private Image _visionEffectImage;
    private readonly List<LightningTexture> _lightningTextures = new List<LightningTexture>();
    private GameObject _lightningObject;
    private MeshRenderer _lightningRenderer;
    private Material _lightningMaterial;
    private float _flashStartedAt = -1f;
    private float _nextDischargeAt;
    private float _flashBrightness;
    private int _remainingDischarges;
    private bool _nearStrikeQueued;
    private bool _nearStrikeImpactEnabled;
    private bool _distantLightningPlanned;
    private Vector3 _distantLightningGround;
    private float _distantLightningDistance;
    private Vector3 _nearStrikePoint;
    private Vector3 _nearStrikeNormal;
    private BallisticCollider _nearStrikeBallisticCollider;
    private float _nearStrikeDistance;
    private string _nearStrikeSurface;
    private List<NativeParticleState> _activeNativeParticleStates;
    private List<NativeParticleSystemState> _activeNativeParticleSystemStates;
    private bool _nativeImpactTuningActive;
    private int _extendedNativeParticleSystems;
    private bool _missingNearStrikeTexturesLogged;
    private float _visionEffectPendingAt = -1f;
    private float _visionEffectPendingStrength;
    private float _visionEffectStartedAt = -1f;
    private float _visionEffectDuration;
    private float _visionEffectStrength;

    internal void Initialize(ExtremeWeatherConfig settings, ManualLogSource logger)
    {
        _settings = settings;
        _logger = logger;
        BuildCanvas();
        BuildLightningBillboard();
    }

    internal IEnumerator LoadLightningTextures(string textureDirectory)
    {
        if (!Directory.Exists(textureDirectory))
        {
            _logger.LogWarning($"Lightning texture directory was not found: {textureDirectory}");
            yield break;
        }

        var paths = Directory.GetFiles(textureDirectory, "*.png");
        System.Array.Sort(paths, System.StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            using (var request = UnityWebRequestTexture.GetTexture(new System.Uri(path).AbsoluteUri, true))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _logger.LogWarning($"Could not load lightning texture '{Path.GetFileName(path)}': {request.error}");
                    continue;
                }

                var original = DownloadHandlerTexture.GetContent(request);
                if (original == null)
                {
                    continue;
                }

                original.name = Path.GetFileNameWithoutExtension(path);
                var texture = DownscaleTexture(original, MaximumLightningTextureHeight);
                Object.Destroy(original);
                if (texture != null)
                {
                    texture.name = Path.GetFileNameWithoutExtension(path);
                    var endpointUv = FindLightningEndpoint(texture);
                    texture.Apply(false, true);
                    _lightningTextures.Add(new LightningTexture
                    {
                        Texture = texture,
                        EndpointUv = endpointUv,
                    });
                }
            }

            yield return null;
        }

        _logger.LogInfo($"Loaded {_lightningTextures.Count} world-space lightning texture(s).");
    }

    internal void TriggerFlash(float brightness)
    {
        _nearStrikeQueued = false;
        PlanDistantLightning();
        _flashBrightness = Mathf.Max(0f, brightness);
        var chanceRoll = Random.Range(0f, 100f);
        if (chanceRoll < _settings.TripleDischargeChance.Value)
        {
            _remainingDischarges = 3;
        }
        else if (chanceRoll < _settings.TripleDischargeChance.Value + _settings.DoubleDischargeChance.Value)
        {
            _remainingDischarges = 2;
        }
        else
        {
            _remainingDischarges = 1;
        }
        _nextDischargeAt = Time.unscaledTime;
        _flashStartedAt = -1f;
        _logger.LogInfo($"Lightning burst started: {_remainingDischarges} discharge(s), brightness={_flashBrightness:0.00}.");
    }

    internal bool TriggerNearStrike(float brightness, bool createImpactEffect, bool forceVisible)
    {
        if (!TryFindNearStrikePoint(
                forceVisible,
                out _nearStrikePoint,
                out _nearStrikeNormal,
                out _nearStrikeBallisticCollider,
                out _nearStrikeDistance,
                out _nearStrikeSurface))
        {
            return false;
        }

        _nearStrikeQueued = true;
        _distantLightningPlanned = false;
        _nearStrikeImpactEnabled = createImpactEffect;
        _flashBrightness = Mathf.Max(0f, brightness);
        _remainingDischarges = 1;
        _nextDischargeAt = Time.unscaledTime;
        _flashStartedAt = -1f;
        _logger.LogInfo(
            $"Near-strike flash queued at {_nearStrikeDistance:0} metres on '{_nearStrikeSurface}', "
            + $"brightness={_flashBrightness:0.00}, forceVisible={forceVisible}.");
        return true;
    }

    internal bool TryGetThunderOrigin(out Vector3 position, out float distance)
    {
        if (_nearStrikeQueued)
        {
            position = _nearStrikePoint;
            distance = _nearStrikeDistance;
            return true;
        }
        if (_distantLightningPlanned)
        {
            position = _distantLightningGround;
            distance = _distantLightningDistance;
            return true;
        }

        position = Vector3.zero;
        distance = 0f;
        return false;
    }

    internal void ResetEffects()
    {
        _flashStartedAt = -1f;
        _remainingDischarges = 0;
        _nearStrikeQueued = false;
        _distantLightningPlanned = false;
        if (_flashImage != null)
        {
            _flashImage.color = new Color(0.79f, 0.88f, 1f, 0f);
        }
        _visionEffectStartedAt = -1f;
        _visionEffectPendingAt = -1f;
        if (_visionEffectImage != null)
        {
            _visionEffectImage.color = new Color(0.82f, 0.9f, 1f, 0f);
        }
        if (_lightningObject != null)
        {
            _lightningObject.SetActive(false);
        }
    }

    private void Update()
    {
        if (_flashImage == null)
        {
            return;
        }

        UpdateFlash();
        UpdateVisionEffect();
    }

    internal void TriggerNearStrikeVisionEffectNow()
    {
        StartVisionEffect(1f);
    }

    private void UpdateFlash()
    {
        if (!_settings.LightningEnabled.Value)
        {
            _flashImage.color = new Color(0.79f, 0.88f, 1f, 0f);
            _remainingDischarges = 0;
            if (_lightningObject != null)
            {
                _lightningObject.SetActive(false);
            }
            return;
        }

        if (_flashStartedAt < 0f)
        {
            if (_remainingDischarges > 0 && Time.unscaledTime >= _nextDischargeAt)
            {
                StartNextDischarge();
            }
            return;
        }

        var elapsed = Time.unscaledTime - _flashStartedAt;
        float pulse;
        if (elapsed < 0.025f)
        {
            pulse = elapsed / 0.025f;
        }
        else if (elapsed < 0.14f)
        {
            pulse = 1f - (elapsed - 0.025f) / 0.115f;
        }
        else
        {
            FinishDischarge();
            return;
        }

        var coverMultiplier = RainController.IsCameraUnderRain ? 1f : _settings.IndoorFlashMultiplier.Value;
        var ambientAlpha = Mathf.Clamp01(pulse * _flashBrightness * coverMultiplier * 0.022f);
        _flashImage.color = new Color(0.79f, 0.88f, 1f, ambientAlpha);

        if (_lightningMaterial != null && _lightningObject.activeSelf)
        {
            var boltAlpha = Mathf.Clamp01(pulse * _flashBrightness * 0.78f);
            _lightningMaterial.color = new Color(0.82f, 0.91f, 1f, boltAlpha);
        }
    }

    private void StartNextDischarge()
    {
        _remainingDischarges--;
        _flashStartedAt = Time.unscaledTime;
        if (_nearStrikeQueued)
        {
            PlaceNearStrikeBillboard();
            TriggerNearStrikeVisionEffect();
        }
        else
        {
            PlaceLightningBillboard();
        }
    }

    private void FinishDischarge()
    {
        _flashStartedAt = -1f;
        _flashImage.color = new Color(0.79f, 0.88f, 1f, 0f);
        if (_lightningObject != null)
        {
            _lightningObject.SetActive(false);
        }

        if (_remainingDischarges <= 0)
        {
            FinishFlash();
            return;
        }

        var minimumGap = Mathf.Min(_settings.BurstGapMinimum.Value, _settings.BurstGapMaximum.Value);
        var maximumGap = Mathf.Max(_settings.BurstGapMinimum.Value, _settings.BurstGapMaximum.Value);
        _nextDischargeAt = Time.unscaledTime + Random.Range(minimumGap, maximumGap);
    }

    private void FinishFlash()
    {
        _flashStartedAt = -1f;
        _remainingDischarges = 0;
        _nearStrikeQueued = false;
        _flashImage.color = new Color(0.79f, 0.88f, 1f, 0f);
        if (_lightningObject != null)
        {
            _lightningObject.SetActive(false);
        }
    }

    private void TriggerNearStrikeVisionEffect()
    {
        if (!_settings.NearStrikeVisionEnabled.Value)
        {
            return;
        }

        var camera = FindGameCamera();
        if (camera == null)
        {
            return;
        }

        var directionToStrike = (_nearStrikePoint - camera.transform.position).normalized;
        var facing = Mathf.InverseLerp(-0.35f, 0.75f,
            Vector3.Dot(camera.transform.forward, directionToStrike));
        var distance = Mathf.InverseLerp(
            _settings.NearStrikeDistanceMaximum.Value,
            _settings.NearStrikeDistanceMinimum.Value,
            _nearStrikeDistance);
        var viewMultiplier = Mathf.Lerp(0.28f, 1f, facing);
        var distanceMultiplier = Mathf.Lerp(0.75f, 1f, distance);
        ScheduleVisionEffect(viewMultiplier * distanceMultiplier);
    }

    private void ScheduleVisionEffect(float eventStrength)
    {
        if (!_settings.NearStrikeVisionEnabled.Value || _visionEffectImage == null)
        {
            return;
        }

        _visionEffectPendingStrength = eventStrength;
        _visionEffectPendingAt = Time.unscaledTime + 0.16f;
        _visionEffectStartedAt = -1f;
        _visionEffectImage.color = new Color(0.82f, 0.9f, 1f, 0f);
    }

    private void StartVisionEffect(float eventStrength)
    {
        if (!_settings.NearStrikeVisionEnabled.Value || _visionEffectImage == null)
        {
            return;
        }

        _visionEffectStrength = Mathf.Clamp(
            eventStrength * _settings.NearStrikeVisionStrength.Value / 100f,
            0f,
            2f);
        _visionEffectDuration = _settings.NearStrikeVisionDuration.Value;
        _visionEffectStartedAt = Time.unscaledTime;
    }

    private void UpdateVisionEffect()
    {
        if (_visionEffectImage == null)
        {
            return;
        }

        if (!_settings.NearStrikeVisionEnabled.Value)
        {
            _visionEffectPendingAt = -1f;
            _visionEffectStartedAt = -1f;
            _visionEffectImage.color = new Color(0.82f, 0.9f, 1f, 0f);
            return;
        }

        if (_visionEffectPendingAt >= 0f)
        {
            if (Time.unscaledTime < _visionEffectPendingAt)
            {
                return;
            }

            var pendingStrength = _visionEffectPendingStrength;
            _visionEffectPendingAt = -1f;
            StartVisionEffect(pendingStrength);
        }

        if (_visionEffectStartedAt < 0f)
        {
            return;
        }

        var elapsed = Time.unscaledTime - _visionEffectStartedAt;
        if (elapsed >= _visionEffectDuration)
        {
            _visionEffectStartedAt = -1f;
            _visionEffectImage.color = new Color(0.82f, 0.9f, 1f, 0f);
            return;
        }

        const float initialWashoutDuration = 0.1f;
        float alpha;
        Color color;
        if (elapsed < initialWashoutDuration)
        {
            var pulse = 1f - elapsed / initialWashoutDuration * 0.18f;
            alpha = pulse * 0.68f * _visionEffectStrength;
            color = new Color(0.92f, 0.96f, 1f, 1f);
        }
        else
        {
            var fade = 1f - Mathf.InverseLerp(
                initialWashoutDuration,
                _visionEffectDuration,
                elapsed);
            alpha = Mathf.Pow(fade, 1.65f) * 0.34f * _visionEffectStrength;
            color = new Color(0.72f, 0.82f, 0.92f, 1f);
        }

        color.a = Mathf.Clamp01(alpha);
        _visionEffectImage.color = color;
    }

    private void PlaceLightningBillboard()
    {
        var camera = FindGameCamera();
        if (camera == null || _lightningTextures.Count == 0 || _lightningObject == null)
        {
            if (_lightningTextures.Count == 0)
            {
                _logger.LogWarning("No lightning textures are ready for the world-space bolt.");
            }
            return;
        }

        if (!_distantLightningPlanned && !PlanDistantLightning())
        {
            return;
        }

        var lightningTexture = _lightningTextures[Random.Range(0, _lightningTextures.Count)];
        var texture = lightningTexture.Texture;
        var height = Random.Range(230f, 340f);
        var aspect = (float)texture.width / texture.height;
        var position = _distantLightningGround + Vector3.up * (height * 0.5f);
        var facing = Quaternion.LookRotation(camera.transform.position - position, Vector3.up);

        _lightningObject.transform.position = position;
        _lightningObject.transform.rotation = facing * Quaternion.Euler(0f, 0f, Random.Range(-7f, 7f));
        _lightningObject.transform.localScale = new Vector3(height * aspect, height, 1f);
        AlignLightningEndpoint(_lightningObject.transform, lightningTexture.EndpointUv, _distantLightningGround);
        _lightningMaterial.mainTexture = texture;
        _lightningMaterial.color = new Color(0.82f, 0.91f, 1f, 0f);
        _lightningObject.SetActive(true);
        _logger.LogInfo($"Texture lightning '{texture.name}' placed at {_distantLightningDistance:0} metres.");
    }

    private bool PlanDistantLightning()
    {
        var camera = FindGameCamera();
        if (camera == null)
        {
            _distantLightningPlanned = false;
            return false;
        }

        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.1f)
        {
            forward = Vector3.forward;
        }

        var direction = Quaternion.AngleAxis(Random.Range(-35f, 35f), Vector3.up) * forward;
        _distantLightningDistance = Random.Range(280f, 560f);
        _distantLightningGround = camera.transform.position
            + direction * _distantLightningDistance
            - Vector3.up * 20f;
        var rayStart = camera.transform.position
            + direction * _distantLightningDistance
            + Vector3.up * 420f;
        if (Physics.Raycast(rayStart, Vector3.down, out var hit, 850f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            _distantLightningGround = hit.point;
        }

        _distantLightningPlanned = true;
        return true;
    }

    private void PlaceNearStrikeBillboard()
    {
        var camera = FindGameCamera();
        if (camera == null || _lightningTextures.Count == 0 || _lightningObject == null)
        {
            _logger.LogWarning("Near-strike lightning could not be rendered because its camera, texture or renderer is unavailable.");
            return;
        }

        var lightningTexture = SelectNearStrikeTexture();
        var texture = lightningTexture.Texture;
        var height = Mathf.Clamp(
            _nearStrikeDistance * Random.Range(1.15f, 1.45f),
            90f,
            180f);
        var aspect = (float)texture.width / texture.height;
        var position = _nearStrikePoint + Vector3.up * (height * 0.5f);
        var cameraDirection = camera.transform.position - position;
        cameraDirection.y = 0f;
        if (cameraDirection.sqrMagnitude < 0.1f)
        {
            cameraDirection = Vector3.back;
        }

        _lightningObject.transform.position = position;
        _lightningObject.transform.rotation = Quaternion.LookRotation(cameraDirection.normalized, Vector3.up)
            * Quaternion.Euler(0f, 0f, Random.Range(-4f, 4f));
        _lightningObject.transform.localScale = new Vector3(height * aspect, height, 1f);
        AlignLightningEndpoint(_lightningObject.transform, lightningTexture.EndpointUv, _nearStrikePoint);
        _lightningMaterial.mainTexture = texture;
        _lightningMaterial.color = new Color(0.88f, 0.95f, 1f, 0f);
        _lightningObject.SetActive(true);

        if (_nearStrikeImpactEnabled)
        {
            CreateNativeImpactEffect(
                _nearStrikePoint,
                _nearStrikeNormal,
                _nearStrikeBallisticCollider);
        }

        _logger.LogInfo(
            $"Near lightning texture '{texture.name}' struck '{_nearStrikeSurface}' at {_nearStrikeDistance:0} metres.");
    }

    private bool TryFindNearStrikePoint(
        bool forceVisible,
        out Vector3 point,
        out Vector3 normal,
        out BallisticCollider ballisticCollider,
        out float distance,
        out string surface)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        ballisticCollider = null;
        distance = 0f;
        surface = "unknown";

        var camera = FindGameCamera();
        if (camera == null)
        {
            _logger.LogWarning("Near strike was skipped because no game camera is available.");
            return false;
        }
        if (!HasOpenSky(camera.transform.position + Vector3.up * 0.2f))
        {
            _logger.LogInfo("Near strike was skipped because the player is not under open sky.");
            return false;
        }

        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.1f)
        {
            forward = Vector3.forward;
        }

        var configuredMinimumDistance = Mathf.Min(
            _settings.NearStrikeDistanceMinimum.Value,
            _settings.NearStrikeDistanceMaximum.Value);
        var configuredMaximumDistance = Mathf.Max(
            _settings.NearStrikeDistanceMinimum.Value,
            _settings.NearStrikeDistanceMaximum.Value);
        var minimumDistance = Mathf.Max(
            configuredMinimumDistance,
            _settings.NearStrikeCameraSafetyDistance.Value);
        var maximumDistance = Mathf.Max(configuredMaximumDistance, minimumDistance + 10f);

        var maximumAngle = forceVisible ? 18f : 72f;
        var maximumAttempts = forceVisible ? 32 : 16;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var direction = Quaternion.AngleAxis(
                Random.Range(-maximumAngle, maximumAngle),
                Vector3.up) * forward;
            var candidateDistance = Random.Range(minimumDistance, maximumDistance);
            var rayStart = camera.transform.position + direction * candidateDistance + Vector3.up * 300f;
            if (!Physics.Raycast(rayStart, Vector3.down, out var hit, 650f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                continue;
            }
            if (!HasOpenSky(hit.point + Vector3.up * 0.4f))
            {
                continue;
            }
            if (hit.normal.y < 0.5f)
            {
                continue;
            }
            if (forceVisible)
            {
                var viewportPoint = camera.WorldToViewportPoint(hit.point + Vector3.up * 1.5f);
                if (viewportPoint.z <= 0f
                    || viewportPoint.x < 0.15f
                    || viewportPoint.x > 0.85f
                    || viewportPoint.y < 0.08f
                    || viewportPoint.y > 0.82f)
                {
                    continue;
                }
            }

            var hitBallisticCollider = hit.collider != null
                ? hit.collider.GetComponentInParent<BallisticCollider>()
                : null;
            if (hitBallisticCollider != null
                && (hitBallisticCollider.TypeOfMaterial == MaterialType.Water
                    || hitBallisticCollider.TypeOfMaterial == MaterialType.WaterPuddle))
            {
                continue;
            }

            point = hit.point;
            normal = hit.normal;
            ballisticCollider = hitBallisticCollider;
            distance = Vector3.ProjectOnPlane(point - camera.transform.position, Vector3.up).magnitude;
            surface = hit.collider != null ? hit.collider.name : "unknown";
            return true;
        }

        _logger.LogWarning(forceVisible
            ? "Debug near strike was skipped because no visible open-sky ground point could be found in front of the camera."
            : "Near strike was skipped because no open-sky impact point could be found nearby.");
        return false;
    }

    private static bool HasOpenSky(Vector3 position)
    {
        return !Physics.Raycast(position, Vector3.up, 400f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }

    private bool CreateNativeImpactEffect(
        Vector3 point,
        Vector3 normal,
        BallisticCollider ballisticCollider)
    {
        if (!Singleton<Effects>.Instantiated)
        {
            _logger.LogWarning(
                $"Native impact '{NativeLauncherEffect}' is unavailable because Tarkov's effect system is not ready.");
            return false;
        }

        var effects = Singleton<Effects>.Instance;
        var nativeEffect = FindNativeEffect(effects);
        if (nativeEffect == null)
        {
            _logger.LogWarning(
                $"Native impact '{NativeLauncherEffect}' was not found in Tarkov's effect table.");
            return false;
        }

        if (!_nativeImpactTuningActive)
        {
            _activeNativeParticleStates = TuneNativeImpact(
                nativeEffect,
                out _activeNativeParticleSystemStates,
                out _extendedNativeParticleSystems);
            _nativeImpactTuningActive = true;
            StartCoroutine(RestoreNativeImpactAfterEffectsUpdate());
        }

        var emissionCount = Mathf.Clamp(
            1 + Mathf.RoundToInt(
                (_settings.NearStrikeImpactStrength.Value - 100f) / 50f),
            1,
            5);
        for (var index = 0; index < emissionCount; index++)
        {
            effects.EmitGrenade(NativeLauncherEffect, point, normal, 0f);
        }

        if (effects.DeferredDecals != null)
        {
            effects.DeferredDecals.DrawDecal(
                point + normal * 0.015f,
                normal,
                ballisticCollider,
                true);
        }
        else if (nativeEffect.Decal != null)
        {
            nativeEffect.Decal.Add(point + normal * 0.015f, normal);
        }
        else
        {
            _logger.LogWarning("Tarkov's native grenade decal renderer is unavailable for this impact.");
        }

        _logger.LogInfo(
            $"Native 40 mm/AGS impact queued {emissionCount} time(s) at "
            + $"{_settings.NearStrikeImpactStrength.Value}% strength; extended "
            + $"{_extendedNativeParticleSystems} long-lived particle system(s) to "
            + $"{_settings.NearStrikeSmokeLifetime.Value}% lifetime.");
        return true;
    }

    private static Effects.Effect FindNativeEffect(Effects effects)
    {
        if (effects.EffectsArray == null)
        {
            return null;
        }

        foreach (var effect in effects.EffectsArray)
        {
            if (effect != null && effect.Name == NativeLauncherEffect)
            {
                return effect;
            }
        }

        return null;
    }

    private List<NativeParticleState> TuneNativeImpact(
        Effects.Effect effect,
        out List<NativeParticleSystemState> particleSystemStates,
        out int extendedParticleSystems)
    {
        var states = new List<NativeParticleState>();
        particleSystemStates = new List<NativeParticleSystemState>();
        extendedParticleSystems = 0;
        var strengthMultiplier = _settings.NearStrikeImpactStrength.Value / 100f;
        var sizeMultiplier = Mathf.Lerp(1f, 1.6f, Mathf.InverseLerp(1f, 3f, strengthMultiplier));
        var configuredLifetimeMultiplier = _settings.NearStrikeSmokeLifetime.Value / 100f;
        var lifetimeMultiplier = 1f + Mathf.Max(0f, configuredLifetimeMultiplier - 1f) * 2f;

        if (effect.Particles != null)
        {
            foreach (var particle in effect.Particles)
            {
                if (particle == null)
                {
                    continue;
                }

                var state = new NativeParticleState
                {
                    Adapter = particle.Particle as ParticleSystemAdapter,
                };

                if (state.Adapter != null && state.Adapter.Size != null)
                {
                    state.HasSize = true;
                    state.SizeMinimum = state.Adapter.Size.Min;
                    state.SizeRange = state.Adapter.Size.Range;
                    state.Adapter.Size.Min *= sizeMultiplier;
                    state.Adapter.Size.Range *= sizeMultiplier;
                }

                if (state.Adapter != null && state.Adapter.Lifetime != null)
                {
                    var longestLifetime = state.Adapter.Lifetime.Min + state.Adapter.Lifetime.Range;
                    if (longestLifetime >= 0.05f)
                    {
                        state.HasLifetime = true;
                        state.LifetimeMinimum = state.Adapter.Lifetime.Min;
                        state.LifetimeRange = state.Adapter.Lifetime.Range;
                        state.Adapter.Lifetime.Min *= lifetimeMultiplier;
                        state.Adapter.Lifetime.Range *= lifetimeMultiplier;
                        extendedParticleSystems++;
                    }
                }

                if (state.HasSize || state.HasLifetime)
                {
                    state.Adapter.FillValues();
                    states.Add(state);
                }
            }
        }

        var mediator = effect.BasicParticleSystemMediator;
        if (mediator == null || mediator._particleSystems == null)
        {
            return states;
        }

        var visited = new HashSet<ParticleSystem>();
        foreach (var rootParticleSystem in mediator._particleSystems)
        {
            if (rootParticleSystem == null)
            {
                continue;
            }

            foreach (var particleSystem in rootParticleSystem.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particleSystem == null || !visited.Add(particleSystem))
                {
                    continue;
                }

                var main = particleSystem.main;
                var state = new NativeParticleSystemState
                {
                    ParticleSystem = particleSystem,
                    SizeMultiplier = main.startSizeMultiplier,
                    LifetimeMultiplier = main.startLifetimeMultiplier,
                };
                main.startSizeMultiplier = state.SizeMultiplier * sizeMultiplier;

                var longestLifetime = main.startLifetime.constantMax * state.LifetimeMultiplier;
                if (longestLifetime >= 0.05f)
                {
                    state.HasLifetime = true;
                    main.startLifetimeMultiplier = state.LifetimeMultiplier * lifetimeMultiplier;
                    extendedParticleSystems++;
                }

                particleSystemStates.Add(state);
            }
        }

        return states;
    }

    private IEnumerator RestoreNativeImpactAfterEffectsUpdate()
    {
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();
        RestoreNativeImpactTuning();
    }

    private void RestoreNativeImpactTuning()
    {
        if (!_nativeImpactTuningActive)
        {
            return;
        }

        foreach (var state in _activeNativeParticleStates)
        {
            if (state.HasSize)
            {
                state.Adapter.Size.Min = state.SizeMinimum;
                state.Adapter.Size.Range = state.SizeRange;
            }

            if (state.HasLifetime)
            {
                state.Adapter.Lifetime.Min = state.LifetimeMinimum;
                state.Adapter.Lifetime.Range = state.LifetimeRange;
            }

            if (state.HasSize || state.HasLifetime)
            {
                state.Adapter.FillValues();
            }
        }

        foreach (var state in _activeNativeParticleSystemStates)
        {
            if (state.ParticleSystem == null)
            {
                continue;
            }

            var main = state.ParticleSystem.main;
            main.startSizeMultiplier = state.SizeMultiplier;
            if (state.HasLifetime)
            {
                main.startLifetimeMultiplier = state.LifetimeMultiplier;
            }
        }

        _activeNativeParticleStates = null;
        _activeNativeParticleSystemStates = null;
        _nativeImpactTuningActive = false;
        _extendedNativeParticleSystems = 0;
    }

    private LightningTexture SelectNearStrikeTexture()
    {
        var matchingTextures = new List<LightningTexture>();
        foreach (var texture in _lightningTextures)
        {
            if (NearStrikeTextureNames.Contains(texture.Texture.name))
            {
                matchingTextures.Add(texture);
            }
        }

        if (matchingTextures.Count > 0)
        {
            _missingNearStrikeTexturesLogged = false;
            return matchingTextures[Random.Range(0, matchingTextures.Count)];
        }

        if (!_missingNearStrikeTexturesLogged)
        {
            _logger.LogWarning(
                "None of the built-in near-strike textures are loaded; all textures remain available as a fallback.");
            _missingNearStrikeTexturesLogged = true;
        }
        return _lightningTextures[Random.Range(0, _lightningTextures.Count)];
    }

    private void BuildLightningBillboard()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            _logger.LogError("Unity's Sprites/Default shader is unavailable; texture lightning cannot be rendered.");
            return;
        }

        _lightningObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _lightningObject.name = "Senze Texture Lightning";
        _lightningObject.transform.SetParent(transform, false);
        var collider = _lightningObject.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        _lightningRenderer = _lightningObject.GetComponent<MeshRenderer>();
        _lightningRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _lightningRenderer.receiveShadows = false;
        _lightningMaterial = new Material(shader)
        {
            name = "Senze Texture Lightning Material",
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = 3100,
        };
        _lightningRenderer.material = _lightningMaterial;
        _lightningObject.SetActive(false);
    }

    private void BuildCanvas()
    {
        var canvasObject = new GameObject("Senze Extreme Weather Overlay");
        canvasObject.transform.SetParent(transform, false);
        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 30000;

        var flashObject = new GameObject("Lightning Flash");
        flashObject.transform.SetParent(canvasObject.transform, false);
        _flashImage = flashObject.AddComponent<Image>();
        _flashImage.raycastTarget = false;
        StretchToCanvas(_flashImage.rectTransform);

        var visionEffectObject = new GameObject("Near Strike Vision Effect");
        visionEffectObject.transform.SetParent(canvasObject.transform, false);
        _visionEffectImage = visionEffectObject.AddComponent<Image>();
        _visionEffectImage.raycastTarget = false;
        StretchToCanvas(_visionEffectImage.rectTransform);
        ResetEffects();
        _logger.LogInfo("Texture-lightning renderer initialized.");
    }

    private static Texture2D DownscaleTexture(Texture2D source, int maximumHeight)
    {
        var height = Mathf.Min(source.height, maximumHeight);
        var width = Mathf.Max(1, Mathf.RoundToInt((float)source.width / source.height * height));
        var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Default);
        var previous = RenderTexture.active;
        Graphics.Blit(source, renderTexture);
        RenderTexture.active = renderTexture;
        var resized = new Texture2D(width, height, TextureFormat.RGBA32, false);
        resized.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
        resized.Apply(false, false);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(renderTexture);
        return resized;
    }

    private static Vector2 FindLightningEndpoint(Texture2D texture)
    {
        const byte alphaThreshold = 48;
        var pixels = texture.GetPixels32();
        var minimumY = texture.height;

        for (var y = 0; y < texture.height; y++)
        {
            for (var x = 0; x < texture.width; x++)
            {
                if (pixels[y * texture.width + x].a >= alphaThreshold)
                {
                    minimumY = y;
                    break;
                }
            }
            if (minimumY != texture.height)
            {
                break;
            }
        }

        if (minimumY == texture.height)
        {
            return new Vector2(0.5f, 0f);
        }

        var bandHeight = Mathf.Max(2, texture.height / 100);
        long totalX = 0;
        var count = 0;
        var maximumY = Mathf.Min(texture.height, minimumY + bandHeight);
        for (var y = minimumY; y < maximumY; y++)
        {
            for (var x = 0; x < texture.width; x++)
            {
                if (pixels[y * texture.width + x].a < alphaThreshold)
                {
                    continue;
                }

                totalX += x;
                count++;
            }
        }

        var endpointX = count > 0 ? totalX / (float)count : texture.width * 0.5f;
        return new Vector2(
            endpointX / Mathf.Max(1f, texture.width - 1f),
            minimumY / Mathf.Max(1f, texture.height - 1f));
    }

    private static void AlignLightningEndpoint(Transform lightningTransform, Vector2 endpointUv, Vector3 target)
    {
        var localEndpoint = new Vector3(endpointUv.x - 0.5f, endpointUv.y - 0.5f, 0f);
        var endpointWorld = lightningTransform.TransformPoint(localEndpoint);
        lightningTransform.position += target - endpointWorld;
    }

    private static Camera FindGameCamera()
    {
        var mainCamera = Camera.main;
        if (mainCamera != null && mainCamera.enabled)
        {
            return mainCamera;
        }

        Camera selected = null;
        foreach (var camera in Camera.allCameras)
        {
            if (!camera.enabled || camera.cameraType != CameraType.Game)
            {
                continue;
            }

            if (selected == null || camera.depth > selected.depth)
            {
                selected = camera;
            }
        }

        return selected;
    }

    private static void StretchToCanvas(RectTransform rectTransform)
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
    }

    private void OnDestroy()
    {
        RestoreNativeImpactTuning();
        foreach (var texture in _lightningTextures)
        {
            if (texture?.Texture != null)
            {
                Destroy(texture.Texture);
            }
        }
        _lightningTextures.Clear();
        if (_lightningMaterial != null)
        {
            Destroy(_lightningMaterial);
        }
    }
}
