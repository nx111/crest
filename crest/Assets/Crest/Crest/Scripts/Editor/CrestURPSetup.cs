// Crest Ocean System - MIT License (see LICENSE)
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Crest
{
    public static class CrestURPSetup
    {
        const string Folder = "Assets/CrestURPSettings";
        const string Examples = "Assets/Crest/Crest-Examples";

        [MenuItem("Crest/URP/Convert Example Assets")]
        public static void ConvertExamples()
        {
            CreatePipeline();
            var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.defaultRenderPipeline;
            pipeline.shadowDistance = QualitySettings.shadowDistance;
            pipeline.shadowCascadeCount = Mathf.Max(1, QualitySettings.shadowCascades);
            var serializedPipeline = new SerializedObject(pipeline);
            serializedPipeline.FindProperty("m_OpaqueDownsampling").intValue = (int)Downsampling.None;
            serializedPipeline.ApplyModifiedPropertiesWithoutUndo();

            var upgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(UniversalRenderPipelineAsset));
            var convertedMaterials = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { Examples }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".mat")) continue;
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                var shader = material.shader.name;
                if (shader != "Standard" && shader != "Standard (Specular setup)" &&
                    !shader.StartsWith("Nature/Terrain/") && !shader.StartsWith("Particles/")) continue;
                var message = string.Empty;
                if (!MaterialUpgrader.Upgrade(material, upgraders,
                    MaterialUpgrader.UpgradeFlags.LogMessageWhenNoUpgraderFound, ref message))
                    throw new BuildFailedException(path + ": " + message);
                EditorUtility.SetDirty(material);
                convertedMaterials++;
            }

            var convertedPrefabs = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Examples }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".prefab")) continue;
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var changed = false;
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (PrefabUtility.IsPartOfPrefabInstance(renderer)) continue;
                        var materials = renderer.sharedMaterials;
                        var replaced = false;
                        for (var i = 0; i < materials.Length; i++)
                        {
                            var replacement = ConvertBuiltinMaterial(materials[i], pipeline);
                            if (replacement == materials[i]) continue;
                            materials[i] = replacement;
                            replaced = true;
                        }
                        if (!replaced) continue;
                        renderer.sharedMaterials = materials;
                        changed = true;
                    }
                    if (root.TryGetComponent<Camera>(out var camera) && !PrefabUtility.IsPartOfPrefabInstance(camera))
                    {
                        changed |= !camera.TryGetComponent<UniversalAdditionalCameraData>(out _);
                        camera.GetUniversalAdditionalCameraData();
                        changed |= camera.forceIntoRenderTexture || camera.allowMSAA;
                        camera.forceIntoRenderTexture = false;
                        camera.allowMSAA = false;
                    }
                    if (!changed) continue;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    convertedPrefabs++;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            GraphicsSettings.lightsUseLinearIntensity = true;
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            ValidatePipeline();
            Debug.Log($"Crest URP examples converted: {convertedMaterials} materials, {convertedPrefabs} prefabs.");
        }

        static Material ConvertBuiltinMaterial(Material material, UniversalRenderPipelineAsset pipeline)
        {
            if (material == null) return null;
            var path = AssetDatabase.GetAssetPath(material);
            if (path.StartsWith("Assets/") || path.StartsWith("Packages/")) return material;
            switch (material.name)
            {
                case "Default-Diffuse":
                case "Default-Material": return pipeline.defaultMaterial;
                case "Default-ParticleSystem":
                case "Default-Particle": return pipeline.defaultParticleMaterial;
                case "Default-Terrain-Diffuse":
                case "Default-Terrain-Specular":
                case "Default-Terrain-Standard": return pipeline.defaultTerrainMaterial;
                default: return material;
            }
        }

        [MenuItem("Crest/URP/Create and Assign Pipeline")]
        public static void CreatePipeline()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "CrestURPSettings");
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Folder + "/CrestRenderer.asset");
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, Folder + "/CrestRenderer.asset");
            }
            AddFeature(renderer);
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Folder + "/CrestPipeline.asset");
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, Folder + "/CrestPipeline.asset");
            }
            pipeline.supportsCameraDepthTexture = true;
            // Crest requests opaque color per camera when it needs it. A global request duplicates refraction copies.
            pipeline.supportsCameraOpaqueTexture = false;
            pipeline.maxAdditionalLightsCount = 8;
            // URP exposes these setters internally; use its serialized asset settings in the editor.
            var options = new SerializedObject(pipeline);
            options.FindProperty("m_ReflectionProbeBlending").boolValue = true;
            options.FindProperty("m_ReflectionProbeBoxProjection").boolValue = true;
            options.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
            options.FindProperty("m_SoftShadowsSupported").boolValue = true;
            options.ApplyModifiedPropertiesWithoutUndo();
            pipeline.msaaSampleCount = 1;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            // Existing per-quality BIRP/other overrides would otherwise hide the new default.
            var quality = QualitySettings.GetQualityLevel();
            for (var i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(quality, false);
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            Selection.activeObject = pipeline;
            Debug.Log("Crest URP pipeline assigned. Use Render Graph and a Universal Renderer. See docs/URP-MIGRATION.md for material conversion and local validation.");
        }

        [MenuItem("Crest/URP/Add Feature to Selected Renderer")]
        static void AddToSelected()
        {
            if (Selection.activeObject is UniversalRendererData renderer) AddFeature(renderer);
            else Debug.LogError("Select a Universal Renderer Data asset in the Project window first.");
        }

        static void AddFeature(UniversalRendererData renderer)
        {
            if (renderer.rendererFeatures.Any(x => x is CrestUniversalRendererFeature)) return;
            var feature = ScriptableObject.CreateInstance<CrestUniversalRendererFeature>();
            feature.name = "Crest Ocean";
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Crest/URP/Configure Transparent Refraction")]
        public static void ConfigureTransparentRefraction()
        {
            var renderer = Selection.activeObject as UniversalRendererData;
            if (renderer == null) renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Folder + "/CrestRenderer.asset");
            if (renderer == null) throw new BuildFailedException("Select the Universal Renderer Data used by your water camera.");
            const string layerName = "Crest Refracted";
            var layer = LayerMask.NameToLayer(layerName);
            if (layer < 0)
            {
                var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
                var layers = tags.FindProperty("layers");
                for (var i = 8; i < layers.arraySize; i++)
                {
                    if (!string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) continue;
                    layer = i;
                    layers.GetArrayElementAtIndex(i).stringValue = layerName;
                    tags.ApplyModifiedProperties();
                    break;
                }
            }
            if (layer < 0) throw new BuildFailedException("No free user layer for Crest Refracted. Configure Refracted Layers manually.");
            AddFeature(renderer);
            var feature = renderer.rendererFeatures.OfType<CrestUniversalRendererFeature>().First();
            Undo.RecordObjects(new Object[] { renderer, feature }, "Configure Crest transparent refraction");
            feature.RefractedLayers = feature.RefractedLayers.value | (1 << layer);
            renderer.transparentLayerMask = renderer.transparentLayerMask.value & ~feature.RefractedLayers.value;
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();
            Debug.Log("Crest refraction configured. Assign underwater transparent renderers to the Crest Refracted layer. Keep water on its existing layer.");
        }

        [MenuItem("Crest/URP/Validate Active Pipeline")]
        public static void ValidatePipeline()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline))
                throw new BuildFailedException("Crest urp branch requires URP. Run Crest > URP > Create and Assign Pipeline, or configure your existing URP asset.");
            if (!pipeline.supportsCameraDepthTexture)
                throw new BuildFailedException("Enable Depth Texture on the active URP asset.");
