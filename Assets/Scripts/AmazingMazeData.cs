using System;
using System.Collections.Generic;
using UnityEngine;

public class AmazingMazeData
{
    public const int DIR_LEFT  = 0; // West  (-X / col - 1)
    public const int DIR_DOWN  = 1; // South (-Z / row + 1)
    public const int DIR_RIGHT = 2; // East  (+X / col + 1)
    public const int DIR_UP    = 3; // North (+Z / row - 1)

    public const int LAYER_WIDE_BASE     = 1;
    public const int LAYER_WIDE_BRIDGE   = 2;
    public const int LAYER_NARROW_BASE   = 3;
    public const int LAYER_NARROW_BRIDGE = 4;

    [Serializable]
    public struct SkillConfig
    {
        public int rows, cols;
        public bool allowWideBridges, allowNarrowPlane, allowNarrowBridges;
        public int numWalks;
        public float rampChance, bridgeChance;
        public string label;
    }

    public static readonly SkillConfig[] SKILL_CONFIGS = new SkillConfig[]
    {
        new SkillConfig { rows = 19, cols = 31, allowWideBridges = false, allowNarrowPlane = false, allowNarrowBridges = false, numWalks = 55,  rampChance = 0f,    bridgeChance = 0f,    label = "Level 1 (Easy): Wide paths only" },
        new SkillConfig { rows = 19, cols = 31, allowWideBridges = true,  allowNarrowPlane = false, allowNarrowBridges = false, numWalks = 150, rampChance = 0f,    bridgeChance = 0.65f, label = "Level 2: Wide paths + 90 deg tunnels" },
        new SkillConfig { rows = 20, cols = 32, allowWideBridges = true,  allowNarrowPlane = true,  allowNarrowBridges = true,  numWalks = 340, rampChance = 0.16f, bridgeChance = 0.70f, label = "Level 3: Upper Narrow Walkways + Tunnels" },
        new SkillConfig { rows = 20, cols = 32, allowWideBridges = true,  allowNarrowPlane = true,  allowNarrowBridges = true,  numWalks = 520, rampChance = 0.55f, bridgeChance = 0.85f, label = "Level 4 (Hard): Max Tunnels & Ramps" }
    };

    public struct NodePos : IEquatable<NodePos>
    {
        public int r, c, layer;
        public NodePos(int r, int c, int layer) { this.r = r; this.c = c; this.layer = layer; }
        public bool Equals(NodePos other) => r == other.r && c == other.c && layer == other.layer;
    }

    public struct StepInfo
    {
        public int r, c, layer, entryLayer;
    }

    public int SkillLevel { get; private set; } = 0;
    public int MaxRow { get; private set; } = 19;
    public int MaxCol { get; private set; } = 31;

    // gMaze[r, c, subLayer] and gNibMask[r, c, subLayer]
    public byte[,,] Maze { get; private set; }
    public byte[,,] NibMask { get; private set; }

    public NodePos StartPos { get; private set; }
    public NodePos EndPos { get; private set; }
    public NodePos PlayerPos { get; private set; }
    public List<NodePos> PathHistory { get; private set; } = new List<NodePos>();
    public bool GameWon { get; private set; }

    private System.Random rng = new System.Random();

    public void AllocateGrid(int rows, int cols)
    {
        MaxRow = rows;
        MaxCol = cols;
        Maze = new byte[rows + 2, cols + 2, 5];
        NibMask = new byte[rows + 2, cols + 2, 5];
    }

    public byte GetSquare(int r, int c, int layer)
    {
        if (r < 0 || r > MaxRow + 1 || c < 0 || c > MaxCol + 1) return 0x0F;
        return Maze[r, c, layer];
    }

    public void PutSquare(int r, int c, int layer, int val)
    {
        if (r >= 0 && r <= MaxRow + 1 && c >= 0 && c <= MaxCol + 1)
            Maze[r, c, layer] = (byte)(val & 0xFF);
    }

