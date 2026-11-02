using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using EFT.Weather;
using UnityEngine;
using UnityEngine.Networking;

namespace SenzeExtremeWeather;

internal enum ThunderStyle
{
    Random,
    SoftRumble,
    RollingRumble,
    ClapAndRumble,
}

internal sealed class ThunderAudio
{
    private const int MaximumSimultaneousVoices = 10;

    private sealed class ThunderVoice
    {
        internal GameObject GameObject;
        internal AudioSource ImpactSource;
        internal AudioSource RumbleSource;
        internal AudioClip DownloadedClip;
        internal AudioClip ClapClip;
        internal float ForcedFadeStartedAt = -1f;
        internal float ForcedFadeDuration;
        internal bool Released;
    }

    private sealed class ClipAnalysis
    {
        internal int FirstAudibleFrame;
        internal int PeakFrame;
    }

    private readonly ExtremeWeatherConfig _settings;
    private readonly ManualLogSource _logger;
    private readonly MonoBehaviour _coroutineHost;
    private readonly List<string> _audioFiles = new List<string>();
    private readonly List<ThunderVoice> _activeVoices = new List<ThunderVoice>();
    private readonly Dictionary<string, ClipAnalysis> _clipAnalysis =
        new Dictionary<string, ClipAnalysis>(System.StringComparer.OrdinalIgnoreCase);
    private string _lastAudioPath;
    private int _playbackGeneration;
    private bool _disposed;
    private bool _warnedAboutMissingClips;

    internal ThunderAudio(ExtremeWeatherConfig settings, ManualLogSource logger, MonoBehaviour coroutineHost)
    {
        _settings = settings;
        _logger = logger;
        _coroutineHost = coroutineHost;
    }

    internal IEnumerator LoadClips(string audioDirectory)
    {
        _audioFiles.Clear();
        if (!Directory.Exists(audioDirectory))
        {
            _logger.LogWarning($"Thunder audio directory was not found: {audioDirectory}");
            yield break;
        }

        _audioFiles.AddRange(Directory.GetFiles(audioDirectory, "*.mp3"));
        _audioFiles.AddRange(Directory.GetFiles(audioDirectory, "*.wav"));
        _audioFiles.Sort(System.StringComparer.OrdinalIgnoreCase);
        _logger.LogInfo($"Discovered {_audioFiles.Count} thunder sound file(s) for on-demand playback.");
        yield return null;
    }

    internal void Play(float delay, bool strong, ThunderStyle style = ThunderStyle.Random,
        Vector3? worldPosition = null, float sourceDistance = 0f)
    {
        if (_disposed || _audioFiles.Count == 0)
        {
            if (!_warnedAboutMissingClips)
            {
                _warnedAboutMissingClips = true;
                _logger.LogWarning("No thunder sounds are available. Lightning will continue without thunder audio.");
            }
            return;
        }

        var playbackGeneration = _playbackGeneration;
        _coroutineHost.StartCoroutine(LoadAndPlayCoroutine(
            delay, strong, style, worldPosition, sourceDistance, playbackGeneration));
    }

    internal void Reset()
    {
        _playbackGeneration++;
        foreach (var voice in _activeVoices.ToArray())
        {
            ReleaseVoice(voice);
        }
        _activeVoices.Clear();
    }

    internal void Dispose()
    {
        _playbackGeneration++;
        _disposed = true;
        foreach (var voice in _activeVoices.ToArray())
        {
            ReleaseVoice(voice);
        }
        _activeVoices.Clear();
        _audioFiles.Clear();
        _clipAnalysis.Clear();
    }

