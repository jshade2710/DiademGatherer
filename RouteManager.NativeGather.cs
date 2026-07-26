using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using GameObject = Dalamud.Game.ClientState.Objects.Types.IGameObject;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DiademGatherer;

// Native gathering: instead of handing node selection to GBR, the plugin finds
// the real gathering node near each recorded waypoint, interacts with it, and
// works the item slots itself (window layout decoded from GBR's GatheringReader).
// This makes the route exact — we mine the node in front of us, and if there's
// no node within the search radius we simply move on.
public sealed partial class RouteManager
{
    private GameObject? _nativeNode;
    private ulong       _nativeNodeId;
    private DateTime    _gatherStep = DateTime.MinValue;
    private bool        _gatherBuffed;
    private bool        _gatherPending;             // fired a gather, waiting for it to resolve
    private bool        _gatherStarted;             // saw the gather animation actually begin
    private uint        _buffGpBefore;              // GP before a buff cast; >0 = waiting for it to land
    private DateTime    _buffWaitCap  = DateTime.MinValue;
    private bool        _navIssued;                           // a nav order is outstanding for this node
    private Vector3?    _navTarget;                           // node position snapped onto the navmesh
    private bool        _spotLearned;                         // recorded a working spot for this node
    private bool        _forceLanding;                        // recovering from an "in flight" refusal
    private Vector3?    _forceLandSpot;                       // mesh point we're landing on
    private DateTime    _forceLandDeadline = DateTime.MinValue; // bound on the landing recovery
    private DateTime    _landPressAt = DateTime.MinValue;      // spacing on the touch-down press
    private DateTime    _landAttemptSince = DateTime.MinValue; // when we began trying to touch down
    private DateTime    _approachSince = DateTime.MinValue;    // clock for the APPROACH phase only

    // How long to try landing before attempting the interact anyway.
    private static readonly TimeSpan LandAttemptWindow = TimeSpan.FromSeconds(2);

    private bool     _wasInFlight;                        // for spotting the touch-down
    private bool     _wasMounted;
    private DateTime _landedAt = DateTime.MinValue;       // when we last touched down

    // Let the landing animation play out before interacting. Without this the
    // game refuses with "Unable to execute command while jumping" and we burn a
    // retry — it was the single most frequent friction in a 2-hour run.
    private static readonly TimeSpan LandingSettle = TimeSpan.FromMilliseconds(600);

    // The chain's next node is up the INSTANT the previous one is exhausted, so by
    // the time we've flown here it already exists — this is only a grace for the
    // object streaming in, not a wait for a spawn. If it isn't here, the chain was
    // broken upstream (we left a node with integrity on it), and nothing is coming.
    private static readonly TimeSpan NodeSpawnWait   = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ChainBrokenWait = TimeSpan.FromSeconds(2);
    private bool _chainIntact = true;   // did we fully exhaust the previous node?

    // GP jump this big mid-node means a proc refunded it (Revisit gives ~997).
    private const uint GpProcThreshold = 250;
    private uint _lastGp;

    // How far to back off before re-approaching after an "in flight" refusal.
    private const float RetreatDistance = 12f;
    private int         _groundLegCount;                      // waypoints of the combined path that are on foot
    private readonly CombinedPathBuilder _pathBuilder = new();
    private int         _navCandidate;                        // which approach target we're on
    private float       _navBestH  = float.MaxValue;          // closest we've got with this target
    private DateTime    _navStallSince = DateTime.MinValue;   // when we stopped making progress

    // No progress for this long → the current approach target can't finish; try
    // the next one rather than sitting until the whole-approach timeout. Once the
    // path has actually FINISHED there's nothing left to wait for, so that case
    // gets a much shorter fuse.
    private static readonly TimeSpan ApproachStallWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ArrivedStallWindow  = TimeSpan.FromSeconds(1);
    private const int MaxApproachCandidates = 3;

    // Where to AIM when we have no learned spot: this far from the node, at a
    // random bearing. Distinct from ArrivalTolerance below, which is how near that
    // aim point counts as arrived — the two add up, so keep
    // NodeAimOffset + ArrivalTolerance comfortably under InteractHRange.
    private const float NodeAimOffset = 2.2f;
    private const int   MaxLearnedSpotsPerNode = 6;