    public void MarkSquare(int r, int c, int layer, int bitIndex)
    {
        PutSquare(r, c, layer, GetSquare(r, c, layer) | (1 << bitIndex));
    }

    public bool MemberOf(int bitIndex, int r, int c, int layer)
    {
        return (GetSquare(r, c, layer) & (1 << bitIndex)) != 0;
    }

    public static bool IsWideSubLayer(int layer) => layer == LAYER_WIDE_BASE || layer == LAYER_WIDE_BRIDGE;
    public static int BaseLayerOfPlane(int layer) => IsWideSubLayer(layer) ? LAYER_WIDE_BASE : LAYER_NARROW_BASE;
    public static int BridgeLayerOfPlane(int layer) => IsWideSubLayer(layer) ? LAYER_WIDE_BRIDGE : LAYER_NARROW_BRIDGE;
    public static int DirOpp(int dir) => (dir + 2) & 3;

    public static (int r, int c) StepCoord(int r, int c, int dir)
    {
        switch (dir)
        {
            case DIR_LEFT:  return (r, c - 1);
            case DIR_DOWN:  return (r + 1, c);
            case DIR_RIGHT: return (r, c + 1);
            case DIR_UP:    return (r - 1, c);
            default:        return (r, c);
        }
    }

    public bool SquareFree(int r, int c, int layer)
    {
        if (r < 1 || r > MaxRow || c < 1 || c > MaxCol) return false;
        return (GetSquare(r, c, layer) & 0x8F) == 0;
    }

    public bool IsRamp(int r, int c)
    {
        if (r < 1 || r > MaxRow || c < 1 || c > MaxCol) return false;
        return MemberOf(7, r, c, LAYER_WIDE_BASE) || MemberOf(7, r, c, LAYER_NARROW_BASE);
    }

    public bool IsBridge(int r, int c, int baseLayer, int moveDir)
    {
        if (r <= 1 || r >= MaxRow || c <= 1 || c >= MaxCol) return false;
        if (IsRamp(r, c)) return false;
        int bridgeLayer = BridgeLayerOfPlane(baseLayer);
        if (!SquareFree(r, c, bridgeLayer)) return false;

        int sq = GetSquare(r, c, baseLayer) & 0x0F;
        if (moveDir == DIR_LEFT || moveDir == DIR_RIGHT)
            return sq == ((1 << DIR_UP) | (1 << DIR_DOWN));
        else
            return sq == ((1 << DIR_LEFT) | (1 << DIR_RIGHT));
    }

    private class GenMove
    {
        public string type;
        public int dir, nr, nc, nl, bridgeLayer;
        public List<(int r, int c)> mids;
        public float priority;
    }

