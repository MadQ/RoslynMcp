using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools.SignatureChange;

/// <summary>
///     The output of a signature change preparation. Contains the new solution,
///     diff, diagnostics, and summary — regardless of which editor produced it.
/// </summary>
internal sealed record SignatureChangeResult
{
	public required bool     Success			{ get; init; }
	public required Solution BaseSolution		{ get; init; }
	public required Solution NewSolution		{ get; init; }
	public string?           Error				{ get; init; }
	public string?           Diff				{ get; init; }
	public string[]          Warnings			{ get; init; } = [];
	public string[]          ParametersAdded	{ get; init; } = [];
	public string[]          ParametersRemoved	{ get; init; } = [];
	public string?           DeprecationMessage	{ get; init; }
	public int               CallSitesUpdated	{ get; init; }
	public int               FilesAffected		{ get; init; }
	
	public static SignatureChangeResult Failed(Solution solution, string error)
		=> new() {
			
			Success      = false,
			BaseSolution = solution,
			NewSolution  = solution,
			Error        = error
		}
	;
}
