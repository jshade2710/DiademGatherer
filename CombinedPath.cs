using System.Numerics;
using DiademGatherer.IPC;

namespace DiademGatherer;

// A port of GatherBuddyReborn's FindCombinedPath (AutoGather.Movement.cs,
// Apache-2.0). This is the piece that makes its node approach reliable.
//
// The idea: don't choose "fly" OR "walk" for the whole approach. Build a single
// path that FLIES to a landing point exactly `landingDistance` short of the
// target, then WALKS the last stretch on the ground. We descend under navigation
// control and arrive on foot — no hovering over the target, and no surface
// detour around terrain.
//
// vnavmesh pathfinding is asynchronous, so this runs as a small state machine
// stepped from the framework tick: ground path → landing point → fly path. All
// IPC stays on the framework thread; only the pathfind tasks are awaited.
public sealed class CombinedPathBuilder
{
    private enum Stage { Idle, GroundPending, FlyPending, Done, Failed }

    private Stage _stage = Stage.Idle;
    private Task<List<Vector3>>? _task;
    private Vector3 _target;
    private float   _landingDistance;
    private List<Vector3> _groundPath = new();
    private int _splitIndex;

    public List<Vector3> Path { get; private set; } = new();
    public int  GroundLegCount { get; private set; }

    public bool IsIdle    => _stage == Stage.Idle;
    public bool IsWorking => _stage is Stage.GroundPending or Stage.FlyPending;
    public bool IsDone    => _stage == Stage.Done;
    public bool IsFailed  => _stage == Stage.Failed;

    public void Reset()
    {
        _stage = Stage.Idle;
        _task  = null;
        _groundPath = new List<Vector3>();
        Path = new List<Vector3>();
        GroundLegCount = 0;
        _splitIndex = 0;
    }

    public void Start(NavmeshIPC nav, Vector3 player, Vector3 target, float landingDistance, bool flying)
    {
        Reset();
        _target          = target;
        _landingDistance = landingDistance;

        // Ground pathing has to start from the floor beneath us, not mid-air.
        var start = flying ? nav.PointOnFloor(player, 5f) : player;
        if (start == null) { _stage = Stage.Failed; return; }

        _task  = nav.PathfindAsync(start.Value, target, fly: false);
        _stage = _task == null ? Stage.Failed : Stage.GroundPending;
    }

    public void Step(NavmeshIPC nav, Vector3 player)
    {
        if (_task == null || !_task.IsCompleted) return;
        if (_task.IsFaulted || _task.IsCanceled) { _stage = Stage.Failed; _task = null; return; }

        if (_stage == Stage.GroundPending)
        {
            _groundPath = _task.Result ?? new List<Vector3>();
            _task = null;
            if (_groundPath.Count < 2) { _stage = Stage.Failed; return; }

            _splitIndex = FindIntersection(_groundPath, _target, _landingDistance);
            if (_splitIndex + 1 >= _groundPath.Count) { _stage = Stage.Failed; return; }

            var landingWP = GetPointAtRadius(_groundPath[_splitIndex], _groundPath[_splitIndex + 1],
                                             _target, _landingDistance);

            // GBR's "// Diadem fix": snap the landing point onto the mesh with a
            // tall vertical extent, or uneven terrain yields a point in mid-air.
            var meshWP = nav.NearestPoint(landingWP, _landingDistance, 10f);
            if (meshWP == null || MathF.Abs(_target.Y - meshWP.Value.Y) > 10f)
            { _stage = Stage.Failed; return; }

            _task  = nav.PathfindAsync(player, meshWP.Value, fly: true);
            _stage = _task == null ? Stage.Failed : Stage.FlyPending;
            return;
        }

        if (_stage == Stage.FlyPending)
        {
            var flyPath = _task.Result ?? new List<Vector3>();
            _task = null;
            if (flyPath.Count == 0) { _stage = Stage.Failed; return; }

            if (flyPath.Count > 1 && Vector3.DistanceSquared(flyPath[^1], flyPath[^2]) < 0.01f)
                flyPath.RemoveAt(flyPath.Count - 1);

            var groundLeg = _groundPath.Skip(_splitIndex + 1).ToList();
            flyPath.AddRange(groundLeg);

            Path           = flyPath;
            GroundLegCount = groundLeg.Count;
            _stage         = Stage.Done;
        }
    }

    // The last waypoint still outside `radius` of the target — where the ground
    // route crosses the landing circle.
    private static int FindIntersection(List<Vector3> wp, Vector3 target, float radius)
    {
        var r2 = radius * radius;
        for (var i = wp.Count - 2; i > 0; i--)
            if (Vector3.DistanceSquared(wp[i], target) > r2)
                return i;
        return 0;
    }

    // The point on segment a→b sitting exactly `radius` from `target`.
    private static Vector3 GetPointAtRadius(Vector3 a, Vector3 b, Vector3 target, float radius)
    {
        var seg = b - a;
        var len = seg.Length();
        if (len < 0.001f) return a;
        var dir = seg / len;

        var m    = a - target;
        var half = Vector3.Dot(m, dir);
        var c    = Vector3.Dot(m, m) - radius * radius;
        var disc = half * half - c;
        if (disc < 0) return a;

        var t = Math.Clamp(-half + MathF.Sqrt(disc), 0f, len);
        return a + dir * t;
    }
}