    public void MakeMaze(int skillLevel = 0)
    {
        SkillLevel = Mathf.Clamp(skillLevel, 0, SKILL_CONFIGS.Length - 1);
        SkillConfig cfg = SKILL_CONFIGS[SkillLevel];
        AllocateGrid(cfg.rows, cfg.cols);
        GameWon = false;

        bool IsCellCompletelyEmpty(int r, int c) =>
            r >= 1 && r <= MaxRow && c >= 1 && c <= MaxCol &&
            !IsRamp(r, c) &&
            GetSquare(r, c, LAYER_WIDE_BASE) == 0 &&
            GetSquare(r, c, LAYER_WIDE_BRIDGE) == 0 &&
            GetSquare(r, c, LAYER_NARROW_BASE) == 0 &&
            GetSquare(r, c, LAYER_NARROW_BRIDGE) == 0;

        bool IsPlaneBaseFree(int r, int c, int baseLayer)
        {
            if (r < 1 || r > MaxRow || c < 1 || c > MaxCol || IsRamp(r, c)) return false;
            int bridgeLayer = BridgeLayerOfPlane(baseLayer);
            return GetSquare(r, c, baseLayer) == 0 && GetSquare(r, c, bridgeLayer) == 0;
        }

        GenMove FindSamePlaneBridgeMove(int r, int c, int baseLayer, int dir)
        {
            if (baseLayer == LAYER_WIDE_BASE && !cfg.allowWideBridges) return null;
            if (baseLayer == LAYER_NARROW_BASE && !cfg.allowNarrowBridges) return null;

            int bridgeLayer = BridgeLayerOfPlane(baseLayer);
            var mids = new List<(int r, int c)>();
            (int r, int c) cur = (r, c);

            for (int span = 1; span <= 3; span++)
            {
                var mid = StepCoord(cur.r, cur.c, dir);
                if (!IsBridge(mid.r, mid.c, baseLayer, dir)) break;
                mids.Add(mid);
                var land = StepCoord(mid.r, mid.c, dir);
                if (IsPlaneBaseFree(land.r, land.c, baseLayer))
                {
                    return new GenMove
                    {
                        type = "bridge",
                        dir = dir,
                        mids = new List<(int r, int c)>(mids),
                        nr = land.r,
                        nc = land.c,
                        bridgeLayer = bridgeLayer,
                        nl = baseLayer
                    };
                }
                cur = mid;
            }
            return null;
        }

        List<GenMove> GetAvailableMoves(int r, int c, int l, int lastDir, int stepsOnCurrentPlane)
        {
            int otherPlaneBase = (l == LAYER_WIDE_BASE) ? LAYER_NARROW_BASE : LAYER_WIDE_BASE;
            var moves = new List<GenMove>();
            bool curIsRamp = IsRamp(r, c);
            int rampEntryMask = curIsRamp ? (GetSquare(r, c, otherPlaneBase) & 0x0F) : 0;

            int[] dirs = { 0, 1, 2, 3 };
            for (int i = 0; i < 4; i++)
            {
                int swap = rng.Next(i, 4);
                (dirs[i], dirs[swap]) = (dirs[swap], dirs[i]);
            }

            foreach (int dir in dirs)
            {
                if (curIsRamp && (rampEntryMask & (1 << dir)) != 0) continue;
                var (nr, nc) = StepCoord(r, c, dir);
                if (nr < 1 || nr > MaxRow || nc < 1 || nc > MaxCol) continue;

                if (IsPlaneBaseFree(nr, nc, l))
                {
                    float turnBonus = (dir != lastDir) ? 0.18f : 0f;
                    float weaveBonus = (l == LAYER_NARROW_BASE && GetSquare(nr, nc, LAYER_WIDE_BASE) != 0) ? 0.25f : 0f;
                    moves.Add(new GenMove
                    {
                        type = "flat", dir = dir, nr = nr, nc = nc, nl = l,
                        priority = (float)rng.NextDouble() + turnBonus + weaveBonus
                    });
                }

                var br = FindSamePlaneBridgeMove(r, c, l, dir);
                if (br != null)
                {
                    float bridgeBoost = (rng.NextDouble() < cfg.bridgeChance) ? 1.35f : 0.40f;
                    br.priority = (float)rng.NextDouble() + bridgeBoost;
                    moves.Add(br);
                }

                if (cfg.allowNarrowPlane && !curIsRamp &&
                    nr > 1 && nr < MaxRow && nc > 1 && nc < MaxCol &&
                    IsCellCompletelyEmpty(nr, nc))
                {
                    bool canExitRamp = false;
                    for (int ed = 0; ed < 4; ed++)
                    {
                        if (ed == DirOpp(dir)) continue;
                        if (SkillLevel == 2 && ed != dir) continue;
                        var after = StepCoord(nr, nc, ed);
                        if (IsPlaneBaseFree(after.r, after.c, otherPlaneBase) ||
                            FindSamePlaneBridgeMove(nr, nc, otherPlaneBase, ed) != null)
                        {
                            canExitRamp = true;
                            break;
                        }
                    }

                    if (canExitRamp)
                    {
                        bool shouldRamp = (rng.NextDouble() < cfg.rampChance) ||
                                          (stepsOnCurrentPlane >= 5 && rng.NextDouble() < 0.45);
                        float rampBoost = shouldRamp ? 0.90f : -0.30f;
                        moves.Add(new GenMove
                        {
                            type = "ramp_step", dir = dir, nr = nr, nc = nc, nl = otherPlaneBase,
                            priority = (float)rng.NextDouble() + rampBoost
                        });
                    }
                }
            }
            return moves;
        }

        var activeNodes = new List<NodePos>();
        var allCarvedNodes = new List<NodePos>();

        int seedR = 1, seedC = rng.Next(3, MaxCol - 2);
        var seedNode = new NodePos(seedR, seedC, LAYER_WIDE_BASE);
        activeNodes.Add(seedNode);
        allCarvedNodes.Add(seedNode);

        int maxStepsPerBurst = (SkillLevel == 0) ? 999 : (SkillLevel == 1 ? 22 : 14);

        for (int walkIdx = 0; walkIdx < cfg.numWalks && activeNodes.Count > 0; walkIdx++)
        {
            int pickIdx = rng.Next(activeNodes.Count);
            NodePos origin = activeNodes[pickIdx];
            int r = origin.r, c = origin.c, l = origin.layer;

            if (IsRamp(r, c) &&
                (GetSquare(r, c, LAYER_WIDE_BASE) & 0x0F) != 0 &&
                (GetSquare(r, c, LAYER_NARROW_BASE) & 0x0F) != 0)
            {
                activeNodes.RemoveAt(pickIdx);
                continue;
            }

            int lastDir = -1, stepsOnCurrentPlane = 0, burstSteps = 0;

            while (burstSteps < maxStepsPerBurst || IsRamp(r, c))
            {
                var moves = GetAvailableMoves(r, c, l, lastDir, stepsOnCurrentPlane);
                if (moves.Count == 0)
                {
                    if (IsRamp(r, c) && (GetSquare(r, c, l) & 0x0F) == 0)
                    {
                        PutSquare(r, c, LAYER_WIDE_BASE, GetSquare(r, c, LAYER_WIDE_BASE) & ~0x80);
                        PutSquare(r, c, LAYER_NARROW_BASE, GetSquare(r, c, LAYER_NARROW_BASE) & ~0x80);
                    }
                    activeNodes.RemoveAll(n => n.r == r && n.c == c && n.layer == l);
                    break;
                }

                moves.Sort((a, b) => b.priority.CompareTo(a.priority));
                GenMove pick = moves[0];
                lastDir = pick.dir;
                burstSteps++;

                if (pick.type == "flat")
                {
                    MarkSquare(r, c, l, pick.dir);
                    MarkSquare(pick.nr, pick.nc, pick.nl, DirOpp(pick.dir));
                    r = pick.nr; c = pick.nc; l = pick.nl;
                    stepsOnCurrentPlane++;
                    var node = new NodePos(r, c, l);
                    activeNodes.Add(node);
                    allCarvedNodes.Add(node);
                }
                else if (pick.type == "ramp_step")
                {
                    MarkSquare(r, c, l, pick.dir);
                    MarkSquare(pick.nr, pick.nc, l, DirOpp(pick.dir));
                    MarkSquare(pick.nr, pick.nc, LAYER_WIDE_BASE, 7);
                    MarkSquare(pick.nr, pick.nc, LAYER_NARROW_BASE, 7);
                    r = pick.nr; c = pick.nc; l = pick.nl;
                    stepsOnCurrentPlane = 0;
                }
                else if (pick.type == "bridge")
                {
                    MarkSquare(r, c, l, pick.dir);
                    foreach (var mid in pick.mids)
                    {
                        MarkSquare(mid.r, mid.c, pick.bridgeLayer, DirOpp(pick.dir));
                        MarkSquare(mid.r, mid.c, pick.bridgeLayer, pick.dir);
                    }
                    MarkSquare(pick.nr, pick.nc, pick.nl, DirOpp(pick.dir));
                    r = pick.nr; c = pick.nc; l = pick.nl;
                    stepsOnCurrentPlane += pick.mids.Count + 1;
                    var node = new NodePos(r, c, l);
                    activeNodes.Add(node);
                    allCarvedNodes.Add(node);
                }
            }
        }

        // Select Start (●) and End (■) dead-ends
        var wideDeadEnds = new List<NodePos>();
        for (int r = 1; r <= MaxRow; r++)
        {
            for (int c = 1; c <= MaxCol; c++)
            {
                if (IsRamp(r, c)) continue;
                if ((GetSquare(r, c, LAYER_WIDE_BRIDGE) & 0x0F) != 0) continue;
                if ((GetSquare(r, c, LAYER_NARROW_BASE) & 0x0F) != 0) continue;
                if ((GetSquare(r, c, LAYER_NARROW_BRIDGE) & 0x0F) != 0) continue;
                int mask = GetSquare(r, c, LAYER_WIDE_BASE) & 0x0F;
                if (mask == 1 || mask == 2 || mask == 4 || mask == 8)
                    wideDeadEnds.Add(new NodePos(r, c, LAYER_WIDE_BASE));
            }
        }

        if (wideDeadEnds.Count >= 2)
        {
            StartPos = wideDeadEnds[rng.Next(wideDeadEnds.Count)];
            int bestDist = -1;
            NodePos bestEnd = wideDeadEnds[0];
            foreach (var cand in wideDeadEnds)
            {
                int d = Mathf.Abs(cand.r - StartPos.r) + Mathf.Abs(cand.c - StartPos.c);
                if (d > bestDist) { bestDist = d; bestEnd = cand; }
            }
            EndPos = bestEnd;
        }
        else
        {
            StartPos = seedNode;
            EndPos = allCarvedNodes[allCarvedNodes.Count - 1];
        }

        PlayerPos = StartPos;
        PathHistory.Clear();
        PathHistory.Add(StartPos);
        SyncGridWithPathHistory(4);
    }

