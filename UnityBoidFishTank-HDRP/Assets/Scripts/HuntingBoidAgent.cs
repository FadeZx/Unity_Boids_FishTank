using UnityEngine;
using UnityEngine.Events;
using TMPro;

public enum HunterRole { Leader, Flanker, Striker, Support }

[RequireComponent(typeof(Transform))]
public class HuntingBoidAgent : MonoBehaviour
{
    [HideInInspector] public HuntingBoidController controller;
    [HideInInspector] public HunterRole role;

    // Per-agent target lock
    public BoidAgent CurrentTarget;
    public float targetHoldTime = 1.0f;
    float targetHoldTimer = 0f;

    public bool HasTarget => CurrentTarget != null;
    [Header("Debug/Visual")]
    [Tooltip("Optional renderer whose material color is tinted based on state.")]
    public Renderer debugRenderer;
    [Tooltip("Color when tracking a target.")]
    public Color colorHasTarget = Color.blue;
    [Tooltip("Color when striking.")]
    public Color colorStriking = Color.red;
    [Tooltip("Blend speed for debug color changes.")]
    public float colorLerpSpeed = 6f;

    public void SetTarget(BoidAgent t)
    {
        if (t != CurrentTarget)
            targetHoldTimer = targetHoldTime;
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

    [Header("Collision / Capture")]
    [Tooltip("When true and a trigger is assigned, capture is driven by that trigger's OnTrigger callbacks.")]
    public bool useCaptureTrigger = true;
    [Tooltip("Trigger collider used to detect captures (can be head, body, or an area trigger). Leave empty to fall back to body collisions.")]
    public Collider captureTrigger;
    [Tooltip("Layer mask that can be captured. Leave to Everything to allow any layer.")]
    public LayerMask captureLayers = ~0;
    [Tooltip("Invoked when this agent captures a target (passes the captured collider).")]
    public UnityEvent<Collider> OnCapture = new UnityEvent<Collider>();

    [Header("Animation")]
    [Tooltip("Animator driving SwimFast (bool), Eat (bool), and Attack (trigger) parameters.")]
    public Animator animator;
    [Tooltip("Time to keep SwimFast enabled after a strike boost starts.")]
    public float strikeBoostAnimDuration = 0.8f;
    [Tooltip("Seconds spent eating after a capture; also used as cooldown before the next capture.")]
    public float eatDuration = 2.0f;

    static readonly int animSwimFast = Animator.StringToHash("FastSwim");
    static readonly int animEat = Animator.StringToHash("Eating");
    static readonly int animAttack = Animator.StringToHash("Attack");

    float strikeCooldownTimer = 0f;
    float strikeBoostTimer = 0f;
    float eatTimer = 0f;

    void Awake()
    {
        // Attach a forwarder to whatever trigger collider is assigned (head or custom area)
        if (captureTrigger != null && captureTrigger.gameObject.GetComponent<CaptureTriggerForwarder>() == null)
        {
            var fwd = captureTrigger.gameObject.AddComponent<CaptureTriggerForwarder>();
            fwd.agent = this;
        }
    }

    private class CaptureTriggerForwarder : MonoBehaviour
    {
        public HuntingBoidAgent agent;
        void OnTriggerEnter(Collider other) => agent?.TryCaptureViaTrigger(other);
        void OnCollisionEnter(Collision collision) => agent?.TryCaptureViaTrigger(collision.collider);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        UpdateActionTimers(dt);
        if (!controller) return;
        if (strikeCooldownTimer > 0f) strikeCooldownTimer -= dt;
        if (targetHoldTimer > 0f) targetHoldTimer -= dt;

        if (CurrentTarget != null && (CurrentTarget.controller == null || CurrentTarget.gameObject == null))
            ClearTarget();

        var steer = controller.ComputeSteering(this, dt, out _);

        Velocity += steer * dt;
        float speed = Mathf.Clamp(Velocity.magnitude, controller.minSpeed, controller.maxSpeed);
        if (speed > 0.0001f)
            Velocity = Velocity.normalized * speed;

        Vector3 start = transform.position;
        Vector3 delta = Velocity * dt;
        float radius = controller.agentRadius;

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

        if (Velocity.sqrMagnitude > 1e-6f)
        {
            Vector3 fwd = Velocity.normalized;
            Vector3 lateral = steer - Vector3.Dot(steer, fwd) * fwd;
            float roll = Mathf.Clamp(-lateral.magnitude * bankingAmount, -0.8f, 0.8f);
            Quaternion target = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0, 0, Mathf.Rad2Deg * roll);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-turnResponsiveness * dt));
        }

        Vector3 pos = transform.position;
        Vector3 vel = Velocity;
        controller.EnforceBounds(ref pos, ref vel);
        transform.position = pos;
        Velocity = vel;

        if (roleLabel != null)
        {
            Camera cam = controller.labelCamera != null ? controller.labelCamera : Camera.main;
            roleLabel.gameObject.SetActive(controller.showRoleText);
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - roleLabel.transform.position;
                if (toCam.sqrMagnitude > 1e-6f)
                    roleLabel.transform.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
            }
            roleLabel.text = role.ToString();
        }

        UpdateDebugColor(dt);
    }

    public bool CanStrike() => strikeCooldownTimer <= 0f;
    public void ResetStrikeCooldown() => strikeCooldownTimer = controller.strikeCooldown;

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

    void UpdateDebugColor(float dt)
    {
        if (debugRenderer == null) return;
        Color targetColor = HasTarget ? colorHasTarget : debugRenderer.material.color;
        if (strikeBoostTimer > 0f)
            targetColor = colorStriking;
        var mat = debugRenderer.material;
        mat.color = Color.Lerp(mat.color, targetColor, 1f - Mathf.Exp(-colorLerpSpeed * dt));
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

    void OnTriggerEnter(Collider other)
    {
        // If we have a dedicated capture trigger, its forwarder will call TryCaptureViaTrigger.
        // Otherwise allow body-trigger capture when trigger-based capture is enabled or when trigger is absent.
        if (!useCaptureTrigger || captureTrigger == null)
            TryCapture(other);
    }

    void OnCollisionEnter(Collision collision)
    {
        // Body collisions are only used when no capture trigger is assigned.
        if (!useCaptureTrigger || captureTrigger == null)
            TryCapture(collision.collider);
    }

    internal void TryCaptureViaTrigger(Collider col)
    {
        if (!useCaptureTrigger) return;
        if (captureTrigger != null && col.transform == captureTrigger.transform) return; // ignore self collisions
        TryCapture(col);
    }

    void TryCapture(Collider col)
    {
        if (!IsLayerAllowed(col)) return;
        if (IsEating()) return;
        // Fire event for any allowed collider (bullet-style); no BoidAgent check required.
        OnCapture.Invoke(col);
        BeginEatState();
    }

    bool IsEating() => eatTimer > 0f;

    bool IsLayerAllowed(Collider col)
    {
        return (captureLayers.value & (1 << col.gameObject.layer)) != 0;
    }

    /// <summary>
    /// Remove this agent from the controller and destroy its GameObject.
    /// </summary>
    public void DestroySelf()
    {
        if (controller != null)
            controller.DestroyAgent(this);
        else
            Destroy(gameObject);
    }
}
