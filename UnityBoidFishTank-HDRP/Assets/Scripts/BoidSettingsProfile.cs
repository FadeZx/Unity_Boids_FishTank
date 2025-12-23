using UnityEngine;

// ScriptableObject container for boid settings so they can be edited in the Inspector,
// live-synced while playing, and persisted like a Unity module/profile asset.
[CreateAssetMenu(menuName = "Boids/Boid Settings Profile", fileName = "BoidSettingsProfile")]
public class BoidSettingsProfile : ScriptableObject
{
    [SerializeField] BoidController.BoidSettings settings = new BoidController.BoidSettings();
    [SerializeField, HideInInspector] int revision;

    public BoidController.BoidSettings Settings => settings;
    public int Revision => revision;

    // Copy stored values to a controller.
    public void ApplyTo(BoidController controller, bool respawnIfNeeded = true)
    {
        if (!controller || settings == null) return;
        controller.Apply(settings, respawnIfNeeded);
    }

    // Capture controller runtime values back into the profile.
    public void CaptureFrom(BoidController controller)
    {
        if (!controller) return;
        if (settings == null) settings = new BoidController.BoidSettings();
        controller.CopyToSettings(settings);
        MarkDirty();
    }

    // Mark the profile as changed so live-sync can pick it up.
    public void MarkDirty()
    {
        unchecked { revision++; }
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        MarkDirty();
    }
#endif
}
