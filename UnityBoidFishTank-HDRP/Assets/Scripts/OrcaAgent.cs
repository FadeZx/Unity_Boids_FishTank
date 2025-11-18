using UnityEngine;
using TMPro;

public enum OrcaRole { Leader, Flanker, Striker, Support }

[RequireComponent(typeof(Transform))]
public class OrcaAgent : MonoBehaviour
{
    [HideInInspector] public OrcaController controller;
    [HideInInspector] public OrcaRole role;

    // Per-orca prey targeting (lock-on)
    public BoidAgent CurrentTarget;                 // prey this orca is focused on
    public float targetHoldTime = 1.0f;             // minimum time to keep current target before allowing switch
    float targetHoldTimer = 0f;                     // countdown timer for hysteresis
    public bool HasTarget => CurrentTarget != null;
    public void SetTarget(BoidAgent t)
    {
        if (t != CurrentTarget)
            targetHoldTimer = targetHoldTime;       // reset hold when target changes
        CurrentTarget = t;
    }
    public void ClearTarget()
    {
        CurrentTarget = null;
        targetHoldTimer = 0f;
    }
    public bool CanSwitchTarget() => targetHoldTimer <= 0f;

    public Vector3 Position => transform.position;
    public Vector3 Velocity { get; set; }

    [Header("Rotation")]
    public float turnResponsiveness = 6f;
    public float bankingAmount = 0.5f;

    [Header("Labels")]
    [Tooltip("Assign the TextMeshPro TMP_Text component for the role label.")]
    public TMP_Text roleLabel;

    [Header("Collision / Kills")]
    [Tooltip("Enable kill-on-contact using the head trigger collider.")]
    public bool enableHeadKill = true;
    [Tooltip("Assign the Orca head trigger collider (set as IsTrigger). Collisions with this collider will kill prey.")]
    public Collider headTrigger;

    [Header("Animation")]
    [Tooltip("Animator driving SwimFast (bool), Eat (bool), and Attack (trigger) parameters.")]
    public Animator animator;
    [Tooltip("Time to keep SwimFast enabled after a strike boost starts.")]
    public float strikeBoostAnimDuration = 0.8f;
    [Tooltip("Seconds spent eating after a kill; also used as cooldown before the next eat.")]
    public float eatDuration = 2.0f;

    static readonly int animSwimFast = Animator.StringToHash("FastSwim");
    static readonly int animEat = Animator.StringToHash("Eating");
    static readonly int animAttack = Animator.StringToHash("Attack");

    float strikeCooldownTimer = 0f;
    float strikeBoostTimer = 0f;
    float eatTimer = 0f;

    void Awake()
    {
        // Attach a forwarder to the head trigger so we can detect exactly head collisions
        if (headTrigger != null && headTrigger.gameObject.GetComponent<OrcaHeadHitboxForwarder>() == null)
        {
            var fwd = headTrigger.gameObject.AddComponent<OrcaHeadHitboxForwarder>();
            fwd.agent = this;
        }
    }

    // Forwards trigger/collision from head collider into OrcaAgent logic
    private class OrcaHeadHitboxForwarder : MonoBehaviour
    {
        public OrcaAgent agent;
        void OnTriggerEnter(Collider other)
        {
            agent?.TryHeadKill(other);
        }
        void OnCollisionEnter(Collision collision)
        {
            agent?.TryHeadKill(collision.collider);
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;
        UpdateActionTimers(dt);
        if (!controller) return;
        if (strikeCooldownTimer > 0f)
            strikeCooldownTimer -= dt;
        if (targetHoldTimer > 0f)
            targetHoldTimer -= dt;
        // Clear target if prey destroyed or missing
        if (CurrentTarget != null && (CurrentTarget.controller == null || CurrentTarget.gameObject == null))
            ClearTarget();

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

        // Billboard role label to camera and toggle visibility
        if (roleLabel != null)
        {
            Camera cam = controller.labelCamera != null ? controller.labelCamera : Camera.main;
            roleLabel.gameObject.SetActive(controller.showRoleText);
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - roleLabel.transform.position;
                if (toCam.sqrMagnitude > 1e-6f)
                {
                    // Face camera (billboard)
                    roleLabel.transform.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
                }
            }
            roleLabel.text = role.ToString();
        }

    }

    public bool CanStrike() => strikeCooldownTimer <= 0f;
    public void ResetStrikeCooldown() => strikeCooldownTimer = controller.strikeCooldown;
    bool IsEating() => eatTimer > 0f;

    public void NotifyStrikeBoost()
    {
        strikeBoostTimer = strikeBoostAnimDuration;
        if (animator != null)
            animator.SetBool(animSwimFast, true);
    }

    void BeginEatState()
    {
        eatTimer = eatDuration;
        if (animator != null)
        {
            animator.SetBool(animEat, true);
            animator.SetTrigger(animAttack);
        }
    }

    void UpdateActionTimers(float dt)
    {
        if (strikeBoostTimer > 0f)
        {
            strikeBoostTimer -= dt;
            if (strikeBoostTimer <= 0f && animator != null)
                animator.SetBool(animSwimFast, false);
        }

        if (eatTimer > 0f)
        {
            eatTimer -= dt;
            if (eatTimer <= 0f && animator != null)
                animator.SetBool(animEat, false);
        }
    }

    // Contact-based kill: requires orca head collider (Trigger) and prey colliders
    void OnTriggerEnter(Collider other)
    {
        // Body triggers only kill if head-kill is disabled (fallback)
        if (!enableHeadKill)
            TryKill(other);
    }
    void OnCollisionEnter(Collision collision)
    {
        // Body collisions only kill if head-kill is disabled (fallback)
        if (!enableHeadKill)
            TryKill(collision.collider);
    }

    // Explicit kill only from head hitbox forwarder when enabled
    internal void TryHeadKill(Collider col)
    {
        if (!enableHeadKill) return;
        TryKill(col);
    }

    void TryKill(Collider col)
    {
        if (controller == null || controller.preyController == null) return;
        if (IsEating()) return; // wait for eat delay before killing again
        var prey = col.GetComponentInParent<BoidAgent>();
        if (prey == null) return;
        // Remove prey and increment kill count
        controller.preyController.RemoveAgent(prey);
        controller.killCount++;
        BeginEatState();
    }
}