    private IEnumerator LoadAndPlayCoroutine(float delay, bool strong, ThunderStyle requestedStyle,
        Vector3? worldPosition, float sourceDistance, int playbackGeneration)
    {
        var path = SelectAudioPath();
        var audioType = string.Equals(Path.GetExtension(path), ".wav", System.StringComparison.OrdinalIgnoreCase)
            ? AudioType.WAV
            : AudioType.MPEG;

        using (var request = UnityWebRequestMultimedia.GetAudioClip(new System.Uri(path).AbsoluteUri, audioType))
        {
            var downloadHandler = request.downloadHandler as DownloadHandlerAudioClip;
            if (downloadHandler != null)
            {
                downloadHandler.streamAudio = false;
                downloadHandler.compressed = false;
            }

            yield return request.SendWebRequest();
            if (_disposed || playbackGeneration != _playbackGeneration)
            {
                yield break;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                _logger.LogWarning($"Could not load thunder sound '{Path.GetFileName(path)}': {request.error}");
                yield break;
            }

            var downloadedClip = DownloadHandlerAudioClip.GetContent(request);
            if (downloadedClip == null || downloadedClip.samples <= 0)
            {
                _logger.LogWarning(
                    $"Thunder sound '{Path.GetFileName(path)}' decoded without samples; state={downloadedClip?.loadState}.");
                yield break;
            }

            downloadedClip.name = Path.GetFileNameWithoutExtension(path) + "-live";
            if (!_clipAnalysis.TryGetValue(path, out var analysis))
            {
                analysis = new ClipAnalysis();
                yield return AnalyzeClipCoroutine(downloadedClip, analysis);
                if (_disposed || playbackGeneration != _playbackGeneration)
                {
                    Object.Destroy(downloadedClip);
                    yield break;
                }
                _clipAnalysis[path] = analysis;
            }
            StartOldestVoiceFadeIfNeeded();

            var style = ResolveStyle(requestedStyle);
            var origin = ResolveOrigin(worldPosition, delay);
            var voice = CreateVoice(downloadedClip, origin);
            _activeVoices.Add(voice);

            var firstAudibleSample = analysis.FirstAudibleFrame;
            var playbackClip = downloadedClip;
            if (style == ThunderStyle.ClapAndRumble)
            {
                voice.ClapClip = CreateClapClip(downloadedClip, analysis.PeakFrame);
                if (voice.ClapClip != null)
                {
                    playbackClip = voice.ClapClip;
                }
                else
                {
                    style = ThunderStyle.RollingRumble;
                }
            }

            var configuredVolume = _settings.ThunderVolume.Value / 100f * _settings.ThunderImpact.Value / 100f;
            var distanceFactor = Mathf.Lerp(1f, 0.76f,
                Mathf.InverseLerp(_settings.ThunderDelayMinimum.Value, _settings.ThunderDelayMaximum.Value, delay));
            var coverFactor = RainController.IsCameraUnderRain
                ? 1f
                : _settings.IndoorThunderVolume.Value / 100f;
            var styleVolume = GetStyleVolume(style);
            var loudBoost = style == ThunderStyle.ClapAndRumble
                ? _settings.LoudThunderBoost.Value / 100f
                : 1f;
            var impactVolume = Mathf.Clamp01(configuredVolume * distanceFactor * coverFactor * styleVolume * loudBoost);
            var rumbleVolume = style == ThunderStyle.ClapAndRumble
                ? Mathf.Clamp01(configuredVolume * distanceFactor * coverFactor * Random.Range(0.64f, 0.86f))
                : 0f;

            voice.ImpactSource.clip = playbackClip;
            voice.ImpactSource.volume = impactVolume;
            voice.ImpactSource.pitch = GetPitch(style, false);
            voice.ImpactSource.timeSamples = style == ThunderStyle.ClapAndRumble
                ? 0
                : Mathf.Min(firstAudibleSample, playbackClip.samples - 1);
            voice.ImpactSource.Play();

            if (style == ThunderStyle.ClapAndRumble)
            {
                voice.RumbleSource.clip = downloadedClip;
                voice.RumbleSource.volume = rumbleVolume * 0.55f;
                voice.RumbleSource.pitch = GetPitch(style, true);
                voice.RumbleSource.timeSamples = Mathf.Min(firstAudibleSample, downloadedClip.samples - 1);
                voice.RumbleSource.Play();
            }

            var naturalLength = Mathf.Max(playbackClip.length / Mathf.Abs(voice.ImpactSource.pitch),
                style == ThunderStyle.ClapAndRumble
                    ? downloadedClip.length / Mathf.Abs(voice.RumbleSource.pitch)
                    : 0f);
            var minimumDuration = Mathf.Min(
                _settings.ThunderDurationMinimum.Value,
                _settings.ThunderDurationMaximum.Value);
            var maximumDuration = Mathf.Max(
                _settings.ThunderDurationMinimum.Value,
                _settings.ThunderDurationMaximum.Value);
            var eventLength = Mathf.Min(naturalLength, Random.Range(minimumDuration, maximumDuration));

            var minimumFade = Mathf.Min(
                _settings.ThunderFadeOutMinimum.Value,
                _settings.ThunderFadeOutMaximum.Value);
            var maximumFade = Mathf.Max(
                _settings.ThunderFadeOutMinimum.Value,
                _settings.ThunderFadeOutMaximum.Value);
            var fadeLength = Mathf.Min(Random.Range(minimumFade, maximumFade), eventLength * 0.82f);
            var fadeStartedAt = eventLength - fadeLength;
            var attackLength = GetAttackLength(style, fadeStartedAt);
            var bodyEndGain = style == ThunderStyle.SoftRumble
                ? Random.Range(0.58f, 0.78f)
                : Random.Range(0.62f, 0.82f);

            _logger.LogInfo(
                $"Thunder requested from live file: file={Path.GetFileName(path)}, style={style}, "
                + $"samples={downloadedClip.samples}, volume={impactVolume:0.00}, duration={eventLength:0.00}s, "
                + $"fade={fadeLength:0.00}s, directionality={_settings.ThunderSpatialBlend.Value}%, "
                + $"distance={sourceDistance:0}m, activeVoices={_activeVoices.Count}, visualStrong={strong}.");

            yield return null;

            if (_disposed || playbackGeneration != _playbackGeneration)
            {
                yield break;
            }
            if (!voice.ImpactSource.isPlaying)
            {
                _logger.LogWarning(
                    $"Live thunder AudioSource did not start. Clip state={playbackClip.loadState}, "
                    + $"samples={playbackClip.samples}, active={voice.GameObject.activeInHierarchy}.");
                ReleaseVoice(voice);
                yield break;
            }

            var startedAt = Time.unscaledTime;
            while (!_disposed
                && playbackGeneration == _playbackGeneration
                && Time.unscaledTime - startedAt < eventLength)
            {
                var elapsed = Time.unscaledTime - startedAt;
                var naturalFade = CalculateNaturalFade(elapsed, fadeStartedAt, fadeLength);
                var forcedFade = CalculateForcedFade(voice);

                var impactEnvelope = CalculateImpactEnvelope(
                    style, elapsed, attackLength, fadeStartedAt, bodyEndGain);
                var rumbleEnvelope = CalculateRumbleEnvelope(
                    style, elapsed, attackLength, fadeStartedAt, bodyEndGain);
                var finalFade = Mathf.Clamp01(Mathf.Min(naturalFade, forcedFade));

                voice.ImpactSource.volume = impactVolume * impactEnvelope * finalFade;
                voice.RumbleSource.volume = rumbleVolume * rumbleEnvelope * finalFade;
                if (finalFade <= 0.001f)
                {
                    break;
                }
                yield return null;
            }

            ReleaseVoice(voice);
        }
    }

