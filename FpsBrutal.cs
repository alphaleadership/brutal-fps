/*
 * FpsBrutal — un FPS (first-person shooter) jouable construit sur le framework BRUTAL
 * (https://github.com/ahwootech/BRUTAL, Ahwoo / RocketWerkz).
 *
 *   - Fenêtre + entrée souris/clavier : Ahwoo.Brutal.Glfw
 *   - Rendu 3D (OpenGL 4.1 core)      : Ahwoo.Brutal.OpenGl
 *   - Physique (collisions, raycasts) : Ahwoo.Brutal.Jolt
 *   - Maths vectorielles/matricielles  : Ahwoo.Brutal.Numerics
 *
 * Contrôles : WASD = bouger, souris = viser, clic gauche = tirer, espace = sauter,
 *              shift = sprint, Echap = quitter.
 * Lancez avec `dotnet run FpsBrutal.cs` (ou `dotnet run FpsBrutal.cs -- --selftest`
 * pour un test automatique headless-ish qui joue tout seul 15 s puis quitte).
 *
 * Licence BRUTAL : NOTICE.txt doit accompagner le produit (voir NOTICE.txt à côté).
 */

#:property AllowUnsafeBlocks=true
#:package Ahwoo.Brutal.Core@2026.10.4
#:package Ahwoo.Brutal.Glfw@2026.10.5
#:package Ahwoo.Brutal.OpenGl@2026.10.2
#:package Ahwoo.Brutal.Jolt@2026.10.4
#:package Ahwoo.Brutal.Numerics@2026.10.2
#:package Ahwoo.Brutal.Stb@2026.10.3

using System.Text;
using Brutal;
using Brutal.GlfwApi;
using Brutal.JoltApi;
using Brutal.Numerics;
using Brutal.OpenGlApi;
using Brutal.StbApi;

// Couches de collision Jolt : le joueur ne doit pas se raycaster lui-même.
const ushort LAYER_STATIC = 0;
const ushort LAYER_MOVING = 1;
const ushort LAYER_PLAYER = 2;

var selftest = args.Contains("--selftest");

const string LitVert = """
    #version 410 core
    layout (location = 0) in vec3 aPos;
    layout (location = 1) in vec3 aNormal;
    uniform mat4 model;
    uniform mat4 viewProjection;
    out vec3 normal;
    out vec3 worldPos;
    void main()
    {
        vec4 wp = model * vec4(aPos, 1.0);
        worldPos = wp.xyz;
        normal = mat3(model) * aNormal;
        gl_Position = viewProjection * wp;
    }
    """;

const string LitFrag = """
    #version 410 core
    in vec3 normal;
    in vec3 worldPos;
    uniform vec3 color;
    uniform vec3 eyePos;
    out vec4 FragColor;
    void main()
    {
        vec3 n = normalize(normal);
        vec3 lightDir = normalize(vec3(0.45, 1.0, 0.35));
        float diff = max(dot(n, lightDir), 0.0);
        vec3 viewDir = normalize(eyePos - worldPos);
        vec3 h = normalize(lightDir + viewDir);
        float spec = pow(max(dot(n, h), 0.0), 48.0);
        vec3 col = color * (0.25 + 0.75 * diff) + vec3(0.35) * spec;
        FragColor = vec4(col, 1.0);
    }
    """;

const string FlatVert = """
    #version 410 core
    layout (location = 0) in vec3 aPos;
    layout (location = 1) in vec3 aNormal;
    uniform mat4 model;
    uniform mat4 viewProjection;
    void main()
    {
        gl_Position = viewProjection * model * vec4(aPos, 1.0);
    }
    """;

const string FlatFrag = """
    #version 410 core
    uniform vec3 color;
    out vec4 FragColor;
    void main()
    {
        FragColor = vec4(color, 1.0);
    }
    """;

const string OverlayVert = """
    #version 410 core
    layout (location = 0) in vec2 aPos;
    void main()
    {
        gl_Position = vec4(aPos, 0.0, 1.0);
    }
    """;

const string OverlayFrag = """
    #version 410 core
    uniform vec4 color;
    out vec4 FragColor;
    void main()
    {
        FragColor = color;
    }
    """;

// Jolt a besoin d'un allocator, d'une factory et de ses types enregistrés avant toute simulation.
Jolt.RegisterDefaultAllocator();
Factory.Instance = Factory.Create();
Jolt.RegisterTypes();
try
{
    if (!Glfw.Init())
    {
        var errorCode = Glfw.GetError(out var errorMessage);
        throw new InvalidOperationException($"Failed to initialize GLFW: {errorCode}, {errorMessage}");
    }

    try
    {
        Run(selftest);
    }
    finally
    {
        Glfw.Terminate();
    }
}
finally
{
    Jolt.UnregisterTypes();
    Factory.DestroyInstance();
}

