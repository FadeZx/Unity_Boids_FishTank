using UnityEngine;

public enum OrcaRole { Leader, Flanker, Striker, Support }

[RequireComponent(typeof(Transform))]
public class OrcaAgent : MonoBehaviour
{
    [HideInInspector] public OrcaController controller;
    [HideInInspector] public OrcaRole role;

    public Vector3 Position => transform.position;
    public Vector3 Velocity { get; set; }

    [Header("Rotation")]
    public float turnResponsiveness = 6f;
    public float bankingAmount = 0.5f;

    float strikeCooldownTimer = 0f;

    void Update()
    {
        if (!controller) return;
        float dt = Time.deltaTime;
        if (strikeCooldownTimer > 0f)
            strikeCooldownTimer -= dt;

        var steer = controller.ComputeSteering(this, dt, out var debugForces);

        Velocity += steer * dt;
        float speed = Mathf.Clamp(Velocity.magnitude, controller.minSpeed, controller.maxSpeed);
        if (speed > 0.0001f)
            Velocity = Velocity.normalized * speed;

        Vector3 start = transform.position;
        Vector3 delta = Velocity * dt;
        float radius = controller.orcaRadius;

        // Sweep movement (avoid tunneling)
        if (delta.sqrMagnitude > 1e-8f && Physics.SphereCast(start, radius, delta.normalized,
            out RaycastHit hit, delta.magnitude, controller.obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 atHit = start + delta.normalized * (hit.distance - 0.002f);
            Vector3 slide = Vector3.ProjectOnPlane(delta - delta.normalized * hit.distance, hit.normal);
            transform.position = atHit + slide;
            Velocity = slide.sqrMagnitude > 1e-8f ? slide / dt : Velocity * 0.25f;
        }
        else
        {
            transform.position = start + delta;
        }

        // Rotate toward direction
        if (Velocity.sqrMagnitude > 1e-6f)
        {
            Vector3 fwd = Velocity.normalized;
            Vector3 lateral = steer - Vector3.Dot(steer, fwd) * fwd;
            float roll = Mathf.Clamp(-lateral.magnitude * bankingAmount, -0.8f, 0.8f);
            Quaternion target = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0, 0, Mathf.Rad2Deg * roll);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-turnResponsiveness * dt));
        }

        // Get local copies
        Vector3 pos = transform.position;
        Vector3 vel = Velocity;

        // Call the method with refs to locals
        controller.EnforceBounds(ref pos, ref vel);

        // Write results back
        transform.position = pos;
        Velocity = vel;

    }

    // ✅ Add these two helpers:
    public bool CanStrike() => strikeCooldownTimer <= 0f;
    public void ResetStrikeCooldown() => strikeCooldownTimer = controller.strikeCooldown;
}