    private string SelectAudioPath()
    {
        if (_audioFiles.Count == 1)
        {
            _lastAudioPath = _audioFiles[0];
            return _lastAudioPath;
        }

        string selected;
        do
        {
            selected = _audioFiles[Random.Range(0, _audioFiles.Count)];
        }
        while (string.Equals(selected, _lastAudioPath, System.StringComparison.OrdinalIgnoreCase));

        _lastAudioPath = selected;
        return selected;
    }

    private ThunderStyle ResolveStyle(ThunderStyle requestedStyle)
    {
        if (requestedStyle != ThunderStyle.Random)
        {
            return requestedStyle;
        }

        var roll = Random.Range(0f, 100f);
        if (roll < _settings.LoudThunderChance.Value)
        {
            return ThunderStyle.ClapAndRumble;
        }

        return Random.Range(0f, 100f) < _settings.SoftRumbleChance.Value
            ? ThunderStyle.SoftRumble
            : ThunderStyle.RollingRumble;
    }

    private ThunderVoice CreateVoice(AudioClip downloadedClip, Vector3 origin)
    {
        var audioObject = new GameObject("Senze Extreme Weather thunder voice");
        audioObject.transform.position = origin;
        var spatialBlend = _settings.ThunderSpatialBlend.Value / 100f;
        return new ThunderVoice
        {
            GameObject = audioObject,
            ImpactSource = CreateAudioSource(audioObject, 0, spatialBlend),
            RumbleSource = CreateAudioSource(audioObject, 1, spatialBlend),
            DownloadedClip = downloadedClip,
            ForcedFadeDuration = Random.Range(3f, 5f),
        };
    }

