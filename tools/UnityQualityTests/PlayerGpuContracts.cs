// Copy beside Assets/PlayerAudit.cs; install RealismAudit.shader in Assets/Resources.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public sealed partial class PlayerAudit
{
    Color ContractPixel(Material material)
    {
        var target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var staging = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
        var saved = RenderTexture.active;
        using (var command = new CommandBuffer())
        {
            try
            {
                target.Create(); command.SetRenderTarget(target);
                command.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(command);
                RenderTexture.active = target;
                staging.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); staging.Apply();
                return staging.GetPixel(0, 0);
            }
            finally { RenderTexture.active = saved; target.Release(); Object.Destroy(target); Object.Destroy(staging); }
        }
    }

    [UnityEngine.Scripting.Preserve]
    void RunGpuContracts()
    {
        var shader = Resources.Load<Shader>("RealismAudit");
        Check(shader != null && shader.isSupported, "Player production-HLSL contract shader is supported");
        var material = new Material(shader);
        try
        {
            foreach (var slope in new[] { 0f, 30f, 60f })
            {
                var macro = new Vector3(Mathf.Sin(slope * Mathf.Deg2Rad), Mathf.Cos(slope * Mathf.Deg2Rad), 0);
                material.SetVector("_AuditBase", macro);
                foreach (var detail in new[] { new Vector2(0.5f, 0), new Vector2(-0.5f, 0), new Vector2(0, 0.5f), Vector2.zero })
                {
                    material.SetVector("_AuditDetail", new Vector4(detail.x, detail.y, 0, 0));
                    var pixel = ContractPixel(material);
                    var normal = new Vector3(pixel.r, pixel.g, pixel.b);
                    var angle = Vector3.Angle(macro, normal);
                    Check(Finite(normal.x) && Finite(normal.y) && Finite(normal.z)
                        && Mathf.Abs(normal.magnitude - 1) < 0.0001f
                        && Mathf.Abs(angle - Mathf.Atan(detail.magnitude) * Mathf.Rad2Deg) < 0.025f,
                        "Player GPU RNM: macro " + slope + "; detail " + detail + "; deviation " + angle);
                }
            }
            material.SetInt("_AuditCase", 1);
            material.SetFloat("_AdditionalLightIntensity", 1);
            material.SetFloat("_WaveFoamLightScale", 1);
            material.SetFloat("_WaveFoamSpecularBoost", 0);
            material.SetVector("_AuditNormal0", new Vector3(1, 1, 0).normalized);
            material.SetVector("_AuditNormal1", new Vector3(-1, 1, 0).normalized);
            material.SetVector("_AuditLight", Vector3.right);
            material.SetVector("_AuditWeights", new Vector4(1, 0, 0, 0));
            var one = ContractPixel(material).r;
            Check(Mathf.Abs(one - Mathf.Sqrt(0.5f)) < 0.002f, "Player GPU foam side-light diffuse uses foam relief: " + one);
            material.SetVector("_AuditNormal1", Vector3.right);
            material.SetVector("_AuditVariance", new Vector4(0, 0.02f, 0, 0));
            Check(Mathf.Abs(ContractPixel(material).r - one) < 0.0001f, "Player GPU empty Flow phase adds no foam light");
            material.SetVector("_AuditWeights", new Vector4(0.5f, 0.5f, 0, 0));
            material.SetVector("_AuditNormal1", new Vector3(-1, 1, 0).normalized);
            Check(Mathf.Abs(ContractPixel(material).r - 0.5f * one) < 0.002f, "Player GPU foam lights each phase before blending");
            material.SetVector("_AuditWeights", Vector4.zero);
            Check(Mathf.Abs(ContractPixel(material).r) < 0.0001f, "Player GPU zero foam coverage adds no light");
            material.SetVector("_AuditWeights", new Vector4(1, 0, 0, 0));
            material.SetVector("_AuditNormal0", Vector3.up);
            material.SetVector("_AuditLight", Vector3.up);
            material.SetFloat("_WaveFoamSpecularBoost", 1);
            material.SetVector("_AuditVariance", Vector4.zero);
            var unfiltered = ContractPixel(material).r;
            material.SetVector("_AuditVariance", new Vector4(0.01f, 0, 0, 0));
            var filtered = ContractPixel(material).r;
            Check(unfiltered > 1.9f && filtered > 1 && filtered < unfiltered * 0.6f,
                "Player GPU foam highlight uses foam variance: " + unfiltered + " -> " + filtered);
        }
        finally { Object.Destroy(material); }
    }
}
