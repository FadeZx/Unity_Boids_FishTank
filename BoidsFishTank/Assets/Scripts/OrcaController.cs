﻿﻿#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Cinemachine;

[DefaultExecutionOrder(-49)] // after BoidController (-50)
public class OrcaController : MonoBehaviour
{
    [Header("References")]
    public BoxCollider simulationArea;         // same tank
    public BoidController preyController;      // assign your prey BoidController
    public OrcaAgent orcaPrefab;               // predator prefab

    [Header("Role Counts")]
    public int leaders = 1;
    public int flankers = 3;
    public int strikers = 2;
    public int supports = 2;

    [Header("Speeds")]
    public float minSpeed = 2.2f;
    public float maxSpeed = 6.0f;
    public float maxSteerForce = 10.0f;

    [Header("Swimming")]
    [Tooltip("Weight to keep orcas near a preferred water depth to avoid constant nose-diving.")]
    public float wDepth = 1.2f;
    [Range(0f, 1f), Tooltip("0 = bottom, 1 = surface. Preferred center depth within the tank.")]
    public float depthCenterBias = 0.5f;
    [Range(0f, 1f), Tooltip("Blend toward prey height: 0 = ignore prey height, 1 = match prey height.")]
    public float depthFollowPrey = 0.4f;
    [Header("Neighborhood (pod cohesion)")]
    public float neighborRadius = 3.0f;
    public float separationRadius = 0.9f;

    [Header("Weights (pod rules)")]
    public float wSeparation = 1.3f;
    public float wAlignment = 0.8f;
    public float wCohesion = 0.8f;

    [Header("Hunt Weights")]
    public float wPursuit = 2.0f;   // Leader & Striker
    public float wEncircle = 2.2f;   // Flankers circling
    public float wCorral = 1.6f;   // Support behind/beside prey

    [Header("Encirclement")]
    public float encircleRadius = 4.0f;       // radius around prey centroid
    public float flankOffsetAngle = 45f;      // degrees around ring

    [Header("Strike")]
    public float strikeRange = 3.0f;          // start dash when within
    public float strikeBoost = 1.6f;          // speed multiplier during strike
    public float strikeCooldown = 2.5f;       // seconds

    [Header("Obstacle & Boundary Avoidance")]
    public LayerMask obstacleMask;
    public float avoidDistance = 2.5f;
    public float avoidProbeAngle = 25f;
    public float orcaRadius = 0.25f;
    [Tooltip("Distance from walls where orcas start steering away (soft boundary).")]
    public float boundaryAvoidRadius = 1.2f;


    [Header("Targeting")]
    [Tooltip("How often to re-evaluate prey targets per orca (seconds).")]
    public float retargetInterval = 0.6f;
    [Tooltip("Max number of orcas allowed to focus the same prey to improve coverage.")]
    public int maxOrcasPerPrey = 2;
    [Tooltip("Bias toward closer prey.")]
    public float wTargetDistance = 1.0f;
    [Tooltip("Bias toward isolated prey (few neighbors).")]
    public float wTargetIsolation = 1.0f;

    [Tooltip("Force all roles to share Leader's target.")]
    public bool shareLeaderTarget = true;

    float retargetTimer = 0f;

    [Header("Labels & Stats")]
    [Tooltip("Show role text labels above each orca (Leader/Flanker/Striker/Support).")]
    public bool showRoleText = false;
    [Tooltip("Camera used for role label billboarding. If not set, Camera.main is used.")]
    public Camera labelCamera;

    [Header("Cinemachine")]
    [Tooltip("Assign your FreeLook camera (v3 uses CinemachineCamera with FreeLook components). Assign your FreeLook CinemachineCamera here.")]
    public CinemachineCamera freeLook;
    [Tooltip("Assign your Target Group for overview camera.")]
    public CinemachineTargetGroup targetGroup;
    [Tooltip("Assign your overview Virtual Camera that looks at the Target Group.")]
    public CinemachineCamera overviewCamera;

    [Tooltip("Total number of prey killed by orcas this session.")]
    public int killCount = 0;

    public readonly List<OrcaAgent> pod = new();
    Vector3 preyCentroid, preyAvgVel;

    // UI
    bool showUI = true; // F2
    Vector2 scroll;
    const string kPrefs = "Orca_Settings_JSON";
    string JsonPath => Path.Combine(Application.persistentDataPath, "orca_settings.json");

