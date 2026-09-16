using Muralis.Core.Models;

namespace Muralis.Desktop.Surfaces;

/// <summary>Asks the shell to show one piece of content on one display.</summary>
public sealed record SurfaceRequest(ISurfaceContent Content, MonitorRef Monitor)
{
    /// <summary>
    /// Checks the rules every surface request must satisfy. Returns an empty list when the request
    /// is acceptable; callers log or reject otherwise instead of mounting something invalid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Monitor.StableId))
        {
            problems.Add("The request must name a display by stable id.");
        }

        if (Content is null)
        {
            problems.Add("The request must carry content to show.");
        }
        else if (Content.Kind == SurfaceKind.Backdrop)
        {
            if (Content.Interaction != SurfaceInteraction.None)
            {
                problems.Add("Backdrop content sits behind the icons and cannot take input.");
            }

            if (Content.Activation != SurfaceActivation.Never)
            {
                problems.Add("Backdrop content is never activated.");
            }
        }
        else if (Content.Kind == SurfaceKind.InteractiveOverlay)
        {
            if (Content.Interaction == SurfaceInteraction.None)
            {
                problems.Add("Interactive overlay content must declare the pointer input it takes.");
            }

            if (Content.Activation == SurfaceActivation.Never)
            {
                problems.Add("Interactive overlay content must be activatable; the mouse has to reach it.");
            }
        }

        return problems;
    }
}
