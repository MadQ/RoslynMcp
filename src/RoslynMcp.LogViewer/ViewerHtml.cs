using System.Reflection;

namespace RoslynMcp.LogViewer;

static class ViewerHtml
{
static readonly string _page = LoadPage();

public static string Page => _page;

static string LoadPage()
{
var assembly = Assembly.GetExecutingAssembly();
var name     = assembly.GetManifestResourceNames()
.First(n => n.EndsWith("viewer.html", StringComparison.OrdinalIgnoreCase))
;

using var stream = assembly.GetManifestResourceStream(name)!;
using var reader = new StreamReader(stream);

return reader.ReadToEnd();
}
}