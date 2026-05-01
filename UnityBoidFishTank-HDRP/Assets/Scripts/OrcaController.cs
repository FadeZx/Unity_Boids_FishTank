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
    [Tooltip("Spawn the orca pod automatically when play mode starts.")]
    public bool spawnOnStart = true;
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

    [Tooltip("Share the Leader's prey identity with the pod. Roles still use separate movement goals.")]
    public bool shareLeaderTarget = true;
    [Tooltip("Minimum seconds the Leader keeps its current prey before considering a switch.")]
    public float leaderTargetHoldTime = 4.0f;
    [Tooltip("Required score improvement before the Leader switches target. 0.35 means 35% better.")]
    public float targetSwitchScoreMargin = 0.35f;
    [Tooltip("Maximum seconds to lead the prey when calculating pursuit intercepts.")]
    public float interceptMaxLeadTime = 0.85f;
    [Tooltip("Maximum world distance the intercept may be ahead of the prey.")]
    public float interceptMaxLeadDistance = 4.0f;
    [Tooltip("Distance from prey used by strikers while staging outside strike range.")]
    public float strikerStageRadius = 3.0f;

    float retargetTimer = 0f;
    float leaderTargetHoldTimer = 0f;
    BoidAgent leaderTarget;

    [Header("Labels & Stats")]
    [Tooltip("Show role text labels above each orca (Leader/Flanker/Striker/Support).")]
    public bool showRoleText = false;
    [Tooltip("Camera used for role label billboarding. If not set, Camera.main is used.")]
    public Camera labelCamera;

    [Header("Decision Debug")]
    [Tooltip("Show orca decision debug lines and screen labels.")]
    public bool drawDebug = false;
    [Tooltip("Which orca decision layer to draw.")]
    public OrcaDebugMode debugMode = OrcaDebugMode.SelectedOrca;
    [Tooltip("Selected pod index used by Selected Orca debug mode.")]
    public int debugSelectedIndex = 0;
    [Tooltip("Show compact instructions inside the Orca UI panel.")]
    public bool showDebugInstructions = true;
    [Tooltip("Draw compact state labels over debugged orcas.")]
    public bool showDebugText = true;
    [Tooltip("Draw decision lines directly on the game screen.")]
    public bool showDebugScreenLines = true;
    [Tooltip("Scale applied to short force vectors.")]
    public float debugVectorScale = 1.4f;

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
    bool debugDropdownOpen = false;
    float panelAnim = 1f; // 0 collapsed -> 1 expanded
    float panelAnimVel = 0f;
    Vector2 scroll;
    static Texture2D debugLineTexture;
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

        if (spawnOnStart)
        {
            SpawnPod();
        }

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

        if (drawDebug && debugMode == OrcaDebugMode.PodPlan)
            DrawPodPlanDebug();

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
        Vector3 roleForce = RoleForce(self, pos, vel, preyCentroid, preyAvgVel, dt, out OrcaDecisionDebug decision);

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

        decision.podSeparation = wSeparation * sep;
        decision.podAlignment = wAlignment * ali;
        decision.podCohesion = wCohesion * coh;
        decision.roleForce = roleForce;
        decision.avoidance = avoid;
        decision.boundaryAvoidance = boundaryAvoid;
        decision.depthForce = wDepth * depthForce;
        decision.finalSteer = steer;
        self.LastDecision = decision;

        dbg = (sep, ali, coh, roleForce, avoid);
        return steer;
    }

    Vector3 RoleForce(OrcaAgent self, Vector3 pos, Vector3 vel, Vector3 preyCtr, Vector3 preyVel, float dt, out OrcaDecisionDebug decision)
    {
        Vector3 f = Vector3.zero;
        decision = new OrcaDecisionDebug
        {
            state = "No Prey",
            preyCentroid = preyCtr,
            preyVelocity = preyVel,
            roleIndex = IndexAmongRole(self, self.role),
            sharedTarget = shareLeaderTarget
        };

        if (!IsValidPrey(self.CurrentTarget))
            self.ClearTarget();

        // Drive behavior by target if locked, else fall back to centroid
        Vector3 aimCtr = self.HasTarget ? self.CurrentTarget.Position : preyCtr;
        Vector3 aimVel = self.HasTarget ? self.CurrentTarget.Velocity : preyVel;

        Vector3 toPrey = aimCtr - pos;
        float dist = toPrey.magnitude;
        Vector3 preyDir = GetPlanarDirection(aimVel, vel);

        // Intercept point for pursuit
        Vector3 intercept = CalculateIntercept(aimCtr, aimVel, dist, out float tLead);
        decision.target = self.CurrentTarget;
        decision.hasTarget = self.HasTarget;
        decision.aimPoint = aimCtr;
        decision.interceptPoint = intercept;
        decision.hasIntercept = true;
        decision.distanceToTarget = dist;
        decision.leadTime = tLead;

        switch (self.role)
        {
            case OrcaRole.Leader:
                // Strong pursuit toward intercept
                decision.state = "Pursuit";
                decision.roleGoal = intercept;
                decision.hasRoleGoal = true;
                f += wPursuit * (intercept - pos);
                break;

            case OrcaRole.Flanker:
                Vector3 ringTarget = FlankGoal(self, aimCtr, preyDir, 1f);
                decision.state = $"Flank {FlankSignedAngle(self):0} deg";
                decision.roleGoal = ringTarget;
                decision.hasRoleGoal = true;
                f += wEncircle * (ringTarget - pos);
                break;

            case OrcaRole.Striker:
                // If close enough, dash straight at intercept; else behave like flanker closing in
                if (dist <= strikeRange && (self.CanStrike() || self.IsStrikeBoosting))
                {
                    Vector3 dash = (intercept - pos).normalized * (maxSpeed * strikeBoost);
                    decision.state = "Strike";
                    decision.roleGoal = intercept;
                    decision.hasRoleGoal = true;
                    f += wPursuit * (dash - vel); // quick acceleration toward dash dir
                    if (self.CanStrike())
                    {
                        self.NotifyStrikeBoost();
                        self.ResetStrikeCooldown();
                    }
                }
                else
                {
                    Vector3 targetS = StrikerStageGoal(self, aimCtr, preyDir);
                    decision.state = "Pre-Strike";
                    decision.roleGoal = targetS;
                    decision.hasRoleGoal = true;
                    f += (wEncircle + 0.6f) * (targetS - pos);
                }
                break;

            case OrcaRole.Support:
                // Stay slightly behind the selected prey direction to corral (herding)
                Vector3 behind = aimCtr - preyDir * (encircleRadius * 1.1f);
                decision.state = shareLeaderTarget ? "Shared Corral" : "Corral";
                decision.roleGoal = behind;
                decision.hasRoleGoal = true;
                f += wCorral * (behind - pos);
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

    Vector3 CalculateIntercept(Vector3 aimCtr, Vector3 aimVel, float dist, out float tLead)
    {
        if (aimVel.sqrMagnitude < 0.04f)
        {
            tLead = 0f;
            return aimCtr;
        }

        tLead = Mathf.Clamp(dist / Mathf.Max(0.1f, maxSpeed + aimVel.magnitude), 0f, Mathf.Max(0f, interceptMaxLeadTime));
        Vector3 lead = aimVel * tLead;
        float maxLead = Mathf.Max(0f, interceptMaxLeadDistance);
        if (maxLead > 0f && lead.sqrMagnitude > maxLead * maxLead)
            lead = lead.normalized * maxLead;

        Vector3 intercept = aimCtr + lead;
        if (!IsFinite(intercept))
        {
            tLead = 0f;
            return aimCtr;
        }
        return intercept;
    }

    Vector3 FlankGoal(OrcaAgent self, Vector3 aimCtr, Vector3 preyDir, float radiusMultiplier)
    {
        Quaternion q = Quaternion.AngleAxis(FlankSignedAngle(self), Vector3.up);
        Vector3 tangent = q * preyDir;
        return aimCtr + tangent.normalized * (encircleRadius * radiusMultiplier);
    }

    float FlankSignedAngle(OrcaAgent self)
    {
        int index = IndexAmongRole(self, OrcaRole.Flanker);
        float sign = (index % 2 == 0) ? 1f : -1f;
        return sign * flankOffsetAngle * (1 + index / 2);
    }

    Vector3 StrikerStageGoal(OrcaAgent self, Vector3 aimCtr, Vector3 preyDir)
    {
        int index = IndexAmongRole(self, OrcaRole.Striker);
        float sign = (index % 2 == 0) ? -1f : 1f;
        Vector3 side = Vector3.Cross(Vector3.up, preyDir).normalized * sign;
        if (side.sqrMagnitude < 1e-6f) side = Vector3.right * sign;
        float radius = Mathf.Max(0.1f, strikerStageRadius);
        return aimCtr - preyDir * (radius * 0.6f) + side * radius;
    }

    Vector3 GetPlanarDirection(Vector3 primary, Vector3 fallback)
    {
        Vector3 dir = new Vector3(primary.x, 0f, primary.z);
        if (dir.sqrMagnitude < 1e-6f)
            dir = new Vector3(fallback.x, 0f, fallback.z);
        if (dir.sqrMagnitude < 1e-6f)
            dir = Vector3.forward;
        return dir.normalized;
    }

    bool IsFinite(Vector3 v)
    {
        return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
    }

    bool IsFinite(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v);
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
        if (leaderTargetHoldTimer > 0f)
            leaderTargetHoldTimer -= dt;

        retargetTimer -= dt;
        if (retargetTimer > 0f) return;
        retargetTimer = retargetInterval;
        if (preyController == null || preyController.agents.Count == 0 || pod.Count == 0) return;

        OrcaAgent leader = FindPrimaryLeader();
        UpdateLeaderTarget(leader);

        if (shareLeaderTarget && IsValidPrey(leaderTarget))
        {
            foreach (var o in pod)
            {
                if (o == null) continue;
                if (o.CurrentTarget != leaderTarget)
                    o.SetTarget(leaderTarget);
            }
            return;
        }

        var preyToCount = new Dictionary<BoidAgent, int>();
        foreach (var o in pod)
        {
            if (o == null) continue;
            if (IsValidPrey(o.CurrentTarget))
            {
                if (!preyToCount.ContainsKey(o.CurrentTarget)) preyToCount[o.CurrentTarget] = 0;
                preyToCount[o.CurrentTarget]++;
            }
        }

        foreach (var o in pod)
        {
            if (o == null) continue;
            if (o.role == OrcaRole.Leader && IsValidPrey(leaderTarget))
            {
                if (o.CurrentTarget != leaderTarget)
                    o.SetTarget(leaderTarget);
                continue;
            }

            if (!o.CanSwitchTarget()) continue;
            var best = FindBestPreyFor(o, preyToCount, out _);
            if (best != null && best != o.CurrentTarget)
            {
                if (IsValidPrey(o.CurrentTarget) && preyToCount.ContainsKey(o.CurrentTarget))
                    preyToCount[o.CurrentTarget] = Mathf.Max(0, preyToCount[o.CurrentTarget] - 1);
                o.SetTarget(best);
                if (!preyToCount.ContainsKey(best)) preyToCount[best] = 0;
                preyToCount[best]++;
            }
        }
    }

    OrcaAgent FindPrimaryLeader()
    {
        foreach (var o in pod)
            if (o != null && o.role == OrcaRole.Leader)
                return o;
        return pod.Count > 0 ? pod[0] : null;
    }

    void UpdateLeaderTarget(OrcaAgent leader)
    {
        if (leader == null) return;

        if (!IsValidPrey(leaderTarget))
        {
            leaderTarget = null;
            leaderTargetHoldTimer = 0f;
        }

        BoidAgent best = FindBestPreyFor(leader, null, out float bestScore, true);
        if (!IsValidPrey(leaderTarget))
        {
            SetLeaderTarget(best);
            return;
        }

        float currentScore = ScorePreyFor(leader, leaderTarget);
        bool farEnoughBetter = best != null && best != leaderTarget &&
                               bestScore > currentScore * (1f + Mathf.Max(0f, targetSwitchScoreMargin));
        if (leaderTargetHoldTimer <= 0f && farEnoughBetter)
            SetLeaderTarget(best);

        if (leader.CurrentTarget != leaderTarget)
            leader.SetTarget(leaderTarget);
    }

    void SetLeaderTarget(BoidAgent target)
    {
        leaderTarget = target;
        leaderTargetHoldTimer = leaderTargetHoldTime;
        foreach (var o in pod)
        {
            if (o != null && o.role == OrcaRole.Leader)
                o.SetTarget(leaderTarget);
        }
    }

    BoidAgent FindBestPreyFor(OrcaAgent orca, Dictionary<BoidAgent, int> preyToCount, out float bestScore, bool ignoreCapacity = false)
    {
        BoidAgent best = null;
        bestScore = float.NegativeInfinity;
        if (orca == null || preyController == null) return best;
        Vector3 pos = orca.Position;
        var list = preyController.agents;
        for (int i = 0; i < list.Count; i++)
        {
            var prey = list[i];
            if (!IsValidPrey(prey)) continue;
            if (!ignoreCapacity && preyToCount != null)
            {
                int c = preyToCount.TryGetValue(prey, out int v) ? v : 0;
                if (c >= maxOrcasPerPrey) continue;
            }

            float score = ScorePreyAtPosition(prey, pos);
            if (score > bestScore)
            {
                bestScore = score;
                best = prey;
            }
        }
        return best;
    }

    float ScorePreyFor(OrcaAgent orca, BoidAgent prey)
    {
        if (orca == null || !IsValidPrey(prey)) return float.NegativeInfinity;
        return ScorePreyAtPosition(prey, orca.Position);
    }

    float ScorePreyAtPosition(BoidAgent prey, Vector3 pos)
    {
        float d2 = (prey.Position - pos).sqrMagnitude;
        float distScore = 1f / Mathf.Max(0.1f, Mathf.Sqrt(d2));
        int neighbors = CountPreyNeighbors(prey.Position, 1.5f);
        float isolationScore = 1f / Mathf.Max(1f, neighbors);
        return wTargetDistance * distScore + wTargetIsolation * isolationScore;
    }

    bool IsValidPrey(BoidAgent prey)
    {
        return prey != null && prey.controller != null && prey.gameObject != null;
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
        public int settingsVersion;
        public int leaders, flankers, strikers, supports;
        public float minSpeed, maxSpeed, maxSteerForce;
        public float neighborRadius, separationRadius;
        public float wSeparation, wAlignment, wCohesion;
        public float wPursuit, wEncircle, wCorral;
        public float encircleRadius, flankOffsetAngle;
        public float strikeRange, strikeBoost, strikeCooldown;
        public float avoidDistance, avoidDistanceCap, avoidProbeAngle, orcaRadius;
        public float wDepth, depthCenterBias, depthFollowPrey;
        public float retargetInterval, wTargetDistance, wTargetIsolation;
        public int maxOrcasPerPrey;
        public bool shareLeaderTarget;
        public float leaderTargetHoldTime, targetSwitchScoreMargin;
        public float interceptMaxLeadTime, interceptMaxLeadDistance, strikerStageRadius;
        public bool drawDebug;
        public OrcaDebugMode debugMode;
        public int debugSelectedIndex;
        public bool showDebugInstructions;
        public bool showDebugText;
        public bool showDebugScreenLines;
        public float debugVectorScale;
        public bool showRoleText;
        public int killCount;
        public float spawnRadius;
        public int maxSpawnAttempts;
    }

    OrcaSettings Collect() => new()
    {
        settingsVersion = 2,
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
        retargetInterval = retargetInterval,
        maxOrcasPerPrey = maxOrcasPerPrey,
        wTargetDistance = wTargetDistance,
        wTargetIsolation = wTargetIsolation,
        shareLeaderTarget = shareLeaderTarget,
        leaderTargetHoldTime = leaderTargetHoldTime,
        targetSwitchScoreMargin = targetSwitchScoreMargin,
        interceptMaxLeadTime = interceptMaxLeadTime,
        interceptMaxLeadDistance = interceptMaxLeadDistance,
        strikerStageRadius = strikerStageRadius,
        drawDebug = drawDebug,
        debugMode = debugMode,
        debugSelectedIndex = debugSelectedIndex,
        showDebugInstructions = showDebugInstructions,
        showDebugText = showDebugText,
        showDebugScreenLines = showDebugScreenLines,
        debugVectorScale = debugVectorScale,
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
        retargetInterval = s.retargetInterval > 0f ? s.retargetInterval : retargetInterval;
        maxOrcasPerPrey = s.maxOrcasPerPrey > 0 ? s.maxOrcasPerPrey : maxOrcasPerPrey;
        wTargetDistance = s.wTargetDistance > 0f ? s.wTargetDistance : wTargetDistance;
        wTargetIsolation = s.wTargetIsolation > 0f ? s.wTargetIsolation : wTargetIsolation;
        if (s.settingsVersion >= 2)
            shareLeaderTarget = s.shareLeaderTarget;
        leaderTargetHoldTime = s.leaderTargetHoldTime > 0f ? s.leaderTargetHoldTime : leaderTargetHoldTime;
        targetSwitchScoreMargin = s.targetSwitchScoreMargin > 0f ? s.targetSwitchScoreMargin : targetSwitchScoreMargin;
        interceptMaxLeadTime = s.interceptMaxLeadTime > 0f ? s.interceptMaxLeadTime : interceptMaxLeadTime;
        interceptMaxLeadDistance = s.interceptMaxLeadDistance > 0f ? s.interceptMaxLeadDistance : interceptMaxLeadDistance;
        strikerStageRadius = s.strikerStageRadius > 0f ? s.strikerStageRadius : strikerStageRadius;
        drawDebug = s.drawDebug;
        debugMode = s.debugMode;
        debugSelectedIndex = s.debugSelectedIndex;
        showDebugInstructions = s.showDebugInstructions;
        showDebugText = s.showDebugText;
        showDebugScreenLines = s.showDebugScreenLines;
        debugVectorScale = s.debugVectorScale > 0f ? s.debugVectorScale : debugVectorScale;
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
        const float w = 380f;
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
            DrawDecisionScreenLabels();
            return;
        }

        GUILayout.Label("<b>Orca Pod (Predators)</b>", new GUIStyle(GUI.skin.label) { richText = true });

        scroll = GUILayout.BeginScrollView(scroll);

        SectionLabel("Pod Setup");
        leaders = IntSliderT("Leaders", "Number of leaders (strong pursuit toward intercept).", leaders, 1, 4, "orcas");
        flankers = IntSliderT("Flankers", "Orcas that orbit prey on a ring to constrain it.", flankers, 0, 16, "orcas");
        strikers = IntSliderT("Strikers", "Orcas that dash in to strike when close.", strikers, 0, 16, "orcas");
        supports = IntSliderT("Supports", "Orcas that stay behind prey to corral it.", supports, 0, 16, "orcas");
        if (GUILayout.Button("Respawn Pod")) SpawnPod();

        SectionLabel("Movement");
        minSpeed = SliderT("Min Speed", "Minimum cruising speed. Prevents orcas from stalling.", minSpeed, 0.1f, Mathf.Max(0.1f, maxSpeed), "u/s");
        maxSpeed = SliderT("Max Speed", "Top speed used for desired velocities and dashes.", maxSpeed, minSpeed, Mathf.Max(20f, minSpeed), "u/s");
        if (maxSpeed < minSpeed) maxSpeed = minSpeed;
        maxSteerForce = SliderT("Max Steer", "Upper limit on steering force to avoid jitter.", maxSteerForce, 0.1f, 30f, "force");
        wDepth = WeightSliderT("Depth W", "Weight to keep near preferred depth. Displayed 0-1, internally mapped to 0-5.", wDepth, 5f);
        depthCenterBias = PercentSliderT("Depth Center", "Preferred vertical center in tank (0=bottom, 1=surface).", depthCenterBias, 0f, 1f);
        depthFollowPrey = PercentSliderT("Follow Prey Y", "Blend toward prey height: 0 = ignore prey height, 1 = match prey height.", depthFollowPrey, 0f, 1f);
        HelpText("Min Speed is clamped below Max Speed.");

        SectionLabel("Pod Flocking");
        wSeparation = WeightSliderT("Separation W", "Weight of separation (spread apart). Displayed 0-1, internally mapped to 0-10.", wSeparation, 10f);
        wAlignment = WeightSliderT("Alignment W", "Weight of alignment (match headings). Displayed 0-1, internally mapped to 0-10.", wAlignment, 10f);
        wCohesion = WeightSliderT("Cohesion W", "Weight of cohesion (stay together). Displayed 0-1, internally mapped to 0-10.", wCohesion, 10f);
        neighborRadius = SliderT("Neighbor Radius", "How far pod-mates influence alignment/cohesion.", neighborRadius, 0.1f, 10f, "units");
        separationRadius = SliderT("Separation Radius", "Distance where strong separation kicks in.", separationRadius, 0.05f, 20f, "units");
        if (separationRadius > neighborRadius)
            WarningText("Separation Radius is larger than Neighbor Radius; pod spacing can overpower cohesion.");

        SectionLabel("Hunting");
        wPursuit = WeightSliderT("Pursuit W", "Pursuit strength (leaders/strikers aim at an intercept). Displayed 0-1, internally mapped to 0-10.", wPursuit, 10f);
        wEncircle = WeightSliderT("Encircle W", "Flankers circle radius pull. Displayed 0-1, internally mapped to 0-10.", wEncircle, 10f);
        wCorral = WeightSliderT("Corral W", "Support tries to stay behind prey to herd it. Displayed 0-1, internally mapped to 0-10.", wCorral, 10f);
        encircleRadius = SliderT("Encircle Radius", "Ring radius used for encirclement around prey.", encircleRadius, 0.5f, 20f, "units");
        flankOffsetAngle = SliderT("Flank Angle", "Spacing angle offsets around the ring for flankers.", flankOffsetAngle, 0f, 160f, "deg");
        shareLeaderTarget = ToggleT("Share Leader Target", "Share the Leader's prey identity. Flankers, strikers, and support still use separate role goals.", shareLeaderTarget);
        interceptMaxLeadTime = SliderT("Lead Time Cap", "Maximum seconds to lead prey when calculating intercepts.", interceptMaxLeadTime, 0f, 2f, "sec");
        interceptMaxLeadDistance = SliderT("Lead Dist Cap", "Maximum world distance an intercept can be ahead of the prey.", interceptMaxLeadDistance, 0f, 12f, "units");

        SectionLabel("Strike");
        strikeRange = SliderT("Strike Range", "Distance threshold to trigger a strike dash.", strikeRange, 0.5f, 10f, "units");
        strikeBoost = SliderT("Strike Boost", "Speed multiplier during strike dashes.", strikeBoost, 1f, 3f, "x");
        strikeCooldown = SliderT("Strike Cooldown", "Cooldown between strikes for each striker.", strikeCooldown, 0f, 8f, "sec");
        strikerStageRadius = SliderT("Stage Radius", "Distance from selected prey where strikers wait before entering strike range.", strikerStageRadius, 0.5f, 12f, "units");
        if (strikeRange > encircleRadius)
            WarningText("Strike Range is larger than Encircle Radius; strikers will engage very early.");

        SectionLabel("Targeting");
        retargetInterval = SliderT("Retarget", "How often to re-evaluate prey targets per orca.", retargetInterval, 0.1f, 5f, "sec");
        leaderTargetHoldTime = SliderT("Leader Hold", "Minimum seconds the Leader keeps its current prey before considering a switch.", leaderTargetHoldTime, 0.5f, 12f, "sec");
        targetSwitchScoreMargin = PercentSliderT("Switch Margin", "How much better a new prey must score before the Leader switches.", targetSwitchScoreMargin, 0f, 1f);
        maxOrcasPerPrey = IntSliderT("Max Per Prey", "Max number of orcas allowed to focus the same prey.", maxOrcasPerPrey, 1, 16, "orcas");
        wTargetDistance = WeightSliderT("Distance Bias", "Bias toward closer prey. Displayed 0-1, internally mapped to 0-10.", wTargetDistance, 10f);
        wTargetIsolation = WeightSliderT("Isolation Bias", "Bias toward isolated prey. Displayed 0-1, internally mapped to 0-10.", wTargetIsolation, 10f);

        SectionLabel("Environment");
        spawnRadius = SliderT("Spawn Radius", "Distance from tank center for spawning ring (gizmo shows exact radius).", spawnRadius, 1f, 100f, "units");
        maxSpawnAttempts = IntSliderT("Spawn Attempts", "Maximum attempts to find valid spawn position outside tank.", maxSpawnAttempts, 10, 200, "attempts");
        avoidDistance = SliderT("Avoid Dist", "Forward probe length for obstacle detection.", avoidDistance, 0.2f, 15f, "units");
        avoidDistanceCap = SliderT("Avoid Dist Cap", "Optional max probe length (0 = uncapped).", avoidDistanceCap, 0f, 30f, avoidDistanceCap <= 0f ? "off" : "units");
        HelpText("Avoid Dist Cap at 0 means uncapped.");
        avoidProbeAngle = SliderT("Avoid Angle", "Side probe spread to feel around obstacles.", avoidProbeAngle, 0f, 85f, "deg");
        orcaRadius = SliderT("Orca Radius", "Radius used for sweeps and spherecasts.", orcaRadius, 0.05f, 3f, "units");
        boundaryAvoidRadius = SliderT("Boundary Radius", "Distance from walls where orcas start steering away.", boundaryAvoidRadius, 0.1f, 10f, "units");

        SectionLabel("Camera / Labels / Stats");
        showRoleText = ToggleT("Show Role Text", "Show text labels above each orca indicating its role.", showRoleText);

        SectionLabel("Decision Debug");
        drawDebug = ToggleT("Draw Debug", "Enable orca decision debug. Turn on Screen Lines to see colored lines in Game view, then choose Mode: SelectedOrca, AllOrcas, TargetsOnly, ForcesOnly, or PodPlan. Cyan=target, purple=goal/intercept, orange=role force, white=final steer.", drawDebug);
        showDebugInstructions = ToggleT("Show Instructions", "Show the debug legend/instructions in this panel.", showDebugInstructions);
        showDebugText = ToggleT("Screen Labels", "Show compact decision labels over debugged orcas in Game view.", showDebugText);
        showDebugScreenLines = ToggleT("Screen Lines", "Draw colored decision lines directly on the Game screen.", showDebugScreenLines);
        if (GUILayout.Button(new GUIContent($"Mode: {debugMode}", "Choose which orca decision layer to draw.")))
            debugDropdownOpen = !debugDropdownOpen;
        if (debugDropdownOpen)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            foreach (OrcaDebugMode mode in Enum.GetValues(typeof(OrcaDebugMode)))
            {
                if (GUILayout.Button(mode.ToString()))
                {
                    debugMode = mode;
                    debugDropdownOpen = false;
                }
            }
            GUILayout.EndVertical();
        }
        if (pod.Count > 0)
            debugSelectedIndex = IntSliderT("Selected Orca", "Pod index used by Selected Orca debug mode.", debugSelectedIndex, 0, pod.Count - 1, "index");
        debugVectorScale = SliderT("Vector Scale", "Length multiplier for short force vectors.", debugVectorScale, 0.25f, 5f, "x");
        if (showDebugInstructions)
        {
            var debugHelpStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 10, richText = true };
            int liveDebugCount = 0;
            for (int i = 0; i < pod.Count; i++)
                if (pod[i] != null && pod[i].HasDecisionDebug) liveDebugCount++;
            GUILayout.Label(
                "Cyan: prey target | Purple: intercept/role goal | Orange: role force | Yellow: cohesion | Red: separation | Magenta: avoid | White: final steer.\n" +
                "Screen Lines are visible in Game view. Unity Debug.DrawLine still needs the Game view Gizmos button.\n" +
                $"Live debug agents: {liveDebugCount}/{pod.Count}",
                debugHelpStyle);
        }

        GUILayout.Label($"Kill Count: <b>{killCount}</b>", new GUIStyle(GUI.skin.label) { richText = true });
        if (GUILayout.Button("Reset Kill Count")) killCount = 0;

        cameraController?.DrawCameraUI(pod);

        SectionLabel("Presets");
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
        GUILayout.Label(string.IsNullOrEmpty(tip) ? " " : tip, tipStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(string.IsNullOrEmpty(tip) ? 16f : 42f));

        GUILayout.EndArea();

        DrawDecisionScreenLabels();
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
    void SectionLabel(string label)
    {
        GUILayout.Space(8);
        GUILayout.Label($"<b>{label}</b>", new GUIStyle(GUI.skin.label) { richText = true });
    }

    void HelpText(string text)
    {
        GUILayout.Label(text, new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true });
    }

    void WarningText(string text)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true };
        style.normal.textColor = Color.yellow;
        GUILayout.Label(text, style);
    }

    float SliderT(string label, string tooltip, float v, float min, float max, string suffix = "")
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(140));
        v = GUILayout.HorizontalSlider(v, min, max);
        string display = suffix == "off" ? "off" : string.IsNullOrEmpty(suffix) ? v.ToString("0.00") : $"{v:0.00} {suffix}";
        GUILayout.Label(display, GUILayout.Width(90));
        GUILayout.EndHorizontal();
        return v;
    }
    int IntSliderT(string label, string tooltip, int v, int min, int max, string suffix = "")
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(140));
        v = (int)GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(string.IsNullOrEmpty(suffix) ? v.ToString() : $"{v} {suffix}", GUILayout.Width(90));
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

    float WeightSliderT(string label, string tooltip, float value, float internalMax)
    {
        float normalized = internalMax <= 0f ? 0f : Mathf.Clamp01(value / internalMax);
        normalized = SliderT(label, tooltip, normalized, 0f, 1f);
        return normalized * internalMax;
    }

    float PercentSliderT(string label, string tooltip, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(140));
        value = GUILayout.HorizontalSlider(value, min, max);
        GUILayout.Label($"{Mathf.RoundToInt(value * 100f)}%", GUILayout.Width(90));
        GUILayout.EndHorizontal();
        return Mathf.Clamp(value, min, max);
    }

    public bool ShouldDrawDebug(OrcaAgent self)
    {
        if (!drawDebug || debugMode == OrcaDebugMode.Off || self == null) return false;
        if (debugMode == OrcaDebugMode.SelectedOrca)
        {
            if (pod.Count == 0) return false;
            debugSelectedIndex = Mathf.Clamp(debugSelectedIndex, 0, pod.Count - 1);
            return pod[debugSelectedIndex] == self;
        }
        return true;
    }

    public void DrawOrcaDebug(OrcaAgent self)
    {
        if (self == null || !self.HasDecisionDebug) return;

        OrcaDecisionDebug d = self.LastDecision;
        Vector3 p = self.Position;
        bool showTargets = debugMode == OrcaDebugMode.TargetsOnly || debugMode == OrcaDebugMode.AllOrcas || debugMode == OrcaDebugMode.SelectedOrca || debugMode == OrcaDebugMode.PodPlan;
        bool showForces = debugMode == OrcaDebugMode.ForcesOnly || debugMode == OrcaDebugMode.AllOrcas || debugMode == OrcaDebugMode.SelectedOrca;

        if (showTargets)
        {
            if (d.hasTarget && d.target != null)
                Debug.DrawLine(p, d.target.Position, Color.cyan);

            if (d.hasIntercept)
                DrawDebugCross(d.interceptPoint, 0.35f, new Color(0.8f, 0.2f, 1f));

            if (d.hasRoleGoal)
            {
                Debug.DrawLine(p, d.roleGoal, new Color(0.8f, 0.2f, 1f));
                DrawDebugCross(d.roleGoal, 0.25f, new Color(0.8f, 0.2f, 1f));
            }
        }

        if (showForces)
        {
            DrawVector(p, d.podSeparation, Color.red);
            DrawVector(p, d.podAlignment, Color.blue);
            DrawVector(p, d.podCohesion, Color.yellow);
            DrawVector(p, d.roleForce, new Color(1f, 0.55f, 0f));
            DrawVector(p, d.avoidance + d.boundaryAvoidance, Color.magenta);
            DrawVector(p, d.depthForce, Color.green);
            DrawVector(p, d.finalSteer, Color.white);
        }
    }

    void DrawVector(Vector3 origin, Vector3 v, Color color)
    {
        if (v.sqrMagnitude < 1e-6f) return;
        Vector3 end = origin + v.normalized * Mathf.Min(v.magnitude, debugVectorScale);
        Debug.DrawLine(origin, end, color);
    }

    void DrawDebugCross(Vector3 center, float size, Color color)
    {
        Debug.DrawLine(center - Vector3.right * size, center + Vector3.right * size, color);
        Debug.DrawLine(center - Vector3.up * size, center + Vector3.up * size, color);
        Debug.DrawLine(center - Vector3.forward * size, center + Vector3.forward * size, color);
    }

    void DrawPodPlanDebug()
    {
        if (preyController == null || preyController.agents.Count == 0) return;

        Color ringColor = new Color(0.8f, 0.2f, 1f);
        DrawDebugCircle(preyCentroid, encircleRadius, ringColor);
        DrawDebugCross(preyCentroid, 0.3f, Color.cyan);

        Vector3 preyDir = preyAvgVel.sqrMagnitude > 1e-6f ? preyAvgVel.normalized : Vector3.forward;
        DrawDebugCross(preyCentroid + preyDir * encircleRadius, 0.2f, ringColor);
        DrawDebugCross(preyCentroid - preyDir * (encircleRadius * 1.1f), 0.2f, ringColor);
    }

    void DrawDebugCircle(Vector3 center, float radius, Color color)
    {
        const int segments = 32;
        Vector3 prev = center + Vector3.forward * radius;
        for (int i = 1; i <= segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            Vector3 next = center + new Vector3(Mathf.Sin(a) * radius, 0f, Mathf.Cos(a) * radius);
            Debug.DrawLine(prev, next, color);
            prev = next;
        }
    }

    void DrawDecisionScreenLabels()
    {
        if (!drawDebug || debugMode == OrcaDebugMode.Off) return;

        Camera cam = labelCamera != null ? labelCamera : Camera.main;
        if (cam == null) return;

        var style = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 11,
            richText = true
        };

        for (int i = 0; i < pod.Count; i++)
        {
            OrcaAgent o = pod[i];
            if (!ShouldDrawDebug(o) || !o.HasDecisionDebug) continue;

            Vector3 world = o.Position + Vector3.up * 0.7f;
            Vector3 screen = cam.WorldToScreenPoint(world);
            if (screen.z <= 0f) continue;

            OrcaDecisionDebug d = o.LastDecision;
            if (showDebugScreenLines)
                DrawDecisionScreenLines(o, d, cam);

            if (showDebugText)
            {
                string targetName = d.target != null ? d.target.name : "Centroid";
                string text = $"{i}: {o.role}\n{d.state}\nTarget: {targetName}\nDist: {d.distanceToTarget:0.0} Lead: {d.leadTime:0.0}s";
                GUIContent content = new GUIContent(text);
                float width = Mathf.Max(145f, style.CalcSize(content).x + 12f);
                float height = style.CalcHeight(content, width) + 10f;
                Rect rect = new Rect(screen.x + 8f, Screen.height - screen.y - 8f, width, height);
                GUI.Box(rect, text, style);
            }
        }
    }

    void DrawDecisionScreenLines(OrcaAgent o, OrcaDecisionDebug d, Camera cam)
    {
        bool showTargets = debugMode == OrcaDebugMode.TargetsOnly || debugMode == OrcaDebugMode.AllOrcas || debugMode == OrcaDebugMode.SelectedOrca || debugMode == OrcaDebugMode.PodPlan;
        bool showForces = debugMode == OrcaDebugMode.ForcesOnly || debugMode == OrcaDebugMode.AllOrcas || debugMode == OrcaDebugMode.SelectedOrca;
        Vector3 p = o.Position;

        if (showTargets)
        {
            if (d.hasTarget && d.target != null)
                DrawWorldLine(cam, p, d.target.Position, Color.cyan, 2f);
            if (d.hasRoleGoal)
            {
                Color purple = new Color(0.8f, 0.2f, 1f);
                DrawWorldLine(cam, p, d.roleGoal, purple, 2f);
                DrawWorldCross(cam, d.roleGoal, 0.35f, purple);
            }
            if (d.hasIntercept)
                DrawWorldCross(cam, d.interceptPoint, 0.45f, new Color(0.8f, 0.2f, 1f));
        }

        if (showForces)
        {
            DrawWorldVector(cam, p, d.podSeparation, Color.red);
            DrawWorldVector(cam, p, d.podAlignment, Color.blue);
            DrawWorldVector(cam, p, d.podCohesion, Color.yellow);
            DrawWorldVector(cam, p, d.roleForce, new Color(1f, 0.55f, 0f));
            DrawWorldVector(cam, p, d.avoidance + d.boundaryAvoidance, Color.magenta);
            DrawWorldVector(cam, p, d.depthForce, Color.green);
            DrawWorldVector(cam, p, d.finalSteer, Color.white);
        }
    }

    void DrawWorldVector(Camera cam, Vector3 origin, Vector3 v, Color color)
    {
        if (v.sqrMagnitude < 1e-6f) return;
        Vector3 end = origin + v.normalized * Mathf.Min(v.magnitude, debugVectorScale);
        DrawWorldLine(cam, origin, end, color, 2f);
    }

    void DrawWorldCross(Camera cam, Vector3 center, float size, Color color)
    {
        DrawWorldLine(cam, center - Vector3.right * size, center + Vector3.right * size, color, 2f);
        DrawWorldLine(cam, center - Vector3.up * size, center + Vector3.up * size, color, 2f);
    }

    void DrawWorldLine(Camera cam, Vector3 a, Vector3 b, Color color, float thickness)
    {
        Vector3 sa = cam.WorldToScreenPoint(a);
        Vector3 sb = cam.WorldToScreenPoint(b);
        if (sa.z <= 0f || sb.z <= 0f) return;
        DrawScreenLine(new Vector2(sa.x, Screen.height - sa.y), new Vector2(sb.x, Screen.height - sb.y), color, thickness);
    }

    void DrawScreenLine(Vector2 a, Vector2 b, Color color, float thickness)
    {
        if (debugLineTexture == null)
        {
            debugLineTexture = new Texture2D(1, 1);
            debugLineTexture.SetPixel(0, 0, Color.white);
            debugLineTexture.Apply();
        }

        Matrix4x4 matrix = GUI.matrix;
        Color oldColor = GUI.color;
        Vector2 d = b - a;
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, d.magnitude, thickness), debugLineTexture);
        GUI.matrix = matrix;
        GUI.color = oldColor;
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
