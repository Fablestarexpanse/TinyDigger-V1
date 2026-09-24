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

        struct Machine
        {
            public string Name;
            public string Work;             // clip the Work state plays, or null
            public float WorkSeconds;       // what the work took before the swap; 0 = as authored
            public string Drive;            // clip Move and Carry play, or null
            public float MetresPerLoop;     // at forge size, from <machine>.machine.json
        }

        static readonly Machine[] Units =
        {
            // Dig and tip times are the walker clips' (2.97 s, 1.77 s), which the crew balance was
            // tuned on; the forge clips are twice and three times as long.
            new Machine { Name = "digger", Work = "Dig", WorkSeconds = 2.97f, Drive = "Drive", MetresPerLoop = 0.5655f },
            new Machine { Name = "dumper", Work = "Tip", WorkSeconds = 1.77f, Drive = "Drive", MetresPerLoop = 0.8168f },
            new Machine { Name = "dozer", Work = "BladeCycle", WorkSeconds = 0f, Drive = "Drive", MetresPerLoop = 0.4509f },
        };

        [MenuItem("TinyDiggers/Build Forge Machines")]
        public static void Build()
        {
            var scale = DumperHeight / Height(Load("dumper"));
            foreach (var machine in Units)
                BuildUnit(machine, scale);
            BuildBoat(scale);
            AssetDatabase.SaveAssets();
            Debug.Log($"ForgeMachines: built {Units.Length} machines and the landing craft at x{scale:0.000}");
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

            Save(Assemble("boat", scale, controller), "boat");
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
