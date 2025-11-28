﻿﻿#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;

[DefaultExecutionOrder(-49)] // after BoidController (-50)
public class OrcaController : MonoBehaviour
{
    [Header("References")]
    public BoxCollider simulationArea;         // same tank
    public BoidController preyController;      // assign your prey BoidController
    public OrcaAgent orcaPrefab;               // predator prefab
    [Tooltip("Tank area collider - orcas will avoid spawning inside this area but use it for movement boundaries")]


    [Header("Role Counts")]
    public int leaders = 1;
    public int flankers = 3;
    public int strikers = 2;
    public int supports = 2;

    [Header("Spawning")]
    [Tooltip("Distance from tank center for spawning ring (gizmo shows exact radius you set)")]
    public float spawnRadius = 8.0f;
    [Tooltip("Center point for spawning ring (if not set, uses tank center when tank area exists)")]
    public Transform spawnCenter;
    [Tooltip("Maximum attempts to find valid spawn position outside tank")]
    public int maxSpawnAttempts = 50;

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
    [Tooltip("Optional max cap for obstacle probe length (0 = uncapped).")]
    public float avoidDistanceCap = 0f;
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

    [Header("Camera Control")]
    [Tooltip("Separate component that manages Cinemachine cameras and UI actions for this controller.")]
    public OrcaCameraController cameraController;

    [Tooltip("Total number of prey killed by orcas this session.")]
    public int killCount = 0;

    public readonly List<OrcaAgent> pod = new();
    Vector3 preyCentroid, preyAvgVel;
    NativeArray<float3> preyPositions;
    NativeParallelMultiHashMap<int, int> preyGrid;
    float preyCellSize = 1.5f;
    bool preyGridReady;

    // UI
    bool showUI = true; // always draw handle; F2 collapses/expands panel
    bool showPanel = true; // collapse/expand similar to audio UI
    float panelAnim = 1f; // 0 collapsed -> 1 expanded
    float panelAnimVel = 0f;
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

        if (!cameraController)
            cameraController = GetComponent<OrcaCameraController>();
        if (cameraController != null)
            cameraController.Initialize(this);

