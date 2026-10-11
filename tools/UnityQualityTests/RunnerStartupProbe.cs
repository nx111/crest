// Negative control for the real Runner's domain-reload failure persistence.
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class RunnerStartupProbe
{
    const string Probe = "CrestRetestInjectedStartup";
    static RunnerStartupProbe()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode && SessionState.GetBool(Probe, false))
                UnityEditor.Search.SearchDatabaseNegativeControl.Emit();
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Probe, false)) return;
            SessionState.EraseBool(Probe);
            var retained = SessionState.GetString("CrestQualityFailureLog", "").Contains("CREST_INJECTED_SEARCH_FAILURE");
            var file = Path.Combine(Path.GetDirectoryName(Application.dataPath), "startup-probe.txt");
            File.WriteAllText(file, "STARTUP_RELOAD_PROBE " + (retained ? "PASS" : "FAIL") + "; expected Unity process exit=1");
            Debug.Log("CREST_STARTUP_RELOAD_PROBE " + (retained ? "PASS" : "FAIL"));
            typeof(QualityRunner).GetMethod("Finish", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { 1 });
        };
    }
    public static void Start()
    {
        SessionState.SetBool(Probe, true);
        QualityRunner.Start();
    }
}

namespace UnityEditor.Search
{
    public static class SearchDatabaseNegativeControl
    {
        public static void Emit() => Debug.LogError("CREST_INJECTED_SEARCH_FAILURE: deliberate startup negative control");
    }
}
