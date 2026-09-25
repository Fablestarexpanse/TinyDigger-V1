using System.IO;
using System.Linq;
using TinyDiggers.Interaction;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Builds the prefabs and Animator controllers for the machines made in machine-forge
    /// (F:/machine-forge) once <c>Art/Tools/td_forge_import.py</c> has turned each one into
    /// <c>Art/Units/Forge/&lt;machine&gt;/&lt;machine&gt;.fbx</c> plus one <c>machine@Clip.fbx</c> per clip.
    /// Run it again after a re-export; it rebuilds in place and keeps the prefabs' GUIDs.
    ///
    /// Each prefab is a plain root, which <see cref="CrewView"/> turns to the unit's heading every
    /// frame, then a Size object with the size ruling, then the model with the FBX's own axis turn,
    /// the Animator and a <see cref="MachineBody"/>. Put on the root, the turn would be overwritten
    /// by the heading and the machine would lie on its back; put on the model, the size would be
    /// undone by the clips, which key the model's root.
    ///
    /// Sizes keep the game's rulings (Ronan, 2026-09-24): the dumper stands <see cref="DumperHeight"/>
    /// over all, and every machine gets the same factor so the digger's bucket still clears the
    /// dumper's bed as the forge checked it.
    /// </summary>
    public static class ForgeMachines
    {
        const string Root = "Assets/TinyDiggers/Art/Units/Forge";

        /// <summary>The dumper's ruled height over all, metres (Ronan, 2026-09-22).</summary>
        const float DumperHeight = 0.79f;

        /// <summary>The landing craft's waterline above its keel, loaded, at forge size (boat.machine.json).</summary>
        const float BoatWaterlineLoaded = 0.12f;

        struct Machine
        {
            public string Name;
            public string Work;             // clip the Work state plays, or null
            public float WorkSeconds;       // what the work took before the swap; 0 = as authored
            public string Drive;            // clip Move and Carry play, or null
            public string Reverse;          // clip for backing up, or null to play Drive backwards
            public float MetresPerLoop;     // at forge size, from <machine>.machine.json
        }

        static readonly Machine[] Units =
        {
            // Dig and tip times are the walker clips' (2.97 s, 1.77 s), which the crew balance was
            // tuned on; the forge clips are twice and three times as long.
            new Machine { Name = "digger", Work = "Dig", WorkSeconds = 2.97f, Drive = "Drive", Reverse = "Reverse", MetresPerLoop = 0.5655f },
            new Machine { Name = "dumper", Work = "Tip", WorkSeconds = 1.77f, Drive = "Drive", MetresPerLoop = 0.8168f },
            new Machine { Name = "dozer", Work = "BladeCycle", WorkSeconds = 0f, Drive = "Drive", Reverse = "Reverse", MetresPerLoop = 0.4509f },
        };

        /// <summary>
        /// A working craft (Ronan, 2026-09-25, slice A: afloat first). Float points and waterlines
        /// are the forge's (<c>machine.json</c>), at forge size; no half width means the hull's own
        /// footprint (the gold dredge has no float points in its machine.json).
        /// </summary>
        struct Vessel
        {
            public string Name;
            public float HalfWidth, HalfLength;
            public float Waterline;
            public string[] Loops;          // clips that play all the time, each on its own layer
            public string[] Holds;          // clips played once on their own layer, held at the end
        }

        static readonly Vessel[] Vessels =
        {
            new Vessel { Name = "dredge", HalfWidth = 1.52f, HalfLength = 2.8f, Waterline = 0.192f, Loops = new string[0], Holds = new string[0] },
            // Its rest pose has the ladder down 3 m under the keel: raised to move (game_clips).
            new Vessel { Name = "golddredge", Waterline = 0.2f, Loops = new string[0], Holds = new[] { "LadderRaise" } },
            // Light waterline: it sails empty until the dredge loop fills it (slice C). Its rest pose
            // has the bottom doors hanging open 1.7 m under the keel: shut.
            new Vessel { Name = "scow", HalfWidth = 1.7f, HalfLength = 4.6f, Waterline = 0.22f, Loops = new string[0], Holds = new[] { "DoorsClose" } },
            new Vessel { Name = "tug", HalfWidth = 0.7f, HalfLength = 1.8f, Waterline = 0.3f, Loops = new[] { "Props" }, Holds = new string[0] },
        };

        [MenuItem("TinyDiggers/Build Forge Machines")]
        public static void Build()
        {
            var scale = DumperHeight / Height(Load("dumper"));
            foreach (var machine in Units)
                BuildUnit(machine, scale);
            BuildBoat(scale);
            foreach (var vessel in Vessels)
            {
                EnsureMaterials(vessel.Name);
                BuildVessel(vessel, scale);
            }

            WriteFleetCatalog();

            AssetDatabase.SaveAssets();
            Debug.Log($"ForgeMachines: built {Units.Length} machines, the landing craft and {Vessels.Length} working craft at x{scale:0.000}");
        }

        static GameObject Load(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/{name}/{name}.fbx");

        static AnimationClip Clip(string machine, string clip) =>
            AssetDatabase.LoadAllAssetsAtPath($"{Root}/{machine}/{machine}@{clip}.fbx")
                .OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));

        static float Height(GameObject model)
        {
            var probe = Object.Instantiate(model);
            var renderers = probe.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            Object.DestroyImmediate(probe);
            return bounds.size.y;
        }

        static void BuildUnit(Machine machine, float scale)
        {
            var controllerPath = $"{Root}/{machine.Name}/{machine.Name}.controller";
            var controller = FreshController(controllerPath);
            var states = controller.layers[0].stateMachine;
            // Idle has no clip: with write defaults on, the machine stands in its rest pose.
            states.defaultState = states.AddState(CrewAnimation.Idle);
            var drive = machine.Drive != null ? Clip(machine.Name, machine.Drive) : null;
            states.AddState(CrewAnimation.Move).motion = drive;
            states.AddState(CrewAnimation.Carry).motion = drive;
            // Backing in to tip (CrewAnimation.Reverse): the forge's own Reverse clip where there is
            // one, else the drive clip played backwards, wheels turning the other way.
            var reverse = states.AddState(CrewAnimation.Reverse);
            if (machine.Reverse != null)
                reverse.motion = Clip(machine.Name, machine.Reverse);
            else
            {
                reverse.motion = drive;
                reverse.speed = -1f;
            }
            var work = states.AddState(CrewAnimation.Work);
            var workSpeed = 1f;
            if (machine.Work != null)
            {
                var clip = Clip(machine.Name, machine.Work);
                work.motion = clip;
                if (clip != null && machine.WorkSeconds > 0f)
                    workSpeed = clip.length / machine.WorkSeconds;
                work.speed = workSpeed;
            }

            var body = Assemble(machine.Name, scale, controller);
            var info = body.GetComponentInChildren<Animator>().gameObject.AddComponent<MachineBody>();
            info.MetresPerDriveLoop = machine.MetresPerLoop * scale;
            info.DriveLoopSeconds = drive != null ? drive.length : 1f;
            info.WorkSpeed = workSpeed;
            Save(body, machine.Name);
        }

        /// <summary>
        /// The landing craft, as a prop for now (Ronan, 2026-09-24: model now, ferrying next): it
        /// bobs and turns its props, and its ramp can be lowered and raised by state.
        /// </summary>
        static void BuildBoat(float scale)
        {
            var controller = FreshController($"{Root}/boat/boat.controller");
            var bob = controller.layers[0].stateMachine;
            bob.defaultState = bob.AddState("Bob");
            bob.defaultState.motion = Clip("boat", "Bob");

            controller.AddLayer("Props");
            controller.AddLayer("Ramp");
            var layers = controller.layers;
            layers[1].defaultWeight = 1f;
            layers[2].defaultWeight = 1f;
            controller.layers = layers;
            var props = controller.layers[1].stateMachine;
            props.defaultState = props.AddState("Props");
            props.defaultState.motion = Clip("boat", "Props");
            var ramp = controller.layers[2].stateMachine;
            ramp.defaultState = ramp.AddState("Up");
            ramp.AddState("RampDown").motion = Clip("boat", "RampDown");
            ramp.AddState("RampUp").motion = Clip("boat", "RampUp");

            // It rides the water as drawn, swell and all (Ronan, 2026-09-24: "make sure boat floats
            // on our water"). Float points and the loaded waterline are the forge's
            // (boat.machine.json: corners at 0.9 x 1.4 m, waterline 0.12 m loaded), at our size.
            var boat = Assemble("boat", scale, controller);
            var floater = boat.AddComponent<PromptWaffle.DynamicWater.WaterFloater>();
            floater.Configure(new[]
            {
                new Vector3(0.9f, 0f, 1.4f) * scale, new Vector3(-0.9f, 0f, 1.4f) * scale,
                new Vector3(0.9f, 0f, -1.4f) * scale, new Vector3(-0.9f, 0f, -1.4f) * scale,
            }, BoatWaterlineLoaded * scale);
            Save(boat, "boat");
        }

        static void BuildVessel(Vessel vessel, float scale)
        {
            SetLooping(vessel.Name, "Bob", true);
            foreach (var loop in vessel.Loops)
                SetLooping(vessel.Name, loop, true);
            foreach (var hold in vessel.Holds)
                SetLooping(vessel.Name, hold, false);

            var controller = FreshController($"{Root}/{vessel.Name}/{vessel.Name}.controller");
            var bob = controller.layers[0].stateMachine;
            bob.defaultState = bob.AddState("Bob");
            bob.defaultState.motion = Clip(vessel.Name, "Bob");
            foreach (var clip in vessel.Loops.Concat(vessel.Holds))
            {
                controller.AddLayer(clip);
                var layers = controller.layers;
                layers[layers.Length - 1].defaultWeight = 1f;
                controller.layers = layers;
                var machine = controller.layers[layers.Length - 1].stateMachine;
                machine.defaultState = machine.AddState(clip);
                machine.defaultState.motion = Clip(vessel.Name, clip);
            }

            var root = Assemble(vessel.Name, scale, controller);
            var halfWidth = vessel.HalfWidth;
            var halfLength = vessel.HalfLength;
            if (halfWidth <= 0f)
            {
                // The hull's own footprint, a little inside its outline, back at forge size.
                var hull = root.GetComponentsInChildren<MeshRenderer>().First(r => r.name == "Hull_mesh");
                var bounds = LocalBounds(root.transform, hull);
                halfWidth = bounds.extents.x / scale * 0.85f;
                halfLength = bounds.extents.z / scale * 0.85f;
            }

            var floater = root.AddComponent<PromptWaffle.DynamicWater.WaterFloater>();
            floater.Configure(new[]
            {
                new Vector3(halfWidth, 0f, halfLength) * scale, new Vector3(-halfWidth, 0f, halfLength) * scale,
                new Vector3(halfWidth, 0f, -halfLength) * scale, new Vector3(-halfWidth, 0f, -halfLength) * scale,
            }, vessel.Waterline * scale);
            // Foam round the hull as it lies and moves (WaterFoamMap), sized to the float points
            // with a little over for the fenders.
            var foam = root.AddComponent<PromptWaffle.DynamicWater.WaterFoamEmitterComponent>();
            foam.HullSize = new Vector2(halfLength * 2.1f, halfWidth * 2.1f) * scale;
            Save(root, vessel.Name);
        }

        /// <summary>
        /// Sets whether a clip loops, in its FBX's import settings. The FBX comes in with every clip
        /// playing once, so Bob stopped after five seconds; the first machines' clips were set by
        /// hand, and a re-export would have lost that.
        /// </summary>
        static void SetLooping(string machine, string clip, bool loop)
        {
            var importer = AssetImporter.GetAtPath($"{Root}/{machine}/{machine}@{clip}.fbx") as ModelImporter;
            if (importer == null)
                return;
            var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            var changed = false;
            foreach (var c in clips)
                if (c.loopTime != loop)
                {
                    c.loopTime = loop;
                    changed = true;
                }

            if (!changed)
                return;
            importer.clipAnimations = clips;
            importer.SaveAndReimport();
        }

        const string FleetCatalogPath = "Assets/TinyDiggers/Interaction/Resources/FleetCatalog.asset";

        /// <summary>Points the game's fleet catalog at the working craft just built.</summary>
        static void WriteFleetCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FleetCatalog>(FleetCatalogPath);
            if (catalog == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FleetCatalogPath));
                catalog = ScriptableObject.CreateInstance<FleetCatalog>();
                AssetDatabase.CreateAsset(catalog, FleetCatalogPath);
            }

            GameObject Prefab(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/{name}/{name}.prefab");
            catalog.Dredge = Prefab("dredge");
            catalog.GoldDredge = Prefab("golddredge");
            catalog.Scow = Prefab("scow");
            catalog.Tug = Prefab("tug");
            EditorUtility.SetDirty(catalog);
        }

        /// <summary>A renderer's own bounds in <paramref name="root"/>'s space.</summary>
        static Bounds LocalBounds(Transform root, Renderer renderer)
        {
            var b = renderer.localBounds;
            var local = new Bounds(root.InverseTransformPoint(renderer.transform.TransformPoint(b.center)), Vector3.zero);
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                local.Encapsulate(root.InverseTransformPoint(renderer.transform.TransformPoint(corner)));
            }

            return local;
        }

        /// <summary>
        /// A URP Lit material for every colour the machine uses (forge_hex, shared by all machines),
        /// made from an existing one where the colour is new, and each of its FBX files mapped onto
        /// them: a colour no machine had before otherwise comes in as the importer's own material.
        /// </summary>
        static void EnsureMaterials(string machine)
        {
            var manifest = $"{Root}/{machine}/{machine}.forge_import.json";
            if (!File.Exists(manifest))
                return;
            var names = JsonUtility.FromJson<ImportManifest>(File.ReadAllText(manifest)).materials;
            var template = AssetDatabase.FindAssets("forge_ t:Material", new[] { $"{Root}/Materials" })
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g))).FirstOrDefault();
            foreach (var name in names)
            {
                var path = $"{Root}/Materials/{name}.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null || template == null)
                    continue;
                var made = new Material(template) { name = name };
                ColorUtility.TryParseHtmlString("#" + name.Substring("forge_".Length), out var colour);
                made.SetColor("_BaseColor", colour);
                AssetDatabase.CreateAsset(made, path);
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { $"{Root}/{machine}" }))
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                var map = importer.GetExternalObjectMap();
                var changed = false;
                foreach (var name in names)
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>($"{Root}/Materials/{name}.mat");
                    var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), name);
                    if (material == null || (map.TryGetValue(id, out var mapped) && mapped == material))
                        continue;
                    importer.AddRemap(id, material);
                    changed = true;
                }

                if (changed)
                    importer.SaveAndReimport();
            }
        }

        [System.Serializable]
        class ImportManifest
        {
            public string[] materials;
        }

        /// <summary>
        /// The controller at <paramref name="path"/>, emptied, or a new one. Emptied in place rather
        /// than deleted and made again: a rebuilt controller is a new object, and the prefabs saved
        /// against the old one came into play with no controller at all, so no machine played a
        /// single clip (2026-09-24).
        /// </summary>
        static AnimatorController FreshController(string path)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return AnimatorController.CreateAnimatorControllerAtPath(path);
            while (controller.layers.Length > 1)
                controller.RemoveLayer(controller.layers.Length - 1);
            var states = controller.layers[0].stateMachine;
            foreach (var child in states.states)
                states.RemoveState(child.state);
            return controller;
        }

        static GameObject Assemble(string name, float scale, RuntimeAnimatorController controller)
        {
            var root = new GameObject(name);
            // The size on its own object above the model: every clip keys the model's root (its
            // axis turn and a scale of 1), so a size put on the model is undone by the first frame
            // any clip plays.
            var size = new GameObject("Size");
            size.transform.SetParent(root.transform, false);
            size.transform.localScale = Vector3.one * scale;
            var model = (GameObject)PrefabUtility.InstantiatePrefab(Load(name));
            model.transform.SetParent(size.transform, false);
            // Not ??: a missing component is Unity's fake null, which ?? does not see.
            var animator = model.GetComponent<Animator>();
            if (animator == null)
                animator = model.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            return root;
        }

        static void Save(GameObject root, string name)
        {
            PrefabUtility.SaveAsPrefabAsset(root, $"{Root}/{name}/{name}.prefab");
            Object.DestroyImmediate(root);
        }
    }
}
