// Crest Ocean System - MIT License (see LICENSE)
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Crest
{
    public static class CrestURPSetup
    {
        const string Folder = "Assets/CrestURPSettings";

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
            pipeline.supportsCameraOpaqueTexture = true;
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

        [MenuItem("Crest/URP/Validate Active Pipeline")]
        public static void ValidatePipeline()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline))
                throw new BuildFailedException("Crest urp branch requires URP. Run Crest > URP > Create and Assign Pipeline, or configure your existing URP asset.");
            if (!pipeline.supportsCameraDepthTexture || !pipeline.supportsCameraOpaqueTexture)
                throw new BuildFailedException("Enable Depth Texture and Opaque Texture on the active URP asset.");
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
                if (renderer == null || !renderer.rendererFeatures.Any(x => x is CrestUniversalRendererFeature && x.isActive))
                    throw new BuildFailedException("Each Universal Renderer used by Crest cameras needs an active CrestUniversalRendererFeature.");
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