    void Start()
    {
        if (!simulationArea || !orcaPrefab || !preyController)
        {
            Debug.LogError("OrcaController: assign simulationArea, orcaPrefab, preyController.");
            enabled = false; return;
        }
        TryLoad();
        if (labelCamera == null) labelCamera = Camera.main;

        // Auto-find Cinemachine components if not assigned
        AutoFindCinemachine();

        SpawnPod();
        // Populate target group with spawned orcas
        SyncTargetGroup();
    }

    void Update()
    {
        if (KeyDown_F2()) showUI = !showUI;

        // compute prey centroid/avg vel once per frame
        GetPreyStats(out preyCentroid, out preyAvgVel);

        // periodic target assignment for each orca
        AssignTargetsPeriodically(Time.deltaTime);

        // share Leader's target with the whole pod if enabled
        if (shareLeaderTarget)
        {
            BoidAgent leadersTarget = null;
            foreach (var o in pod)
            {
                if (o.role == OrcaRole.Leader && o.CurrentTarget != null)
                {
                    leadersTarget = o.CurrentTarget;
                    break;
                }
            }
            if (leadersTarget != null)
            {
                foreach (var o in pod)
                {
                    if (o.CurrentTarget != leadersTarget)
                        o.SetTarget(leadersTarget);
                }
            }
        }

       
    }

