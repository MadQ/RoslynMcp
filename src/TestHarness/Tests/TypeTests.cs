using System.Text.Json.Nodes;

static class TypeTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		// The dependency fixtures live in a temp project (#274) with stand-ins for the RoslynMcp and
		// Roslyn types they reference, so the exact type_name assertions below hold verbatim without
		// compiling anything into the dogfood assembly. Stand-ins go in their own file: this one uses
		// a file-scoped namespace, which cannot share a file with other namespaces.
		var fx          = TestFixtures.NewMsBuildProject("TypeDeps");
		var fixturePath = fx.PathOf("_TypeDependenciesOperatorFixture_.cs");
		
		fx.Write("_TypeDependenciesStandIns_.cs", """
		namespace RoslynMcp.Tools
		{
			internal class ToolResult { }
			internal sealed class ErrorResult : ToolResult { }
		}
		
		namespace RoslynMcp
		{
			internal sealed class FileLogger { }
		}
		
		namespace Microsoft.CodeAnalysis
		{
			internal sealed class Project { }
			internal sealed class Compilation { }
		}
		""");
		
		File.WriteAllText(fixturePath, """
		namespace RoslynMcp;
		
		internal sealed class _TypeDependenciesOperatorFixture_
		{
			public _TypeDependenciesOperatorFixture_(int value)
			{
				Value = value;
			}
			
			public int Value { get; }
			
			public static _TypeDependenciesOperatorFixture_ operator +(_TypeDependenciesOperatorFixture_ left, _TypeDependenciesOperatorFixture_ right)
				=> new(left.Value + right.Value);
			
			public static explicit operator string(_TypeDependenciesOperatorFixture_ value)
				=> value.Value.ToString();
		}
		
		internal interface _TypeDependenciesDirectInterface_
		{
		}
		
		internal sealed class _TypeDependenciesMemberFixture_<TItem> : _TypeDependenciesDirectInterface_
			where TItem : RoslynMcp.Tools.ToolResult
		{
			private FileLogger? logger;
			
			public System.Collections.Generic.Dictionary<string, TItem[]> Items { get; } = [];
			
			public event System.Action<RoslynMcp.Tools.ErrorResult>? Changed;
			
			public Microsoft.CodeAnalysis.Project? Build<TResult>(Microsoft.CodeAnalysis.Compilation compilation)
				where TResult : RoslynMcp.Tools.ToolResult
				=> null;
		}
		""");
		
		var tests = new List<TestCase> {
			
			new("roslyn_get_type_members: WorkspaceManager members with signatures",
				() => ctx.RunTestAsync(
					"roslyn_get_type_members",
					new { typeName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["members"]?.AsArray().Count > 0 && data["members"]?[0]?["signature"] is not null)),
			
			new("roslyn_get_type_hierarchy: WorkspaceManager inheritance",
				() => ctx.RunTestAsync(
					"roslyn_get_type_hierarchy",
					new { typeName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["interfaces_and_derived"]?.AsArray().Any(i => i?.GetValue<string>().Contains("IDisposable") == true) == true)),
			
			new("roslyn_find_implementations: IDisposable implementers",
				() => ctx.RunTestAsync(
					"roslyn_find_implementations",
					new { symbolName = "IDisposable", projectPath = ctx.TargetPath },
					data => data?["error"] is not null
						|| (data?["total_implementations"] is not null && data?["implementations"]?.AsArray() is not null))),
			
			new("roslyn_get_symbol_documentation: WorkspaceManager XML docs",
				() => ctx.RunTestAsync(
					"roslyn_get_symbol_documentation",
					new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["symbol_name"] is not null)),
			
			new("roslyn_find_overloads: BeginTool overload signatures",
				() => ctx.RunTestAsync(
					"roslyn_find_overloads",
					new { methodName = "BeginTool", containingType = "RoslynMcpTool", projectPath = ctx.TargetPath },
					data => data?["total_overloads"]?.GetValue<int>() == 2
						&& data?["overloads"]?.AsArray().Any(o => o?.GetValue<string>().Contains("BeginTool(string name, string? subject = null)") == true) == true
						&& data?["overloads"]?.AsArray().Any(o => o?.GetValue<string>().Contains("BeginTool<T>(string name, string? subject, T args)") == true) == true)),
			
			new("roslyn_find_overloads: fully-qualified containing type",
				() => ctx.RunTestAsync(
					"roslyn_find_overloads",
					new { methodName = "BeginTool", containingType = "RoslynMcp.Tools.RoslynMcpTool", projectPath = ctx.TargetPath },
					data => data?["total_overloads"]?.GetValue<int>() == 2
						&& data?["containing_type"]?.GetValue<string>() == "RoslynMcp.Tools.RoslynMcpTool")),
			
			new("roslyn_find_overloads: full signature includes out parameters",
				() => ctx.RunTestAsync(
					"roslyn_find_overloads",
					new { methodName = "TryGetCompilation", containingType = "RoslynMcpTool", projectPath = ctx.TargetPath },
					data => data?["total_overloads"]?.GetValue<int>() == 1
						&& data?["overloads"]?.AsArray().Any(o => o?.GetValue<string>().Contains("out Compilation? compilation") == true) == true
						&& data?["overloads"]?.AsArray().Any(o => o?.GetValue<string>().Contains("out ToolResult? error") == true) == true)),
			
			new("roslyn_find_overloads: full signatures include ref parameters",
				() => ctx.RunTestAsync(
					"roslyn_find_overloads",
					new { methodName = "Paginate", containingType = "RoslynMcpTool", projectPath = ctx.TargetPath },
					data => data?["total_overloads"]?.GetValue<int>() == 2
						&& data?["overloads"]?.AsArray().All(o => o?.GetValue<string>().Contains("ref int skip") == true) == true)),
			
			new("roslyn_get_type_dependencies: FindOverloadsTool direct dependencies",
				() => ctx.RunTestAsync(
					"roslyn_get_type_dependencies",
					new { typeName = "FindOverloadsTool", projectPath = ctx.TargetPath },
					data => data?["total_dependencies"]?.GetValue<int>() > 0
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("RoslynMcpTool") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "base_type") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("WorkspaceResolver") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "constructor_parameter") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("Compilation") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "method_parameter") == true)),
			
			new("roslyn_get_type_dependencies: direct member dependency categories",
				() => ctx.RunTestAsync(
					"roslyn_get_type_dependencies",
					new { typeName = "_TypeDependenciesMemberFixture_", projectPath = fx.Csproj },
					data => data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("_TypeDependenciesDirectInterface_") == true
						&& d?["dependency_kind"]?.GetValue<string>() == "interface") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("FileLogger") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "field"
							&& d?["member"]?.GetValue<string>() == "logger") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>() == "RoslynMcp.Tools.ToolResult"
							&& d?["dependency_kind"]?.GetValue<string>() == "generic_constraint"
							&& d?["member"]?.GetValue<string>() == "TItem") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>() == "RoslynMcp.Tools.ToolResult"
							&& d?["dependency_kind"]?.GetValue<string>() == "generic_constraint"
							&& d?["member"]?.GetValue<string>() == "Build") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("Dictionary<string, TItem[]>") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "property"
							&& d?["member"]?.GetValue<string>() == "Items") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>() == "string"
							&& d?["dependency_kind"]?.GetValue<string>() == "property"
							&& d?["member"]?.GetValue<string>() == "Items") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("ErrorResult") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "event"
							&& d?["member"]?.GetValue<string>() == "Changed") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("Project") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "method_return"
							&& d?["member"]?.GetValue<string>() == "Build") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>().Contains("Compilation") == true
							&& d?["dependency_kind"]?.GetValue<string>() == "method_parameter"
							&& d?["member"]?.GetValue<string>() == "Build") == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["type_name"]?.GetValue<string>() == "TItem") == false)),
			
			new("roslyn_get_type_dependencies: operators and conversions are direct dependencies",
				() => ctx.RunTestAsync(
					"roslyn_get_type_dependencies",
					new { typeName = "_TypeDependenciesOperatorFixture_", projectPath = fx.Csproj },
					data => data?["dependencies"]?.AsArray().Any(d => d?["member"]?.GetValue<string>() == "op_Addition"
						&& d?["dependency_kind"]?.GetValue<string>() == "method_return"
						&& d?["type_name"]?.GetValue<string>().Contains("_TypeDependenciesOperatorFixture_") == true) == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["member"]?.GetValue<string>() == "op_Addition"
							&& d?["dependency_kind"]?.GetValue<string>() == "method_parameter"
							&& d?["type_name"]?.GetValue<string>().Contains("_TypeDependenciesOperatorFixture_") == true) == true
						&& data?["dependencies"]?.AsArray().Any(d => d?["member"]?.GetValue<string>() == "op_Explicit"
							&& d?["dependency_kind"]?.GetValue<string>() == "method_return"
							&& d?["type_name"]?.GetValue<string>() == "string") == true)),
			
			new("roslyn_find_overloads: missing method returns empty list",
				() => ctx.RunTestAsync(
					"roslyn_find_overloads",
					new { methodName = "DefinitelyNotAMethod", containingType = "RoslynMcpTool", projectPath = ctx.TargetPath },
					data => data?["total_overloads"]?.GetValue<int>() == 0
						&& data?["overloads"]?.AsArray().Count == 0)),
		};
		
		return new TestGroup($"Type Understanding Tools ({tests.Count} tests)", tests, Teardown: () =>
		{
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
