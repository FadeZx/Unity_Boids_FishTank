using System.Collections.Generic;
using UnityEngine;

public class HuntingBoidController : MonoBehaviour
{
    [Header("References")]
    public BoxCollider huntingArea;
    public HuntingBoidAgent hunterPrefab;

    [Header("Spawn")]
    public bool spawnOnStart = true;
    public int spawnCount = 8;
    public float spawnRadius = 8f;
    public bool useHuntingArea = true;
    public Transform spawnCenter;
    [Tooltip("Enable periodic respawning of hunters.")]
    public bool spawnOnInterval = false;
    [Tooltip("Seconds between spawns when spawnOnInterval is enabled.")]
    public float spawnInterval = 8f;
    [Tooltip("Respawn hunters automatically when all agents are destroyed.")]
    public bool respawnWhenEmpty = true;

    [Header("Speeds")]
    public float minSpeed = 2.2f;
    public float maxSpeed = 6.0f;
    public float maxSteerForce = 10.0f;

    [Header("Flocking")]
    public float neighborRadius = 3.0f;
    public float separationRadius = 0.9f;
    public float wSeparation = 1.3f;
    public float wAlignment = 0.8f;
    public float wCohesion = 0.8f;

    [Header("Hunt")]
    [Tooltip("Tag used to discover targets.")]
    public string targetTag = "Prey";
    [Tooltip("Optional layer mask filter for targets (use Everything to allow any layer).")]
    public LayerMask targetLayers = ~0;
    [Tooltip("If true, targets must match the tag AND layer; if false, layer alone is sufficient.")]
    public bool requireTargetTag = false;
    public float retargetInterval = 0.6f;
    public float wPursuit = 2.0f;
    public float strikeRange = 3.0f;
    public float strikeBoost = 1.6f;
    public float strikeCooldown = 2.5f;
    [Tooltip("How long hunters pause/aim before committing to a strike.")]
    public float strikeWindupTime = 0.4f;
    [Tooltip("Distance traveled during a strike dash.")]
    public float strikeDashDistance = 6.0f;
    [Tooltip("Preferred encirclement radius while hunting.")]
    public float encircleRadius = 6.0f;
    [Tooltip("Offset distance hunters try to sit behind the target before a strike.")]
    public float blindspotDistance = 4.0f;
    [Tooltip("How much sideways sway while holding the blindspot (prevents tight orbits).")]
    public float blindspotSideJitter = 1.5f;
    [Tooltip("When inside this range, hunters slow down and hold position before striking.")]
    public float strikeApproachRange = 5.0f;

    [Header("Obstacle & Boundary Avoidance")]
    public LayerMask obstacleMask;
    public float avoidDistance = 2.5f;
    [Tooltip("Optional max cap for obstacle probe length (0 = uncapped).")]
    public float avoidDistanceCap = 0f;
    public float avoidProbeAngle = 25f;
    public float agentRadius = 0.25f;
    [Tooltip("Distance from walls where hunters start steering away (soft boundary).")]
    public float boundaryAvoidRadius = 1.2f;
    [Tooltip("Baseline weight for obstacle avoidance while hunting.")]
    public float obstacleWeight = 1f;
    [Tooltip("Weight for obstacle avoidance while striking (0 = ignore obstacles).")]
    public float obstacleWeightWhenStriking = 0.1f;
    [Tooltip("Speed above which obstacle avoidance weight is reduced toward obstacleWeightAtHighSpeed.")]
    public float obstacleHighSpeed = 10f;
    [Tooltip("Avoidance weight when speed is twice obstacleHighSpeed (blended).")]
    public float obstacleWeightAtHighSpeed = 0.2f;

    [Header("Labels")]
    public bool showRoleText = true;
    public Camera labelCamera;

    public readonly List<HuntingBoidAgent> hunters = new();
    readonly List<BoidAgent> targetPool = new();

    float retargetTimer = 0f;
    float spawnTimer = 0f;

    void Start()
    {
        if (!hunterPrefab)
        {
            Debug.LogError("HuntingBoidController: assign hunterPrefab.");
            enabled = false;
            return;
        }

        if (labelCamera == null) labelCamera = Camera.main;
        RefreshTargetPool();
        if (spawnOnStart) SpawnHunters();
        spawnTimer = spawnInterval;
    }

