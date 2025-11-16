using UnityEngine;

[RequireComponent(typeof(AudioListener))]
public class UnderwaterAudioController : MonoBehaviour
{
    [System.Serializable]
    public class AudioBlendGroup
    {
        public AudioSource[] sources;
        public float volumeAbove = 1f;
        public float volumeUnderwater = 0f;
        public float pitchAbove = 1f;
        public float pitchUnderwater = 0.9f;
        [Range(0f, 1.1f)] public float reverbAbove = 0f;       // Uses AudioSource.reverbZoneMix
        [Range(0f, 1.1f)] public float reverbUnderwater = 1f;
        public bool playOnStart = true;
    }

    [Header("References")]
    public Transform cameraTransform;         // Main camera (or Cinemachine output camera)
    public Transform waterSurfaceTransform;   // Any point on the ocean surface (or null if you use waterHeightY)
    public float waterHeightY = 0f;           // Used if waterSurfaceTransform is null

    [Header("Audio Sources")]
    public AudioBlendGroup aboveWaterAudio = new AudioBlendGroup();    // Waves / nature ambient
    public AudioBlendGroup underwaterAudio = new AudioBlendGroup();    // Underwater loop (muffled)

    [Header("Blend Settings")]
    [Tooltip("Depth range (in meters) over which we fade from surface to full underwater.")]
    public float fadeDepth = 3f;             // e.g. fully underwater at 3m below surface
    public float fadeSpeed = 3f;             // how quickly volume reacts to changes

    float targetBlend = 0f;   // 0 = fully above water, 1 = fully underwater
    float currentBlend = 0f;

    void Reset()
    {
        if (Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    void Start()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        EnsurePlaying(aboveWaterAudio);
        EnsurePlaying(underwaterAudio);
    }

    void Update()
    {
        if (cameraTransform == null) return;

        float waterY = waterSurfaceTransform != null
            ? waterSurfaceTransform.position.y
            : waterHeightY;

        float camY = cameraTransform.position.y;

        // Positive depth = below surface
        float depth = waterY - camY;

        // Convert depth to blend factor 0..1 (0 at surface/above, 1 when deep)
        // depth <= 0 => above water -> 0
        // depth >= fadeDepth => fully underwater -> 1
        float target = Mathf.InverseLerp(0f, fadeDepth, depth);
        targetBlend = Mathf.Clamp01(target);

        // Smooth blend
        currentBlend = Mathf.MoveTowards(currentBlend, targetBlend, fadeSpeed * Time.deltaTime);

        // Apply to audio source volumes, pitch, reverb
        ApplyBlendToGroup(aboveWaterAudio, currentBlend);
        ApplyBlendToGroup(underwaterAudio, currentBlend);
    }

    void ApplyBlendToGroup(AudioBlendGroup group, float underwaterBlend)
    {
        if (group == null || group.sources == null) return;

        for (int i = 0; i < group.sources.Length; i++)
        {
            var src = group.sources[i];
            if (src == null) continue;

            src.volume = Mathf.Lerp(group.volumeAbove, group.volumeUnderwater, underwaterBlend);
            src.pitch = Mathf.Lerp(group.pitchAbove, group.pitchUnderwater, underwaterBlend);
            src.reverbZoneMix = Mathf.Lerp(group.reverbAbove, group.reverbUnderwater, underwaterBlend);
        }
    }

    void EnsurePlaying(AudioBlendGroup group)
    {
        if (group == null || group.sources == null) return;

        for (int i = 0; i < group.sources.Length; i++)
        {
            var src = group.sources[i];
            if (src == null) continue;

            src.loop = true;
            if (group.playOnStart && !src.isPlaying)
                src.Play();
        }
    }
}
