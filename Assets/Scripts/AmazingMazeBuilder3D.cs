using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class AmazingMazeBuilder3D : MonoBehaviour
{
    public enum WallArchitectureStyle
    {
        FlatEdgedMac1Bit,
        CycladicRounded
    }

    [Header("VR / XR Setup (Drag 'XR Origin (XR Rig)' here!)")]
    public Transform xrRigRoot;
    public Transform leftWristTransform;
    public Vector3 wristMapLocalPosition = new Vector3(0.06f, 0.05f, -0.08f);
    public Vector3 wristMapLocalEuler = new Vector3(45f, 20f, 0f);
    public float wristMapWidthMeters = 0.18f;

    [Header("Locomotion: Walk / Run Mode & Teleport Settings")]
    [Tooltip("Click either VR Joystick Hat Button (Thumbstick Click), A/X button, or Left Shift on Desktop to toggle Run Mode!")]
    public bool isRunMode = false;
    public float walkSpeed = 3.2f;
    public float runSpeed = 7.2f;
    [Range(1, 20)] public int maxTeleportCellsPerJump = 6;

    [Header("Architecture & Maze Settings (Skill Level 1 = Level 2 Overpass Tunnels!)")]
    public WallArchitectureStyle architectureStyle = WallArchitectureStyle.FlatEdgedMac1Bit;
    [Tooltip("0 = Level 1 (Flat), 1 = Level 2 (Non-Euclidean Archway Overpasses!), 2 = Level 3 (Upper Narrow + Stairs), 3 = Level 4 (Hard)")]
    [Range(0, 3)] public int skillLevel = 1;
    public float cellSize = 3.0f;
    public float wallHeight = 0.85f;
    [Tooltip("Height of the enclosed Non-Euclidean Archway Tunnels")]
    public float tunnelArchHeight = 2.25f;
    public float wallThickness = 0.36f;
    [Range(0.01f, 0.08f)] public float blackEdgeOutlineWidth = 0.03f;

    [Header("Upper Narrow Plane & Tapered Stairs (Skill Levels 2 & 3 = Maze Levels 3 & 4)")]
    [Tooltip("Elevation of the upper narrow walkway plane (sits cleanly above lower archway roofs)")]
    public float upperFloorY = 2.65f;
    public float narrowCorridorWidth = 1.26f;
    public float narrowWallThickness = 0.24f;
    public float narrowWallHeight = 0.65f;
    public float narrowTunnelArchHeight = 2.05f;

    [Header("Unoccupied Area Checkerboard Settings")]
    public bool fillUnoccupiedWithCheckerboard = true;
    [Range(2, 16)] public int checkerSquaresPerMeter = 6;

    [Header("Dither3D Settings (Square Macintosh Pixels!)")]
    public bool useSquareMacintoshDots = true;
    public bool crisp1BitMacPixels = true;
    public Texture3D dither3DTexture;
    public Texture2D ditherRampTexture;
    [Range(2f, 10f)] public float dotScale = 6.2f;
    [Range(0f, 1f)]  public float dotSizeVariability = 0.0f;
    [Range(0.5f, 5f)] public float dotContrast = 2.5f;
    [Range(0.5f, 2f)] public float lightExposure = 1.05f;
    [Range(-0.5f, 0.5f)] public float lightOffset = 0.0f;
    public bool inverseDots = true;
    public bool quantizeLayers = true;

    [Header("2D Map Display Settings")]
    public bool showDesktopHudMap = true;
    [Range(0.5f, 2.0f)] public float hudMapScale = 1.0f;

    [Header("Optional Materials")]
    public Material whiteWallMaterial;
    public Material whiteFloorMaterial;
    public Material blackBreadcrumbMaterial;
    public Material blackEdgeMaterial;
    public Material checkerboardMaterial;

    // Non-Euclidean Stencil Template Materials
    private Material portalMaskNS_Mat;
    private Material portalMaskEW_Mat;

    public AmazingMazeData MazeData { get; private set; }
    private GameObject mazeRoot;
    private Transform activePlayerHeadOrBody;
    private GameObject wristMapObject;
    private Material wristMapMaterial;

    private Dictionary<(int r, int c, int subLayer), GameObject[]> breadcrumbMeshes = new Dictionary<(int r, int c, int subLayer), GameObject[]>();
    private Dictionary<(int r, int c), List<GameObject>> rampBreadcrumbMeshes = new Dictionary<(int r, int c), List<GameObject>>();
    private Renderer startMarkerRenderer;
    private Renderer endMarkerRenderer;

    private class BridgeCellInstance
    {
        public int r, c;
        public int planeBaseLayer;
        public float baseFloorY;
        public float archHalfSpan;
        public float archWallThick;
        public Vector3 worldCenter;
        public int nsSubLayer;
        public int ewSubLayer;
        public List<Collider> nsWallColliders = new List<Collider>();
        public List<Collider> ewWallColliders = new List<Collider>();
        public List<GameObject> portalQuads = new List<GameObject>();

        // Per-cell stencil materials sorted front-to-back so nearer tunnel walls always Z-occlude distant portals
        public Material cellPortalNS, cellPortalEW;
        public Material cellWallNS, cellFloorNS, cellCrumbNS, cellEdgeNS;
        public Material cellWallEW, cellFloorEW, cellCrumbEW, cellEdgeEW;
    }
    private Dictionary<(int r, int c, int planeBaseLayer), BridgeCellInstance> bridgeCells = new Dictionary<(int r, int c, int planeBaseLayer), BridgeCellInstance>();
    private List<BridgeCellInstance> sortedBridgeList = new List<BridgeCellInstance>();

    // Exact 1-bit QuickDraw Framebuffer
    private const int TILE_STRIDE = 16;
    private const int MARGIN_X = 8;
    private const int MARGIN_Y = 8;
    private int fbWidth;
    private int fbHeight;
    private byte[] fbPixels;
    private Color32[] fbColors;
    private Texture2D mac2DMapTex;

    private Texture3D generatedSquareDither3D;
    private Texture2D generatedSquareRamp2D;
    private Texture2D generatedCheckerTex;

    private WallArchitectureStyle lastBuiltStyle;
    private int lastBuiltSkillLevel = -1;
    private Vector3 lastPlayerPos;
    private float lastPlayerYaw = -999f;

    // XRI Reflection caches
    private Type teleportAreaType;
    private object cachedTeleportProvider;
    private CharacterController xrCharacterController;
    private List<Collider> stairFloorColliders = new List<Collider>();
    private List<MonoBehaviour> moveProviders = new List<MonoBehaviour>();
    private List<MonoBehaviour> teleportRayInteractors = new List<MonoBehaviour>();
    private MethodInfo tryGetHitInfoMethod;

    // Teleport Reticle & State
    private bool wasTeleportRayActiveLastFrame = false;
    private bool hasValidPendingTeleportHit = false;
    private Vector3 pendingTeleportWorldHit;
    private GameObject teleportReticleObj;
    private Renderer teleportReticleRenderer;
    private Texture2D validTargetTex;
    private Texture2D invalidXTex;
    private Gradient blueValidGradient;
    private Gradient redInvalidGradient;

    // Joystick Hat Click (Run Mode) debounce
    private bool hatClickWasPressed = false;

    private static readonly byte[] CIRCLE_ROWS = new byte[]
    {
        0b00111100,
        0b01111110,
        0b11111111,
        0b11111111,
        0b11111111,
        0b11111111,
        0b01111110,
        0b00111100
    };

    void Start()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            teleportAreaType = asm.GetType("UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea")
                            ?? asm.GetType("UnityEngine.XR.Interaction.Toolkit.TeleportationArea");
            if (teleportAreaType != null) break;
        }

        EnsureDither3DMaterials();
        MazeData = new AmazingMazeData();
        MazeData.MakeMaze(skillLevel);
        lastBuiltSkillLevel = skillLevel;

        if (xrRigRoot != null)
            CacheXRLocomotionAndTeleportComponents();

        RebuildMazeGeometryOnly();

        if (xrRigRoot != null)
            SetupVRRigAndWristMap();
        else
            SpawnSimpleDesktopPlayer();

        IgnoreStairFloorCollisionsForPlayer();
        ApplyMoveSpeedToXR();
        ConfigureCameraAndGlobalDither();
        UpdateNonEuclideanTunnelState();
        UpdateBreadcrumbsAnd2DMap();
    }

    void SetXRRigPositionAndSync(Vector3 newRigPos)
    {
        if (xrRigRoot == null) return;
        bool wasEnabled = false;
        if (xrCharacterController != null && xrCharacterController.enabled)
        {
            wasEnabled = true;
            xrCharacterController.enabled = false;
        }
        xrRigRoot.position = newRigPos;
        Physics.SyncTransforms();
        if (wasEnabled)
        {
            xrCharacterController.enabled = true;
        }
    }

    void IgnoreStairFloorCollisionsForPlayer()
    {
        CharacterController playerCC = xrCharacterController;
        if (playerCC == null && activePlayerHeadOrBody != null)
            playerCC = activePlayerHeadOrBody.GetComponent<CharacterController>();

        if (playerCC == null) return;
        playerCC.slopeLimit = 75f;
        playerCC.stepOffset = 0.65f;

        foreach (Collider col in stairFloorColliders)
        {
            if (col != null)
                Physics.IgnoreCollision(col, playerCC, true);
        }
    }

    void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            architectureStyle = (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                ? WallArchitectureStyle.CycladicRounded
                : WallArchitectureStyle.FlatEdgedMac1Bit;
            RebuildMazeGeometryOnly();
            IgnoreStairFloorCollisionsForPlayer();
            UpdateNonEuclideanTunnelState();
            UpdateBreadcrumbsAnd2DMap();
        }
        if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift))
        {
            ToggleRunMode();
        }
        if (Input.GetKeyDown(KeyCode.Alpha1)) SwitchToSkillLevel(0);
        if (Input.GetKeyDown(KeyCode.Alpha2)) SwitchToSkillLevel(1);
        if (Input.GetKeyDown(KeyCode.Alpha3)) SwitchToSkillLevel(2);
        if (Input.GetKeyDown(KeyCode.Alpha4)) SwitchToSkillLevel(3);