    public StepInfo? CanMove(int r, int c, int layer, int dir)
    {
        var (nr, nc) = StepCoord(r, c, dir);
        if (nr < 1 || nr > MaxRow || nc < 1 || nc > MaxCol) return null;
        int oppDir = DirOpp(dir);

        int baseL = BaseLayerOfPlane(layer);
        int otherPlaneBase = (baseL == LAYER_WIDE_BASE) ? LAYER_NARROW_BASE : LAYER_WIDE_BASE;

        int exitSubLayer = 0;
        if (MemberOf(dir, r, c, layer)) exitSubLayer = layer;
        else if (IsRamp(r, c) && MemberOf(dir, r, c, otherPlaneBase)) exitSubLayer = otherPlaneBase;
        else return null;

        int exitPlaneBase = BaseLayerOfPlane(exitSubLayer);
        int exitPlaneBridge = BridgeLayerOfPlane(exitSubLayer);

        if (MemberOf(oppDir, nr, nc, exitPlaneBase))
        {
            int landedLayer = exitPlaneBase;
            if (IsRamp(nr, nc))
            {
                int rampTargetBase = (exitPlaneBase == LAYER_WIDE_BASE) ? LAYER_NARROW_BASE : LAYER_WIDE_BASE;
                if ((GetSquare(nr, nc, rampTargetBase) & 0x0F) != 0)
                    landedLayer = rampTargetBase;
            }
            return new StepInfo { r = nr, c = nc, layer = landedLayer, entryLayer = exitPlaneBase };
        }

        if (!IsRamp(nr, nc) && MemberOf(oppDir, nr, nc, exitPlaneBridge))
        {
            return new StepInfo { r = nr, c = nc, layer = exitPlaneBridge, entryLayer = exitPlaneBridge };
        }

        return null;
    }

