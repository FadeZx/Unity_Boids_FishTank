using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
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
    [Tooltip("AudioListener sourced from the camera. Optional; auto-assigned if left empty.")]
    public AudioListener cameraAudioListener; // Serialized reference to the camera's AudioListener
    public Transform waterSurfaceTransform;   // Any point on the ocean surface (or null if you use waterHeightY)
    public float waterHeightY = 0f;           // Used if waterSurfaceTransform is null

    [Header("Audio Sources")]
    public AudioBlendGroup aboveWaterAudio = new AudioBlendGroup();    // Waves / nature ambient
    public AudioBlendGroup underwaterAudio = new AudioBlendGroup();    // Underwater loop (muffled)

    [Header("Blend Settings")]
    [Tooltip("Depth range (in meters) over which we fade from surface to full underwater.")]
    public float fadeDepth = 3f;             // e.g. fully underwater at 3m below surface
    public float fadeSpeed = 3f;             // how quickly volume reacts to changes

    [Header("Global Controls")]
    [Tooltip("When true, all controlled audio is muted.")]
    public bool muted = false;
    [Range(0f, 1f)]
    [Tooltip("Overall volume multiplier applied to all controlled audio.")]
    public float masterVolume = 1f;

    // Simple pop-up UI state
    [SerializeField, Tooltip("Show the bottom-center audio UI panel.")]
    bool showPanel = false;
    float panelAnim = 0f; // 0 hidden -> 1 shown
    float panelAnimVelocity = 0f;

    float targetBlend = 0f;   // 0 = fully above water, 1 = fully underwater
    float currentBlend = 0f;

    void Reset()
    {
        if (Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
            if (cameraAudioListener == null)
                cameraAudioListener = Camera.main.GetComponent<AudioListener>();
        }
    }

    void Start()
    {
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }
        if (cameraAudioListener == null && cameraTransform != null)
        {
            cameraAudioListener = cameraTransform.GetComponentInChildren<AudioListener>();
        }

        EnsurePlaying(aboveWaterAudio);
        EnsurePlaying(underwaterAudio);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }
        if (cameraAudioListener == null && cameraTransform != null)
        {
            cameraAudioListener = cameraTransform.GetComponentInChildren<AudioListener>();
        }
    }
#endif

    void Update()
    {
        if (cameraTransform == null) return;

        // Hotkey: toggle mute with 'M' (New Input System)
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
        {
            ToggleMute();
        }
#else
        // Legacy Input fallback (only if enabled in Player Settings)
        if (Input.GetKeyDown(KeyCode.M))
        {
            ToggleMute();
        }
#endif

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

        // Ensure global listener volume follows master/mute
        AudioListener.volume = muted ? 0f : Mathf.Clamp01(masterVolume);

        // Animate panel (smooth pop in/out)
        float panelTarget = showPanel ? 1f : 0f;
        panelAnim = Mathf.SmoothDamp(panelAnim, panelTarget, ref panelAnimVelocity, 0.08f);
    }

    void ApplyBlendToGroup(AudioBlendGroup group, float underwaterBlend)
    {
        if (group == null || group.sources == null) return;

        for (int i = 0; i < group.sources.Length; i++)
        {
            var src = group.sources[i];
            if (src == null) continue;

            float baseVol = Mathf.Lerp(group.volumeAbove, group.volumeUnderwater, underwaterBlend);
            float effectiveMaster = muted ? 0f : Mathf.Clamp01(masterVolume);
            src.volume = Mathf.Clamp01(baseVol * effectiveMaster);
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

    // Public API: global mute/volume
    public void SetMuted(bool value)
    {
        muted = value;
    }

    public void ToggleMute()
    {
        muted = !muted;
    }

    public bool IsMuted()
    {
        return muted;
    }

    public void SetMasterVolume(float value)
    {
        masterVolume = Mathf.Clamp01(value);
    }

    public float GetMasterVolume()
    {
        return masterVolume;
    }

    void OnGUI()
    {
        // Basic bottom-center UI for mute + volume
        const float panelWidth = 360f;
        const float panelHeightExpanded = 90f;
        const float panelHeightCollapsed = 22f;
        const float margin = 8f;

        // Interpolated height for pop-up animation
        float panelHeight = Mathf.Lerp(panelHeightCollapsed, panelHeightExpanded, Mathf.Clamp01(panelAnim));

        float x = (Screen.width - panelWidth) * 0.5f;
        float y = Screen.height - panelHeight - margin;

        // Background panel
        var panelRect = new Rect(x, y, panelWidth, panelHeight);
        GUI.Box(panelRect, "");

        // Handle bar/button to toggle panel visibility
        var handleRect = new Rect(x + (panelWidth - 120f) * 0.5f, y + panelHeight - panelHeightCollapsed, 120f, panelHeightCollapsed);
        if (GUI.Button(handleRect, showPanel ? "Audio ^" : "Audio v"))
        {
            showPanel = !showPanel;
        }

        // Only draw controls when sufficiently expanded
        if (panelAnim > 0.05f)
        {
            float padding = 10f;
            float left = x + padding;
            float top = y + padding;
            float contentW = panelWidth - padding * 2f;

            // Mute toggle (with hotkey hint)
            var muteRect = new Rect(left, top, contentW * 0.45f, 24f);
            bool newMuted = GUI.Toggle(muteRect, muted, "Mute (M)");
            if (newMuted != muted)
            {
                SetMuted(newMuted);
            }

            // Volume slider label + slider
            var labelRect = new Rect(left + contentW * 0.5f, top, 60f, 20f);
            GUI.Label(labelRect, "Volume");
            var sliderRect = new Rect(labelRect.xMax + 6f, top + 2f, contentW - (labelRect.xMax - left) - 6f, 20f);
            float newVol = GUI.HorizontalSlider(sliderRect, masterVolume, 0f, 1f);
            if (!Mathf.Approximately(newVol, masterVolume))
            {
                SetMasterVolume(newVol);
            }
        }
    }
}