void Run(bool selftest)
{
    // ============================== Physique (Jolt) ==============================
    using var tempAllocator = TempAllocatorImpl.Create(10 * 1024 * 1024);
    using var jobSystem = JobSystemThreadPool.Create(2048, 8, -1);

    // 2 broad-phase layers (statique / dynamique), 3 object layers.
    using var broadPhaseLayers = BroadPhaseLayerInterfaceTable.Create(2, 3);
    broadPhaseLayers.MapObjectToBroadPhaseLayer(LAYER_STATIC, 0);
    broadPhaseLayers.MapObjectToBroadPhaseLayer(LAYER_MOVING, 1);
    broadPhaseLayers.MapObjectToBroadPhaseLayer(LAYER_PLAYER, 1);

    using var pairFilter = ObjectLayerPairFilterTable.Create(3);
    pairFilter.EnableCollision(LAYER_STATIC, LAYER_MOVING);
    pairFilter.EnableCollision(LAYER_STATIC, LAYER_PLAYER);
    pairFilter.EnableCollision(LAYER_MOVING, LAYER_MOVING);
    pairFilter.EnableCollision(LAYER_MOVING, LAYER_PLAYER);

    using var broadPhaseFilter = ObjectVsBroadPhaseLayerFilterTable.Create(broadPhaseLayers, 2, pairFilter, 3);

    using var physicsSystem = PhysicsSystem.Create();
    physicsSystem.Init(1024, 0, 1024, 1024, broadPhaseLayers, broadPhaseFilter, pairFilter);
    physicsSystem.SetGravity(new float3(0, -22f, 0)); // gravité un peu plus "jeu d'arcade"
    var bodyInterface = physicsSystem.GetBodyInterface();
    var narrowPhase = physicsSystem.GetNarrowPhaseQuery();
    using var rayCastSettings = RayCastSettings.Create(); // réutilisé pour tous les raycasts

    // ============================== Niveau ==============================
    // Sol, murs, piliers, plateformes : des boxes statiques.
    var statics = new List<(float3 Pos, float3 Half, float3 Color, uint Body)>();

    void AddStatic(float3 pos, float3 half, float3 color)
    {
        using var shape = BoxShapeSettings.Create(half, 0.05f, default);
        using var settings = BodyCreationSettings.Create(shape, pos, floatQuat.Identity, MotionType.Static, LAYER_STATIC);
        var body = bodyInterface.CreateAndAddBody(settings, Activation.DontActivate);
        statics.Add((pos, half, color, body));
    }

    AddStatic(new float3(0, -0.5f, 0), new float3(20, 0.5f, 20), new float3(0.35f, 0.42f, 0.35f)); // sol
    AddStatic(new float3(0, 2, -20), new float3(20.5f, 2, 0.5f), new float3(0.55f, 0.5f, 0.45f));   // mur nord
    AddStatic(new float3(0, 2, 20), new float3(20.5f, 2, 0.5f), new float3(0.55f, 0.5f, 0.45f));    // mur sud
    AddStatic(new float3(-20, 2, 0), new float3(0.5f, 2, 20.5f), new float3(0.5f, 0.45f, 0.55f));  // mur ouest
    AddStatic(new float3(20, 2, 0), new float3(0.5f, 2, 20.5f), new float3(0.5f, 0.45f, 0.55f));   // mur est
    foreach (var (x, z) in new[] { (-8f, -8f), (8f, -8f), (-8f, 8f), (8f, 8f) })
        AddStatic(new float3(x, 1.5f, z), new float3(0.6f, 1.5f, 0.6f), new float3(0.6f, 0.45f, 0.3f)); // piliers
    AddStatic(new float3(-6, 1.0f, -6), new float3(2.5f, 0.25f, 2.5f), new float3(0.3f, 0.5f, 0.7f));  // plateformes
    AddStatic(new float3(6, 2.0f, 6), new float3(2.5f, 0.25f, 2.5f), new float3(0.3f, 0.5f, 0.7f));
    AddStatic(new float3(0, 3.0f, -10), new float3(2f, 0.25f, 2f), new float3(0.3f, 0.5f, 0.7f));

    // Cibles : 8 boxes dynamiques qu'on peut toucher / repousser, qui respawnent en l'air.
    var rng = new Random(1234);
    var targets = new List<Target>();
    for (var i = 0; i < 8; i++)
    {
        using var shape = BoxShapeSettings.Create(new float3(0.4f), 0.05f, default);
        using var settings = BodyCreationSettings.Create(shape, RandomSpot(rng, 2f), floatQuat.Identity, MotionType.Dynamic, LAYER_MOVING);
        var body = bodyInterface.CreateAndAddBody(settings, Activation.Activate);
        var color = new float3(1.0f, 0.3f + 0.55f * i / 8f, 0.2f);
        targets.Add(new Target(body, new float3(0.4f), color));
    }

    // Joueur : capsule dynamique (hauteur totale 1.7, rayon 0.35).
    using var playerShape = CapsuleShapeSettings.Create(0.5f, 0.35f);
    using var playerSettings = BodyCreationSettings.Create(
        playerShape, new float3(0, 2f, 10f), floatQuat.Identity, MotionType.Dynamic, LAYER_PLAYER);
    var playerBody = bodyInterface.CreateAndAddBody(playerSettings, Activation.Activate);

    static float3 RandomSpot(Random rng, float yBase) =>
        new((float)(rng.NextDouble() * 28 - 14), yBase + (float)rng.NextDouble() * 2, (float)(rng.NextDouble() * 28 - 14));

    // ============================== Fenêtre + OpenGL ==============================
    Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, 4);
    Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, 1);
    Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_CORE_PROFILE);
    if (Platform.IsMacOS)
        Glfw.WindowHint(GlfwWindowHint.OpenGlForwardCompat, 1);
    Glfw.WindowHint(GlfwWindowHint.Resizable, Glfw.Constants.GLFW_FALSE);

    const int WIDTH = 1280, HEIGHT = 720;
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "BRUTAL FPS", Size = new int2(WIDTH, HEIGHT) });
    window.MakeContextCurrent();
    Glfw.SwapInterval(1);

    unsafe
    {
        if (!Glad.Initialize(Glfw.PInvoke.GetProcAddress))
            throw new InvalidOperationException("Failed to initialize OpenGL (GLAD)");
    }

    // ============================== Shaders ==============================
    using var litProgram = LinkProgram(LitVert, LitFrag);       // scène + arme, éclairage Phong
    using var flatProgram = LinkProgram(FlatVert, FlatFrag);    // tracer / flash, couleur unie
    using var overlayProgram = LinkProgram(OverlayVert, OverlayFrag); // viseur 2D en NDC

    // ============================== Armes & gadgets ==============================
    // 4 armes hitscan différentes (roue au clic droit, ou touches 1-4).
    var weapons = new[]
    {
        new Weapon("Pistolet", 0.11, 14f, 1, 0.0f, 1, new float3(0.75f, 0.75f, 0.8f)),
        new Weapon("SMG", 0.065, 6f, 1, 0.035f, 1, new float3(0.3f, 0.6f, 1.0f)),
        new Weapon("Pompe", 0.75, 9f, 6, 0.09f, 1, new float3(1.0f, 0.55f, 0.2f)),
        new Weapon("Sniper", 1.3, 45f, 1, 0.0f, 3, new float3(0.8f, 0.3f, 1.0f)),
    };
    var currentWeapon = 0;
    var selectedSlot = 0;
    var wheelOpen = false;

    // Gadgets : E = utiliser, molette souris = changer. Grenade / Dash / Jetpack.
    var gadgets = new[] { "Grenade", "Dash", "Jetpack" };
    var currentGadget = 0;
    var nextGadgetAt = 0.0;
    var jetpackUntil = 0.0;
    var explosionFlashUntil = 0.0;
    var grenades = new List<(uint Body, double ExplodeAt)>();
    var debris = new List<(uint Body, double RemoveAt, float3 Half, float3 Color)>(); // éclats des cibles détruites
    const int TARGET_HP = 5;

    // ============================== Géométrie ==============================
    var cube = BuildCube();
    var cubeVao = OpenGl.GenVertexArray().Bind();
    var cubeVbo = OpenGl.GenBuffer();
    cubeVbo.Bind(GlBindTarget.ArrayBuffer);
    OpenGl.BufferData(GlBindTarget.ArrayBuffer, cube, GlBufferUsage.StaticDraw);
    OpenGl.VertexAttribute(new GlAttribute(0, 3, OpenGl.GL_FLOAT, GlScaling.Float, 6 * sizeof(float), 0));
    OpenGl.VertexAttribute(new GlAttribute(1, 3, OpenGl.GL_FLOAT, GlScaling.Float, 6 * sizeof(float), 3 * sizeof(float)));
    OpenGl.EnableVertexAttribArray(0);
    OpenGl.EnableVertexAttribArray(1);

    // Viseur : une croix en coordonnées NDC (dessinée en dernier, depth test off).
    float2[] crosshair =
    [
        new(-0.09f, 0), new(-0.02f, 0), new(0.02f, 0), new(0.09f, 0),
        new(0, -0.09f), new(0, -0.02f), new(0, 0.02f), new(0, 0.09f),
    ];
    var crossVao = OpenGl.GenVertexArray().Bind();
    var crossVbo = OpenGl.GenBuffer();
    crossVbo.Bind(GlBindTarget.ArrayBuffer);
    OpenGl.BufferData(GlBindTarget.ArrayBuffer, crosshair, GlBufferUsage.StaticDraw);
    OpenGl.VertexAttribute(new GlAttribute(0, 2, OpenGl.GL_FLOAT, GlScaling.Float, 2 * sizeof(float), 0));
    OpenGl.EnableVertexAttribArray(0);

    // Roue des armes : un anneau de 4 secteurs (haut/gauche/bas/droite), SEG triangles chacun.
    const int SEG = 20;
    const float R0 = 0.14f, R1 = 0.46f;
    var ringVerts = new float2[4 * SEG * 6];
    for (var s = 0; s < 4; s++)
    {
        var a0 = MathF.PI / 4 + s * MathF.PI / 2; // le secteur 0 est en haut
        for (var i = 0; i < SEG; i++)
        {
            var a1 = a0 + (MathF.PI / 2) * i / SEG;
            var a2 = a0 + (MathF.PI / 2) * (i + 1) / SEG;
            var inner1 = new float2(MathF.Cos(a1) * R0, MathF.Sin(a1) * R0);
            var outer1 = new float2(MathF.Cos(a1) * R1, MathF.Sin(a1) * R1);
            var inner2 = new float2(MathF.Cos(a2) * R0, MathF.Sin(a2) * R0);
            var outer2 = new float2(MathF.Cos(a2) * R1, MathF.Sin(a2) * R1);
            var b = (s * SEG + i) * 6;
            ringVerts[b + 0] = inner1;
            ringVerts[b + 1] = outer1;
            ringVerts[b + 2] = outer2;
            ringVerts[b + 3] = inner1;
            ringVerts[b + 4] = outer2;
            ringVerts[b + 5] = inner2;
        }
    }
    var ringVao = OpenGl.GenVertexArray().Bind();
    var ringVbo = OpenGl.GenBuffer();
    ringVbo.Bind(GlBindTarget.ArrayBuffer);
    OpenGl.BufferData(GlBindTarget.ArrayBuffer, ringVerts, GlBufferUsage.StaticDraw);
    OpenGl.VertexAttribute(new GlAttribute(0, 2, OpenGl.GL_FLOAT, GlScaling.Float, 2 * sizeof(float), 0));
    OpenGl.EnableVertexAttribArray(0);

    // Disque central de la roue + quad plein écran (flash d'explosion).
    float2[] centerQuad = [new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)];
    var centerVao = OpenGl.GenVertexArray().Bind();
    var centerVbo = OpenGl.GenBuffer();
    centerVbo.Bind(GlBindTarget.ArrayBuffer);
    OpenGl.BufferData(GlBindTarget.ArrayBuffer, centerQuad, GlBufferUsage.StaticDraw);
    OpenGl.VertexAttribute(new GlAttribute(0, 2, OpenGl.GL_FLOAT, GlScaling.Float, 2 * sizeof(float), 0));
    OpenGl.EnableVertexAttribArray(0);

    float2[] fullQuad = [new(-1, -1), new(1, -1), new(1, 1), new(-1, -1), new(1, 1), new(-1, 1)];
    var fullVao = OpenGl.GenVertexArray().Bind();
    var fullVbo = OpenGl.GenBuffer();
    fullVbo.Bind(GlBindTarget.ArrayBuffer);
    OpenGl.BufferData(GlBindTarget.ArrayBuffer, fullQuad, GlBufferUsage.StaticDraw);
    OpenGl.VertexAttribute(new GlAttribute(0, 2, OpenGl.GL_FLOAT, GlScaling.Float, 2 * sizeof(float), 0));
    OpenGl.EnableVertexAttribArray(0);

    // Pointeur de la roue (mis à jour à chaque frame, 2 sommets).
    var pointerVao = OpenGl.GenVertexArray().Bind();
    var pointerVbo = OpenGl.GenBuffer();
    pointerVbo.Bind(GlBindTarget.ArrayBuffer);
    OpenGl.BufferData(GlBindTarget.ArrayBuffer, new float2[2], GlBufferUsage.DynamicDraw);
    OpenGl.VertexAttribute(new GlAttribute(0, 2, OpenGl.GL_FLOAT, GlScaling.Float, 2 * sizeof(float), 0));
    OpenGl.EnableVertexAttribArray(0);

    // Labels des armes sur la roue (mini-font 5x7, VBO dynamique rempli quand la roue est ouverte).
    var textVao = OpenGl.GenVertexArray().Bind();
    var textVbo = OpenGl.GenBuffer();
    textVbo.Bind(GlBindTarget.ArrayBuffer);
    OpenGl.BufferData(GlBindTarget.ArrayBuffer, new float2[8192], GlBufferUsage.DynamicDraw);
    OpenGl.VertexAttribute(new GlAttribute(0, 2, OpenGl.GL_FLOAT, GlScaling.Float, 2 * sizeof(float), 0));
    OpenGl.EnableVertexAttribArray(0);

    var labelBuf = new float2[8192];
    var weaponNames = new string[weapons.Length];
    for (var i = 0; i < weapons.Length; i++)
        weaponNames[i] = weapons[i].Name.ToUpperInvariant();

    // Construit les quads du texte des 4 secteurs, centrés à r=0.30 sur l'angle médian de chaque secteur.
    const float PX = 0.016f;
    int BuildLabels()
    {
        var n = 0;
        for (var s = 0; s < weaponNames.Length; s++)
        {
            var m = MathF.PI / 2 + s * MathF.PI / 2; // médian du secteur (0 = haut)
            var cx = MathF.Cos(m) * 0.30f;
            var cy = MathF.Sin(m) * 0.30f;
            var text = weaponNames[s];
            var x0 = cx - (text.Length * 6 * PX - PX) / 2;
            var y0 = cy - 3.5f * PX;
            for (var i = 0; i < text.Length; i++)
            {
                var g = Glyph(text[i]);
                for (var l = 0; l < 7 && n < labelBuf.Length - 6; l++)
                    for (var c = 0; c < 5; c++)
                        if (((g[l] >> (4 - c)) & 1) == 1)
                        {
                            var x = x0 + i * 6 * PX + c * PX;
                            var y = y0 + (6 - l) * PX; // ligne 0 du glyphe en haut (NDC y-up)
                            labelBuf[n++] = new float2(x, y);
                            labelBuf[n++] = new float2(x + PX, y);
                            labelBuf[n++] = new float2(x + PX, y + PX);
                            labelBuf[n++] = new float2(x, y);
                            labelBuf[n++] = new float2(x + PX, y + PX);
                            labelBuf[n++] = new float2(x, y + PX);
                        }
            }
        }
        return n;
    }

    OpenGl.Enable(GlCapability.DepthTest);
    OpenGl.ClearColor(float4.Rgb(0.12f, 0.14f, 0.2f));

    // ============================== Entrées ==============================
    var yaw = 0f;
    var pitch = 0f;
    var lastMouse = new double2(WIDTH / 2.0, HEIGHT / 2.0);
    var shootQueued = false;
    var jumpQueued = false;
    var gadgetQueued = false;

    if (!selftest)
    {
        window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_DISABLED); // capture la souris
        if (Glfw.RawMotionSupported())
            window.SetInputMode(GlfwInputMode.RawMouseMotion, Glfw.Constants.GLFW_TRUE);
    }

    window.OnCursorPos += (_, pos) =>
    {
        var dx = pos.X - lastMouse.X;
        var dy = pos.Y - lastMouse.Y;
        lastMouse = pos;
        if (wheelOpen)
            return; // la souris sert à choisir dans la roue, pas à viser
        yaw += (float)(dx * 0.0024);
        pitch = Math.Clamp(pitch - (float)(dy * 0.0024), -1.5f, 1.5f);
    };
    window.OnMouseButton += (_, button, action, _) =>
    {
        if (button == GlfwMouseButton.Left && action == GlfwButtonAction.Press && !wheelOpen)
            shootQueued = true;
        if (button == GlfwMouseButton.Right)
        {
            if (action == GlfwButtonAction.Press)
                wheelOpen = true;
            else if (wheelOpen)
            {
                wheelOpen = false;
                currentWeapon = selectedSlot; // on équipe l'arme pointée
            }
        }
    };
    window.OnScroll += (_, offset) =>
    {
        if (!wheelOpen)
            currentGadget = (currentGadget + (offset.Y > 0 ? 1 : -1) + gadgets.Length) % gadgets.Length;
    };
    window.OnKey += (handle, key, _, action, _) =>
    {
        if (action != GlfwKeyAction.Press)
            return;
        if (key == GlfwKey.Escape)
            handle.ShouldClose = true;
        else if (key == GlfwKey.E)
            gadgetQueued = true;
        else if (key == GlfwKey.Number1) currentWeapon = 0;
        else if (key == GlfwKey.Number2) currentWeapon = 1;
        else if (key == GlfwKey.Number3) currentWeapon = 2;
        else if (key == GlfwKey.Number4) currentWeapon = 3;
    };

    // ============================== État du jeu ==============================
    var score = 0;
    var shots = 0;
    var hits = 0;
    var nextShotAt = 0.0;
    var tracerUntil = 0.0;
    var flashUntil = 0.0;
    var tracerFrom = float3.Zero;
    var tracerTo = float3.Zero;
    var recoil = 0f;
    var frames = 0;
    var captureAt = -1; // frame à capturer juste après une destruction de cible (selftest)

    float3 Eye()
    {
        var p = bodyInterface.GetPosition(playerBody);
        return p + new float3(0, 0.55f, 0); // hauteur des yeux au-dessus du centre de la capsule
    }

    float3 ViewDir()
    {
        var cp = MathF.Cos(pitch);
        var d = new float3(MathF.Sin(yaw) * cp, MathF.Sin(pitch), -MathF.Cos(yaw) * cp);
        return float3.Dot(d, d) > 1e-8f ? d : new float3(0, 0, -1);
    }

    void Shoot(double now)
    {
        var w = weapons[currentWeapon];
        shots++;
        nextShotAt = now + w.Cadence;
        recoil = 1f;
        flashUntil = now + 0.055;

        var eye = Eye();
        var dir0 = ViewDir();
        // Le rayon part un peu devant l'œil pour ne pas toucher la capsule du joueur elle-même.
        var muzzle = eye + dir0 * 0.9f;

        for (var p = 0; p < w.Pellets; p++)
        {
            var dir = dir0;
            if (w.Spread > 0f)
                dir = float3.Normalize(
                    dir0
                    + new float3(
                        (float)(rng.NextDouble() - 0.5) * 2 * w.Spread,
                        (float)(rng.NextDouble() - 0.5) * 2 * w.Spread,
                        (float)(rng.NextDouble() - 0.5) * 2 * w.Spread
                    )
                );

            var ray = new RRayCast { Origin = muzzle, Direction = dir * 100f }; // direction normalisée, longueur = portée
            if (!narrowPhase.CastRayClosest(in ray, rayCastSettings, out var hit))
                continue;
            if (hit.BodyID == playerBody)
                continue;

            hits++;
            var impact = muzzle + dir * (100f * hit.Fraction);
            tracerFrom = muzzle;
            tracerTo = impact;
            tracerUntil = now + 0.09;
            bodyInterface.AddImpulse(hit.BodyID, dir * w.Impulse, impact); // le tir repousse ce qu'il touche

            var idx = targets.FindIndex(t => t.Alive && t.Body == hit.BodyID);
            if (idx >= 0)
            {
                var t = targets[idx];
                t.Hp -= w.Damage;
                score += w.Damage;
                if (t.Hp <= 0)
                    DestroyTarget(t, now, dir);
            }
            if (selftest && shots <= 3 && idx >= 0)
                Console.WriteLine($"  shot#{shots} [{w.Name}] -> TARGET#{idx} hp={targets[idx].Hp} frac={hit.Fraction:F3}");
        }
    }

    // Détruit une cible : elle explose en 10 débris physiques qui volent, puis respawn 3 s plus tard.
    void DestroyTarget(Target t, double now, float3 hitDir)
    {
        if (!t.Alive)
            return;
        t.Alive = false;
        t.RespawnAt = now + 3.0;
        score += 5;
        var pos = bodyInterface.GetPosition(t.Body);
        for (var i = 0; i < 10; i++)
        {
            using var shape = BoxShapeSettings.Create(new float3(0.12f), 0.02f, default);
            var p = pos + new float3(
                (float)(rng.NextDouble() - 0.5) * 0.6f,
                (float)(rng.NextDouble() - 0.5) * 0.6f,
                (float)(rng.NextDouble() - 0.5) * 0.6f);
            using var settings = BodyCreationSettings.Create(
                shape, p,
                floatQuat.CreateFromYawPitchRoll((float)(rng.NextDouble() * 6.28), (float)(rng.NextDouble() * 6.28), 0),
                MotionType.Dynamic, LAYER_MOVING);
            var b = bodyInterface.CreateAndAddBody(settings, Activation.Activate);
            bodyInterface.SetLinearVelocity(
                b,
                hitDir * 6f + new float3(
                    (float)(rng.NextDouble() - 0.5) * 8f,
                    (float)rng.NextDouble() * 7f,
                    (float)(rng.NextDouble() - 0.5) * 8f));
            debris.Add((b, now + 2.5, new float3(0.12f), t.Color));
        }
        bodyInterface.RemoveBody(t.Body);
        bodyInterface.DestroyBody(t.Body);
        if (selftest)
        {
            captureAt = frames + 8; // on prendra une capture 8 frames plus tard pour voir les débris
            Console.WriteLine($"  💥 frame {frames}: cible #{targets.IndexOf(t)} détruite à ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) -> 10 débris");
        }
    }

    void UseGadget(double now)
    {
        if (now < nextGadgetAt)
            return;
        switch (currentGadget)
        {
            case 0: // Grenade : une sphère physique lancée qui explose après 2.5 s
            {
                nextGadgetAt = now + 1.0;
                using var shape = SphereShapeSettings.Create(0.18f);
                var eye = Eye();
                var dir = ViewDir();
                using var settings = BodyCreationSettings.Create(
                    shape, eye + dir * 0.8f, floatQuat.Identity, MotionType.Dynamic, LAYER_MOVING);
                var b = bodyInterface.CreateAndAddBody(settings, Activation.Activate);
                bodyInterface.SetLinearVelocity(b, dir * 14f + new float3(0, 5f, 0));
                grenades.Add((b, now + 2.5));
                break;
            }
            case 1: // Dash : propulsion dans la direction de la vue
                nextGadgetAt = now + 3.0;
                bodyInterface.SetLinearVelocity(playerBody, ViewDir() * 20f + new float3(0, 3f, 0));
                break;
            case 2: // Jetpack : 1.2 s de propulsion verticale
                nextGadgetAt = now + 5.0;
                jetpackUntil = now + 1.2;
                break;
        }
    }

    void ExplodeGrenades(double now)
    {
        for (var i = grenades.Count - 1; i >= 0; i--)
        {
            var (gb, explodeAt) = grenades[i];
            if (now < explodeAt)
                continue;
            grenades.RemoveAt(i);
            var ep = bodyInterface.GetPosition(gb);
            explosionFlashUntil = now + 0.35;
            // onde de choc : impulsion radiale sur les cibles proches (+ dégâts si très proche)
            foreach (var t in targets)
            {
                var tp = bodyInterface.GetPosition(t.Body);
                var d = tp - ep;
                var dist = MathF.Sqrt(float3.Dot(d, d));
                if (dist < 7f)
                {
                    var dir = dist > 1e-3f ? d / dist : float3.UnitY;
                    bodyInterface.AddImpulse(t.Body, dir * 40f * (1f - dist / 7f) + new float3(0, 12f, 0), tp);
                    if (dist < 4.5f && t.Alive)
                    {
                        t.Hp -= 2;
                        score += 2;
                        if (t.Hp <= 0)
                            DestroyTarget(t, now, dir);
                    }
                }
            }
            // auto-knockback si le joueur est proche
            var pp = bodyInterface.GetPosition(playerBody);
            var dp = pp - ep;
            var ddist = MathF.Sqrt(float3.Dot(dp, dp));
            if (ddist < 6f && ddist > 1e-3f)
                bodyInterface.AddImpulse(playerBody, dp / ddist * 25f * (1f - ddist / 6f), pp);
            bodyInterface.RemoveBody(gb);
            bodyInterface.DestroyBody(gb);
            if (selftest)
                Console.WriteLine($"  💥 grenade à ({ep.X:F1},{ep.Y:F1},{ep.Z:F1})");
        }
    }

    static floatQuat AlignZ(float3 d)
    {
        var dir = float3.Normalize(d);
        var dot = float3.Dot(float3.UnitZ, dir);
        if (dot > 0.9999f)
            return floatQuat.Identity;
        if (dot < -0.9999f)
            return floatQuat.CreateFromAxisAngle(float3.UnitX, MathF.PI);
        return floatQuat.CreateFromAxisAngle(float3.Normalize(float3.Cross(float3.UnitZ, dir)), MathF.Acos(dot));
    }

    // ============================== Boucle principale ==============================
    const float TIME_STEP = 1f / 60f;
    var simTime = 0.0;
    var prevTime = selftest ? 0.0 : Glfw.Time;
    var accumulator = 0.0;
    var fpsTimer = 0.0;
    var fpsCount = 0;
    var fps = 0.0;
    var titleTimer = 0.0;

    Console.WriteLine(
        selftest
            ? "SELFTEST: 900 frames scriptées (15 s simulées)..."
            : "BRUTAL FPS prêt — WASD bouger, souris viser, clic tirer, espace sauter, shift sprint, Echap quitter.");

    if (selftest)
    {
        // rayons de test : vers le bas (sol attendu, frac ~0.05) et horizontal (mur ouest, frac ~0.2)
        var downRay = new RRayCast { Origin = new float3(0, 5, 0), Direction = new float3(0, -100, 0) };
        var ok1 = narrowPhase.CastRayClosest(in downRay, rayCastSettings, out var downHit);
        Console.WriteLine($"raycast DOWN : ok={ok1} body={downHit.BodyID} frac={downHit.Fraction:F3}");
        var sideRay = new RRayCast { Origin = new float3(0, 1, 0), Direction = new float3(-100, 0, 0) };
        var ok2 = narrowPhase.CastRayClosest(in sideRay, rayCastSettings, out var sideHit);
        Console.WriteLine($"raycast SIDE : ok={ok2} body={sideHit.BodyID} frac={sideHit.Fraction:F3}");
        Console.WriteLine($"playerBody={playerBody} statics[0].Body={statics[0].Body} targets[0].Body={targets[0].Body}");
    }

    while (!window.ShouldClose)
    {
        Glfw.PollEvents();
        var now = selftest ? simTime += TIME_STEP : Glfw.Time;
        var dt = Math.Min(now - prevTime, 0.25);
        prevTime = now;

        // --- entrées clavier ---
        var fwd = 0f;
        var strafe = 0f;
        if (window.GetKey(GlfwKey.W) is GlfwKeyAction.Press or GlfwKeyAction.Repeat) fwd += 1;
        if (window.GetKey(GlfwKey.S) is GlfwKeyAction.Press or GlfwKeyAction.Repeat) fwd -= 1;
        if (window.GetKey(GlfwKey.D) is GlfwKeyAction.Press or GlfwKeyAction.Repeat) strafe += 1;
        if (window.GetKey(GlfwKey.A) is GlfwKeyAction.Press or GlfwKeyAction.Repeat) strafe -= 1;
        var sprint = window.GetKey(GlfwKey.LeftShift) is GlfwKeyAction.Press or GlfwKeyAction.Repeat;

        if (selftest)
        {
            // pilotage scripté : on vise la cible la plus proche, on tire, on saute,
            // et on avance sauf si un mur bloque devant (sinon on tourne)
            var pp0 = bodyInterface.GetPosition(playerBody);
            var eye0 = pp0 + new float3(0, 0.55f, 0);
            var bestDist = float.MaxValue;
            var bestDir = float3.UnitZ;
            if (frames < 60)
            {
                // pour la capture : viser le centre de l'arène plutôt qu'une cible
                bestDir = float3.Normalize(new float3(0, 1f, 0) - eye0);
            }
            else
            {
                foreach (var t in targets)
                {
                    var d = bodyInterface.GetPosition(t.Body) - eye0;
                    var dist = float3.Dot(d, d);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        if (dist > 1e-4f)
                            bestDir = float3.Normalize(d);
                    }
                }
            }
            if (float3.Dot(bestDir, bestDir) > 1e-8f && !float.IsNaN(bestDir.X))
            {
                yaw = MathF.Atan2(bestDir.X, -bestDir.Z);
                pitch = Math.Clamp(MathF.Asin(Math.Clamp(bestDir.Y, -1f, 1f)), -1.5f, 1.5f); // jamais pile vertical
            }
            var ahead = new RRayCast
            {
                Origin = eye0,
                Direction = new float3(MathF.Sin(yaw), 0, -MathF.Cos(yaw)) * 2.5f
            };
            if (narrowPhase.CastRayClosest(in ahead, rayCastSettings, out var aheadHit) && aheadHit.BodyID != playerBody)
            {
                fwd = 0;
                yaw += 0.06f; // obstacle devant : on tourne
            }
            else
            {
                fwd = 1;
            }
            if (frames == 149)
                // on remet le joueur au centre de l'arène pour une capture dégagée
                bodyInterface.SetPositionAndRotation(playerBody, new float3(0, 0.9f, 8), floatQuat.Identity, Activation.Activate);
            if (frames % 120 == 60) jumpQueued = true;
            if (frames % 25 == 10) shootQueued = true;
            // teste chaque gadget explicitement (grenade, dash, jetpack, grenade)
            if (frames is 100 or 300 or 500 or 700)
            {
                currentGadget = frames switch { 100 => 0, 300 => 1, 500 => 2, _ => 0 };
                gadgetQueued = true;
            }
            if (frames % 300 == 250) currentWeapon = (currentWeapon + 1) % weapons.Length; // cycler les armes
            // ouvre la roue des armes entre les frames 400 et 450, souris en haut (pour la capture 420)
            if (frames is >= 400 and < 450)
            {
                wheelOpen = true;
                lastMouse = new double2(WIDTH / 2.0, HEIGHT / 2.0 - 120);
            }
            else if (frames == 450)
            {
                wheelOpen = false;
                currentWeapon = selectedSlot;
            }
            if (frames >= 900) window.ShouldClose = true;
        }

        // --- physique à pas fixe ---
        accumulator += dt;
        while (accumulator >= TIME_STEP)
        {
            var vel = bodyInterface.GetLinearVelocity(playerBody);
            var forward2 = new float3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
            var right2 = float3.Normalize(float3.Cross(forward2, float3.UnitY));
            var wish = forward2 * fwd + right2 * strafe;
            if (float3.Dot(wish, wish) > 0.0001f)
                wish = float3.Normalize(wish);
            var speed = sprint ? 10f : 6.5f;
            var vy = vel.Y;
            if (now < jetpackUntil)
                vy = 9f; // gadget jetpack
            bodyInterface.SetLinearVelocity(playerBody, new float3(wish.X * speed, vy, wish.Z * speed));

            // au sol ? (raycast vers le bas, en ignorant le joueur lui-même)
            var pp = bodyInterface.GetPosition(playerBody);
            // depuis le bas de la capsule, un court rayon vers le bas détecte le sol
            var downRay = new RRayCast { Origin = pp + new float3(0, -0.8f, 0), Direction = new float3(0, -0.3f, 0) };
            var grounded = narrowPhase.CastRayClosest(in downRay, rayCastSettings, out var downHit) && downHit.BodyID != playerBody;
            var wantJump = window.GetKey(GlfwKey.Space) is GlfwKeyAction.Press or GlfwKeyAction.Repeat;
            if (grounded && (wantJump || jumpQueued))
            {
                jumpQueued = false;
                bodyInterface.SetLinearVelocity(playerBody, new float3(wish.X * speed, 7.5f, wish.Z * speed));
            }
            ExplodeGrenades(now);

            bodyInterface.SetRotation(playerBody, floatQuat.Identity, Activation.DontActivate); // la capsule ne bascule pas
            physicsSystem.Update(TIME_STEP, 1, tempAllocator, jobSystem);
            accumulator -= TIME_STEP;
        }

        // --- tir ---
        if (shootQueued)
        {
            shootQueued = false;
            if (now >= nextShotAt && !wheelOpen)
                Shoot(now);
        }

        // --- gadget (touche E) ---
        if (gadgetQueued)
        {
            gadgetQueued = false;
            UseGadget(now);
        }

        // --- respawn des cibles (repositionnées si juste touchées, recréées si détruites) + cleanup débris ---
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t.RespawnAt > 0 && now >= t.RespawnAt)
            {
                t.RespawnAt = 0;
                var pos = RandomSpot(rng, 4f);
                if (t.Alive)
                {
                    bodyInterface.SetPositionAndRotation(t.Body, pos, floatQuat.Identity, Activation.Activate);
                    bodyInterface.SetLinearVelocity(t.Body, float3.Zero);
                }
                else
                {
                    using var shape = BoxShapeSettings.Create(t.Half, 0.05f, default);
                    using var settings = BodyCreationSettings.Create(shape, pos, floatQuat.Identity, MotionType.Dynamic, LAYER_MOVING);
                    t.Body = bodyInterface.CreateAndAddBody(settings, Activation.Activate);
                    t.Alive = true;
                    t.Hp = TARGET_HP;
                }
            }
        }
        for (var i = debris.Count - 1; i >= 0; i--)
        {
            var (db, removeAt, _, _) = debris[i];
            if (now >= removeAt)
            {
                debris.RemoveAt(i);
                bodyInterface.RemoveBody(db);
                bodyInterface.DestroyBody(db);
            }
        }

        // --- rendu ---
        var eye = Eye();
        var viewDir = ViewDir();
        var view = float4x4.CreateLookAt(eye, eye + viewDir, float3.UnitY);
        var projection = float4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(75f), (float)WIDTH / HEIGHT, 0.1f, 200f);
        var viewProjection = view * projection;

        OpenGl.Clear(GlClearBit.Color | GlClearBit.Depth);

        litProgram.Use();
        litProgram.Uniform("viewProjection"u8, viewProjection);
        litProgram.Uniform("eyePos"u8, eye);
        cubeVao.Bind();

        foreach (var s in statics)
        {
            var model = float4x4.CreateScale(s.Half) * float4x4.CreateTranslation(s.Pos);
            litProgram.Uniform("model"u8, model);
            litProgram.Uniform("color"u8, s.Color);
            OpenGl.Draw(GlTopology.Triangles, cube.Length, 1, 0, 0);
        }
        foreach (var t in targets)
        {
            bodyInterface.GetPositionAndRotation(t.Body, out var pos, out var rot);
            var model = float4x4.CreateScale(t.Half) * float4x4.CreateFromQuaternion(rot) * float4x4.CreateTranslation(pos);
            litProgram.Uniform("model"u8, model);
            litProgram.Uniform("color"u8, t.Color);
            OpenGl.Draw(GlTopology.Triangles, cube.Length, 1, 0, 0);
        }
        foreach (var (gb, _) in grenades) // les grenades en vol (petites sphères carrées sombres)
        {
            var gp = bodyInterface.GetPosition(gb);
            var model = float4x4.CreateScale(0.18f) * float4x4.CreateTranslation(gp);
            litProgram.Uniform("model"u8, model);
            litProgram.Uniform("color"u8, new float3(0.1f, 0.1f, 0.12f));
            OpenGl.Draw(GlTopology.Triangles, cube.Length, 1, 0, 0);
        }
        foreach (var (db, _, half, col) in debris) // éclats des cibles détruites
        {
            bodyInterface.GetPositionAndRotation(db, out var pos, out var rot);
            var model = float4x4.CreateScale(half) * float4x4.CreateFromQuaternion(rot) * float4x4.CreateTranslation(pos);
            litProgram.Uniform("model"u8, model);
            litProgram.Uniform("color"u8, col);
            OpenGl.Draw(GlTopology.Triangles, cube.Length, 1, 0, 0);
        }

        // tracer du tir (cube étiré entre la bouche et le point d'impact)
        if (now < tracerUntil)
        {
            flatProgram.Use();
            flatProgram.Uniform("viewProjection"u8, viewProjection);
            cubeVao.Bind();
            var d = tracerTo - tracerFrom;
            var len = MathF.Sqrt(float3.Dot(d, d));
            if (len < 1e-4f)
                goto NoTracer;
            var mid = (tracerFrom + tracerTo) * 0.5f;
            var model = float4x4.CreateScale(new float3(0.012f, 0.012f, len * 0.5f))
                * float4x4.CreateFromQuaternion(AlignZ(d))
                * float4x4.CreateTranslation(mid);
            flatProgram.Uniform("model"u8, model);
            flatProgram.Uniform("color"u8, new float3(1f, 0.9f, 0.3f));
            OpenGl.Draw(GlTopology.Triangles, cube.Length, 1, 0, 0);
            NoTracer: ;
        }

        // arme en vue subjective (view = identité, donc collée à la caméra) + recul + flash
        recoil = Math.Max(0f, recoil - (float)dt * 8f);
        OpenGl.Disable(GlCapability.DepthTest);
        litProgram.Use();
        litProgram.Uniform("viewProjection"u8, projection);
        litProgram.Uniform("eyePos"u8, float3.Zero);
        cubeVao.Bind();
        // Arme sur le bord droit, tournée pour qu'on ne voie que son profil (flanc).
        var gunRot = floatQuat.CreateFromYawPitchRoll(-0.45f, 0f, -0.25f); // yaw vers le centre, roll pour le côté
        var gunPos = new float3(0.34f, -0.26f, -0.45f - recoil * 0.05f);
        var gunModel =
            float4x4.CreateScale(new float3(0.02f, 0.035f, 0.15f))
            * float4x4.CreateFromQuaternion(gunRot)
            * float4x4.CreateTranslation(gunPos);
        litProgram.Uniform("model"u8, gunModel);
        litProgram.Uniform("color"u8, new float3(0.15f, 0.15f, 0.18f));
        OpenGl.Draw(GlTopology.Triangles, cube.Length, 1, 0, 0);

        if (now < flashUntil)
        {
            flatProgram.Use();
            flatProgram.Uniform("viewProjection"u8, projection);
            cubeVao.Bind();
            // flash à la bouche du canon (suit la rotation de l'arme)
            var flashWorld = gunPos + gunRot * new float3(0, 0, -0.68f);
            var flashModel = float4x4.CreateScale(0.03f) * float4x4.CreateTranslation(flashWorld);
            flatProgram.Uniform("model"u8, flashModel);
            flatProgram.Uniform("color"u8, new float3(1f, 0.8f, 0.2f));
            OpenGl.Draw(GlTopology.Triangles, cube.Length, 1, 0, 0);
        }

        // viseur 2D
        OpenGl.Disable(GlCapability.DepthTest);
        overlayProgram.Use();
        overlayProgram.Uniform("color"u8, new float4(0.9f, 0.95f, 1f, 1f));
        crossVao.Bind();
        OpenGl.Draw(GlTopology.Lines, 8, 1, 0, 0);

        // flash d'explosion plein écran (alpha décroissant)
        if (now < explosionFlashUntil)
        {
            var k = (float)((explosionFlashUntil - now) / 0.35);
            OpenGl.Enable(GlCapability.Blend);
            OpenGl.BlendFunc(OpenGl.GL_SRC_ALPHA, OpenGl.GL_ONE_MINUS_SRC_ALPHA);
            overlayProgram.Uniform("color"u8, new float4(1f, 0.55f, 0.1f, 0.55f * k));
            fullVao.Bind();
            OpenGl.Draw(GlTopology.Triangles, 6, 1, 0, 0);
            OpenGl.Disable(GlCapability.Blend);
        }

        // roue des armes (clic droit maintenu)
        if (wheelOpen)
        {
            var ndc = new float2(
                (float)(lastMouse.X / WIDTH * 2.0 - 1.0),
                (float)(1.0 - lastMouse.Y / HEIGHT * 2.0));
            var angle = MathF.Atan2(ndc.Y, ndc.X);
            var dist = MathF.Sqrt(ndc.X * ndc.X + ndc.Y * ndc.Y);
            if (dist > 0.12f) // deadzone au centre
            {
                var a = (angle - MathF.PI / 4 + MathF.PI * 4) % (MathF.PI * 2);
                selectedSlot = Math.Clamp((int)(a / (MathF.PI / 2)), 0, 3);
            }

            OpenGl.Enable(GlCapability.Blend);
            OpenGl.BlendFunc(OpenGl.GL_SRC_ALPHA, OpenGl.GL_ONE_MINUS_SRC_ALPHA);
            overlayProgram.Use();
            ringVao.Bind();
            for (var s = 0; s < weapons.Length; s++)
            {
                var c = weapons[s].Color;
                var sel = s == selectedSlot;
                overlayProgram.Uniform("color"u8, new float4(sel ? c : c * 0.45f, sel ? 0.95f : 0.45f));
                OpenGl.Draw(GlTopology.Triangles, SEG * 6, 1, s * SEG * 6, 0);
            }
            // disque central avec la couleur de l'arme sélectionnée
            overlayProgram.Uniform("color"u8, new float4(weapons[selectedSlot].Color, 0.9f));
            centerVao.Bind();
            OpenGl.Draw(GlTopology.Triangles, 6, 1, 0, 0);
            // pointeur vers la souris
            var plen = MathF.Min(dist, R1);
            var pdir = dist > 1e-4f ? ndc / dist : new float2(0, 1);
            pointerVbo.Bind(GlBindTarget.ArrayBuffer);
            OpenGl.BufferData(GlBindTarget.ArrayBuffer, new[] { new float2(0, 0), pdir * plen }, GlBufferUsage.DynamicDraw);
            pointerVao.Bind();
            overlayProgram.Uniform("color"u8, new float4(1f, 1f, 1f, 0.9f));
            OpenGl.Draw(GlTopology.Lines, 2, 1, 0, 0);
            // noms des armes sur chaque secteur
            var labelCount = BuildLabels();
            textVbo.Bind(GlBindTarget.ArrayBuffer);
            OpenGl.BufferData(GlBindTarget.ArrayBuffer, labelBuf.AsSpan(0, labelCount), GlBufferUsage.DynamicDraw);
            textVao.Bind();
            overlayProgram.Uniform("color"u8, new float4(1f, 1f, 1f, 0.95f));
            OpenGl.Draw(GlTopology.Triangles, labelCount, 1, 0, 0);
            OpenGl.Disable(GlCapability.Blend);
        }
        OpenGl.Enable(GlCapability.DepthTest);

        // En selftest, on sauvegarde des frames du framebuffer en PNG (preuve du rendu).
        if (selftest && (frames is 30 or 150 or 420 or 600 || frames == captureAt))
        {
            var pixels = new byte[WIDTH * HEIGHT * 4];
            unsafe
            {
                fixed (byte* ptr = pixels)
                    OpenGl.ReadPixels(0, 0, WIDTH, HEIGHT, OpenGl.GL_RGBA, OpenGl.GL_UNSIGNED_BYTE, ptr);
            }
            var flipped = new byte[pixels.Length]; // OpenGL est bottom-up, on retourne l'image
            for (var y = 0; y < HEIGHT; y++)
                Array.Copy(pixels, y * WIDTH * 4, flipped, (HEIGHT - 1 - y) * WIDTH * 4, WIDTH * 4);
            Stb.WritePng($"frame{frames}.png", WIDTH, HEIGHT, 4, flipped);
            Console.WriteLine($"frame{frames}.png sauvegardé (capture du framebuffer OpenGL)");
        }

        window.SwapBuffers();

        // --- HUD (titre de fenêtre) + compteur FPS ---
        frames++;
        fpsCount++;
        fpsTimer += dt;
        if (fpsTimer >= 0.5)
        {
            fps = fpsCount / fpsTimer;
            fpsCount = 0;
            fpsTimer = 0;
        }
        titleTimer += dt;
        if (titleTimer > 0.2)
        {
            titleTimer = 0;
            var arme = wheelOpen ? $"roue: {weapons[selectedSlot].Name}" : $"[{currentWeapon + 1}] {weapons[currentWeapon].Name}";
            var gadget = $"{gadgets[currentGadget]} (E, molette=switch)";
            if (now < nextGadgetAt)
                gadget += $" cd {nextGadgetAt - now:F1}s";
            var alive = 0;
            foreach (var t in targets)
                if (t.Alive)
                    alive++;
            window.Title =
                $"BRUTAL FPS — {arme} — gadget: {gadget} — score {score} — cibles {alive}/{targets.Count} — {shots} tirs / {hits} touchés — {fps:F0} FPS";
        }
    }

    var endPos = bodyInterface.GetPosition(playerBody);
    if (selftest)
        Console.WriteLine(
            $"SELFTEST DONE frames={frames} shouldClose={window.ShouldClose} score={score} shots={shots} hits={hits} playerPos=({endPos.X:F2}, {endPos.Y:F2}, {endPos.Z:F2})");

    // ============================== Nettoyage ==============================
    OpenGl.DeleteVertexArrays([cubeVao, crossVao, ringVao, centerVao, fullVao, pointerVao, textVao]);
    OpenGl.DeleteBuffers([cubeVbo, crossVbo, ringVbo, centerVbo, fullVbo, pointerVbo, textVbo]);

    foreach (var (gb, _) in grenades)
    {
        bodyInterface.RemoveBody(gb);
        bodyInterface.DestroyBody(gb);
    }
    foreach (var (db, _, _, _) in debris)
    {
        bodyInterface.RemoveBody(db);
        bodyInterface.DestroyBody(db);
    }
    foreach (var t in targets)
    {
        bodyInterface.RemoveBody(t.Body);
        bodyInterface.DestroyBody(t.Body);
    }
    foreach (var s in statics)
    {
        bodyInterface.RemoveBody(s.Body);
        bodyInterface.DestroyBody(s.Body);
    }
    bodyInterface.RemoveBody(playerBody);
    bodyInterface.DestroyBody(playerBody);
}

