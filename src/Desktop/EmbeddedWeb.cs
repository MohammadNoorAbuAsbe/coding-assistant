using System.Reflection;

namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Extracts the embedded web UI (index.html, styles.css, app.js, vendor libs)
/// to a local cache folder so WebView2 can serve it via a virtual host name.
/// The set of web files is fixed at compile time, so extraction matches known
/// relative paths against manifest names deterministically.
/// </summary>
internal static class EmbeddedWeb
{
    private const string ResourcePrefix = "src.Web.";

    private static readonly string[] WebFiles =
    {
        "index.html",
        "styles.css",
        "app.js",
        "vendor/marked.min.js",
        "vendor/highlight.min.js"
    };

    public static string ExtractToCache()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodingAssistant",
            "web");

        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();

        foreach (var relative in WebFiles)
        {
            string logical = ResourcePrefix + relative.Replace('/', '.').Replace('\\', '.');
            // Manifest names carry the root namespace prefix, so match by suffix.
            string? fullName = names.FirstOrDefault(n =>
                n.EndsWith(logical, StringComparison.OrdinalIgnoreCase));
            if (fullName == null)
                continue;

            string target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var stream = assembly.GetManifestResourceStream(fullName);
            if (stream == null)
                continue;

            using var output = File.Create(target);
            stream.CopyTo(output);
        }

        return root;
    }
}