    private Vector3 ResolveOrigin(Vector3? worldPosition, float delay)
    {
        if (worldPosition.HasValue)
        {
            return worldPosition.Value + Vector3.up * 45f;
        }

        var camera = Camera.main;
        if (camera == null)
        {
            return _coroutineHost.transform.position;
        }

        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.1f)
        {
            forward = Vector3.forward;
        }
        var direction = Quaternion.AngleAxis(Random.Range(-110f, 110f), Vector3.up) * forward;
        var syntheticDistance = Mathf.Lerp(90f, 420f,
            Mathf.InverseLerp(_settings.ThunderDelayMinimum.Value, _settings.ThunderDelayMaximum.Value, delay));
        return camera.transform.position + direction * syntheticDistance + Vector3.up * 70f;
    }

    private void StartOldestVoiceFadeIfNeeded()
    {
        if (_activeVoices.Count < MaximumSimultaneousVoices)
        {
            return;
        }

        foreach (var voice in _activeVoices)
        {
            if (!voice.Released && voice.ForcedFadeStartedAt < 0f)
            {
                voice.ForcedFadeStartedAt = Time.unscaledTime;
                return;
            }
        }
    }

    private static float CalculateNaturalFade(float elapsed, float fadeStartedAt, float fadeLength)
    {
        if (elapsed < fadeStartedAt)
        {
            return 1f;
        }
        return 1f - Mathf.SmoothStep(0f, 1f,
            (elapsed - fadeStartedAt) / Mathf.Max(0.01f, fadeLength));
    }

    private static float CalculateForcedFade(ThunderVoice voice)
    {
        if (voice.ForcedFadeStartedAt < 0f)
        {
            return 1f;
        }
        return 1f - Mathf.SmoothStep(0f, 1f,
            (Time.unscaledTime - voice.ForcedFadeStartedAt) / voice.ForcedFadeDuration);
    }

    private static float CalculateImpactEnvelope(ThunderStyle style, float elapsed,
        float attackLength, float fadeStartedAt, float bodyEndGain)
    {
        if (style == ThunderStyle.ClapAndRumble)
        {
            const float clapLength = 3f;
            return elapsed >= clapLength
                ? 0f
                : 1f - Mathf.SmoothStep(0f, 1f, elapsed / clapLength);
        }

        if (elapsed < attackLength)
        {
            var startGain = style == ThunderStyle.SoftRumble ? 0.06f : 0.42f;
            return Mathf.Lerp(startGain, 1f,
                Mathf.SmoothStep(0f, 1f, elapsed / Mathf.Max(0.01f, attackLength)));
        }

        var bodyProgress = Mathf.InverseLerp(attackLength, Mathf.Max(attackLength, fadeStartedAt), elapsed);
        var gentleVariation = 1f + Mathf.Sin(elapsed * 1.7f) * 0.035f;
        return Mathf.Lerp(1f, bodyEndGain, bodyProgress) * gentleVariation;
    }

    private static float CalculateRumbleEnvelope(ThunderStyle style, float elapsed,
        float attackLength, float fadeStartedAt, float bodyEndGain)
    {
        if (style != ThunderStyle.ClapAndRumble)
        {
            return 0f;
        }

        var rumbleAttack = Mathf.Min(0.8f, Mathf.Max(0.2f, attackLength));
        if (elapsed < rumbleAttack)
        {
            return Mathf.Lerp(0.48f, 1f,
                Mathf.SmoothStep(0f, 1f, elapsed / rumbleAttack));
        }

        var bodyProgress = Mathf.InverseLerp(rumbleAttack, Mathf.Max(rumbleAttack, fadeStartedAt), elapsed);
        return Mathf.Lerp(1f, bodyEndGain, bodyProgress);
    }

    private static float GetStyleVolume(ThunderStyle style)
    {
        switch (style)
        {
            case ThunderStyle.SoftRumble:
                return Random.Range(0.42f, 0.66f);
            case ThunderStyle.RollingRumble:
                return Random.Range(0.72f, 0.94f);
            default:
                return 1f;
        }
    }

    private static float GetPitch(ThunderStyle style, bool rumbleLayer)
    {
        if (rumbleLayer)
        {
            return Random.Range(0.78f, 0.9f);
        }

        switch (style)
        {
            case ThunderStyle.SoftRumble:
                return Random.Range(0.84f, 0.96f);
            case ThunderStyle.RollingRumble:
                return Random.Range(0.91f, 1.04f);
            default:
                return Random.Range(0.97f, 1.05f);
        }
    }

    private static float GetAttackLength(ThunderStyle style, float fadeStartedAt)
    {
        float attack;
        switch (style)
        {
            case ThunderStyle.SoftRumble:
                attack = Random.Range(1.1f, 3.2f);
                break;
            case ThunderStyle.RollingRumble:
                attack = Random.Range(0.12f, 0.55f);
                break;
            default:
                attack = Random.Range(0.15f, 0.45f);
                break;
        }
        return Mathf.Min(attack, fadeStartedAt * 0.6f);
    }

    private void ReleaseVoice(ThunderVoice voice)
    {
        if (voice == null || voice.Released)
        {
            return;
        }

        voice.Released = true;
        _activeVoices.Remove(voice);
        if (voice.ImpactSource != null)
        {
            voice.ImpactSource.Stop();
            voice.ImpactSource.clip = null;
        }
        if (voice.RumbleSource != null)
        {
            voice.RumbleSource.Stop();
            voice.RumbleSource.clip = null;
        }
        if (voice.ClapClip != null)
        {
            Object.Destroy(voice.ClapClip);
        }
        if (voice.DownloadedClip != null)
        {
            Object.Destroy(voice.DownloadedClip);
        }
        if (voice.GameObject != null)
        {
            Object.Destroy(voice.GameObject);
        }
    }

    private static AudioSource CreateAudioSource(GameObject audioObject, int priority, float spatialBlend)
    {
        var source = audioObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.mute = false;
        source.priority = priority;
        source.spatialBlend = spatialBlend;
        source.spatialize = false;
        source.dopplerLevel = 0f;
        source.spread = 115f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 35f;
        source.maxDistance = 1400f;
        source.ignoreListenerPause = true;
        source.ignoreListenerVolume = true;
        source.bypassEffects = true;
        source.bypassListenerEffects = true;
        source.bypassReverbZones = true;
        return source;
    }

    private static IEnumerator AnalyzeClipCoroutine(AudioClip clip, ClipAnalysis analysis)
    {
        const int framesPerBlock = 32768;
        const int blocksPerFrame = 8;

        var firstAudibleFrame = -1;
        var peakFrame = 0;
        var peakAmplitude = 0f;
        var blockIndex = 0;
        var samples = new float[framesPerBlock * clip.channels];

        for (var offsetFrame = 0; offsetFrame < clip.samples; offsetFrame += framesPerBlock)
        {
            var frameCount = Mathf.Min(framesPerBlock, clip.samples - offsetFrame);
            if (frameCount != framesPerBlock)
            {
                samples = new float[frameCount * clip.channels];
            }
            if (!clip.GetData(samples, offsetFrame))
            {
                analysis.FirstAudibleFrame = 0;
                analysis.PeakFrame = 0;
                yield break;
            }

            for (var frame = 0; frame < frameCount; frame++)
            {
                for (var channel = 0; channel < clip.channels; channel++)
                {
                    var amplitude = Mathf.Abs(samples[frame * clip.channels + channel]);
                    if (firstAudibleFrame < 0 && amplitude >= 0.008f)
                    {
                        firstAudibleFrame = offsetFrame + frame;
                    }
                    if (amplitude > peakAmplitude)
                    {
                        peakAmplitude = amplitude;
                        peakFrame = offsetFrame + frame;
                    }
                }
            }

            blockIndex++;
            if (blockIndex % blocksPerFrame == 0)
            {
                yield return null;
            }
        }

        analysis.FirstAudibleFrame = firstAudibleFrame < 0
            ? 0
            : Mathf.Max(0, firstAudibleFrame - clip.frequency / 20);
        analysis.PeakFrame = peakFrame;
    }

    private static AudioClip CreateClapClip(AudioClip source, int peakFrame)
    {
        var clapStartFrame = Mathf.Max(0, peakFrame - source.frequency / 12);
        var clapFrameCount = Mathf.Min(source.samples - clapStartFrame, source.frequency * 3);
        var clapSamples = new float[clapFrameCount * source.channels];
        if (!source.GetData(clapSamples, clapStartFrame))
        {
            return null;
        }

        var fadeFrameCount = Mathf.Min(source.frequency, clapFrameCount / 2);
        for (var frame = 0; frame < fadeFrameCount; frame++)
        {
            var gain = 1f - Mathf.SmoothStep(0f, 1f, frame / (float)fadeFrameCount);
            var targetFrame = clapFrameCount - fadeFrameCount + frame;
            for (var channel = 0; channel < source.channels; channel++)
            {
                clapSamples[targetFrame * source.channels + channel] *= gain;
            }
        }

        var clapClip = AudioClip.Create(source.name + "-clap", clapFrameCount, source.channels, source.frequency, false);
        if (!clapClip.SetData(clapSamples, 0))
        {
            Object.Destroy(clapClip);
            return null;
        }
        return clapClip;
    }
}
