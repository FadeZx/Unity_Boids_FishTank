using UnityEngine;

/// <summary>
/// Helper script to test and visualize spawner modifications.
/// Attach this to an empty GameObject to create spawn center points.
/// </summary>
public class SpawnerTestHelper : MonoBehaviour
{
    [Header("Spawn Center Visualization")]
    [Tooltip("Color for the spawn center gizmo")]
    public Color gizmoColor = Color.yellow;
    [Tooltip("Size of the spawn center gizmo")]
    public float gizmoSize = 0.5f;
    
    void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;
        Gizmos.DrawWireSphere(transform.position, gizmoSize);
        Gizmos.DrawSphere(transform.position, gizmoSize * 0.3f);
    }
    
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.5f);
        Gizmos.DrawSphere(transform.position, gizmoSize);
    }
}