#if !UNITY_6000_4_OR_NEWER
            var settings = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
            if (settings != null && settings.enableRenderCompatibilityMode)
                throw new BuildFailedException("Crest requires Render Graph. Turn off Compatibility Mode in Graphics settings.");
#endif
            var serialized = new SerializedObject(pipeline);
            var renderers = serialized.FindProperty("m_RendererDataList");
            for (var i = 0; i < renderers.arraySize; i++)
            {
                var renderer = renderers.GetArrayElementAtIndex(i).objectReferenceValue as UniversalRendererData;
                if (renderer == null || renderer.rendererFeatures.OfType<CrestUniversalRendererFeature>().Count(x => x.isActive) != 1)
                    throw new BuildFailedException("Each Universal Renderer used by Crest cameras needs exactly one active CrestUniversalRendererFeature.");
                foreach (var feature in renderer.rendererFeatures.OfType<CrestUniversalRendererFeature>().Where(x => x.isActive))
                {
                    if ((renderer.transparentLayerMask.value & feature.RefractedLayers.value) != 0)
                        throw new BuildFailedException("Exclude Crest Refracted Layers from the renderer's Transparent Layer Mask to prevent double rendering.");
                    foreach (var ocean in Object.FindObjectsByType<OceanRenderer>(FindObjectsSortMode.None))
                        if ((feature.RefractedLayers.value & (1 << ocean.Layer)) != 0)
                            throw new BuildFailedException("Water cannot be on Crest Refracted Layers: its color must be captured before the ocean draw.");
                }
            }
            Debug.Log("Crest URP configuration checks passed. This is not a Unity shader or device rendering test.");
        }
    }

    sealed class CrestURPBuildValidation : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) => CrestURPSetup.ValidatePipeline();
    }
}
