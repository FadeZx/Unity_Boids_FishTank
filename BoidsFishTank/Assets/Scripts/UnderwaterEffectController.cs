using UnityEngine;
using UnityEngine.Rendering;

public class UnderwaterURPController : MonoBehaviour
{
    public Volume underwaterVolume;  // Assign your Tank Volume
    public Transform cameraTransform; // Main Camera
    public Collider tankAreaCollider; // Tank�s Box Collider

    void Update()
    {
        if (underwaterVolume == null || cameraTransform == null || tankAreaCollider == null)
            return;

        if (tankAreaCollider.bounds.Contains(cameraTransform.position))
        {
            underwaterVolume.weight = 1f; // inside tank
            RenderSettings.fog = true;     // inside tank
        }
        else
        {
            underwaterVolume.weight = 0f; // outside tank
            RenderSettings.fog = false;
        }
    }
}