    // Call this when the player walks into an adjacent (targetR, targetC) cell in 3D space
    public bool TryStepToAdjacentCell(int targetR, int targetC)
    {
        int dr = targetR - PlayerPos.r;
        int dc = targetC - PlayerPos.c;
        if (Mathf.Abs(dr) + Mathf.Abs(dc) != 1) return false;

        int dir = -1;
        if (dc == -1) dir = DIR_LEFT;
        else if (dr == 1) dir = DIR_DOWN;
        else if (dc == 1) dir = DIR_RIGHT;
        else if (dr == -1) dir = DIR_UP;

        var stepOpt = CanMove(PlayerPos.r, PlayerPos.c, PlayerPos.layer, dir);
        if (!stepOpt.HasValue) return false;
        StepInfo step = stepOpt.Value;

        var nextNode = new NodePos(step.r, step.c, step.layer);

        // Backtracking check: if stepping back into an earlier cell on our path, unwind the breadcrumb!
        int existingIdx = PathHistory.IndexOf(nextNode);
        if (existingIdx != -1)
        {
            PathHistory.RemoveRange(existingIdx + 1, PathHistory.Count - (existingIdx + 1));
            PlayerPos = nextNode;
        }
        else
        {
            PlayerPos = nextNode;
            PathHistory.Add(PlayerPos);
        }

        SyncGridWithPathHistory(4);
        if (PlayerPos.Equals(EndPos)) GameWon = true;
        return true;
    }