    // Only learn a spot this close to the node, so that spot + arrival tolerance
    // still lands inside InteractHRange on the next visit.
    private const float RecordMaxSeparation = 2.4f;

    // A learned spot further than this from its node is stale (node moved to
    // another spawn point) — ignore it and re-learn.
    private const float LearnedSpotMaxRange = 12f;
    private DateTime    _approachLogAt = DateTime.MinValue;   // throttles the approach heartbeat log
    private DateTime    _navRetryAt    = DateTime.MinValue;   // spacing on re-issuing a nav order
    private DateTime    _gatherCap     = DateTime.MinValue;   // fallback if a swing never registers

    // GBR's gate for "the client is ready for the next action". Using this rather
    // than a fixed delay is what keeps its gather cadence at the animation floor.
    private static bool CanAct
        => !Plugin.Condition[ConditionFlag.BetweenAreas]
        && !Plugin.Condition[ConditionFlag.Casting]
        && !Plugin.Condition[ConditionFlag.ExecutingGatheringAction];

    // GBR's node-interaction envelope: a node opens with less than 3.5 yalms of
    // horizontal and 3 yalms of vertical separation. Judged separately, so being
    // "close" while hovering overhead correctly counts as not-there-yet.
    private const float InteractHRange = 3.5f;
    private const float InteractVRange = 3f;

    // How far short of the target the combined path lands, then walks in. This is
    // GBR's AutoGatherConfig.LandingDistance default — 6, NOT the 15 of
    // MountUpDistance. Too large and the ground leg becomes a long walk that
    // detours around anything near the node (the "loop around the node").
    private const float LandingDistance = 6f;
    private const float ArrivalTolerance = 2f;   // vnavmesh "close enough" radius

    // Whole-approach bound (fly in + land + open). Generous on purpose: giving up
    // doesn't cost one node, it costs every node behind it in the chain, so it's
    // worth spending the extra seconds to avoid a break.
    private static readonly TimeSpan NativeApproachTimeout = TimeSpan.FromSeconds(45);


