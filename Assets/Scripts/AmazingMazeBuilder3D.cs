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
    [Tooltip("0 = Level 1 (Flat), 1 = Level 2 (Non-Euclidean Archway Overpasses!), 2 = Level 3, 3 = Level 4")]
    [Range(0, 3)] public int skillLevel = 1;
    public float cellSize = 3.0f;
    public float wallHeight = 0.85f;
    [Tooltip("Height of the enclosed Non-Euclidean Archway Tunnels")]
    public float tunnelArchHeight = 2.25f;
    public float wallThickness = 0.36f;
    [Range(0.01f, 0.08f)] public float blackEdgeOutlineWidth = 0.03f;

    [Header("Unoccupied Area Checkerboard Settings")]
    public bool fillUnoccupiedWithCheckerboard = true;
    [Range(2, 16)] public int checkerSquaresPerMeter = 6;

    [Header("Dither3D Settings (Square Macintosh Pixels!)")]
    public bool useSquareMacintoshDots = true;
    public bool crisp1BitMacPixels = true;
    public Texture3D dither3DTexture;
    public Texture2D ditherRampTexture;
    [Range(2f, 10f)] public float dotScale = 6.2f;
    [Range(0f, 1f)] public float dotSizeVariability = 0.0f;
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

    // Non-Euclidean Stencil Materials (Ref 1 = North-South Tunnel, Ref 2 = East-West Tunnel)
    private Material portalMaskNS_Mat;
    private Material portalMaskEW_Mat;
    private Material tunnelWallNS_Mat, tunnelFloorNS_Mat, tunnelCrumbNS_Mat, tunnelEdgeNS_Mat;
    private Material tunnelWallEW_Mat, tunnelFloorEW_Mat, tunnelCrumbEW_Mat, tunnelEdgeEW_Mat;

    public AmazingMazeData MazeData { get; private set; }
    private GameObject mazeRoot;
    private Transform activePlayerHeadOrBody;
    private GameObject wristMapObject;
    private Material wristMapMaterial;

    private Dictionary<(int r, int c, int subLayer), GameObject[]> breadcrumbMeshes = new Dictionary<(int r, int c, int subLayer), GameObject[]>();

    // Tracks each Non-Euclidean Bridge Cell's NS and EW colliders & portal masks
    private class BridgeCellInstance
    {
        public int r, c;
        public Vector3 worldCenter;
        public int nsSubLayer;
        public int ewSubLayer;
        public List<Collider> nsWallColliders = new List<Collider>();
        public List<Collider> ewWallColliders = new List<Collider>();
        public List<GameObject> portalQuads = new List<GameObject>();

        // Per-cell material instances so closer bridge cells ALWAYS render & write Z-depth BEFORE distant bridge cells!
        public Material cellPortalNS, cellPortalEW;
        public Material cellWallNS, cellFloorNS, cellCrumbNS, cellEdgeNS;
        public Material cellWallEW, cellFloorEW, cellCrumbEW, cellEdgeEW;
    }
    private Dictionary<(int r, int c), BridgeCellInstance> bridgeCells = new Dictionary<(int r, int c), BridgeCellInstance>();
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

        ApplyMoveSpeedToXR();
        ConfigureCameraAndGlobalDither();
        UpdateNonEuclideanTunnelState();
        UpdateBreadcrumbsAnd2DMap();
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
            UpdateNonEuclideanTunnelState();
            UpdateBreadcrumbsAnd2DMap();
        }
        if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift))
        {
            ToggleRunMode();
        }
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

        // Keep bridge cells sorted front-to-back from camera so near tunnel walls always Z-occlude distant portals!
        UpdateBridgeRenderQueuesFrontToBack();

        float curYaw = activePlayerHeadOrBody.eulerAngles.y;
        if ((curPos - lastPlayerPos).sqrMagnitude > 0.0001f || Mathf.Abs(Mathf.DeltaAngle(curYaw, lastPlayerYaw)) > 0.25f)
        {
            lastPlayerPos = curPos;
            lastPlayerYaw = curYaw;
            FlushFramebufferWithPlayerWedge();
        }
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
        }
    }

    void PollJoystickHatRunToggleSafe()
    {
        bool hatClicked = false;

        try
        {
            var rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            var leftHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);

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
     * NON-EUCLIDEAN STENCIL + COLLIDER CONTROLLER
     * Visually: BOTH the NS Tunnel (Stencil Ref 1) and EW Tunnel (Stencil Ref 2)
     * are rendered simultaneously through their respective portal windows!
     * Only when the player physically steps INSIDE a bridge cell do we set that
     * tunnel's StencilComp to Always (8) so turning your head inside the tunnel
     * never clips against the doorway portal plane.
     * Physically: Enables the NS or EW wall colliders based on topological BFS!
     * ============================================================================ */
    /* ============================================================================
     * FRONT-TO-BACK DEPTH + STENCIL CONTROLLER FOR NON-EUCLIDEAN ARCHWAYS
     * Sorts bridge cells by distance from the player's camera every frame and assigns
     * ascending RenderQueues (2005 + i*10). Nearer tunnel walls write to the Z-buffer
     * BEFORE distant portals are tested, making it impossible for distant arches to
     * punch holes through nearer walls!
     * ============================================================================ */
    void UpdateBridgeRenderQueuesFrontToBack()
    {
        if (bridgeCells.Count == 0 || activePlayerHeadOrBody == null) return;

        Vector3 camPos = activePlayerHeadOrBody.position;
        int pr = MazeData.PlayerPos.r;
        int pc = MazeData.PlayerPos.c;
        int pLayer = MazeData.PlayerPos.layer;

        sortedBridgeList.Clear();
        foreach (var kvp in bridgeCells)
            sortedBridgeList.Add(kvp.Value);

        // Sort closest to furthest from the camera
        sortedBridgeList.Sort((a, b) =>
        {
            float da = (a.worldCenter - camPos).sqrMagnitude;
            float db = (b.worldCenter - camPos).sqrMagnitude;
            return da.CompareTo(db);
        });

        for (int i = 0; i < sortedBridgeList.Count; i++)
        {
            BridgeCellInstance br = sortedBridgeList[i];
            bool playerInsideThisBridge = (pr == br.r && pc == br.c);

            // Base queue for the i-th closest bridge cell (each cell gets a 10-queue slot: 2005, 2015, 2025...)
            int baseQ = 2005 + i * 10;

            if (playerInsideThisBridge)
            {
                // When standing inside this arch, disable its doorway portal quads so they don't clip the camera,
                // render the active tunnel at Queue 2002 with Stencil Always (8) so it writes solid Z-depth immediately,
                // and hide the perpendicular tunnel (Stencil Never = 1)!
                foreach (var pq in br.portalQuads)
                    if (pq != null && pq.activeSelf) pq.SetActive(false);

                bool inNS = (pLayer == br.nsSubLayer);
                ConfigureCellTunnelMaterials(br.cellWallNS, br.cellFloorNS, br.cellCrumbNS, br.cellEdgeNS, inNS ? 8 : 1, 2002);
                ConfigureCellTunnelMaterials(br.cellWallEW, br.cellFloorEW, br.cellCrumbEW, br.cellEdgeEW, inNS ? 1 : 8, 2002);
            }
            else
            {
                foreach (var pq in br.portalQuads)
                    if (pq != null && !pq.activeSelf) pq.SetActive(true);

                // Check which axis of this bridge cell is closer to the camera so that doorway + wall renders first
                float dx = Mathf.Abs(camPos.x - br.worldCenter.x);
                float dz = Mathf.Abs(camPos.z - br.worldCenter.z);
                bool nsDoorIsCloser = (dz >= dx);

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

        // Topological BFS for physical wall colliders
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

            if (pr == br.r && pc == br.c)
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
                    bool isFloorHit = hitNormal.y > 0.7f;
                    bool isLegalCorridorJump = isFloorHit &&
                                               IsLegalStraightCorridorTeleport(hitR, hitC, out int clampedR, out int clampedC) &&
                                               (clampedR == hitR && clampedC == hitC) &&
                                               !(hitR == MazeData.PlayerPos.r && hitC == MazeData.PlayerPos.c);

                    if (isLegalCorridorJump)
                    {
                        hasValidPendingTeleportHit = true;
                        pendingTeleportWorldHit = hitPos;

                        SetTeleportRayVisualColor(rayInteractor.gameObject, true);
                        teleportReticleObj.transform.position = new Vector3(hitPos.x, 0.025f, hitPos.z);
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

            if (IsLegalStraightCorridorTeleport(targetR, targetC, out int clampedR, out int clampedC))
            {
                Vector3 destWorld = CellToWorldCenter(clampedR, clampedC, xrRigRoot.position.y);
                Vector3 headOffset = activePlayerHeadOrBody.position - xrRigRoot.position;
                headOffset.y = 0f;
                xrRigRoot.position = destWorld - headOffset;

                StepPlayerTowardCell(clampedR, clampedC);
                UpdateNonEuclideanTunnelState();
            }
        }

        if (!anyTeleportRayActive)
        {
            hasValidPendingTeleportHit = false;
        }
        wasTeleportRayActiveLastFrame = anyTeleportRayActive;
    }

    bool IsLegalStraightCorridorTeleport(int targetR, int targetC, out int clampedR, out int clampedC)
    {
        clampedR = MazeData.PlayerPos.r;
        clampedC = MazeData.PlayerPos.c;

        int dr = targetR - clampedR;
        int dc = targetC - clampedC;

        if (dr != 0 && dc != 0)
            return false;

        if (dr == 0 && dc == 0)
            return true;

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
            if (xrRigRoot != null)
            {
                Vector3 legalWorldPos = CellToWorldCenter(MazeData.PlayerPos.r, MazeData.PlayerPos.c, xrRigRoot.position.y);
                Vector3 headOffset = activePlayerHeadOrBody.position - xrRigRoot.position;
                headOffset.y = 0f;
                xrRigRoot.position = legalWorldPos - headOffset;
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

        if (tunnelWallNS_Mat == null)
        {
            tunnelWallNS_Mat = CreateStencilClone(whiteWallMaterial, "TunnelWall_NS_Ref1", 1);
            tunnelFloorNS_Mat = CreateStencilClone(whiteFloorMaterial, "TunnelFloor_NS_Ref1", 1);
            tunnelCrumbNS_Mat = CreateStencilClone(blackBreadcrumbMaterial, "TunnelCrumb_NS_Ref1", 1);
            tunnelEdgeNS_Mat = CreateStencilClone(blackEdgeMaterial, "TunnelEdge_NS_Ref1", 1);

            tunnelWallEW_Mat = CreateStencilClone(whiteWallMaterial, "TunnelWall_EW_Ref2", 2);
            tunnelFloorEW_Mat = CreateStencilClone(whiteFloorMaterial, "TunnelFloor_EW_Ref2", 2);
            tunnelCrumbEW_Mat = CreateStencilClone(blackBreadcrumbMaterial, "TunnelCrumb_EW_Ref2", 2);
            tunnelEdgeEW_Mat = CreateStencilClone(blackEdgeMaterial, "TunnelEdge_EW_Ref2", 2);
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

        if (tunnelWallNS_Mat != null)
        {
            int nsComp = tunnelWallNS_Mat.HasProperty("_StencilComp") ? tunnelWallNS_Mat.GetInt("_StencilComp") : 3;
            int ewComp = tunnelWallEW_Mat.HasProperty("_StencilComp") ? tunnelWallEW_Mat.GetInt("_StencilComp") : 3;

            ApplyDitherPropertiesToMaterial(tunnelWallNS_Mat, Color.white, lightExposure, lightOffset, 1, nsComp);
            ApplyDitherPropertiesToMaterial(tunnelFloorNS_Mat, new Color(0.95f, 0.95f, 0.95f), lightExposure, lightOffset, 1, nsComp);
            ApplyDitherPropertiesToMaterial(tunnelCrumbNS_Mat, new Color(0.04f, 0.04f, 0.04f), 0.1f, -0.8f, 1, nsComp);
            ApplyDitherPropertiesToMaterial(tunnelEdgeNS_Mat, Color.black, 0.0f, -2.0f, 1, nsComp);

            ApplyDitherPropertiesToMaterial(tunnelWallEW_Mat, Color.white, lightExposure, lightOffset, 2, ewComp);
            ApplyDitherPropertiesToMaterial(tunnelFloorEW_Mat, new Color(0.95f, 0.95f, 0.95f), lightExposure, lightOffset, 2, ewComp);
            ApplyDitherPropertiesToMaterial(tunnelCrumbEW_Mat, new Color(0.04f, 0.04f, 0.04f), 0.1f, -0.8f, 2, ewComp);
            ApplyDitherPropertiesToMaterial(tunnelEdgeEW_Mat, Color.black, 0.0f, -2.0f, 2, ewComp);
        }
    }

    void ApplyDitherPropertiesToMaterial(Material mat, Color baseColor, float exposure, float offset, int stencilRef, int stencilComp)
    {
        if (mat == null) return;
        mat.color = baseColor;

        Texture3D active3DTex = (useSquareMacintoshDots && generatedSquareDither3D != null) ? generatedSquareDither3D : dither3DTexture;
        Texture2D activeRamp = (useSquareMacintoshDots && generatedSquareRamp2D != null) ? generatedSquareRamp2D : ditherRampTexture;

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

    /* ============================================================================
     * 3D MAZE GEOMETRY BUILDER (SEAMLESS ARCHWAYS + DUAL STENCIL PORTAL TUNNELS)
     * ============================================================================ */
    public void RebuildMazeGeometryOnly()
    {
        lastBuiltStyle = architectureStyle;
        if (mazeRoot != null) Destroy(mazeRoot);
        mazeRoot = new GameObject("MazeGeometryRoot");
        breadcrumbMeshes.Clear();
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
                int wBaseOpen = insideGrid ? (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) & 0x0F) : 0;
                int wBridgeOpen = insideGrid ? (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE) & 0x0F) : 0;

                if (wBaseOpen == 0 && wBridgeOpen == 0)
                {
                    if (fillUnoccupiedWithCheckerboard)
                        BuildUnoccupiedCheckerboardCell(r, c);
                }
                else if (wBridgeOpen != 0)
                {
                    // NON-EUCLIDEAN OVERPASS CELL: Dual simultaneous Stencil Portal Archway!
                    BuildSimultaneousNonEuclideanBridgeCell(r, c, wBaseOpen, wBridgeOpen);
                }
                else
                {
                    BuildWideCellFloorAndBreadcrumbs(mazeRoot.transform, r, c, AmazingMazeData.LAYER_WIDE_BASE, whiteFloorMaterial, blackBreadcrumbMaterial, true);
                    if (architectureStyle == WallArchitectureStyle.CycladicRounded)
                        BuildCycladicCellWalls(r, c, wBaseOpen);
                }
            }
        }

        if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
        {
            BuildSeamlessFlatWallsWithPhysicalOutlines();
        }

        float markerY = Mathf.Max(0.55f, wallHeight * 0.65f);
        SpawnMarkerPrimitive(PrimitiveType.Sphere, MazeData.StartPos.r, MazeData.StartPos.c, markerY, "StartSphere_Marker");
        SpawnMarkerPrimitive(PrimitiveType.Cube, MazeData.EndPos.r, MazeData.EndPos.c, markerY, "GoalCube_Marker");
    }

    /* ============================================================================
        * SIMULTANEOUS DUAL-PORTAL NON-EUCLIDEAN ARCHWAY BUILDER
        * Uses Outward-Facing (Cull Back) Portal Quads at Queue 2005 (after normal walls
        * at 2000, before Stencil Tunnel interiors at 2010) so looking into the Left
        * opening and Right opening simultaneously NEVER overwrites each other's stencil!
        * ============================================================================ */
    void BuildSimultaneousNonEuclideanBridgeCell(int r, int c, int wBaseOpen, int wBridgeOpen)
    {
        Vector3 center = CellToWorldCenter(r, c, 0f);
        GameObject bridgeRoot = new GameObject($"NonEuclideanBridgeCell_{r}_{c}");
        bridgeRoot.transform.SetParent(mazeRoot.transform, false);
        bridgeRoot.transform.position = center;

        GameObject sharedFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        sharedFloor.name = "SharedBridgeWhiteFloor";
        sharedFloor.transform.SetParent(bridgeRoot.transform, false);
        sharedFloor.transform.localPosition = new Vector3(0f, -0.05f, 0f);
        Vector3 floorScale = new Vector3(cellSize, 0.1f, cellSize);
        sharedFloor.transform.localScale = floorScale;
        NormalizeMeshUVsToWorldSpacePosition(sharedFloor, PrimitiveType.Cube, center + new Vector3(0f, -0.05f, 0f), floorScale);
        sharedFloor.GetComponent<Renderer>().sharedMaterial = whiteFloorMaterial;
        ConfigureFloorTeleportationArea(sharedFloor);

        bool baseIsNS = (wBaseOpen & ((1 << AmazingMazeData.DIR_UP) | (1 << AmazingMazeData.DIR_DOWN))) != 0;
        int nsSubLayer = baseIsNS ? AmazingMazeData.LAYER_WIDE_BASE : AmazingMazeData.LAYER_WIDE_BRIDGE;
        int ewSubLayer = baseIsNS ? AmazingMazeData.LAYER_WIDE_BRIDGE : AmazingMazeData.LAYER_WIDE_BASE;

        // Give every bridge cell its own unique pair of Stencil IDs (1..254) so one arch's portal window
        // NEVER unmasks another arch's interior walls in the background!
        int bridgeIndex = bridgeCells.Count;
        int nsStencilRef = ((bridgeIndex * 2) % 250) + 1;
        int ewStencilRef = nsStencilRef + 1;

        var brInfo = new BridgeCellInstance
        {
            r = r,
            c = c,
            worldCenter = center,
            nsSubLayer = nsSubLayer,
            ewSubLayer = ewSubLayer,
            cellPortalNS = new Material(portalMaskNS_Mat) { name = $"PortalNS_{r}_{c}_Ref{nsStencilRef}" },
            cellPortalEW = new Material(portalMaskEW_Mat) { name = $"PortalEW_{r}_{c}_Ref{ewStencilRef}" },
            cellWallNS = CreateStencilClone(whiteWallMaterial, $"WallNS_{r}_{c}", nsStencilRef),
            cellFloorNS = CreateStencilClone(whiteFloorMaterial, $"FloorNS_{r}_{c}", nsStencilRef),
            cellCrumbNS = CreateStencilClone(blackBreadcrumbMaterial, $"CrumbNS_{r}_{c}", nsStencilRef),
            cellEdgeNS = CreateStencilClone(blackEdgeMaterial, $"EdgeNS_{r}_{c}", nsStencilRef),
            cellWallEW = CreateStencilClone(whiteWallMaterial, $"WallEW_{r}_{c}", ewStencilRef),
            cellFloorEW = CreateStencilClone(whiteFloorMaterial, $"FloorEW_{r}_{c}", ewStencilRef),
            cellCrumbEW = CreateStencilClone(blackBreadcrumbMaterial, $"CrumbEW_{r}_{c}", ewStencilRef),
            cellEdgeEW = CreateStencilClone(blackEdgeMaterial, $"EdgeEW_{r}_{c}", ewStencilRef)
        };
        brInfo.cellPortalNS.SetInt("_StencilRef", nsStencilRef);
        brInfo.cellPortalEW.SetInt("_StencilRef", ewStencilRef);

        bridgeCells[(r, c)] = brInfo;

        float half = cellSize * 0.5f;
        float wt = wallThickness;
        float halfWT = wt * 0.5f;
        float th = tunnelArchHeight;
        float roofThick = 0.24f;
        float doorH = th - roofThick;
        float innerSpan = cellSize - wt;
        float ew = blackEdgeOutlineWidth;

        // 1. Outward-Facing (Cull Back) Stencil Portal Windows with this cell's unique Stencil Ref IDs!
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_North_Ref{nsStencilRef}", new Vector3(0f, doorH * 0.5f, half - halfWT), new Vector3(innerSpan, doorH, 1f), Quaternion.Euler(0f, 180f, 0f), brInfo.cellPortalNS));
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_South_Ref{nsStencilRef}", new Vector3(0f, doorH * 0.5f, -(half - halfWT)), new Vector3(innerSpan, doorH, 1f), Quaternion.identity, brInfo.cellPortalNS));
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_East_Ref{ewStencilRef}", new Vector3(half - halfWT, doorH * 0.5f, 0f), new Vector3(innerSpan, doorH, 1f), Quaternion.Euler(0f, -90f, 0f), brInfo.cellPortalEW));
        brInfo.portalQuads.Add(CreatePortalQuad(bridgeRoot.transform, $"Portal_West_Ref{ewStencilRef}", new Vector3(-(half - halfWT), doorH * 0.5f, 0f), new Vector3(innerSpan, doorH, 1f), Quaternion.Euler(0f, 90f, 0f), brInfo.cellPortalEW));

        // 2. Exterior Shared Archway Frame (Upper Pillar Extensions from wallHeight to th)
        float upperPillarH = th - wallHeight;
        float upperPillarMidY = wallHeight + upperPillarH * 0.5f;

        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 upperPillarCenter = center + new Vector3(sx * half, upperPillarMidY, sz * half);
                Vector3 upperPillarScale = new Vector3(wt, upperPillarH, wt);

                if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
                {
                    CreateWallBoxWithMaterial(bridgeRoot.transform, "ArchUpperPillar", upperPillarCenter, upperPillarScale, whiteWallMaterial);

                    for (int ex = -1; ex <= 1; ex += 2)
                    {
                        for (int ez = -1; ez <= 1; ez += 2)
                        {
                            bool isOuterX = (ex == sx);
                            bool isOuterZ = (ez == sz);
                            float topY = (isOuterX && isOuterZ) ? th : doorH;
                            float segH = topY - wallHeight;
                            float segMidY = wallHeight + segH * 0.5f;

                            Vector3 edgePos = center + new Vector3(sx * half + ex * halfWT, segMidY, sz * half + ez * halfWT);
                            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, edgePos, new Vector3(ew, segH + ew, ew), blackEdgeMaterial);
                        }
                    }
                }
                else
                {
                    GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Destroy(post.GetComponent<Collider>());
                    post.transform.SetParent(bridgeRoot.transform, false);
                    post.transform.position = center + new Vector3(sx * half, th * 0.5f, sz * half);
                    Vector3 postScale = new Vector3(wt, th * 0.5f, wt);
                    post.transform.localScale = postScale;
                    NormalizeMeshUVsToWorldSpacePosition(post, PrimitiveType.Cylinder, post.transform.position, postScale);
                    post.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;
                }
            }
        }

        if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
        {
            float fullWidth = cellSize + wt;
            Vector3 roofCenter = center + new Vector3(0f, th - roofThick * 0.5f, 0f);
            Vector3 roofScale = new Vector3(fullWidth, roofThick, fullWidth);
            CreateWallBoxWithMaterial(bridgeRoot.transform, "SharedArchRoof", roofCenter, roofScale, whiteWallMaterial);

            float rHalf = fullWidth * 0.5f;
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, th, rHalf), new Vector3(fullWidth + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, th, -rHalf), new Vector3(fullWidth + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(rHalf, th, 0f), new Vector3(ew, ew, fullWidth + ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(-rHalf, th, 0f), new Vector3(ew, ew, fullWidth + ew), blackEdgeMaterial);

            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, doorH, half + halfWT), new Vector3(innerSpan + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(0f, doorH, -(half + halfWT)), new Vector3(innerSpan + ew, ew, ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(half + halfWT, doorH, 0f), new Vector3(ew, ew, innerSpan + ew), blackEdgeMaterial);
            CreateBlackEdgeBarWithMaterial(bridgeRoot.transform, center + new Vector3(-(half + halfWT), doorH, 0f), new Vector3(ew, ew, innerSpan + ew), blackEdgeMaterial);
        }
        else
        {
            BuildCycladicCrossVaultRoof(bridgeRoot.transform, center, th, innerSpan, cellSize + wt);
        }

        // 3. Build NORTH-SOUTH Tunnel Interior using this cell's unique Stencil Ref!
        BuildWideCellFloorAndBreadcrumbs(bridgeRoot.transform, r, c, nsSubLayer, brInfo.cellFloorNS, brInfo.cellCrumbNS, false);
        BuildInwardStencilTunnelWalls(bridgeRoot.transform, center, alongZ: true, doorH, innerSpan, cellSize + wt, brInfo.cellWallNS, brInfo.cellEdgeNS, brInfo.nsWallColliders);

        // 4. Build EAST-WEST Tunnel Interior using this cell's unique Stencil Ref!
        BuildWideCellFloorAndBreadcrumbs(bridgeRoot.transform, r, c, ewSubLayer, brInfo.cellFloorEW, brInfo.cellCrumbEW, false);
        BuildInwardStencilTunnelWalls(bridgeRoot.transform, center, alongZ: false, doorH, innerSpan, cellSize + wt, brInfo.cellWallEW, brInfo.cellEdgeEW, brInfo.ewWallColliders);
    }

    void BuildInwardStencilTunnelWalls(Transform parent, Vector3 cellCenter, bool alongZ, float doorH, float innerSpan, float tunnelLength, Material stencilWallMat, Material stencilEdgeMat, List<Collider> outColliders)
    {
        float halfInner = innerSpan * 0.5f;
        float ew = blackEdgeOutlineWidth;

        if (alongZ)
        {
            // North-South Tunnel: East inner wall (facing West/-X) and West inner wall (facing East/+X)
            CreateInwardWallQuad(parent, "NS_EastInnerWall_Ref1", cellCenter + new Vector3(halfInner, doorH * 0.5f, 0f), new Vector3(tunnelLength, doorH, 1f), Quaternion.Euler(0f, 90f, 0f), stencilWallMat);
            CreateInwardWallQuad(parent, "NS_WestInnerWall_Ref1", cellCenter + new Vector3(-halfInner, doorH * 0.5f, 0f), new Vector3(tunnelLength, doorH, 1f), Quaternion.Euler(0f, -90f, 0f), stencilWallMat);

            if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
            {
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(halfInner, ew * 0.5f, 0f), new Vector3(ew, ew, tunnelLength), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(-halfInner, ew * 0.5f, 0f), new Vector3(ew, ew, tunnelLength), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(halfInner, doorH, 0f), new Vector3(ew, ew, tunnelLength), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(-halfInner, doorH, 0f), new Vector3(ew, ew, tunnelLength), stencilEdgeMat);
            }

            outColliders.Add(CreateTunnelCollider(parent, "NS_EastCol", cellCenter + new Vector3(cellSize * 0.5f, 2.25f, 0f), new Vector3(wallThickness, 4.5f, cellSize)));
            outColliders.Add(CreateTunnelCollider(parent, "NS_WestCol", cellCenter + new Vector3(-cellSize * 0.5f, 2.25f, 0f), new Vector3(wallThickness, 4.5f, cellSize)));
        }
        else
        {
            // East-West Tunnel: North inner wall (facing South/-Z) and South inner wall (facing North/+Z)
            CreateInwardWallQuad(parent, "EW_NorthInnerWall_Ref2", cellCenter + new Vector3(0f, doorH * 0.5f, halfInner), new Vector3(tunnelLength, doorH, 1f), Quaternion.identity, stencilWallMat);
            CreateInwardWallQuad(parent, "EW_SouthInnerWall_Ref2", cellCenter + new Vector3(0f, doorH * 0.5f, -halfInner), new Vector3(tunnelLength, doorH, 1f), Quaternion.Euler(0f, 180f, 0f), stencilWallMat);

            if (architectureStyle == WallArchitectureStyle.FlatEdgedMac1Bit)
            {
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, ew * 0.5f, halfInner), new Vector3(tunnelLength, ew, ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, ew * 0.5f, -halfInner), new Vector3(tunnelLength, ew, ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, doorH, halfInner), new Vector3(tunnelLength, ew, ew), stencilEdgeMat);
                CreateBlackEdgeBarWithMaterial(parent, cellCenter + new Vector3(0f, doorH, -halfInner), new Vector3(tunnelLength, ew, ew), stencilEdgeMat);
            }

            outColliders.Add(CreateTunnelCollider(parent, "EW_NorthCol", cellCenter + new Vector3(0f, 2.25f, cellSize * 0.5f), new Vector3(cellSize, 4.5f, wallThickness)));
            outColliders.Add(CreateTunnelCollider(parent, "EW_SouthCol", cellCenter + new Vector3(0f, 2.25f, -cellSize * 0.5f), new Vector3(cellSize, 4.5f, wallThickness)));
        }
    }

    void CreateInwardWallQuad(Transform parent, string name, Vector3 worldPos, Vector3 scale, Quaternion rot, Material mat)
    {
        GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = name;
        Destroy(q.GetComponent<Collider>());
        q.transform.SetParent(parent, false);
        q.transform.position = worldPos;
        q.transform.rotation = rot;
        q.transform.localScale = scale;
        NormalizeMeshUVsToWorldSpacePosition(q, PrimitiveType.Quad, worldPos, scale);
        q.GetComponent<Renderer>().sharedMaterial = mat;
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
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return q;
    }

    void BuildSeamlessFlatWallsWithPhysicalOutlines()
    {
        int R = MazeData.MaxRow;
        int C = MazeData.MaxCol;

        bool[,] hWall = new bool[R + 2, C + 2];
        bool[,] vWall = new bool[R + 2, C + 2];

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
            return ((MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) |
                     MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE)) & 0x0F) != 0;
        }

        for (int r = 1; r <= R; r++)
        {
            for (int c = 1; c <= C; c++)
            {
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
        float ew = blackEdgeOutlineWidth;
        float spanLen = cellSize - wt;

        Vector3 VertexWorldPos(int vr, int vc)
        {
            float vx = (vc - 0.5f) * cellSize;
            float vz = -(vr - 0.5f) * cellSize;
            return new Vector3(vx, 0f, vz);
        }

        bool IsVertexTouchingBridgeCell(int vr, int vc)
        {
            return IsBridgeCell(vr, vc) || IsBridgeCell(vr + 1, vc) || IsBridgeCell(vr, vc + 1) || IsBridgeCell(vr + 1, vc + 1);
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

        // Build lower 0..0.85m Corner Posts at ALL vertices (including bridge corners, since ArchUpperPillar now starts at y = 0.85m!)
        for (int vr = 0; vr <= R; vr++)
        {
            for (int vc = 0; vc <= C; vc++)
            {
                bool isBridgeCorner = IsVertexTouchingBridgeCell(vr, vc);
                bool hasN = (vr >= 1) && vWall[vr, vc];
                bool hasS = (vr < R) && vWall[vr + 1, vc];
                bool hasW = (vc >= 1) && hWall[vr, vc];
                bool hasE = (vc < C) && hWall[vr, vc + 1];

                if (!isBridgeCorner && !hasN && !hasS && !hasW && !hasE) continue;

                Vector3 vPos = VertexWorldPos(vr, vc);
                Vector3 postScale = new Vector3(wt, wh, wt);
                CreateWallBoxWithMaterial(wallsRoot.transform, $"CornerPost_{vr}_{vc}", vPos + new Vector3(0f, wh * 0.5f, 0f), postScale, whiteWallMaterial);

                // Top horizontal cap lines ONLY on non-bridge vertices (at a bridge vertex, the pillar continues up to 2.25m!)
                if (!hasN)
                {
                    if (!isBridgeCorner) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, wh, halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                }
                if (!hasS)
                {
                    if (!isBridgeCorner) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, wh, -halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, -halfWT), new Vector3(wt + ew, ew, ew), blackEdgeMaterial);
                }
                if (!hasE)
                {
                    if (!isBridgeCorner) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, wh, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                }
                if (!hasW)
                {
                    if (!isBridgeCorner) CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, wh, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew), blackEdgeMaterial);
                }

                // Vertical corner lines on the 0..0.85m base post ONLY where a true 90-degree corner occurs!
                if (hasN == hasE)
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, wh * 0.5f, halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
                if (hasN == hasW)
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, wh * 0.5f, halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
                if (hasS == hasE)
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(halfWT, wh * 0.5f, -halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
                if (hasS == hasW)
                    CreateBlackEdgeBarWithMaterial(wallsRoot.transform, vPos + new Vector3(-halfWT, wh * 0.5f, -halfWT), new Vector3(ew, wh + ew, ew), blackEdgeMaterial);
            }
        }
    }

    void CreateWallBoxWithMaterial(Transform parent, string name, Vector3 worldCenter, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = worldCenter;
        go.transform.localScale = scale;
        NormalizeMeshUVsToWorldSpacePosition(go, PrimitiveType.Cube, worldCenter, scale);
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    void CreateBlackEdgeBarWithMaterial(Transform parent, Vector3 worldPos, Vector3 scale, Material mat)
    {
        GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "PhysicalCornerEdge";
        Destroy(bar.GetComponent<Collider>());
        bar.transform.SetParent(parent, false);
        bar.transform.position = worldPos;
        bar.transform.localScale = scale;
        Renderer r = bar.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void CreateWallColliderWorld(Transform parent, Vector3 worldPos, Vector3 size)
    {
        GameObject colObj = new GameObject("WallCollider");
        colObj.transform.SetParent(parent, false);
        colObj.transform.position = new Vector3(worldPos.x, 2.25f, worldPos.z);
        BoxCollider bc = colObj.AddComponent<BoxCollider>();
        bc.size = new Vector3(size.x, 4.5f, size.z);
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
        crumbs[AmazingMazeData.DIR_LEFT] = CreateCrumbPiece(cellObj.transform, "Crumb_W", center, new Vector3(-stubOffset, crumbY, 0f), new Vector3(stubLen, 0.015f, crumbWidth), crumbMat);
        crumbs[AmazingMazeData.DIR_DOWN] = CreateCrumbPiece(cellObj.transform, "Crumb_S", center, new Vector3(0f, crumbY, -stubOffset), new Vector3(crumbWidth, 0.015f, stubLen), crumbMat);
        crumbs[AmazingMazeData.DIR_RIGHT] = CreateCrumbPiece(cellObj.transform, "Crumb_E", center, new Vector3(stubOffset, crumbY, 0f), new Vector3(stubLen, 0.015f, crumbWidth), crumbMat);
        crumbs[AmazingMazeData.DIR_UP] = CreateCrumbPiece(cellObj.transform, "Crumb_N", center, new Vector3(0f, crumbY, stubOffset), new Vector3(crumbWidth, 0.015f, stubLen), crumbMat);

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
        col.center = new Vector3(0f, 2.25f, 0f);
        col.size = alongZ
            ? new Vector3(wallThickness, 4.5f, length + wallThickness)
            : new Vector3(length + wallThickness, 4.5f, wallThickness);
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

    void SpawnMarkerPrimitive(PrimitiveType type, int r, int c, float yHeight, string name)
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
        marker.GetComponent<Renderer>().sharedMaterial = blackBreadcrumbMaterial;
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

        RenderEntireMaze2D();
    }

    void SetupVRRigAndWristMap()
    {
        ForceEnableAllXRInputActions(xrRigRoot.gameObject);

        xrRigRoot.position = CellToWorldCenter(MazeData.StartPos.r, MazeData.StartPos.c, 0.02f);

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
                UpdateNonEuclideanTunnelState();
                UpdateBreadcrumbsAnd2DMap();
            }
            else if (architectureStyle != lastBuiltStyle && MazeData != null)
            {
                RebuildMazeGeometryOnly();
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

        byte wBase = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE);
        byte wBridge = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BRIDGE);
        byte nBase = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BASE);
        byte nBridge = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_NARROW_BRIDGE);

        int wBaseOpen = wBase & 0x0F;
        int wBridgeOpen = wBridge & 0x0F;
        int nBaseOpen = nBase & 0x0F;
        int nBridgeOpen = nBridge & 0x0F;
        bool isRamp = MazeData.IsRamp(r, c);

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

            if ((openMask & (1 << AmazingMazeData.DIR_LEFT)) == 0) FillRectFB(ox + 1, oy + 1, 1, 14, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_RIGHT)) == 0) FillRectFB(ox + 14, oy + 1, 1, 14, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_UP)) == 0) FillRectFB(ox + 1, oy + 1, 14, 1, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_DOWN)) == 0) FillRectFB(ox + 1, oy + 14, 14, 1, 1);

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

            if ((openMask & (1 << AmazingMazeData.DIR_LEFT)) == 0) FillRectFB(ox + 5, oy + 5, 1, 6, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_RIGHT)) == 0) FillRectFB(ox + 10, oy + 5, 1, 6, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_UP)) == 0) FillRectFB(ox + 5, oy + 5, 6, 1, 1);
            if ((openMask & (1 << AmazingMazeData.DIR_DOWN)) == 0) FillRectFB(ox + 5, oy + 10, 6, 1, 1);

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
                if ((nibs & (1 << AmazingMazeData.DIR_LEFT)) != 0) FillRectFB(ox, oy + 3, 3, 10, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_RIGHT)) != 0) FillRectFB(ox + 13, oy + 3, 3, 10, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_UP)) != 0) FillRectFB(ox + 3, oy, 10, 3, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_DOWN)) != 0) FillRectFB(ox + 3, oy + 13, 10, 3, 1);
            }
            else
            {
                FillRectFB(ox + 7, oy + 7, 2, 2, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_LEFT)) != 0) FillRectFB(ox, oy + 7, 7, 2, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_RIGHT)) != 0) FillRectFB(ox + 9, oy + 7, 7, 2, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_UP)) != 0) FillRectFB(ox + 7, oy, 2, 7, 1);
                if ((nibs & (1 << AmazingMazeData.DIR_DOWN)) != 0) FillRectFB(ox + 7, oy + 9, 2, 7, 1);
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

            Vector2 tip = center + fwd * 5.2f;
            Vector2 leftWing = center - fwd * 3.8f - right * 3.8f;
            Vector2 rightWing = center - fwd * 3.8f + right * 3.8f;
            Vector2 notch = center - fwd * 1.5f;

            int pr = MazeData.PlayerPos.r;
            int pc = MazeData.PlayerPos.c;
            bool cellOccupied = (MazeData.GetSquare(pr, pc, MazeData.PlayerPos.layer) & 0x30) != 0;
            Color32 fg = cellOccupied ? white : black;
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

        playerObject.transform.position = CellToWorldCenter(MazeData.StartPos.r, MazeData.StartPos.c, 0.05f);

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
        cc.SimpleMove(move);
#endif
    }
}