namespace TabMirror.Host.Capture;

/// <summary>
/// Thrown when the DXGI desktop duplication session is lost, 
/// usually caused by a resolution change, monitor disconnect, 
/// or UAC prompt (session switch).
/// </summary>
public sealed class ResolutionChangedException : Exception
{
    public ResolutionChangedException(string message) : base(message) { }
}
