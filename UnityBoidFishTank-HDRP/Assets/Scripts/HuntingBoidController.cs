
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;

[DefaultExecutionOrder(-48)]
public class HuntingBoidController : MonoBehaviour
{
    public enum EmissionStyle { Area, Cone }

    [Header("References")]
    [Tooltip("Optional bounds used for spawning and boundary avoidance.")]
    public BoxCollider huntingArea;
    [Tooltip("Prefab used for hunter agents (Leader/Flanker/Striker/Support).")]
    public HuntingBoidAgent hunterPrefab;

    [Header("Spawning")]
    public bool spawnOnStart = true;
    [Tooltip("Use the huntingArea collider for spawn/bounds. If false, free-flight using only target tag.")]
    public bool useHuntingArea = true;
    public EmissionStyle emission = EmissionStyle.Area;
    [Tooltip("Radius used for Area emission.")]
    public float spawnRadius = 8f;
    [Tooltip("Max angle (deg) for Cone emission.")]
    public float spawnConeAngle = 25f;
    [Tooltip("Length for Cone emission.")]
    public float spawnConeLength = 6f;
    [Tooltip("Optional center override for spawning.")]
    public Transform spawnCenter;
    public int maxSpawnAttempts = 50;

    [Header("Role Counts")]
    public int leaders = 1;
    public int flankers = 3;
    public int strikers = 2;
    public int supports = 2;

    [Header("Speeds")]
    public float minSpeed = 2.2f;
    public float maxSpeed = 6.0f;
    public float maxSteerForce = 10.0f;

    [Header("Neighborhood (flock cohesion)")]
    public float neighborRadius = 3.0f;
    public float separationRadius = 0.9f;

    [Header("Weights (flock rules)")]
    public float wSeparation = 1.3f;
    public float wAlignment = 0.8f;
    public float wCohesion = 0.8f;

    [Header("Hunt Weights")]
    public float wPursuit = 2.0f;
    public float wEncircle = 2.2f;
    public float wCorral = 1.6f;

    [Header("Encirclement")]
    public float encircleRadius = 4.0f;
    public float flankOffsetAngle = 45f;
    [Tooltip("Preferred offset above the target when orbiting (not striking).")]
    public float orbitHeightOffset = 2.0f;
    [Tooltip("How strongly to correct toward the orbit height while circling.")]
    public float wOrbitHeight = 0.6f;

    [Header("Strike")]
    public float strikeRange = 3.0f;
    public float strikeBoost = 1.6f;
    public float strikeCooldown = 2.5f;

    [Header("Obstacle & Boundary Avoidance")]
    public LayerMask obstacleMask;
    public float avoidDistance = 2.5f;
    [Tooltip("Optional max cap for obstacle probe length (0 = uncapped).")]
    public float avoidDistanceCap = 0f;
    public float avoidProbeAngle = 25f;
    public float agentRadius = 0.25f;
    [Tooltip("Distance from walls where hunters start steering away (soft boundary).")]
    public float boundaryAvoidRadius = 1.2f;

    [Header("Targeting")]
    [Tooltip("Tag used to discover targets.")]
    public string targetTag = "Prey";
    public float retargetInterval = 0.6f;
    public int maxHuntersPerTarget = 2;
    public float wTargetDistance = 1.0f;
    public float wTargetIsolation = 1.0f;
    public bool shareLeaderTarget = true;

    [Header("Labels & Stats")]
    public bool showRoleText = false;
    public Camera labelCamera;
    public int captureCount = 0;

    public readonly List<HuntingBoidAgent> hunters = new();
    readonly List<BoidAgent> targetPool = new();
    Vector3 targetCentroid, targetAvgVel;
    NativeArray<float3> targetPositions;
    NativeParallelMultiHashMap<int, int> targetGrid;
    float targetCellSize = 1.5f;
    bool targetGridReady;

    bool showPanel = true;
    float panelAnim = 1f;
    float panelAnimVel = 0f;
    Vector2 scroll;
    [Tooltip("Show legacy runtime UI (OnGUI). Leave off for build/game view.")]
    public bool showRuntimeUI = false;
    [Tooltip("Save settings automatically when play mode stops or object is destroyed.")]
    public bool saveSettingsOnPlay = false;
    const string kPrefs = "HuntingBoids_Settings_JSON";
    string JsonPath => Path.Combine(Application.persistentDataPath, "hunting_boids_settings.json");

