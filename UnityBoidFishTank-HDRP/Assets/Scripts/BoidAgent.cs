using UnityEngine;

[RequireComponent(typeof(Transform))]
public class BoidAgent : MonoBehaviour
{
    [HideInInspector] public BoidController controller;

    public Vector3 Position => transform.position;
    public Vector3 Velocity { get; set; }
    public float RuntimeMaxSpeed { get; set; }

    [Header("Rotation")]
    public float turnResponsiveness = 6f;
    public float bankingAmount = 0.4f;

    void Update()
    {
        // Skip per-agent update when controller runs the Burst/job pipeline.
        if (!controller || controller.JobsEnabled) return;
        float dt = Time.deltaTime;

        // Steering + debug forces
        var steer = controller.ComputeSteering(this, dt, out var f);

        Velocity += steer * dt;
        float requestedMax = RuntimeMaxSpeed > 0f ? RuntimeMaxSpeed : controller.maxSpeed;
        float cappedMax = controller.GetCappedSpeed(requestedMax);
        float speed = Mathf.Clamp(Velocity.magnitude, controller.minSpeed, cappedMax);
        if (speed > 0.0001f) Velocity = Velocity.normalized * speed;
        transform.position += Velocity * dt;

        if (Velocity.sqrMagnitude > 0.0001f)
        {
            Vector3 fwd = Velocity.normalized;
            Vector3 lateral = steer - Vector3.Dot(steer, fwd) * fwd;
            float roll = Mathf.Clamp(-lateral.magnitude * bankingAmount, -0.7f, 0.7f);
            Quaternion targetRot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0, 0, Mathf.Rad2Deg * roll);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 1f - Mathf.Exp(-turnResponsiveness * dt));
        }

#if UNITY_EDITOR
        // --- Draw debug vectors ---
        if (controller.drawDebug)
        {
            // If controller is selected → show all lines
            bool showAll = UnityEditor.Selection.activeGameObject == controller.gameObject;
            // If this object is selected → show this one
            bool showSelf = UnityEditor.Selection.activeGameObject == gameObject;

            if (showAll || showSelf)
                DrawDebugVectors(f);
        }
#endif
    }

#if UNITY_EDITOR
    void DrawDebugVectors((Vector3 sep, Vector3 ali, Vector3 coh, Vector3 bounds, Vector3 avoid) f)
    {
        Vector3 p = transform.position;
        float scale = 0.5f;

        // Rule forces
        Debug.DrawLine(p, p + f.sep.normalized * scale, Color.red);       // Separation
        Debug.DrawLine(p, p + f.ali.normalized * scale, Color.blue);      // Alignment
        Debug.DrawLine(p, p + f.coh.normalized * scale, Color.yellow);    // Cohesion
        Debug.DrawLine(p, p + f.bounds.normalized * scale, Color.green);  // Bounds
        Debug.DrawLine(p, p + f.avoid.normalized * scale, Color.magenta); // Avoid
        // Resultant / velocity (white)
        Debug.DrawLine(p, p + Velocity.normalized * scale, Color.white);
    }
#endif
}
