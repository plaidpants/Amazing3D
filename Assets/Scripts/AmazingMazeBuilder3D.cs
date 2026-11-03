using System;
using System.Collections.Generic;
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
    [Tooltip("If assigned, uses this XR Rig for VR and attaches the 2D Mac Map to the Left Controller! If left empty, spawns the Desktop WASD player.")]
    public Transform xrRigRoot;
    [Tooltip("Optional: Drag Left Controller Transform here. If blank, auto-searches inside xrRigRoot.")]
    public Transform leftWristTransform;
    public Vector3 wristMapLocalPosition = new Vector3(0.06f, 0.05f, -0.08f);
    public Vector3 wristMapLocalEuler = new Vector3(45f, 20f, 0f);
    public float wristMapWidthMeters = 0.18f;

    [Header("Architecture & Maze Settings (Press TAB in Play Mode to toggle style!)")]
    public WallArchitectureStyle architectureStyle = WallArchitectureStyle.FlatEdgedMac1Bit;
    [Range(0, 3)] public int skillLevel = 0;
    public float cellSize = 3.0f;
    public float wallHeight = 0.85f;
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

    public AmazingMazeData MazeData { get; private set; }
    private GameObject mazeRoot;
    private Transform activePlayerHeadOrBody;
    private GameObject wristMapObject;
    private Material wristMapMaterial;

    private Dictionary<(int r, int c), GameObject[]> breadcrumbMeshes = new Dictionary<(int r, int c), GameObject[]>();

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
    private Vector3 lastPlayerPos;
    private float lastPlayerYaw = -999f;
    private Type teleportAreaType;

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

    private object cachedTeleportProvider;

    void Start()
    {
        // Locate XR Interaction Toolkit's TeleportationArea type across XRI 2.x and 3.x
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            teleportAreaType = asm.GetType("UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea")
                            ?? asm.GetType("UnityEngine.XR.Interaction.Toolkit.TeleportationArea");
            if (teleportAreaType != null) break;
        }

        EnsureDither3DMaterials();
        MazeData = new AmazingMazeData();
        MazeData.MakeMaze(skillLevel);

        if (xrRigRoot != null)
            CacheTeleportProviderAndHardenRays();

        RebuildMazeGeometryOnly();

        if (xrRigRoot != null)
            SetupVRRigAndWristMap();
        else
            SpawnSimpleDesktopPlayer();

        ConfigureCameraAndGlobalDither();
        UpdateBreadcrumbsAnd2DMap();
    }

    // Finds the TeleportationProvider on the XR Rig and flattens the Teleport Ray so it cannot lob over walls!
    void CacheTeleportProviderAndHardenRays()
    {
        foreach (MonoBehaviour mb in xrRigRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            Type t = mb.GetType();

            if (t.Name == "TeleportationProvider")
            {
                cachedTeleportProvider = mb;
            }
            else if (t.Name == "XRRayInteractor")
            {
                // Make sure the Teleport Interactor ray uses a fast, flat trajectory (velocity = 22)
                // and cannot arc over the 4.5m tall invisible wall colliders!
                string goName = mb.gameObject.name.ToLowerInvariant();
                if (goName.Contains("teleport"))
                {
                    var velProp = t.GetProperty("velocity");
                    velProp?.SetValue(mb, 24f); // Flatter arc

                    // Enable all InteractionLayers (~0) on Teleport Interactor so it always recognizes floor TeleportationAreas
                    var maskProp = t.GetProperty("interactionLayers");
                    if (maskProp != null)
                    {
                        object maskStruct = maskProp.GetValue(mb);
                        var valField = maskStruct?.GetType().GetField("m_Bits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                    ?? maskStruct?.GetType().GetField("value", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (valField != null)
                        {
                            valField.SetValue(maskStruct, unchecked((uint)~0));
                            maskProp.SetValue(mb, maskStruct);
                        }
                    }
                }
            }
        }
    }

    // Properly wires up TeleportationArea at runtime (InteractionLayers = Everything, Colliders list, and TeleportationProvider)
    void ConfigureFloorTeleportationArea(GameObject floor)
    {
        if (teleportAreaType == null) return;

        // Deactivate floor briefly before adding TeleportationArea so OnEnable registers the collider cleanly!
        floor.SetActive(false);
        Component teleArea = floor.AddComponent(teleportAreaType);

        // 1. Assign the floor's BoxCollider to TeleportationArea.colliders
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

        // 2. Assign TeleportationProvider reference if found
        if (cachedTeleportProvider != null)
        {
            var providerProp = teleportAreaType.GetProperty("teleportationProvider");
            providerProp?.SetValue(teleArea, cachedTeleportProvider);
        }

        // 3. Set InteractionLayerMask to Everything (~0) so the Teleport Interactor (Bit 31) ALWAYS matches!
        var maskProp = teleportAreaType.GetProperty("interactionLayers");
        if (maskProp != null)
        {
            object maskStruct = maskProp.GetValue(teleArea);
            var valField = maskStruct?.GetType().GetField("m_Bits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?? maskStruct?.GetType().GetField("value", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valField != null)
            {
                valField.SetValue(maskStruct, unchecked((uint)~0));
                maskProp.SetValue(teleArea, maskStruct);
            }
        }

        floor.SetActive(true);
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
            UpdateBreadcrumbsAnd2DMap();
        }
#endif
        if (activePlayerHeadOrBody == null) return;

        // Track player movement across grid cells (Smooth Walk, Roomscale, and Teleport!)
        Vector3 curPos = activePlayerHeadOrBody.position;
        var (curR, curC) = WorldToCell(curPos);
        if (curR != MazeData.PlayerPos.r || curC != MazeData.PlayerPos.c)
        {
            StepPlayerTowardCell(curR, curC);
        }

        // Continuously update the player's exact position and smooth rotation wedge on the 2D map
        float curYaw = activePlayerHeadOrBody.eulerAngles.y;
        if ((curPos - lastPlayerPos).sqrMagnitude > 0.0001f || Mathf.Abs(Mathf.DeltaAngle(curYaw, lastPlayerYaw)) > 0.25f)
        {
            lastPlayerPos = curPos;
            lastPlayerYaw = curYaw;
            FlushFramebufferWithPlayerWedge();
        }
    }

    // Validates every single step (walking or multi-cell teleport) against the maze walls!
    // If a teleport ever tries to jump across a wall or diagonally through a corner, it clamps
    // the player to the furthest legal cell before the wall so cheating is impossible.
    void StepPlayerTowardCell(int targetR, int targetC)
    {
        bool anyMoved = false;
        int safety = 0;

        while ((MazeData.PlayerPos.r != targetR || MazeData.PlayerPos.c != targetC) && safety++ < 32)
        {
            int dr = Mathf.Clamp(targetR - MazeData.PlayerPos.r, -1, 1);
            int dc = Mathf.Clamp(targetC - MazeData.PlayerPos.c, -1, 1);

            bool stepped = false;
            // Try cardinal directions toward target
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
                break; // Hit a wall! Stop advancing.
        }

        // ANTI-CHEAT SNAPBACK: If the player's rig teleported into a cell (targetR, targetC)
        // that could NOT be reached legally through open corridors, snap the XR Rig back to PlayerPos!
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

    void SetupVRRigAndWristMap()
    {
        // 1. Force-enable all InputActions & InputActionAssets on the XR Rig so head & hand tracking ALWAYS work!
        ForceEnableAllXRInputActions(xrRigRoot.gameObject);

        // 2. Place XR Origin at StartPos and face down the open corridor
        xrRigRoot.position = CellToWorldCenter(MazeData.StartPos.r, MazeData.StartPos.c, 0.02f);

        byte startMask = MazeData.GetSquare(MazeData.StartPos.r, MazeData.StartPos.c, MazeData.StartPos.layer);
        float startYaw = 0f;
        if ((startMask & (1 << AmazingMazeData.DIR_RIGHT)) != 0) startYaw = 90f;
        else if ((startMask & (1 << AmazingMazeData.DIR_DOWN)) != 0) startYaw = 180f;
        else if ((startMask & (1 << AmazingMazeData.DIR_LEFT)) != 0) startYaw = -90f;
        xrRigRoot.eulerAngles = new Vector3(0f, startYaw, 0f);

        // 3. Ensure Camera Offset is at standing height if Floor/Device tracking mode hasn't offset it yet
        Transform camOffset = xrRigRoot.Find("Camera Offset");
        if (camOffset != null && camOffset.localPosition.y < 0.1f)
        {
            camOffset.localPosition = new Vector3(0f, 1.36f, 0f);
        }

        // Track the VR Main Camera (HMD) for accurate head position & gaze direction on the 2D map
        Camera vrCam = xrRigRoot.GetComponentInChildren<Camera>();
        activePlayerHeadOrBody = (vrCam != null) ? vrCam.transform : xrRigRoot;

        // 4. Find Left Controller if not manually assigned
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

        // 5. Clean up thick white LineRenderers on controllers (which caused the white triangles!)
        foreach (LineRenderer lr in xrRigRoot.GetComponentsInChildren<LineRenderer>(true))
        {
            lr.widthMultiplier = 0.004f; // Thin, subtle ray instead of a giant white wedge
        }

        // 6. Build the 3D Macintosh Wrist Map Tablet on the Left Controller
        if (leftWristTransform != null)
        {
            Build3DWristMapTablet(leftWristTransform);
        }
        else
        {
            Debug.LogWarning("Could not auto-find Left Controller inside xrRigRoot. Assign 'Left Wrist Transform' in the Inspector.");
        }
    }

    // Automatically enables XRI InputActionAssets and TrackedPoseDrivers even if InputActionManager is missing from the scene!
    void ForceEnableAllXRInputActions(GameObject rigRoot)
    {
        // Enable any InputActionManager in the scene first
        foreach (MonoBehaviour mb in FindObjectsOfType<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            Type t = mb.GetType();
            if (t.Name == "InputActionManager")
            {
                var enableMethod = t.GetMethod("EnableInput", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                enableMethod?.Invoke(mb, null);
            }

            // Also walk every component on the XR Rig (TrackedPoseDriver, ActionBasedController, etc.)
            // and call .Enable() on any UnityEngine.InputSystem.InputActionProperty or InputActionAsset
            foreach (var field in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
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

        // Black Macintosh Window Border Frame
        GameObject bezel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bezel.name = "MacWindowBezel";
        Destroy(bezel.GetComponent<Collider>());
        bezel.transform.SetParent(wristMapObject.transform, false);
        bezel.transform.localPosition = new Vector3(0f, 0f, 0.003f);
        bezel.transform.localScale = new Vector3(w + 0.01f, h + 0.01f, 0.005f);
        bezel.GetComponent<Renderer>().sharedMaterial = blackEdgeMaterial;

        // Crisp Unlit 2D Map Screen Quad
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

            ApplyDitherPropertiesToMaterial(whiteWallMaterial, Color.white, lightExposure, lightOffset);
            ApplyDitherPropertiesToMaterial(whiteFloorMaterial, new Color(0.95f, 0.95f, 0.95f), lightExposure, lightOffset);
            ApplyDitherPropertiesToMaterial(blackBreadcrumbMaterial, new Color(0.08f, 0.08f, 0.08f), 0.5f, -0.2f);
            ConfigureCameraAndGlobalDither();

            if (wristMapObject != null)
            {
                wristMapObject.transform.localPosition = wristMapLocalPosition;
                wristMapObject.transform.localEulerAngles = wristMapLocalEuler;
            }

            if (architectureStyle != lastBuiltStyle && MazeData != null)
            {
                RebuildMazeGeometryOnly();
                UpdateBreadcrumbsAnd2DMap();
            }
        }
    }

    /* ============================================================================
     * PROCEDURAL TEXTURES: SQUARE BAYER 3D DITHER & 1-BIT 50% MAC CHECKERBOARD
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
        {
            for (int bx = 0; bx < dotsPerSide; bx++)
            {
                int rank = bayer4x4[by, bx];
                dotCenters[rank] = new Vector2((bx + 0.5f) / dotsPerSide, (by + 0.5f) / dotsPerSide);
            }
        }

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
        {
            Shader unlitCol = Shader.Find("Unlit/Color");
            if (unlitCol == null) unlitCol = Shader.Find("Standard");
            blackEdgeMaterial = new Material(unlitCol) { name = "BlackEdgeOutline_Mat", color = Color.black };
        }

        ApplyDitherPropertiesToMaterial(whiteWallMaterial, Color.white, lightExposure, lightOffset);
        ApplyDitherPropertiesToMaterial(whiteFloorMaterial, new Color(0.95f, 0.95f, 0.95f), lightExposure, lightOffset);
        ApplyDitherPropertiesToMaterial(blackBreadcrumbMaterial, new Color(0.08f, 0.08f, 0.08f), 0.5f, -0.2f);
    }

    void ApplyDitherPropertiesToMaterial(Material mat, Color baseColor, float exposure, float offset)
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

        if (type == PrimitiveType.Cube)
        {
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 wp = worldPos + new Vector3(verts[i].x * localScale.x, verts[i].y * localScale.y, verts[i].z * localScale.z);
                Vector3 n = normals[i];
                if (Mathf.Abs(n.x) > 0.5f)
                    uvs[i] = new Vector2(wp.z, wp.y) * uvPerMeter;
                else if (Mathf.Abs(n.y) > 0.5f)
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

    public void RebuildMazeGeometryOnly()
    {
        lastBuiltStyle = architectureStyle;
        if (mazeRoot != null) Destroy(mazeRoot);
        mazeRoot = new GameObject("MazeGeometryRoot");
        breadcrumbMeshes.Clear();

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
                int openMask = insideGrid ? (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) & 0x0F) : 0;

                if (openMask == 0)
                {
                    if (fillUnoccupiedWithCheckerboard)
                        BuildUnoccupiedCheckerboardCell(r, c);
                }
                else
                {
                    BuildWideCellFloorAndBreadcrumbs(r, c);
                    if (architectureStyle == WallArchitectureStyle.CycladicRounded)
                        BuildCycladicCellWalls(r, c, openMask);
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

    void BuildSeamlessFlatWallsWithPhysicalOutlines()
    {
        int R = MazeData.MaxRow;
        int C = MazeData.MaxCol;

        bool[,] hWall = new bool[R + 2, C + 2];
        bool[,] vWall = new bool[R + 2, C + 2];

        bool IsCarved(int r, int c)
        {
            if (r < 1 || r > R || c < 1 || c > C) return false;
            return (MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) & 0x0F) != 0;
        }

        for (int r = 1; r <= R; r++)
        {
            for (int c = 1; c <= C; c++)
            {
                if (!IsCarved(r, c)) continue;
                int mask = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE) & 0x0F;
                if ((mask & (1 << AmazingMazeData.DIR_UP)) == 0)    hWall[r - 1, c] = true;
                if ((mask & (1 << AmazingMazeData.DIR_DOWN)) == 0)  hWall[r, c] = true;
                if ((mask & (1 << AmazingMazeData.DIR_LEFT)) == 0)  vWall[r, c - 1] = true;
                if ((mask & (1 << AmazingMazeData.DIR_RIGHT)) == 0) vWall[r, c] = true;
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
                CreateWallBoxWithWorldUVs(wallsRoot.transform, "HWallSpan", bodyCenter, scale);

                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3(0f, wh,  halfWT), new Vector3(spanLen + ew, ew, ew));
                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3(0f, wh, -halfWT), new Vector3(spanLen + ew, ew, ew));
                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3(0f, ew * 0.5f,  halfWT), new Vector3(spanLen + ew, ew, ew));
                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3(0f, ew * 0.5f, -halfWT), new Vector3(spanLen + ew, ew, ew));

                CreateWallColliderWorld(wallsRoot.transform, mid + new Vector3(0f, 0.9f, 0f), new Vector3(cellSize, 1.8f, wt));
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
                CreateWallBoxWithWorldUVs(wallsRoot.transform, "VWallSpan", bodyCenter, scale);

                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3( halfWT, wh, 0f), new Vector3(ew, ew, spanLen + ew));
                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3(-halfWT, wh, 0f), new Vector3(ew, ew, spanLen + ew));
                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3( halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, spanLen + ew));
                CreateBlackEdgeBarWorld(wallsRoot.transform, mid + new Vector3(-halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, spanLen + ew));

                CreateWallColliderWorld(wallsRoot.transform, mid + new Vector3(0f, 0.9f, 0f), new Vector3(wt, 1.8f, cellSize));
            }
        }

        for (int vr = 0; vr <= R; vr++)
        {
            for (int vc = 0; vc <= C; vc++)
            {
                bool hasN = (vr >= 1) && vWall[vr, vc];
                bool hasS = (vr < R)  && vWall[vr + 1, vc];
                bool hasW = (vc >= 1) && hWall[vr, vc];
                bool hasE = (vc < C)  && hWall[vr, vc + 1];

                if (!hasN && !hasS && !hasW && !hasE) continue;

                Vector3 vPos = VertexWorldPos(vr, vc);
                Vector3 postScale = new Vector3(wt, wh, wt);
                CreateWallBoxWithWorldUVs(wallsRoot.transform, $"CornerPost_{vr}_{vc}", vPos + new Vector3(0f, wh * 0.5f, 0f), postScale);

                if (!hasN)
                {
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(0f, wh, halfWT), new Vector3(wt + ew, ew, ew));
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, halfWT), new Vector3(wt + ew, ew, ew));
                }
                if (!hasS)
                {
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(0f, wh, -halfWT), new Vector3(wt + ew, ew, ew));
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(0f, ew * 0.5f, -halfWT), new Vector3(wt + ew, ew, ew));
                }
                if (!hasE)
                {
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(halfWT, wh, 0f), new Vector3(ew, ew, wt + ew));
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew));
                }
                if (!hasW)
                {
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(-halfWT, wh, 0f), new Vector3(ew, ew, wt + ew));
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(-halfWT, ew * 0.5f, 0f), new Vector3(ew, ew, wt + ew));
                }

                if (hasN == hasE)
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3( halfWT, wh * 0.5f,  halfWT), new Vector3(ew, wh + ew, ew));
                if (hasN == hasW)
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(-halfWT, wh * 0.5f,  halfWT), new Vector3(ew, wh + ew, ew));
                if (hasS == hasE)
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3( halfWT, wh * 0.5f, -halfWT), new Vector3(ew, wh + ew, ew));
                if (hasS == hasW)
                    CreateBlackEdgeBarWorld(wallsRoot.transform, vPos + new Vector3(-halfWT, wh * 0.5f, -halfWT), new Vector3(ew, wh + ew, ew));
            }
        }
    }

    void CreateWallBoxWithWorldUVs(Transform parent, string name, Vector3 worldCenter, Vector3 scale)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = worldCenter;
        go.transform.localScale = scale;
        NormalizeMeshUVsToWorldSpacePosition(go, PrimitiveType.Cube, worldCenter, scale);
        go.GetComponent<Renderer>().sharedMaterial = whiteWallMaterial;
    }

    void CreateBlackEdgeBarWorld(Transform parent, Vector3 worldPos, Vector3 scale)
    {
        GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "PhysicalCornerEdge";
        Destroy(bar.GetComponent<Collider>());
        bar.transform.SetParent(parent, false);
        bar.transform.position = worldPos;
        bar.transform.localScale = scale;
        Renderer r = bar.GetComponent<Renderer>();
        r.sharedMaterial = blackEdgeMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void CreateWallColliderWorld(Transform parent, Vector3 worldPos, Vector3 size)
    {
        GameObject colObj = new GameObject("WallCollider");
        colObj.transform.SetParent(parent, false);
        // Raise invisible wall collider height to 4.5m so teleport arcs can NEVER lob over low 0.85m walls!
        colObj.transform.position = new Vector3(worldPos.x, 2.25f, worldPos.z);
        BoxCollider bc = colObj.AddComponent<BoxCollider>();
        bc.size = new Vector3(size.x, 4.5f, size.z);
    }

    void BuildUnoccupiedCheckerboardCell(int r, int c)
    {
        Vector3 center = CellToWorldCenter(r, c, 0f);
        GameObject chkObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chkObj.name = $"CheckerBlock_{r}_{c}";
        // No TeleportationArea on Checkerboard blocks so player cannot teleport out of bounds!
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

    void BuildWideCellFloorAndBreadcrumbs(int r, int c)
    {
        Vector3 center = CellToWorldCenter(r, c, 0f);
        GameObject cellObj = new GameObject($"Cell_{r}_{c}");
        cellObj.transform.SetParent(mazeRoot.transform, false);
        cellObj.transform.position = center;

        // 1. White Base Floor (with properly configured XR TeleportationArea!)
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "WhiteFloor";
        floor.transform.SetParent(cellObj.transform, false);
        floor.transform.localPosition = new Vector3(0f, -0.05f, 0f);
        Vector3 floorScale = new Vector3(cellSize, 0.1f, cellSize);
        floor.transform.localScale = floorScale;
        NormalizeMeshUVsToWorldSpacePosition(floor, PrimitiveType.Cube, center + new Vector3(0f, -0.05f, 0f), floorScale);
        floor.GetComponent<Renderer>().sharedMaterial = whiteFloorMaterial;

        ConfigureFloorTeleportationArea(floor);

        // 2. Inset 3D Black Breadcrumb Overlay
        float crumbWidth = cellSize * 0.625f;
        float stubLen = (cellSize - crumbWidth) * 0.5f;
        float stubOffset = (crumbWidth + stubLen) * 0.5f;
        float crumbY = 0.008f;

        GameObject[] crumbs = new GameObject[5];
        crumbs[4] = CreateCrumbPiece(cellObj.transform, "Crumb_Center", center, Vector3.up * crumbY, new Vector3(crumbWidth, 0.015f, crumbWidth));
        crumbs[AmazingMazeData.DIR_LEFT] = CreateCrumbPiece(cellObj.transform, "Crumb_W", center, new Vector3(-stubOffset, crumbY, 0f), new Vector3(stubLen, 0.015f, crumbWidth));
        crumbs[AmazingMazeData.DIR_DOWN] = CreateCrumbPiece(cellObj.transform, "Crumb_S", center, new Vector3(0f, crumbY, -stubOffset), new Vector3(crumbWidth, 0.015f, stubLen));
        crumbs[AmazingMazeData.DIR_RIGHT] = CreateCrumbPiece(cellObj.transform, "Crumb_E", center, new Vector3(stubOffset, crumbY, 0f), new Vector3(stubLen, 0.015f, crumbWidth));
        crumbs[AmazingMazeData.DIR_UP] = CreateCrumbPiece(cellObj.transform, "Crumb_N", center, new Vector3(0f, crumbY, stubOffset), new Vector3(crumbWidth, 0.015f, stubLen));

        breadcrumbMeshes[(r, c)] = crumbs;
    }

    void BuildCycladicCellWalls(int r, int c, int openMask)
    {
        Vector3 center = CellToWorldCenter(r, c, 0f);
        Transform cellTransform = mazeRoot.transform.Find($"Cell_{r}_{c}");
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

    GameObject CreateCrumbPiece(Transform parent, string name, Vector3 cellWorldCenter, Vector3 localPos, Vector3 scale)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        NormalizeMeshUVsToWorldSpacePosition(go, PrimitiveType.Cube, cellWorldCenter + localPos, scale);
        go.GetComponent<Renderer>().sharedMaterial = blackBreadcrumbMaterial;
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
            var (r, c) = kvp.Key;
            GameObject[] pieces = kvp.Value;
            byte sq = MazeData.GetSquare(r, c, AmazingMazeData.LAYER_WIDE_BASE);
            bool occupied = (sq & 0x30) != 0;
            byte nibs = MazeData.NibMask[r, c, AmazingMazeData.LAYER_WIDE_BASE];

            pieces[4].SetActive(occupied);
            for (int d = 0; d < 4; d++)
                pieces[d].SetActive(occupied && (nibs & (1 << d)) != 0);
        }

        RenderEntireMaze2D();
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

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        Vector3 move = (transform.right * h + transform.forward * v).normalized * 4.5f;
        cc.SimpleMove(move);
#endif
    }
}