    void Start()
    {
        if (!hunterPrefab)
        {
            Debug.LogError("HuntingBoidController: assign hunterPrefab.");
            enabled = false; return;
        }

        TryLoad();
        if (labelCamera == null) labelCamera = Camera.main;
        RefreshTargetPool();
        if (spawnOnStart) SpawnHunters();
    }

    void OnDestroy()
    {
        DisposeTargetGrid();
        if (saveSettingsOnPlay && Application.isPlaying)
            SaveToFile();
    }

    void Update()
    {
        if (KeyDown_F3()) showPanel = !showPanel;

        RefreshTargetPool();
        GetTargetStats(out targetCentroid, out targetAvgVel);
        BuildTargetGrid();
        AssignTargetsPeriodically(Time.deltaTime);
        EnsureRoleCoverage();
        EnsureTargetsAssigned();

        if (shareLeaderTarget)
        {
            BoidAgent leadersTarget = null;
            foreach (var h in hunters)
            {
                if (h.role == HunterRole.Leader && h.CurrentTarget != null)
                {
                    leadersTarget = h.CurrentTarget;
                    break;
                }
            }
            if (leadersTarget != null)
            {
                foreach (var h in hunters)
                {
                    if (h.CurrentTarget != leadersTarget)
                        h.SetTarget(leadersTarget);
                }
            }
        }

#if UNITY_EDITOR
        if (spawnCenter != null)
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        DisposeTargetGrid();
    }

    void BuildTargetGrid()
    {
        DisposeTargetGrid();
        var list = GetTargets();
        if (list == null || list.Count == 0) return;

        int count = list.Count;
        targetPositions = new NativeArray<float3>(count, Allocator.TempJob);
        for (int i = 0; i < count; i++)
            targetPositions[i] = list[i].transform.position;

        targetCellSize = Mathf.Max(0.25f, neighborRadius);
        int capacity = Mathf.Max(1, count * 4);
        targetGrid = new NativeParallelMultiHashMap<int, int>(capacity, Allocator.TempJob);
        for (int i = 0; i < count; i++)
        {
            int3 cell = (int3)math.floor(targetPositions[i] / targetCellSize);
            targetGrid.Add(Hash(cell), i);
        }
        targetGridReady = true;
    }