        SpawnPod();
        cameraController?.SyncTargetGroup(pod);
    }

    void OnDestroy()
    {
        DisposePreyGrid();
    }

    void Update()
    {
        // Toggle panel collapse/expand with F2
        if (KeyDown_F2()) showPanel = !showPanel;

        // compute prey centroid/avg vel once per frame
        GetPreyStats(out preyCentroid, out preyAvgVel);
        BuildPreyGrid();

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

#if UNITY_EDITOR
        // Force gizmo updates in editor when spawn center moves
        if (spawnCenter != null)
        {
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
#endif
        DisposePreyGrid();
    }

    void BuildPreyGrid()
    {
        DisposePreyGrid();
        var list = preyController != null ? preyController.agents : null;
        if (list == null || list.Count == 0) return;

        int count = list.Count;
        preyPositions = new NativeArray<float3>(count, Allocator.TempJob);
        for (int i = 0; i < count; i++)
            preyPositions[i] = list[i].transform.position;

        preyCellSize = Mathf.Max(0.25f, preyController.neighborRadius);
        int capacity = Mathf.Max(1, count * 4);
        preyGrid = new NativeParallelMultiHashMap<int, int>(capacity, Allocator.TempJob);
        for (int i = 0; i < count; i++)
        {
            int3 cell = (int3)math.floor(preyPositions[i] / preyCellSize);
            preyGrid.Add(Hash(cell), i);
        }
        preyGridReady = true;
    }

    void DisposePreyGrid()
    {
        if (preyPositions.IsCreated) preyPositions.Dispose();
        if (preyGrid.IsCreated) preyGrid.Dispose();
        preyGridReady = false;
    }

    // ---------------- Spawning / Roles ----------------
    public void SpawnPod()
    {
        // Reset kill count when starting over
        killCount = 0;
        Clear();
        var b = simulationArea.bounds;
        Vector3 centerPoint = spawnCenter ? spawnCenter.position : simulationArea.bounds.center;

        void SpawnRole(int count, OrcaRole role)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 p = GetValidSpawnPosition(centerPoint, b);
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
        cameraController?.SyncTargetGroup(pod);
    }

    Vector3 GetValidSpawnPosition(Vector3 centerPoint, Bounds area)
    {
        // Always use the centerPoint passed in (which is already spawn center or fallback)
        Vector3 spawnCenterPos = centerPoint;
        Bounds bounds = simulationArea != null ? simulationArea.bounds : area;

        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            // Uniformly pick a point inside the requested spawn radius
            Vector3 candidatePos = spawnCenterPos + UnityEngine.Random.insideUnitSphere * spawnRadius;

            // Keep within the simulation bounds if we have them
            candidatePos.x = Mathf.Clamp(candidatePos.x, bounds.min.x, bounds.max.x);
            candidatePos.y = Mathf.Clamp(candidatePos.y, bounds.min.y, bounds.max.y);
            candidatePos.z = Mathf.Clamp(candidatePos.z, bounds.min.z, bounds.max.z);

            return candidatePos;
        }

        // Fallback: use center if something goes wrong
        return spawnCenterPos;
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

            bool withinNeighbor = d2 <= nr2;
            bool withinSeparation = d2 <= sr2;
            if (!withinNeighbor && !withinSeparation) continue;

            if (withinSeparation)
                sep -= to.normalized / Mathf.Max(0.001f, Mathf.Sqrt(d2));

            if (withinNeighbor)
            {
                n++;
                ali += o.Velocity;
                coh += o.Position;
            }
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
                    self.NotifyStrikeBoost();
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
        float probe = avoidDistanceCap > 0f ? Mathf.Min(avoidDistance, avoidDistanceCap) : avoidDistance;
        Vector3 fwd = vel.normalized;

        // Forward spherecast; if clear, alternate a single side sweep each frame (cuts cast count).
        if (Physics.SphereCast(pos, orcaRadius, fwd, out RaycastHit hit, probe, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 slide = Vector3.ProjectOnPlane(fwd, hit.normal).normalized;
            float t = 1f - Mathf.Clamp01(hit.distance / probe);
            return slide * (maxSpeed * (0.8f + 0.6f * t)) - vel * 0.1f;
        }

        bool useLeft = (Time.frameCount & 1) == 0;
        Quaternion sideQ = Quaternion.AngleAxis(useLeft ? -avoidProbeAngle : avoidProbeAngle, Vector3.up);
        Vector3 sideDir = sideQ * fwd;
        float sideProbe = probe * 0.7f;
        if (Physics.SphereCast(pos, orcaRadius, sideDir, out hit, sideProbe, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 slide = Vector3.ProjectOnPlane(fwd, hit.normal).normalized;
            float t = 1f - Mathf.Clamp01(hit.distance / sideProbe);
            return slide * (maxSpeed * (0.7f + 0.5f * t)) - vel * 0.1f;
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
        var list = preyController.agents;
        if (!preyGridReady)
        {
            BoidAgent bestFallback = null; float bestD2Fallback = float.PositiveInfinity;
            for (int i = 0; i < list.Count; i++)
            {
                float d2 = (list[i].Position - pos).sqrMagnitude;
                if (d2 < bestD2Fallback) { bestD2Fallback = d2; bestFallback = list[i]; }
            }
            return bestFallback;
        }

        float bestD2 = float.PositiveInfinity;
        BoidAgent best = null;
        int3 cell = (int3)math.floor((float3)pos / preyCellSize);
        float searchR = preyController.neighborRadius * 1.5f;
        float searchR2 = searchR * searchR;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int3 c = cell + new int3(dx, dy, dz);
            var it = preyGrid.GetValuesForKey(Hash(c));
            while (it.MoveNext())
            {
                int idx = it.Current;
                float d2 = math.lengthsq(preyPositions[idx] - (float3)pos);
                if (d2 < bestD2 && d2 <= searchR2)
                {
                    bestD2 = d2;
                    best = list[idx];
                }
            }
        }
        return best;
    }

    int CountPreyNeighbors(Vector3 center, float radius)
    {
        var list = preyController.agents;
        if (!preyGridReady)
        {
            int c = 0;
            float r2 = radius * radius;
            for (int i = 0; i < list.Count; i++)
            {
                Vector3 to = list[i].Position - center;
                if (to.sqrMagnitude <= r2) c++;
            }
            return c;
        }

        int count = 0;
        float r2p = radius * radius;
        int3 cell = (int3)math.floor((float3)center / preyCellSize);
        int range = Mathf.CeilToInt(radius / preyCellSize) + 1;
        for (int dz = -range; dz <= range; dz++)
        for (int dy = -range; dy <= range; dy++)
        for (int dx = -range; dx <= range; dx++)
        {
            int3 c = cell + new int3(dx, dy, dz);
            var it = preyGrid.GetValuesForKey(Hash(c));
            while (it.MoveNext())
            {
                int idx = it.Current;
                float d2 = math.lengthsq(preyPositions[idx] - (float3)center);
                if (d2 <= r2p) count++;
            }
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
        public float avoidDistance, avoidDistanceCap, avoidProbeAngle, orcaRadius;
        public float wDepth, depthCenterBias, depthFollowPrey;
        public bool drawDebug;
        public bool showRoleText;
        public int killCount;
        public float spawnRadius;
        public int maxSpawnAttempts;
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
        avoidDistanceCap = avoidDistanceCap,
        avoidProbeAngle = avoidProbeAngle,
        orcaRadius = orcaRadius,
        wDepth = wDepth,
        depthCenterBias = depthCenterBias,
        depthFollowPrey = depthFollowPrey,
        showRoleText = showRoleText,
        killCount = killCount,
        spawnRadius = spawnRadius,
        maxSpawnAttempts = maxSpawnAttempts
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
        avoidDistance = s.avoidDistance; avoidDistanceCap = s.avoidDistanceCap; avoidProbeAngle = s.avoidProbeAngle; orcaRadius = s.orcaRadius;
        wDepth = s.wDepth; depthCenterBias = s.depthCenterBias; depthFollowPrey = s.depthFollowPrey;
        showRoleText = s.showRoleText;
        killCount = s.killCount;
        spawnRadius = s.spawnRadius;
        maxSpawnAttempts = s.maxSpawnAttempts;

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

    public void ResetKillCount() { killCount = 0; }

    void OnGUI()
    {
        const float w = 340f;
        const float handleH = 22f;
        const float margin = 12f;

        float targetAnim = showPanel ? 1f : 0f;
        panelAnim = Mathf.SmoothDamp(panelAnim, targetAnim, ref panelAnimVel, 0.15f, Mathf.Infinity, Time.deltaTime);

        float x = Screen.width - w - margin;
        float y = margin;
        float collapsedH = handleH + 4f;
        float expandedH = Screen.height - margin * 2f;
        float h = Mathf.Lerp(collapsedH, expandedH, Mathf.Clamp01(panelAnim));

        Rect r = new Rect(x, y, w, h);
        GUILayout.BeginArea(r, GUI.skin.box);

        if (GUILayout.Button(showPanel ? "Orcas(F2) ▲" : "Orcas(F2) ▼", GUILayout.Height(handleH)))
            showPanel = !showPanel;

        if (!showPanel)
        {
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label("<b>Orca Pod (Predators)</b>", new GUIStyle(GUI.skin.label) { richText = true });

        scroll = GUILayout.BeginScrollView(scroll);

        GUILayout.Label("<b>Roles</b>", new GUIStyle(GUI.skin.label) { richText = true });
        leaders = IntSliderT("Leaders", "Number of leaders (strong pursuit toward intercept).", leaders, 1, 4);
        flankers = IntSliderT("Flankers", "Orcas that orbit prey on a ring to constrain it.", flankers, 0, 16);
        strikers = IntSliderT("Strikers", "Orcas that dash in to strike when close.", strikers, 0, 16);
        supports = IntSliderT("Supports", "Orcas that stay behind prey to corral it.", supports, 0, 16);
        if (GUILayout.Button("Respawn Pod")) SpawnPod();

        GUILayout.Space(6);
        GUILayout.Label("<b>Spawning</b>", new GUIStyle(GUI.skin.label) { richText = true });
        spawnRadius = SliderT("Spawn Radius", "Distance from tank center for spawning ring (gizmo shows exact radius).", spawnRadius, 1f, 100f);
        maxSpawnAttempts = IntSliderT("Max Spawn Attempts", "Maximum attempts to find valid spawn position outside tank.", maxSpawnAttempts, 10, 200);

        GUILayout.Space(6);
        GUILayout.Label("<b>Speeds</b>");
        minSpeed = SliderT("Min Speed", "Minimum cruising speed. Prevents orcas from stalling.", minSpeed, 0.1f, maxSpeed);
        maxSpeed = SliderT("Max Speed", "Top speed used for desired velocities and dashes.", maxSpeed, minSpeed, 20f);
        maxSteerForce = SliderT("Max Steer", "Upper limit on steering force to avoid jitter.", maxSteerForce, 0.1f, 30f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Pod Rules</b>");
        neighborRadius = SliderT("Neighbor Radius", "How far pod-mates influence alignment/cohesion.", neighborRadius, 0.1f, 10f);
        // Lift cap: allow separation radius beyond neighbor radius
        separationRadius = SliderT("Separation Radius", "Distance where strong separation kicks in.", separationRadius, 0.05f, 20f);
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
        avoidDistanceCap = SliderT("Avoid Dist Cap", "Optional max probe length (0 = uncapped).", avoidDistanceCap, 0f, 30f);
        avoidProbeAngle = SliderT("Avoid Angle", "Side probe spread to feel around obstacles.", avoidProbeAngle, 0f, 85f);
        orcaRadius = SliderT("Orca Radius", "Radius used for sweeps and spherecasts.", orcaRadius, 0.05f, 3f);

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

        cameraController?.DrawCameraUI(pod);

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

        // Hover tooltip display: plain text only (no box backgrounds)
        string tip = GUI.tooltip;
        var tipStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
        GUILayout.Space(4);
        GUILayout.Label(string.IsNullOrEmpty(tip) ? " " : tip, tipStyle, GUILayout.ExpandWidth(true));

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

    // --- Validation for real-time gizmo updates ---
#if UNITY_EDITOR
    void OnValidate()
    {
        // This forces gizmos to update when inspector values change
        if (Application.isPlaying)
        {
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
    }
#endif

    // input helpers
    bool KeyDown_F2()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.F2);
#endif
    }

    static int Hash(int3 cell) => (int)math.hash(cell);

    // ------------- Gizmos -------------
    void OnDrawGizmosSelected()
    {
        // Draw simulation area (orange) - only when selected for clarity
        if (simulationArea)
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.15f);
            Gizmos.DrawCube(simulationArea.bounds.center, simulationArea.bounds.size);
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
            Gizmos.DrawWireCube(simulationArea.bounds.center, simulationArea.bounds.size);
        }
        
        // Draw tank area (red - avoid spawning here) - only when selected
        if (simulationArea)
        {
            Gizmos.color = new Color(1f, 0f, 0f, 0.15f);
            Gizmos.DrawCube(simulationArea.bounds.center, simulationArea.bounds.size);
            
            Gizmos.color = new Color(1f, 0f, 0f, 0.7f);
            Gizmos.DrawWireCube(simulationArea.bounds.center, simulationArea.bounds.size);
        }
        
        // Draw detailed spawn area info when selected
        Vector3 centerPoint;
        if (simulationArea)
        {
            // Always use spawn center if set, otherwise use simulation area center (not tank center)
            centerPoint = spawnCenter ? spawnCenter.position : simulationArea.bounds.center;
            float tankMaxExtent = Mathf.Max(simulationArea.bounds.size.x, simulationArea.bounds.size.y, simulationArea.bounds.size.z) * 0.5f;
            float minDistanceFromTank = tankMaxExtent + 1.5f;
            
            // Draw minimum safe distance (yellow) if different from spawn radius
            if (spawnRadius < minDistanceFromTank)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
                Gizmos.DrawWireSphere(centerPoint, minDistanceFromTank);
            }
        }
    }

    void OnDrawGizmos()
    {
        // Always show spawn area (visible for orcas since they're important predators)
        Vector3 centerPoint;
        if (simulationArea)
        {
            // Always use spawn center if set, otherwise use simulation area center (consistent with spawning logic)
            centerPoint = spawnCenter ? spawnCenter.position : simulationArea.bounds.center;
        }
        else
        {
            centerPoint = spawnCenter ? spawnCenter.position : (simulationArea ? simulationArea.bounds.center : transform.position);
        }
        
        // Show spawn radius with subtle transparency
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.08f);
        Gizmos.DrawSphere(centerPoint, spawnRadius);
        
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f);
        Gizmos.DrawWireSphere(centerPoint, spawnRadius);
        
        // Show spawn center point
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
        Gizmos.DrawWireCube(centerPoint, Vector3.one * 0.4f);
    }

}