    // ---------------- Spawning / Roles ----------------
    public void SpawnPod()
    {
        // Reset kill count when starting over
        killCount = 0;
        Clear();
        var b = simulationArea.bounds;

        void SpawnRole(int count, OrcaRole role)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 p = new Vector3(
                    UnityEngine.Random.Range(b.min.x, b.max.x),
                    UnityEngine.Random.Range(b.min.y, b.max.y),
                    UnityEngine.Random.Range(b.min.z, b.max.z)
                );
                var a = Instantiate(orcaPrefab, p, Quaternion.identity, transform);
                a.controller = this;
                a.role = role;
                a.name = $"{role} {i+1}"; // set object name to role instead of (Clone)
                a.Velocity = UnityEngine.Random.insideUnitSphere.normalized * UnityEngine.Random.Range(minSpeed, maxSpeed);
                pod.Add(a);
            }
        }

        SpawnRole(Mathf.Max(1, leaders), OrcaRole.Leader);
        SpawnRole(Mathf.Max(0, flankers), OrcaRole.Flanker);
        SpawnRole(Mathf.Max(0, strikers), OrcaRole.Striker);
        SpawnRole(Mathf.Max(0, supports), OrcaRole.Support);

        // Sync TargetGroup members
        SyncTargetGroup();
    }

    public void Clear()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);
        pod.Clear();
    }

    // ---------------- Steering Core ----------------
    public Vector3 ComputeSteering(OrcaAgent self, float dt, out (Vector3 podSep, Vector3 podAli, Vector3 podCoh, Vector3 role, Vector3 avoid) dbg)
    {
        Vector3 pos = self.Position;
        Vector3 vel = self.Velocity;

        // --- pod rules (like boids for orcas) ---
        Vector3 sep = Vector3.zero, ali = Vector3.zero, coh = Vector3.zero;
        int n = 0;
        float nr2 = neighborRadius * neighborRadius;
        float sr2 = separationRadius * separationRadius;

        foreach (var o in pod)
        {
            if (o == self) continue;
            Vector3 to = o.Position - pos;
            float d2 = to.sqrMagnitude;
            if (d2 > nr2) continue;
            n++;
            if (d2 < sr2) sep -= to.normalized / Mathf.Max(0.001f, Mathf.Sqrt(d2));
            ali += o.Velocity;
            coh += o.Position;
        }
        if (n > 0)
        {
            ali = (ali / n).normalized * maxSpeed - vel;
            coh = ((coh / n) - pos);
        }
        if (sep.sqrMagnitude > 1e-6f) sep = sep.normalized * maxSpeed - vel;

        // --- role-based steering toward prey ---
        Vector3 roleForce = RoleForce(self, pos, vel, preyCentroid, preyAvgVel, dt);

        // --- depth keeping (prevent nose-diving to floor) ---
        var bnd = simulationArea.bounds;
        float centerY = Mathf.Lerp(bnd.min.y, bnd.max.y, depthCenterBias);
        float targetY = Mathf.Lerp(centerY, preyCentroid.y, Mathf.Clamp01(depthFollowPrey));
        Vector3 depthForce = new Vector3(0f, targetY - pos.y, 0f);

        // --- obstacle avoidance ---
        Vector3 avoid = ObstacleAvoid(pos, vel);
        Vector3 boundaryAvoid = BoundaryAvoid(pos, vel);

        // blend
        Vector3 steer = wSeparation * sep + wAlignment * ali + wCohesion * coh
                      + roleForce + avoid + boundaryAvoid + wDepth * depthForce;

        // limit
        if (steer.sqrMagnitude > maxSteerForce * maxSteerForce)
            steer = steer.normalized * maxSteerForce;

        dbg = (sep, ali, coh, roleForce, avoid);
        return steer;
    }

    Vector3 RoleForce(OrcaAgent self, Vector3 pos, Vector3 vel, Vector3 preyCtr, Vector3 preyVel, float dt)
    {
        Vector3 f = Vector3.zero;

        // Choose individual prey target with hysteresis
        BoidAgent target = self.CurrentTarget;
        // Clear invalid
        if (target != null && (target.controller == null || target.gameObject == null))
            self.ClearTarget();
        target = self.CurrentTarget;
        // Attempt lock-on if no target
        if (target == null && preyController != null && preyController.agents.Count > 0)
        {
            target = FindNearestPrey(pos);
            self.SetTarget(target);
        }
        // Consider switching only when hold timer elapsed and a clearly better candidate exists
        if (self.CanSwitchTarget() && preyController != null && preyController.agents.Count > 0)
        {
            var best = FindNearestPrey(pos);
            if (best != null && best != target)
            {
                float currentDist = (target != null) ? (target.Position - pos).sqrMagnitude : float.PositiveInfinity;
                float bestDist = (best.Position - pos).sqrMagnitude;
                // Switch if new is significantly closer (30% closer) or current got too far
                if (bestDist < currentDist * 0.7f || currentDist > strikeRange * strikeRange * 4f)
                    self.SetTarget(best);
            }
        }

        // Drive behavior by target if locked, else fall back to centroid
        Vector3 aimCtr = self.HasTarget ? self.CurrentTarget.Position : preyCtr;
        Vector3 aimVel = self.HasTarget ? self.CurrentTarget.Velocity : preyVel;

        Vector3 toPrey = aimCtr - pos;
        float dist = toPrey.magnitude;
        Vector3 preyDir = (aimVel.sqrMagnitude > 1e-6f) ? aimVel.normalized : Vector3.forward;

        // Intercept point for pursuit
        float tLead = Mathf.Clamp(dist / Mathf.Max(0.1f, maxSpeed + aimVel.magnitude), 0.1f, 2.0f);
        Vector3 intercept = aimCtr + aimVel * tLead;

        switch (self.role)
        {
            case OrcaRole.Leader:
                // Strong pursuit toward intercept
                f += wPursuit * (intercept - pos);
                break;

            case OrcaRole.Flanker:
                // With shared target enabled, also converge toward intercept for reliable collision
                if (shareLeaderTarget)
                {
                    f += wEncircle * (intercept - pos);
                }
                else
                {
                    // Move to ring around prey, offset left/right relative to prey heading
                    int index = IndexAmongRole(self, OrcaRole.Flanker);
                    float sign = (index % 2 == 0) ? 1f : -1f;
                    float angle = flankOffsetAngle * (1 + index / 2); // 45, 90, ...
                    Quaternion q = Quaternion.AngleAxis(sign * angle, Vector3.up);
                    Vector3 tangent = q * preyDir;
                    Vector3 ringTarget = preyCtr + tangent.normalized * encircleRadius;
                    f += wEncircle * (ringTarget - pos);
                }
                break;

            case OrcaRole.Striker:
                // If close enough, dash straight at intercept; else behave like flanker closing in
                if (dist <= strikeRange && self.CanStrike())
                {
                    Vector3 dash = (intercept - pos).normalized * (maxSpeed * strikeBoost);
                    f += wPursuit * (dash - vel); // quick acceleration toward dash dir
                    self.ResetStrikeCooldown();
                }
                else
                {
                    // pre-strike encircle tightening
                    Vector3 ringDir = Vector3.Cross(preyDir, Vector3.up).normalized;
                    Vector3 targetS = preyCtr + ringDir * (encircleRadius * 0.7f);
                    f += (wEncircle + 0.6f) * (targetS - pos);
                }
                break;

            case OrcaRole.Support:
                // With shared target, also help converge to ensure contact
                if (shareLeaderTarget)
                {
                    f += wCorral * (intercept - pos);
                }
                else
                {
                    // Stay slightly behind the prey direction to corral (herding)
                    Vector3 behind = preyCtr - preyDir * (encircleRadius * 1.1f);
                    f += wCorral * (behind - pos);
                }
                break;
        }

        // Convert desired-direction forces to steering (desired vel - current vel)
        if (f.sqrMagnitude > 1e-8f)
        {
            Vector3 desired = f.normalized * maxSpeed;
            return desired - vel;
        }
        return Vector3.zero;
    }

    int IndexAmongRole(OrcaAgent self, OrcaRole role)
    {
        int idx = 0;
        foreach (var a in pod)
        {
            if (a.role != role) continue;
            if (a == self) return idx;
            idx++;
        }
        return 0;
    }

    Vector3 ObstacleAvoid(Vector3 pos, Vector3 vel)
    {
        if (vel.sqrMagnitude < 1e-8f) return Vector3.zero;
        float probe = avoidDistance;
        Vector3 fwd = vel.normalized;

        Vector3[] dirs = new Vector3[]
        {
            fwd,
            Quaternion.AngleAxis( avoidProbeAngle, Vector3.up) * fwd,
            Quaternion.AngleAxis(-avoidProbeAngle, Vector3.up) * fwd,
            Quaternion.AngleAxis( avoidProbeAngle, Vector3.right) * fwd,
            Quaternion.AngleAxis(-avoidProbeAngle, Vector3.right) * fwd,
        };

        for (int i = 0; i < dirs.Length; i++)
        {
            if (Physics.SphereCast(pos, orcaRadius, dirs[i], out RaycastHit hit, probe, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 slide = Vector3.ProjectOnPlane(fwd, hit.normal).normalized;
                float t = 1f - Mathf.Clamp01(hit.distance / probe);
                return slide * (maxSpeed * (0.75f + 1.25f * t)) - vel * 0.1f;
            }
        }
        return Vector3.zero;
    }

    // Soft avoidance from tank boundaries before hard box reflection
    Vector3 BoundaryAvoid(Vector3 pos, Vector3 vel)
    {
        var b = simulationArea.bounds;
        Vector3 steer = Vector3.zero;
        float pad = boundaryAvoidRadius;

        // X walls
        if (pos.x - b.min.x < pad)
            steer += Vector3.right * (1f - Mathf.Clamp01((pos.x - b.min.x) / pad));
        else if (b.max.x - pos.x < pad)
            steer += Vector3.left * (1f - Mathf.Clamp01((b.max.x - pos.x) / pad));

        // Y walls
        if (pos.y - b.min.y < pad)
            steer += Vector3.up * (1f - Mathf.Clamp01((pos.y - b.min.y) / pad));
        else if (b.max.y - pos.y < pad)
            steer += Vector3.down * (1f - Mathf.Clamp01((b.max.y - pos.y) / pad));

        // Z walls
        if (pos.z - b.min.z < pad)
            steer += Vector3.forward * (1f - Mathf.Clamp01((pos.z - b.min.z) / pad));
        else if (b.max.z - pos.z < pad)
            steer += Vector3.back * (1f - Mathf.Clamp01((b.max.z - pos.z) / pad));

        if (steer.sqrMagnitude > 1e-8f)
        {
            steer = steer.normalized * maxSpeed - vel * 0.2f;
        }
        return steer;
    }

    // Hard keep-in-box (same idea you used)
    public void EnforceBounds(ref Vector3 pos, ref Vector3 vel, float bounciness = 0.25f, float skin = 0.01f)
    {
        var b = simulationArea.bounds;
        bool hit = false;
        Vector3 n = Vector3.zero;
        if (pos.x < b.min.x + skin) { pos.x = b.min.x + skin; n += Vector3.right; hit = true; }
        else if (pos.x > b.max.x - skin) { pos.x = b.max.x - skin; n += Vector3.left; hit = true; }
        if (pos.y < b.min.y + skin) { pos.y = b.min.y + skin; n += Vector3.up; hit = true; }
        else if (pos.y > b.max.y - skin) { pos.y = b.max.y - skin; n += Vector3.down; hit = true; }
        if (pos.z < b.min.z + skin) { pos.z = b.min.z + skin; n += Vector3.forward; hit = true; }
        else if (pos.z > b.max.z - skin) { pos.z = b.max.z - skin; n += Vector3.back; hit = true; }
        if (hit && vel.sqrMagnitude > 1e-8f)
        {
            n = n.normalized;
            vel = Vector3.Reflect(vel, n) * (1f - bounciness);
            pos += n * skin;
        }
    }

    void GetPreyStats(out Vector3 centroid, out Vector3 avgVel)
    {
        centroid = Vector3.zero; avgVel = Vector3.zero;
        if (preyController == null || preyController.agents.Count == 0) return;

        int count = preyController.agents.Count;
        for (int i = 0; i < count; i++)
        {
            centroid += preyController.agents[i].Position;
            avgVel += preyController.agents[i].Velocity;
        }
        centroid /= count;
        avgVel /= Mathf.Max(1, count);
    }

    // ---------------- Targeting ----------------
    void AssignTargetsPeriodically(float dt)
    {
        retargetTimer -= dt;
        if (retargetTimer > 0f) return;
        retargetTimer = retargetInterval;
        if (preyController == null || preyController.agents.Count == 0 || pod.Count == 0) return;

        // Build map: how many orcas currently on each prey
        var preyToCount = new Dictionary<BoidAgent, int>();
        foreach (var o in pod)
        {
            if (o.CurrentTarget != null)
            {
                if (!preyToCount.ContainsKey(o.CurrentTarget)) preyToCount[o.CurrentTarget] = 0;
                preyToCount[o.CurrentTarget]++;
            }
        }

        foreach (var o in pod)
        {
            // Skip switch if in hold
            if (!o.CanSwitchTarget()) continue;
            var best = FindBestPreyFor(o, preyToCount);
            if (best != null && best != o.CurrentTarget)
            {
                o.SetTarget(best);
                if (!preyToCount.ContainsKey(best)) preyToCount[best] = 0;
                preyToCount[best]++;
            }
        }
    }

    BoidAgent FindBestPreyFor(OrcaAgent orca, Dictionary<BoidAgent, int> preyToCount)
    {
        BoidAgent best = null;
        float bestScore = float.NegativeInfinity;
        Vector3 pos = orca.Position;
        var list = preyController.agents;
        for (int i = 0; i < list.Count; i++)
        {
            var prey = list[i];
            // limit share
            int c = preyToCount.TryGetValue(prey, out int v) ? v : 0;
            if (c >= maxOrcasPerPrey) continue;

            // distance term (closer is better)
            float d2 = (prey.Position - pos).sqrMagnitude;
            float distScore = 1f / Mathf.Max(0.1f, Mathf.Sqrt(d2));

            // isolation term: fewer neighbors nearby increases score
            int neighbors = CountPreyNeighbors(prey.Position, 1.5f);
            float isolationScore = 1f / Mathf.Max(1f, neighbors);

            float score = wTargetDistance * distScore + wTargetIsolation * isolationScore;
            if (score > bestScore)
            {
                bestScore = score;
                best = prey;
            }
        }
        return best;
    }

    BoidAgent FindNearestPrey(Vector3 pos)
    {
        BoidAgent best = null; float bestD2 = float.PositiveInfinity;
        var list = preyController.agents;
        for (int i = 0; i < list.Count; i++)
        {
            float d2 = (list[i].Position - pos).sqrMagnitude;
            if (d2 < bestD2) { bestD2 = d2; best = list[i]; }
        }
        return best;
    }

    int CountPreyNeighbors(Vector3 center, float radius)
    {
        int count = 0;
        float r2 = radius * radius;
        var list = preyController.agents;
        for (int i = 0; i < list.Count; i++)
        {
            Vector3 to = list[i].Position - center;
            if (to.sqrMagnitude <= r2) count++;
        }
        return count;
    }

    // ---------------- UI (F2) + Save/Load ----------------
    [Serializable]
    class OrcaSettings
    {
        public int leaders, flankers, strikers, supports;
        public float minSpeed, maxSpeed, maxSteerForce;
        public float neighborRadius, separationRadius;
        public float wSeparation, wAlignment, wCohesion;
        public float wPursuit, wEncircle, wCorral;
        public float encircleRadius, flankOffsetAngle;
        public float strikeRange, strikeBoost, strikeCooldown;
        public float avoidDistance, avoidProbeAngle, orcaRadius;
        public float wDepth, depthCenterBias, depthFollowPrey;
        public bool drawDebug;
        public bool showRoleText;
        public int killCount;
    }

    OrcaSettings Collect() => new()
    {
        leaders = leaders,
        flankers = flankers,
        strikers = strikers,
        supports = supports,
        minSpeed = minSpeed,
        maxSpeed = maxSpeed,
        maxSteerForce = maxSteerForce,
        neighborRadius = neighborRadius,
        separationRadius = separationRadius,
        wSeparation = wSeparation,
        wAlignment = wAlignment,
        wCohesion = wCohesion,
        wPursuit = wPursuit,
        wEncircle = wEncircle,
        wCorral = wCorral,
        encircleRadius = encircleRadius,
        flankOffsetAngle = flankOffsetAngle,
        strikeRange = strikeRange,
        strikeBoost = strikeBoost,
        strikeCooldown = strikeCooldown,
        avoidDistance = avoidDistance,
        avoidProbeAngle = avoidProbeAngle,
        orcaRadius = orcaRadius,
        wDepth = wDepth,
        depthCenterBias = depthCenterBias,
        depthFollowPrey = depthFollowPrey,
        showRoleText = showRoleText,
        killCount = killCount
    };

    void Apply(OrcaSettings s, bool respawn)
    {
        if (s == null) return;
        bool countsChanged = (leaders != s.leaders || flankers != s.flankers || strikers != s.strikers || supports != s.supports);

        leaders = s.leaders; flankers = s.flankers; strikers = s.strikers; supports = s.supports;
        minSpeed = s.minSpeed; maxSpeed = s.maxSpeed; maxSteerForce = s.maxSteerForce;
        neighborRadius = s.neighborRadius; separationRadius = s.separationRadius;
        wSeparation = s.wSeparation; wAlignment = s.wAlignment; wCohesion = s.wCohesion;
        wPursuit = s.wPursuit; wEncircle = s.wEncircle; wCorral = s.wCorral;
        encircleRadius = s.encircleRadius; flankOffsetAngle = s.flankOffsetAngle;
        strikeRange = s.strikeRange; strikeBoost = s.strikeBoost; strikeCooldown = s.strikeCooldown;
        avoidDistance = s.avoidDistance; avoidProbeAngle = s.avoidProbeAngle; orcaRadius = s.orcaRadius;
        wDepth = s.wDepth; depthCenterBias = s.depthCenterBias; depthFollowPrey = s.depthFollowPrey;
        showRoleText = s.showRoleText;
        killCount = s.killCount;

        if (respawn && countsChanged) SpawnPod();
    }

    void SaveToFile()
    {
        try { File.WriteAllText(JsonPath, JsonUtility.ToJson(Collect(), true)); }
        catch (Exception e) { Debug.LogError(e.Message); }
    }
    void LoadFromFile()
    {
        try
        {
            if (!File.Exists(JsonPath)) { Debug.LogWarning("No orca settings file."); return; }
            var s = JsonUtility.FromJson<OrcaSettings>(File.ReadAllText(JsonPath));
            Apply(s, true);
        }
        catch (Exception e) { Debug.LogError(e.Message); }
    }
    void SaveToPrefs()
    {
        PlayerPrefs.SetString(kPrefs, JsonUtility.ToJson(Collect(), false));
        PlayerPrefs.Save();
    }
    void LoadFromPrefs()
    {
        if (!PlayerPrefs.HasKey(kPrefs)) { Debug.LogWarning("No orca prefs."); return; }
        var s = JsonUtility.FromJson<OrcaSettings>(PlayerPrefs.GetString(kPrefs));
        Apply(s, true);
    }
    void TryLoad()
    {
        if (File.Exists(JsonPath)) LoadFromFile();
        else if (PlayerPrefs.HasKey(kPrefs)) LoadFromPrefs();
    }

    void AutoFindCinemachine()
    {
        // Try to auto-resolve references so UI buttons work without manual assignment
        if (freeLook == null)
            freeLook = FindFirstObjectByType<CinemachineCamera>();
        if (targetGroup == null)
            targetGroup = FindFirstObjectByType<CinemachineTargetGroup>();
        if (overviewCamera == null)
            overviewCamera = FindFirstObjectByType<CinemachineCamera>();
    }

    void SyncTargetGroup()
    {
        if (targetGroup == null) return;
        // Clear existing members and add pod transforms
        var targets = new List<CinemachineTargetGroup.Target>(pod.Count);
        for (int i = 0; i < pod.Count; i++)
        {
            targets.Add(new CinemachineTargetGroup.Target
            {
                Object = pod[i].transform,
                Weight = 1f,
                Radius = 1.5f
            });
        }
        targetGroup.Targets = targets;
    }

    // ---------------- Cinemachine control ----------------
    void Follow(OrcaAgent target)
    {
        if (freeLook == null || target == null) return;
        freeLook.Follow = target.transform;
        freeLook.LookAt = target.transform;
        freeLook.Priority = 20;
        if (overviewCamera != null) overviewCamera.Priority = 10;
    }
    void FollowByName(string roleName)
    {
        var t = pod.Find(o => o.name.StartsWith(roleName, StringComparison.OrdinalIgnoreCase));
        if (t != null) Follow(t);
    }
    void ActivateOverviewCamera()
    {
        if (overviewCamera == null) return;
        overviewCamera.Priority = 25;
        if (freeLook != null) freeLook.Priority = 10;
    }

    public void ResetKillCount() { killCount = 0; }

    void OnGUI()
    {
        if (!showUI) return;
        const int w = 340;
        Rect r = new Rect(Screen.width - w - 12, 12, w, Screen.height - 24);
        GUILayout.BeginArea(r, GUI.skin.box);
        GUILayout.Label("<b>Orca Pod (Predators)</b>", new GUIStyle(GUI.skin.label) { richText = true });

        scroll = GUILayout.BeginScrollView(scroll);

        GUILayout.Label("<b>Roles</b>", new GUIStyle(GUI.skin.label) { richText = true });
        leaders = IntSliderT("Leaders", "Number of leaders (strong pursuit toward intercept).", leaders, 1, 4);
        flankers = IntSliderT("Flankers", "Orcas that orbit prey on a ring to constrain it.", flankers, 0, 16);
        strikers = IntSliderT("Strikers", "Orcas that dash in to strike when close.", strikers, 0, 16);
        supports = IntSliderT("Supports", "Orcas that stay behind prey to corral it.", supports, 0, 16);
        if (GUILayout.Button("Respawn Pod")) SpawnPod();

        GUILayout.Space(6);
        GUILayout.Label("<b>Speeds</b>");
        minSpeed = SliderT("Min Speed", "Minimum cruising speed. Prevents orcas from stalling.", minSpeed, 0.1f, maxSpeed);
        maxSpeed = SliderT("Max Speed", "Top speed used for desired velocities and dashes.", maxSpeed, minSpeed, 20f);
        maxSteerForce = SliderT("Max Steer", "Upper limit on steering force to avoid jitter.", maxSteerForce, 0.1f, 30f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Pod Rules</b>");
        neighborRadius = SliderT("Neighbor Radius", "How far pod-mates influence alignment/cohesion.", neighborRadius, 0.1f, 10f);
        separationRadius = SliderT("Separation Radius", "Distance where strong separation kicks in.", separationRadius, 0.05f, neighborRadius);
        wSeparation = SliderT("W Separation", "Weight of separation (spread apart).", wSeparation, 0f, 10f);
        wAlignment = SliderT("W Alignment", "Weight of alignment (match headings).", wAlignment, 0f, 10f);
        wCohesion = SliderT("W Cohesion", "Weight of cohesion (stay together).", wCohesion, 0f, 10f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Hunt</b>");
        wPursuit = SliderT("W Pursuit", "Pursuit strength (leaders/strikers aim at an intercept).", wPursuit, 0f, 10f);
        wEncircle = SliderT("W Encircle", "Flankers circle radius pull (ring around prey).", wEncircle, 0f, 10f);
        wCorral = SliderT("W Corral", "Support tries to stay behind prey to herd it.", wCorral, 0f, 10f);
        encircleRadius = SliderT("Encircle Radius", "Ring radius used for encirclement around prey.", encircleRadius, 0.5f, 20f);
        flankOffsetAngle = SliderT("Flank Angle", "Spacing angle offsets around the ring for flankers.", flankOffsetAngle, 0f, 160f);
        strikeRange = SliderT("Strike Range", "Distance threshold to trigger a strike dash.", strikeRange, 0.5f, 10f);
        strikeBoost = SliderT("Strike Boost", "Speed multiplier during strike dashes.", strikeBoost, 1f, 3f);
        strikeCooldown = SliderT("Strike Cooldown", "Cooldown between strikes for each striker.", strikeCooldown, 0f, 8f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Obstacles</b>");
        avoidDistance = SliderT("Avoid Dist", "Forward probe length for obstacle detection.", avoidDistance, 0.2f, 15f);
        avoidProbeAngle = SliderT("Avoid Angle", "Side probe spread to feel around obstacles.", avoidProbeAngle, 0f, 85f);
        orcaRadius = SliderT("Orca Radius", "Radius used for sweeps and spherecasts.", orcaRadius, 0.05f, 0.6f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Hunt Stats</b>");
        GUILayout.Label($"Kill Count: <b>{killCount}</b>", new GUIStyle(GUI.skin.label) { richText = true });
        if (GUILayout.Button("Reset Kill Count")) killCount = 0;

        GUILayout.Space(6);
        GUILayout.Label("<b>Swimming</b>");
        wDepth = SliderT("W Depth", "Weight to keep near preferred depth (blended with prey height).", wDepth, 0f, 5f);
        depthCenterBias = SliderT("Depth Center Bias", "Preferred vertical center in tank (0=bottom, 1=surface).", depthCenterBias, 0f, 1f);
        depthFollowPrey = SliderT("Depth Follow Prey", "Blend toward prey height for more natural pursuit.", depthFollowPrey, 0f, 1f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Labels</b>");
        showRoleText = ToggleT("Show Role Text", "Show text labels above each orca indicating its role.", showRoleText);

        GUILayout.Space(6);
        GUILayout.Label("<b>Camera</b>");
        GUILayout.BeginHorizontal();
        GUI.enabled = overviewCamera != null;
        if (GUILayout.Button("Top-Down: Target Group")) ActivateOverviewCamera();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        // Per-orca buttons by name
        foreach (var o in pod)
        {
            GUI.enabled = freeLook != null;
            if (GUILayout.Button($"FreeLook Follow: {o.name}")) Follow(o);
            GUI.enabled = true;
        }

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save → File")) SaveToFile();
        if (GUILayout.Button("Load ← File")) LoadFromFile();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save → PlayerPrefs")) SaveToPrefs();
        if (GUILayout.Button("Load ← PlayerPrefs")) LoadFromPrefs();
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label($"Save Path:<size=10>{Application.persistentDataPath}</size>", new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

        GUILayout.EndScrollView();

        // Hover tooltip display (always render container to keep layout consistent)
        string tip = GUI.tooltip;
        var tipStyle = new GUIStyle(GUI.skin.box) { wordWrap = true, fontSize = 11 };
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(string.IsNullOrEmpty(tip) ? " " : tip, tipStyle, GUILayout.ExpandWidth(true));
        GUILayout.EndVertical();

        GUILayout.EndArea();
    }

    // IMGUI helpers
    float Slider(string label, float v, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        v = GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString("0.00"), GUILayout.Width(50));
        GUILayout.EndHorizontal();
        return v;
    }
    int IntSlider(string label, int v, int min, int max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        v = (int)GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString(), GUILayout.Width(50));
        GUILayout.EndHorizontal();
        return Mathf.Clamp(v, min, max);
    }
    bool Toggle(string label, bool t)
    {
        GUILayout.BeginHorizontal();
        t = GUILayout.Toggle(t, "", GUILayout.Width(18));
        GUILayout.Label(label);
        GUILayout.EndHorizontal();
        return t;
    }

    // Tooltip-aware helpers
    float SliderT(string label, string tooltip, float v, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(140));
        v = GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString("0.00"), GUILayout.Width(50));
        GUILayout.EndHorizontal();
        return v;
    }
    int IntSliderT(string label, string tooltip, int v, int min, int max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(140));
        v = (int)GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString(), GUILayout.Width(50));
        GUILayout.EndHorizontal();
        return Mathf.Clamp(v, min, max);
    }
    bool ToggleT(string label, string tooltip, bool t)
    {
        GUILayout.BeginHorizontal();
        t = GUILayout.Toggle(t, new GUIContent("", tooltip), GUILayout.Width(18));
        GUILayout.Label(new GUIContent(label, tooltip));
        GUILayout.EndHorizontal();
        return t;
    }

    // input helpers
    bool KeyDown_F2()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.F2);
#endif
    }

}