    void DisposeTargetGrid()
    {
        if (targetPositions.IsCreated) targetPositions.Dispose();
        if (targetGrid.IsCreated) targetGrid.Dispose();
        targetGridReady = false;
    }
    // ---------------- Spawning / Roles ----------------
    public void SpawnHunters()
    {
        captureCount = 0;
        Clear();
        var b = huntingArea != null ? huntingArea.bounds : new Bounds(Vector3.zero, Vector3.zero);
        Vector3 centerPoint = spawnCenter ? spawnCenter.position : (huntingArea ? huntingArea.bounds.center : transform.position);

        void SpawnRole(int count, HunterRole role)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 p = GetValidSpawnPosition(centerPoint, b);
                var a = Instantiate(hunterPrefab, p, Quaternion.identity, transform);
                a.controller = this;
                a.role = role;
                a.name = $"{role} {hunters.Count + 1}";
                a.Velocity = UnityEngine.Random.insideUnitSphere.normalized * UnityEngine.Random.Range(minSpeed, maxSpeed);
                hunters.Add(a);
            }
        }

        SpawnRole(Mathf.Max(1, leaders), HunterRole.Leader);
        SpawnRole(Mathf.Max(0, flankers), HunterRole.Flanker);
        SpawnRole(Mathf.Max(0, strikers), HunterRole.Striker);
        SpawnRole(Mathf.Max(0, supports), HunterRole.Support);
    }

    public void SpawnByRole(string roleName, int count)
    {
        if (count <= 0) return;
        if (!Enum.TryParse(roleName, true, out HunterRole role))
        {
            Debug.LogWarning($"HuntingBoidController: Unknown role '{roleName}'.");
            return;
        }
        var b = huntingArea != null ? huntingArea.bounds : new Bounds(Vector3.zero, Vector3.zero);
        Vector3 centerPoint = spawnCenter ? spawnCenter.position : (huntingArea ? huntingArea.bounds.center : transform.position);
        for (int i = 0; i < count; i++)
        {
            Vector3 p = GetValidSpawnPosition(centerPoint, b);
            var a = Instantiate(hunterPrefab, p, Quaternion.identity, transform);
            a.controller = this;
            a.role = role;
            a.name = $"{role} {hunters.Count + 1}";
            a.Velocity = UnityEngine.Random.insideUnitSphere.normalized * UnityEngine.Random.Range(minSpeed, maxSpeed);
            hunters.Add(a);
        }
    }

    public void DestroyAgent(HuntingBoidAgent agent)
    {
        if (agent == null) return;
        hunters.Remove(agent);
        Destroy(agent.gameObject);
    }

    /// <summary>
    /// Remove and destroy a hunter by reference (safe wrapper for external callers).
    /// </summary>
    public void DestroyAgentObject(HuntingBoidAgent agent)
    {
        DestroyAgent(agent);
    }

    Vector3 GetValidSpawnPosition(Vector3 centerPoint, Bounds area)
    {
        Bounds bounds = huntingArea != null ? huntingArea.bounds : area;
        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            Vector3 candidatePos = centerPoint;
            if (emission == EmissionStyle.Area)
            {
                candidatePos += UnityEngine.Random.insideUnitSphere * spawnRadius;
            }
            else
            {
                Vector3 dir = UnityEngine.Random.onUnitSphere;
                float angle = Vector3.Angle(transform.forward, dir);
                if (angle > spawnConeAngle) dir = Vector3.Slerp(transform.forward, dir, spawnConeAngle / Mathf.Max(angle, 0.0001f));
                float dist = UnityEngine.Random.Range(0.1f, spawnConeLength);
                candidatePos += dir.normalized * dist;
            }

            if (useHuntingArea && huntingArea != null)
            {
                candidatePos.x = Mathf.Clamp(candidatePos.x, bounds.min.x, bounds.max.x);
                candidatePos.y = Mathf.Clamp(candidatePos.y, bounds.min.y, bounds.max.y);
                candidatePos.z = Mathf.Clamp(candidatePos.z, bounds.min.z, bounds.max.z);
            }
            return candidatePos;
        }
        return centerPoint;
    }

    public void Clear()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);
        hunters.Clear();
    }

    // ---------------- Steering Core ----------------
    public Vector3 ComputeSteering(HuntingBoidAgent self, float dt, out (Vector3 podSep, Vector3 podAli, Vector3 podCoh, Vector3 role, Vector3 avoid) dbg)
    {
        Vector3 pos = self.Position;
        Vector3 vel = self.Velocity;

        Vector3 sep = Vector3.zero, ali = Vector3.zero, coh = Vector3.zero;
        int n = 0;
        float nr2 = neighborRadius * neighborRadius;
        float sr2 = separationRadius * separationRadius;

        foreach (var h in hunters)
        {
            if (h == self) continue;
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

        Vector3 roleForce = RoleForce(self, pos, vel, targetCentroid, targetAvgVel);

        Vector3 avoid = ObstacleAvoid(pos, vel);
        Vector3 boundaryAvoid = BoundaryAvoid(pos, vel);

        Vector3 steer = wSeparation * sep + wAlignment * ali + wCohesion * coh
                      + roleForce + avoid + boundaryAvoid;

        if (steer.sqrMagnitude > maxSteerForce * maxSteerForce)
            steer = steer.normalized * maxSteerForce;

        dbg = (sep, ali, coh, roleForce, avoid);
        return steer;
    }

    Vector3 RoleForce(HuntingBoidAgent self, Vector3 pos, Vector3 vel, Vector3 tgtCtr, Vector3 tgtVel)
    {
        Vector3 f = Vector3.zero;

        BoidAgent target = self.CurrentTarget;
        if (target != null && (target.controller == null || target.gameObject == null))
            self.ClearTarget();
        target = self.CurrentTarget;

        var targets = GetTargets();
        if (target == null && targets != null && targets.Count > 0)
        {
            target = FindNearestTarget(pos);
            self.SetTarget(target);
        }

        if (self.CanSwitchTarget() && targets != null && targets.Count > 0)
        {
            var best = FindNearestTarget(pos);
            if (best != null && best != target)
            {
                float currentDist = (target != null) ? (target.Position - pos).sqrMagnitude : float.PositiveInfinity;
                float bestDist = (best.Position - pos).sqrMagnitude;
                if (bestDist < currentDist * 0.7f || currentDist > strikeRange * strikeRange * 4f)
                    self.SetTarget(best);
            }
        }

        Vector3 aimCtr = self.HasTarget ? self.CurrentTarget.Position : tgtCtr;
        Vector3 aimVel = self.HasTarget ? self.CurrentTarget.Velocity : tgtVel;

        Vector3 toTgt = aimCtr - pos;
        float dist = toTgt.magnitude;
        Vector3 tgtDir = (aimVel.sqrMagnitude > 1e-6f) ? aimVel.normalized : Vector3.forward;

        float tLead = Mathf.Clamp(dist / Mathf.Max(0.1f, maxSpeed + aimVel.magnitude), 0.1f, 2.0f);
        Vector3 intercept = aimCtr + aimVel * tLead;

        // Opportunistic strike for any role
        bool canStrikeNow = dist <= strikeRange && self.CanStrike();
        if (canStrikeNow)
        {
            Vector3 dash = (intercept - pos).normalized * (maxSpeed * strikeBoost);
            float strikeWeight = (self.role == HunterRole.Striker) ? wPursuit * 1.2f : wPursuit * 0.9f;
            f += strikeWeight * (dash - vel);
            self.NotifyStrikeBoost();
            self.ResetStrikeCooldown();
        }
        else
        {
            // Ring/orbit behavior
            Vector3 up = Vector3.up;
            Vector3 radial = (pos - aimCtr);
            if (radial.sqrMagnitude < 0.01f) radial = Quaternion.AngleAxis(45f, up) * tgtDir * encircleRadius;
            Vector3 tangent = Vector3.Cross(up, radial).normalized;

            // Role-based ring offsets with staggering to keep the flock spread and moving in different arcs
            int roleIdx = IndexAmongRole(self, self.role);
            float staggerBase = 55f;
            float stagger = ((roleIdx % 2 == 0) ? 1f : -1f) * staggerBase * (1 + roleIdx / 2f);
            float roleOffset = self.role switch
            {
                HunterRole.Leader => 0f,
                HunterRole.Flanker => flankOffsetAngle,
                HunterRole.Support => -flankOffsetAngle,
                HunterRole.Striker => flankOffsetAngle * 0.5f,
                _ => 0f
            };

            float totalAngle = roleOffset + stagger;
            Quaternion roleRot = Quaternion.AngleAxis(totalAngle, up);
            Vector3 ringDir = roleRot * tangent;
            Vector3 ringTarget = aimCtr + ringDir.normalized * encircleRadius;

            Vector3 orbitPull = ringTarget - pos;
            Vector3 orbitFlow = Vector3.Cross(up, (pos - aimCtr)).normalized * maxSpeed; // true tangential flow
            // Keep a medium altitude relative to target
            float desiredY = aimCtr.y + orbitHeightOffset;
            Vector3 heightForce = new Vector3(0f, desiredY - pos.y, 0f);

            float orbitWeight = (self.role == HunterRole.Leader) ? wEncircle * 0.8f : wEncircle;
            f += orbitWeight * orbitPull
               + wPursuit * 0.4f * (orbitFlow - vel)
               + wPursuit * 0.25f * (intercept - pos)
               + wOrbitHeight * heightForce;
        }

        if (f.sqrMagnitude > 1e-8f)
        {
            Vector3 desired = f.normalized * maxSpeed;
            return desired - vel;
        }
        return Vector3.zero;
    }
    int IndexAmongRole(HuntingBoidAgent self, HunterRole role)
    {
        int idx = 0;
        foreach (var a in hunters)
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

    void GetTargetStats(out Vector3 centroid, out Vector3 avgVel)
    {
        centroid = Vector3.zero; avgVel = Vector3.zero;
        var list = GetTargets();
        if (list == null || list.Count == 0) return;

        int count = list.Count;
        for (int i = 0; i < count; i++)
        {
            centroid += list[i].Position;
            avgVel += list[i].Velocity;
        }
        centroid /= count;
        avgVel /= Mathf.Max(1, count);
    }

    // ---------------- Targeting ----------------
    float retargetTimer = 0f;
    void AssignTargetsPeriodically(float dt)
    {
        retargetTimer -= dt;
        if (retargetTimer > 0f) return;
        retargetTimer = retargetInterval;

        var list = GetTargets();
        if (list == null || list.Count == 0 || hunters.Count == 0) return;

        var targetToCount = new Dictionary<BoidAgent, int>();
        foreach (var h in hunters)
        {
            if (h.CurrentTarget != null)
            {
                if (!targetToCount.ContainsKey(h.CurrentTarget)) targetToCount[h.CurrentTarget] = 0;
                targetToCount[h.CurrentTarget]++;
            }
        }

        foreach (var h in hunters)
        {
            if (!h.CanSwitchTarget()) continue;
            var best = FindBestTargetFor(h, targetToCount);
            if (best != null && best != h.CurrentTarget)
            {
                h.SetTarget(best);
                if (!targetToCount.ContainsKey(best)) targetToCount[best] = 0;
                targetToCount[best]++;
            }
        }
    }

    void EnsureTargetsAssigned()
    {
        var list = GetTargets();
        if (list == null || list.Count == 0) return;
        for (int i = 0; i < hunters.Count; i++)
        {
            var h = hunters[i];
            if (h == null) continue;
            if (!h.HasTarget)
            {
                var nearest = FindNearestTarget(h.Position);
                if (nearest != null) h.SetTarget(nearest);
            }
        }
    }

    BoidAgent FindBestTargetFor(HuntingBoidAgent hunter, Dictionary<BoidAgent, int> targetToCount)
    {
        BoidAgent best = null;
        float bestScore = float.NegativeInfinity;
        Vector3 pos = hunter.Position;
        var list = GetTargets();
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
        {
            var tgt = list[i];
            int c = targetToCount.TryGetValue(tgt, out int v) ? v : 0;
            if (c >= maxHuntersPerTarget) continue;

            float d2 = (tgt.Position - pos).sqrMagnitude;
            float distScore = 1f / Mathf.Max(0.1f, Mathf.Sqrt(d2));

            int neighbors = CountTargetNeighbors(tgt.Position, 1.5f);
            float isolationScore = 1f / Mathf.Max(1f, neighbors);

            float score = wTargetDistance * distScore + wTargetIsolation * isolationScore;
            if (score > bestScore)
            {
                bestScore = score;
                best = tgt;
            }
        }
        return best;
    }

    BoidAgent FindNearestTarget(Vector3 pos)
    {
        var list = GetTargets();
        if (list == null) return null;

        if (!targetGridReady)
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
        int3 cell = (int3)math.floor((float3)pos / targetCellSize);
        float searchR = neighborRadius * 1.5f;
        float searchR2 = searchR * searchR;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int3 c = cell + new int3(dx, dy, dz);
            var it = targetGrid.GetValuesForKey(Hash(c));
            while (it.MoveNext())
            {
                int idx = it.Current;
                float d2 = math.lengthsq(targetPositions[idx] - (float3)pos);
                if (d2 < bestD2 && d2 <= searchR2)
                {
                    bestD2 = d2;
                    best = list[idx];
                }
            }
        }
        return best;
    }

    int CountTargetNeighbors(Vector3 center, float radius)
    {
        var list = GetTargets();
        if (list == null) return 0;

        if (!targetGridReady)
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
        int3 cell = (int3)math.floor((float3)center / targetCellSize);
        int range = Mathf.CeilToInt(radius / targetCellSize) + 1;
        for (int dz = -range; dz <= range; dz++)
        for (int dy = -range; dy <= range; dy++)
        for (int dx = -range; dx <= range; dx++)
        {
            int3 c = cell + new int3(dx, dy, dz);
            var it = targetGrid.GetValuesForKey(Hash(c));
            while (it.MoveNext())
            {
                int idx = it.Current;
                float d2 = math.lengthsq(targetPositions[idx] - (float3)center);
                if (d2 <= r2p) count++;
            }
        }
        return count;
    }

    void EnsureRoleCoverage()
    {
        if (hunters.Count == 0) return;

        int leadersCount = 0;
        for (int i = 0; i < hunters.Count; i++)
        {
            if (hunters[i] == null) continue;
            if (hunters[i].role == HunterRole.Leader) leadersCount++;
        }

        // Promote the first valid hunter to Leader if none exists.
        if (leadersCount == 0)
        {
            for (int i = 0; i < hunters.Count; i++)
            {
                if (hunters[i] == null) continue;
                hunters[i].role = HunterRole.Leader;
                break;
            }
        }
    }

    // ---------------- Capture handling ----------------
    public void OnCapturedTarget(BoidAgent target)
    {
        if (target != null)
        {
            if (target.controller != null)
                target.controller.RemoveAgent(target);
            else
                Destroy(target.gameObject);
        }

        captureCount++;
        RefreshTargetPool();
    }

    public void RetargetToTag(string tag)
    {
        targetTag = tag;
        RefreshTargetPool();
    }

    public void RefreshTargetPool()
    {
        targetPool.Clear();
        var found = FindObjectsOfType<BoidAgent>(false);
        for (int i = 0; i < found.Length; i++)
        {
            if (string.IsNullOrEmpty(targetTag) || found[i].CompareTag(targetTag))
                targetPool.Add(found[i]);
        }
    }

    List<BoidAgent> GetTargets()
    {
        return targetPool;
    }
    // ---------------- UI + Save/Load ----------------
    [Serializable]
    class HuntingSettings
    {
        public int leaders, flankers, strikers, supports;
        public float minSpeed, maxSpeed, maxSteerForce;
        public float neighborRadius, separationRadius;
        public float wSeparation, wAlignment, wCohesion;
        public float wPursuit, wEncircle, wCorral;
        public float encircleRadius, flankOffsetAngle;
        public float strikeRange, strikeBoost, strikeCooldown;
        public float avoidDistance, avoidDistanceCap, avoidProbeAngle, agentRadius;
        public bool showRoleText;
        public int captureCount;
        public float spawnRadius;
        public int maxSpawnAttempts;
        public string targetTag;
        public bool spawnOnStart;
        public bool useHuntingArea;
        public float spawnConeAngle;
        public float spawnConeLength;
        public EmissionStyle emission;
        public float boundaryAvoidRadius;
    }

    HuntingSettings Collect() => new()
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
        agentRadius = agentRadius,
        showRoleText = showRoleText,
        captureCount = captureCount,
        spawnRadius = spawnRadius,
        maxSpawnAttempts = maxSpawnAttempts,
        targetTag = targetTag,
        spawnOnStart = spawnOnStart,
        useHuntingArea = useHuntingArea,
        spawnConeAngle = spawnConeAngle,
        spawnConeLength = spawnConeLength,
        emission = emission,
        boundaryAvoidRadius = boundaryAvoidRadius
    };

    void Apply(HuntingSettings s, bool respawn)
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
        avoidDistance = s.avoidDistance; avoidDistanceCap = s.avoidDistanceCap; avoidProbeAngle = s.avoidProbeAngle; agentRadius = s.agentRadius;
        showRoleText = s.showRoleText;
        captureCount = s.captureCount;
        spawnRadius = s.spawnRadius;
        maxSpawnAttempts = s.maxSpawnAttempts;
        targetTag = s.targetTag;
        spawnOnStart = s.spawnOnStart;
        useHuntingArea = s.useHuntingArea;
        spawnConeAngle = s.spawnConeAngle;
        spawnConeLength = s.spawnConeLength;
        emission = s.emission;
        boundaryAvoidRadius = s.boundaryAvoidRadius;

        if (respawn && countsChanged) SpawnHunters();
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
            if (!File.Exists(JsonPath)) { Debug.LogWarning("No hunting boid settings file."); return; }
            var s = JsonUtility.FromJson<HuntingSettings>(File.ReadAllText(JsonPath));
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
        if (!PlayerPrefs.HasKey(kPrefs)) { Debug.LogWarning("No hunting boid prefs."); return; }
        var s = JsonUtility.FromJson<HuntingSettings>(PlayerPrefs.GetString(kPrefs));
        Apply(s, true);
    }
    void TryLoad()
    {
        if (File.Exists(JsonPath)) LoadFromFile();
        else if (PlayerPrefs.HasKey(kPrefs)) LoadFromPrefs();
    }
    void OnGUI()
    {
        if (!showRuntimeUI) return;
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

        if (GUILayout.Button(showPanel ? "Hunters(F3) ?" : "Hunters(F3) ?", GUILayout.Height(handleH)))
            showPanel = !showPanel;

        if (!showPanel)
        {
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label("<b>Hunting Flock (Tools)</b>", new GUIStyle(GUI.skin.label) { richText = true });

        scroll = GUILayout.BeginScrollView(scroll);

        GUILayout.Label("<b>Roles</b>", new GUIStyle(GUI.skin.label) { richText = true });
        leaders = IntSliderT("Leaders", "Number of leaders (strong pursuit toward intercept).", leaders, 1, 4);
        flankers = IntSliderT("Flankers", "Agents that orbit targets on a ring to contain them.", flankers, 0, 16);
        strikers = IntSliderT("Strikers", "Agents that dash in to strike when close.", strikers, 0, 16);
        supports = IntSliderT("Supports", "Agents that stay behind targets to corral them.", supports, 0, 16);
        if (GUILayout.Button("Respawn Flock")) SpawnHunters();

        GUILayout.Space(6);
        GUILayout.Label("<b>Targeting</b>");
        targetTag = GUILayout.TextField(targetTag);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Retarget Now")) RefreshTargetPool();
        if (GUILayout.Button("Share Leader Target")) shareLeaderTarget = !shareLeaderTarget;
        GUILayout.EndHorizontal();
        GUILayout.Label($"Targets tracked: {(GetTargets()?.Count ?? 0)}");

        GUILayout.Space(6);
        GUILayout.Label("<b>Spawning</b>", new GUIStyle(GUI.skin.label) { richText = true });
        spawnOnStart = ToggleT("Spawn On Start", "Spawn once on Start().", spawnOnStart);
        useHuntingArea = ToggleT("Use Hunting Area", "Clamp spawn/bounds to huntingArea if assigned.", useHuntingArea);
        emission = (EmissionStyle)GUILayout.SelectionGrid((int)emission, new[] { "Area", "Cone" }, 2);
        spawnRadius = SliderT("Spawn Radius", "Area emission radius.", spawnRadius, 0.1f, 100f);
        spawnConeAngle = SliderT("Cone Angle", "Max angle for cone emission.", spawnConeAngle, 1f, 90f);
        spawnConeLength = SliderT("Cone Length", "Length of cone emission.", spawnConeLength, 0.1f, 50f);
        maxSpawnAttempts = IntSliderT("Max Spawn Attempts", "Maximum attempts to find valid spawn position.", maxSpawnAttempts, 10, 200);

        GUILayout.Space(6);
        GUILayout.Label("<b>Speeds</b>");
        minSpeed = SliderT("Min Speed", "Minimum cruising speed.", minSpeed, 0.1f, maxSpeed);
        maxSpeed = SliderT("Max Speed", "Top speed used for desired velocities and dashes.", maxSpeed, minSpeed, 20f);
        maxSteerForce = SliderT("Max Steer", "Upper limit on steering force.", maxSteerForce, 0.1f, 30f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Flock Rules</b>");
        neighborRadius = SliderT("Neighbor Radius", "How far squad-mates influence alignment/cohesion.", neighborRadius, 0.1f, 10f);
        separationRadius = SliderT("Separation Radius", "Distance where strong separation kicks in.", separationRadius, 0.05f, 20f);
        wSeparation = SliderT("W Separation", "Weight of separation.", wSeparation, 0f, 10f);
        wAlignment = SliderT("W Alignment", "Weight of alignment.", wAlignment, 0f, 10f);
        wCohesion = SliderT("W Cohesion", "Weight of cohesion.", wCohesion, 0f, 10f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Hunt</b>");
        wPursuit = SliderT("W Pursuit", "Pursuit strength.", wPursuit, 0f, 10f);
        wEncircle = SliderT("W Encircle", "Flankers ring pull.", wEncircle, 0f, 10f);
        wCorral = SliderT("W Corral", "Support corral strength.", wCorral, 0f, 10f);
        encircleRadius = SliderT("Encircle Radius", "Ring radius around targets.", encircleRadius, 0.5f, 20f);
        flankOffsetAngle = SliderT("Flank Angle", "Spacing angle offsets around the ring.", flankOffsetAngle, 0f, 160f);
        strikeRange = SliderT("Strike Range", "Distance threshold to trigger a strike dash.", strikeRange, 0.5f, 10f);
        strikeBoost = SliderT("Strike Boost", "Speed multiplier during strike dashes.", strikeBoost, 1f, 3f);
        strikeCooldown = SliderT("Strike Cooldown", "Cooldown between strikes for each striker.", strikeCooldown, 0f, 8f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Obstacles</b>");
        avoidDistance = SliderT("Avoid Dist", "Forward probe length for obstacle detection.", avoidDistance, 0.2f, 15f);
        avoidDistanceCap = SliderT("Avoid Dist Cap", "Optional max probe length (0 = uncapped).", avoidDistanceCap, 0f, 30f);
        avoidProbeAngle = SliderT("Avoid Angle", "Side probe spread to feel around obstacles.", avoidProbeAngle, 0f, 85f);
        agentRadius = SliderT("Agent Radius", "Radius used for sweeps and spherecasts.", agentRadius, 0.05f, 3f);
        boundaryAvoidRadius = SliderT("Boundary Pad", "Distance from walls where soft avoidance begins.", boundaryAvoidRadius, 0.1f, 5f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Stats</b>");
        GUILayout.Label($"Capture Count: <b>{captureCount}</b>", new GUIStyle(GUI.skin.label) { richText = true });
        if (GUILayout.Button("Reset Capture Count")) captureCount = 0;

        GUILayout.Space(6);
        GUILayout.Label("<b>Labels</b>");
        showRoleText = ToggleT("Show Role Text", "Show text labels above each hunter indicating its role.", showRoleText);

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save File")) SaveToFile();
        if (GUILayout.Button("Load File")) LoadFromFile();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save PlayerPrefs")) SaveToPrefs();
        if (GUILayout.Button("Load PlayerPrefs")) LoadFromPrefs();
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label($"Save Path:<size=10>{Application.persistentDataPath}</size>", new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

        GUILayout.EndScrollView();

        string tip = GUI.tooltip;
        var tipStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
        GUILayout.Space(4);
        GUILayout.Label(string.IsNullOrEmpty(tip) ? " " : tip, tipStyle, GUILayout.ExpandWidth(true));

        GUILayout.EndArea();
    }

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

#if UNITY_EDITOR
    void OnValidate()
    {
        if (Application.isPlaying)
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
    }
#endif

    bool KeyDown_F3()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.F3);
#endif
    }

    static int Hash(int3 cell) => (int)math.hash(cell);

    void OnDrawGizmosSelected()
    {
        if (huntingArea && useHuntingArea)
        {
            Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.15f);
            Gizmos.DrawCube(huntingArea.bounds.center, huntingArea.bounds.size);
            Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.5f);
            Gizmos.DrawWireCube(huntingArea.bounds.center, huntingArea.bounds.size);
        }
    }

    void OnDrawGizmos()
    {
        Vector3 centerPoint = spawnCenter ? spawnCenter.position : (huntingArea ? huntingArea.bounds.center : transform.position);

        Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.08f);
        Gizmos.DrawSphere(centerPoint, spawnRadius);

        Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.3f);
        Gizmos.DrawWireSphere(centerPoint, spawnRadius);

        Gizmos.color = new Color(0.3f, 0.9f, 0.2f, 0.6f);
        Gizmos.DrawWireCube(centerPoint, Vector3.one * 0.4f);

        if (emission == EmissionStyle.Cone)
        {
            Vector3 dir = transform.forward;
            Vector3 tip = centerPoint;
            Vector3 end = tip + dir.normalized * spawnConeLength;
            Gizmos.color = new Color(0.1f, 0.7f, 0.4f, 0.25f);
            Gizmos.DrawLine(tip, end);
        }
    }
}