    void Update()
    {
        retargetTimer -= Time.deltaTime;
        if (retargetTimer <= 0f)
        {
            RefreshTargetPool();
            retargetTimer = retargetInterval;
        }

        EnsureTargetsAssigned();
        MaybeSpawn();
    }

    public void SpawnHunters()
    {
        Clear();
        Vector3 centerPoint = spawnCenter ? spawnCenter.position : (huntingArea ? huntingArea.bounds.center : transform.position);
        Bounds b = huntingArea != null ? huntingArea.bounds : new Bounds(centerPoint, Vector3.one * spawnRadius * 2f);

        for (int i = 0; i < spawnCount; i++)
        {
            Vector3 p = GetValidSpawnPosition(centerPoint, b);
            var a = Instantiate(hunterPrefab, p, Quaternion.identity, transform);
            a.controller = this;
            a.name = $"Hunter {i + 1}";
            a.Velocity = Random.insideUnitSphere.normalized * Random.Range(minSpeed, maxSpeed);
            hunters.Add(a);
        }
    }

    void MaybeSpawn()
    {
        if (!hunterPrefab) return;

        // Respawn when all hunters are gone
        if (respawnWhenEmpty && hunters.Count == 0)
        {
            SpawnHunters();
            return;
        }

        // Periodic spawns
        if (spawnOnInterval)
        {
            spawnTimer -= Time.deltaTime;
            if (spawnTimer <= 0f)
            {
                SpawnHunters();
                spawnTimer = Mathf.Max(0.1f, spawnInterval);
            }
        }
    }