    // The gathering node nearest `near` that's still up (targetable), within
    // `radius`; null if none — the node isn't spawned at this waypoint now.
    private static GameObject? FindGatheringNode(Vector3 near, float radius)
    {
        GameObject? best = null;
        var bestDist = radius;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.GatheringPoint) continue;
            if (!obj.IsTargetable) continue; // depleted nodes stop being targetable
            var d = Vector3.Distance(near, obj.Position);
            if (d < bestDist) { best = obj; bestDist = d; }
        }
        return best;
    }

    // ── Learned approach spots ─────────────────────────────────────────────────
    // Our stand-in for GBR's node-offset table: instead of shipping known offsets,
    // we learn them from spots that actually opened a node on this character.

    // Pick one of the spots we've opened this node from, at RANDOM — same idea as
    // GBR's TryGetRandomOffset. Standing in the exact same place every lap is the
    // tell that gives a bot away. Spots that no longer sit near the node (it moved
    // to another spawn point) are ignored.
    private bool TryGetLearnedSpot(Vector3 nodePos, out Vector3 spot)
    {
        spot = default;
        if (!_config.LearnedNodeSpotSets.TryGetValue(Configuration.NodeSpotKey(nodePos), out var saved)
            || saved.Count == 0)
            return false;

        var usable = saved.Select(s => s.ToVector3())
                          .Where(s => Vector3.Distance(s, nodePos) <= LearnedSpotMaxRange)
                          .ToList();
        if (usable.Count == 0) return false;

        spot = usable[Random.Shared.Next(usable.Count)];
        return true;
    }

    private void RememberNodeSpot(Vector3 nodePos)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position;
        if (me == null) return;

        // Only remember spots comfortably INSIDE the interact envelope. A node can
        // open from up to InteractHRange away, so recording wherever it happened to
        // work can store a spot right on the edge — then next visit we arrive
        // within ArrivalTolerance of it and end up just outside range, stall, and
        // have to re-path. Keeping spot + tolerance under the envelope avoids that.
        var hSep = Vector2.Distance(new Vector2(nodePos.X, nodePos.Z), new Vector2(me.Value.X, me.Value.Z));
        if (hSep > RecordMaxSeparation) return;

        var key = Configuration.NodeSpotKey(nodePos);
        if (!_config.LearnedNodeSpotSets.TryGetValue(key, out var spots))
            _config.LearnedNodeSpotSets[key] = spots = new List<SavedVector3>();

        // Only keep spots that are meaningfully different from ones we already
        // have, so the set stays a spread of positions rather than a cluster.
        if (spots.Any(s => Vector3.Distance(s.ToVector3(), me.Value) < 1.5f)) return;

        spots.Add(new SavedVector3(me.Value));
        if (spots.Count > MaxLearnedSpotsPerNode) spots.RemoveAt(0);
        _config.Save();
        Plugin.Log.Debug($"[DiademGatherer] Learned approach spot #{spots.Count} for {CurrentLabel} " +
                         $"@ {new SavedVector3(me.Value)}");
    }

    // Approach targets, most-trusted first. We fall through to the next when the
    // current one stops closing the gap:
    //   0 — a spot we've actually opened this node from before
    //   1 — the node itself, snapped onto the navmesh (handles nodes whose raw
    //       position isn't walkable, e.g. R4 sitting in a pit)
    //   2 — the node's raw position (short hops where the snap over-corrects)
    //   3 — the route's recorded waypoint, as a last resort staging spot
    private Vector3 PickApproachTarget(int index, Vector3 nodePos, Vector3 wpPos)
    {
        if (index == 0 && TryGetLearnedSpot(nodePos, out var learned))
        {
            Plugin.Log.Debug($"[DiademGatherer] {CurrentLabel}: approach via learned spot {learned}");
            return learned;
        }
        if (index <= 1)
        {
            // Aim just off the node at a random bearing rather than dead-centre on
            // it: still well inside interact range, but we don't land on the exact
            // same pixel every lap (which is what looks automated).
            var ang   = Random.Shared.NextSingle() * MathF.Tau;
            var probe = nodePos + new Vector3(MathF.Cos(ang) * NodeAimOffset, 0f, MathF.Sin(ang) * NodeAimOffset);
            var snapped = _nav.Reachable(probe);
            Plugin.Log.Debug($"[DiademGatherer] {CurrentLabel}: approach via mesh point {snapped} (node {nodePos})");
            return snapped;
        }
        if (index == 2)
        {
            Plugin.Log.Debug($"[DiademGatherer] {CurrentLabel}: approach via raw node position {nodePos}");
            return nodePos;
        }
        Plugin.Log.Debug($"[DiademGatherer] {CurrentLabel}: approach via recorded waypoint {wpPos}");
        return wpPos;
    }

    // ── Interaction error recovery (GBR's HandleNodeInteractionErrorToast) ─────
    // The game refuses interaction with a toast. GBR's reading of these:
    //   "…while in flight"  → we're too high, or not over traversable ground.
    //                         Retrying is pointless — force a landing instead.
    //   "…while jumping"    → we're already dismounted, just retry shortly.
    internal void OnErrorToast(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref bool isHandled)
    {
        if (!_running || _state != BotState.GatheringNode) return;

        var text = message.TextValue;
        if (text.Contains("in flight", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Information($"[DiademGatherer] {CurrentLabel}: '{text}' — forcing a landing.");
            _forceLanding      = true;
            _forceLandSpot     = null;
            _forceLandDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            _nav.Stop();
            _navIssued  = false;
            _gatherStep = DateTime.MinValue;
        }
        else if (text.Contains("jumping", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Debug($"[DiademGatherer] {CurrentLabel}: '{text}' — retrying shortly.");
            _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
        }
    }

    // GBR's ForceLandAndDismount: fly to the nearest mesh point to the PLAYER
    // (not the node), then dismount there — the reliable way down when we're
    // hovering somewhere the game won't let us interact from.
    private void DriveForceLand()
    {
        if (DateTime.UtcNow < _gatherStep) return;

        if (!Plugin.Condition[ConditionFlag.Mounted])
        {
            _forceLanding = false;           // on foot — that's all we needed
            _navIssued    = false;           // re-path from where we actually are
            _gatherStep   = DateTime.UtcNow + TimeSpan.FromMilliseconds(150);
            return;
        }

        // Never let the recovery itself become the stall: give up and let the
        // normal approach retry rather than grinding here.
        if (DateTime.UtcNow > _forceLandDeadline)
        {
            Plugin.Log.Information($"[DiademGatherer] {CurrentLabel}: force-land timed out; resuming approach.");
            _forceLanding = false;
            _navIssued    = false;
            _navTarget    = null;
            return;
        }

        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return;

        // Trying to dismount in place doesn't work when we're hovering on top of a
        // node (nowhere to touch down) — that's why 24 of 29 force-lands timed
        // out. Instead BACK OFF from the node, then hand control back to the normal
        // approach: its combined path ends in a ground leg, and that mechanism has
        // landed us successfully every single time it ran.
        if (_forceLandSpot == null)
        {
            var origin = _nativeNode?.Position ?? me.Position;
            var away   = me.Position - origin;
            away.Y = 0f;
            var dir = away.LengthSquared() > 0.25f
                ? Vector3.Normalize(away)
                : new Vector3(MathF.Cos(Random.Shared.NextSingle() * MathF.Tau), 0f,
                              MathF.Sin(Random.Shared.NextSingle() * MathF.Tau));

            _forceLandSpot = _nav.Reachable(origin + dir * RetreatDistance);
            Plugin.Log.Information($"[DiademGatherer] {CurrentLabel}: backing off to {_forceLandSpot} to re-approach.");
            _nav.MoveCloseTo(_forceLandSpot.Value, fly: true, range: 2f);
            _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
            return;
        }

        if (_nav.IsBusy()) { _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(250); return; }

        // Far enough out — let the approach rebuild a combined path, which will
        // land us on the way in.
        _forceLanding = false;
        _navIssued    = false;
        _navTarget    = null;
        _pathBuilder.Reset();
        _gatherStep   = DateTime.UtcNow + TimeSpan.FromMilliseconds(150);
    }

    // Issue the approach move the way GBR does: prefer a COMBINED path — fly to a
    // landing point `LandingDistance` short of the target, then walk the last
    // stretch — and only fall back to a plain move if one can't be built. This is
    // what stops us either hovering over the target or taking a surface detour
    // around terrain, which were the two failure modes we kept flip-flopping
    // between when I picked fly-or-walk for the whole leg.
    // Returns true once a move has actually been issued. Pathfinding is async, so
    // this may report false for a few frames while the combined path is built.
    private bool IssueApproachMove(Vector3 from, Vector3 to, bool mounted)
    {
        if (mounted)
        {
            if (_pathBuilder.IsIdle)
                _pathBuilder.Start(_nav, from, to, LandingDistance,
                                   Plugin.Condition[ConditionFlag.InFlight]);

            _pathBuilder.Step(_nav, from);
            if (_pathBuilder.IsWorking) return false; // still computing

            if (_pathBuilder.IsDone)
            {
                // No Stop() first — MoveTo replaces the running path, and stopping
                // beforehand puts a visible hitch in the middle of every approach.
                _nav.MoveAlong(_pathBuilder.Path, fly: true);
                _groundLegCount = _pathBuilder.GroundLegCount;
                Plugin.Log.Information($"[DiademGatherer] {CurrentLabel}: combined path, " +
                                 $"{_pathBuilder.Path.Count} waypoints ({_groundLegCount} on the ground).");
                _pathBuilder.Reset();
                return true;
            }

            Plugin.Log.Information($"[DiademGatherer] {CurrentLabel}: no combined path — direct move.");
            _pathBuilder.Reset();
        }

        _groundLegCount = 0;
        _nav.MoveCloseTo(to, fly: mounted, range: _navCandidate <= 1 ? 1f : ArrivalTolerance);
        return true;
    }

    private bool NativeNodeValid()
    {
        if (_nativeNodeId == 0) return false;
        var o = Plugin.ObjectTable.SearchById(_nativeNodeId);
        return o != null && o.IsTargetable;
    }

    // Approach + open the node, then work it. One waypoint = one node.
    private void HandleGatheringNode()
    {
        if (GameUiHelper.GatheringOpen())
        {
            // The node opened, so wherever we're standing right now is a spot that
            // demonstrably works — remember it and approach from there next time.
            if (!_spotLearned && _nativeNode != null)
            {
                _spotLearned = true;
                RememberNodeSpot(_nativeNode.Position);
            }

            // Time spent GATHERING is not time spent approaching. A burst node —
            // especially one a Revisit proc has topped up with extra integrity —
            // easily runs past the approach timeout, and letting that clock run
            // made us throw the node away the moment we went to re-open it.
            _approachSince = DateTime.UtcNow;

            DriveNativeGather();
            return;
        }
        // Recovering from an "in flight" refusal takes priority over approaching.
        if (_forceLanding) { DriveForceLand(); return; }

        if (DateTime.UtcNow < _gatherStep) return;

        if (!TryGetCurrentPosition(out var wpPos, out _)) { AdvanceNode(); return; }

        // Find the real node near this waypoint.
        if (_nativeNode == null || !NativeNodeValid())
        {
            _nativeNode = FindGatheringNode(wpPos, _config.NodeSearchRadius);
            if (_nativeNode == null)
            {
                // The Diadem's chains are SEQUENTIAL, and the next node only spawns
                // once the previous one is fully EXHAUSTED. So if we exhausted the
                // last node, this one is coming — wait for it, because skipping
                // doesn't cost one node, it kills the rest of the chain (why R1–R8
                // each failed 5-6 times a run while B nodes failed once). If we
                // know we left the last node early, the chain is already broken and
                // waiting here is just dead time.
                var spawnWait = _chainIntact ? NodeSpawnWait : ChainBrokenWait;
                if (DateTime.UtcNow - _approachSince > spawnWait)
                    NoGatherHere($"no node within {_config.NodeSearchRadius}y");
                else
                    _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
                return;
            }
            _nativeNodeId    = _nativeNode.GameObjectId;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        // Hard bound on getting onto this node — covers a stubborn landing spot
        // that won't take a dismount, not just an unreachable node. Only the
        // approach hits this; once the window is open we return above.
        if (DateTime.UtcNow - _approachSince > NativeApproachTimeout)
        {
            _nav.Stop();
            NoGatherHere("couldn't get onto the node");
            return;
        }

        // GBR's MoveToCloseNode, verbatim in shape: judge HORIZONTAL and VERTICAL
        // separation separately, and never dismount by hand — interacting with a
        // node dismounts you automatically, which is exactly why GBR never hits
        // "Unable to execute command while jumping".
        // Note the moment we touch down or come off the mount. Interacting while
        // the landing animation is still playing is refused ("while jumping") —
        // that one race produced hundreds of wasted retries per run.
        var inFlightNow = Plugin.Condition[ConditionFlag.InFlight];
        var mountedNow  = Plugin.Condition[ConditionFlag.Mounted];
        if ((_wasInFlight && !inFlightNow) || (_wasMounted && !mountedNow))
            _landedAt = DateTime.UtcNow;
        _wasInFlight = inFlightNow;
        _wasMounted  = mountedNow;

        var nodePos = _nativeNode.Position;
        var hSep = Vector2.Distance(
            new Vector2(nodePos.X, nodePos.Z), new Vector2(player.Position.X, player.Position.Z));
        var vSep = MathF.Abs(nodePos.Y - player.Position.Y);

        // Heartbeat so a stall is never silent in the log again.
        if (DateTime.UtcNow >= _approachLogAt)
        {
            Plugin.Log.Information($"[DiademGatherer] {CurrentLabel} approach: h={hSep:F1} v={vSep:F1} " +
                             $"mounted={Plugin.Condition[ConditionFlag.Mounted]} " +
                             $"inFlight={Plugin.Condition[ConditionFlag.InFlight]} navBusy={_nav.IsBusy()}");
            _approachLogAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        }

        // Not in the node's envelope yet → navigate to it. Issue the order once
        // and let vnavmesh run it (re-deciding every tick made us oscillate).
        if (hSep >= InteractHRange || vSep >= InteractVRange)
        {
            // Path to the node's position SNAPPED ONTO THE MESH, which is what
            // GBR does. A node's raw position often isn't a walkable point — R4
            // sits in a pit — and pathing to a non-mesh point makes vnavmesh route
            // to whatever it can reach instead, i.e. the long way round the rim.
            // Approach spots are tried in order of trust, and we FALL THROUGH to
            // the next one when the current target stops getting us closer. A
            // recorded waypoint or learned spot is only a staging position — the
            // node can spawn several yalms off it (B1), so arriving there is not
            // the same as being able to interact.
            if (_navTarget == null)
            {
                _navTarget = PickApproachTarget(_navCandidate, nodePos, wpPos);
                _navBestH  = float.MaxValue;
                _navStallSince = DateTime.UtcNow;
            }

            // Progress check. Improving resets the clock. If vnavmesh has FINISHED
            // its path and we're still out of range, this target simply can't get
            // us there — move on almost immediately rather than hovering out the
            // full stall window (that was the long wait in the air at R2).
            var pathDone = _navIssued && !_nav.IsBusy();
            var window   = pathDone ? ArrivedStallWindow : ApproachStallWindow;

            if (hSep < _navBestH - 0.5f)
            {
                _navBestH = hSep;
                _navStallSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _navStallSince > window
                     && _navCandidate < MaxApproachCandidates)
            {
                _navCandidate++;
                _navTarget = null;
                _navIssued = false;
                _nav.Stop();
                Plugin.Log.Information($"[DiademGatherer] {CurrentLabel}: stalled at h={hSep:F1} — " +
                                 $"trying approach target #{_navCandidate}.");
                return;
            }

            var mounted = Plugin.Condition[ConditionFlag.Mounted];

            if (!_navIssued || (!_nav.IsBusy() && DateTime.UtcNow >= _navRetryAt))
            {
                if (IssueApproachMove(player.Position, _navTarget.Value, mounted))
                {
                    _navIssued  = true;
                    _navRetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
                else
                {
                    // Path still being computed — check back next frame.
                    _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(80);
                    return;
                }
            }

            // Once the ground leg of a combined path begins, land — GBR switches
            // vnavmesh to no-fly and presses dismount at exactly this point, so we
            // walk the final stretch on foot instead of hovering to the target.
            if (_groundLegCount > 0 && Plugin.Condition[ConditionFlag.InFlight]
                && _nav.Waypoints().Count <= _groundLegCount
                && DateTime.UtcNow >= _landPressAt)
            {
                SkillCaster.ForceDismount();
                _landPressAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }

            _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
            return;
        }

        // Within both ranges. Interacting while IN FLIGHT gets refused, so give a
        // touch-down a brief chance first — but only briefly. If the spot won't
        // take a landing we must still ATTEMPT the interact: the refusal toast is
        // what triggers ForceLandAndDismount, and blocking the attempt meant we
        // hovered here silently until the approach timed out (R7).
        if (Plugin.Condition[ConditionFlag.InFlight])
        {
            if (_landAttemptSince == DateTime.MinValue) _landAttemptSince = DateTime.UtcNow;

            if (DateTime.UtcNow - _landAttemptSince < LandAttemptWindow)
            {
                if (_nav.IsBusy()) { _nav.Stop(); _navIssued = false; }
                if (DateTime.UtcNow >= _landPressAt)
                {
                    SkillCaster.ForceDismount();
                    _landPressAt = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
                }
                _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(150);
                return;
            }
            // Landing isn't happening here — fall through and let the game tell us
            // why, so the proper recovery can run.
        }

        // Just touched down? Let the landing animation finish before interacting,
        // or the game refuses with "while jumping" and we burn a 400 ms retry.
        if (DateTime.UtcNow - _landedAt < LandingSettle)
        {
            _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(80);
            return;
        }

        // Grounded. GBR deliberately waits a frame between stopping movement and
        // interacting "to avoid the 'Unable to execute command while in flight'
        // error" — issuing both in one tick is what produced our error spam.
        if (_navIssued || _nav.IsBusy())
        {
            _nav.Stop();
                _navIssued = false;
            _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(60); // settle one frame
            return;
        }

        GameUiHelper.OpenNodeInteraction(_nativeNode.Address);
        Plugin.Log.Information($"[DiademGatherer] {CurrentLabel}: interacting (h={hSep:F1} v={vSep:F1} " +
                         $"mounted={Plugin.Condition[ConditionFlag.Mounted]} " +
                         $"inFlight={Plugin.Condition[ConditionFlag.InFlight]})");
        // GBR allows up to 1100 ms for the window to appear (≈600 ms for Mounted
        // to fade plus ≈500 ms more for Gathering to show).
        _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(1100);
    }

    // Node window is open: buff, then gather a slot, until the node is spent.
    private void DriveNativeGather()
    {
        var integrity = GameUiHelper.GatheringIntegrity();

        // Waiting for the last gather to finish? The game clears
        // ExecutingGatheringAction the instant the swing completes — fire the next
        // right then instead of sitting on a fixed pad.
        if (_gatherPending)
        {
            var executing = Plugin.Condition[ConditionFlag.ExecutingGatheringAction];
            if (executing)
            {
                // A swing is genuinely in progress — wait it out no matter how
                // long. A Revisit proc restores GP + integrity and stretches the
                // swing well past the normal ~1.8s; bailing on the cap here read
                // the node mid-gather and desynced everything.
                _gatherStarted = true;
                return;
            }
            // Not swinging now: proceed if the swing finished, or (fallback) if it
            // never even started because the click was swallowed. NOTE the cap is
            // its own field — it must never gate the steps below, or every swing
            // waits the full fallback instead of the ~1.8s the animation takes.
            if (_gatherStarted || DateTime.UtcNow >= _gatherCap)
            {
                _gatherPending = false;
                _gatherStep    = DateTime.MinValue; // don't hold up the next swing
            }
            else return;
        }

        // GBR gates each action on "the client can act again" rather than a timer
        // (its configured delay is 0 ms) — that's what keeps its cadence tight.
        if (!CanAct) return;

        var slots = GameUiHelper.GatheringItemSlots();
        if (integrity <= 0 || slots.Count == 0) { FinishNativeNode(); return; }

        // Gather the item the list wants at this node — nothing else. Pick the
        // wanted slot present and keep taking it until the node is spent; never a
        // stray slot (crystals, off-list drops), never spread across items.
        int pick = -1;
        foreach (var s in slots)
        {
            if (_route!.WantedItemIds.Contains(GameUiHelper.GatheringSlotItemId(s))) { pick = s; break; }
        }
        if (pick < 0)
        {
            // Every node on this route carries the listed items, so this should
            // never happen — if it does we've opened something that isn't our chain
            // node. Log what's actually in it so it can be diagnosed, but still
            // exhaust it: the chain's successor only appears once this node is
            // depleted, and leaving integrity behind forfeits everything after it.
            var ids = string.Join(",", slots.Select(GameUiHelper.GatheringSlotItemId));
            Plugin.Log.Warning($"[DiademGatherer] {CurrentLabel}: node has none of the route's items " +
                               $"(slots: {ids}) — exhausting it anyway to keep the chain alive.");
            pick = slots[0];
        }

        var burst = _route!.BurstNodes.Contains(CurrentLabel);

        // A Revisit proc hands back ~997 GP mid-node. That's free GP and it should
        // go straight back into skills — especially on the big R8/B8 nodes, where
        // the extra integrity it grants is exactly what we want buffed. Spotting
        // the GP jump re-arms the whole buff plan for this node.
        var gpHere = Plugin.ObjectTable.LocalPlayer?.CurrentGp ?? 0;
        if (_lastGp > 0 && gpHere > _lastGp + GpProcThreshold)
        {
            Plugin.Log.Debug($"[DiademGatherer] {CurrentLabel}: GP proc (+{gpHere - _lastGp}) — re-casting buffs.");
            _castThisNode.Clear();
            _precastDone  = false;   // burst nodes run the whole priority again
            _gatherBuffed = false;   // normal nodes get Bountiful again
            _nextCastAt   = DateTime.MinValue;
        }
        _lastGp = gpHere;

        // Burst nodes: pre-cast the whole priority list before the first gather.
        if (burst && !_precastDone)
        {
            if (DateTime.UtcNow < _gatherStep) return;
            CastNodeBuffs();
            var allCast = _skills.BurstList.All(_castThisNode.Contains);
            var stalled = DateTime.UtcNow - _lastBuffCastAt > BuffStallWindow;
            if (allCast || stalled) _precastDone = true;
            _gatherStep = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            return;
        }

        // Normal nodes: cast Bountiful before each gather (re-applied per attempt
        // when GP allows).
        if (!burst && !_gatherBuffed)
        {
            if (DateTime.UtcNow < _gatherStep) return;
            var gpBefore = Plugin.ObjectTable.LocalPlayer?.CurrentGp ?? 0;
            _gatherBuffed = true;
            if (CastNodeBuffs())
            {
                // A buff actually fired — don't gather until it lands (the game
                // confirms that by spending the GP). Poll for the drop instead of
                // guessing a delay; the cap is only a fallback if GP never moves.
                _buffGpBefore = gpBefore;
                _buffWaitCap  = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
                _gatherStep   = DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
                return;
            }
            // Nothing to cast (GP too low) → gather this instant, no pause.
        }

        // Buff cast but not yet confirmed by the server → hold until GP is spent.
        if (_buffGpBefore > 0)
        {
            var gpNow = Plugin.ObjectTable.LocalPlayer?.CurrentGp ?? 0;
            if (gpNow < _buffGpBefore || DateTime.UtcNow >= _buffWaitCap)
                _buffGpBefore = 0; // buff is up (or we gave up) → gather now
            else
                return;
        }

        GameUiHelper.GatherSlot(pick);
        _gatherBuffed = false;
        if (!burst && _skills.BountifulSkill != null)
            _castThisNode.Remove(_skills.BountifulSkill); // let Bountiful re-cast next attempt
        _gatherPending = true;                                      // wait for the swing to finish
        _gatherStarted = false;
        _gatherStep    = DateTime.MinValue;                         // no artificial pad
        _gatherCap     = DateTime.UtcNow + TimeSpan.FromSeconds(3); // fallback only
    }

    // Node spent → count it, then auger (if charged) or advance the route.
    private void FinishNativeNode()
    {
        _chainIntact     = true;   // exhausted, so the chain's next node will spawn
        _emptyNodeStreak = 0;
        _lastGatherAt    = DateTime.UtcNow;
        _gathersThisInstance++;

        // Only cancel the window when we're leaving it EARLY with gathers still
        // left (e.g. no wanted item). When the node depleted (integrity 0) the
        // game closes the window itself — firing our own close then races that
        // and can leave the Gathering condition stuck on, which blocks mounting
        // for up to a minute afterwards.
        if (GameUiHelper.GatheringOpen() && GameUiHelper.GatheringIntegrity() > 0)
            GameUiHelper.CloseAddon("Gathering");

        _nativeNode           = null;
        _nativeNodeId         = 0;
        _gatherBuffed         = false;
        _gatherPending        = false;
        _lastGp               = 0;
        _gatherStarted        = false;
        _buffGpBefore         = 0;
        _navIssued            = false;
        _navTarget            = null;
        _spotLearned          = false;
        _navCandidate         = 0;
        _forceLanding         = false;
        _forceLandSpot        = null;
        _landPressAt          = DateTime.MinValue;
        _landAttemptSince     = DateTime.MinValue;
        _approachSince        = DateTime.UtcNow;
        _landedAt             = DateTime.MinValue;
        _wasInFlight          = false;
        _wasMounted           = false;
        _groundLegCount       = 0;
        _pathBuilder.Reset();
        _navBestH             = float.MaxValue;
        _navStallSince        = DateTime.MinValue;
        _navRetryAt           = DateTime.MinValue;
        _gatherCap            = DateTime.MinValue;

        if (_config.UseAuger && GameUiHelper.AugerGaugeValue() >= DiademData.AugerGaugeReady)
        {
            _augerAttempts = 0;
            _augerNextTry  = DateTime.MinValue;
            SetState(BotState.AugerPhase);
            return;
        }

        FinishWaypoint();
    }
}
