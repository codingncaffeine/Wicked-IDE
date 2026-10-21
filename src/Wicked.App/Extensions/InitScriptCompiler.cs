using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Wicked.Core.Extensions;

namespace Wicked.App.Extensions;

/// <summary>
/// Compiles init.csx with Roslyn's scripting API into an assembly that can be kept on disk and run again without
/// Roslyn. Kept apart from <see cref="InitScript"/> so that Roslyn is not even loaded when a cached build is found or
/// there is no script.
/// </summary>
internal static class InitScriptCompiler
{
    /// <summary>The namespaces a script uses without a using line.</summary>
    internal static readonly string[] Imports =
    [
        "System", "System.IO", "System.Linq", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks",
        "System.Text", "Wicked.Core.Extensions", "Avalonia", "Avalonia.Controls", "Avalonia.Threading",
    ];

    /// <summary>The compiled assembly, or the compiler's errors as <c>init.csx(12,5): error CS1002: ; expected</c> lines.</summary>
    public static (byte[]? Assembly, IReadOnlyList<string> Errors) Compile(string text, string path)
    {
        var options = ScriptOptions.Default
            .WithFilePath(path)
            .WithFileEncoding(Encoding.UTF8)
            .WithEmitDebugInformation(true)
            .WithReferences(
                typeof(IWickedApi).Assembly,
                typeof(Avalonia.Controls.Control).Assembly,
                typeof(Avalonia.AvaloniaObject).Assembly,
                typeof(Avalonia.Application).Assembly,
                typeof(Wicked.Editor.Model.Document).Assembly,
                typeof(Enumerable).Assembly)
            .WithImports(Imports);
        var script = CSharpScript.Create(text, options, typeof(InitGlobals));
        var compilation = script.GetCompilation();
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(Format).ToList();
        if (errors.Count > 0) return (null, errors);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!emitted.Success) return (null, [.. emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(Format).DefaultIfEmpty("error: the script could not be compiled")]);
        return (stream.ToArray(), []);
    }

    private static string Format(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        var file = Path.GetFileName(span.Path);
        return d.Location == Location.None
            ? $"error {d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{(file.Length == 0 ? "init.csx" : file)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): error {d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
