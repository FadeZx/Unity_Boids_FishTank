using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(-50)]
public class BoidController : MonoBehaviour
{
    [Header("References")]
    public BoxCollider simulationArea;     // IsTrigger = true
    public BoidAgent boidPrefab;
    public int boidCount = 100;

    [Header("Speeds")]
    public float minSpeed = 1.5f;
    public float maxSpeed = 4.0f;
    public float maxSteerForce = 6.0f;

    [Header("Neighborhood")]
    public float neighborRadius = 2.0f;
    public float separationRadius = 0.6f;

    [Header("Weights")]
    public float weightSeparation = 1.5f;
    public float weightAlignment = 1.0f;
    public float weightCohesion = 1.0f;
    public float weightBounds = 2.0f;
    public float weightObstacleAvoid = 3.0f;

    [Header("Obstacle Avoidance")]
    public LayerMask obstacleMask;       // set to "Obstacle"
    public float avoidDistance = 1.5f;   // ray length
    public float avoidProbeAngle = 25f;  // degrees (side feelers)

    [Header("Debug")]
    public bool drawDebug = false;

    [HideInInspector] public List<BoidAgent> agents = new List<BoidAgent>();

    void Start()
    {
        if (!simulationArea)
        {
            Debug.LogError("Assign a BoxCollider as simulationArea (Is Trigger).");
            enabled = false;
            return;
        }

        Spawn();
    }

    void Spawn()
    {
        agents.Clear();
        var area = simulationArea.bounds;

        for (int i = 0; i < boidCount; i++)
        {
            Vector3 p = new Vector3(
                Random.Range(area.min.x, area.max.x),
                Random.Range(area.min.y, area.max.y),
                Random.Range(area.min.z, area.max.z)
            );

            var a = Instantiate(boidPrefab, p, Quaternion.identity, transform);
            a.controller = this;
            a.Velocity = Random.insideUnitSphere.normalized * Random.Range(minSpeed, maxSpeed);
            agents.Add(a);
        }
    }

    public Vector3 ComputeSteering(BoidAgent self, float dt, out (Vector3 sep, Vector3 ali, Vector3 coh, Vector3 bounds, Vector3 avoid) forces)
    {
        Vector3 pos = self.Position;
        Vector3 vel = self.Velocity;

        Vector3 sep = Vector3.zero;
        Vector3 ali = Vector3.zero;
        Vector3 coh = Vector3.zero;
        int neighborCount = 0;

        float nRad2 = neighborRadius * neighborRadius;
        float sRad2 = separationRadius * separationRadius;

        foreach (var other in agents)
        {
            if (other == self) continue;
            Vector3 to = other.Position - pos;
            float d2 = to.sqrMagnitude;
            if (d2 > nRad2) continue;
            neighborCount++;

            Vector3 dir = to.normalized;
            if (d2 < sRad2)
                sep -= dir / Mathf.Max(0.001f, Mathf.Sqrt(d2));
            ali += other.Velocity;
            coh += other.Position;
        }

        if (neighborCount > 0)
        {
            ali = (ali / neighborCount).normalized * maxSpeed - vel;
            coh = ((coh / neighborCount) - pos);
        }

        if (sep.sqrMagnitude > 0.0001f) sep = sep.normalized * maxSpeed - vel;

        Vector3 boundsForce = BoundsSteer(pos, vel);
        Vector3 avoid = ObstacleAvoid(pos, vel);

        // Weighted blend
        Vector3 steer =
            weightSeparation * sep +
            weightAlignment * ali +
            weightCohesion * coh +
            weightBounds * boundsForce +
            weightObstacleAvoid * avoid;

        // Clamp
        if (steer.sqrMagnitude > maxSteerForce * maxSteerForce)
            steer = steer.normalized * maxSteerForce;

        // return all unweighted vectors for debug
        forces = (sep, ali, coh, boundsForce, avoid);
        return steer;
    }

    Vector3 BoundsSteer(Vector3 pos, Vector3 vel)
    {
        var b = simulationArea.bounds;
        Vector3 steer = Vector3.zero;
        // Soft zone toward center when near borders
        float pad = 0.5f; // padding from walls
        Vector3 target = pos;

        if (pos.x < b.min.x + pad) target.x = b.min.x + pad;
        else if (pos.x > b.max.x - pad) target.x = b.max.x - pad;

        if (pos.y < b.min.y + pad) target.y = b.min.y + pad;
        else if (pos.y > b.max.y - pad) target.y = b.max.y - pad;

        if (pos.z < b.min.z + pad) target.z = b.min.z + pad;
        else if (pos.z > b.max.z - pad) target.z = b.max.z - pad;

        if (target != pos)
        {
            Vector3 desired = (target - pos).normalized * maxSpeed;
            steer = desired - vel;
        }
        return steer;
    }

    Vector3 ObstacleAvoid(Vector3 pos, Vector3 vel)
    {
        if (vel.sqrMagnitude < 0.0001f) return Vector3.zero;
        Vector3 fwd = vel.normalized;
        Vector3 steer = Vector3.zero;

        // Forward ray
        if (Physics.Raycast(pos, fwd, out RaycastHit hit, avoidDistance, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            // steer away from obstacle normal
            Vector3 away = Vector3.Reflect(fwd, hit.normal);
            steer += away;
        }

        // Side feelers (spread)
        Quaternion leftQ = Quaternion.AngleAxis(-avoidProbeAngle, Vector3.up);
        Quaternion rightQ = Quaternion.AngleAxis(avoidProbeAngle, Vector3.up);

        Vector3 left = leftQ * fwd;
        Vector3 right = rightQ * fwd;

        if (Physics.Raycast(pos, left, avoidDistance * 0.8f, obstacleMask, QueryTriggerInteraction.Ignore))
            steer += -Vector3.Cross(Vector3.up, left).normalized;

        if (Physics.Raycast(pos, right, avoidDistance * 0.8f, obstacleMask, QueryTriggerInteraction.Ignore))
            steer += Vector3.Cross(Vector3.up, right).normalized;

        return steer;
    }

    void OnDrawGizmosSelected()
    {
      
    }
}
