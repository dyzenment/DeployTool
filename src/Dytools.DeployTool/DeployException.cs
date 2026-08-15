namespace Dytools.DeployTool;

/// <summary>
/// Signals an expected, recoverable deployment failure within a handler.
/// Caught by the handler itself to trigger rollback logic and produce a clean
/// failure result without propagating a stack trace to the orchestrator.
/// </summary>
public sealed class DeployException(string message) : Exception(message);