#endif
        if (activePlayerHeadOrBody == null) return;

        PollJoystickHatRunToggleSafe();

        if (xrRigRoot != null)
        {
            MonitorAndExecuteVRTeleport();
        }

        Vector3 curPos = activePlayerHeadOrBody.position;
        var (curR, curC) = WorldToCell(curPos);
        if (curR != MazeData.PlayerPos.r || curC != MazeData.PlayerPos.c)
        {
            StepPlayerTowardCell(curR, curC);
            UpdateNonEuclideanTunnelState();
        }

        // Keep VR rig elevation synced with current plane or tapered stair steps
        if (xrRigRoot != null)
        {
            float targetFloorY = GetTargetPlayerFloorY(activePlayerHeadOrBody.position);
            Vector3 rigPos = xrRigRoot.position;
            float nextY = Mathf.Lerp(rigPos.y, targetFloorY + 0.02f, Mathf.Clamp01(Time.deltaTime * 18f));
            if (Mathf.Abs(nextY - rigPos.y) > 0.0005f)
            {
                rigPos.y = nextY;
                SetXRRigPositionAndSync(rigPos);
            }
        }

        UpdateBridgeRenderQueuesFrontToBack();

        float curYaw = activePlayerHeadOrBody.eulerAngles.y;
        if ((curPos - lastPlayerPos).sqrMagnitude > 0.0001f || Mathf.Abs(Mathf.DeltaAngle(curYaw, lastPlayerYaw)) > 0.25f)
        {
            lastPlayerPos = curPos;
            lastPlayerYaw = curYaw;
            FlushFramebufferWithPlayerWedge();
        }
    }

    public void SwitchToSkillLevel(int newSkillLevel)
    {
        skillLevel = Mathf.Clamp(newSkillLevel, 0, 3);
        lastBuiltSkillLevel = skillLevel;
        MazeData.MakeMaze(skillLevel);
        RebuildMazeGeometryOnly();
        IgnoreStairFloorCollisionsForPlayer();
        RepositionPlayerAtStart();
        UpdateNonEuclideanTunnelState();
        UpdateBreadcrumbsAnd2DMap();
    }

    void RepositionPlayerAtStart()
    {
        float startY = GetLayerBaseFloorY(MazeData.StartPos.layer);
        byte startMask = MazeData.GetSquare(MazeData.StartPos.r, MazeData.StartPos.c, MazeData.StartPos.layer);
        float startYaw = 0f;
        if ((startMask & (1 << AmazingMazeData.DIR_RIGHT)) != 0) startYaw = 90f;
        else if ((startMask & (1 << AmazingMazeData.DIR_DOWN)) != 0) startYaw = 180f;
        else if ((startMask & (1 << AmazingMazeData.DIR_LEFT)) != 0) startYaw = -90f;

        if (xrRigRoot != null)
        {
            Vector3 destWorld = CellToWorldCenter(MazeData.StartPos.r, MazeData.StartPos.c, startY + 0.02f);
            Vector3 headOffset = (activePlayerHeadOrBody != null) ? (activePlayerHeadOrBody.position - xrRigRoot.position) : Vector3.zero;
            headOffset.y = 0f;
            SetXRRigPositionAndSync(destWorld - headOffset);
            xrRigRoot.eulerAngles = new Vector3(0f, startYaw, 0f);
        }
        else if (activePlayerHeadOrBody != null)
        {
            CharacterController cc = activePlayerHeadOrBody.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            activePlayerHeadOrBody.position = CellToWorldCenter(MazeData.StartPos.r, MazeData.StartPos.c, startY + 0.05f);
            activePlayerHeadOrBody.eulerAngles = new Vector3(0f, startYaw, 0f);
            Physics.SyncTransforms();
            if (cc != null) cc.enabled = true;
        }
    }

    public float GetLayerBaseFloorY(int subLayer)
    {
        return AmazingMazeData.IsWideSubLayer(subLayer) ? 0f : upperFloorY;
    }

    public static Vector3 DirToWorldVec(int dir)
    {
        switch (dir)
        {
            case AmazingMazeData.DIR_LEFT:  return new Vector3(-1f, 0f, 0f);
            case AmazingMazeData.DIR_DOWN:  return new Vector3(0f, 0f, -1f);
            case AmazingMazeData.DIR_RIGHT: return new Vector3(1f, 0f, 0f);
            case AmazingMazeData.DIR_UP:    return new Vector3(0f, 0f, 1f);
            default:                        return Vector3.forward;
        }
    }

    public float GetTargetPlayerFloorY(Vector3 worldPos)
    {
        if (MazeData == null) return 0f;
        int pr = MazeData.PlayerPos.r;
        int pc = MazeData.PlayerPos.c;
        var (curR, curC) = WorldToCell(worldPos);

        int rampR = -1, rampC = -1;
        if (MazeData.IsRamp(pr, pc))
        {
            rampR = pr;
            rampC = pc;
        }
        else if (MazeData.IsRamp(curR, curC) && (Mathf.Abs(curR - pr) + Mathf.Abs(curC - pc) <= 1))
        {
            rampR = curR;
            rampC = curC;
        }

        if (rampR != -1)
        {
            int wMask = MazeData.GetSquare(rampR, rampC, AmazingMazeData.LAYER_WIDE_BASE) & 0x0F;
            int nMask = MazeData.GetSquare(rampR, rampC, AmazingMazeData.LAYER_NARROW_BASE) & 0x0F;
            int wideDir = 0, narrowDir = 2;
            for (int d = 0; d < 4; d++)
            {
                if ((wMask & (1 << d)) != 0) wideDir = d;
                if ((nMask & (1 << d)) != 0) narrowDir = d;
            }

            Vector3 rel = worldPos - CellToWorldCenter(rampR, rampC, 0f);
            float halfCell = cellSize * 0.5f;
            Vector3 vecW = DirToWorldVec(wideDir);
            Vector3 vecN = DirToWorldVec(narrowDir);

            if (narrowDir == AmazingMazeData.DirOpp(wideDir))
            {
                float distAlongNarrow = Vector3.Dot(rel, vecN);
                float t = Mathf.Clamp01((distAlongNarrow + halfCell) / cellSize);
                return t * upperFloorY;
            }
            else
            {
                float u = Vector3.Dot(rel, vecW);
                float v = Vector3.Dot(rel, vecN);
                float p1 = Mathf.Clamp01((halfCell - u) / halfCell) * 0.5f;
                float p2 = Mathf.Clamp01(v / halfCell) * 0.5f;
                return Mathf.Clamp01(p1 + p2) * upperFloorY;
            }
        }

        return GetLayerBaseFloorY(MazeData.PlayerPos.layer);
    }

    public void ToggleRunMode()
    {
        isRunMode = !isRunMode;
        ApplyMoveSpeedToXR();
        FlushFramebufferWithPlayerWedge();
    }

    void ApplyMoveSpeedToXR()
    {
        float targetSpeed = isRunMode ? runSpeed : walkSpeed;
        foreach (MonoBehaviour mp in moveProviders)
        {
            if (mp == null) continue;
            Type t = mp.GetType();
            PropertyInfo p = t.GetProperty("moveSpeed", BindingFlags.Public | BindingFlags.Instance);
            p?.SetValue(mp, targetSpeed);

            // Disable built-in gravity so XRI does not fight stair & upper-plane elevation tracking
            PropertyInfo gravProp = t.GetProperty("useGravity", BindingFlags.Public | BindingFlags.Instance);
            gravProp?.SetValue(mp, false);
        }
    }

    void PollJoystickHatRunToggleSafe()
    {
        bool hatClicked = false;

        try
        {
            var rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            var leftHand  = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);

            if (rightHand.isValid)
            {
                if (rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxisClick, out bool rStick) && rStick) hatClicked = true;
                if (rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool rBtn) && rBtn) hatClicked = true;
            }
            if (leftHand.isValid)
            {
                if (leftHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxisClick, out bool lStick) && lStick) hatClicked = true;
                if (leftHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool lBtn) && lBtn) hatClicked = true;
            }

            if (!hatClicked)
            {
                Type xrControllerType = Type.GetType("UnityEngine.InputSystem.XR.XRController, Unity.InputSystem");
                Type inputControlType = Type.GetType("UnityEngine.InputSystem.InputControl, Unity.InputSystem");
                MethodInfo tryGetChild = inputControlType?.GetMethod("TryGetChildControl", new Type[] { typeof(string) });

                if (xrControllerType != null && tryGetChild != null)
                {
                    foreach (string handPropName in new string[] { "leftHand", "rightHand" })
                    {
                        object handDev = xrControllerType.GetProperty(handPropName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                        if (handDev != null)
                        {
                            foreach (string ctrlName in new string[] { "thumbstickClicked", "primaryButton", "secondaryButton" })
                            {
                                object ctrl = tryGetChild.Invoke(handDev, new object[] { ctrlName });
                                if (ctrl != null)
                                {
                                    var isPressedProp = ctrl.GetType().GetProperty("isPressed");
                                    if (isPressedProp != null && (bool)isPressedProp.GetValue(ctrl))
                                    {
                                        hatClicked = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        if (hatClicked && !hatClickWasPressed)
        {
            ToggleRunMode();
        }
        hatClickWasPressed = hatClicked;
    }

    /* ============================================================================
     * FRONT-TO-BACK STENCIL PORTAL SORTING & COLLIDER CONTROLLER
     * ============================================================================ */
    void UpdateBridgeRenderQueuesFrontToBack()
    {
        if (bridgeCells.Count == 0 || activePlayerHeadOrBody == null) return;

        Vector3 camPos = activePlayerHeadOrBody.position;
        int pr = MazeData.PlayerPos.r;
        int pc = MazeData.PlayerPos.c;
        int pLayer = MazeData.PlayerPos.layer;
        int playerPlaneBase = AmazingMazeData.BaseLayerOfPlane(pLayer);

        sortedBridgeList.Clear();
        foreach (var kvp in bridgeCells)
            sortedBridgeList.Add(kvp.Value);

        sortedBridgeList.Sort((a, b) =>
        {
            float da = (a.worldCenter - camPos).sqrMagnitude;
            float db = (b.worldCenter - camPos).sqrMagnitude;
            return da.CompareTo(db);
        });

        for (int i = 0; i < sortedBridgeList.Count; i++)
        {
            BridgeCellInstance br = sortedBridgeList[i];
            float absDX = Mathf.Abs(camPos.x - br.worldCenter.x);
            float absDZ = Mathf.Abs(camPos.z - br.worldCenter.z);

            float portalOuterDist = br.archHalfSpan + br.archWallThick * 0.5f;
            float thresholdDist = portalOuterDist + 0.28f;
            float corridorHalfWidth = (br.archHalfSpan - br.archWallThick * 0.5f) + 0.15f;

            bool onSamePlane = (playerPlaneBase == br.planeBaseLayer);
            bool inCellGrid = onSamePlane && (pr == br.r && pc == br.c);
            bool atNSThreshold = onSamePlane && (absDX <= corridorHalfWidth && absDZ <= thresholdDist);
            bool atEWThreshold = onSamePlane && (absDZ <= corridorHalfWidth && absDX <= thresholdDist);

            // Check if the player is inside the bridge OR standing right at its active entrance threshold on the same plane
            bool activeIsNS = (inCellGrid) ? (pLayer == br.nsSubLayer) : (absDZ >= absDX);
            bool playerInsideOrAtThreshold = inCellGrid || (activeIsNS && atNSThreshold) || (!activeIsNS && atEWThreshold);

            int baseQ = 2005 + i * 10;

            if (playerInsideOrAtThreshold)
            {
                foreach (var pq in br.portalQuads)
                    if (pq != null && pq.activeSelf) pq.SetActive(false);

                ConfigureCellTunnelMaterials(br.cellWallNS, br.cellFloorNS, br.cellCrumbNS, br.cellEdgeNS, activeIsNS ? 8 : 1, 2002);
                ConfigureCellTunnelMaterials(br.cellWallEW, br.cellFloorEW, br.cellCrumbEW, br.cellEdgeEW, activeIsNS ? 1 : 8, 2002);
            }
            else
            {
                foreach (var pq in br.portalQuads)
                    if (pq != null && !pq.activeSelf) pq.SetActive(true);

                bool nsDoorIsCloser = (absDZ >= absDX);

                if (nsDoorIsCloser)
                {
                    br.cellPortalNS.renderQueue = baseQ;
                    ConfigureCellTunnelMaterials(br.cellWallNS, br.cellFloorNS, br.cellCrumbNS, br.cellEdgeNS, 3, baseQ + 1);
                    br.cellPortalEW.renderQueue = baseQ + 2;
                    ConfigureCellTunnelMaterials(br.cellWallEW, br.cellFloorEW, br.cellCrumbEW, br.cellEdgeEW, 3, baseQ + 3);
                }
                else
                {
                    br.cellPortalEW.renderQueue = baseQ;
                    ConfigureCellTunnelMaterials(br.cellWallEW, br.cellFloorEW, br.cellCrumbEW, br.cellEdgeEW, 3, baseQ + 1);
                    br.cellPortalNS.renderQueue = baseQ + 2;
                    ConfigureCellTunnelMaterials(br.cellWallNS, br.cellFloorNS, br.cellCrumbNS, br.cellEdgeNS, 3, baseQ + 3);
                }
            }
        }
    }

    void ConfigureCellTunnelMaterials(Material wallM, Material floorM, Material crumbM, Material edgeM, int stencilComp, int queue)
    {
        if (wallM != null) { wallM.SetInt("_StencilComp", stencilComp); wallM.renderQueue = queue; }
        if (floorM != null) { floorM.SetInt("_StencilComp", stencilComp); floorM.renderQueue = queue; }
        if (crumbM != null) { crumbM.SetInt("_StencilComp", stencilComp); crumbM.renderQueue = queue; }
        if (edgeM != null) { edgeM.SetInt("_StencilComp", stencilComp); edgeM.renderQueue = queue; }
    }

    void UpdateNonEuclideanTunnelState()
    {
        if (bridgeCells.Count == 0) return;

        int pr = MazeData.PlayerPos.r;
        int pc = MazeData.PlayerPos.c;
        int pLayer = MazeData.PlayerPos.layer;

        var distMap = new Dictionary<(int r, int c, int l), int>();
        var queue = new Queue<(int r, int c, int l)>();
        var startState = (pr, pc, pLayer);
        distMap[startState] = 0;
        queue.Enqueue(startState);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            int curDist = distMap[cur];
            if (curDist >= 18) continue;

            for (int dir = 0; dir < 4; dir++)
            {
                var stepOpt = MazeData.CanMove(cur.r, cur.c, cur.l, dir);
                if (stepOpt.HasValue)
                {
                    var nextState = (stepOpt.Value.r, stepOpt.Value.c, stepOpt.Value.layer);
                    if (!distMap.ContainsKey(nextState))
                    {
                        distMap[nextState] = curDist + 1;
                        queue.Enqueue(nextState);
                    }
                }
            }
        }

        foreach (var kvp in bridgeCells)
        {
            BridgeCellInstance br = kvp.Value;
            bool enableNSColliders;

            if (pr == br.r && pc == br.c && AmazingMazeData.BaseLayerOfPlane(pLayer) == br.planeBaseLayer)
            {
                enableNSColliders = (pLayer == br.nsSubLayer);
            }
            else
            {
                int nsSteps = distMap.TryGetValue((br.r, br.c, br.nsSubLayer), out int dNS) ? dNS : 999;
                int ewSteps = distMap.TryGetValue((br.r, br.c, br.ewSubLayer), out int dEW) ? dEW : 999;
                if (nsSteps != ewSteps)
                    enableNSColliders = (nsSteps < ewSteps);
                else
                    enableNSColliders = Mathf.Abs(pr - br.r) >= Mathf.Abs(pc - br.c);
            }

            foreach (Collider col in br.nsWallColliders)
                if (col != null) col.enabled = enableNSColliders;
            foreach (Collider col in br.ewWallColliders)
                if (col != null) col.enabled = !enableNSColliders;
        }

        UpdateBridgeRenderQueuesFrontToBack();
    }

    /* ============================================================================
     * TELEPORT VISUAL RETICLE (BLUE TARGET ◎ vs RED X ✕) & ARC COLOR CONTROLLER
     * ============================================================================ */
    void EnsureTeleportReticleAndGradients()
    {
        if (blueValidGradient == null)
        {
            blueValidGradient = new Gradient();
            blueValidGradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(new Color(0.1f, 0.55f, 1f), 0f), new GradientColorKey(new Color(0.2f, 0.75f, 1f), 1f) },
                new GradientAlphaKey[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(1f, 1f) }
            );

            redInvalidGradient = new Gradient();
            redInvalidGradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(new Color(1f, 0.15f, 0.15f), 0f), new GradientColorKey(new Color(1f, 0.1f, 0.1f), 1f) },
                new GradientAlphaKey[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(1f, 1f) }
            );
        }

        if (validTargetTex == null)
        {
            validTargetTex = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            validTargetTex.filterMode = FilterMode.Point;
            validTargetTex.wrapMode = TextureWrapMode.Clamp;
            Color32[] cols = new Color32[32 * 32];
            Color32 blue = new Color32(30, 160, 255, 255);
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 clear = new Color32(0, 0, 0, 0);

            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    float dx = x - 15.5f;
                    float dy = y - 15.5f;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    bool outerRing = (dist >= 12.0f && dist <= 14.8f);
                    bool innerRing = (dist >= 5.0f && dist <= 7.2f);
                    bool centerDot = (dist <= 2.0f);
                    bool crossTicks = (Mathf.Abs(dx) <= 1.2f && Mathf.Abs(dy) >= 8.5f && Mathf.Abs(dy) <= 14.5f) ||
                                      (Mathf.Abs(dy) <= 1.2f && Mathf.Abs(dx) >= 8.5f && Mathf.Abs(dx) <= 14.5f);

                    if (outerRing || innerRing || centerDot || crossTicks)
                        cols[y * 32 + x] = blue;
                    else if (Mathf.Abs(dist - 11.5f) < 0.8f || Mathf.Abs(dist - 15.3f) < 0.8f)
                        cols[y * 32 + x] = white;
                    else
                        cols[y * 32 + x] = clear;
                }
            }
            validTargetTex.SetPixels32(cols);
            validTargetTex.Apply();
        }

        if (invalidXTex == null)
        {
            invalidXTex = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            invalidXTex.filterMode = FilterMode.Point;
            invalidXTex.wrapMode = TextureWrapMode.Clamp;
            Color32[] cols = new Color32[32 * 32];
            Color32 red = new Color32(255, 40, 40, 255);
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 clear = new Color32(0, 0, 0, 0);

            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    float dx = x - 15.5f;
                    float dy = y - 15.5f;
                    if (Mathf.Abs(dx) > 12.5f || Mathf.Abs(dy) > 12.5f)
                    {
                        cols[y * 32 + x] = clear;
                        continue;
                    }

                    float d1 = Mathf.Abs(dx - dy);
                    float d2 = Mathf.Abs(dx + dy);
                    float minDiag = Mathf.Min(d1, d2);

                    if (minDiag <= 2.6f)
                        cols[y * 32 + x] = red;
                    else if (minDiag <= 3.8f)
                        cols[y * 32 + x] = white;
                    else
                        cols[y * 32 + x] = clear;
                }
            }
            invalidXTex.SetPixels32(cols);
            invalidXTex.Apply();
        }

        if (teleportReticleObj == null)
        {
            teleportReticleObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            teleportReticleObj.name = "TeleportFloorReticle";
            Destroy(teleportReticleObj.GetComponent<Collider>());
            teleportReticleObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            teleportReticleObj.transform.localScale = new Vector3(0.75f, 0.75f, 1f);

            Shader transShader = Shader.Find("Unlit/Transparent");
            if (transShader == null) transShader = Shader.Find("Sprites/Default");
            teleportReticleRenderer = teleportReticleObj.GetComponent<Renderer>();
            teleportReticleRenderer.sharedMaterial = new Material(transShader);
            teleportReticleObj.SetActive(false);
        }
    }

    void MonitorAndExecuteVRTeleport()
    {
        EnsureTeleportReticleAndGradients();

        bool anyTeleportRayActive = false;
        bool showReticleThisFrame = false;

        foreach (MonoBehaviour rayInteractor in teleportRayInteractors)
        {
            if (rayInteractor == null) continue;
            if (!rayInteractor.gameObject.activeInHierarchy || !rayInteractor.enabled) continue;

            anyTeleportRayActive = true;

            if (tryGetHitInfoMethod != null)
            {
                object[] args = new object[] { Vector3.zero, Vector3.zero, 0, false };
                bool hitAnything = (bool)tryGetHitInfoMethod.Invoke(rayInteractor, args);
                if (hitAnything)
                {
                    Vector3 hitPos = (Vector3)args[0];
                    Vector3 hitNormal = (Vector3)args[1];

                    var (hitR, hitC) = WorldToCell(hitPos);
                    bool isFloorOrStairHit = hitNormal.y > 0.45f;

                    bool isLegalCorridorJump = isFloorOrStairHit &&
                                               IsLegalStraightCorridorTeleport(hitR, hitC, hitPos.y, out int clampedR, out int clampedC) &&
                                               (clampedR == hitR && clampedC == hitC) &&
                                               (!(hitR == MazeData.PlayerPos.r && hitC == MazeData.PlayerPos.c) || MazeData.IsRamp(hitR, hitC));

                    if (isLegalCorridorJump)
                    {
                        hasValidPendingTeleportHit = true;
                        pendingTeleportWorldHit = hitPos;

                        SetTeleportRayVisualColor(rayInteractor.gameObject, true);
                        teleportReticleObj.transform.position = new Vector3(hitPos.x, hitPos.y + 0.025f, hitPos.z);
                        teleportReticleObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                        teleportReticleRenderer.sharedMaterial.mainTexture = validTargetTex;
                        showReticleThisFrame = true;
                    }
                    else
                    {
                        hasValidPendingTeleportHit = false;

                        SetTeleportRayVisualColor(rayInteractor.gameObject, false);
                        teleportReticleObj.transform.position = hitPos + hitNormal * 0.03f;
                        teleportReticleObj.transform.rotation = Quaternion.LookRotation(-hitNormal);
                        teleportReticleRenderer.sharedMaterial.mainTexture = invalidXTex;
                        showReticleThisFrame = true;
                    }
                }
                else
                {
                    hasValidPendingTeleportHit = false;
                    SetTeleportRayVisualColor(rayInteractor.gameObject, false);
                }
            }
        }

        if (teleportReticleObj != null && teleportReticleObj.activeSelf != showReticleThisFrame)
        {
            teleportReticleObj.SetActive(showReticleThisFrame);
        }

        if (wasTeleportRayActiveLastFrame && !anyTeleportRayActive && hasValidPendingTeleportHit)
        {
            hasValidPendingTeleportHit = false;
            var (targetR, targetC) = WorldToCell(pendingTeleportWorldHit);

            if (IsLegalStraightCorridorTeleport(targetR, targetC, pendingTeleportWorldHit.y, out int clampedR, out int clampedC))
            {
                StepPlayerTowardCell(clampedR, clampedC);
                Vector3 destWorld;
                if (MazeData.IsRamp(clampedR, clampedC))
                {
                    float stairY = GetTargetPlayerFloorY(pendingTeleportWorldHit);
                    destWorld = new Vector3(pendingTeleportWorldHit.x, stairY + 0.02f, pendingTeleportWorldHit.z);
                }
                else
                {
                    float destFloorY = GetLayerBaseFloorY(MazeData.PlayerPos.layer);
                    destWorld = CellToWorldCenter(clampedR, clampedC, destFloorY + 0.02f);
                }
                Vector3 headOffset = activePlayerHeadOrBody.position - xrRigRoot.position;
                headOffset.y = 0f;
                SetXRRigPositionAndSync(destWorld - headOffset);

                UpdateNonEuclideanTunnelState();
            }
        }

        if (!anyTeleportRayActive)
        {
            hasValidPendingTeleportHit = false;
        }
        wasTeleportRayActiveLastFrame = anyTeleportRayActive;
    }

    bool IsLegalStraightCorridorTeleport(int targetR, int targetC, float hitY, out int clampedR, out int clampedC)
    {
        clampedR = MazeData.PlayerPos.r;
        clampedC = MazeData.PlayerPos.c;

        int dr = targetR - clampedR;
        int dc = targetC - clampedC;

        if (dr != 0 && dc != 0)
            return false;

        if (dr == 0 && dc == 0)
        {
            if (MazeData.IsRamp(clampedR, clampedC)) return true;
            return Mathf.Abs(hitY - GetLayerBaseFloorY(MazeData.PlayerPos.layer)) < 0.60f;
        }

        int dir = -1;
        int dist = Mathf.Abs(dr) + Mathf.Abs(dc);
        if (dist > maxTeleportCellsPerJump)
            return false;

        if (dc < 0) dir = AmazingMazeData.DIR_LEFT;
        else if (dr > 0) dir = AmazingMazeData.DIR_DOWN;
        else if (dc > 0) dir = AmazingMazeData.DIR_RIGHT;
        else if (dr < 0) dir = AmazingMazeData.DIR_UP;

        int simR = MazeData.PlayerPos.r;
        int simC = MazeData.PlayerPos.c;
        int simLayer = MazeData.PlayerPos.layer;

        for (int s = 0; s < dist; s++)
        {
            var stepOpt = MazeData.CanMove(simR, simC, simLayer, dir);
            if (!stepOpt.HasValue) return false;

            simR = stepOpt.Value.r;
            simC = stepOpt.Value.c;
            simLayer = stepOpt.Value.layer;
        }

        // Anti-cheat check: if the destination cell is not a stair cell, its hit elevation MUST match the plane
        // reached by legally stepping through CanMove (prevents jumping from upper narrow path down into lower maze!)
        if (!MazeData.IsRamp(simR, simC))
        {
            float expectedTargetY = GetLayerBaseFloorY(simLayer);
            if (Mathf.Abs(hitY - expectedTargetY) > 0.60f)
                return false;
        }

        clampedR = simR;
        clampedC = simC;
        return true;
    }

    void SetTeleportRayVisualColor(GameObject rayObj, bool isValidBlue)
    {
        Gradient activeGrad = isValidBlue ? blueValidGradient : redInvalidGradient;

        foreach (MonoBehaviour mb in rayObj.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            Type t = mb.GetType();
            if (t.Name == "XRInteractorLineVisual")
            {
                var validProp = t.GetProperty("validColorGradient");
                var invalidProp = t.GetProperty("invalidColorGradient");
                validProp?.SetValue(mb, activeGrad);
                invalidProp?.SetValue(mb, activeGrad);
            }
        }

        LineRenderer lr = rayObj.GetComponent<LineRenderer>();
        if (lr != null)
        {
            lr.colorGradient = activeGrad;
        }
    }

    void StepPlayerTowardCell(int targetR, int targetC)
    {
        bool anyMoved = false;
        int safety = 0;

        while ((MazeData.PlayerPos.r != targetR || MazeData.PlayerPos.c != targetC) && safety++ < maxTeleportCellsPerJump)
        {
            int dr = Mathf.Clamp(targetR - MazeData.PlayerPos.r, -1, 1);
            int dc = Mathf.Clamp(targetC - MazeData.PlayerPos.c, -1, 1);

            bool stepped = false;
            if (dr != 0 && MazeData.TryStepToAdjacentCell(MazeData.PlayerPos.r + dr, MazeData.PlayerPos.c))
            {
                stepped = true;
            }
            else if (dc != 0 && MazeData.TryStepToAdjacentCell(MazeData.PlayerPos.r, MazeData.PlayerPos.c + dc))
            {
                stepped = true;
            }

            if (stepped)
                anyMoved = true;
            else
                break;
        }

        if (MazeData.PlayerPos.r != targetR || MazeData.PlayerPos.c != targetC)
        {
            Vector3 legalCellCenter = CellToWorldCenter(MazeData.PlayerPos.r, MazeData.PlayerPos.c, 0f);
            float legalY = MazeData.IsRamp(MazeData.PlayerPos.r, MazeData.PlayerPos.c)
                ? GetTargetPlayerFloorY(legalCellCenter)
                : GetLayerBaseFloorY(MazeData.PlayerPos.layer);
            if (xrRigRoot != null)
            {
                Vector3 legalWorldPos = CellToWorldCenter(MazeData.PlayerPos.r, MazeData.PlayerPos.c, legalY + 0.02f);
                Vector3 headOffset = activePlayerHeadOrBody.position - xrRigRoot.position;
                headOffset.y = 0f;
                SetXRRigPositionAndSync(legalWorldPos - headOffset);
            }
            else if (activePlayerHeadOrBody != null)
            {
                CharacterController cc = activePlayerHeadOrBody.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                activePlayerHeadOrBody.position = CellToWorldCenter(MazeData.PlayerPos.r, MazeData.PlayerPos.c, legalY + 0.05f);
                Physics.SyncTransforms();
                if (cc != null) cc.enabled = true;
            }
        }

        if (anyMoved)
            UpdateBreadcrumbsAnd2DMap();
    }

    static void SetAllInteractionLayersOnComponent(Component comp)
    {
        if (comp == null) return;
        Type t = comp.GetType();
        while (t != null && t != typeof(MonoBehaviour))
        {
            FieldInfo f = t.GetField("m_InteractionLayers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (f != null)
            {
                object maskBoxed = f.GetValue(comp);
                if (maskBoxed != null)
                {
                    FieldInfo bitsField = maskBoxed.GetType().GetField("m_Bits", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (bitsField != null)
                    {
                        if (bitsField.FieldType == typeof(uint))
                            bitsField.SetValue(maskBoxed, uint.MaxValue);
                        else
                            bitsField.SetValue(maskBoxed, -1);

                        f.SetValue(comp, maskBoxed);
                    }
                }
                break;
            }
            t = t.BaseType;
        }
    }

    void CacheXRLocomotionAndTeleportComponents()
    {
        teleportRayInteractors.Clear();
        moveProviders.Clear();
        xrCharacterController = xrRigRoot.GetComponentInChildren<CharacterController>(true);
        if (xrCharacterController != null)
        {
            xrCharacterController.slopeLimit = 75f;
            xrCharacterController.stepOffset = 0.65f;
        }

        foreach (MonoBehaviour mb in xrRigRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            Type t = mb.GetType();

            if (t.Name == "TeleportationProvider")
            {
                cachedTeleportProvider = mb;
            }
            else if (t.GetProperty("moveSpeed", BindingFlags.Public | BindingFlags.Instance) != null)
            {
                moveProviders.Add(mb);
            }
            else if (t.Name == "XRRayInteractor")
            {
                SetAllInteractionLayersOnComponent(mb);

                string goName = mb.gameObject.name.ToLowerInvariant();
                if (goName.Contains("teleport"))
                {
                    teleportRayInteractors.Add(mb);
                    if (tryGetHitInfoMethod == null)
                    {
                        tryGetHitInfoMethod = t.GetMethod("TryGetHitInfo", new Type[] {
                            typeof(Vector3).MakeByRefType(),
                            typeof(Vector3).MakeByRefType(),
                            typeof(int).MakeByRefType(),
                            typeof(bool).MakeByRefType()
                        });
                    }

                    var velProp = t.GetProperty("velocity");
                    velProp?.SetValue(mb, 12f);
                }
            }
        }
    }

    void ConfigureFloorTeleportationArea(GameObject floor)
    {
        if (teleportAreaType == null) return;

        floor.SetActive(false);
        Component teleArea = floor.AddComponent(teleportAreaType);

        var collidersProp = teleportAreaType.GetProperty("colliders");
        if (collidersProp != null)
        {
            var colList = collidersProp.GetValue(teleArea) as System.Collections.IList;
            if (colList != null)
            {
                colList.Clear();
                colList.Add(floor.GetComponent<Collider>());
            }
        }

        if (cachedTeleportProvider != null)
        {
            var providerProp = teleportAreaType.GetProperty("teleportationProvider");
            providerProp?.SetValue(teleArea, cachedTeleportProvider);
        }

        SetAllInteractionLayersOnComponent(teleArea);
        floor.SetActive(true);
    }

    /* ============================================================================
     * PROCEDURAL TEXTURES & STENCIL MATERIALS
     * ============================================================================ */
    void BuildSquareMacintoshDitherTextures()
    {
        int[,] bayer4x4 = new int[4, 4]
        {
            {  0,  8,  2, 10 },
            { 12,  4, 14,  6 },
            {  3, 11,  1,  9 },
            { 15,  7, 13,  5 }
        };

        int dotsPerSide = 4;
        int dotsTotal = dotsPerSide * dotsPerSide;
        int texelsPerDot = 16;
        int resXY = dotsPerSide * texelsPerDot;
        int resZ = dotsTotal;

        if (generatedSquareDither3D == null)
        {
            generatedSquareDither3D = new Texture3D(resXY, resXY, resZ, TextureFormat.R8, false);
            generatedSquareDither3D.wrapMode = TextureWrapMode.Repeat;
        }
        generatedSquareDither3D.filterMode = crisp1BitMacPixels ? FilterMode.Point : FilterMode.Bilinear;

        Color32[] cols3D = new Color32[resXY * resXY * resZ];
        Vector2[] dotCenters = new Vector2[dotsTotal];
        for (int by = 0; by < dotsPerSide; by++)
            for (int bx = 0; bx < dotsPerSide; bx++)
                dotCenters[bayer4x4[by, bx]] = new Vector2((bx + 0.5f) / dotsPerSide, (by + 0.5f) / dotsPerSide);

        for (int z = 0; z < resZ; z++)
        {
            int activeDotCount = z + 1;
            float gridFreq = Mathf.Sqrt(activeDotCount);

            for (int y = 0; y < resXY; y++)
            {
                float v = (y + 0.5f) / resXY;
                for (int x = 0; x < resXY; x++)
                {
                    float u = (x + 0.5f) / resXY;
                    float minSquareDist = 999f;
                    for (int d = 0; d < activeDotCount; d++)
                    {
                        float dx = Mathf.Abs(u - dotCenters[d].x);
                        float dy = Mathf.Abs(v - dotCenters[d].y);
                        if (dx > 0.5f) dx = 1.0f - dx;
                        if (dy > 0.5f) dy = 1.0f - dy;

                        float sqDist = Mathf.Max(dx, dy) * gridFreq * 2.0f;
                        if (sqDist < minSquareDist) minSquareDist = sqDist;
                    }

                    float val = Mathf.Clamp01(1.0f - minSquareDist * 0.85f);
                    byte bVal = (byte)Mathf.RoundToInt(val * 255f);
                    cols3D[z * (resXY * resXY) + y * resXY + x] = new Color32(bVal, bVal, bVal, 255);
                }
            }
        }

        generatedSquareDither3D.SetPixels32(cols3D);
        generatedSquareDither3D.Apply();

        if (generatedSquareRamp2D == null)
        {
            generatedSquareRamp2D = new Texture2D(resXY, 2, TextureFormat.R8, false);
            generatedSquareRamp2D.wrapMode = TextureWrapMode.Clamp;
            generatedSquareRamp2D.filterMode = FilterMode.Bilinear;
        }

        Color32[] rampCols = new Color32[resXY * 2];
        for (int x = 0; x < resXY; x++)
        {
            byte b = (byte)Mathf.RoundToInt((x / (float)(resXY - 1)) * 255f);
            rampCols[x] = new Color32(b, b, b, 255);
            rampCols[resXY + x] = new Color32(b, b, b, 255);
        }
        generatedSquareRamp2D.SetPixels32(rampCols);
        generatedSquareRamp2D.Apply();
    }

    void BuildCheckerboardMaterial()
    {
        if (generatedCheckerTex == null)
        {
            generatedCheckerTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            generatedCheckerTex.filterMode = FilterMode.Point;
            generatedCheckerTex.wrapMode = TextureWrapMode.Repeat;
            Color32 w = new Color32(255, 255, 255, 255);
            Color32 b = new Color32(0, 0, 0, 255);
            generatedCheckerTex.SetPixels32(new Color32[] { b, w, w, b });
            generatedCheckerTex.Apply();
        }

        if (checkerboardMaterial == null)
        {
            Shader unlitTex = Shader.Find("Unlit/Texture");
            if (unlitTex == null) unlitTex = Shader.Find("Standard");
            checkerboardMaterial = new Material(unlitTex) { name = "MacCheckerboard_Mat" };
        }
        checkerboardMaterial.mainTexture = generatedCheckerTex;
    }

    void EnsureDither3DMaterials()
    {
#if UNITY_EDITOR
        if (dither3DTexture == null)
            dither3DTexture = AssetDatabase.LoadAssetAtPath<Texture3D>("Assets/Dither3D/Dither3D_4x4.asset");
        if (ditherRampTexture == null)
            ditherRampTexture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Dither3D/Dither3D_4x4_Ramp.png");
#endif
        if (useSquareMacintoshDots)
            BuildSquareMacintoshDitherTextures();

        BuildCheckerboardMaterial();

        Shader ditherShader = Shader.Find("Dither 3D/Opaque");
        if (ditherShader == null) ditherShader = Shader.Find("Dither3D/Opaque");
        if (ditherShader == null) ditherShader = Shader.Find("Standard");

        if (whiteWallMaterial == null)
            whiteWallMaterial = new Material(ditherShader) { name = "MazeWall_DitherMat" };
        if (whiteFloorMaterial == null)
            whiteFloorMaterial = new Material(ditherShader) { name = "MazeFloor_DitherMat" };
        if (blackBreadcrumbMaterial == null)
            blackBreadcrumbMaterial = new Material(ditherShader) { name = "Breadcrumb_DitherMat" };
        if (blackEdgeMaterial == null)
            blackEdgeMaterial = new Material(ditherShader) { name = "BlackEdgeOutline_Mat" };

        EnsureNonEuclideanStencilMaterials();
        ApplyAllMaterialProperties();
    }

    void EnsureNonEuclideanStencilMaterials()
    {
        Shader portalShader = Shader.Find("Custom/NonEuclideanPortalMask");
        if (portalShader == null)
        {
            Debug.LogWarning("Custom/NonEuclideanPortalMask shader not found! Make sure NonEuclideanPortalMask.shader is in Assets/Scripts.");
            portalShader = Shader.Find("Unlit/Color");
        }

        if (portalMaskNS_Mat == null)
        {
            portalMaskNS_Mat = new Material(portalShader) { name = "PortalMask_NS_Ref1" };
            if (portalMaskNS_Mat.HasProperty("_StencilRef")) portalMaskNS_Mat.SetInt("_StencilRef", 1);
        }
        if (portalMaskEW_Mat == null)
        {
            portalMaskEW_Mat = new Material(portalShader) { name = "PortalMask_EW_Ref2" };
            if (portalMaskEW_Mat.HasProperty("_StencilRef")) portalMaskEW_Mat.SetInt("_StencilRef", 2);
        }
    }

    Material CreateStencilClone(Material baseMat, string name, int stencilRef)
    {
        Material m = new Material(baseMat) { name = name };
        m.renderQueue = 2010;
        if (m.HasProperty("_StencilRef")) m.SetInt("_StencilRef", stencilRef);
        if (m.HasProperty("_StencilComp")) m.SetInt("_StencilComp", 3); // 3 = Equal
        return m;
    }

    void ApplyAllMaterialProperties()
    {
        ApplyDitherPropertiesToMaterial(whiteWallMaterial, Color.white, lightExposure, lightOffset, 0, 8);
        ApplyDitherPropertiesToMaterial(whiteFloorMaterial, new Color(0.95f, 0.95f, 0.95f), lightExposure, lightOffset, 0, 8);
        ApplyDitherPropertiesToMaterial(blackBreadcrumbMaterial, new Color(0.04f, 0.04f, 0.04f), 0.1f, -0.8f, 0, 8);
        ApplyDitherPropertiesToMaterial(blackEdgeMaterial, Color.black, 0.0f, -2.0f, 0, 8);

        foreach (var kvp in bridgeCells)
        {
            BridgeCellInstance br = kvp.Value;
            int nsRef = br.cellWallNS.HasProperty("_StencilRef") ? br.cellWallNS.GetInt("_StencilRef") : 1;
            int ewRef = br.cellWallEW.HasProperty("_StencilRef") ? br.cellWallEW.GetInt("_StencilRef") : 2;
            int nsComp = br.cellWallNS.HasProperty("_StencilComp") ? br.cellWallNS.GetInt("_StencilComp") : 3;
            int ewComp = br.cellWallEW.HasProperty("_StencilComp") ? br.cellWallEW.GetInt("_StencilComp") : 3;

            ApplyDitherPropertiesToMaterial(br.cellWallNS,  Color.white, lightExposure, lightOffset, nsRef, nsComp);
            ApplyDitherPropertiesToMaterial(br.cellFloorNS, new Color(0.95f, 0.95f, 0.95f), lightExposure, lightOffset, nsRef, nsComp);
            ApplyDitherPropertiesToMaterial(br.cellCrumbNS, new Color(0.04f, 0.04f, 0.04f), 0.1f, -0.8f, nsRef, nsComp);
            ApplyDitherPropertiesToMaterial(br.cellEdgeNS,  Color.black, 0.0f, -2.0f, nsRef, nsComp);

            ApplyDitherPropertiesToMaterial(br.cellWallEW,  Color.white, lightExposure, lightOffset, ewRef, ewComp);
            ApplyDitherPropertiesToMaterial(br.cellFloorEW, new Color(0.95f, 0.95f, 0.95f), lightExposure, lightOffset, ewRef, ewComp);
            ApplyDitherPropertiesToMaterial(br.cellCrumbEW, new Color(0.04f, 0.04f, 0.04f), 0.1f, -0.8f, ewRef, ewComp);
            ApplyDitherPropertiesToMaterial(br.cellEdgeEW,  Color.black, 0.0f, -2.0f, ewRef, ewComp);
        }
    }

    void ApplyDitherPropertiesToMaterial(Material mat, Color baseColor, float exposure, float offset, int stencilRef, int stencilComp)
    {
        if (mat == null) return;
        mat.color = baseColor;

        Texture3D active3DTex = (useSquareMacintoshDots && generatedSquareDither3D != null) ? generatedSquareDither3D : dither3DTexture;
        Texture2D activeRamp  = (useSquareMacintoshDots && generatedSquareRamp2D != null)   ? generatedSquareRamp2D   : ditherRampTexture;

        if (active3DTex != null && mat.HasProperty("_DitherTex"))
            mat.SetTexture("_DitherTex", active3DTex);
        if (activeRamp != null && mat.HasProperty("_DitherRampTex"))
            mat.SetTexture("_DitherRampTex", activeRamp);

        float contrastVal = crisp1BitMacPixels ? Mathf.Max(dotContrast, 3.0f) : dotContrast;

        if (mat.HasProperty("_InputExposure")) mat.SetFloat("_InputExposure", exposure);
        if (mat.HasProperty("_InputOffset")) mat.SetFloat("_InputOffset", offset);
        if (mat.HasProperty("_Scale")) mat.SetFloat("_Scale", dotScale);
        if (mat.HasProperty("_SizeVariability")) mat.SetFloat("_SizeVariability", dotSizeVariability);
        if (mat.HasProperty("_Contrast")) mat.SetFloat("_Contrast", contrastVal);
        if (mat.HasProperty("_StretchSmoothness")) mat.SetFloat("_StretchSmoothness", 1.0f);
        if (mat.HasProperty("_StencilRef")) mat.SetInt("_StencilRef", stencilRef);
        if (mat.HasProperty("_StencilComp")) mat.SetInt("_StencilComp", stencilComp);
    }

    void ConfigureCameraAndGlobalDither()
    {
        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.85f, 0.85f, 0.85f);
        }

        // Disable cast shadows on directional lights so Dither3D uses clean N-dot-L directional shading
        // without Euclidean shadow-map projection artifacts across non-Euclidean portals
        foreach (Light l in FindObjectsOfType<Light>())
        {
            l.shadows = LightShadows.None;
        }

        Shader.EnableKeyword("DITHERCOL_GRAYSCALE");
        Shader.DisableKeyword("DITHERCOL_RGB");
        Shader.DisableKeyword("DITHERCOL_CMYK");

        if (inverseDots) Shader.EnableKeyword("INVERSE_DOTS");
        else Shader.DisableKeyword("INVERSE_DOTS");

        if (quantizeLayers) Shader.EnableKeyword("QUANTIZE_LAYERS");
        else Shader.DisableKeyword("QUANTIZE_LAYERS");

        Shader.EnableKeyword("RADIAL_COMPENSATION");
    }

    void NormalizeMeshUVsToWorldSpacePosition(GameObject go, PrimitiveType type, Vector3 worldPos, Vector3 localScale, float uvPerMeter = 0.5f)
    {
        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        Mesh mesh = Instantiate(mf.sharedMesh);
        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Vector2[] uvs = mesh.uv;

        if (type == PrimitiveType.Cube || type == PrimitiveType.Quad)
        {
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 wp = go.transform.TransformPoint(verts[i]);
                Vector3 wn = go.transform.TransformDirection(normals[i]);
                if (Mathf.Abs(wn.x) > 0.5f)
                    uvs[i] = new Vector2(wp.z, wp.y) * uvPerMeter;
                else if (Mathf.Abs(wn.y) > 0.5f)
                    uvs[i] = new Vector2(wp.x, wp.z) * uvPerMeter;
                else
                    uvs[i] = new Vector2(wp.x, wp.y) * uvPerMeter;
            }
        }
        else if (type == PrimitiveType.Cylinder)
        {
            float circumference = Mathf.PI * localScale.x;
            float heightMeters = localScale.y * 2.0f;
            for (int i = 0; i < uvs.Length; i++)
                uvs[i] = new Vector2(uvs[i].x * circumference, uvs[i].y * heightMeters) * uvPerMeter;
        }
        else if (type == PrimitiveType.Sphere)
        {
            float circumference = Mathf.PI * localScale.x;
            for (int i = 0; i < uvs.Length; i++)
                uvs[i] = new Vector2(uvs[i].x * circumference, uvs[i].y * (circumference * 0.5f)) * uvPerMeter;
        }

        mesh.uv = uvs;
        mf.mesh = mesh;
    }

    public Vector3 CellToWorldCenter(int r, int c, float y = 0f)
    {
        float x = (c - 1) * cellSize;
        float z = -(r - 1) * cellSize;
        return new Vector3(x, y, z);
    }

    public (int r, int c) WorldToCell(Vector3 worldPos)
    {
        int c = Mathf.RoundToInt(worldPos.x / cellSize) + 1;
        int r = Mathf.RoundToInt(-worldPos.z / cellSize) + 1;
        return (r, c);
    }

    bool IsBridgeCell(int r, int c)
    {
        if (r < 1 || r > MazeData.MaxRow || c < 1 || c > MazeData.MaxCol) return false;
        return (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE) & 0x0F) != 0;
    }

    bool IsNarrowBridgeCell(int r, int c)
    {
        if (r < 1 || r > MazeData.MaxRow || c < 1 || c > MazeData.MaxCol) return false;
        return (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BRIDGE) & 0x0F) != 0;
    }

    /* ============================================================================
     * 3D MAZE GEOMETRY BUILDER (SEAMLESS ARCHWAYS + DUAL STENCIL PORTAL TUNNELS)
     * ============================================================================ */
    public void RebuildMazeGeometryOnly()
    {
        // Ensure upper narrow walkway (including its 0.12m floor slab thickness) always sits cleanly above lower archway roofs and edge bars
        upperFloorY = Mathf.Max(upperFloorY, tunnelArchHeight + 0.25f);
        lastBuiltStyle = architectureStyle;
        if (mazeRoot != null) Destroy(mazeRoot);
        mazeRoot = new GameObject("MazeGeometryRoot");
        breadcrumbMeshes.Clear();
        rampBreadcrumbMeshes.Clear();
        stairFloorColliders.Clear();
        bridgeCells.Clear();

        InitFramebuffer(
            MARGIN_X * 2 + MazeData.MaxCol * TILE_STRIDE,
            MARGIN_Y * 2 + MazeData.MaxRow * TILE_STRIDE
        );

        int minR = fillUnoccupiedWithCheckerboard ? 0 : 1;
        int maxR = fillUnoccupiedWithCheckerboard ? MazeData.MaxRow + 1 : MazeData.MaxRow;
        int minC = fillUnoccupiedWithCheckerboard ? 0 : 1;
        int maxC = fillUnoccupiedWithCheckerboard ? MazeData.MaxCol + 1 : MazeData.MaxCol;

        for (int r = minR; r <= maxR; r++)
        {
            for (int c = minC; c <= maxC; c++)
            {
                bool insideGrid = (r >= 1 && r <= MazeData.MaxRow && c >= 1 && c <= MazeData.MaxCol);
                bool isRampCell = insideGrid && MazeData.IsRamp(r, c);
                int wBaseOpen   = insideGrid ? (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) & 0x0F) : 0;
                int wBridgeOpen = insideGrid ? (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE) & 0x0F) : 0;
                int nBaseOpen   = insideGrid ? (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BASE) & 0x0F) : 0;
                int nBridgeOpen = insideGrid ? (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BRIDGE) & 0x0F) : 0;

                if (isRampCell)
                {
                    BuildTaperedRampStairsCell(r, c, wBaseOpen, nBaseOpen);
                    continue;
                }

                // 1. LOWER WIDE PLANE (y = 0m)
                if (wBaseOpen == 0 && wBridgeOpen == 0)
                {
                    if (fillUnoccupiedWithCheckerboard)
                        BuildUnoccupiedCheckerboardCell(r, c);
                }
                else if (wBridgeOpen != 0)
                {
                    BuildSimultaneousNonEuclideanBridgeCell(r, c, wBaseOpen, wBridgeOpen, isNarrowPlane: false);
                }
                else
                {
                    BuildWideCellFloorAndBreadcrumbs(mazeRoot.transform, r, c, AmazingMazeData.LAYER_WIDE_BASE, whiteFloorMaterial, blackBreadcrumbMaterial, true);
                    if (architectureStyle == WallArchitectureStyle.CycladicRounded)
                        BuildCycladicCellWalls(r, c, wBaseOpen);
                }

                // 2. UPPER NARROW PLANE (y = upperFloorY)
                if (nBridgeOpen != 0)
                {
                    BuildSimultaneousNonEuclideanBridgeCell(r, c, nBaseOpen, nBridgeOpen, isNarrowPlane: true);
                }
                else if (nBaseOpen != 0)
                {
                    BuildUpperNarrowCorridorCell(r, c, nBaseOpen);
                }
            }
        }

        if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
        {
            BuildSeamlessFlatWallsWithPhysicalOutlines();
        }

        float markerOffsetY = Mathf.Max(0.55f, wallHeight * 0.65f);
        float startY = GetLayerBaseFloorY(MazeData.StartPos.layer) + markerOffsetY;
        float endY   = GetLayerBaseFloorY(MazeData.EndPos.layer) + markerOffsetY;
        startMarkerRenderer = SpawnMarkerPrimitive(PrimitiveType.Sphere, MazeData.StartPos.r, MazeData.StartPos.c, startY, "StartSphere_Marker");
        endMarkerRenderer   = SpawnMarkerPrimitive(PrimitiveType.Cube, MazeData.EndPos.r, MazeData.EndPos.c, endY, "GoalCube_Marker");
    }

    void BuildSimultaneousNonEuclideanBridgeCell(int r, int c, int baseOpenMask, int bridgeOpenMask, bool isNarrowPlane)
    {
        float baseFloorY = isNarrowPlane ? upperFloorY : 0f;
        int planeBaseLayer = isNarrowPlane ? AmazingMazeData.LAYER_NARROW_BASE : AmazingMazeData.LAYER_WIDE_BASE;
        int planeBridgeLayer = isNarrowPlane ? AmazingMazeData.LAYER_NARROW_BRIDGE : AmazingMazeData.LAYER_WIDE_BRIDGE;

        Vector3 center = CellToWorldCenter(r, c, baseFloorY);
        string prefix = isNarrowPlane ? "UpperNarrowBridgeCell" : "NonEuclideanBridgeCell";
        GameObject bridgeRoot = new GameObject($"{prefix}_{r}_{c}");
        bridgeRoot.transform.SetParent(mazeRoot.transform, false);
        bridgeRoot.transform.position = center;

        float wt = isNarrowPlane ? narrowWallThickness : wallThickness;
        float halfWT = wt * 0.5f;
        float innerSpan = isNarrowPlane ? narrowCorridorWidth : (cellSize - wt);
        float half = (innerSpan + wt) * 0.5f;
        float th = isNarrowPlane ? narrowTunnelArchHeight : tunnelArchHeight;
        float roofThick = isNarrowPlane ? 0.20f : 0.24f;
        float doorH = th - roofThick;
        float fullTunnelLen = innerSpan + 2f * wt;
        float ew = blackEdgeOutlineWidth;

        GameObject baseColliderFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseColliderFloor.name = "BridgeTeleportFloor";
        baseColliderFloor.transform.SetParent(bridgeRoot.transform, false);
        float floorSlabThick = isNarrowPlane ? 0.12f : 0.1f;
        baseColliderFloor.transform.localPosition = new Vector3(0f, -(floorSlabThick * 0.5f + 0.002f), 0f);
        Vector3 floorScale = isNarrowPlane ? new Vector3(innerSpan, floorSlabThick, innerSpan) : new Vector3(cellSize, floorSlabThick, cellSize);
        baseColliderFloor.transform.localScale = floorScale;
        NormalizeMeshUVsToWorldSpacePosition(baseColliderFloor, PrimitiveType.Cube, center + new Vector3(0f, -(floorSlabThick * 0.5f + 0.002f), 0f), floorScale);
        baseColliderFloor.GetComponent<Renderer>().sharedMaterial = whiteFloorMaterial;
        ConfigureFloorTeleportationArea(baseColliderFloor);

        bool baseIsNS = (baseOpenMask & ((1 << AmazingMazeData.DIR_UP) | (1 << AmazingMazeData.DIR_DOWN))) != 0;
        int nsSubLayer = baseIsNS ? planeBaseLayer : planeBridgeLayer;
        int ewSubLayer = baseIsNS ? planeBridgeLayer : planeBaseLayer;

        int bridgeIndex = bridgeCells.Count;
        int nsStencilRef = ((bridgeIndex * 2) % 250) + 1;
        int ewStencilRef = nsStencilRef + 1;

        var brInfo = new BridgeCellInstance
        {
            r = r,
            c = c,
            planeBaseLayer = planeBaseLayer,
            baseFloorY = baseFloorY,
            archHalfSpan = half,
            archWallThick = wt,
            worldCenter = center,
            nsSubLayer = nsSubLayer,
            ewSubLayer = ewSubLayer,
            cellPortalNS = new Material(portalMaskNS_Mat) { name = $"PortalNS_{r}_{c}_P{planeBaseLayer}_Ref{nsStencilRef}" },
            cellPortalEW = new Material(portalMaskEW_Mat) { name = $"PortalEW_{r}_{c}_P{planeBaseLayer}_Ref{ewStencilRef}" },
            cellWallNS = CreateStencilClone(whiteWallMaterial, $"WallNS_{r}_{c}_P{planeBaseLayer}", nsStencilRef),
            cellFloorNS = CreateStencilClone(whiteFloorMaterial, $"FloorNS_{r}_{c}_P{planeBaseLayer}", nsStencilRef),
            cellCrumbNS = CreateStencilClone(blackBreadcrumbMaterial, $"CrumbNS_{r}_{c}_P{planeBaseLayer}", nsStencilRef),
            cellEdgeNS = CreateStencilClone(blackEdgeMaterial, $"EdgeNS_{r}_{c}_P{planeBaseLayer}", nsStencilRef),
            cellWallEW = CreateStencilClone(whiteWallMaterial, $"WallEW_{r}_{c}_P{planeBaseLayer}", ewStencilRef),
            cellFloorEW = CreateStencilClone(whiteFloorMaterial, $"FloorEW_{r}_{c}_P{planeBaseLayer}", ewStencilRef),
            cellCrumbEW = CreateStencilClone(blackBreadcrumbMaterial, $"CrumbEW_{r}_{c}_P{planeBaseLayer}", ewStencilRef),
            cellEdgeEW = CreateStencilClone(blackEdgeMaterial, $"EdgeEW_{r}_{c}_P{planeBaseLayer}", ewStencilRef)
        };
        brInfo.cellPortalNS.SetInt("_StencilRef", nsStencilRef);
        brInfo.cellPortalEW.SetInt("_StencilRef", ewStencilRef);

        bridgeCells[(r, c, planeBaseLayer)] = brInfo;

        // 1. Outward-Facing Stencil Portal Windows at the outer face of the archway (`half + halfWT`)
        float outerPortalDist = half + halfWT;
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_North_Ref{nsStencilRef}", new Vector3(0f, doorH * 0.5f, outerPortalDist), new Vector3(innerSpan, doorH, 1f), Quaternion.Euler(0f, 180f, 0f), brInfo.cellPortalNS));
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_South_Ref{nsStencilRef}", new Vector3(0f, doorH * 0.5f, -outerPortalDist), new Vector3(innerSpan, doorH, 1f), Quaternion.identity, brInfo.cellPortalNS));
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_East_Ref{ewStencilRef}", new Vector3(outerPortalDist, doorH * 0.5f, 0f), new Vector3(innerSpan, doorH, 1f), Quaternion.Euler(0f, -90f, 0f), brInfo.cellPortalEW));
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_West_Ref{ewStencilRef}", new Vector3(-outerPortalDist, doorH * 0.5f, 0f), new Vector3(innerSpan, doorH, 1f), Quaternion.Euler(0f, 90f, 0f), brInfo.cellPortalEW));

        // 2. Pillars & Roof
        if (architectureStyle == WallArchitectureStyle.CycladicRounded)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 pillarCenter = center + new Vector3(sx * half, th * 0.5f, sz * half);
                    GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Destroy(post.GetComponent<Collider>());
                    post.transform.SetParent(bridgeRoot.transform, false);
                    post.transform.position = pillarCenter;
                    Vector3 postScale = new Vector3(wt, th * 0.5f, wt);
                    post.transform.localScale = postScale;
                    NormalizeMeshUVsToWorldSpacePosition(post, PrimitiveType.Cylinder, pillarCenter, postScale);
                    post.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;
                }
            }
            BuildCycladicCrossVaultRoof(bridgeRoot.transform, center, th, innerSpan, fullTunnelLen);
        }
        else
        {
            // Flat-Edged Roof Slab
            float fullWidth = fullTunnelLen;
            Vector3 roofCenter = center + new Vector3(0f, th - roofThick * 0.5f, 0f);
            Vector3 roofScale = new Vector3(fullWidth, roofThick, fullWidth);
            CreateWallBoxWithMaterial(bridgeRoot.transform, "SharedArchRoof", roofCenter, roofScale, whiteWallMaterial);

            float rHalf = fullWidth * 0.5f;
            // Top outer roof perimeter bars
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, th, rHalf), new Vector3(fullWidth + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, th, -rHalf), new Vector3(fullWidth + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(rHalf, th, 0f), new Vector3(ew, ew, fullWidth + ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(-rHalf, th, 0f), new Vector3(ew, ew, fullWidth + ew), blackEdgeMaterial);

            // Outer bottom lintel bars above the 4 doorway openings
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, doorH, half + halfWT), new Vector3(innerSpan + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, doorH, -(half + halfWT)), new Vector3(innerSpan + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(half + halfWT, doorH, 0f), new Vector3(ew, ew, innerSpan + ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(-(half + halfWT), doorH, 0f), new Vector3(ew, ew, innerSpan + ew), blackEdgeMaterial);

            if (isNarrowPlane)
            {
                BuildUpperNarrowBridgePillarsAndApproachArms(bridgeRoot.transform, center, half, wt, doorH, th, innerSpan);
            }
        }

        if (isNarrowPlane && architectureStyle == WallArchitectureStyle.CycladicRounded)
        {
            BuildUpperNarrowBridgePillarsAndApproachArms(bridgeRoot.transform, center, half, wt, doorH, th, innerSpan);
        }

        // 3. Build NORTH-SOUTH Tunnel Interior (Inset by 0.004m so it sits cleanly in front of the pillar inner face with ZERO z-fighting!)
        BuildTunnelStencilFloorAndBreadcrumbs(bridgeRoot.transform, r, c, nsSubLayer, baseFloorY, fullTunnelLen, isNarrowPlane, brInfo.cellFloorNS, brInfo.cellCrumbNS);
        BuildInwardStencilTunnelWalls(bridgeRoot.transform, center, alongZ: true, doorH, innerSpan - 0.008f, fullTunnelLen, wt, brInfo.cellWallNS, brInfo.cellEdgeNS, brInfo.nsWallColliders);

        // 4. Build EAST-WEST Tunnel Interior (Inset by 0.004m so it sits cleanly in front of the pillar inner face with ZERO z-fighting!)
        BuildTunnelStencilFloorAndBreadcrumbs(bridgeRoot.transform, r, c, ewSubLayer, baseFloorY, fullTunnelLen, isNarrowPlane, brInfo.cellFloorEW, brInfo.cellCrumbEW);
        BuildInwardStencilTunnelWalls(bridgeRoot.transform, center, alongZ: false, doorH, innerSpan - 0.008f, fullTunnelLen, wt, brInfo.cellWallEW, brInfo.cellEdgeEW, brInfo.ewWallColliders);
    }

    void BuildUpperNarrowBridgePillarsAndApproachArms(Transform parent, Vector3 center, float half, float nwt, float doorH, float th, float innerSpan)
    {
        float halfWT = nwt * 0.5f;
        float outerHW = half + halfWT;
        float halfCell = cellSize * 0.5f;
        float armLen = Mathf.Max(0.05f, halfCell - outerHW);
        float armMid = outerHW + armLen * 0.5f;
        float nwh = narrowWallHeight;
        float ew = blackEdgeOutlineWidth;
        float floorThick = 0.12f;
        float fullWallH = nwh + floorThick;
        float wallMidY = (nwh - floorThick) * 0.5f;
        float pillarH = doorH + floorThick;
        float pillarMidY = (doorH - floorThick) * 0.5f;

        if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 pPos = center + new Vector3(sx * half, 0f, sz * half);
                    CreateWallBoxWithMaterial(parent, $"NarrowArchPillar_{sx}_{sz}", pPos + new Vector3(0f, pillarMidY, 0f), new Vector3(nwt, pillarH, nwt), whiteWallMaterial);

                    // Transverse horizontal edge bars at height nwh where low approach walls butt against the pillar
                    CreateBlackEdgeBarWithMaterial(parent, pPos + new Vector3(0f, nwh, sz * halfWT), new Vector3(nwt + ew, ew, ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(parent, pPos + new Vector3(sx * halfWT, nwh, 0f), new Vector3(ew, ew, nwt + ew), blackEdgeMaterial);

                    // Exterior bottom edge bars at -floorThick so the bottom outline continues seamlessly across the pillar
                    CreateBlackEdgeBarWithMaterial(parent, pPos + new Vector3(0f, -floorThick, sz * halfWT), new Vector3(nwt + ew, ew, ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(parent, pPos + new Vector3(sx * halfWT, -floorThick, 0f), new Vector3(ew, ew, nwt + ew), blackEdgeMaterial);

                    // Outer vertical corner bar extending from -floorThick all the way to the roof top (th)
                    float fullCornerH = th + floorThick;
                    float fullCornerMidY = (th - floorThick) * 0.5f;
                    CreateBlackEdgeBarWithMaterial(parent, pPos + new Vector3(sx * halfWT, fullCornerMidY, sz * halfWT), new Vector3(ew, fullCornerH + ew, ew), blackEdgeMaterial);

                    // Vertical doorway jamb bars above the approach wall (nwh .. doorH)
                    float jambSegH = doorH - nwh;
                    float jambMidY = nwh + jambSegH * 0.5f;
                    CreateBlackEdgeBarWithMaterial(parent, pPos + new Vector3(sx * halfWT, jambMidY, -sz * halfWT), new Vector3(ew, jambSegH + ew, ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(parent, pPos + new Vector3(-sx * halfWT, jambMidY, sz * halfWT), new Vector3(ew, jambSegH + ew, ew), blackEdgeMaterial);
                }
            }
        }

        float armFloorLen = halfCell - (half - halfWT);
        float armFloorMid = (half - halfWT) + armFloorLen * 0.5f;

        for (int dir = 0; dir < 4; dir++)
        {
            bool alongZ = (dir == AmazingMazeData.DIR_UP || dir == AmazingMazeData.DIR_DOWN);
            float sign = (dir == AmazingMazeData.DIR_UP || dir == AmazingMazeData.DIR_RIGHT) ? 1f : -1f;

            Vector3 armCenter = center + (alongZ ? new Vector3(0f, 0f, sign * armMid) : new Vector3(sign * armMid, 0f, 0f));
            Vector3 armFloorPos = center + (alongZ ? new Vector3(0f, -floorThick * 0.5f, sign * armFloorMid) : new Vector3(sign * armFloorMid, -floorThick * 0.5f, 0f));
            Vector3 fScale = alongZ ? new Vector3(innerSpan, floorThick, armFloorLen) : new Vector3(armFloorLen, floorThick, innerSpan);

            GameObject armFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            armFloor.name = $"NarrowBridgeApproachFloor_{dir}";
            armFloor.transform.SetParent(parent, false);
            armFloor.transform.position = armFloorPos;
            armFloor.transform.localScale = fScale;
            NormalizeMeshUVsToWorldSpacePosition(armFloor, PrimitiveType.Cube, armFloorPos, fScale);
            armFloor.GetComponent<Renderer>().sharedMaterial = whiteFloorMaterial;
            ConfigureFloorTeleportationArea(armFloor);

            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 wallPos = armCenter + (alongZ ? new Vector3(side * half, wallMidY, 0f) : new Vector3(0f, wallMidY, side * half));
                Vector3 wallScale = alongZ ? new Vector3(nwt, fullWallH, armLen) : new Vector3(armLen, fullWallH, nwt);
                CreateWallBoxWithMaterial(parent, "NarrowApproachWall", wallPos, wallScale, whiteWallMaterial);

                Vector3 colPos = armCenter + (alongZ ? new Vector3(side * half, 1.5f, 0f) : new Vector3(0f, 1.5f, side * half));
                CreateTunnelCollider(parent, "NarrowApproachCol", colPos, alongZ ? new Vector3(nwt, 3.0f, armLen) : new Vector3(armLen, 3.0f, nwt));

                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    Vector3 barScale = alongZ ? new Vector3(ew, ew, armLen + ew) : new Vector3(armLen + ew, ew, ew);
                    float innerCoord = side * (half - halfWT);
                    float outerCoord = side * outerHW;

                    // Inner walkway face: top bar at nwh and floor crease bar at ew * 0.5f
                    Vector3 innerTop = armCenter + (alongZ ? new Vector3(innerCoord, nwh, 0f) : new Vector3(0f, nwh, innerCoord));
                    Vector3 innerBot = armCenter + (alongZ ? new Vector3(innerCoord, ew * 0.5f, 0f) : new Vector3(0f, ew * 0.5f, innerCoord));
                    CreateBlackEdgeBarWithMaterial(parent, innerTop, barScale, blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(parent, innerBot, barScale, blackEdgeMaterial);

                    // Outer exterior face: top bar at nwh and bottom bar at -floorThick (NO mid-seam bar at y=0!)
                    Vector3 outerTop = armCenter + (alongZ ? new Vector3(outerCoord, nwh, 0f) : new Vector3(0f, nwh, outerCoord));
                    Vector3 outerBot = armCenter + (alongZ ? new Vector3(outerCoord, -floorThick, 0f) : new Vector3(0f, -floorThick, outerCoord));
                    CreateBlackEdgeBarWithMaterial(parent, outerTop, barScale, blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(parent, outerBot, barScale, blackEdgeMaterial);
                }
            }
        }
    }

    void BuildTunnelStencilFloorAndBreadcrumbs(Transform parent, int r, int c, int subLayer, float baseFloorY, float tunnelFloorSpan, bool isNarrowPlane, Material stencilFloorMat, Material stencilCrumbMat)
    {
        Vector3 center = CellToWorldCenter(r, c, baseFloorY);
        GameObject cellObj = new GameObject($"TunnelFloorAndCrumbs_{r}_{c}_Sub{subLayer}");
        cellObj.transform.SetParent(parent, false);
        cellObj.transform.position = center;

        GameObject floorQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        floorQuad.name = "StencilTunnelFloor";
        Destroy(floorQuad.GetComponent<Collider>());
        floorQuad.transform.SetParent(cellObj.transform, false);
        floorQuad.transform.localPosition = new Vector3(0f, 0.002f, 0f);
        floorQuad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        Vector3 fScale = new Vector3(tunnelFloorSpan, tunnelFloorSpan, 1f);
        floorQuad.transform.localScale = fScale;
        NormalizeMeshUVsToWorldSpacePosition(floorQuad, PrimitiveType.Quad, center + new Vector3(0f, 0.002f, 0f), fScale);
        floorQuad.GetComponent<Renderer>().sharedMaterial = stencilFloorMat;

        float crumbWidth = isNarrowPlane ? (cellSize * 0.25f) : (cellSize * 0.625f);
        float stubLen = (cellSize - crumbWidth) * 0.5f;
        float stubOffset = (crumbWidth + stubLen) * 0.5f;
        float crumbY = 0.008f;

        GameObject[] crumbs = new GameObject[5];
        crumbs[4] = CreateCrumbPiece(cellObj.transform, "Crumb_Center", center, Vector3.up * crumbY, new Vector3(crumbWidth, 0.012f, crumbWidth), stencilCrumbMat);

        if (!isNarrowPlane)
        {
            crumbs[AmazingMazeData.DIR_LEFT] = CreateCrumbPiece(cellObj.transform, "Crumb_W", center, new Vector3(-stubOffset, crumbY, 0f), new Vector3(stubLen, 0.012f, crumbWidth), stencilCrumbMat);
            crumbs[AmazingMazeData.DIR_DOWN] = CreateCrumbPiece(cellObj.transform, "Crumb_S", center, new Vector3(0f, crumbY, -stubOffset), new Vector3(crumbWidth, 0.012f, stubLen), stencilCrumbMat);
            crumbs[AmazingMazeData.DIR_RIGHT] = CreateCrumbPiece(cellObj.transform, "Crumb_E", center, new Vector3(stubOffset, crumbY, 0f), new Vector3(stubLen, 0.012f, crumbWidth), stencilCrumbMat);
            crumbs[AmazingMazeData.DIR_UP] = CreateCrumbPiece(cellObj.transform, "Crumb_N", center, new Vector3(0f, crumbY, stubOffset), new Vector3(crumbWidth, 0.012f, stubLen), stencilCrumbMat);
        }
        else
        {
            // On the upper narrow plane, the archway portal quads sit at +/- (tunnelFloorSpan * 0.5f) = +/- 0.87m,
            // while the approach arms extend from 0.87m to 1.50m (cellSize * 0.5f) outside the stencil portal.
            // Split each directional stub at the portal plane so the inner part uses stencilCrumbMat and the outer approach arm uses blackBreadcrumbMaterial!
            float innerStubLen = (tunnelFloorSpan - crumbWidth) * 0.5f;
            float innerOffset = (crumbWidth + innerStubLen) * 0.5f;
            float outerArmLen = (cellSize - tunnelFloorSpan) * 0.5f;
            float outerOffset = (tunnelFloorSpan + outerArmLen) * 0.5f;

            GameObject CreateSplitNarrowBridgeCrumb(string name, Vector3 dirVec, bool alongZ)
            {
                GameObject group = new GameObject(name);
                group.transform.SetParent(cellObj.transform, false);
                group.transform.localPosition = Vector3.zero;

                Vector3 innerPos = dirVec * innerOffset + Vector3.up * crumbY;
                Vector3 innerScale = alongZ ? new Vector3(crumbWidth, 0.012f, innerStubLen) : new Vector3(innerStubLen, 0.012f, crumbWidth);
                GameObject innerPiece = CreateCrumbPiece(group.transform, "InnerStencilStub", center, innerPos, innerScale, stencilCrumbMat);
                innerPiece.SetActive(true);

                Vector3 outerPos = dirVec * outerOffset + Vector3.up * crumbY;
                Vector3 outerScale = alongZ ? new Vector3(crumbWidth, 0.015f, outerArmLen) : new Vector3(outerArmLen, 0.015f, crumbWidth);
                GameObject outerPiece = CreateCrumbPiece(group.transform, "OuterApproachStub", center, outerPos, outerScale, blackBreadcrumbMaterial);
                outerPiece.SetActive(true);

                group.SetActive(false);
                return group;
            }

            crumbs[AmazingMazeData.DIR_LEFT]  = CreateSplitNarrowBridgeCrumb("Crumb_W", Vector3.left, false);
            crumbs[AmazingMazeData.DIR_DOWN]  = CreateSplitNarrowBridgeCrumb("Crumb_S", Vector3.back, true);
            crumbs[AmazingMazeData.DIR_RIGHT] = CreateSplitNarrowBridgeCrumb("Crumb_E", Vector3.right, false);
            crumbs[AmazingMazeData.DIR_UP]    = CreateSplitNarrowBridgeCrumb("Crumb_N", Vector3.forward, true);
        }

        breadcrumbMeshes[(r, c, subLayer)] = crumbs;
    }

    void BuildInwardStencilTunnelWalls(Transform parent, Vector3 cellCenter, bool alongZ, float doorH, float innerSpan, float tunnelLength, float wt, Material stencilWallMat, Material stencilEdgeMat, List<Collider> outColliders)
    {
        float halfInner = innerSpan * 0.5f;
        float halfCol = (innerSpan + wt) * 0.5f;
        float ew = blackEdgeOutlineWidth;

        if (alongZ)
        {
            CreateInwardWallQuad(parent, "NS_EastInnerWall_Ref", cellCenter + new Vector3(halfInner, doorH * 0.5f, 0f), new Vector3(tunnelLength, doorH, 1f), Quaternion.Euler(0f, 90f, 0f), stencilWallMat);
            CreateInwardWallQuad(parent, "NS_WestInnerWall_Ref", cellCenter + new Vector3(-halfInner, doorH * 0.5f, 0f), new Vector3(tunnelLength, doorH, 1f), Quaternion.Euler(0f, -90f, 0f), stencilWallMat);

            if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
            {
                // Horizontal floor & ceiling edge bars along the NS tunnel walls (NO vertical bars inside the tunnel!)
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(halfInner, ew * 0.5f, 0f), new Vector3(ew, ew, tunnelLength + ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(-halfInner, ew * 0.5f, 0f), new Vector3(ew, ew, tunnelLength + ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(halfInner, doorH, 0f), new Vector3(ew, ew, tunnelLength + ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(-halfInner, doorH, 0f), new Vector3(ew, ew, tunnelLength + ew), stencilEdgeMat);
            }

            outColliders.Add(CreateTunnelCollider(parent, "NS_EastCol", cellCenter + new Vector3(halfCol, 1.05f, 0f), new Vector3(wt, 2.10f, tunnelLength)));
            outColliders.Add(CreateTunnelCollider(parent, "NS_WestCol", cellCenter + new Vector3(-halfCol, 1.05f, 0f), new Vector3(wt, 2.10f, tunnelLength)));
        }
        else
        {
            CreateInwardWallQuad(parent, "EW_NorthInnerWall_Ref", cellCenter + new Vector3(0f, doorH * 0.5f, halfInner), new Vector3(tunnelLength, doorH, 1f), Quaternion.identity, stencilWallMat);
            CreateInwardWallQuad(parent, "EW_SouthInnerWall_Ref", cellCenter + new Vector3(0f, doorH * 0.5f, -halfInner), new Vector3(tunnelLength, doorH, 1f), Quaternion.Euler(0f, 180f, 0f), stencilWallMat);

            if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
            {
                // Horizontal floor & ceiling edge bars along the EW tunnel walls (NO vertical bars inside the tunnel!)
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, ew * 0.5f, halfInner), new Vector3(tunnelLength + ew, ew, ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, ew * 0.5f, -halfInner), new Vector3(tunnelLength + ew, ew, ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, doorH, halfInner), new Vector3(tunnelLength + ew, ew, ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, doorH, -halfInner), new Vector3(tunnelLength + ew, ew, ew), stencilEdgeMat);
            }

            outColliders.Add(CreateTunnelCollider(parent, "EW_NorthCol", cellCenter + new Vector3(0f, 1.05f, halfCol), new Vector3(tunnelLength, 2.10f, wt)));
            outColliders.Add(CreateTunnelCollider(parent, "EW_SouthCol", cellCenter + new Vector3(0f, 1.05f, -halfCol), new Vector3(tunnelLength, 2.10f, wt)));
        }
    }

    Renderer CreateInwardWallQuad(Transform parent, string name, Vector3 worldPos, Vector3 scale, Quaternion rot, Material mat)
    {
        GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = name;
        Destroy(q.GetComponent<Collider>());
        q.transform.SetParent(parent, false);
        q.transform.position = worldPos;
        q.transform.rotation = rot;
        q.transform.localScale = scale;
        NormalizeMeshUVsToWorldSpacePosition(q, PrimitiveType.Quad, worldPos, scale);
        Renderer r = q.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        return r;
    }

    Collider CreateTunnelCollider(Transform parent, string name, Vector3 worldPos, Vector3 size)
    {
        GameObject colObj = new GameObject(name);
        colObj.transform.SetParent(parent, false);
        colObj.transform.position = worldPos;
        BoxCollider bc = colObj.AddComponent<BoxCollider>();
        bc.size = size;
        return bc;
    }

    void BuildCycladicCrossVaultRoof(Transform parent, Vector3 cellCenter, float th, float innerSpan, float fullLen)
    {
        int archSegments = 7;
        float archRadius = innerSpan * 0.5f;
        float springY = th - 0.35f;

        for (int axis = 0; axis < 2; axis++)
        {
            bool alongZ = (axis == 0);
            for (int i = 0; i < archSegments; i++)
            {
                float t0 = (i / (float)archSegments) * Mathf.PI;
                float t1 = ((i + 1) / (float)archSegments) * Mathf.PI;
                float tMid = (t0 + t1) * 0.5f;

                float xOff = Mathf.Cos(tMid) * archRadius;
                float yOff = Mathf.Sin(tMid) * (archRadius * 0.5f);
                float segWidth = (Mathf.PI * archRadius / archSegments) * 1.15f;
                float tiltDeg = (tMid * Mathf.Rad2Deg) - 90f;

                GameObject archSeg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(archSeg.GetComponent<Collider>());
                archSeg.transform.SetParent(parent, false);

                Vector3 segWorldPos = cellCenter + (alongZ ? new Vector3(xOff, springY + yOff, 0f) : new Vector3(0f, springY + yOff, xOff));
                archSeg.transform.position = segWorldPos;
                archSeg.transform.rotation = alongZ ? Quaternion.Euler(0f, 0f, tiltDeg) : Quaternion.Euler(-tiltDeg, 0f, 0f);
                Vector3 segScale = alongZ ? new Vector3(segWidth, 0.18f, fullLen) : new Vector3(fullLen, 0.18f, segWidth);
                archSeg.transform.localScale = segScale;
                NormalizeMeshUVsToWorldSpacePosition(archSeg, PrimitiveType.Cube, segWorldPos, segScale);
                archSeg.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;
            }
        }
    }

    GameObject CreatePortalQuad(Transform parent, string name, Vector3 localPos, Vector3 scale, Quaternion localRot, Material mat)
    {
        GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = name;
        Destroy(q.GetComponent<Collider>());
        q.transform.SetParent(parent, false);
        q.transform.localPosition = localPos;
        q.transform.localRotation = localRot;
        q.transform.localScale = scale;
        Renderer r = q.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        return q;
    }

    void BuildSeamlessFlatWallsWithPhysicalOutlines()
    {
        int R = MazeData.MaxRow;
        int C = MazeData.MaxCol;

        bool[,] hWall = new bool[R + 2, C + 2];
        bool[,] vWall = new bool[R + 2, C + 2];
        bool[,] rampStubN = new bool[R + 2, C + 2];
        bool[,] rampStubS = new bool[R + 2, C + 2];
        bool[,] rampStubW = new bool[R + 2, C + 2];
        bool[,] rampStubE = new bool[R + 2, C + 2];

        bool IsOpenInDir(int r, int c, int dir)
        {
            if (r < 1 || r > R || c < 1 || c > C) return false;
            int combined = (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) |
                            MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE)) & 0x0F;
            return (combined & (1 << dir)) != 0;
        }

        bool IsAnyWideCarved(int r, int c)
        {
            if (r < 1 || r > R || c < 1 || c > C) return false;
            if (MazeData.IsRamp(r, c)) return false;
            return ((MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) |
                     MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE)) & 0x0F) != 0;
        }

        for (int r = 1; r <= R; r++)
        {
            for (int c = 1; c <= C; c++)
            {
                if (MazeData.IsRamp(r, c))
                {
                    int wOpen = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) & 0x0F;
                    if ((wOpen & (1 << AmazingMazeData.DIR_UP)) != 0)
                    {
                        rampStubS[r - 1, c - 1] = true;
                        rampStubS[r - 1, c] = true;
                    }
                    else if ((wOpen & (1 << AmazingMazeData.DIR_DOWN)) != 0)
                    {
                        rampStubN[r, c - 1] = true;
                        rampStubN[r, c] = true;
                    }
                    else if ((wOpen & (1 << AmazingMazeData.DIR_LEFT)) != 0)
                    {
                        rampStubE[r - 1, c - 1] = true;
                        rampStubE[r, c - 1] = true;
                    }
                    else if ((wOpen & (1 << AmazingMazeData.DIR_RIGHT)) != 0)
                    {
                        rampStubW[r - 1, c] = true;
                        rampStubW[r, c] = true;
                    }
                    continue;
                }
                if (!IsAnyWideCarved(r, c)) continue;
                if (!IsOpenInDir(r, c, AmazingMazeData.DIR_UP)) hWall[r - 1, c] = true;
                if (!IsOpenInDir(r, c, AmazingMazeData.DIR_DOWN)) hWall[r, c] = true;
                if (!IsOpenInDir(r, c, AmazingMazeData.DIR_LEFT)) vWall[r, c - 1] = true;
                if (!IsOpenInDir(r, c, AmazingMazeData.DIR_RIGHT)) vWall[r, c] = true;
            }
        }

        GameObject wallsRoot = new GameObject("SeamlessFlatWalls");
        wallsRoot.transform.SetParent(mazeRoot.transform, false);

        float wt = wallThickness;
        float halfWT = wt * 0.5f;
        float wh = wallHeight;
        float th = tunnelArchHeight;
        float doorH = th - 0.24f;
        float ew = blackEdgeOutlineWidth;
        float spanLen = cellSize - wt;

        Vector3 VertexWorldPos(int vr, int vc)
        {
            float vx = (vc - 0.5f) * cellSize;
            float vz = -(vr - 0.5f) * cellSize;
            return new Vector3(vx, 0f, vz);
        }

        for (int r = 0; r <= R; r++)
        {
            for (int c = 1; c <= C; c++)
            {
                if (!hWall[r, c]) continue;
                Vector3 vLeft = VertexWorldPos(r, c - 1);
                Vector3 vRight = VertexWorldPos(r, c);
                Vector3 mid = (vLeft + vRight) * 0.5f;

                Vector3 scale = new Vector3(spanLen, wh, wt);
                Vector3 bodyCenter = mid + new Vector3(0f, wh * 0.5f, 0f);
                CreateWallBoxWithMaterial(wallsRoot.transform, "HWallSpan", bodyCenter, scale, whiteWallMaterial);

                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(0f, wh, halfWT), new Vector3(spanLen + ew, ew, ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(0f, wh, -halfWT), new Vector3(spanLen + ew, ew, ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(0f, ew * 0.5f, halfWT), new Vector3(spanLen + ew, ew, ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(0f, ew * 0.5f, -halfWT), new Vector3(spanLen + ew, ew, ew), blackEdgeMaterial);

                CreateWallColliderWorld(wallsRoot.transform, mid, new Vector3(cellSize, 4.5f, wt));
            }
        }

        for (int r = 1; r <= R; r++)
        {
            for (int c = 0; c <= C; c++)
            {
                if (!vWall[r, c]) continue;
                Vector3 vTop = VertexWorldPos(r - 1, c);
                Vector3 vBot = VertexWorldPos(r, c);
                Vector3 mid = (vTop + vBot) * 0.5f;

                Vector3 scale = new Vector3(wt, wh, spanLen);
                Vector3 bodyCenter = mid + new Vector3(0f, wh * 0.5f, 0f);
                CreateWallBoxWithMaterial(wallsRoot.transform, "VWallSpan", bodyCenter, scale, whiteWallMaterial);

                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(halfWT, wh, 0f), new Vector3(ew, ew, spanLen + ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(-halfWT, wh, 0f), new Vector3(ew, ew, spanLen + ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, spanLen + ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(wallsRoot.transform, mid + new Vector3(-halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, spanLen + ew), blackEdgeMaterial);

                CreateWallColliderWorld(wallsRoot.transform, mid, new Vector3(wt, 4.5f, cellSize));
            }
        }

        // Build all vertex posts (both normal 0.85m posts AND 2.01m Archway Pillars) using exact hWall/vWall topology!
        for (int vr = 0; vr <= R; vr++)
        {
            for (int vc = 0; vc <= C; vc++)
            {
                // Which of the 4 cells surrounding vertex (vr, vc) is a Bridge Cell?
                bool brNW = IsBridgeCell(vr, vc);
                bool brNE = IsBridgeCell(vr, vc + 1);
                bool brSW = IsBridgeCell(vr + 1, vc);
                bool brSE = IsBridgeCell(vr + 1, vc + 1);
                bool isBridgeCorner = brNW || brNE || brSW || brSE;

                bool hasN = ((vr >= 1) && vWall[vr, vc]) || rampStubN[vr, vc];
                bool hasS = ((vr < R) && vWall[vr + 1, vc]) || rampStubS[vr, vc];
                bool hasW = ((vc >= 1) && hWall[vr, vc]) || rampStubW[vr, vc];
                bool hasE = ((vc < C) && hWall[vr, vc + 1]) || rampStubE[vr, vc];

                if (!isBridgeCorner && !hasN && !hasS && !hasW && !hasE) continue;

                Vector3 vPos = VertexWorldPos(vr, vc);

                if (!isBridgeCorner)
                {
                    // Normal 0.85m Corner Post
                    Vector3 postScale = new Vector3(wt, wh, wt);
                    CreateWallBoxWithMaterial(wallsRoot.transform, $"CornerPost_{vr}_{vc}", vPos + new Vector3(0f, wh * 0.5f, 0f), postScale, whiteWallMaterial);

                    if (!hasN)
                    {
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, wh, halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    }
                    if (!hasS)
                    {
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, wh, -halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, -halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    }
                    if (!hasE)
                    {
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, wh, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                    }
                    if (!hasW)
                    {
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, wh, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                    }

                    if (hasN == hasE)
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, wh * 0.5f, halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
                    if (hasN == hasW)
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, wh * 0.5f, halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
                    if (hasS == hasE)
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, wh * 0.5f, -halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
                    if (hasS == hasW)
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, wh * 0.5f, -halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
                }
                else
                {
                    // ARCHWAY PILLAR AT BRIDGE VERTEX (vr, vc):
                    // Height goes from 0m to doorH (2.01m), where it meets the bottom of SharedArchRoof!
                    Vector3 pillarScale = new Vector3(wt, doorH, wt);
                    CreateWallBoxWithMaterial(wallsRoot.transform, $"ArchPillar_{vr}_{vc}", vPos + new Vector3(0f, doorH * 0.5f, 0f), pillarScale, whiteWallMaterial);

                    // 1. Transverse horizontal edge bar at height wh (0.85m) where a low wall's top surface
                    //    butts against the vertical face of the 2.01m ArchPillar (and NO longitudinal bars across the pillar faces!)
                    if (hasN) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, wh, halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    if (hasS) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, wh, -halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    if (hasE) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, wh, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                    if (hasW) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, wh, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);

                    // Floor-level (0m) horizontal trim around exposed sides of the pillar
                    if (!hasN) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    if (!hasS) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, -halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    if (!hasE) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                    if (!hasW) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);

                    // 2. RED & BLUE OVAL FIX: Vertical corner lines on the 4 quadrants (NE, NW, SE, SW) of the pillar:
                    //    - NEVER draw a vertical line on a quadrant that is INSIDE a Bridge Cell (brNE, brNW, brSE, brSW)
                    //      because the continuous Stencil Tunnel Wall runs flush across that inner corner!
                    //    - On exterior quadrants: if a low wall connects on ONE side of the quadrant (e.g. hasN != hasE),
                    //      the lower 0..0.85m face is flat/continuous with the low wall, so start the vertical bar at wh (0.85m)!
                    //      If NO low wall connects (hasN == false && hasE == false) or BOTH connect (90-deg low corner), start at 0m!
                    void DrawPillarQuadrantVerticalEdge(int qx, int qz, bool isInsideBridgeQuadrant, bool wallZ, bool wallX)
                    {
                        if (isInsideBridgeQuadrant) return; // Inside the arch tunnel -> NO vertical line!

                        // Above wh (0.85m..doorH), the pillar is exposed on all exterior quadrants
                        // Below wh (0m..0.85m), only draw if it's a true 90-degree corner (wallZ == wallX)!
                        float botY = (wallZ == wallX) ? 0f : wh;
                        // Outer roof corner goes up to th (2.25m); doorway jamb corners stop at doorH (2.01m)
                        bool isOutermostRoofCorner =
                            (qx == 1 && qz == 1 && brSW) ||
                            (qx == -1 && qz == 1 && brSE) ||
                            (qx == 1 && qz == -1 && brNW) ||
                            (qx == -1 && qz == -1 && brNE);
                        float topY = isOutermostRoofCorner ? th : doorH;

                        float segH = topY - botY;
                        float segMidY = botY + segH * 0.5f;
                        Vector3 edgePos = vPos + new Vector3(qx * halfWT, segMidY, qz * halfWT);
                        CreateBlackEdgeBarWithMaterial(wallsRoot.transform, edgePos, new Vector3(ew, segH + ew, ew), blackEdgeMaterial);
                    }

                    DrawPillarQuadrantVerticalEdge(1, 1, brNE, hasN, hasE); // NE quadrant
                    DrawPillarQuadrantVerticalEdge(-1, 1, brNW, hasN, hasW); // NW quadrant
                    DrawPillarQuadrantVerticalEdge(1, -1, brSE, hasS, hasE); // SE quadrant
                    DrawPillarQuadrantVerticalEdge(-1, -1, brSW, hasS, hasW); // SW quadrant
                }
            }
        }
    }

   
    GameObject CreateWallBoxWithMaterial(Transform parent, string name, Vector3 worldCenter, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = worldCenter;
        go.transform.localScale = scale;
        NormalizeMeshUVsToWorldSpacePosition(go, PrimitiveType.Cube, worldCenter, scale);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    void CreateBlackEdgeBarWithMaterial(Transform parent, Vector3 worldPos, Vector3 scale, Material mat)
    {
        GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "PhysicalCornerEdge";
        Destroy(bar.GetComponent<Collider>());
        bar.transform.SetParent(parent, false);
        bar.transform.position = worldPos;
        bar.transform.localScale = scale;
        bar.GetComponent<Renderer>().sharedMaterial = mat;
    }

    void CreateEdgeBarBetweenPoints(Transform parent, Vector3 pA, Vector3 pB, float ew, Material mat)
    {
        Vector3 diff = pB - pA;
        float len = diff.magnitude;
        if (len < 0.001f) return;

        GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "PhysicalSlopedEdge";
        Destroy(bar.GetComponent<Collider>());
        bar.transform.SetParent(parent, false);
        bar.transform.position = (pA + pB) * 0.5f;
        bar.transform.rotation = Quaternion.LookRotation(diff / len, Vector3.up);
        bar.transform.localScale = new Vector3(ew, ew, len + ew);
        bar.GetComponent<Renderer>().sharedMaterial = mat;
    }

    GameObject CreateSlopedTaperedWallMesh(
        Transform parent,
        string name,
        Vector3 fIB, Vector3 fIT, Vector3 fOB, Vector3 fOT,
        Vector3 bIB, Vector3 bIT, Vector3 bOB, Vector3 bOT,
        Vector3 innerNormal, Vector3 outerNormal, Vector3 fwdDir,
        Material mat)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;

        List<Vector3> verts = new List<Vector3>(24);
        List<Vector3> norms = new List<Vector3>(24);
        List<Vector2> uvs   = new List<Vector2>(24);
        List<int> tris      = new List<int>(36);

        float uvPerMeter = 0.5f;

        void AddQuadFace(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 shadingNormal, Vector3 geomOutwardHint)
        {
            Vector3 geomN = Vector3.Cross(v1 - v0, v2 - v0);
            if (Vector3.Dot(geomN, geomOutwardHint) < 0f)
            {
                Vector3 tmp = v1; v1 = v3; v3 = tmp;
            }

            int baseIdx = verts.Count;
            verts.Add(v0); verts.Add(v1); verts.Add(v2); verts.Add(v3);

            Vector3 n = shadingNormal.normalized;
            for (int i = 0; i < 4; i++)
            {
                norms.Add(n);
                Vector3 wp = verts[baseIdx + i];
                if (Mathf.Abs(n.y) > 0.5f)
                    uvs.Add(new Vector2(wp.x, wp.z) * uvPerMeter);
                else if (Mathf.Abs(n.x) >= Mathf.Abs(n.z))
                    uvs.Add(new Vector2(wp.z, wp.y) * uvPerMeter);
                else
                    uvs.Add(new Vector2(wp.x, wp.y) * uvPerMeter);
            }

            tris.Add(baseIdx + 0); tris.Add(baseIdx + 1); tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 0); tris.Add(baseIdx + 2); tris.Add(baseIdx + 3);
        }

        // 1. Flat Sloped Top Handrail Face (uses Vector3.up shading normal so it matches all other bright white wall tops)
        AddQuadFace(fIT, fOT, bOT, bIT, Vector3.up, Vector3.up);
        // 2. Flat Bottom Face on the floor
        AddQuadFace(fIB, bIB, bOB, fOB, Vector3.down, Vector3.down);
        // 3. Flat Inner Wall Face (facing the stairs)
        AddQuadFace(fIB, fIT, bIT, bIB, innerNormal, innerNormal);
        // 4. Flat Outer Wall Face (facing the exterior of the staircase)
        AddQuadFace(fOB, bOB, bOT, fOT, outerNormal, outerNormal);
        // 5. Front Vertical Cap Face at stairStartS
        AddQuadFace(fIB, fOB, fOT, fIT, -fwdDir, -fwdDir);
        // 6. Back Vertical Cap Face at stairEndS
        AddQuadFace(bIB, bIT, bOT, bOB, fwdDir, fwdDir);

        Mesh mesh = new Mesh { name = name + "_Mesh" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        mf.sharedMesh = mesh;

        return go;
    }

    void CreateWallColliderWorld(Transform parent, Vector3 worldPos, Vector3 size)
    {
        GameObject colObj = new GameObject("WallCollider");
        colObj.transform.SetParent(parent, false);
        colObj.transform.position = new Vector3(worldPos.x, 1.05f, worldPos.z);
        BoxCollider bc = colObj.AddComponent<BoxCollider>();
        bc.size = new Vector3(size.x, 2.10f, size.z);
    }

    void BuildUnoccupiedCheckerboardCell(int r, int c)
    {
        Vector3 center = CellToWorldCenter(r, c, 0f);
        GameObject chkObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chkObj.name = $"CheckerBlock_{r}_{c}";
        Destroy(chkObj.GetComponent<Collider>());
        chkObj.transform.SetParent(mazeRoot.transform, false);

        float blockH = Mathf.Max(0.1f, wallHeight - 0.04f);
        Vector3 worldCenter = center + new Vector3(0f, blockH * 0.5f - 0.02f, 0f);
        chkObj.transform.position = worldCenter;
        Vector3 scale = new Vector3(cellSize, blockH, cellSize);
        chkObj.transform.localScale = scale;

        NormalizeMeshUVsToWorldSpacePosition(chkObj, PrimitiveType.Cube, worldCenter, scale, checkerSquaresPerMeter * 0.5f);
        chkObj.GetComponent<Renderer>().sharedMaterial = checkerboardMaterial;
    }

    void BuildWideCellFloorAndBreadcrumbs(Transform parent, int r, int c, int subLayer, Material floorMat, Material crumbMat, bool includeFloorMesh)
    {
        Vector3 center = CellToWorldCenter(r, c, 0f);
        GameObject cellObj = new GameObject($"Cell_{r}_{c}_Sub{subLayer}");
        cellObj.transform.SetParent(parent, false);
        cellObj.transform.position = center;

        if (includeFloorMesh)
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "WhiteFloor";
            floor.transform.SetParent(cellObj.transform, false);
            floor.transform.localPosition = new Vector3(0f, -0.05f, 0f);
            Vector3 floorScale = new Vector3(cellSize, 0.1f, cellSize);
            floor.transform.localScale = floorScale;
            NormalizeMeshUVsToWorldSpacePosition(floor, PrimitiveType.Cube, center + new Vector3(0f, -0.05f, 0f), floorScale);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;
            ConfigureFloorTeleportationArea(floor);
        }

        float crumbWidth = cellSize * 0.625f;
        float stubLen = (cellSize - crumbWidth) * 0.5f;
        float stubOffset = (crumbWidth + stubLen) * 0.5f;
        float crumbY = 0.008f + (subLayer == AmazingMazeData.LAYER_WIDE_BRIDGE ? 0.002f : 0f);

        GameObject[] crumbs = new GameObject[5];
        crumbs[4] = CreateCrumbPiece(cellObj.transform, "Crumb_Center", center, Vector3.up * crumbY, new Vector3(crumbWidth, 0.015f, crumbWidth), crumbMat);
        crumbs[AmazingMazeData.DIR_LEFT]  = CreateCrumbPiece(cellObj.transform, "Crumb_W", center, new Vector3(-stubOffset, crumbY, 0f), new Vector3(stubLen, 0.015f, crumbWidth), crumbMat);
        crumbs[AmazingMazeData.DIR_DOWN]  = CreateCrumbPiece(cellObj.transform, "Crumb_S", center, new Vector3(0f, crumbY, -stubOffset), new Vector3(crumbWidth, 0.015f, stubLen), crumbMat);
        crumbs[AmazingMazeData.DIR_RIGHT] = CreateCrumbPiece(cellObj.transform, "Crumb_E", center, new Vector3(stubOffset, crumbY, 0f),  new Vector3(stubLen, 0.015f, crumbWidth), crumbMat);
        crumbs[AmazingMazeData.DIR_UP]    = CreateCrumbPiece(cellObj.transform, "Crumb_N", center, new Vector3(0f, crumbY, stubOffset),  new Vector3(crumbWidth, 0.015f, stubLen), crumbMat);

        breadcrumbMeshes[(r, c, subLayer)] = crumbs;
    }

    void BuildCycladicCellWalls(int r, int c, int openMask)
    {
        Vector3 center = CellToWorldCenter(r, c, 0f);
        Transform cellTransform = mazeRoot.transform.Find($"Cell_{r}_{c}_Sub1");
        if (cellTransform == null) return;

        float half = cellSize * 0.5f;
        if ((openMask & (1 << AmazingMazeData.DIR_LEFT)) == 0)
            CreateCycladicWall(cellTransform, center + new Vector3(-half, 0f, 0f), new Vector3(-half, 0f, 0f), cellSize, true);
        if ((openMask & (1 << AmazingMazeData.DIR_RIGHT)) == 0)
            CreateCycladicWall(cellTransform, center + new Vector3(half, 0f, 0f), new Vector3(half, 0f, 0f), cellSize, true);
        if ((openMask & (1 << AmazingMazeData.DIR_UP)) == 0)
            CreateCycladicWall(cellTransform, center + new Vector3(0f, 0f, half), new Vector3(0f, 0f, half), cellSize, false);
        if ((openMask & (1 << AmazingMazeData.DIR_DOWN)) == 0)
            CreateCycladicWall(cellTransform, center + new Vector3(0f, 0f, -half), new Vector3(0f, 0f, -half), cellSize, false);
    }

    void CreateCycladicWall(Transform parent, Vector3 worldBaseCenter, Vector3 localBaseCenter, float length, bool alongZ)
    {
        GameObject wallGroup = new GameObject("CycladicWallGroup");
        wallGroup.transform.SetParent(parent, false);
        wallGroup.transform.localPosition = localBaseCenter;

        float radius = wallThickness * 0.5f;
        float bodyHeight = Mathf.Max(0.2f, wallHeight - radius);

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(body.GetComponent<Collider>());
        body.transform.SetParent(wallGroup.transform, false);
        body.transform.localPosition = new Vector3(0f, bodyHeight * 0.5f, 0f);
        Vector3 bodyScale = alongZ
            ? new Vector3(wallThickness, bodyHeight, length)
            : new Vector3(length, bodyHeight, wallThickness);
        body.transform.localScale = bodyScale;
        NormalizeMeshUVsToWorldSpacePosition(body, PrimitiveType.Cube, worldBaseCenter + new Vector3(0f, bodyHeight * 0.5f, 0f), bodyScale);
        body.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;

        GameObject topCap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(topCap.GetComponent<Collider>());
        topCap.transform.SetParent(wallGroup.transform, false);
        topCap.transform.localPosition = new Vector3(0f, bodyHeight, 0f);
        topCap.transform.localRotation = alongZ ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.Euler(0f, 0f, 90f);
        Vector3 capScale = new Vector3(wallThickness, length * 0.5f, wallThickness);
        topCap.transform.localScale = capScale;
        NormalizeMeshUVsToWorldSpacePosition(topCap, PrimitiveType.Cylinder, worldBaseCenter + new Vector3(0f, bodyHeight, 0f), capScale);
        topCap.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;

        float endOffset = length * 0.5f;
        for (int sign = -1; sign <= 1; sign += 2)
        {
            Vector3 postPos = alongZ ? new Vector3(0f, 0f, sign * endOffset) : new Vector3(sign * endOffset, 0f, 0f);

            GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(post.GetComponent<Collider>());
            post.transform.SetParent(wallGroup.transform, false);
            post.transform.localPosition = postPos + new Vector3(0f, bodyHeight * 0.5f, 0f);
            Vector3 postScale = new Vector3(wallThickness, bodyHeight * 0.5f, wallThickness);
            post.transform.localScale = postScale;
            NormalizeMeshUVsToWorldSpacePosition(post, PrimitiveType.Cylinder, worldBaseCenter + postPos, postScale);
            post.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;

            GameObject dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dome.GetComponent<Collider>());
            dome.transform.SetParent(wallGroup.transform, false);
            dome.transform.localPosition = postPos + new Vector3(0f, bodyHeight, 0f);
            Vector3 domeScale = Vector3.one * wallThickness;
            dome.transform.localScale = domeScale;
            NormalizeMeshUVsToWorldSpacePosition(dome, PrimitiveType.Sphere, worldBaseCenter + postPos, domeScale);
            dome.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;
        }

        BoxCollider col = wallGroup.AddComponent<BoxCollider>();
        col.center = new Vector3(0f, 1.05f, 0f);
        col.size = alongZ
            ? new Vector3(wallThickness, 2.10f, length + wallThickness)
            : new Vector3(length + wallThickness, 2.10f, wallThickness);
    }

    void BuildUpperNarrowCorridorCell(int r, int c, int nBaseOpen)
    {
        Vector3 center = CellToWorldCenter(r, c, upperFloorY);
        GameObject cellObj = new GameObject($"NarrowCell_{r}_{c}_Sub3");
        cellObj.transform.SetParent(mazeRoot.transform, false);
        cellObj.transform.position = center;

        float innerW = narrowCorridorWidth;
        float innerHW = innerW * 0.5f;
        float nwt = narrowWallThickness;
        float halfWT = nwt * 0.5f;
        float nwh = narrowWallHeight;
        float halfWall = (innerW + nwt) * 0.5f;
        float outerHW = innerHW + nwt;
        float outerW = outerHW * 2f;
        float armLen = cellSize * 0.5f - outerHW;
        float armMid = outerHW + armLen * 0.5f;
        float armFloorLen = cellSize * 0.5f - innerHW;
        float armFloorMid = innerHW + armFloorLen * 0.5f;
        float floorThick = 0.12f;
        float fullWallH = nwh + floorThick;
        float wallMidY = (nwh - floorThick) * 0.5f;
        float ew = blackEdgeOutlineWidth;

        // 1. Center junction floor slab (spans innerW x innerW between the walls so exterior wall faces have zero z-fighting)
        GameObject centerFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        centerFloor.name = "NarrowCenterFloor";
        centerFloor.transform.SetParent(cellObj.transform, false);
        centerFloor.transform.position = center + new Vector3(0f, -floorThick * 0.5f, 0f);
        Vector3 cfScale = new Vector3(innerW, floorThick, innerW);
        centerFloor.transform.localScale = cfScale;
        NormalizeMeshUVsToWorldSpacePosition(centerFloor, PrimitiveType.Cube, centerFloor.transform.position, cfScale);
        centerFloor.GetComponent<Renderer>().sharedMaterial = whiteFloorMaterial;
        ConfigureFloorTeleportationArea(centerFloor);

        bool openN = (nBaseOpen & (1 << AmazingMazeData.DIR_UP)) != 0;
        bool openS = (nBaseOpen & (1 << AmazingMazeData.DIR_DOWN)) != 0;
        bool openW = (nBaseOpen & (1 << AmazingMazeData.DIR_LEFT)) != 0;
        bool openE = (nBaseOpen & (1 << AmazingMazeData.DIR_RIGHT)) != 0;

        // 2. 4 Corner Posts around the center junction (extending from -floorThick to +nwh)
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 pPos = center + new Vector3(sx * halfWall, 0f, sz * halfWall);
                CreateWallBoxWithMaterial(cellObj.transform, $"NarrowPost_{sx}_{sz}", pPos + new Vector3(0f, wallMidY, 0f), new Vector3(nwt, fullWallH, nwt), whiteWallMaterial);

                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    bool openX = (sx == 1) ? openE : openW;
                    bool openZ = (sz == 1) ? openN : openS;

                    // Outer +sx face (exposed when !openX): top bar at nwh and bottom bar at -floorThick (NO mid-seam bar at y=0!)
                    if (!openX)
                    {
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(sx * halfWT, nwh, 0f), new Vector3(ew, ew, nwt + ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(sx * halfWT, -floorThick, 0f), new Vector3(ew, ew, nwt + ew), blackEdgeMaterial);
                    }
                    // Inner -sx face (exposed onto the openZ walkway when openZ is true): top bar at nwh and inner floor bar at ew * 0.5f
                    if (openZ)
                    {
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(-sx * halfWT, nwh, 0f), new Vector3(ew, ew, nwt + ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(-sx * halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, nwt + ew), blackEdgeMaterial);
                    }
                    // Outer +sz face (exposed when !openZ): top bar at nwh and bottom bar at -floorThick (NO mid-seam bar at y=0!)
                    if (!openZ)
                    {
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(0f, nwh, sz * halfWT), new Vector3(nwt + ew, ew, ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(0f, -floorThick, sz * halfWT), new Vector3(nwt + ew, ew, ew), blackEdgeMaterial);
                    }
                    // Inner -sz face (exposed onto the openX walkway when openX is true): top bar at nwh and inner floor bar at ew * 0.5f
                    if (openX)
                    {
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(0f, nwh, -sz * halfWT), new Vector3(nwt + ew, ew, ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(0f, ew * 0.5f, -sz * halfWT), new Vector3(nwt + ew, ew, ew), blackEdgeMaterial);
                    }

                    // Vertical corner bars: only exist when openX == openZ (either both closed = corner/dead-end, or both open = L-turn/intersection)
                    if (openX == openZ)
                    {
                        // Outer corner at (+sx * halfWT, +sz * halfWT) extends from -floorThick to +nwh
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(sx * halfWT, wallMidY, sz * halfWT), new Vector3(ew, fullWallH + ew, ew), blackEdgeMaterial);
                        // Inner walkway corner at (-sx * halfWT, -sz * halfWT) extends from 0 to +nwh
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, pPos + new Vector3(-sx * halfWT, nwh * 0.5f, -sz * halfWT), new Vector3(ew, nwh + ew, ew), blackEdgeMaterial);
                    }
                }
            }
        }

        // 3. Open Arms or Closed Cross-Walls in each of the 4 directions
        for (int dir = 0; dir < 4; dir++)
        {
            bool isOpen = (nBaseOpen & (1 << dir)) != 0;
            bool alongZ = (dir == AmazingMazeData.DIR_UP || dir == AmazingMazeData.DIR_DOWN);
            float sign = (dir == AmazingMazeData.DIR_UP || dir == AmazingMazeData.DIR_RIGHT) ? 1f : -1f;

            if (isOpen)
            {
                Vector3 armCenter = center + (alongZ ? new Vector3(0f, 0f, sign * armMid) : new Vector3(sign * armMid, 0f, 0f));
                Vector3 armFloorPos = center + (alongZ ? new Vector3(0f, -floorThick * 0.5f, sign * armFloorMid) : new Vector3(sign * armFloorMid, -floorThick * 0.5f, 0f));
                Vector3 fScale = alongZ ? new Vector3(innerW, floorThick, armFloorLen) : new Vector3(armFloorLen, floorThick, innerW);

                GameObject armFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                armFloor.name = $"NarrowArmFloor_{dir}";
                armFloor.transform.SetParent(cellObj.transform, false);
                armFloor.transform.position = armFloorPos;
                armFloor.transform.localScale = fScale;
                NormalizeMeshUVsToWorldSpacePosition(armFloor, PrimitiveType.Cube, armFloorPos, fScale);
                armFloor.GetComponent<Renderer>().sharedMaterial = whiteFloorMaterial;
                ConfigureFloorTeleportationArea(armFloor);

                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 wallPos = armCenter + (alongZ ? new Vector3(side * halfWall, wallMidY, 0f) : new Vector3(0f, wallMidY, side * halfWall));
                    Vector3 wallScale = alongZ ? new Vector3(nwt, fullWallH, armLen) : new Vector3(armLen, fullWallH, nwt);
                    CreateWallBoxWithMaterial(cellObj.transform, "NarrowArmSideWall", wallPos, wallScale, whiteWallMaterial);

                    Vector3 colPos = armCenter + (alongZ ? new Vector3(side * halfWall, 1.5f, 0f) : new Vector3(0f, 1.5f, side * halfWall));
                    CreateTunnelCollider(cellObj.transform, "NarrowArmCol", colPos, alongZ ? new Vector3(nwt, 3.0f, armLen) : new Vector3(armLen, 3.0f, nwt));

                    if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                    {
                        Vector3 barScale = alongZ ? new Vector3(ew, ew, armLen + ew) : new Vector3(armLen + ew, ew, ew);
                        float innerCoord = side * innerHW;
                        float outerCoord = side * outerHW;

                        // Inner walkway face: top bar at nwh and floor crease bar at ew * 0.5f
                        Vector3 innerTop = armCenter + (alongZ ? new Vector3(innerCoord, nwh, 0f) : new Vector3(0f, nwh, innerCoord));
                        Vector3 innerBot = armCenter + (alongZ ? new Vector3(innerCoord, ew * 0.5f, 0f) : new Vector3(0f, ew * 0.5f, innerCoord));
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, innerTop, barScale, blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, innerBot, barScale, blackEdgeMaterial);

                        // Outer exterior face: top bar at nwh and bottom bar at -floorThick (NO mid-seam bar at y=0!)
                        Vector3 outerTop = armCenter + (alongZ ? new Vector3(outerCoord, nwh, 0f) : new Vector3(0f, nwh, outerCoord));
                        Vector3 outerBot = armCenter + (alongZ ? new Vector3(outerCoord, -floorThick, 0f) : new Vector3(0f, -floorThick, outerCoord));
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, outerTop, barScale, blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(cellObj.transform, outerBot, barScale, blackEdgeMaterial);
                    }
                }
            }
            else
            {
                Vector3 closeMid = center + (alongZ ? new Vector3(0f, 0f, sign * halfWall) : new Vector3(sign * halfWall, 0f, 0f));
                Vector3 closeScale = alongZ ? new Vector3(innerW, fullWallH, nwt) : new Vector3(nwt, fullWallH, innerW);
                CreateWallBoxWithMaterial(cellObj.transform, $"NarrowCloseWall_{dir}", closeMid + new Vector3(0f, wallMidY, 0f), closeScale, whiteWallMaterial);

                CreateTunnelCollider(cellObj.transform, $"NarrowCloseCol_{dir}", closeMid + new Vector3(0f, 1.5f, 0f), alongZ ? new Vector3(outerW, 3.0f, nwt) : new Vector3(nwt, 3.0f, outerW));

                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    Vector3 barScale = alongZ ? new Vector3(innerW + ew, ew, ew) : new Vector3(ew, ew, innerW + ew);

                    // Inner walkway face (-sign * halfWT): top bar at nwh and floor crease bar at ew * 0.5f
                    Vector3 innerTop = closeMid + (alongZ ? new Vector3(0f, nwh, -sign * halfWT) : new Vector3(-sign * halfWT, nwh, 0f));
                    Vector3 innerBot = closeMid + (alongZ ? new Vector3(0f, ew * 0.5f, -sign * halfWT) : new Vector3(-sign * halfWT, ew * 0.5f, 0f));
                    CreateBlackEdgeBarWithMaterial(cellObj.transform, innerTop, barScale, blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(cellObj.transform, innerBot, barScale, blackEdgeMaterial);

                    // Outer exterior face (+sign * halfWT): top bar at nwh and bottom bar at -floorThick (NO mid-seam bar at y=0!)
                    Vector3 outerTop = closeMid + (alongZ ? new Vector3(0f, nwh, sign * halfWT) : new Vector3(sign * halfWT, nwh, 0f));
                    Vector3 outerBot = closeMid + (alongZ ? new Vector3(0f, -floorThick, sign * halfWT) : new Vector3(sign * halfWT, -floorThick, 0f));
                    CreateBlackEdgeBarWithMaterial(cellObj.transform, outerTop, barScale, blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(cellObj.transform, outerBot, barScale, blackEdgeMaterial);
                }
            }
        }

        // 4. Upper Narrow Breadcrumbs
        float crumbWidth = cellSize * 0.25f;
        float stubLen = (cellSize - crumbWidth) * 0.5f;
        float stubOffset = (crumbWidth + stubLen) * 0.5f;
        float crumbY = 0.008f;

        GameObject[] crumbs = new GameObject[5];
        crumbs[4] = CreateCrumbPiece(cellObj.transform, "Crumb_Center", center, Vector3.up * crumbY, new Vector3(crumbWidth, 0.015f, crumbWidth), blackBreadcrumbMaterial);
        crumbs[AmazingMazeData.DIR_LEFT]  = CreateCrumbPiece(cellObj.transform, "Crumb_W", center, new Vector3(-stubOffset, crumbY, 0f), new Vector3(stubLen, 0.015f, crumbWidth), blackBreadcrumbMaterial);
        crumbs[AmazingMazeData.DIR_DOWN]  = CreateCrumbPiece(cellObj.transform, "Crumb_S", center, new Vector3(0f, crumbY, -stubOffset), new Vector3(crumbWidth, 0.015f, stubLen), blackBreadcrumbMaterial);
        crumbs[AmazingMazeData.DIR_RIGHT] = CreateCrumbPiece(cellObj.transform, "Crumb_E", center, new Vector3(stubOffset, crumbY, 0f),  new Vector3(stubLen, 0.015f, crumbWidth), blackBreadcrumbMaterial);
        crumbs[AmazingMazeData.DIR_UP]    = CreateCrumbPiece(cellObj.transform, "Crumb_N", center, new Vector3(0f, crumbY, stubOffset),  new Vector3(crumbWidth, 0.015f, stubLen), blackBreadcrumbMaterial);

        breadcrumbMeshes[(r, c, AmazingMazeData.LAYER_NARROW_BASE)] = crumbs;
    }

    void BuildTaperedRampStairsCell(int r, int c, int wBaseOpen, int nBaseOpen)
    {
        Vector3 cellCenter = CellToWorldCenter(r, c, 0f);
        GameObject rampObj = new GameObject($"TaperedRampStairs_{r}_{c}");
        rampObj.transform.SetParent(mazeRoot.transform, false);
        rampObj.transform.position = cellCenter;

        int wideDir = 0, narrowDir = 2;
        for (int d = 0; d < 4; d++)
        {
            if ((wBaseOpen & (1 << d)) != 0) wideDir = d;
            if ((nBaseOpen & (1 << d)) != 0) narrowDir = d;
        }

        // fwd points from the lower wide entrance (s = -1.5m) into the ramp cell toward s = +1.5m
        Vector3 fwd = -DirToWorldVec(wideDir);
        Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
        bool fwdAlongZ = Mathf.Abs(fwd.z) > 0.5f;

        Vector3 LocalToWorld(float uRight, float y, float sFwd)
        {
            return cellCenter + right * uRight + Vector3.up * y + fwd * sFwd;
        }

        Vector3 LocalScale(float sizeRight, float sizeY, float sizeFwd)
        {
            return fwdAlongZ ? new Vector3(sizeRight, sizeY, sizeFwd) : new Vector3(sizeFwd, sizeY, sizeRight);
        }

        float wt = wallThickness;
        float halfWT = wt * 0.5f;
        float wh = wallHeight;
        float nwt = narrowWallThickness;
        float halfNWT = nwt * 0.5f;
        float nwh = narrowWallHeight;
        float ew = blackEdgeOutlineWidth;
        float wideWallCenterU = cellSize * 0.5f;
        float halfWideInner = wideWallCenterU - halfWT;
        float halfNarrowInner = narrowCorridorWidth * 0.5f;

        List<GameObject> rampCrumbs = new List<GameObject>();

        bool isStraightRamp = (narrowDir == AmazingMazeData.DirOpp(wideDir));

        if (isStraightRamp)
        {
            // 1. Lower Wide Entrance Landing (s in [-1.50, -1.10])
            float lowLandingLen = 0.40f;
            float lowLandingMidS = -cellSize * 0.5f + lowLandingLen * 0.5f;
            Vector3 lowFloorPos = LocalToWorld(0f, -0.05f, lowLandingMidS);
            Vector3 lowFloorScale = LocalScale(cellSize, 0.1f, lowLandingLen);
            CreateWallBoxWithMaterial(rampObj.transform, "RampLowerLanding", lowFloorPos, lowFloorScale, whiteFloorMaterial);

            // Wide side wall stubs centered at wideWallCenterU (+/-1.50m) so they connect 100% flush with CornerPost and wide walls
            float stubLen = lowLandingLen - halfWT;
            float stubMidS = -cellSize * 0.5f + halfWT + stubLen * 0.5f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 stubPos = LocalToWorld(side * wideWallCenterU, wh * 0.5f, stubMidS);
                CreateWallBoxWithMaterial(rampObj.transform, "RampWideStubWall", stubPos, LocalScale(wt, wh, stubLen), whiteWallMaterial);
                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    for (int fSide = -1; fSide <= 1; fSide += 2)
                    {
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(side * wideWallCenterU + fSide * halfWT, wh, stubMidS), LocalScale(ew, ew, stubLen + ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(side * wideWallCenterU + fSide * halfWT, ew * 0.5f, stubMidS), LocalScale(ew, ew, stubLen + ew), blackEdgeMaterial);
                    }
                }
            }

            // Crumb on lower landing
            float crumbWide = cellSize * 0.625f;
            rampCrumbs.Add(CreateCrumbPiece(rampObj.transform, "RampCrumb_Low", cellCenter, LocalToWorld(0f, 0.008f, lowLandingMidS) - cellCenter, LocalScale(crumbWide, 0.015f, lowLandingLen), blackBreadcrumbMaterial));

            // 2. Smooth Flat-Topped Sloped Handrail Side Walls (s in [-1.10, +1.10], extending solidly down to the floor y = 0)
            int numSteps = 10;
            float stairStartS = -1.10f;
            float stairEndS = 1.10f;
            float stepDepth = (stairEndS - stairStartS) / numSteps;
            float stepRise = upperFloorY / numSteps;
            float floorThick = 0.12f;
            float outerNarrowW = narrowCorridorWidth + nwt * 2f;

            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 fIB = LocalToWorld(side * halfWideInner, 0f, stairStartS);
                Vector3 fIT = LocalToWorld(side * halfWideInner, wh, stairStartS);
                Vector3 fOB = LocalToWorld(side * (halfWideInner + wt), 0f, stairStartS);
                Vector3 fOT = LocalToWorld(side * (halfWideInner + wt), wh, stairStartS);

                Vector3 bIB = LocalToWorld(side * halfNarrowInner, 0f, stairEndS);
                Vector3 bIT = LocalToWorld(side * halfNarrowInner, upperFloorY + nwh, stairEndS);
                Vector3 bOB = LocalToWorld(side * (halfNarrowInner + nwt), 0f, stairEndS);
                Vector3 bOT = LocalToWorld(side * (halfNarrowInner + nwt), upperFloorY + nwh, stairEndS);

                CreateSlopedTaperedWallMesh(
                    rampObj.transform,
                    $"StairSmoothSlopedWall_{side}",
                    fIB, fIT, fOB, fOT,
                    bIB, bIT, bOB, bOT,
                    -side * right, side * right, fwd,
                    whiteWallMaterial);

                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    // Sloped top inner & outer handrail edges (matching the blue lines!)
                    CreateEdgeBarBetweenPoints(rampObj.transform, fIT, bIT, ew, blackEdgeMaterial);
                    CreateEdgeBarBetweenPoints(rampObj.transform, fOT, bOT, ew, blackEdgeMaterial);

                    // Outer floor-level bottom edge bar along y = 0
                    CreateEdgeBarBetweenPoints(rampObj.transform, fOB + Vector3.up * (ew * 0.5f), bOB + Vector3.up * (ew * 0.5f), ew, blackEdgeMaterial);

                    // Transverse top handrail creases at bottom (s = stairStartS, y = wh) and top (s = stairEndS, y = upperFloorY + nwh)
                    CreateEdgeBarBetweenPoints(rampObj.transform, fIT, fOT, ew, blackEdgeMaterial);
                    CreateEdgeBarBetweenPoints(rampObj.transform, bIT, bOT, ew, blackEdgeMaterial);

                    // Vertical corner bars at front transition (s = stairStartS) and back transition (s = stairEndS)
                    CreateEdgeBarBetweenPoints(rampObj.transform, fOB, fOT, ew, blackEdgeMaterial);
                    CreateEdgeBarBetweenPoints(rampObj.transform, fIB, fIT, ew, blackEdgeMaterial);
                    CreateEdgeBarBetweenPoints(rampObj.transform, bOB, bOT, ew, blackEdgeMaterial);
                    CreateEdgeBarBetweenPoints(rampObj.transform, LocalToWorld(side * halfNarrowInner, upperFloorY, stairEndS), bIT, ew, blackEdgeMaterial);
                }
            }

            // Solid back wall at s = stairEndS closing the underside of the stairs from y = 0 up to upperFloorY - floorThick
            float pedestalBackH = upperFloorY - floorThick;
            CreateWallBoxWithMaterial(
                rampObj.transform,
                "RampPedestalBackWall",
                LocalToWorld(0f, pedestalBackH * 0.5f, stairEndS - 0.02f),
                LocalScale(outerNarrowW, pedestalBackH, 0.04f),
                whiteWallMaterial);
            if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
            {
                CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(0f, ew * 0.5f, stairEndS), LocalScale(outerNarrowW + ew, ew, ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(0f, pedestalBackH, stairEndS), LocalScale(outerNarrowW + ew, ew, ew), blackEdgeMaterial);
            }

            // 10 Solid Stair Steps between the two flat sloped side walls (extending all the way down to y = 0 so no stepped underside is ever visible)
            for (int i = 0; i < numSteps; i++)
            {
                float t0 = (float)i / numSteps;
                float t1 = (float)(i + 1) / numSteps;
                float tMid = (i + 0.5f) / numSteps;

                float uFront = Mathf.Lerp(halfWideInner, halfNarrowInner, t0);
                float uBack  = Mathf.Lerp(halfWideInner, halfNarrowInner, t1);

                float frontS = stairStartS + i * stepDepth;
                float backS  = frontS + stepDepth;
                float stepMidS = (frontS + backS) * 0.5f;
                float stepTopY = (i + 1) * stepRise;

                // Step block extends solidly from y = 0 to stepTopY and tucks 0.06m inside the solid side walls
                Vector3 stepPos = LocalToWorld(0f, stepTopY * 0.5f, stepMidS);
                Vector3 stepScale = LocalScale((uFront + 0.06f) * 2f, stepTopY, stepDepth + 0.005f);
                CreateWallBoxWithMaterial(rampObj.transform, $"StairStep_{i}", stepPos, stepScale, whiteFloorMaterial);

                // Step surface outlines (strictly on the walkable stair treads and risers between the two flat walls)
                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(0f, stepTopY, frontS), LocalScale(uFront * 2f + ew, ew, ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(0f, i * stepRise + ew * 0.5f, frontS), LocalScale(uFront * 2f + ew, ew, ew), blackEdgeMaterial);

                    for (int side = -1; side <= 1; side += 2)
                    {
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(side * uFront, stepTopY - stepRise * 0.5f, frontS), LocalScale(ew, stepRise + ew, ew), blackEdgeMaterial);
                        CreateEdgeBarBetweenPoints(
                            rampObj.transform,
                            LocalToWorld(side * uFront, stepTopY + ew * 0.5f, frontS),
                            LocalToWorld(side * uBack, stepTopY + ew * 0.5f, backS),
                            ew,
                            blackEdgeMaterial);
                    }
                }

                // Step breadcrumb strip on top of step i (plus riser crumb so path looks continuous from any angle!)
                float curCrumbW = Mathf.Lerp(crumbWide, cellSize * 0.25f, tMid);
                rampCrumbs.Add(CreateCrumbPiece(rampObj.transform, $"RampCrumb_Step_{i}", cellCenter, LocalToWorld(0f, stepTopY + 0.008f, stepMidS) - cellCenter, LocalScale(curCrumbW, 0.015f, stepDepth + 0.01f), blackBreadcrumbMaterial));
                rampCrumbs.Add(CreateCrumbPiece(rampObj.transform, $"RampCrumb_Riser_{i}", cellCenter, LocalToWorld(0f, stepTopY - stepRise * 0.5f, frontS - 0.006f) - cellCenter, LocalScale(curCrumbW, stepRise, 0.012f), blackBreadcrumbMaterial));
            }

            // 3. Upper Narrow Exit Landing (s in [+1.10, +1.50])
            float highLandingLen = 0.40f;
            float highLandingMidS = cellSize * 0.5f - highLandingLen * 0.5f;
            float fullHighWallH = nwh + floorThick;
            float highWallMidY = upperFloorY + (nwh - floorThick) * 0.5f;
            Vector3 highFloorPos = LocalToWorld(0f, upperFloorY - floorThick * 0.5f, highLandingMidS);
            CreateWallBoxWithMaterial(rampObj.transform, "RampUpperLanding", highFloorPos, LocalScale(narrowCorridorWidth, floorThick, highLandingLen), whiteFloorMaterial);

            float highWallU = (narrowCorridorWidth + nwt) * 0.5f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 hwPos = LocalToWorld(side * highWallU, highWallMidY, highLandingMidS);
                CreateWallBoxWithMaterial(rampObj.transform, "RampNarrowExitWall", hwPos, LocalScale(nwt, fullHighWallH, highLandingLen), whiteWallMaterial);
                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    float innerU = side * halfNarrowInner;
                    float outerU = side * (halfNarrowInner + nwt);
                    Vector3 barScale = LocalScale(ew, ew, highLandingLen + ew);

                    // Inner walkway face: top bar at upperFloorY + nwh and floor bar at upperFloorY + ew * 0.5f
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(innerU, upperFloorY + nwh, highLandingMidS), barScale, blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(innerU, upperFloorY + ew * 0.5f, highLandingMidS), barScale, blackEdgeMaterial);

                    // Outer exterior face: top bar at upperFloorY + nwh and bottom bar at upperFloorY - floorThick (NO mid-seam bar at upperFloorY!)
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(outerU, upperFloorY + nwh, highLandingMidS), barScale, blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(outerU, upperFloorY - floorThick, highLandingMidS), barScale, blackEdgeMaterial);
                }
            }

            rampCrumbs.Add(CreateCrumbPiece(rampObj.transform, "RampCrumb_High", cellCenter, LocalToWorld(0f, upperFloorY + 0.008f, highLandingMidS) - cellCenter, LocalScale(cellSize * 0.25f, 0.015f, highLandingLen), blackBreadcrumbMaterial));

            // 4. Ground-level checkerboard base pad under the tapered/narrowed section of the ramp cell
            if (fillUnoccupiedWithCheckerboard)
            {
                float padLen = cellSize - lowLandingLen;
                float padMidS = -cellSize * 0.5f + lowLandingLen + padLen * 0.5f;
                GameObject chkPad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                chkPad.name = "RampGroundCheckerPad";
                Destroy(chkPad.GetComponent<Collider>());
                chkPad.transform.SetParent(rampObj.transform, false);
                Vector3 chkPos = LocalToWorld(0f, -0.05f, padMidS);
                Vector3 chkScale = LocalScale(cellSize, 0.1f, padLen);
                chkPad.transform.position = chkPos;
                chkPad.transform.localScale = chkScale;
                NormalizeMeshUVsToWorldSpacePosition(chkPad, PrimitiveType.Cube, chkPos, chkScale, checkerSquaresPerMeter * 0.5f);
                chkPad.GetComponent<Renderer>().sharedMaterial = checkerboardMaterial;
            }

            // 5. Smooth Physics Colliders & Teleportation Surfaces for walking and teleporting up/down the straight tapered ramp
            Collider lowLandCol = CreateTunnelCollider(rampObj.transform, "RampLowLandingCol", LocalToWorld(0f, -0.05f, lowLandingMidS), LocalScale(cellSize, 0.1f, lowLandingLen));
            ConfigureFloorTeleportationArea(lowLandCol.gameObject);
            stairFloorColliders.Add(lowLandCol);

            Collider highLandCol = CreateTunnelCollider(rampObj.transform, "RampHighLandingCol", LocalToWorld(0f, upperFloorY - 0.05f, highLandingMidS), LocalScale(outerNarrowW, 0.1f, highLandingLen));
            ConfigureFloorTeleportationArea(highLandCol.gameObject);
            stairFloorColliders.Add(highLandCol);

            float slopeRun = stairEndS - stairStartS;
            float slopeLen = Mathf.Sqrt(slopeRun * slopeRun + upperFloorY * upperFloorY);
            float slopeAngle = Mathf.Atan2(upperFloorY, slopeRun) * Mathf.Rad2Deg;
            GameObject slopeColObj = new GameObject("RampSlopeCollider");
            slopeColObj.transform.SetParent(rampObj.transform, false);
            slopeColObj.transform.position = LocalToWorld(0f, upperFloorY * 0.5f - 0.04f, 0f);
            slopeColObj.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(-slopeAngle, 0f, 0f);
            BoxCollider slopeBC = slopeColObj.AddComponent<BoxCollider>();
            slopeBC.size = new Vector3(cellSize, 0.1f, slopeLen);
            ConfigureFloorTeleportationArea(slopeColObj);
            stairFloorColliders.Add(slopeBC);

            // Angled side-wall colliders that taper smoothly from wide (u = +/-1.32m) to narrow (u = +/-0.75m)
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 pStart = LocalToWorld(side * halfWideInner, 2.0f, -1.10f);
                Vector3 pEnd   = LocalToWorld(side * highWallU, 2.0f, 1.10f);
                Vector3 segVec = pEnd - pStart;
                GameObject taperWallCol = new GameObject($"RampTaperSideCol_{side}");
                taperWallCol.transform.SetParent(rampObj.transform, false);
                taperWallCol.transform.position = (pStart + pEnd) * 0.5f;
                taperWallCol.transform.rotation = Quaternion.LookRotation(segVec.normalized, Vector3.up);
                taperWallCol.AddComponent<BoxCollider>().size = new Vector3(nwt, 4.5f, segVec.magnitude);

                CreateTunnelCollider(rampObj.transform, "RampSideCol_HighStub", LocalToWorld(side * highWallU, upperFloorY + 1.2f, highLandingMidS), LocalScale(nwt, 2.6f, highLandingLen));
            }
        }
        else
        {
            // 90-DEGREE CORNER TURNING RAMP (Level 4):
            // Enters wide from wideDir (s = -1.50m, y = 0m) and turns 90 degrees toward narrowDir (turnSign * right, y = upperFloorY)
            Vector3 narrowVec = DirToWorldVec(narrowDir);
            float turnSign = (Vector3.Dot(narrowVec, right) >= 0f) ? 1f : -1f;

            // 1. Lower Wide Entrance Flight: 5 steps rising from y = 0m to y = 1.20m along s in [-1.50, -0.35]
            int lowSteps = 5;
            float lowStartS = -1.35f;
            float lowEndS = -0.35f;
            float lowStepDepth = (lowEndS - lowStartS) / lowSteps;
            float midY = upperFloorY * 0.5f;
            float lowStepRise = midY / lowSteps;

            CreateWallBoxWithMaterial(rampObj.transform, "CornerRampEntryPad", LocalToWorld(0f, -0.05f, -1.425f), LocalScale(cellSize, 0.1f, 0.15f), whiteFloorMaterial);

            for (int i = 0; i < lowSteps; i++)
            {
                float t = (i + 0.5f) / lowSteps;
                float curHalfInner = Mathf.Lerp(halfWideInner - halfWT, halfNarrowInner + 0.15f, t);
                float stepMidS = lowStartS + (i + 0.5f) * lowStepDepth;
                float stepTopY = (i + 1) * lowStepRise;

                CreateWallBoxWithMaterial(rampObj.transform, $"CornerLowStep_{i}", LocalToWorld(0f, stepTopY * 0.5f, stepMidS), LocalScale((curHalfInner + nwt) * 2f, stepTopY, lowStepDepth + 0.01f), whiteFloorMaterial);
                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    float frontS = lowStartS + i * lowStepDepth;
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(0f, stepTopY, frontS), LocalScale(curHalfInner * 2f + ew, ew, ew), blackEdgeMaterial);
                }

                for (int side = -1; side <= 1; side += 2)
                {
                    float wU = side * (curHalfInner + halfNWT);
                    CreateWallBoxWithMaterial(rampObj.transform, $"CornerLowWall_{i}_{side}", LocalToWorld(wU, stepTopY + nwh * 0.5f, stepMidS), LocalScale(nwt, nwh, lowStepDepth + 0.01f), whiteWallMaterial);
                    if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                    {
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(side * curHalfInner, stepTopY + nwh, stepMidS), LocalScale(ew, ew, lowStepDepth + ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(side * (curHalfInner + nwt), stepTopY + nwh, stepMidS), LocalScale(ew, ew, lowStepDepth + ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(side * curHalfInner, stepTopY + ew * 0.5f, stepMidS), LocalScale(ew, ew, lowStepDepth + ew), blackEdgeMaterial);
                    }
                }

                float curCrumbW = Mathf.Lerp(cellSize * 0.625f, cellSize * 0.25f, t);
                rampCrumbs.Add(CreateCrumbPiece(rampObj.transform, $"CornerCrumb_Low_{i}", cellCenter, LocalToWorld(0f, stepTopY + 0.008f, stepMidS) - cellCenter, LocalScale(curCrumbW, 0.015f, lowStepDepth + 0.02f), blackBreadcrumbMaterial));
            }

            // 2. Corner Turn Mid-Landing at y = midY (s in [-0.35, +0.63], u in [-0.63, +0.63])
            float landHalf = halfNarrowInner + nwt;
            CreateWallBoxWithMaterial(rampObj.transform, "CornerMidLanding", LocalToWorld(0f, midY * 0.5f, 0.14f), LocalScale(landHalf * 2f, midY, 0.98f), whiteFloorMaterial);
            rampCrumbs.Add(CreateCrumbPiece(rampObj.transform, "CornerCrumb_Mid", cellCenter, LocalToWorld(0f, midY + 0.008f, 0f) - cellCenter, LocalScale(cellSize * 0.25f, 0.015f, cellSize * 0.25f), blackBreadcrumbMaterial));

            // Back & outer corner parapet walls around the mid-landing
            float highWallOffset = (narrowCorridorWidth + nwt) * 0.5f;
            CreateWallBoxWithMaterial(rampObj.transform, "CornerMidBackWall", LocalToWorld(0f, midY + nwh * 0.5f, highWallOffset), LocalScale(landHalf * 2f, nwh, nwt), whiteWallMaterial);
            CreateWallBoxWithMaterial(rampObj.transform, "CornerMidOuterWall", LocalToWorld(-turnSign * highWallOffset, midY + nwh * 0.5f, 0.14f), LocalScale(nwt, nwh, 0.98f), whiteWallMaterial);
            if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
            {
                CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(0f, midY + nwh, halfNarrowInner), LocalScale(narrowCorridorWidth + ew, ew, ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(0f, midY + ew * 0.5f, halfNarrowInner), LocalScale(narrowCorridorWidth + ew, ew, ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(-turnSign * halfNarrowInner, midY + nwh, 0.14f), LocalScale(ew, ew, 0.98f + ew), blackEdgeMaterial);
                CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(-turnSign * halfNarrowInner, midY + ew * 0.5f, 0.14f), LocalScale(ew, ew, 0.98f + ew), blackEdgeMaterial);
            }

            // 3. Upper Narrow Exit Flight: 5 steps rising from y = midY to y = upperFloorY along turnSign * u in [0.50, 1.50]
            int highSteps = 5;
            float highStartU = 0.50f;
            float highEndU = 1.50f;
            float highStepDepth = (highEndU - highStartU) / highSteps;
            float highStepRise = (upperFloorY - midY) / highSteps;

            for (int i = 0; i < highSteps; i++)
            {
                float stepMidU = turnSign * (highStartU + (i + 0.5f) * highStepDepth);
                float stepTopY = midY + (i + 1) * highStepRise;
                float stepBotY = midY - 0.12f;
                float blockH = stepTopY - stepBotY;

                CreateWallBoxWithMaterial(rampObj.transform, $"CornerHighStep_{i}", LocalToWorld(stepMidU, stepBotY + blockH * 0.5f, 0f), LocalScale(highStepDepth + 0.01f, blockH, (halfNarrowInner + nwt) * 2f), whiteFloorMaterial);
                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    float frontU = turnSign * (highStartU + i * highStepDepth);
                    CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(frontU, stepTopY, 0f), LocalScale(ew, ew, narrowCorridorWidth + ew), blackEdgeMaterial);
                }

                for (int sSide = -1; sSide <= 1; sSide += 2)
                {
                    float wallS = sSide * highWallOffset;
                    CreateWallBoxWithMaterial(rampObj.transform, $"CornerHighWall_{i}_{sSide}", LocalToWorld(stepMidU, stepTopY + nwh * 0.5f, wallS), LocalScale(highStepDepth + 0.01f, nwh, nwt), whiteWallMaterial);
                    if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                    {
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(stepMidU, stepTopY + nwh, sSide * halfNarrowInner), LocalScale(highStepDepth + ew, ew, ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(stepMidU, stepTopY + nwh, sSide * (halfNarrowInner + nwt)), LocalScale(highStepDepth + ew, ew, ew), blackEdgeMaterial);
                        CreateBlackEdgeBarWithMaterial(rampObj.transform, LocalToWorld(stepMidU, stepTopY + ew * 0.5f, sSide * halfNarrowInner), LocalScale(highStepDepth + ew, ew, ew), blackEdgeMaterial);
                    }
                }

                rampCrumbs.Add(CreateCrumbPiece(rampObj.transform, $"CornerCrumb_High_{i}", cellCenter, LocalToWorld(stepMidU, stepTopY + 0.008f, 0f) - cellCenter, LocalScale(highStepDepth + 0.02f, 0.015f, cellSize * 0.25f), blackBreadcrumbMaterial));
            }

            // Ground-level closing walls on the 3 non-wide sides of the cell
            CreateWallBoxWithMaterial(rampObj.transform, "CornerGroundBackWall", LocalToWorld(0f, wh * 0.5f, cellSize * 0.5f), LocalScale(cellSize - wt, wh, wt), whiteWallMaterial);
            for (int side = -1; side <= 1; side += 2)
            {
                CreateWallBoxWithMaterial(rampObj.transform, "CornerGroundSideWall", LocalToWorld(side * cellSize * 0.5f, wh * 0.5f, 0f), LocalScale(wt, wh, cellSize - wt), whiteWallMaterial);
            }

            // Smooth physics colliders & teleport surfaces for the 2 flights + mid-landing + walls
            Collider midLandCol = CreateTunnelCollider(rampObj.transform, "CornerMidLandingCol", LocalToWorld(0f, midY - 0.05f, 0.14f), LocalScale(landHalf * 2f, 0.1f, 0.98f));
            ConfigureFloorTeleportationArea(midLandCol.gameObject);
            stairFloorColliders.Add(midLandCol);

            GameObject lowSlope = new GameObject("CornerLowSlopeCol");
            lowSlope.transform.SetParent(rampObj.transform, false);
            lowSlope.transform.position = LocalToWorld(0f, midY * 0.5f - 0.04f, -0.85f);
            float lowAng = Mathf.Atan2(midY, 1.15f) * Mathf.Rad2Deg;
            lowSlope.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(-lowAng, 0f, 0f);
            BoxCollider lowSlopeBC = lowSlope.AddComponent<BoxCollider>();
            lowSlopeBC.size = new Vector3(cellSize, 0.1f, Mathf.Sqrt(1.15f * 1.15f + midY * midY));
            ConfigureFloorTeleportationArea(lowSlope);
            stairFloorColliders.Add(lowSlopeBC);

            GameObject highSlope = new GameObject("CornerHighSlopeCol");
            highSlope.transform.SetParent(rampObj.transform, false);
            highSlope.transform.position = LocalToWorld(turnSign * 1.0f, (midY + upperFloorY) * 0.5f - 0.04f, 0f);
            float highAng = Mathf.Atan2(upperFloorY - midY, 1.0f) * Mathf.Rad2Deg;
            highSlope.transform.rotation = Quaternion.LookRotation(turnSign * right, Vector3.up) * Quaternion.Euler(-highAng, 0f, 0f);
            BoxCollider highSlopeBC = highSlope.AddComponent<BoxCollider>();
            highSlopeBC.size = new Vector3(narrowCorridorWidth + nwt * 2f, 0.1f, Mathf.Sqrt(1.0f + (upperFloorY - midY) * (upperFloorY - midY)));
            ConfigureFloorTeleportationArea(highSlope);
            stairFloorColliders.Add(highSlopeBC);

            CreateTunnelCollider(rampObj.transform, "CornerBackWallCol", LocalToWorld(0f, 2.0f, highWallOffset), LocalScale(cellSize, 4.0f, nwt));
            CreateTunnelCollider(rampObj.transform, "CornerOuterWallCol", LocalToWorld(-turnSign * highWallOffset, 2.0f, 0.14f), LocalScale(nwt, 4.0f, 1.05f));
            for (int sSide = -1; sSide <= 1; sSide += 2)
            {
                CreateTunnelCollider(rampObj.transform, $"CornerHighSideCol_{sSide}", LocalToWorld(turnSign * 1.0f, upperFloorY + 1.0f, sSide * highWallOffset), LocalScale(1.0f, 2.5f, nwt));
            }
        }

        rampBreadcrumbMeshes[(r, c)] = rampCrumbs;
    }

    GameObject CreateCrumbPiece(Transform parent, string name, Vector3 cellWorldCenter, Vector3 localPos, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        NormalizeMeshUVsToWorldSpacePosition(go, PrimitiveType.Cube, cellWorldCenter + localPos, scale);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.SetActive(false);
        return go;
    }

    Renderer SpawnMarkerPrimitive(PrimitiveType type, int r, int c, float yHeight, string name)
    {
        GameObject marker = GameObject.CreatePrimitive(type);
        marker.name = name;
        Destroy(marker.GetComponent<Collider>());
        marker.transform.SetParent(mazeRoot.transform, false);
        Vector3 worldPos = CellToWorldCenter(r, c, yHeight);
        marker.transform.position = worldPos;
        Vector3 markerScale = Vector3.one * (cellSize * 0.28f);
        marker.transform.localScale = markerScale;
        NormalizeMeshUVsToWorldSpacePosition(marker, type, worldPos, markerScale);
        Renderer rend = marker.GetComponent<Renderer>();
        rend.sharedMaterial = blackBreadcrumbMaterial;
        return rend;
    }

    public void UpdateBreadcrumbsAnd2DMap()
    {
        foreach (var kvp in breadcrumbMeshes)
        {
            var (r, c, subLayer) = kvp.Key;
            GameObject[] pieces = kvp.Value;
            byte sq = MazeData.GetSquare(r, c, subLayer);
            bool occupied = (sq & 0x30) != 0;
            byte nibs = MazeData.NibMask[r, c, subLayer];

            pieces[4].SetActive(occupied);
            for (int d = 0; d < 4; d++)
                pieces[d].SetActive(occupied && (nibs & (1 << d)) != 0);
        }

        foreach (var kvp in rampBreadcrumbMeshes)
        {
            var (r, c) = kvp.Key;
            List<GameObject> pieces = kvp.Value;
            bool occupied = ((MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) |
                              MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BASE)) & 0x30) != 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i] != null) pieces[i].SetActive(occupied);
            }
        }

        // Invert Start Sphere and Goal Cube between black (unoccupied) and white (when black breadcrumb path is present) to match 2D map!
        if (startMarkerRenderer != null)
        {
            bool startOccupied = (MazeData.GetSquare(MazeData.StartPos.r, MazeData.StartPos.c, MazeData.StartPos.layer) & 0x30) != 0;
            startMarkerRenderer.sharedMaterial = startOccupied ? whiteWallMaterial : blackBreadcrumbMaterial;
        }
        if (endMarkerRenderer != null)
        {
            bool endOccupied = (MazeData.GetSquare(MazeData.EndPos.r, MazeData.EndPos.c, MazeData.EndPos.layer) & 0x30) != 0;
            endMarkerRenderer.sharedMaterial = endOccupied ? whiteWallMaterial : blackBreadcrumbMaterial;
        }

        RenderEntireMaze2D();
    }

    void SetupVRRigAndWristMap()
    {
        ForceEnableAllXRInputActions(xrRigRoot.gameObject);

        float startY = GetLayerBaseFloorY(MazeData.StartPos.layer);
        xrRigRoot.position = CellToWorldCenter(MazeData.StartPos.r, MazeData.StartPos.c, startY + 0.02f);

        byte startMask = MazeData.GetSquare(MazeData.StartPos.r, MazeData.StartPos.c, MazeData.StartPos.layer);
        float startYaw = 0f;
        if ((startMask & (1 << AmazingMazeData.DIR_RIGHT)) != 0) startYaw = 90f;
        else if ((startMask & (1 << AmazingMazeData.DIR_DOWN)) != 0) startYaw = 180f;
        else if ((startMask & (1 << AmazingMazeData.DIR_LEFT)) != 0) startYaw = -90f;
        xrRigRoot.eulerAngles = new Vector3(0f, startYaw, 0f);

        Transform camOffset = xrRigRoot.Find("Camera Offset");
        if (camOffset != null && camOffset.localPosition.y < 0.1f)
        {
            camOffset.localPosition = new Vector3(0f, 1.36f, 0f);
        }

        Camera vrCam = xrRigRoot.GetComponentInChildren<Camera>();
        activePlayerHeadOrBody = (vrCam != null) ? vrCam.transform : xrRigRoot;

        if (leftWristTransform == null)
        {
            foreach (Transform t in xrRigRoot.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("left") && (n.Contains("controller") || n.Contains("hand")))
                {
                    leftWristTransform = t;
                    break;
                }
            }
        }

        foreach (LineRenderer lr in xrRigRoot.GetComponentsInChildren<LineRenderer>(true))
        {
            lr.widthMultiplier = 0.004f;
        }

        if (leftWristTransform != null)
        {
            Build3DWristMapTablet(leftWristTransform);
        }
    }

    void ForceEnableAllXRInputActions(GameObject rigRoot)
    {
        foreach (MonoBehaviour mb in FindObjectsOfType<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            Type t = mb.GetType();
            if (t.Name == "InputActionManager")
            {
                var enableMethod = t.GetMethod("EnableInput", BindingFlags.Public | BindingFlags.Instance);
                enableMethod?.Invoke(mb, null);
            }

            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType.Name == "InputActionProperty")
                {
                    object propVal = field.GetValue(mb);
                    if (propVal != null)
                    {
                        var actionProp = field.FieldType.GetProperty("action");
                        object actionObj = actionProp?.GetValue(propVal);
                        var enableAct = actionObj?.GetType().GetMethod("Enable");
                        enableAct?.Invoke(actionObj, null);
                    }
                }
                else if (field.FieldType.Name == "InputActionAsset")
                {
                    object assetObj = field.GetValue(mb);
                    var enableAsset = assetObj?.GetType().GetMethod("Enable");
                    enableAsset?.Invoke(assetObj, null);
                }
            }
        }
    }

    void Build3DWristMapTablet(Transform wristParent)
    {
        if (wristMapObject != null) Destroy(wristMapObject);

        wristMapObject = new GameObject("Mac1984_WristMap");
        wristMapObject.transform.SetParent(wristParent, false);
        wristMapObject.transform.localPosition = wristMapLocalPosition;
        wristMapObject.transform.localEulerAngles = wristMapLocalEuler;

        float aspect = (float)fbHeight / fbWidth;
        float w = wristMapWidthMeters;
        float h = w * aspect;

        GameObject bezel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bezel.name = "MacWindowBezel";
        Destroy(bezel.GetComponent<Collider>());
        bezel.transform.SetParent(wristMapObject.transform, false);
        bezel.transform.localPosition = new Vector3(0f, 0f, 0.003f);
        bezel.transform.localScale = new Vector3(w + 0.01f, h + 0.01f, 0.005f);
        bezel.GetComponent<Renderer>().sharedMaterial = blackEdgeMaterial;

        GameObject screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "MacMapScreen";
        Destroy(screen.GetComponent<Collider>());
        screen.transform.SetParent(wristMapObject.transform, false);
        screen.transform.localPosition = Vector3.zero;
        screen.transform.localScale = new Vector3(w, h, 1f);

        Shader unlitTex = Shader.Find("Unlit/Texture");
        if (unlitTex == null) unlitTex = Shader.Find("Standard");
        wristMapMaterial = new Material(unlitTex) { mainTexture = mac2DMapTex };
        screen.GetComponent<Renderer>().sharedMaterial = wristMapMaterial;
    }

    void OnValidate()
    {
        if (Application.isPlaying && whiteWallMaterial != null)
        {
            if (useSquareMacintoshDots)
                BuildSquareMacintoshDitherTextures();
            BuildCheckerboardMaterial();

            ApplyAllMaterialProperties();
            ConfigureCameraAndGlobalDither();
            ApplyMoveSpeedToXR();

            if (wristMapObject != null)
            {
                wristMapObject.transform.localPosition = wristMapLocalPosition;
                wristMapObject.transform.localEulerAngles = wristMapLocalEuler;
            }

            if (skillLevel != lastBuiltSkillLevel && MazeData != null)
            {
                lastBuiltSkillLevel = skillLevel;
                MazeData.MakeMaze(skillLevel);
                RebuildMazeGeometryOnly();
                IgnoreStairFloorCollisionsForPlayer();
                RepositionPlayerAtStart();
                UpdateNonEuclideanTunnelState();
                UpdateBreadcrumbsAnd2DMap();
            }
            else if (architectureStyle != lastBuiltStyle && MazeData != null)
            {
                RebuildMazeGeometryOnly();
                IgnoreStairFloorCollisionsForPlayer();
                UpdateNonEuclideanTunnelState();
                UpdateBreadcrumbsAnd2DMap();
            }
        }
    }

    /* ============================================================================
     * EXACT 1-BIT QUICKDRAW FRAMEBUFFER PORT + CONTINUOUS POSITION/ANGLE WEDGE
     * ============================================================================ */
    void InitFramebuffer(int w, int h)
    {
        fbWidth = w;
        fbHeight = h;
        fbPixels = new byte[w * h];
        fbColors = new Color32[w * h];
        mac2DMapTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        mac2DMapTex.filterMode = FilterMode.Point;
        mac2DMapTex.wrapMode = TextureWrapMode.Clamp;
        if (wristMapMaterial != null) wristMapMaterial.mainTexture = mac2DMapTex;
    }

    void SetPixelFB(int x, int y, byte val)
    {
        if (x >= 0 && x < fbWidth && y >= 0 && y < fbHeight)
            fbPixels[y * fbWidth + x] = val;
    }

    void FillRectFB(int x0, int y0, int w, int h, byte val)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            if (y < 0 || y >= fbHeight) continue;
            int rowIdx = y * fbWidth;
            for (int x = x0; x < x0 + w; x++)
            {
                if (x >= 0 && x < fbWidth)
                    fbPixels[rowIdx + x] = val;
            }
        }
    }

    void FillGrayRectFB(int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            if (y < 0 || y >= fbHeight) continue;
            int rowIdx = y * fbWidth;
            for (int x = x0; x < x0 + w; x++)
            {
                if (x >= 0 && x < fbWidth)
                    fbPixels[rowIdx + x] = (byte)(((x + y) & 1) == 0 ? 1 : 0);
            }
        }
    }

    void DrawSquare2D(int r, int c)
    {
        if (r < 1 || r > MazeData.MaxRow || c < 1 || c > MazeData.MaxCol) return;
        int ox = MARGIN_X + (c - 1) * TILE_STRIDE;
        int oy = MARGIN_Y + (r - 1) * TILE_STRIDE;

        FillGrayRectFB(ox, oy, 16, 16);

        byte wBase   = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE);
        byte wBridge = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE);
        byte nBase   = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BASE);
        byte nBridge = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BRIDGE);

        int wBaseOpen   = wBase & 0x0F;
        int wBridgeOpen = wBridge & 0x0F;
        int nBaseOpen   = nBase & 0x0F;
        int nBridgeOpen = nBridge & 0x0F;
        bool isRamp     = MazeData.IsRamp(r, c);

        void DrawWideCorridor(int openMask)
        {
            if (openMask == 0) return;
            FillRectFB(ox + 2, oy + 2, 12, 12, 0);

            if ((openMask & (1 << AmazingMazeData.DIR_LEFT)) != 0)
            {
                FillRectFB(ox, oy + 2, 2, 12, 0);
                FillRectFB(ox, oy + 1, 2, 1, 1);
                FillRectFB(ox, oy + 14, 2, 1, 1);
            }
            if ((openMask & (1 << AmazingMazeData.DIR_RIGHT)) != 0)
            {
                FillRectFB(ox + 14, oy + 2, 2, 12, 0);
                FillRectFB(ox + 14, oy + 1, 2, 1, 1);
                FillRectFB(ox + 14, oy + 14, 2, 1, 1);
            }
            if ((openMask & (1 << AmazingMazeData.DIR_UP)) != 0)
            {
                FillRectFB(ox + 2, oy, 12, 2, 0);
                FillRectFB(ox + 1, oy, 1, 2, 1);
                FillRectFB(ox + 14, oy, 1, 2, 1);
            }
            if ((openMask & (1 << AmazingMazeData.DIR_DOWN)) != 0)
            {
                FillRectFB(ox + 2, oy + 14, 12, 2, 0);
                FillRectFB(ox + 1, oy + 14, 1, 2, 1);
                FillRectFB(ox + 14, oy + 14, 1, 2, 1);
            }

            if ((openMask & (1 << AmazingMazeData.DIR_LEFT)) == 0)  FillRectFB(ox + 1, oy + 1, 1, 14, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_RIGHT)) == 0) FillRectFB(ox + 14, oy + 1, 1, 14, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_UP)) == 0)    FillRectFB(ox + 1, oy + 1, 14, 1, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_DOWN)) == 0)  FillRectFB(ox + 1, oy + 14, 14, 1, 1);

            SetPixelFB(ox + 1, oy + 1, 1);
            SetPixelFB(ox + 14, oy + 1, 1);
            SetPixelFB(ox + 1, oy + 14, 1);
            SetPixelFB(ox + 14, oy + 14, 1);
        }

        void DrawWideOverpass(int openMask)
        {
            if (openMask == 0) return;
            if ((openMask & ((1 << AmazingMazeData.DIR_LEFT) | (1 << AmazingMazeData.DIR_RIGHT))) != 0)
            {
                FillRectFB(ox, oy + 2, 16, 12, 0);
                FillRectFB(ox, oy + 1, 16, 1, 1);
                FillRectFB(ox, oy + 14, 16, 1, 1);
            }
            else
            {
                FillRectFB(ox + 2, oy, 12, 16, 0);
                FillRectFB(ox + 1, oy, 1, 16, 1);
                FillRectFB(ox + 14, oy, 1, 16, 1);
            }
        }

        void DrawNarrowCorridor(int openMask)
        {
            if (openMask == 0) return;
            FillRectFB(ox + 6, oy + 6, 4, 4, 0);

            if ((openMask & (1 << AmazingMazeData.DIR_LEFT)) != 0)
            {
                FillRectFB(ox, oy + 6, 6, 4, 0);
                FillRectFB(ox, oy + 5, 6, 1, 1);
                FillRectFB(ox, oy + 10, 6, 1, 1);
            }
            if ((openMask & (1 << AmazingMazeData.DIR_RIGHT)) != 0)
            {
                FillRectFB(ox + 10, oy + 6, 6, 4, 0);
                FillRectFB(ox + 10, oy + 5, 6, 1, 1);
                FillRectFB(ox + 10, oy + 10, 6, 1, 1);
            }
            if ((openMask & (1 << AmazingMazeData.DIR_UP)) != 0)
            {
                FillRectFB(ox + 6, oy, 4, 6, 0);
                FillRectFB(ox + 5, oy, 1, 6, 1);
                FillRectFB(ox + 10, oy, 1, 6, 1);
            }
            if ((openMask & (1 << AmazingMazeData.DIR_DOWN)) != 0)
            {
                FillRectFB(ox + 6, oy + 10, 4, 6, 0);
                FillRectFB(ox + 5, oy + 10, 1, 6, 1);
                FillRectFB(ox + 10, oy + 10, 1, 6, 1);
            }

            if ((openMask & (1 << AmazingMazeData.DIR_LEFT)) == 0)  FillRectFB(ox + 5, oy + 5, 1, 6, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_RIGHT)) == 0) FillRectFB(ox + 10, oy + 5, 1, 6, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_UP)) == 0)    FillRectFB(ox + 5, oy + 5, 6, 1, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_DOWN)) == 0)  FillRectFB(ox + 5, oy + 10, 6, 1, 1);

            SetPixelFB(ox + 5, oy + 5, 1);
            SetPixelFB(ox + 10, oy + 5, 1);
            SetPixelFB(ox + 5, oy + 10, 1);
            SetPixelFB(ox + 10, oy + 10, 1);
        }

        void DrawNarrowOverpass(int openMask)
        {
            if (openMask == 0) return;
            if ((openMask & ((1 << AmazingMazeData.DIR_LEFT) | (1 << AmazingMazeData.DIR_RIGHT))) != 0)
            {
                FillRectFB(ox, oy + 6, 16, 4, 0);
                FillRectFB(ox, oy + 5, 16, 1, 1);
                FillRectFB(ox, oy + 10, 16, 1, 1);
            }
            else
            {
                FillRectFB(ox + 6, oy, 4, 16, 0);
                FillRectFB(ox + 5, oy, 1, 16, 1);
                FillRectFB(ox + 10, oy, 1, 16, 1);
            }
        }

        void DrawOccupiedSubLayer(int subLayer)
        {
            byte sq = MazeData.GetSquare(r, c, subLayer);
            if ((sq & 0x30) == 0) return;

            byte nibs = MazeData.NibMask[r, c, subLayer];
            if (nibs == 0) nibs = (byte)(sq & 0x0F);

            if (AmazingMazeData.IsWideSubLayer(subLayer))
            {
                FillRectFB(ox + 3, oy + 3, 10, 10, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_LEFT)) != 0)  FillRectFB(ox, oy + 3, 3, 10, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_RIGHT)) != 0) FillRectFB(ox + 13, oy + 3, 3, 10, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_UP)) != 0)    FillRectFB(ox + 3, oy, 10, 3, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_DOWN)) != 0)  FillRectFB(ox + 3, oy + 13, 10, 3, 1);
            }
            else
            {
                FillRectFB(ox + 7, oy + 7, 2, 2, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_LEFT)) != 0)  FillRectFB(ox, oy + 7, 7, 2, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_RIGHT)) != 0) FillRectFB(ox + 9, oy + 7, 7, 2, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_UP)) != 0)    FillRectFB(ox + 7, oy, 2, 7, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_DOWN)) != 0)  FillRectFB(ox + 7, oy + 9, 2, 7, 1);
            }
        }

        if (!isRamp)
        {
            if (wBaseOpen != 0)
            {
                DrawWideCorridor(wBaseOpen);
                DrawOccupiedSubLayer(AmazingMazeData.LAYER_WIDE_BASE);
            }
            if (wBridgeOpen != 0)
            {
                DrawWideOverpass(wBridgeOpen);
                DrawOccupiedSubLayer(AmazingMazeData.LAYER_WIDE_BRIDGE);
            }
            if (nBaseOpen != 0)
            {
                DrawNarrowCorridor(nBaseOpen);
                DrawOccupiedSubLayer(AmazingMazeData.LAYER_NARROW_BASE);
            }
            if (nBridgeOpen != 0)
            {
                DrawNarrowOverpass(nBridgeOpen);
                DrawOccupiedSubLayer(AmazingMazeData.LAYER_NARROW_BRIDGE);
            }
        }
        else
        {
            // Draw 16x16 Macintosh Tapered Ramp (Straight or 90-Degree Corner)
            int wideDir = 0, narrowDir = 2;
            for (int d = 0; d < 4; d++)
            {
                if ((wBaseOpen & (1 << d)) != 0) wideDir = d;
                if ((nBaseOpen & (1 << d)) != 0) narrowDir = d;
            }
            bool rampOccupied = ((wBase | nBase) & 0x30) != 0;

            // Map canonical (u, v) in [0..15]x[0..15] where wide entrance is at v = 15 (DIR_DOWN)
            void SetRampPixel(int u, int v, byte val)
            {
                int px, py;
                switch (wideDir)
                {
                    case AmazingMazeData.DIR_DOWN:  px = u;      py = v;      break;
                    case AmazingMazeData.DIR_UP:    px = 15 - u; py = 15 - v; break;
                    case AmazingMazeData.DIR_RIGHT: px = v;      py = 15 - u; break;
                    case AmazingMazeData.DIR_LEFT:  px = 15 - v; py = u;      break;
                    default:                        px = u;      py = v;      break;
                }
                SetPixelFB(ox + px, oy + py, val);
            }

            if (narrowDir == AmazingMazeData.DirOpp(wideDir))
            {
                // Straight Tapered Ramp: narrow on v=0..5, 45-degree diagonal taper on v=6..9, wide on v=10..15
                for (int v = 0; v <= 15; v++)
                {
                    int uL, uR, cL, cR;
                    if (v <= 5)
                    {
                        uL = 5; uR = 10;
                        cL = 7; cR = 8;
                    }
                    else if (v <= 9)
                    {
                        int s = v - 5;
                        uL = 5 - s; uR = 10 + s;
                        cL = 7 - s; cR = 8 + s;
                    }
                    else
                    {
                        uL = 1; uR = 14;
                        cL = 3; cR = 12;
                    }

                    SetRampPixel(uL, v, 1);
                    SetRampPixel(uR, v, 1);
                    for (int u = uL + 1; u <= uR - 1; u++)
                        SetRampPixel(u, v, 0);

                    if (rampOccupied)
                    {
                        for (int u = cL; u <= cR; u++)
                            SetRampPixel(u, v, 1);
                    }
                }
            }
            else
            {
                // 90-Degree Corner Turning Ramp (Level 4)
                // Determine if narrowDir is towards u=0 (left in canonical frame) or u=15 (right in canonical frame)
                int leftOfWide = (wideDir + 1) & 3;
                bool turnToZeroU = (narrowDir == leftOfWide);

                void SetCornerPixel(int u0, int v, byte val)
                {
                    int u = turnToZeroU ? u0 : (15 - u0);
                    SetRampPixel(u, v, val);
                }

                // Wide lower half (v = 10..15)
                for (int v = 10; v <= 15; v++)
                {
                    SetCornerPixel(1, v, 1);
                    SetCornerPixel(14, v, 1);
                    for (int u = 2; u <= 13; u++) SetCornerPixel(u, v, 0);
                    if (rampOccupied)
                    {
                        for (int u = 3; u <= 12; u++) SetCornerPixel(u, v, 1);
                    }
                }

                // Corner transition & narrow exit (v = 5..9)
                for (int u = 0; u <= 10; u++) SetCornerPixel(u, 5, 1);
                SetCornerPixel(1, 10, 1);
                for (int v = 6; v <= 9; v++)
                {
                    int s = v - 5;
                    int uOuter = 10 + s;
                    for (int u = 0; u < uOuter; u++) SetCornerPixel(u, v, 0);
                    SetCornerPixel(uOuter, v, 1);
                    if (v == 9) SetCornerPixel(0, 10, 1);

                    if (rampOccupied)
                    {
                        int cR = 8 + s;
                        int cL = (v <= 8) ? 0 : 3;
                        for (int u = cL; u <= cR; u++) SetCornerPixel(u, v, 1);
                    }
                }
            }
        }

        if (r == MazeData.EndPos.r && c == MazeData.EndPos.c)
        {
            bool endOccupied = (MazeData.GetSquare(r, c, MazeData.EndPos.layer) & 0x30) != 0;
            byte endColor = (byte)(endOccupied ? 0 : 1);
            FillRectFB(ox + 4, oy + 4, 8, 8, endColor);
        }

        if (r == MazeData.StartPos.r && c == MazeData.StartPos.c)
        {
            bool startOccupied = (MazeData.GetSquare(r, c, MazeData.StartPos.layer) & 0x30) != 0;
            byte circleColor = (byte)(startOccupied ? 0 : 1);
            for (int cy = 0; cy < 8; cy++)
            {
                for (int cx = 0; cx < 8; cx++)
                {
                    if ((CIRCLE_ROWS[cy] & (0x80 >> cx)) != 0)
                        SetPixelFB(ox + 4 + cx, oy + 4 + cy, circleColor);
                }
            }
        }
    }

    void RenderEntireMaze2D()
    {
        FillGrayRectFB(0, 0, fbWidth, fbHeight);

        for (int r = 1; r <= MazeData.MaxRow; r++)
            for (int c = 1; c <= MazeData.MaxCol; c++)
                DrawSquare2D(r, c);

        FillRectFB(MARGIN_X - 1, MARGIN_Y - 1, MazeData.MaxCol * TILE_STRIDE + 2, 1, 1);
        FillRectFB(MARGIN_X - 1, MARGIN_Y + MazeData.MaxRow * TILE_STRIDE, MazeData.MaxCol * TILE_STRIDE + 2, 1, 1);
        FillRectFB(MARGIN_X - 1, MARGIN_Y - 1, 1, MazeData.MaxRow * TILE_STRIDE + 2, 1);
        FillRectFB(MARGIN_X + MazeData.MaxCol * TILE_STRIDE, MARGIN_Y - 1, 1, MazeData.MaxRow * TILE_STRIDE + 2, 1);

        FlushFramebufferWithPlayerWedge();
    }

    static float EdgeCross(Vector2 a, Vector2 b, Vector2 p)
    {
        return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
    }

    static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = EdgeCross(a, b, p);
        float d2 = EdgeCross(b, c, p);
        float d3 = EdgeCross(c, a, p);
        bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
        return !(hasNeg && hasPos);
    }

    void FlushFramebufferWithPlayerWedge()
    {
        if (mac2DMapTex == null) return;

        Color32 white = new Color32(255, 255, 255, 255);
        Color32 black = new Color32(0, 0, 0, 255);

        for (int y = 0; y < fbHeight; y++)
        {
            int srcRow = y * fbWidth;
            int dstRow = (fbHeight - 1 - y) * fbWidth;
            for (int x = 0; x < fbWidth; x++)
            {
                fbColors[dstRow + x] = fbPixels[srcRow + x] != 0 ? black : white;
            }
        }

        if (activePlayerHeadOrBody != null)
        {
            Vector3 wp = activePlayerHeadOrBody.position;
            float exactMapX = MARGIN_X + 7.5f + (wp.x / cellSize) * TILE_STRIDE;
            float exactMapY = MARGIN_Y + 7.5f + (-wp.z / cellSize) * TILE_STRIDE;
            Vector2 center = new Vector2(exactMapX, exactMapY);

            float rad = activePlayerHeadOrBody.eulerAngles.y * Mathf.Deg2Rad;
            Vector2 fwd = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
            Vector2 right = new Vector2(-fwd.y, fwd.x);

            Vector2 tip       = center + fwd * 5.2f;
            Vector2 leftWing  = center - fwd * 3.8f - right * 3.8f;
            Vector2 rightWing = center - fwd * 3.8f + right * 3.8f;
            Vector2 notch     = center - fwd * 1.5f;

            int pr = MazeData.PlayerPos.r;
            int pc = MazeData.PlayerPos.c;
            bool cellOccupied = (MazeData.GetSquare(pr, pc, MazeData.PlayerPos.layer) & 0x30) != 0;
            Color32 fg   = cellOccupied ? white : black;
            Color32 halo = cellOccupied ? black : white;

            int minX = Mathf.Max(0, Mathf.FloorToInt(exactMapX - 7));
            int maxX = Mathf.Min(fbWidth - 1, Mathf.CeilToInt(exactMapX + 7));
            int minY = Mathf.Max(0, Mathf.FloorToInt(exactMapY - 7));
            int maxY = Mathf.Min(fbHeight - 1, Mathf.CeilToInt(exactMapY + 7));

            for (int py = minY; py <= maxY; py++)
            {
                for (int px = minX; px <= maxX; px++)
                {
                    Vector2 p = new Vector2(px, py);
                    if (PointInTriangle(p, tip, leftWing, notch) || PointInTriangle(p, tip, notch, rightWing))
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int hx = px + dx;
                                int hy = py + dy;
                                if (hx >= 0 && hx < fbWidth && hy >= 0 && hy < fbHeight)
                                    fbColors[(fbHeight - 1 - hy) * fbWidth + hx] = halo;
                            }
                        }
                    }
                }
            }

            for (int py = minY; py <= maxY; py++)
            {
                for (int px = minX; px <= maxX; px++)
                {
                    Vector2 p = new Vector2(px, py);
                    if (PointInTriangle(p, tip, leftWing, notch) || PointInTriangle(p, tip, notch, rightWing))
                    {
                        fbColors[(fbHeight - 1 - py) * fbWidth + px] = fg;
                    }
                }
            }
        }

        if (isRunMode && fbWidth > 36 && fbHeight > 12)
        {
            for (int by = 1; by <= 7; by++)
            {
                for (int bx = fbWidth - 25; bx <= fbWidth - 2; bx++)
                {
                    bool border = (by == 1 || by == 7 || bx == fbWidth - 25 || bx == fbWidth - 2);
                    fbColors[(fbHeight - 1 - by) * fbWidth + bx] = border ? white : black;
                }
            }
        }

        mac2DMapTex.SetPixels32(fbColors);
        mac2DMapTex.Apply();
    }

    void SpawnSimpleDesktopPlayer()
    {
        GameObject playerObject = new GameObject("DesktopPlayer");
        CharacterController cc = playerObject.AddComponent<CharacterController>();
        cc.radius = 0.35f;
        cc.height = 1.7f;
        cc.center = new Vector3(0f, 0.85f, 0f);
        cc.stepOffset = 0.50f;
        cc.slopeLimit = 60f;

        float startY = GetLayerBaseFloorY(MazeData.StartPos.layer);
        playerObject.transform.position = CellToWorldCenter(MazeData.StartPos.r, MazeData.StartPos.c, startY + 0.05f);

        byte startMask = MazeData.GetSquare(MazeData.StartPos.r, MazeData.StartPos.c, MazeData.StartPos.layer);
        float startYaw = 0f;
        if ((startMask & (1 << AmazingMazeData.DIR_RIGHT)) != 0) startYaw = 90f;
        else if ((startMask & (1 << AmazingMazeData.DIR_DOWN)) != 0) startYaw = 180f;
        else if ((startMask & (1 << AmazingMazeData.DIR_LEFT)) != 0) startYaw = -90f;
        playerObject.transform.eulerAngles = new Vector3(0f, startYaw, 0f);

        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.transform.SetParent(playerObject.transform, false);
            cam.transform.localPosition = new Vector3(0f, 1.55f, 0f);
            cam.transform.localRotation = Quaternion.identity;
        }

        activePlayerHeadOrBody = playerObject.transform;
        playerObject.AddComponent<SimpleDesktopMazeWalker>().Init(this, cc, cam != null ? cam.transform : null);
    }

    void OnGUI()
    {
        if (showDesktopHudMap && mac2DMapTex != null)
        {
            GUI.DrawTexture(
                new Rect(12, 12, mac2DMapTex.width * hudMapScale, mac2DMapTex.height * hudMapScale),
                mac2DMapTex
            );
        }
    }
}

