using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System.Numerics;

namespace DiademGatherer.IPC;

// vnavmesh IPC — sourced from:
// https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/IPCProvider.cs
//
// IMPORTANT: every label is prefixed "vnavmesh." (RegisterFunc does
// `"vnavmesh." + name`). Subscribing without the prefix binds to a
// nonexistent gate: GetIpcSubscriber still succeeds, and only InvokeFunc
// throws — which is why availability must be probed by invoking, not by
// construction succeeding.
public sealed class NavmeshIPC : IDisposable
{
    private readonly ICallGateSubscriber<bool>? _isReady;
    private readonly ICallGateSubscriber<float>? _buildProgress;
    private readonly ICallGateSubscriber<bool>? _isRunning;
    private readonly ICallGateSubscriber<object>? _stop;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool>? _pathfindAndMoveCloseTo;

    // SimpleMove.PathfindInProgress() → bool — true while a path is still
    // being computed (async), BEFORE Path.IsRunning turns true.
    private readonly ICallGateSubscriber<bool>? _pathfindInProgress;

    // Query.Mesh.PointOnFloor(pos, allowUnlandable, halfExtentXZ) → Vector3?
    // The walkable floor under a position. Used to aim at a spot we can stand
    // on, so we land and walk in instead of hovering overhead and dropping.
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?>? _pointOnFloor;

    // Query.Mesh.NearestPoint(pos, halfExtentXZ, halfExtentY) → Vector3?
    // Snaps a world position onto the navmesh. A gathering node's raw position
    // is often NOT a walkable point (clipped into terrain, or at the bottom of a
    // pit); pathing straight to it makes vnavmesh route the long way round.
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?>? _nearestPoint;

    // Raw pathfinding + waypoint following, which is what GBR drives. Needed to
    // build its "combined path": fly to a landing point a fixed distance short of
    // the target, then walk the last stretch on the ground.
    // NOTE: Nav.Pathfind returns a TASK — pathfinding is async inside vnavmesh.
    // Declaring it as List<Vector3> makes Dalamud try to serialize the Task and
    // throw ("Self referencing loop detected for property 'Task'").
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>? _pathfind;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object>?          _pathMoveTo;
    private readonly ICallGateSubscriber<List<Vector3>>?                        _listWaypoints;

    public NavmeshIPC(IDalamudPluginInterface pi)
    {
        _isReady               = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _buildProgress         = pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        _isRunning             = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _stop                  = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _pathfindAndMoveCloseTo= pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _pathfindInProgress    = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        _pointOnFloor          = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        _nearestPoint          = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        _pathfind              = pi.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");
        _pathMoveTo            = pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        _listWaypoints         = pi.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
    }

    // The walkable floor point under/near `p`, or null if the mesh has none.
    public Vector3? PointOnFloor(Vector3 p, float halfExtentXZ = 5f, bool allowUnlandable = false)
    {
        try { return _pointOnFloor?.InvokeFunc(p, allowUnlandable, halfExtentXZ); }
        catch { return null; }
    }

    // The nearest point ON the navmesh to `p`, or null if nothing is in range.
    public Vector3? NearestPoint(Vector3 p, float halfExtentXZ = 5f, float halfExtentY = 5f)
    {
        try { return _nearestPoint?.InvokeFunc(p, halfExtentXZ, halfExtentY); }
        catch { return null; }
    }

    // A destination vnavmesh can actually path to: the node's own position snapped
    // onto the mesh, falling back to the floor under it, then the raw position.
    public Vector3 Reachable(Vector3 p, float extent = 5f)
        => NearestPoint(p, extent, extent) ?? PointOnFloor(p, extent) ?? p;

    // Kicks off a pathfind and hands back the task — vnavmesh computes it on a
    // worker, so we poll the task from the framework tick rather than blocking.
    public Task<List<Vector3>>? PathfindAsync(Vector3 from, Vector3 to, bool fly)
    {
        try { return _pathfind?.InvokeFunc(from, to, fly); }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[DiademGatherer] vnavmesh.Nav.Pathfind failed");
            return null;
        }
    }

    // Follow an explicit waypoint list, flying or on the ground.
    public void MoveAlong(List<Vector3> waypoints, bool fly)
    {
        try { _pathMoveTo?.InvokeAction(waypoints, fly); }
        catch (Exception ex) { Plugin.Log.Error(ex, "[DiademGatherer] vnavmesh.Path.MoveTo failed"); }
    }

    public List<Vector3> Waypoints()
    {
        try { return _listWaypoints?.InvokeFunc() ?? new List<Vector3>(); }
        catch { return new List<Vector3>(); }
    }

    // Live probe: true only if vnavmesh is actually loaded and answering.
    public bool IsAvailable
    {
        get
        {
            try { _isReady!.InvokeFunc(); return true; }
            catch { return false; }
        }
    }

    public bool IsReady()
    {
        try { return _isReady?.InvokeFunc() ?? false; }
        catch { return false; }
    }

    // 0..1 while building; useful for UI feedback.
    public float BuildProgress()
    {
        try { return _buildProgress?.InvokeFunc() ?? -1f; }
        catch { return -1f; }
    }

    public bool MoveCloseTo(Vector3 dest, bool fly = true, float range = 3f)
    {
        try { return _pathfindAndMoveCloseTo?.InvokeFunc(dest, fly, range) ?? false; }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[DiademGatherer] vnavmesh.SimpleMove.PathfindAndMoveCloseTo failed");
            return false;
        }
    }

    public bool IsMoving()
    {
        try { return _isRunning?.InvokeFunc() ?? false; }
        catch { return false; }
    }

    public bool PathfindInProgress()
    {
        try { return _pathfindInProgress?.InvokeFunc() ?? false; }
        catch { return false; }
    }

    // True while vnavmesh is doing ANYTHING for us — computing a path or
    // executing one. "Not busy" right after a move request means the request
    // failed/finished, not that it hasn't started (pathfinding is async).
    public bool IsBusy() => IsMoving() || PathfindInProgress();

    public void Stop()
    {
        try { _stop?.InvokeAction(); }
        catch { /* already stopped or plugin gone */ }
    }

    public void Dispose() { }
}