    public void Clear()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);
        hunters.Clear();
    }

    Vector3 GetValidSpawnPosition(Vector3 centerPoint, Bounds area)
    {
        Vector3 candidatePos = centerPoint + Random.insideUnitSphere * spawnRadius;

        if (useHuntingArea && huntingArea != null)
        {
            candidatePos.x = Mathf.Clamp(candidatePos.x, area.min.x, area.max.x);
            candidatePos.y = Mathf.Clamp(candidatePos.y, area.min.y, area.max.y);
            candidatePos.z = Mathf.Clamp(candidatePos.z, area.min.z, area.max.z);
        }
        return candidatePos;
    }

    public Vector3 ComputeSteering(HuntingBoidAgent self, float dt, out (Vector3 sep, Vector3 ali, Vector3 coh, Vector3 pursuit, Vector3 avoid) dbg)
    {
        Vector3 pos = self.Position;
        Vector3 vel = self.Velocity;

        Vector3 sep = Vector3.zero, ali = Vector3.zero, coh = Vector3.zero;
        int n = 0;
        float nr2 = neighborRadius * neighborRadius;
        float sr2 = separationRadius * separationRadius;

        foreach (var h in hunters)
        {
            if (h == null || h == self) continue;
            Vector3 to = h.Position - pos;
            float d2 = to.sqrMagnitude;
            bool withinNeighbor = d2 <= nr2;
            bool withinSeparation = d2 <= sr2;
            if (!withinNeighbor && !withinSeparation) continue;

            if (withinSeparation)
                sep -= to.normalized / Mathf.Max(0.001f, Mathf.Sqrt(d2));

            if (withinNeighbor)
            {
                n++;
                ali += h.Velocity;
                coh += h.Position;
            }
        }
        if (n > 0)
        {
            ali = (ali / n).normalized * maxSpeed - vel;
            coh = ((coh / n) - pos);
        }
        if (sep.sqrMagnitude > 1e-6f) sep = sep.normalized * maxSpeed - vel;

        Vector3 pursuit = PursuitForce(self, pos, vel);
        Vector3 obstacleAvoid = ObstacleAvoid(pos, vel);
        Vector3 boundaryAvoid = BoundaryAvoid(pos, vel);

        // Scale obstacle avoidance based on strike/high-speed state
        float avoidScale = obstacleWeight;
        if (self.IsStrikingNow)
        {
            avoidScale = obstacleWeightWhenStriking;
        }
        else
        {
            float speed = vel.magnitude;
            if (speed > obstacleHighSpeed)
            {
                float t = Mathf.Clamp01((speed - obstacleHighSpeed) / Mathf.Max(0.001f, obstacleHighSpeed));
                avoidScale = Mathf.Lerp(obstacleWeight, obstacleWeightAtHighSpeed, t);
            }
        }

        Vector3 avoid = obstacleAvoid * avoidScale + boundaryAvoid;

        // Hold/windup: stop flocking movement, just brake and avoid
        if (self.InStrikeWindup)
        {
            Vector3 holdSteer = -vel + avoid;
            if (holdSteer.sqrMagnitude > maxSteerForce * maxSteerForce)
                holdSteer = holdSteer.normalized * maxSteerForce;
            dbg = (sep, ali, coh, pursuit, avoid);
            return holdSteer;
        }

        // If we triggered a strike, pursuit returns the dash directly (already includes maxSpeed scaling).
        // When in dash mode, pursuit already contains the locked direction; apply only avoidances
        if (self.IsStrikingNow)
        {
            Vector3 dashSteer = pursuit + avoid;
            dbg = (sep, ali, coh, pursuit, avoid);
            return dashSteer;
        }

        Vector3 steer = wSeparation * sep + wAlignment * ali + wCohesion * coh + pursuit + avoid;
        if (steer.sqrMagnitude > maxSteerForce * maxSteerForce)
            steer = steer.normalized * maxSteerForce;

        dbg = (sep, ali, coh, pursuit, avoid);
        return steer;
    }

    Vector3 PursuitForce(HuntingBoidAgent self, Vector3 pos, Vector3 vel)
    {
        AcquireTarget(self);
        var target = self.CurrentTarget;
        if (target == null) return Vector3.zero;

        Vector3 toTgt = target.Position - pos;
        float dist = toTgt.magnitude;
        Vector3 tgtVel = target.Velocity;

        Vector3 primaryDir = tgtVel.sqrMagnitude > 1e-4f ? tgtVel.normalized : (dist > 0.0001f ? -toTgt / dist : Vector3.forward);
        Vector3 behindDir = -primaryDir;
        Vector3 side = Vector3.Cross(Vector3.up, behindDir);
        if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
        side.Normalize();

        // Encircle / hunt movement around a preferred ring
        Vector3 radial = pos - target.Position;
        if (radial.sqrMagnitude < 0.01f) radial = behindDir * encircleRadius;
        float radialMag = radial.magnitude;
        Vector3 tangential = Vector3.Cross(Vector3.up, radial).normalized;
        float ringError = radialMag - encircleRadius;
        float encircleSpeed = maxSpeed * 0.65f;
        Vector3 encircleVel = tangential * encircleSpeed - radial.normalized * Mathf.Clamp(ringError, -1f, 1f) * (maxSpeed * 0.35f);

        float sway = Mathf.Sin(Time.time * 1.3f + self.GetInstanceID() * 0.37f);
        Vector3 blindspotOffset = behindDir * blindspotDistance + side * (blindspotSideJitter * 0.35f * sway);
        Vector3 desiredPos = target.Position + blindspotOffset;
        Vector3 toHold = desiredPos - pos;
        float holdDist = toHold.magnitude;
        Vector3 desiredDir = holdDist > 0.0001f ? toHold / holdDist : behindDir;

        // Complete windup -> start dash with locked direction
        if (self.InStrikeWindup && self.StrikeWindupRemaining <= 0f)
        {
            // Lock dash direction toward the current target position at launch time
            Vector3 dir = dist > 0.0001f ? toTgt / dist : self.StrikeDashDirection;
            float dashDuration = strikeDashDistance / Mathf.Max(0.001f, maxSpeed * strikeBoost);
            self.BeginStrikeDash(dir, dashDuration);
        }

        // Active dash: keep direction locked
        if (self.InStrikeDash)
        {
            Vector3 dash = self.StrikeDashDirection * (maxSpeed * strikeBoost);
            return dash - vel;
        }

        // Start windup only when actually inside strike range, behind target, and off cooldown
        bool withinStrikeRange = dist <= strikeRange;
        float behindScore = Vector3.Dot((-toTgt).normalized, primaryDir); // 1 = fully behind target
        bool inBlindspot = behindScore > 0.25f;
        if (withinStrikeRange && inBlindspot && self.CanStrike() && !self.InStrikeWindup)
        {
            Vector3 dir = dist > 0.0001f ? toTgt / dist : desiredDir;
            self.SetStrikeWindup(dir, strikeWindupTime);
            // Brake/hold during windup
            return -vel;
        }

        // Holding behavior: stay mostly behind target with minimal drift (no orbit)
        float holdSpeed = holdDist < strikeApproachRange
            ? Mathf.Lerp(minSpeed * 0.35f, maxSpeed * 0.45f, holdDist / strikeApproachRange)
            : maxSpeed * 0.65f;
        Vector3 desired = desiredDir * holdSpeed;

        // Blend encircle motion while outside approach range to keep hunting movement
        if (holdDist > strikeApproachRange)
            desired = Vector3.Lerp(desired, encircleVel, 0.5f);
        else
            desired = Vector3.Lerp(desired, encircleVel * 0.35f, 0.2f);

        return (desired - vel) * wPursuit;
    }

    Vector3 ObstacleAvoid(Vector3 pos, Vector3 vel)
    {
        if (vel.sqrMagnitude < 1e-8f) return Vector3.zero;
        float probe = avoidDistanceCap > 0f ? Mathf.Min(avoidDistance, avoidDistanceCap) : avoidDistance;
        Vector3 fwd = vel.normalized;

        if (Physics.SphereCast(pos, agentRadius, fwd, out RaycastHit hit, probe, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 slide = Vector3.ProjectOnPlane(fwd, hit.normal).normalized;
            float t = 1f - Mathf.Clamp01(hit.distance / probe);
            return slide * (maxSpeed * (0.8f + 0.6f * t)) - vel * 0.1f;
        }

        bool useLeft = (Time.frameCount & 1) == 0;
        Quaternion sideQ = Quaternion.AngleAxis(useLeft ? -avoidProbeAngle : avoidProbeAngle, Vector3.up);
        Vector3 sideDir = sideQ * fwd;
        float sideProbe = probe * 0.7f;
        if (Physics.SphereCast(pos, agentRadius, sideDir, out hit, sideProbe, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 slide = Vector3.ProjectOnPlane(fwd, hit.normal).normalized;
            float t = 1f - Mathf.Clamp01(hit.distance / sideProbe);
            return slide * (maxSpeed * (0.7f + 0.5f * t)) - vel * 0.1f;
        }

        return Vector3.zero;
    }

    Vector3 BoundaryAvoid(Vector3 pos, Vector3 vel)
    {
        if (!useHuntingArea || huntingArea == null) return Vector3.zero;
        var b = huntingArea.bounds;
        Vector3 steer = Vector3.zero;
        float pad = boundaryAvoidRadius;

        if (pos.x - b.min.x < pad)
            steer += Vector3.right * (1f - Mathf.Clamp01((pos.x - b.min.x) / pad));
        else if (b.max.x - pos.x < pad)
            steer += Vector3.left * (1f - Mathf.Clamp01((b.max.x - pos.x) / pad));

        if (pos.y - b.min.y < pad)
            steer += Vector3.up * (1f - Mathf.Clamp01((pos.y - b.min.y) / pad));
        else if (b.max.y - pos.y < pad)
            steer += Vector3.down * (1f - Mathf.Clamp01((b.max.y - pos.y) / pad));

        if (pos.z - b.min.z < pad)
            steer += Vector3.forward * (1f - Mathf.Clamp01((pos.z - b.min.z) / pad));
        else if (b.max.z - pos.z < pad)
            steer += Vector3.back * (1f - Mathf.Clamp01((b.max.z - pos.z) / pad));

        if (steer.sqrMagnitude > 1e-8f)
            steer = steer.normalized * maxSpeed - vel * 0.2f;

        return steer;
    }

    public void EnforceBounds(ref Vector3 pos, ref Vector3 vel, float bounciness = 0.25f, float skin = 0.01f)
    {
        if (!useHuntingArea || huntingArea == null) return;
        var b = huntingArea.bounds;

        if (pos.x < b.min.x + skin) { pos.x = b.min.x + skin; vel.x = Mathf.Abs(vel.x) * bounciness; }
        else if (pos.x > b.max.x - skin) { pos.x = b.max.x - skin; vel.x = -Mathf.Abs(vel.x) * bounciness; }

        if (pos.y < b.min.y + skin) { pos.y = b.min.y + skin; vel.y = Mathf.Abs(vel.y) * bounciness; }
        else if (pos.y > b.max.y - skin) { pos.y = b.max.y - skin; vel.y = -Mathf.Abs(vel.y) * bounciness; }

        if (pos.z < b.min.z + skin) { pos.z = b.min.z + skin; vel.z = Mathf.Abs(vel.z) * bounciness; }
        else if (pos.z > b.max.z - skin) { pos.z = b.max.z - skin; vel.z = -Mathf.Abs(vel.z) * bounciness; }
    }

    void EnsureTargetsAssigned()
    {
        if (targetPool.Count == 0) return;
        for (int i = 0; i < hunters.Count; i++)
        {
            var h = hunters[i];
            if (h == null) continue;
            AcquireTarget(h);
        }
    }

    public void EnsureAgentHasTarget(HuntingBoidAgent agent) => AcquireTarget(agent);

    void AcquireTarget(HuntingBoidAgent agent)
    {
        if (agent == null) return;
        if (targetPool.Count == 0)
        {
            RefreshTargetPool();
            if (targetPool.Count == 0) return;
        }
        if (agent.CurrentTarget != null && agent.CurrentTarget.gameObject != null) return;

        BoidAgent best = null;
        float bestD2 = float.PositiveInfinity;
        Vector3 pos = agent.Position;
        for (int i = 0; i < targetPool.Count; i++)
        {
            var tgt = targetPool[i];
            if (tgt == null) continue;
            float d2 = (tgt.Position - pos).sqrMagnitude;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = tgt;
            }
        }
        if (best != null) agent.SetTarget(best);
    }

    public void DestroyAgent(HuntingBoidAgent agent)
    {
        if (agent == null) return;
        hunters.Remove(agent);
        Destroy(agent.gameObject);
    }

    public void RefreshTargetPool()
    {
        targetPool.Clear();
        var foundAgents = FindObjectsOfType<BoidAgent>(false);
        for (int i = 0; i < foundAgents.Length; i++)
        {
            if (!IsTargetLayerAllowed(foundAgents[i].gameObject.layer)) continue;
            bool tagOk = string.IsNullOrEmpty(targetTag) || foundAgents[i].CompareTag(targetTag);
            if (!requireTargetTag || tagOk)
                targetPool.Add(foundAgents[i]);
        }

        // Fallback: if no agents match the tag, track all BoidAgents so hunters still have a target.
        if (targetPool.Count == 0)
        {
            for (int i = 0; i < foundAgents.Length; i++)
            {
                if (IsTargetLayerAllowed(foundAgents[i].gameObject.layer))
                    targetPool.Add(foundAgents[i]);
            }
        }

        // If still empty, attach passive BoidAgent components to tagged objects so they can be targeted.
        if (targetPool.Count == 0 && !string.IsNullOrEmpty(targetTag))
        {
            var taggedObjects = GameObject.FindGameObjectsWithTag(targetTag);
            for (int i = 0; i < taggedObjects.Length; i++)
            {
                var go = taggedObjects[i];
                if (go == null) continue;
                if (!IsTargetLayerAllowed(go.layer)) continue;
                var proxy = go.GetComponent<BoidAgent>();
                if (proxy == null)
                    proxy = go.AddComponent<BoidAgent>(); // passive: controller stays null
                targetPool.Add(proxy);
            }
        }
    }

    public void OnCapturedTarget(BoidAgent target)
    {
        if (target != null)
        {
            if (target.controller != null)
                target.controller.RemoveAgent(target);
            else
                Destroy(target.gameObject);
        }
        RefreshTargetPool();
    }

    bool IsTargetLayerAllowed(int layer)
    {
        return (targetLayers.value & (1 << layer)) != 0;
    }

    void OnDrawGizmos()
    {
        Vector3 centerPoint = spawnCenter ? spawnCenter.position : (huntingArea ? huntingArea.bounds.center : transform.position);

        Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.08f);
        Gizmos.DrawSphere(centerPoint, spawnRadius);

        Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.3f);
        Gizmos.DrawWireSphere(centerPoint, spawnRadius);
    }
}