public class SimpleDesktopMazeWalker : MonoBehaviour
{
    private AmazingMazeBuilder3D builder;
    private CharacterController cc;
    private Transform camTransform;
    private float pitch = 0f;

    public void Init(AmazingMazeBuilder3D builder, CharacterController cc, Transform cam)
    {
        this.builder = builder;
        this.cc = cc;
        this.camTransform = cam;
        LockCursor();
    }

    void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (Cursor.lockState == CursorLockMode.Locked) UnlockCursor();
            else LockCursor();
        }
        else if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
        {
            LockCursor();
        }

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            float mx = Input.GetAxis("Mouse X") * 2.2f;
            float my = Input.GetAxis("Mouse Y") * 2.2f;
            transform.Rotate(0f, mx, 0f);
            pitch = Mathf.Clamp(pitch - my, -80f, 80f);
            if (camTransform != null) camTransform.localEulerAngles = new Vector3(pitch, 0f, 0f);
        }

        float speed = builder.isRunMode ? builder.runSpeed : builder.walkSpeed;
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        Vector3 move = (transform.right * h + transform.forward * v).normalized * speed;
        cc.Move(move * Time.deltaTime);

        float targetY = builder.GetTargetPlayerFloorY(transform.position) + 0.05f;
        Vector3 pos = transform.position;
        float nextY = Mathf.Lerp(pos.y, targetY, Mathf.Clamp01(Time.deltaTime * 18f));
        if (Mathf.Abs(nextY - pos.y) > 0.0005f)
        {
            pos.y = nextY;
            cc.enabled = false;
            transform.position = pos;
            Physics.SyncTransforms();
            cc.enabled = true;
        }
#endif
    }
}