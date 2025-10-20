#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

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

    [Header("Obstacle Avoidance")]
    public LayerMask obstacleMask;
    public float avoidDistance = 2.5f;
    public float avoidProbeAngle = 25f;
    public float orcaRadius = 0.25f;

    [Header("Debug")]
    public bool drawDebug = false;

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
        SpawnPod();
    }

    void Update()
    {
        if (KeyDown_F2()) showUI = !showUI;

        // compute prey centroid/avg vel once per frame
        GetPreyStats(out preyCentroid, out preyAvgVel);

        if (drawDebug)
        {
            Debug.DrawLine(preyCentroid, preyCentroid + preyAvgVel.normalized * 2f, Color.cyan);
        }
    }

    // ---------------- Spawning / Roles ----------------
    public void SpawnPod()
    {
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
                a.Velocity = UnityEngine.Random.insideUnitSphere.normalized * UnityEngine.Random.Range(minSpeed, maxSpeed);
                pod.Add(a);
            }
        }

        SpawnRole(Mathf.Max(1, leaders), OrcaRole.Leader);
        SpawnRole(Mathf.Max(0, flankers), OrcaRole.Flanker);
        SpawnRole(Mathf.Max(0, strikers), OrcaRole.Striker);
        SpawnRole(Mathf.Max(0, supports), OrcaRole.Support);
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

        // --- obstacle avoidance ---
        Vector3 avoid = ObstacleAvoid(pos, vel);

        // blend
        Vector3 steer = wSeparation * sep + wAlignment * ali + wCohesion * coh
                      + roleForce + avoid;

        // limit
        if (steer.sqrMagnitude > maxSteerForce * maxSteerForce)
            steer = steer.normalized * maxSteerForce;

        dbg = (sep, ali, coh, roleForce, avoid);
        return steer;
    }

    Vector3 RoleForce(OrcaAgent self, Vector3 pos, Vector3 vel, Vector3 preyCtr, Vector3 preyVel, float dt)
    {
        Vector3 f = Vector3.zero;
        Vector3 toPrey = preyCtr - pos;
        float dist = toPrey.magnitude;
        Vector3 preyDir = (preyVel.sqrMagnitude > 1e-6f) ? preyVel.normalized : Vector3.forward;

        // Intercept point for pursuit
        float tLead = Mathf.Clamp(dist / Mathf.Max(0.1f, maxSpeed + preyVel.magnitude), 0.1f, 2.0f);
        Vector3 intercept = preyCtr + preyVel * tLead;

        switch (self.role)
        {
            case OrcaRole.Leader:
                // Strong pursuit toward intercept
                f += wPursuit * (intercept - pos);
                break;

            case OrcaRole.Flanker:
                // Move to ring around prey, offset left/right relative to prey heading
                // Assign each flanker a unique angle based on index for spacing
                int index = IndexAmongRole(self, OrcaRole.Flanker);
                float sign = (index % 2 == 0) ? 1f : -1f;
                float angle = flankOffsetAngle * (1 + index / 2); // 45, 90, ...
                Quaternion q = Quaternion.AngleAxis(sign * angle, Vector3.up);
                Vector3 tangent = q * preyDir;
                Vector3 target = preyCtr + tangent.normalized * encircleRadius;
                f += wEncircle * (target - pos);
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
                // Stay slightly behind the prey direction to corral (herding)
                Vector3 behind = preyCtr - preyDir * (encircleRadius * 1.1f);
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
        public bool drawDebug;
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
        drawDebug = drawDebug
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
        drawDebug = s.drawDebug;

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

    void OnGUI()
    {
        if (!showUI) return;
        const int w = 340;
        Rect r = new Rect(Screen.width - w - 12, 12, w, Screen.height - 24);
        GUILayout.BeginArea(r, GUI.skin.box);
        GUILayout.Label("<b>Orca Pod (Predators)</b>", new GUIStyle(GUI.skin.label) { richText = true });

        scroll = GUILayout.BeginScrollView(scroll);

        GUILayout.Label("<b>Roles</b>", new GUIStyle(GUI.skin.label) { richText = true });
        leaders = IntSlider("Leaders", leaders, 1, 4);
        flankers = IntSlider("Flankers", flankers, 0, 16);
        strikers = IntSlider("Strikers", strikers, 0, 16);
        supports = IntSlider("Supports", supports, 0, 16);
        if (GUILayout.Button("Respawn Pod")) SpawnPod();

        GUILayout.Space(6);
        GUILayout.Label("<b>Speeds</b>");
        minSpeed = Slider("Min Speed", minSpeed, 0.1f, maxSpeed);
        maxSpeed = Slider("Max Speed", maxSpeed, minSpeed, 20f);
        maxSteerForce = Slider("Max Steer", maxSteerForce, 0.1f, 30f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Pod Rules</b>");
        neighborRadius = Slider("Neighbor Radius", neighborRadius, 0.1f, 10f);
        separationRadius = Slider("Separation Radius", separationRadius, 0.05f, neighborRadius);
        wSeparation = Slider("W Separation", wSeparation, 0f, 10f);
        wAlignment = Slider("W Alignment", wAlignment, 0f, 10f);
        wCohesion = Slider("W Cohesion", wCohesion, 0f, 10f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Hunt</b>");
        wPursuit = Slider("W Pursuit", wPursuit, 0f, 10f);
        wEncircle = Slider("W Encircle", wEncircle, 0f, 10f);
        wCorral = Slider("W Corral", wCorral, 0f, 10f);
        encircleRadius = Slider("Encircle Radius", encircleRadius, 0.5f, 20f);
        flankOffsetAngle = Slider("Flank Angle", flankOffsetAngle, 0f, 160f);
        strikeRange = Slider("Strike Range", strikeRange, 0.5f, 10f);
        strikeBoost = Slider("Strike Boost", strikeBoost, 1f, 3f);
        strikeCooldown = Slider("Strike Cooldown", strikeCooldown, 0f, 8f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Obstacles</b>");
        avoidDistance = Slider("Avoid Dist", avoidDistance, 0.2f, 8f);
        avoidProbeAngle = Slider("Avoid Angle", avoidProbeAngle, 0f, 85f);
        orcaRadius = Slider("Orca Radius", orcaRadius, 0.05f, 0.6f);

        drawDebug = Toggle("Draw Debug", drawDebug);

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save → File")) SaveToFile();
        if (GUILayout.Button("Load ← File")) LoadFromFile();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save → PlayerPrefs")) SaveToPrefs();
        if (GUILayout.Button("Load ← PlayerPrefs")) LoadFromPrefs();
        GUILayout.EndHorizontal();

        GUILayout.EndScrollView();
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

    // input helpers
    bool KeyDown_F2()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.F2);
#endif
    }

    // Gizmo: draw ring around prey
    void OnDrawGizmos()
    {
        if (!drawDebug || preyController == null) return;
        Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.2f);
        Gizmos.DrawWireSphere(preyCentroid, encircleRadius);
    }
}
