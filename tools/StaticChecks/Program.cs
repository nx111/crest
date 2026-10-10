using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : "crest/Assets");
var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
var common = new[] { "UNITY_2021_3_OR_NEWER", "UNITY_2022_1_OR_NEWER", "UNITY_2022_2_OR_NEWER", "UNITY_2022_3_OR_NEWER",
    "UNITY_2023_1_OR_NEWER", "UNITY_2023_2_OR_NEWER", "UNITY_6000_0_OR_NEWER", "ENABLE_VR_MODULE", "ENABLE_XR_MODULE",
    "CREST_UNITY_BURST", "CREST_UNITY_MATHEMATICS", "CREST_UNITY_INPUT", "CREST_UNITY_SPLINES" };
var errors = 0;
foreach (var editor in new[] { false, true })
foreach (var current in new[] { false, true })
{
    var symbols = common.Concat(editor ? new[] { "UNITY_EDITOR" } : Array.Empty<string>())
        .Concat(current ? new[] { "UNITY_6000_1_OR_NEWER", "UNITY_6000_2_OR_NEWER", "UNITY_6000_3_OR_NEWER",
            "UNITY_6000_4_OR_NEWER", "UNITY_6000_5_OR_NEWER", "UNITY_6000_6_OR_NEWER" } : Array.Empty<string>());
    var options = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: symbols);
    foreach (var file in files)
    foreach (var diagnostic in CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file).GetDiagnostics())
    {
        if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
        Console.Error.WriteLine($"{(editor ? "Editor" : "Player")}/{(current ? "6.6" : "6.0")}: {diagnostic}");
        errors++;
    }
}
Console.WriteLine($"C# syntax: {files.Length} files x 4 Unity define configurations; {errors} errors.");
Console.WriteLine("Syntax parsing only; no Unity assembly binding, shader compilation, or runtime validation.");
return errors == 0 ? 0 : 1;