    public void SyncGridWithPathHistory(int flagBit = 4)
    {
        for (int r = 1; r <= MaxRow; r++)
            for (int c = 1; c <= MaxCol; c++)
                for (int l = 1; l <= 4; l++)
                {
                    Maze[r, c, l] &= unchecked((byte)~0x30);
                    NibMask[r, c, l] = 0;
                }

        for (int i = 0; i < PathHistory.Count - 1; i++)
        {
            NodePos curr = PathHistory[i];
            NodePos next = PathHistory[i + 1];
            int dir = -1;
            if (next.c == curr.c - 1) dir = DIR_LEFT;
            else if (next.r == curr.r + 1) dir = DIR_DOWN;
            else if (next.c == curr.c + 1) dir = DIR_RIGHT;
            else if (next.r == curr.r - 1) dir = DIR_UP;

            if (dir != -1)
            {
                var stepOpt = CanMove(curr.r, curr.c, curr.layer, dir);
                if (stepOpt.HasValue)
                {
                    StepInfo step = stepOpt.Value;
                    int otherPlaneBase = (BaseLayerOfPlane(curr.layer) == LAYER_WIDE_BASE) ? LAYER_NARROW_BASE : LAYER_WIDE_BASE;
                    int outLayer = MemberOf(dir, curr.r, curr.c, curr.layer) ? curr.layer : otherPlaneBase;

                    MarkSquare(curr.r, curr.c, outLayer, flagBit);
                    NibMask[curr.r, curr.c, outLayer] |= (byte)(1 << dir);
                    if (IsRamp(curr.r, curr.c))
                    {
                        MarkSquare(curr.r, curr.c, LAYER_WIDE_BASE, flagBit);
                        MarkSquare(curr.r, curr.c, LAYER_NARROW_BASE, flagBit);
                    }

                    int inLayer = step.entryLayer;
                    MarkSquare(step.r, step.c, inLayer, flagBit);
                    NibMask[step.r, step.c, inLayer] |= (byte)(1 << DirOpp(dir));
                    if (IsRamp(step.r, step.c))
                    {
                        MarkSquare(step.r, step.c, LAYER_WIDE_BASE, flagBit);
                        MarkSquare(step.r, step.c, LAYER_NARROW_BASE, flagBit);
                    }
                }
            }
        }
    }
}