// ============================== Helpers globaux ==============================

static GlProgram LinkProgram(string vertSrc, string fragSrc)
{
    using var vs = OpenGl.CreateShader(GlShaderType.Vertex).SetSource(Encoding.UTF8.GetBytes(vertSrc)).Compile();
    using var fs = OpenGl.CreateShader(GlShaderType.Fragment).SetSource(Encoding.UTF8.GetBytes(fragSrc)).Compile();
    return OpenGl.CreateProgram().Attach(vs, fs).Link();
}

static Vertex[] BuildCube()
{
    var cube = new List<Vertex>();
    foreach (var normal in new[] { float3.UnitX, -float3.UnitX, float3.UnitY, -float3.UnitY, float3.UnitZ, -float3.UnitZ })
    {
        var u = new float3(normal.Y, normal.Z, normal.X);
        var v = float3.Cross(normal, u);
        float2[] corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, -1), new(1, 1), new(-1, 1)];
        foreach (var corner in corners)
            cube.Add(new Vertex(normal + u * corner.X + v * corner.Y, normal));
    }
    return cube.ToArray();
}


// Mini-font 5x7 maison (pas de dépendance font) : chaque glyphe = 7 octets (lignes haut->bas),
// bit 4 (0x10) = colonne de gauche ... bit 0 (0x01) = colonne de droite.
static byte[] Glyph(char c) => char.ToUpperInvariant(c) switch
{
    'A' => [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
    'B' => [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
    'C' => [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
    'D' => [0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E],
    'E' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
    'F' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
    'G' => [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
    'H' => [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
    'I' => [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
    'J' => [0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C],
    'K' => [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
    'L' => [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
    'M' => [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
    'N' => [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
    'O' => [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
    'P' => [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
    'Q' => [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
    'R' => [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
    'S' => [0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E],
    'T' => [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
    'U' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
    'V' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
    'W' => [0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11],
    'X' => [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
    'Y' => [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
    'Z' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
    '0' => [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
    '1' => [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
    '2' => [0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F],
    '3' => [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
    '4' => [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
    '5' => [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
    '6' => [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
    '7' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
    '8' => [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
    '9' => [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
    '-' => [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
    _ => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
};

record struct Vertex(float3 Position, float3 Normal);

// Une arme hitscan : cadence (s), impulsion par balle, nombre de pellets, dispersion, dégâts, couleur (roue).
sealed record Weapon(string Name, double Cadence, float Impulse, int Pellets, float Spread, int Damage, float3 Color);

sealed class Target(uint body, float3 half, float3 color)
{
    public uint Body = body;
    public float3 Half = half;
    public float3 Color = color;
    public double RespawnAt;
    public int Hp = 5;
    public bool Alive = true;
}
